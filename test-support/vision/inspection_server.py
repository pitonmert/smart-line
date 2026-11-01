"""Gerçek Vision HTTP katmanını sahte kamera/model ile çalıştıran test sunucusu."""
import io
import sys
import os
import time
from pathlib import Path
from datetime import datetime, timezone
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "SmartLine.Vision" / "src"))
from control_server import start_control_server
from frame_selector import FrameSelector
from inference import InferenceEngine
from recipe_source import ActiveRecipeSource
from still_capture import StillImage
from PIL import Image

image = io.BytesIO()
Image.new("RGB", (640, 480), (220, 20, 20)).save(image, format="JPEG")
JPEG = image.getvalue()

class Stills:
    last_error = None
    def capture(self):
        time.sleep(float(os.environ.get("SMARTLINE_TEST_CAPTURE_DELAY", "0")))
        return StillImage(JPEG, 640, 480, datetime.now(timezone.utc))

engine = InferenceEngine(None)
engine.predict = lambda frame: {"available": True, "backend": "test", "error": None, "latencyMs": 2, "detections": []}
context = {"selector": FrameSelector(), "stills": Stills(), "engine": engine, "recipes": ActiveRecipeSource("")}
thread = start_control_server("127.0.0.1", 0, context)
print(context["port"], flush=True)
thread.join()
