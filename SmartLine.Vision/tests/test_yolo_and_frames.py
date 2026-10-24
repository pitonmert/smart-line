import json
import sys
import tempfile
import unittest
import urllib.request
from pathlib import Path

SRC = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC))

from camera_stream import safe_stream_host
from control_server import start_control_server
from frame_selector import ORIGIN_LIVE, ORIGIN_STILL, FrameSelector
from inference import InferenceEngine, load_export_contract, select_backend
from yolo_decode import decode_detections, infer_layout, letterbox_meta


LIVE_JPEG = b"\xff\xd8\xff\xd9"
STILL_JPEG = b"\xff\xd8\xff\x00\xd9"


def identity_meta(width=100, height=80):
    return {
        "scale": 1,
        "padLeft": 0,
        "padTop": 0,
        "inputSize": max(width, height),
        "sourceWidth": width,
        "sourceHeight": height,
    }


class YoloContractTests(unittest.TestCase):
    def test_end_to_end_boxes_are_source_pixels_and_filtered(self):
        output = [[[10, 20, 40, 60, 0.91, 1], [1, 1, 4, 4, 0.2, 0]]]
        boxes = decode_detections(output, identity_meta(), 0.25, ["BOX_GREEN", "BOX_RED"], layout="end2end")
        self.assertEqual(
            [{"x": 10, "y": 20, "width": 30, "height": 40, "label": "BOX_RED", "confidence": 0.91}],
            boxes,
        )

    def test_letterbox_maps_input_pixels_back_to_the_source(self):
        meta = letterbox_meta(240, 176, 640)
        gain = 640 / 240
        box = [0, meta["padTop"], gain, meta["padTop"] + gain, 0.8, 0]
        boxes = decode_detections([[box]], meta, 0.25, ["BOX_RED"], layout="end2end")
        self.assertEqual(1, len(boxes))
        self.assertEqual(0, boxes[0]["x"])
        self.assertEqual(0, boxes[0]["y"])
        self.assertAlmostEqual(1, boxes[0]["width"], places=3)
        self.assertAlmostEqual(1, boxes[0]["height"], places=3)

    def test_raw_head_applies_confidence_and_nms(self):
        raw = [[
            [30, 30, 8],
            [30, 30, 12],
            [20, 20, 6],
            [20, 20, 6],
            [0.1, 0.05, 0.01],
            [0.91, 0.4, 0.1],
        ]]
        boxes = decode_detections(raw, identity_meta(), 0.25, ["BOX_GREEN", "BOX_RED"], layout="raw")
        self.assertEqual(1, len(boxes))
        self.assertEqual("BOX_RED", boxes[0]["label"])
        self.assertEqual(20, boxes[0]["x"])
        self.assertEqual(20, boxes[0]["y"])
        self.assertEqual(20, boxes[0]["width"])
        self.assertEqual(20, boxes[0]["height"])
        self.assertEqual(0.91, boxes[0]["confidence"])

    def test_layout_heuristic_matches_the_export_shapes(self):
        self.assertEqual("end2end", infer_layout((1, 300, 6), 3))
        self.assertEqual("raw", infer_layout((1, 6, 8400), 2))
        self.assertEqual("raw", infer_layout((1, 84, 8400), 80))
        self.assertEqual("raw", infer_layout((1, 8400, 84), 80))

    def test_empty_output_invents_nothing(self):
        self.assertEqual([], decode_detections([], identity_meta(), 0.25, ["BOX_RED"], layout="end2end"))

    def test_executor_records_latency_without_weights(self):
        seen = {}

        def runner(blob):
            seen["blob"] = blob
            return [[[2, 4, 12, 9, 0.5, 0]]]

        engine = InferenceEngine(
            None,
            executor=runner,
            confidence=0.25,
            class_names=["BOX_BLUE"],
            layout="end2end",
        )
        result = engine.predict({"blob": "unused", "meta": identity_meta()})
        self.assertEqual("unused", seen["blob"])
        self.assertTrue(result["available"])
        self.assertIsNotNone(result["latencyMs"])
        self.assertGreaterEqual(result["latencyMs"], 0)
        self.assertEqual("BOX_BLUE", result["detections"][0]["label"])
        self.assertEqual(2, result["detections"][0]["x"])
        self.assertEqual(5, result["detections"][0]["height"])

    def test_missing_artifact_has_no_latency_and_no_boxes(self):
        engine = InferenceEngine("/tmp/smartline-missing-model.onnx")
        result = engine.predict({"jpeg": LIVE_JPEG, "width": 4, "height": 3})
        self.assertEqual([], result["detections"])
        self.assertFalse(result["available"])
        self.assertIsNone(result["latencyMs"])
        self.assertEqual("model_not_found", result["error"])
        source = Path(SRC / "inference.py").read_text(encoding="utf-8")
        self.assertNotIn("postprocessing is not registered", source)

    def test_backend_selection_accepts_an_openvino_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "model.xml").write_text("<net/>", encoding="utf-8")
            (root / "model.bin").write_bytes(b"weights")
            available = {"ultralytics": False, "openvino": True, "onnxruntime": False}
            self.assertEqual("openvino", select_backend(str(root), available=available))
            pt = root / "best.pt"
            pt.write_bytes(b"checkpoint")
            self.assertEqual(
                "ultralytics",
                select_backend(str(pt), available={"ultralytics": True, "openvino": False, "onnxruntime": False}),
            )
            onnx = root / "best.onnx"
            onnx.write_bytes(b"graph")
            self.assertEqual(
                "onnxruntime",
                select_backend(str(onnx), available={"ultralytics": False, "openvino": True, "onnxruntime": True}),
            )
            self.assertEqual(
                "ultralytics",
                select_backend(str(onnx), available={"ultralytics": True, "openvino": True, "onnxruntime": True}),
            )
            contract = {
                "layout": "raw",
                "modelVersion": "demobox-1",
                "classNames": ["BOX_RED", "BOX_BLUE", "BOX_GREEN"],
            }
            onnx.with_suffix(".smartline.json").write_text(json.dumps(contract), encoding="utf-8")
            loaded = load_export_contract(onnx)
            self.assertEqual("raw", loaded["layout"])
            engine = InferenceEngine(str(onnx), preference="onnxruntime")
            self.assertEqual(["BOX_RED", "BOX_BLUE", "BOX_GREEN"], engine.class_names)
            self.assertEqual("raw", engine.layout)


