import json
import os
import time
import signal
import logging


from smartline.vision import mqtt_publisher
from smartline.vision import qr_reader
from smartline.vision.camera_stream import CameraStream
from smartline.vision.control_server import start_control_server
from smartline.vision.frame_selector import FrameSelector
from smartline.vision.inference import InferenceEngine
from smartline.vision.recipe_source import ActiveRecipeSource
from smartline.vision.still_capture import StillCapture, capture_url

logging.basicConfig(
    level=os.environ.get("LOG_LEVEL", "INFO"),
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
log = logging.getLogger("smartline.vision")

CAM_URL = os.environ.get("CAM_STREAM_URL", "http://192.168.1.50:81/stream")
SHOW_PREVIEW = os.environ.get("SHOW_PREVIEW", "false").lower() == "true"
REQUIRE_MQTT = os.environ.get("VISION_REQUIRE_MQTT", "true").lower() == "true"
CONTROL_HOST = os.environ.get("VISION_CONTROL_HOST", "127.0.0.1")
CONTROL_PORT = int(os.environ.get("VISION_CONTROL_PORT", "8090"))
RECIPE_PATH = os.environ.get("RECIPE_PATH", "")
MODEL_PATH = os.environ.get("MODEL_PATH", "")
MODEL_CONFIDENCE = float(os.environ.get("MODEL_CONFIDENCE", "0.25"))
MODEL_INPUT_SIZE = int(os.environ.get("MODEL_INPUT_SIZE", "640"))
MODEL_VERSION = os.environ.get("MODEL_VERSION", "")
MODEL_TASK = os.environ.get("MODEL_TASK", "detect")
MODEL_LAYOUT = os.environ.get("MODEL_LAYOUT", "")
ALLOW_PREVIEW_FALLBACK = os.environ.get("ALLOW_PREVIEW_FALLBACK", "false").lower() == "true"
PREFER_RAW_CAPTURE = os.environ.get("PREFER_RAW_CAPTURE", "true").lower() == "true"
STILL_CAPTURE_TIMEOUT = float(os.environ.get("STILL_CAPTURE_TIMEOUT", "30"))
API_BASE_URL = os.environ.get("API_BASE_URL", "")
CAM_CAPTURE_URL = os.environ.get("CAM_CAPTURE_URL", "")

stop = False


def _handle_signal(signum, frame):
    global stop
    log.warning("Sinyal alındı (%s), durduruluyor...", signum)
    stop = True


signal.signal(signal.SIGINT, _handle_signal)
signal.signal(signal.SIGTERM, _handle_signal)


def _load_recipe():
    if not RECIPE_PATH:
        log.info("RECIPE_PATH is empty. Inspection trigger will wait for a recipe file.")
        return None
    with open(RECIPE_PATH, encoding="utf-8") as handle:
        return json.load(handle)


def _apply_startup_classes(engine, recipe):
    names = _class_names(recipe)
    if names:
        engine.class_names = names


def _class_names(recipe):
    if not recipe:
        return None
    names = [item.get("code") for item in recipe.get("productClasses") or [] if item.get("code")]
    return names or None


def main():
    global stop
    log.info("Sistem başlatılıyor...")
    if REQUIRE_MQTT:
        mqtt_publisher.start_mqtt()
    else:
        log.warning("MQTT is disabled for this process. Barcode publish is off.")
    log.info("Kamera URL: %s", CAM_URL)
    log.info("Inspection is event-triggered. Frames are not sent to a model continuously.")

    selector = FrameSelector()
    recipe = _load_recipe()
    configured_input = recipe.get("model", {}).get("inputSize") if recipe else None
    engine = InferenceEngine(
        MODEL_PATH or None,
        confidence=MODEL_CONFIDENCE,
        class_names=None,
        input_size=int(configured_input or MODEL_INPUT_SIZE),
        layout=MODEL_LAYOUT or None,
        task=MODEL_TASK,
        model_version=MODEL_VERSION or None,
    )
    recipes = ActiveRecipeSource(API_BASE_URL, recipe)
    active, _meta = recipes.resolve(None)
    if active is not None:
        recipe = active
        # Model sınıfları yalnız artifact sözleşmesinden okunur.
    context = {
        "camera": None,
        "selector": selector,
        "engine": engine,
        "recipe": recipe,
        "recipes": recipes,
        "stills": StillCapture(
            capture_url(CAM_URL, CAM_CAPTURE_URL or None),
            timeout=STILL_CAPTURE_TIMEOUT,
            preview_fallback_allowed=ALLOW_PREVIEW_FALLBACK,
            prefer_raw=PREFER_RAW_CAPTURE,
        ),
        "allow_preview_fallback": ALLOW_PREVIEW_FALLBACK,
        "calibration": None,
    }
    stream = CameraStream(CAM_URL)
    context["camera"] = stream
    start_control_server(CONTROL_HOST, CONTROL_PORT, context)
    log.info("Vision control API listening on %s:%s", CONTROL_HOST, CONTROL_PORT)

    preview = None
    try:
        while not stop:
            frame = stream.read()
            if frame is None:
                log.warning("Kamera yayınına bağlanılamadı. 5 saniye içinde tekrar denenecek...")
                time.sleep(5)
                continue

            selector.observe(frame)
            if selector.observed_count % 150 == 0:
                context["stills"].refresh_status()
            if REQUIRE_MQTT:
                qr_reader.process_frame(frame, mqtt_publisher.publish_barcode)

            if SHOW_PREVIEW:
                import cv2

                preview = frame
                cv2.imshow("SmartLine Vision - Canli Yayin", preview)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    log.info("Kullanıcı çıkışı (q tuşuna basıldı).")
                    stop = True
    finally:
        log.info("Temizlik yapılıyor...")
        stream.close()
        server = context.get("server")
        if server is not None:
            server.shutdown()
            server.server_close()
        if SHOW_PREVIEW:
            import cv2

            cv2.destroyAllWindows()
        if REQUIRE_MQTT:
            mqtt_publisher.stop_mqtt()


if __name__ == "__main__":
    main()
