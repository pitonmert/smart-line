"""Camera smoke checks use a fake transport. They do not contact hardware."""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import camera_smoke


JPEG = b"\xff\xd8\xff\xd9"
STREAM = b"--boundary\r\nContent-Type: image/jpeg\r\n\r\n" + JPEG
JPEG_HEADERS = {"X-Frame-Width": "640", "X-Frame-Height": "480", "X-Timing-Total-Ms": "40", "X-Pixel-Format": "JPEG"}


def status_body(**overrides):
    payload = {
        "sensorPid": "0x2145",
        "sensorName": "GC2145",
        "psramDetected": True,
        "previewWidth": 240,
        "previewHeight": 176,
        "previewFrameSize": "HQVGA",
        "configuredStillWidth": 640,
        "configuredStillHeight": 480,
        "configuredStillFrameSize": "VGA",
        "stillAvailable": True,
        "lastStillAtMs": 0,
        "lastStillWidth": 0,
        "lastStillHeight": 0,
        "lastCaptureError": None,
        "cameraReady": True,
        "lastCapturePath": "jpeg",
        "lastMutexWaitMs": 1,
        "lastStillDeinitMs": 2,
        "lastStillInitMs": 3,
        "lastStaleFrame1Ms": 4,
        "lastStaleFrame2Ms": 0,
        "lastFrameAcquireMs": 5,
        "lastJpegEncodeMs": 6,
        "lastFrameCopyMs": 7,
        "lastPreviewDeinitMs": 8,
        "lastPreviewInitMs": 9,
        "lastPreviewStaleMs": 0,
        "lastPreviewRestoreMs": 17,
        "lastHttpPrepMs": 1,
        "lastCaptureTotalMs": 40,
        "stillStaleFrames": 1,
        "previewStaleFrames": 0,
        "lastTransportError": None,
        "lastTransportErrno": 0,
        "lastTransportEspErr": 0,
        "lastTransportTimeout": False,
        "lastBodyStartedAtMs": 0,
        "lastBodyBytes": 4,
        "lastBodyWritten": 4,
        "lastBodySendMs": 1,
        "lastBodyChunkCount": 1,
        "lastBodyMaxChunkMs": 1,
        "lastBodyChunkBytes": 2048,
        "lastTransportPath": "jpeg",
        "lastStreamCaptureMs": 0,
        "lastStreamJpegMs": 0,
        "lastStreamJpegBytes": 0,
        "lastStreamSendMs": 0,
        "lastStreamFrames": 0,
        "lastStreamSendFailures": 0,
    }
    payload.update(overrides)
    return payload


