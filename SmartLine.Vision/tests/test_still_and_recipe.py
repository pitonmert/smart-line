"""Still capture, explicit preview fallback, and the active recipe source."""

from __future__ import annotations

import json
import sys
import unittest
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from control_server import start_control_server
from frame_selector import FrameSelector
from inference import InferenceEngine
from recipe_source import ActiveRecipeSource
from control_server import _acquire
from rgb565 import decode_bgr565_be, decode_rgb565_be
from still_capture import StillCapture, StillImage, capture_url, raw_capture_url, status_url


LIVE = b"\xff\xd8\xff\xd9"
STILL = b"\xff\xd8\xff\x00\xd9"


def urlsplit_path(url: str) -> str:
    from urllib.parse import urlsplit

    return urlsplit(url).path


FILE_RECIPE = {"name": "FileBox", "version": 1, "productClasses": [{"code": "BOX_RED"}]}


class StillUrlTests(unittest.TestCase):
    def test_capture_url_replaces_only_the_path(self):
        self.assertEqual(
            "http://user:secret@192.168.1.50/capture",
            capture_url("http://user:secret@192.168.1.50:81/stream"),
        )
        self.assertEqual("http://cam.local/capture", capture_url("http://cam.local/live", None))
        self.assertEqual("http://10.0.0.5/capture", capture_url("http://ignored/stream", "http://10.0.0.5/capture"))
        self.assertEqual("", capture_url("not a url"))
        self.assertEqual("http://192.168.1.50:81/status", status_url("http://192.168.1.50:81/capture"))
        self.assertEqual(
            "http://user:secret@192.168.1.50:81/capture/raw",
            raw_capture_url("http://user:secret@192.168.1.50:81/capture"),
        )

    def test_a_non_jpeg_or_error_does_not_become_a_frame(self):
        def opener(url, _timeout):
            if url.endswith("/capture"):
                return 503, {}, b'{"error":"still_requires_psram"}'
            return 500, {}, b"nope"

        stills = StillCapture("http://192.168.1.50:81/capture", opener=opener)
        self.assertIsNone(stills.capture())
        self.assertEqual("still_requires_psram", stills.last_error)
        self.assertEqual("192.168.1.50:81", stills.status().capture_host)
        stills.refresh_status()
        self.assertEqual("http_500", stills.camera_status["error"])

    def test_success_records_the_measured_size(self):
        def opener(_url, _timeout):
            return 200, {"X-Frame-Width": "640", "X-Frame-Height": "480"}, STILL

        stills = StillCapture("http://user:secret@192.168.1.50:81/capture", opener=opener)
        image = stills.capture()
        self.assertEqual(STILL, image.jpeg)
        self.assertEqual(640, image.width)
        self.assertEqual(480, image.height)
        self.assertIsNone(stills.last_error)
        self.assertEqual("192.168.1.50:81", stills.status().capture_host)
        self.assertNotIn("secret", stills.status().capture_host)
        self.assertEqual("esp32_capture", image.capture_source)

    def test_raw_vga_is_preferred_over_jpeg(self):
        calls = []
        pixel = b"\xf8\x00"

        def opener(url, _timeout):
            calls.append(urlsplit_path(url))
            if url.endswith("/capture/raw"):
                return 200, {
                    "X-Frame-Width": "1",
                    "X-Frame-Height": "1",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                }, pixel
            raise AssertionError(url)

        marker = {"width": 1, "height": 1, "channels": "BGR"}

        def builder(data, width, height):
            self.assertEqual(pixel, data)
            self.assertEqual(1, width)
            self.assertEqual(1, height)
            return marker

        stills = StillCapture("http://user:secret@192.168.1.50:81/capture", opener=opener, array_builder=builder)
        image = stills.capture()
        self.assertEqual(["/capture/raw"], calls)
        self.assertEqual("esp32_capture_raw", image.capture_source)
        self.assertEqual("RGB565", image.pixel_format)
        self.assertIsNone(image.jpeg)
        self.assertEqual(pixel, image.raw)
        self.assertIs(marker, image.bgr)
        self.assertEqual((248, 0, 0), decode_rgb565_be(image.raw, 1, 1)[0])
        self.assertEqual((0, 0, 248), decode_bgr565_be(image.raw, 1, 1)[0])
        self.assertNotIn("secret", stills.status().capture_host)

        selector = FrameSelector()
        selector.observe({"jpeg": LIVE, "width": 240, "height": 176})
        selected, failure = _acquire({"selector": selector, "stills": stills, "allow_preview_fallback": False}, "inspection")
        self.assertIsNone(failure)
        self.assertEqual("esp32_capture_raw", selected.capture_source)
        self.assertEqual("still_capture", selected.origin)
        self.assertEqual("inspection", selected.role)
        self.assertEqual(1, selected.width)
        self.assertEqual(1, selected.height)
        self.assertIs(marker, selected.frame)
        self.assertEqual(LIVE, selector.select("preview").frame["jpeg"])
        self.assertEqual(["/capture/raw", "/capture/raw"], calls)

    def test_raw_failure_falls_back_to_jpeg_without_using_preview(self):
        calls = []

        def opener(url, _timeout):
            calls.append(url.rsplit("/", 1)[-1])
            if url.endswith("/capture/raw"):
                return 200, {
                    "X-Frame-Width": "2",
                    "X-Frame-Height": "1",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                }, b"\xf8\x00"
            if url.endswith("/capture"):
                return 200, {"X-Frame-Width": "640", "X-Frame-Height": "480"}, STILL
            raise AssertionError(url)

        stills = StillCapture("http://192.168.1.50:81/capture", opener=opener, array_builder=lambda *_args: {"width": 1, "height": 1})
        image = stills.capture()
        self.assertEqual(["raw", "capture"], calls)
        self.assertEqual(STILL, image.jpeg)
        self.assertEqual("esp32_capture", image.capture_source)
        self.assertEqual("raw_length", stills.last_raw_error)
        self.assertIsNone(stills.last_error)

        def fail(url, _timeout):
            if url.endswith("/capture/raw"):
                return 503, {}, b'{"error":"still_init_failed"}'
            return 503, {}, b'{"error":"still_jpeg_failed"}'

        failed = StillCapture("http://192.168.1.50:81/capture", opener=fail)
        self.assertIsNone(failed.capture())
        self.assertEqual("still_jpeg_failed", failed.last_error)
        self.assertEqual("still_init_failed", failed.last_raw_error)
        selector = FrameSelector()
        selector.observe({"jpeg": LIVE, "width": 240, "height": 176})
        selected, failure = _acquire({"selector": selector, "stills": failed, "allow_preview_fallback": False}, "dataset")
        self.assertIsNone(selected)
        self.assertEqual("still_jpeg_failed", failure)
        self.assertEqual(LIVE, selector.select("preview").frame["jpeg"])

    def test_missing_numpy_uses_the_jpeg_endpoint(self):
        calls = []

        def opener(url, _timeout):
            calls.append(url.rsplit("/", 1)[-1])
            if url.endswith("/capture/raw"):
                return 200, {
                    "X-Frame-Width": "1",
                    "X-Frame-Height": "1",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                }, b"\x00\x1f"
            return 200, {"X-Frame-Width": "640", "X-Frame-Height": "480"}, STILL

        def unavailable(*_args):
            raise ImportError("numpy")

        stills = StillCapture("http://192.168.1.50:81/capture", opener=opener, array_builder=unavailable)
        image = stills.capture()
        self.assertEqual(["raw", "capture"], calls)
        self.assertEqual("esp32_capture", image.capture_source)
        self.assertEqual("rgb565_numpy_unavailable", stills.last_raw_error)

    def test_jpeg_only_mode_does_not_request_raw(self):
        calls = []

        def opener(url, _timeout):
            calls.append(url)
            return 200, {"X-Frame-Width": "640", "X-Frame-Height": "480"}, STILL

        stills = StillCapture("http://192.168.1.50:81/capture/raw", opener=opener, prefer_raw=False)
        image = stills.capture()
        self.assertEqual(["http://192.168.1.50:81/capture"], calls)
        self.assertEqual(STILL, image.jpeg)


