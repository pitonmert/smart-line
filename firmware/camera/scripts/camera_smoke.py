"""Host-side HTTP checks for a running ESP32-CAM.

This script does not compile or flash firmware. Point it at the camera, for
example http://192.168.1.50:81. A still capture briefly pauses the preview.
"""

from __future__ import annotations

import argparse
import json
import sys
from urllib import error, request
from urllib.parse import urlsplit, urlunsplit


PREVIEW_WIDTH = 240
PREVIEW_HEIGHT = 176
SENSOR_PID = "0x2145"


def join_url(base: str, path: str) -> str:
    parts = urlsplit(base if "://" in base else f"http://{base}")
    if not parts.scheme or not parts.netloc:
        return ""
    netloc = parts.netloc.removesuffix(":81") if path != "/stream" else parts.netloc if parts.port else parts.netloc + ":81"
    return urlunsplit((parts.scheme, netloc, path, "", ""))


TIMING_FIELDS = (
    "lastMutexWaitMs",
    "lastStillDeinitMs",
    "lastStillInitMs",
    "lastStaleFrame1Ms",
    "lastStaleFrame2Ms",
    "lastFrameAcquireMs",
    "lastJpegEncodeMs",
    "lastFrameCopyMs",
    "lastPreviewDeinitMs",
    "lastPreviewInitMs",
    "lastPreviewStaleMs",
    "lastPreviewRestoreMs",
    "lastHttpPrepMs",
    "lastCaptureTotalMs",
)

TRANSPORT_FIELDS = (
    "lastTransportErrno",
    "lastTransportEspErr",
    "lastBodyStartedAtMs",
    "lastBodyBytes",
    "lastBodyWritten",
    "lastBodySendMs",
    "lastBodyChunkCount",
    "lastBodyMaxChunkMs",
    "lastStreamCaptureMs",
    "lastStreamJpegMs",
    "lastStreamJpegBytes",
    "lastStreamSendMs",
    "lastStreamFrames",
    "lastStreamSendFailures",
)


def run_checks(base: str, captures: int, fetch, stream_reader, include_raw: bool = False) -> list[str]:
    """Return human-readable failures. An empty list means the camera passed."""

    errors: list[str] = []
    status_url = join_url(base, "/status")
    stream_url = join_url(base, "/stream")
    capture_url = join_url(base, "/capture")
    raw_url = join_url(base, "/capture/raw")
    if not status_url:
        return ["Camera base URL is not absolute."]

    status, payload = _read_status(status_url, fetch, errors)
    if status is None:
        return errors

    pid = str(status.get("sensorPid", "")).lower()
    if pid != SENSOR_PID:
        errors.append(f"Sensor PID is {status.get('sensorPid')!r}; expected {SENSOR_PID}.")
    if status.get("previewWidth") != PREVIEW_WIDTH or status.get("previewHeight") != PREVIEW_HEIGHT:
        errors.append(
            f"Preview is {status.get('previewWidth')}x{status.get('previewHeight')}; expected {PREVIEW_WIDTH}x{PREVIEW_HEIGHT}."
        )
    if not isinstance(status.get("psramDetected"), bool):
        errors.append("PSRAM detection was not reported as a boolean.")

    if not _stream_has_jpeg(stream_url, stream_reader, errors, "before still capture"):
        return errors

    psram = status.get("psramDetected") is True
    configured_width = status.get("configuredStillWidth")
    configured_height = status.get("configuredStillHeight")
    if not psram or status.get("stillAvailable") is not True:
        _expect_closed_still(capture_url, fetch, errors)
        if configured_width not in (0, None) or configured_height not in (0, None):
            errors.append("A camera without a usable still reported a still resolution.")
        return errors

    if not isinstance(configured_width, int) or not isinstance(configured_height, int):
        errors.append("Configured still resolution is missing.")
        return errors
    if configured_width * configured_height <= PREVIEW_WIDTH * PREVIEW_HEIGHT:
        errors.append(
            f"Configured still {configured_width}x{configured_height} is not higher than the {PREVIEW_WIDTH}x{PREVIEW_HEIGHT} preview."
        )

    for index in range(captures):
        image = _read_capture(capture_url, fetch, errors, index + 1)
        if image is None:
            continue
        width, height, body = image
        if body[:2] != b"\xff\xd8":
            errors.append(f"Still capture {index + 1} is not a JPEG.")
        if width != configured_width or height != configured_height:
            errors.append(
                f"Still capture {index + 1} is {width}x{height}; configured still is {configured_width}x{configured_height}."
            )

    if include_raw:
        for index in range(captures):
            _read_raw(raw_url, fetch, errors, index + 1, configured_width, configured_height)

    # Read transport before the trailing stream. A live stream overwrites the
    # latest body counters, and those counters must still describe the still.
    capture_path = "raw" if include_raw else "jpeg"
    after_still, _ = _read_status(status_url, fetch, errors)
    if after_still is not None:
        _check_timings(after_still, errors, capture_path)
        _check_transport(after_still, errors, capture_path)

    _stream_has_jpeg(stream_url, stream_reader, errors, "after still capture")
    confirmed, _ = _read_status(status_url, fetch, errors)
    if confirmed is not None:
        if confirmed.get("previewWidth") != PREVIEW_WIDTH or confirmed.get("previewHeight") != PREVIEW_HEIGHT:
            errors.append("Preview resolution changed after still capture.")
        if confirmed.get("lastStillWidth") != configured_width or confirmed.get("lastStillHeight") != configured_height:
            errors.append(
                f"Status last still is {confirmed.get('lastStillWidth')}x{confirmed.get('lastStillHeight')}; expected {configured_width}x{configured_height}."
            )
    return errors


