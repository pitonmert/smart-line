"""One still frame from the ESP32-CAM.

The live MJPEG preview stays at 240x176. Inspection and dataset capture prefer
GET /capture/raw. That body is big-endian RGB565 and is converted to a BGR
image on this host. JPEG compression then happens here, not on the ESP32.
GET /capture remains the fallback when the raw frame is missing or cannot be
decoded. This module does not invent a high-resolution frame when both fail.
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from datetime import datetime, timezone
from urllib import error, request
from urllib.parse import urlsplit, urlunsplit

from rgb565 import bgr_array


@dataclass
class StillImage:
    jpeg: bytes | None
    width: int | None
    height: int | None
    captured_at: datetime
    bgr: object | None = None
    raw: bytes | None = None
    capture_source: str = "esp32_capture"
    pixel_format: str = "JPEG"


@dataclass
class StillStatus:
    configured: bool
    capture_host: str
    last_error: str | None
    last_width: int | None
    last_height: int | None
    last_still_at: str | None
    camera_status: dict | None
    preview_fallback_allowed: bool
    capture_source: str | None = None
    last_raw_error: str | None = None


def capture_url(stream_url: str, explicit: str | None = None) -> str:
    """Point at /capture on the same host as the MJPEG stream."""

    if explicit:
        return explicit
    parts = urlsplit(stream_url)
    if not parts.scheme or not parts.netloc:
        return ""
    return urlunsplit((parts.scheme, parts.netloc.removesuffix(":81"), "/capture", "", ""))


def _jpeg_capture_url(capture: str) -> str:
    parts = urlsplit(capture)
    path = (parts.path or "").rstrip("/")
    if not parts.scheme or not parts.netloc or not path.endswith("/capture/raw"):
        return capture
    return urlunsplit((parts.scheme, parts.netloc, path[: -len("/raw")] or "/capture", "", ""))


def raw_capture_url(capture: str) -> str:
    """Point at /capture/raw beside a JPEG /capture URL."""

    parts = urlsplit(capture)
    if not parts.scheme or not parts.netloc:
        return ""
    path = parts.path or ""
    if path.rstrip("/").endswith("/capture/raw"):
        raw_path = path
    elif path.rstrip("/").endswith("/capture"):
        raw_path = path.rstrip("/") + "/raw"
    else:
        raw_path = "/capture/raw"
    return urlunsplit((parts.scheme, parts.netloc, raw_path, "", ""))


def status_url(capture: str) -> str:
    parts = urlsplit(capture)
    if not parts.scheme or not parts.netloc:
        return ""
    return urlunsplit((parts.scheme, parts.netloc, "/status", "", ""))


def _host(url: str) -> str:
    parts = urlsplit(url)
    if not parts.hostname:
        return ""
    if parts.port:
        return f"{parts.hostname}:{parts.port}"
    return parts.hostname


class StillCapture:
    def __init__(
        self,
        url: str,
        opener=None,
        timeout: float = 30.0,
        preview_fallback_allowed: bool = False,
        prefer_raw: bool = True,
        array_builder=None,
    ) -> None:
        self.url = _jpeg_capture_url(url)
        self.timeout = timeout
        self.preview_fallback_allowed = preview_fallback_allowed
        self.prefer_raw = prefer_raw
        self._opener = opener or _http_get
        self._array_builder = array_builder or bgr_array
        self.last_error: str | None = None if url else "capture_url_missing"
        self.last_raw_error: str | None = None
        self.last_width: int | None = None
        self.last_height: int | None = None
        self.last_still_at: datetime | None = None
        self.last_capture_source: str | None = None
        self.camera_status: dict | None = None

    def capture(self) -> StillImage | None:
        if not self.url:
            self.last_error = "capture_url_missing"
            return None
        self._deadline = time.monotonic() + self.timeout
        if self.prefer_raw:
            image = self._capture_raw()
            if image is not None:
                return image
        if time.monotonic() >= self._deadline:
            self.last_error = "capture_deadline_exceeded"
            return None
        return self._capture_jpeg()

    def _capture_raw(self) -> StillImage | None:
        endpoint = raw_capture_url(self.url)
        if not endpoint:
            self.last_raw_error = "capture_url_missing"
            self.last_error = self.last_raw_error
            return None
        try:
            status, headers, body = self._opener(endpoint, max(0.001, self._deadline - time.monotonic()))
        except Exception as exc:  # noqa: BLE001 - camera failures stay operational
            self.last_raw_error = str(exc)
            self.last_error = self.last_raw_error
            return None
        if status != 200:
            self.last_raw_error = _error_code(body) or f"http_{status}"
            self.last_error = self.last_raw_error
            return None
        if _header(headers, "X-Pixel-Format") != "RGB565":
            self.last_raw_error = "raw_pixel_format"
            self.last_error = self.last_raw_error
            return None
        if _header(headers, "X-Rgb565-Order") != "BE":
            self.last_raw_error = "raw_byte_order"
            self.last_error = self.last_raw_error
            return None
        width = _header_int(headers, "X-Frame-Width")
        height = _header_int(headers, "X-Frame-Height")
        if width is None or height is None:
            self.last_raw_error = "raw_dimensions"
            self.last_error = self.last_raw_error
            return None
        if len(body) != width * height * 2:
            self.last_raw_error = "raw_length"
            self.last_error = self.last_raw_error
            return None
        try:
            bgr = self._array_builder(bytes(body), width, height)
        except ImportError:
            self.last_raw_error = "rgb565_numpy_unavailable"
            self.last_error = self.last_raw_error
            return None
        except ValueError:
            self.last_raw_error = "raw_decode_failed"
            self.last_error = self.last_raw_error
            return None
        if bgr is None:
            self.last_raw_error = "raw_decode_failed"
            self.last_error = self.last_raw_error
            return None
        return self._remember(
            StillImage(
                None,
                width,
                height,
                datetime.now(timezone.utc),
                bgr=bgr,
                raw=bytes(body),
                capture_source="esp32_capture_raw",
                pixel_format="RGB565",
            )
        )

    def _capture_jpeg(self) -> StillImage | None:
        try:
            status, headers, body = self._opener(self.url, max(0.001, self._deadline - time.monotonic()))
        except Exception as exc:  # noqa: BLE001 - camera failures stay operational
            self.last_error = str(exc)
            return None
        if status != 200 or body[:2] != b"\xff\xd8":
            self.last_error = _error_code(body) or f"http_{status}"
            return None
        width = _header_int(headers, "X-Frame-Width")
        height = _header_int(headers, "X-Frame-Height")
        return self._remember(
            StillImage(bytes(body), width, height, datetime.now(timezone.utc), capture_source="esp32_capture", pixel_format="JPEG")
        )

    def _remember(self, image: StillImage) -> StillImage:
        self.last_error = None
        self.last_raw_error = None if image.capture_source == "esp32_capture_raw" else self.last_raw_error
        self.last_width = image.width
        self.last_height = image.height
        self.last_still_at = image.captured_at
        self.last_capture_source = image.capture_source
        return image

    def refresh_status(self) -> None:
        endpoint = status_url(self.url)
        if not endpoint:
            return
        try:
            status, _headers, body = self._opener(endpoint, 2.0)
        except Exception as exc:  # noqa: BLE001
            self.camera_status = {"reachable": False, "error": str(exc)}
            return
        if status != 200:
            self.camera_status = {"reachable": False, "error": _error_code(body) or f"http_{status}"}
            return
        import json

        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            self.camera_status = {"reachable": False, "error": "status_not_json"}
            return
        if isinstance(payload, dict):
            self.camera_status = payload

    def status(self) -> StillStatus:
        return StillStatus(
            configured=bool(self.url),
            capture_host=_host(self.url),
            last_error=self.last_error,
            last_width=self.last_width,
            last_height=self.last_height,
            last_still_at=None if self.last_still_at is None else self.last_still_at.isoformat(),
            camera_status=self.camera_status,
            preview_fallback_allowed=self.preview_fallback_allowed,
            capture_source=self.last_capture_source,
            last_raw_error=self.last_raw_error,
        )


def _header(headers: dict, name: str) -> str | None:
    for key, value in headers.items():
        if key.lower() == name.lower():
            return str(value)
    return None


def _header_int(headers: dict, name: str) -> int | None:
    value = _header(headers, name)
    if value is not None and str(value).isdigit():
        return int(value)
    return None


def _error_code(body: bytes) -> str | None:
    import json

    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if isinstance(payload, dict) and payload.get("error"):
        return str(payload["error"])
    return None


def _http_get(url: str, timeout: float):
    req = request.Request(url, method="GET")
    try:
        with request.urlopen(req, timeout=timeout) as response:
            headers = {key: value for key, value in response.headers.items()}
            body = response.read(2_000_001)
            if len(body) > 2_000_000: raise ValueError("Camera response exceeds the size limit")
            return response.status, headers, body
    except error.HTTPError as exc:
        return exc.code, {key: value for key, value in exc.headers.items()}, exc.read(65536)