class RecipeSourceTests(unittest.TestCase):
    def test_api_snapshot_is_cached_and_a_bad_request_is_not_replaced(self):
        calls = {"count": 0}

        def transport(_url, headers):
            calls["count"] += 1
            self.assertEqual("device-secret", headers["X-Device-Key"])
            return {
                "data": {
                    "id": "recipe-9",
                    "name": "DemoBox",
                    "version": 9,
                    "definition": {"name": "DemoBox", "productClasses": [{"code": "BOX_BLUE"}]},
                }
            }

        source = ActiveRecipeSource("http://api:5047", "device-secret", FILE_RECIPE, transport=transport, ttl_seconds=30)
        recipe, meta = source.resolve(None)
        self.assertEqual("api", meta["source"])
        self.assertEqual(9, recipe["version"])
        self.assertEqual("BOX_BLUE", recipe["productClasses"][0]["code"])
        source.resolve(None)
        self.assertEqual(1, calls["count"])

        rejected, rejected_meta = source.resolve({"name": "Broken"})
        self.assertIsNone(rejected)
        self.assertEqual("recipe_invalid", rejected_meta["error"])
        self.assertEqual(1, calls["count"])

    def test_a_transport_failure_keeps_the_last_snapshot_then_the_file(self):
        def fail(_url, _headers):
            raise RuntimeError("offline")

        cold = ActiveRecipeSource("http://api:5047", "device-secret", FILE_RECIPE, transport=fail)
        recipe, meta = cold.resolve(None)
        self.assertEqual("file", meta["source"])
        self.assertEqual(1, recipe["version"])

        def succeed(_url, _headers):
            return {"id": "recipe-4", "name": "Live", "version": 4, "definition": {"productClasses": [{"code": "BOX_GREEN"}]}}

        warm = ActiveRecipeSource("http://api:5047", "device-secret", FILE_RECIPE, transport=succeed, ttl_seconds=0)
        warm.resolve(None)
        warm._transport = fail
        again, again_meta = warm.resolve(None)
        self.assertEqual("file", again_meta["source"])
        self.assertEqual(1, again["version"])
        self.assertIn("offline", warm.last_error)

    def test_request_snapshot_wins_when_it_is_complete(self):
        source = ActiveRecipeSource("", "", FILE_RECIPE)
        recipe, meta = source.resolve({"recipeId": "abc", "recipeVersion": 7, "definition": {"productClasses": [{"code": "BOX_RED"}]}})
        self.assertEqual("request", meta["source"])
        self.assertEqual(7, recipe["version"])
        self.assertEqual("abc", recipe["id"])