class FrameRoleTests(unittest.TestCase):
    def test_roles_stay_distinct_and_a_still_does_not_replace_preview(self):
        selector = FrameSelector()
        live = {"jpeg": LIVE_JPEG, "width": 240, "height": 176}
        still = {"jpeg": STILL_JPEG, "width": 640, "height": 480}
        selector.observe(live)
        selector.offer_still("inspection", still)
        preview = selector.select("preview")
        inspection = selector.select("inspection")
        dataset = selector.select("dataset")
        self.assertEqual("preview", preview.role)
        self.assertEqual(ORIGIN_LIVE, preview.origin)
        self.assertFalse(preview.preview_fallback)
        self.assertEqual("mjpeg_preview", preview.capture_source)
        self.assertIs(live, preview.frame)
        self.assertEqual(ORIGIN_STILL, inspection.origin)
        self.assertIs(still, inspection.frame)
        self.assertEqual(640, inspection.width)
        self.assertEqual(480, inspection.height)
        self.assertFalse(inspection.preview_fallback)
        self.assertIsNone(dataset)
        fallback = selector.use_preview_fallback("dataset")
        self.assertEqual("dataset", fallback.role)
        self.assertEqual(ORIGIN_LIVE, fallback.origin)
        self.assertTrue(fallback.preview_fallback)
        self.assertEqual("mjpeg_preview", fallback.capture_source)
        self.assertEqual(240, fallback.width)
        self.assertEqual(176, fallback.height)
        selector.offer_still("dataset", still, capture_source="esp32_capture_raw")
        dataset = selector.select("dataset")
        self.assertEqual("esp32_capture_raw", dataset.capture_source)
        self.assertEqual(ORIGIN_STILL, dataset.origin)
        self.assertEqual(640, dataset.width)
        self.assertIs(still, dataset.frame)
        self.assertEqual("mjpeg_preview", selector.select("preview").capture_source)

    def test_capture_endpoint_returns_the_dataset_jpeg_once(self):
        class Camera:
            def status(self):
                from camera_stream import CameraStatus

                return CameraStatus(True, "http://user:secret@192.168.1.50:81/stream", None, 3, 0, 240, 176, None, "192.168.1.50:81")

        selector = FrameSelector()
        selector.observe({"jpeg": LIVE_JPEG, "width": 240, "height": 176})

        class Stills:
            def __init__(self):
                self.last_error = "still_capture_failed"
                self.calls = 0

            def capture(self):
                self.calls += 1
                from datetime import datetime, timezone

                from still_capture import StillImage

                return StillImage(STILL_JPEG, 640, 480, datetime.now(timezone.utc))

            def status(self):
                from still_capture import StillStatus

                return StillStatus(True, "192.168.1.50:81", None, 640, 480, None, {"sensorPid": "0x2145"}, False)

        stills = Stills()
        context = {
            "camera": Camera(),
            "selector": selector,
            "engine": InferenceEngine(None),
            "recipe": {"name": "DemoBox", "version": 3, "productClasses": [{"code": "BOX_RED"}]},
            "stills": stills,
            "allow_preview_fallback": False,
        }
        thread = start_control_server("127.0.0.1", 0, context)
        port = context["port"]
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/dataset") as response:
                self.assertEqual(STILL_JPEG, response.read())
                self.assertEqual("dataset", response.headers["X-Frame-Role"])
                self.assertEqual("still_capture", response.headers["X-Frame-Origin"])
                self.assertEqual("640", response.headers["X-Frame-Width"])
                self.assertEqual("480", response.headers["X-Frame-Height"])
                self.assertEqual("esp32_capture", response.headers["X-Capture-Source"])
                self.assertEqual("false", response.headers["X-Preview-Fallback"])
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/frames/preview") as response:
                self.assertEqual(LIVE_JPEG, response.read())
                self.assertEqual("240", response.headers["X-Frame-Width"])
                self.assertEqual("mjpeg_preview", response.headers["X-Capture-Source"])
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health") as response:
                health = json.loads(response.read().decode("utf-8"))
            self.assertEqual("healthy", health["status"])
            self.assertEqual("192.168.1.50:81", health["camera"]["streamHost"])
            self.assertNotIn("secret", json.dumps(health))
            self.assertIsNone(health["streamUrl"])
            self.assertEqual("still_capture", health["frames"]["dataset"]["origin"])
            self.assertEqual(640, health["frames"]["dataset"]["width"])
            self.assertFalse(health["frames"]["dataset"]["previewFallback"])
            self.assertIsNone(health["frames"]["inspection"])
            self.assertEqual("0x2145", health["camera"]["still"]["cameraStatus"]["sensorPid"])
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(timeout=2)

    def test_stream_host_drops_userinfo(self):
        self.assertEqual("192.168.1.50:81", safe_stream_host("http://user:secret@192.168.1.50:81/stream"))
        self.assertEqual("", safe_stream_host("not a url"))


class CameraFailureTests(unittest.TestCase):
    def test_consecutive_failures_reset_after_a_frame(self):
        from camera_stream import CameraStream

        class FakeCapture:
            def __init__(self, fail: bool):
                self.fail = fail

            def isOpened(self):
                return True

            def read(self):
                if self.fail:
                    return False, None
                return True, {"width": 240, "height": 176}

            def release(self):
                return None

        calls = {"opened": 0}

        def opener(_url):
            calls["opened"] += 1
            return FakeCapture(fail=calls["opened"] == 1)

        stream = CameraStream("http://192.168.1.50:81/stream", opener=opener)
        self.assertIsNone(stream.read())
        self.assertEqual(1, stream.status().consecutive_failures)
        self.assertFalse(stream.status().online)
        frame = stream.read()
        self.assertEqual(240, frame["width"])
        self.assertEqual(0, stream.status().consecutive_failures)
        self.assertEqual(240, stream.status().frame_width)
        self.assertEqual(176, stream.status().frame_height)
        self.assertEqual("192.168.1.50:81", stream.status().stream_host)


if __name__ == "__main__":
    unittest.main()
