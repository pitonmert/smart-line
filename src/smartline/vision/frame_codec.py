"""Encode one selected frame as JPEG without reading the browser image element.

A frame may already be JPEG bytes, a ``{"jpeg": bytes}`` view used by tests, or a
BGR array from OpenCV. The dataset store writes the bytes once. This module does
not save a second copy.
"""

from __future__ import annotations

import io

MAX_PIXELS = 16_000_000


def _validate_size(width: int, height: int) -> None:
    if width <= 0 or height <= 0 or width * height > MAX_PIXELS:
        raise ValueError("Image dimensions must be positive and within the pixel limit.")


def _read_jpeg(payload: bytes):
    from PIL import Image

    if payload[:2] != b"\xff\xd8" or not payload.endswith(b"\xff\xd9"):
        raise ValueError("Frame is not a complete JPEG.")
    try:
        image = Image.open(io.BytesIO(payload))
        try:
            if image.format != "JPEG":
                raise ValueError("Frame is not a JPEG.")
            _validate_size(image.width, image.height)
            image.load()
            return image
        except Exception:
            image.close()
            raise
    except (OSError, Image.DecompressionBombError) as exc:
        raise ValueError("Frame JPEG cannot be decoded.") from exc


def encode_jpeg(frame, quality: int = 80) -> tuple[bytes, int, int]:
    if isinstance(frame, (bytes, bytearray)):
        payload = bytes(frame)
        with _read_jpeg(payload) as image:
            return payload, image.width, image.height
    if isinstance(frame, dict) and "jpeg" in frame:
        payload = bytes(frame["jpeg"])
        with _read_jpeg(payload) as image:
            return payload, image.width, image.height
    encoded = _encode_array(frame, quality)
    return encoded


def frame_size(frame) -> tuple[int | None, int | None]:
    if isinstance(frame, dict):
        width = frame.get("width")
        height = frame.get("height")
        if width is not None and height is not None:
            return int(width), int(height)
    shape = getattr(frame, "shape", None)
    if shape is not None and len(shape) >= 2:
        return int(shape[1]), int(shape[0])
    return None, None


def _encode_array(frame, quality: int) -> tuple[bytes, int, int]:
    width, height = frame_size(frame)
    if width is None or height is None:
        raise ValueError("Frame dimensions are missing.")
    _validate_size(width, height)
    try:
        import cv2

        ok, encoded = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), int(quality)])
        if not ok:
            raise ValueError("OpenCV did not encode the frame.")
        width, height = frame_size(frame)
        return encoded.tobytes(), width, height
    except ImportError:
        pass
    try:
        import numpy as np
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError(
            "Neither OpenCV nor Pillow is available to encode a camera frame."
        ) from exc
    array = np.asarray(frame)
    if array.ndim == 3 and array.shape[2] == 3:
        array = array[:, :, ::-1]
    image = Image.fromarray(array.astype("uint8"), "RGB")
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=int(quality))
    return buffer.getvalue(), image.width, image.height


def decode_bgr(frame):
    """Decode JPEG fallback frames for inference and color analysis."""
    if not isinstance(frame, dict) or "jpeg" not in frame:
        return frame
    import numpy as np

    with _read_jpeg(bytes(frame["jpeg"])) as image:
        return np.asarray(image.convert("RGB"))[:, :, ::-1].copy()