def _read_status(url: str, fetch, errors: list[str]) -> tuple[dict | None, bytes]:
    try:
        status, _headers, body = fetch(url, 5)
    except Exception as exc:  # noqa: BLE001 - report the camera, do not crash
        errors.append(f"GET /status failed: {exc}")
        return None, b""
    if status != 200:
        errors.append(f"GET /status returned HTTP {status}.")
        return None, body
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        errors.append("GET /status did not return JSON.")
        return None, body
    if not isinstance(payload, dict):
        errors.append("GET /status JSON was not an object.")
        return None, body
    if "ssid" in payload or "password" in payload or "wifi" in payload:
        errors.append("GET /status exposed network credentials.")
    return payload, body


def _read_capture(url: str, fetch, errors: list[str], index: int) -> tuple[int, int, bytes] | None:
    try:
        status, headers, body = fetch(url, 30)
    except Exception as exc:  # noqa: BLE001
        errors.append(f"Still capture {index} failed: {exc}")
        return None
    if status != 200:
        errors.append(f"Still capture {index} returned HTTP {status}: {_error_code(body)}.")
        return None
    width = _header_int(headers, "X-Frame-Width")
    height = _header_int(headers, "X-Frame-Height")
    if width is None or height is None:
        errors.append(f"Still capture {index} did not report measured dimensions.")
        return None
    if _header_int(headers, "X-Timing-Total-Ms") is None:
        errors.append(f"Still capture {index} did not report X-Timing-Total-Ms.")
    return width, height, body


def _read_raw(url: str, fetch, errors: list[str], index: int, configured_width: int, configured_height: int) -> None:
    try:
        status, headers, body = fetch(url, 30)
    except Exception as exc:  # noqa: BLE001
        errors.append(f"Raw capture {index} failed: {exc}")
        return
    if status != 200:
        errors.append(f"Raw capture {index} returned HTTP {status}: {_error_code(body)}.")
        return
    if _header(headers, "X-Pixel-Format") != "RGB565":
        errors.append(f"Raw capture {index} pixel format is {_header(headers, 'X-Pixel-Format')!r}; expected RGB565.")
    if _header(headers, "X-Rgb565-Order") != "BE":
        errors.append(f"Raw capture {index} byte order is {_header(headers, 'X-Rgb565-Order')!r}; expected BE.")
    width = _header_int(headers, "X-Frame-Width")
    height = _header_int(headers, "X-Frame-Height")
    if width != configured_width or height != configured_height:
        errors.append(
            f"Raw capture {index} is {width}x{height}; configured still is {configured_width}x{configured_height}."
        )
        return
    expected = configured_width * configured_height * 2
    if len(body) != expected:
        errors.append(f"Raw capture {index} is {len(body)} bytes; expected {expected}.")
    if _header_int(headers, "X-Timing-Total-Ms") is None:
        errors.append(f"Raw capture {index} did not report X-Timing-Total-Ms.")


def _check_timings(status: dict, errors: list[str], expected_path: str) -> None:
    for name in TIMING_FIELDS:
        value = status.get(name)
        if not isinstance(value, int) or value < 0:
            errors.append(f"Status timing {name} is {value!r}; expected a non-negative integer.")
    path = status.get("lastCapturePath")
    if path not in ("jpeg", "raw"):
        errors.append(f"Status lastCapturePath is {path!r}; expected jpeg or raw.")
    elif path != expected_path:
        errors.append(f"Status lastCapturePath is {path!r}; expected {expected_path}.")
    for name in ("stillStaleFrames", "previewStaleFrames"):
        value = status.get(name)
        if not isinstance(value, int) or value < 0 or value > 2:
            errors.append(f"Status {name} is {value!r}; expected an integer from 0 to 2.")


