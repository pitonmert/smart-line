"""Run the production image HTTP adapter with a synthetic camera and inference backend."""

import io
import os
import signal
import threading
import time
from datetime import datetime, timezone

from PIL import Image

from smartline.vision.camera_stream import CameraStatus
from smartline.vision.control_server import start_control_server
from smartline.vision.frame_selector import FrameSelector
from smartline.vision.inference import InferenceEngine
from smartline.vision.recipe_source import ActiveRecipeSource
from smartline.vision.still_capture import StillImage, StillStatus


image = io.BytesIO()
Image.new("RGB", (640, 480), (220, 20, 20)).save(image, format="JPEG")
jpeg = image.getvalue()


class Camera:
    def status(self):
        return CameraStatus(
            True,
            "http://127.0.0.1:81/stream",
            None,
            3,
            0,
            240,
            176,
            datetime.now(timezone.utc),
            "127.0.0.1:81",
        )


class Stills:
    last_error = None

    def capture(self):
        time.sleep(float(os.getenv("SMARTLINE_TEST_CAPTURE_DELAY", "0")))
        return StillImage(
            jpeg, 640, 480, datetime.now(timezone.utc), capture_source="esp32_capture_raw"
        )

    def status(self):
        return StillStatus(True, "127.0.0.1", None, 640, 480, None, {"sensorPid": "0x2145"}, False)


engine = InferenceEngine(None)
engine.predict = lambda frame: {
    "available": True,
    "backend": "test",
    "error": None,
    "latencyMs": 2,
    "detections": [],
}
selector = FrameSelector()
selector.observe({"jpeg": jpeg, "width": 640, "height": 480})
context = {
    "camera": Camera(),
    "selector": selector,
    "stills": Stills(),
    "engine": engine,
    "recipes": ActiveRecipeSource(""),
}
thread = start_control_server("127.0.0.1", 0, context)
stop = threading.Event()
signal.signal(signal.SIGTERM, lambda *a: stop.set())
signal.signal(signal.SIGINT, lambda *a: stop.set())
print(context["port"], flush=True)
stop.wait()
context["server"].shutdown()
context["server"].server_close()
thread.join(timeout=5)