class CameraSmokeTests(unittest.TestCase):
    def test_vga_stills_must_match_the_configured_size_and_leave_preview_alone(self):
        calls = {"capture": 0, "stream": 0}

        def fetch(url, _timeout):
            if url.endswith("/status"):
                body = status_body(lastStillWidth=640 if calls["capture"] else 0, lastStillHeight=480 if calls["capture"] else 0)
                return 200, {}, json.dumps(body).encode("utf-8")
            if url.endswith("/capture"):
                calls["capture"] += 1
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        def stream(_url, _timeout):
            calls["stream"] += 1
            return STREAM

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 3, fetch, stream)
        self.assertEqual([], errors)
        self.assertEqual(3, calls["capture"])
        self.assertEqual(2, calls["stream"])

    def test_missing_psram_must_not_claim_a_high_resolution_frame(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                body = status_body(psramDetected=False, stillAvailable=False, configuredStillWidth=0, configuredStillHeight=0, configuredStillFrameSize="")
                return 200, {}, json.dumps(body).encode("utf-8")
            if url.endswith("/capture"):
                return 503, {}, b'{"error":"still_requires_psram"}'
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 2, fetch, lambda *_args: STREAM)
        self.assertEqual([], errors)

    def test_a_preview_sized_jpeg_is_not_accepted_as_the_still(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                return 200, {}, json.dumps(status_body()).encode("utf-8")
            if url.endswith("/capture"):
                return 200, {"X-Frame-Width": "240", "X-Frame-Height": "176"}, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM)
        self.assertTrue(any("240x176" in item for item in errors))

    def test_status_errors_and_a_dead_stream_are_reported(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                return 503, {}, b'{"error":"camera_busy"}'
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: b"")
        self.assertTrue(any("HTTP 503" in item for item in errors))

        def good_status(url, _timeout):
            if url.endswith("/status"):
                return 200, {}, json.dumps(status_body(sensorPid="0x2640")).encode("utf-8")
            return 200, {"X-Frame-Width": "640", "X-Frame-Height": "480"}, JPEG

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, good_status, lambda *_args: b"no-jpeg")
        self.assertTrue(any("PID" in item for item in errors))
        self.assertTrue(any("JPEG" in item for item in errors))

    def test_raw_frames_must_match_rgb565_length_and_leave_preview_alone(self):
        calls = {"raw": 0}

        def fetch(url, _timeout):
            if url.endswith("/status"):
                body = status_body(
                    lastCapturePath="raw",
                    lastTransportPath="raw",
                    lastStillWidth=640,
                    lastStillHeight=480,
                    lastJpegEncodeMs=0,
                )
                return 200, {}, json.dumps(body).encode("utf-8")
            if url.endswith("/capture/raw"):
                calls["raw"] += 1
                pixel = b"\xf8\x00"
                frame = pixel * (640 * 480)
                headers = {
                    "X-Frame-Width": "640",
                    "X-Frame-Height": "480",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                    "X-Timing-Total-Ms": "12",
                }
                return 200, headers, frame
            if url.endswith("/capture"):
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM, include_raw=True)
        self.assertEqual([], errors)
        self.assertEqual(1, calls["raw"])

    def test_a_short_raw_frame_is_rejected(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                return 200, {}, json.dumps(status_body(lastCapturePath="raw", lastTransportPath="raw", lastStillWidth=640, lastStillHeight=480)).encode("utf-8")
            if url.endswith("/capture/raw"):
                return 200, {
                    "X-Frame-Width": "640",
                    "X-Frame-Height": "480",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                    "X-Timing-Total-Ms": "12",
                }, b"\xf8\x00"
            if url.endswith("/capture"):
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM, include_raw=True)
        self.assertTrue(any("614400" in item for item in errors))

    def test_timing_fields_are_required_after_a_still(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                payload = status_body(lastStillWidth=640, lastStillHeight=480)
                payload["lastCaptureTotalMs"] = None
                return 200, {}, json.dumps(payload).encode("utf-8")
            if url.endswith("/capture"):
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM)
        self.assertTrue(any("lastCaptureTotalMs" in item for item in errors))

    def test_a_short_body_cannot_report_transport_success(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                return 200, {}, json.dumps(status_body(
                    lastStillWidth=640,
                    lastStillHeight=480,
                    lastBodyBytes=29125,
                    lastBodyWritten=25390,
                    lastTransportError=None,
                )).encode("utf-8")
            if url.endswith("/capture"):
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM)
        self.assertTrue(any("lastTransportError" in item for item in errors))

    def test_send_timeout_must_set_the_timeout_flag(self):
        def fetch(url, _timeout):
            if url.endswith("/status"):
                return 200, {}, json.dumps(status_body(
                    lastStillWidth=640,
                    lastStillHeight=480,
                    lastBodyBytes=29125,
                    lastBodyWritten=25390,
                    lastTransportError="send_timeout",
                    lastTransportTimeout=False,
                )).encode("utf-8")
            if url.endswith("/capture"):
                return 200, JPEG_HEADERS, JPEG
            raise AssertionError(url)

        errors = camera_smoke.run_checks("http://192.168.1.50:81", 1, fetch, lambda *_args: STREAM)
        self.assertTrue(any("lastTransportTimeout" in item for item in errors))


if __name__ == "__main__":
    unittest.main()