def _check_transport(status: dict, errors: list[str], expected_path: str) -> None:
    for name in TRANSPORT_FIELDS:
        value = status.get(name)
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            errors.append(f"Status transport {name} is {value!r}; expected a non-negative integer.")
    if not isinstance(status.get("lastTransportTimeout"), bool):
        errors.append(f"Status lastTransportTimeout is {status.get('lastTransportTimeout')!r}; expected a boolean.")
    chunk = status.get("lastBodyChunkBytes")
    if chunk not in (1024, 2048, 4096):
        errors.append(f"Status lastBodyChunkBytes is {chunk!r}; expected 1024, 2048, or 4096.")
    intended = status.get("lastBodyBytes")
    written = status.get("lastBodyWritten")
    transport_error = status.get("lastTransportError")
    if transport_error is not None and not isinstance(transport_error, str):
        errors.append(f"Status lastTransportError is {transport_error!r}; expected a string or null.")
    if isinstance(intended, int) and isinstance(written, int) and not isinstance(intended, bool) and not isinstance(written, bool):
        if written > intended:
            errors.append(f"Status lastBodyWritten {written} exceeds lastBodyBytes {intended}.")
        elif written < intended and not transport_error:
            errors.append("Status body is short and lastTransportError is null.")
    if transport_error == "send_timeout" and status.get("lastTransportTimeout") is not True:
        errors.append("Status send_timeout did not set lastTransportTimeout.")
    if status.get("lastTransportTimeout") is True and transport_error != "send_timeout":
        errors.append(f"Status lastTransportTimeout is set but lastTransportError is {transport_error!r}.")
    path = status.get("lastTransportPath")
    if path not in ("jpeg", "raw", "stream", "debug"):
        errors.append(f"Status lastTransportPath is {path!r}; expected jpeg, raw, stream, or debug.")
    elif path != expected_path:
        errors.append(f"Status lastTransportPath is {path!r}; expected {expected_path}.")


def _expect_closed_still(url: str, fetch, errors: list[str]) -> None:
    try:
        status, _headers, body = fetch(url, 8)
    except Exception as exc:  # noqa: BLE001
        errors.append(f"GET /capture failed while still capture is unavailable: {exc}")
        return
    if status == 200:
        errors.append("GET /capture returned an image even though still capture is unavailable.")
        return
    code = _error_code(body)
    if code != "still_requires_psram":
        errors.append(f"Unavailable still capture returned {code or status}, expected still_requires_psram.")


def _stream_has_jpeg(url: str, stream_reader, errors: list[str], when: str) -> bool:
    try:
        chunk = stream_reader(url, 5)
    except Exception as exc:  # noqa: BLE001
        errors.append(f"Live stream failed {when}: {exc}")
        return False
    if b"\xff\xd8" not in chunk:
        errors.append(f"Live stream did not contain a JPEG {when}.")
        return False
    return True


def _header(headers: dict, name: str) -> str | None:
    for key, value in headers.items():
        if key.lower() == name.lower():
            return str(value)
    return None


def _header_int(headers: dict, name: str) -> int | None:
    value = _header(headers, name)
    if value is not None and value.isdigit():
        return int(value)
    return None


def _error_code(body: bytes) -> str | None:
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if isinstance(payload, dict) and payload.get("error"):
        return str(payload["error"])
    return None


def _http_fetch(url: str, timeout: float):
    req = request.Request(url, method="GET")
    try:
        with request.urlopen(req, timeout=timeout) as response:
            return response.status, {key: value for key, value in response.headers.items()}, response.read()
    except error.HTTPError as exc:
        return exc.code, {key: value for key, value in exc.headers.items()}, exc.read()


def _http_stream(url: str, timeout: float) -> bytes:
    req = request.Request(url, method="GET")
    with request.urlopen(req, timeout=timeout) as response:
        return response.read(65536)


def concurrent_checks(base: str, captures: int) -> list[str]:
    # Tek Vision yayın bağlantısı açıkken kontrol sunucusu cevap vermelidir.
    failures = []
    try:
        with request.urlopen(join_url(base, "/stream"), timeout=10) as stream:
            stream.read(1024)
            for _ in range(captures):
                for path in ("/status", "/capture"):
                    status, _, body = _http_fetch(join_url(base, path), 35)
                    if status != 200 or (path == "/capture" and not body.startswith(b"\xff\xd8")):
                        failures.append(f"Concurrent {path} failed: {status}")
    except Exception as exc:
        failures.append(f"Concurrent preview/still check failed: {exc}")
    return failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check an ESP32-CAM preview stream and still capture. Does not flash firmware.")
    parser.add_argument("--base", default="http://192.168.1.50:81", help="Camera origin, for example http://192.168.1.50:81")
    parser.add_argument("--captures", type=int, default=3, help="Number of still captures. Use 50 for the manual acceptance run.")
    parser.add_argument("--concurrent", action="store_true", help="Keep the preview open while requesting status and still frames.")
    parser.add_argument("--raw", action="store_true", help="Also check GET /capture/raw RGB565 frames. Does not change the JPEG checks.")
    args = parser.parse_args(argv)
    if args.captures < 1:
        print("captures must be at least 1", file=sys.stderr)
        return 2
    errors = run_checks(args.base, args.captures, _http_fetch, _http_stream, include_raw=args.raw)
    if args.concurrent:
        errors.extend(concurrent_checks(args.base, args.captures))
    if errors:
        print(f"Camera smoke test failed ({len(errors)}):", file=sys.stderr)
        for item in errors:
            print(f"- {item}", file=sys.stderr)
        return 1
    print(f"Camera smoke test passed for {args.captures} still capture(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