class FrameEndpointPolicyTests(unittest.TestCase):
    def _server(self, still_error, allow_fallback, recipe_source=None, capture_source="esp32_capture"):
        class Camera:
            def status(self):
                from camera_stream import CameraStatus

                return CameraStatus(True, "http://192.168.1.50:81/stream", None, 1, 0, 240, 176, None, "192.168.1.50:81")

        class Stills:
            def __init__(self):
                self.last_error = still_error
                self.calls = 0

            def capture(self):
                self.calls += 1
                if still_error:
                    return None
                return StillImage(STILL, 640, 480, datetime.now(timezone.utc), capture_source=capture_source)

            def status(self):
                from still_capture import StillStatus

                return StillStatus(True, "192.168.1.50:81", still_error, None, None, None, None, allow_fallback)

        selector = FrameSelector()
        selector.observe({"jpeg": LIVE, "width": 240, "height": 176})
        context = {
            "camera": Camera(),
            "selector": selector,
            "engine": InferenceEngine(None),
            "recipe": FILE_RECIPE,
            "recipes": recipe_source,
            "stills": Stills(),
            "allow_preview_fallback": allow_fallback,
        }
        thread = start_control_server("127.0.0.1", 0, context)
        return context, thread

    def test_fallback_disabled_does_not_return_the_preview(self):
        context, thread = self._server("still_capture_failed", False)
        port = context["port"]
        try:
            with self.assertRaises(urllib.error.HTTPError) as raised:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/dataset")
            self.assertEqual(409, raised.exception.code)
            body = json.loads(raised.exception.read().decode("utf-8"))
            raised.exception.close()
            self.assertEqual("still_capture_failed", body["error"])
            self.assertFalse(body["previewFallback"])
            self.assertEqual(1, context["stills"].calls)
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/preview") as response:
                self.assertEqual(LIVE, response.read())
                self.assertEqual("240", response.headers["X-Frame-Width"])
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)

    def test_explicit_fallback_is_labeled(self):
        context, thread = self._server("still_capture_failed", True)
        port = context["port"]
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/inspection") as response:
                self.assertEqual(LIVE, response.read())
                self.assertEqual("inspection", response.headers["X-Frame-Role"])
                self.assertEqual("live_stream", response.headers["X-Frame-Origin"])
                self.assertEqual("mjpeg_preview", response.headers["X-Capture-Source"])
                self.assertEqual("true", response.headers["X-Preview-Fallback"])
                self.assertEqual("240", response.headers["X-Frame-Width"])
                self.assertEqual("176", response.headers["X-Frame-Height"])
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)

    def test_inspect_uses_one_still_and_records_the_recipe_version(self):
        source = ActiveRecipeSource("", "", FILE_RECIPE)
        context, thread = self._server(None, False, source)
        port = context["port"]
        context["engine"].predict = lambda frame: {"available": True, "backend": "test", "error": None, "latencyMs": 1, "detections": []}
        payload = json.dumps({
            "recipe": {
                "id": "recipe-4",
                "version": 4,
                "name": "Bench",
                "definition": {
                    "productClasses": [{"code": "BOX_GREEN"}, {"code": "BOX_RED"}],
                    "acceptance": {"minimumConfidence": 0.6, "defaultDestinationBin": "MANUAL_REVIEW"},
                    "destinationBins": [{"code": "MANUAL_REVIEW", "role": "HOLD"}],
                },
            }
        }).encode("utf-8")
        request = urllib.request.Request(
            f"http://127.0.0.1:{port}/inspect",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request) as response:
                body = json.loads(response.read().decode("utf-8"))
            self.assertEqual(4, body["recipeVersion"])
            self.assertEqual("request", body["recipeSource"])
            self.assertEqual("recipe-4", body["recipeId"])
            self.assertEqual("inspection", body["frame"]["role"])
            self.assertEqual("still_capture", body["frame"]["origin"])
            self.assertEqual(640, body["frame"]["width"])
            self.assertEqual(480, body["frame"]["height"])
            self.assertEqual("esp32_capture", body["frame"]["captureSource"])
            self.assertFalse(body["frame"]["previewFallback"])
            self.assertEqual(640, body["observation"]["sourceWidth"])
            self.assertEqual("inspection", body["observation"]["frameRole"])
            self.assertIsNone(context["engine"].class_names)
            self.assertEqual(1, context["stills"].calls)
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/preview") as response:
                self.assertEqual(LIVE, response.read())
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)

    def test_dataset_frame_keeps_the_raw_capture_source(self):
        context, thread = self._server(None, False, capture_source="esp32_capture_raw")
        port = context["port"]
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/dataset") as response:
                self.assertEqual(STILL, response.read())
                self.assertEqual("dataset", response.headers["X-Frame-Role"])
                self.assertEqual("still_capture", response.headers["X-Frame-Origin"])
                self.assertEqual("esp32_capture_raw", response.headers["X-Capture-Source"])
                self.assertEqual("false", response.headers["X-Preview-Fallback"])
                self.assertEqual("640", response.headers["X-Frame-Width"])
                self.assertEqual("480", response.headers["X-Frame-Height"])
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/preview") as response:
                self.assertEqual(LIVE, response.read())
                self.assertEqual("mjpeg_preview", response.headers["X-Capture-Source"])
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)

    def test_inspect_without_a_still_does_not_invent_a_frame(self):
        context, thread = self._server("still_capture_failed", False, ActiveRecipeSource("", "", FILE_RECIPE))
        port = context["port"]
        request = urllib.request.Request(
            f"http://127.0.0.1:{port}/inspect",
            data=b"{}",
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with self.assertRaises(urllib.error.HTTPError) as raised:
                urllib.request.urlopen(request)
            self.assertEqual(409, raised.exception.code)
            body = json.loads(raised.exception.read().decode("utf-8"))
            raised.exception.close()
            self.assertEqual("still_capture_failed", body["error"])
            self.assertNotIn("observation", body)
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
