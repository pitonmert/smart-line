import json
import io
import unittest
from pathlib import Path


from smartline.vision.camera_stream import CameraStream
from smartline.vision.color_analysis import analyze_color
from smartline.vision.frame_selector import FrameSelector
from smartline.vision.inference import InferenceEngine, select_backend
from smartline.vision.pipeline import run_inspection
from smartline.vision.size_estimation import estimate_visual_size, fuse_with_tof
from smartline.vision.triggers import TriggerRequest

SRC = Path(__file__).resolve().parents[2] / "src" / "smartline" / "vision"
ROOT = Path(__file__).resolve().parents[2]


RECIPE = json.loads(
    (
        ROOT / "src" / "smartline" / "inspection" / "contracts" / "recipes" / "demobox.json"
    ).read_text()
)


class ColorAndSizeTests(unittest.TestCase):
    def test_red_roi_is_measured_separately_from_another_roi(self):
        red = analyze_color([(220, 20, 20)] * 40, RECIPE["allowedColors"])
        blue = analyze_color([(20, 40, 220)] * 40, RECIPE["allowedColors"])
        self.assertEqual("RED", red["colorClass"])
        self.assertEqual("BLUE", blue["colorClass"])

    def test_white_reference_scales_channels(self):
        dim_red = [(30, 20, 20)] * 20
        uncorrected = analyze_color(dim_red, RECIPE["allowedColors"])
        corrected = analyze_color(dim_red, RECIPE["allowedColors"], white_reference=(100, 100, 100))
        self.assertEqual("UNKNOWN", uncorrected["colorClass"])
        self.assertEqual("RED", corrected["colorClass"])

    def test_visual_size_does_not_invent_depth(self):
        visual = estimate_visual_size(80, 40, 2)
        self.assertEqual(40, visual["widthMm"])
        self.assertFalse(visual["depthAvailable"])
        self.assertIsNone(visual["distanceMm"])
        fused = fuse_with_tof(visual, {"source": "NOT_INTEGRATED", "distanceMm": 12})
        self.assertIsNone(fused["distanceMm"])
        simulated = fuse_with_tof(
            visual, {"source": "SIMULATION", "distanceMm": 12, "productPresent": True}
        )
        self.assertEqual(12, simulated["distanceMm"])
        self.assertEqual("SIMULATION", simulated["heightSource"])


class StreamAndTriggerTests(unittest.TestCase):
    def test_reconnect_and_frame_selection_do_not_infer(self):
        calls = {"opened": 0}

        class FakeCapture:
            def __init__(self, fail: bool):
                self.fail = fail

            def isOpened(self):
                return True

            def read(self):
                if self.fail:
                    return False, None
                return True, {"id": "frame"}

            def release(self):
                return None

        def opener(_url):
            calls["opened"] += 1
            return FakeCapture(fail=calls["opened"] == 1)

        stream = CameraStream("http://192.168.1.50:81/stream", opener=opener)
        self.assertIsNone(stream.read())
        self.assertFalse(stream.status().online)
        frame = stream.read()
        self.assertEqual({"id": "frame"}, frame)
        self.assertEqual(2, calls["opened"])
        selector = FrameSelector()
        selector.observe(frame)
        self.assertIs(frame, selector.select("preview").frame)
        self.assertIsNone(selector.select_for_inspection())
        fallback = selector.use_preview_fallback("inspection")
        self.assertIs(frame, fallback.frame)
        self.assertTrue(fallback.preview_fallback)
        self.assertEqual(1, selector.observed_count)

    def test_hardware_trigger_is_deferred(self):
        trigger = TriggerRequest(source="hardware", distance_mm=80)
        with self.assertRaises(ValueError):
            trigger.validate()
        simulated = TriggerRequest(
            source="simulator", simulation=True, distance_mm=80, confidence=0.5
        )
        simulated.validate()
        self.assertEqual("simulator", simulated.source)


class PipelineTests(unittest.TestCase):
    def test_jpeg_dimensions_come_from_decoded_pixels(self):
        from PIL import Image

        output = io.BytesIO()
        Image.new("RGB", (4, 2), (220, 20, 20)).save(output, format="JPEG")
        result = run_inspection(
            RECIPE,
            {
                "frame": {"jpeg": output.getvalue(), "width": 999, "height": 999},
                "width": 999,
                "height": 999,
            },
            {"source": "api"},
        )
        self.assertEqual(4, result["observation"]["sourceWidth"])
        self.assertEqual(2, result["observation"]["sourceHeight"])

    def test_inference_keeps_observation_metadata_without_a_decision(self):
        engine = InferenceEngine(None)
        detection = {
            "label": "BOX_RED",
            "confidence": 0.95,
            "x": 0,
            "y": 0,
            "width": 4,
            "height": 2,
        }
        engine.predict = lambda frame: {
            "available": True,
            "error": None,
            "latencyMs": 3,
            "backend": "test",
            "detections": [detection],
        }
        result = run_inspection(
            RECIPE,
            {
                "frame": object(),
                "pixels": [(220, 20, 20)] * 30,
                "width": 4,
                "height": 2,
                "role": "inspection",
                "captureSource": "esp32_capture",
            },
            {"source": "api"},
            engine=engine,
        )
        observation = result["observation"]
        self.assertEqual("BOX_RED", observation["productClass"])
        self.assertEqual("RED", observation["colorClass"])
        self.assertEqual(0.95, observation["confidence"])
        self.assertEqual([detection], observation["boundingBoxes"])
        self.assertEqual(RECIPE["model"]["version"], observation["modelVersion"])
        self.assertEqual("inspection", observation["frameRole"])
        self.assertEqual("esp32_capture", observation["captureSource"])
        self.assertEqual(3, observation["processingLatencyMs"])
        self.assertNotIn("previewDecision", result)
        self.assertNotIn("decision", observation)
        self.assertNotIn("destinationBin", observation)

    def test_missing_model_does_not_invent_boxes(self):
        engine = InferenceEngine("/tmp/smartline-missing-model.onnx")
        result = run_inspection(
            RECIPE,
            {"frame": object(), "pixels": [(20, 40, 220)] * 30, "width": 80, "height": 40},
            {"source": "manual", "simulation": False, "confidence": 0.2},
            {"pixelsPerMillimeter": None, "tofBaseline": {"source": "NOT_INTEGRATED"}},
            engine,
        )
        self.assertEqual([], result["observation"]["boundingBoxes"])
        self.assertFalse(result["inference"]["available"])
        self.assertIsNone(result["inference"]["latencyMs"])
        self.assertEqual("BLUE", result["color"]["colorClass"])
        self.assertNotIn("previewDecision", result)
        self.assertFalse(result["observation"]["simulation"])

    def test_backend_selection_without_loading_weights(self):
        self.assertIsNone(select_backend(None))
        self.assertIsNone(select_backend("/tmp/does-not-exist.onnx"))


if __name__ == "__main__":
    unittest.main()
