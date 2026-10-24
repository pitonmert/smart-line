"""Small local control API for health, inspection, and one JPEG frame.

The dashboard captures through the API. This process returns the selected JPEG
and does not write the dataset. MQTT credentials are not part of any response.
"""

from __future__ import annotations

import json
import hmac
from threading import Lock, BoundedSemaphore
from triggers import TriggerRequest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from urllib.parse import urlsplit

from frame_codec import encode_jpeg
from frame_selector import ROLES


class BoundedControlServer(ThreadingHTTPServer):
    request_queue_size = 8
    def __init__(self, *args, **kwargs):
        self._slots = BoundedSemaphore(8)
        super().__init__(*args, **kwargs)
    def process_request(self, request, address):
        if not self._slots.acquire(blocking=False):
            try: request.sendall(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            finally: self.shutdown_request(request)
            return
        try: super().process_request(request, address)
        except Exception:
            self._slots.release()
            raise
    def process_request_thread(self, request, address):
        try: super().process_request_thread(request, address)
        finally: self._slots.release()


def start_control_server(host: str, port: int, context: dict) -> Thread:
    context.setdefault("operation_lock", Lock())
    handler = _handler(context)
    server = BoundedControlServer((host, port), handler)
    thread = Thread(target=server.serve_forever, name="smartline-vision-control", daemon=True)
    context["server"] = server
    context["port"] = server.server_address[1]
    thread.start()
    return thread


def _handler(context: dict):
    class Handler(BaseHTTPRequestHandler):
        def _authorized(self):
            key = context.get("service_key")
            if key and not hmac.compare_digest(self.headers.get("X-Vision-Key", ""), key):
                self._send(401, {"error": "unauthorized"})
                return False
            return True

        def do_GET(self):  # noqa: N802 - stdlib naming
            path = urlsplit(self.path).path
            if path == "/health":
                self._send(200, _health(context))
                return
            if path.startswith("/frames/"):
                if not self._authorized(): return
                role = path.removeprefix("/frames/").strip("/")
                if role == "preview":
                    selected, failure = _acquire(context, role)
                    self._send_frame(role, selected, failure)
                elif context["operation_lock"].acquire(blocking=False):
                    try:
                        selected, failure = _acquire(context, role)
                        self._send_frame(role, selected, failure)
                    finally: context["operation_lock"].release()
                else: self._send(409, {"error": "capture_busy"})
                return
            self._send(404, {"error": "not_found"})

        def do_POST(self):
            if not self._authorized(): return
            if not context["operation_lock"].acquire(blocking=False):
                self._send(409, {"error": "inspection_busy"})
                return
            try:
                self.connection.settimeout(35)
                self._inspect()
            except (ValueError, TypeError, KeyError, json.JSONDecodeError) as exc:
                self._send(400, {"error": "invalid_request"})
            except TimeoutError:
                self._send(504, {"error": "inspection_timeout"})
            finally:
                context["operation_lock"].release()

        def _inspect(self):  # noqa: N802 - stdlib naming
            path = urlsplit(self.path).path
            if path in ("/inspect", "/validate-recipe"):
                length = int(self.headers.get("Content-Length", "0"))
                if length < 0 or length > 65536:
                    self._send(413, {"error": "body_too_large"})
                    return
                raw = self.rfile.read(length) if length else b"{}"
                payload = json.loads(raw.decode("utf-8") or "{}")
                if not isinstance(payload, dict): raise ValueError("object required")
                if any(k in payload for k in ("productClass", "colorClass", "sizeClass", "confidence", "pixels", "defectClasses")):
                    raise ValueError("Model inspection cannot use operator-supplied predictions")
                TriggerRequest(source=payload.get("source", "api"), simulation=bool(payload.get("simulation"))).validate()
                source = context.get("recipes")
                if source is None:
                    recipe = context.get("recipe")
                    meta = {"source": "file" if recipe else "missing", "id": None, "name": None if recipe is None else recipe.get("name"), "version": None if recipe is None else recipe.get("version")}
                else:
                    recipe, meta = source.resolve(payload.get("recipe"))
                if recipe is None:
                    code = meta.get("error") or "recipe_not_loaded"
                    self._send(409, {"error": code})
                    return
                context["recipe"] = recipe
                engine = context.get("engine")
                expected = [item["code"] for item in recipe.get("productClasses", [])]
                if recipe.get("model", {}).get("artifactFileName") and (engine is None or not engine.model_path):
                    self._send(409, {"error": "model_not_configured"})
                    return
                if engine is not None and engine.model_path:
                    from pathlib import Path
                    if engine.backend is None or engine.load_error or not engine.contract_verified or engine.input_size != recipe.get("model", {}).get("inputSize") or engine.task != "detect" or engine.class_names != expected or engine.model_version != recipe.get("model", {}).get("version") or Path(engine.model_path).name != recipe.get("model", {}).get("artifactFileName"):
                        self._send(409, {"error": "model_recipe_mismatch"})
                        return
                if path == "/validate-recipe":
                    self._send(200, {"valid": True})
                    return
                selected, failure = _acquire(context, "inspection")
                if selected is None:
                    self._send(409, {"error": "still_capture_failed", "detail": failure, "previewFallback": False})
                    return
                frame_view = {
                    "frame": selected.frame,
                    "pixels": payload.get("pixels") or [],
                    "role": selected.role,
                    "origin": selected.origin,
                    "width": selected.width,
                    "height": selected.height,
                    "captureSource": selected.capture_source,
                    "previewFallback": selected.preview_fallback,
                }
                from pipeline import run_inspection

                result = run_inspection(
                    recipe,
                    frame_view,
                    payload,
                    payload.get("calibration") or context.get("calibration"),
                    context["engine"],
                )
                if not result["inference"]["available"]:
                    self._send(503, {"error": "inference_unavailable"})
                    return
                result["recipeId"] = meta.get("id")
                result["recipeVersion"] = meta.get("version")
                result["recipeSource"] = meta.get("source")
                result["frame"] = {
                    "role": selected.role,
                    "origin": selected.origin,
                    "width": selected.width,
                    "height": selected.height,
                    "capturedAt": selected.captured_at.isoformat(),
                    "captureSource": selected.capture_source,
                    "previewFallback": selected.preview_fallback,
                }
                self._send(200, result)
                return
            self._send(404, {"error": "not_found"})

        def log_message(self, format, *args):  # noqa: A002 - stdlib signature
            return

        def _send_frame(self, role: str, selected, failure: str | None) -> None:
            if role not in ROLES:
                self._send(404, {"error": "unknown_frame_role"})
                return
            if selected is None:
                self._send(409, {
                    "error": "still_capture_failed" if role != "preview" else "frame_not_available",
                    "detail": failure,
                    "previewFallback": False,
                })
                return
            try:
                quality = 85 if selected.capture_source == "esp32_capture_raw" else 80
                payload, encoded_width, encoded_height = encode_jpeg(selected.frame, quality)
            except (ValueError, RuntimeError) as exc:
                self._send(409, {"error": "frame_encode_failed", "detail": str(exc)})
                return
            width = selected.width if selected.width is not None else encoded_width
            height = selected.height if selected.height is not None else encoded_height
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("X-Frame-Role", selected.role)
            self.send_header("X-Frame-Origin", selected.origin)
            self.send_header("X-Captured-At", selected.captured_at.isoformat())
            self.send_header("X-Frame-Sequence", str(selected.sequence))
            if width is not None:
                self.send_header("X-Frame-Width", str(width))
            if height is not None:
                self.send_header("X-Frame-Height", str(height))
            self.send_header("X-Capture-Source", selected.capture_source)
            self.send_header("X-Preview-Fallback", "true" if selected.preview_fallback else "false")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(payload)

        def _send(self, status: int, body: dict) -> None:
            encoded = json.dumps(body).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

    return Handler


def _acquire(context: dict, role: str):
    selector = context["selector"]
    if role == "preview":
        return selector.select("preview"), None
    still_source = context.get("stills")
    failure = "still_capture_unavailable"
    if still_source is not None:
        image = still_source.capture()
        if image is not None:
            source = getattr(image, "capture_source", None) or "esp32_capture"
            bgr = getattr(image, "bgr", None)
            jpeg_bytes = getattr(image, "jpeg", None)
            if bgr is not None:
                frame = bgr
            elif jpeg_bytes:
                frame = {"jpeg": jpeg_bytes, "width": image.width, "height": image.height}
            else:
                frame = None
            if frame is not None:
                selector.offer_still(role, frame, image.captured_at, capture_source=source)
                return selector.select(role), None
            failure = "still_frame_undecoded"
        else:
            failure = still_source.last_error or "still_capture_failed"
    if context.get("allow_preview_fallback"):
        selected = selector.use_preview_fallback(role)
        if selected is not None:
            return selected, None
        return None, failure
    return None, failure


def _apply_class_names(engine, recipe: dict) -> None:
    if engine is None:
        return
    names = [item.get("code") for item in recipe.get("productClasses") or [] if isinstance(item, dict) and item.get("code")]
    if names:
        engine.class_names = names


def _still_report(context: dict) -> dict:
    source = context.get("stills")
    if source is None:
        return {
            "configured": False,
            "captureHost": "",
            "previewFallbackAllowed": bool(context.get("allow_preview_fallback")),
            "lastError": "still_capture_unavailable",
            "lastWidth": None,
            "lastHeight": None,
            "lastStillAt": None,
            "cameraStatus": None,
            "captureSource": None,
            "lastRawError": None,
        }
    status = source.status()
    return {
        "configured": status.configured,
        "captureHost": status.capture_host,
        "previewFallbackAllowed": status.preview_fallback_allowed,
        "lastError": status.last_error,
        "lastWidth": status.last_width,
        "lastHeight": status.last_height,
        "lastStillAt": status.last_still_at,
        "cameraStatus": status.camera_status,
        "captureSource": status.capture_source,
        "lastRawError": status.last_raw_error,
    }


def _recipe_report(context: dict) -> dict:
    source = context.get("recipes")
    recipe = context.get("recipe") or {}
    return {
        "loaded": recipe is not None and bool(recipe),
        "source": None if source is None else source.last_source,
        "id": None if not isinstance(recipe, dict) else recipe.get("id"),
        "name": None if not isinstance(recipe, dict) else recipe.get("name"),
        "version": None if not isinstance(recipe, dict) else recipe.get("version"),
    }


def _health(context: dict) -> dict:
    camera = context["camera"].status()
    latest = context["selector"].latest()
    stream_url = camera.url if "://" in camera.url and "@" not in camera.url else None
    return {
        "status": "healthy",
        "cameraOnline": camera.online,
        "streamHost": camera.stream_host,
        "streamUrl": stream_url,
        "lastError": camera.last_error,
        "framesRead": camera.frames_read,
        "consecutiveFailures": camera.consecutive_failures,
        "frameWidth": None if latest is None else latest.width,
        "frameHeight": None if latest is None else latest.height,
        "lastFrameAt": None if latest is None else latest.captured_at.isoformat(),
        "camera": {
            "connected": camera.online,
            "streamHost": camera.stream_host,
            "lastFrameAt": None if camera.last_frame_at is None else camera.last_frame_at.isoformat(),
            "frameWidth": camera.frame_width if latest is None else latest.width,
            "frameHeight": camera.frame_height if latest is None else latest.height,
            "consecutiveFailures": camera.consecutive_failures,
            "still": _still_report(context),
        },
        "frames": context["selector"].role_status(),
        "recipe": _recipe_report(context),
        "model": context["engine"].status(),
        "simulation": False,
    }
