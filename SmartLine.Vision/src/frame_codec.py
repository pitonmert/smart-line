"""Encode one selected frame as JPEG without reading the browser image element.

A frame may already be JPEG bytes, a ``{"jpeg": bytes}`` view used by tests, or a
BGR array from OpenCV. The dataset store writes the bytes once. This module does
not save a second copy.
"""

from __future__ import annotations

import io


def encode_jpeg(frame, quality: int = 80) -> tuple[bytes, int | None, int | None]:
    if isinstance(frame, (bytes, bytearray)):
        payload = bytes(frame)
        if payload[:2] != b"\xff\xd8":
            raise ValueError("Frame bytes are not a JPEG.")
        return payload, None, None
    if isinstance(frame, dict) and "jpeg" in frame:
        payload = bytes(frame["jpeg"])
        if payload[:2] != b"\xff\xd8":
            raise ValueError("Frame JPEG is not valid.")
        width = frame.get("width")
        height = frame.get("height")
        return payload, None if width is None else int(width), None if height is None else int(height)
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


def _encode_array(frame, quality: int) -> tuple[bytes, int | None, int | None]:
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
        raise RuntimeError("Neither OpenCV nor Pillow is available to encode a camera frame.") from exc
    array = np.asarray(frame)
    if array.ndim == 3 and array.shape[2] == 3:
        array = array[:, :, ::-1]
    image = Image.fromarray(array.astype("uint8"), "RGB")
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=int(quality))
    return buffer.getvalue(), image.width, image.height


def decode_bgr(frame):
    """JPEG fallback karelerini model ve renk analizi için gerçek piksele dönüştürür."""
    if not isinstance(frame, dict) or "jpeg" not in frame:
        return frame
    import numpy as np
    from PIL import Image
    with Image.open(io.BytesIO(frame["jpeg"])) as image:
        if image.width * image.height > 16_000_000:
            raise ValueError("Image exceeds the pixel limit")
        return np.asarray(image.convert("RGB"))[:, :, ::-1].copy()
