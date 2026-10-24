"""Gerçek Vision HTTP katmanını sahte kamera/model ile çalıştıran test sunucusu."""
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

class Stills:
    last_error = None
    def capture(self):
        time.sleep(float(os.environ.get("SMARTLINE_TEST_CAPTURE_DELAY", "0")))
        return StillImage(b"\xff\xd8\xff\xd9", 640, 480, datetime.now(timezone.utc))

engine = InferenceEngine(None)
engine.predict = lambda frame: {"available": True, "backend": "test", "error": None, "latencyMs": 2, "detections": []}
context = {"selector": FrameSelector(), "stills": Stills(), "engine": engine, "recipes": ActiveRecipeSource("", ""), "service_key": "test-key"}
thread = start_control_server("127.0.0.1", 0, context)
print(context["port"], flush=True)
thread.join()
