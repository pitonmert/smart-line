"""One product, one selected frame, one inspection observation.

The API remains the authority that persists the decision. This pipeline prepares
the observation; recipe acceptance and routing are evaluated only by the API.
"""

from __future__ import annotations

from datetime import datetime, timezone

from smartline.vision.color_analysis import analyze_color
from smartline.vision.inference import InferenceEngine
from smartline.vision.size_estimation import estimate_visual_size, fuse_with_tof


def run_inspection(
    recipe: dict,
    frame_view: dict | None,
    trigger: dict,
    calibration: dict | None = None,
    engine: InferenceEngine | None = None,
) -> dict:
    captured_at = datetime.now(timezone.utc).isoformat()
    calibration = calibration or {}
    engine = engine or InferenceEngine(None)
    if frame_view is not None:
        from smartline.vision.frame_codec import decode_bgr, frame_size

        frame = decode_bgr(frame_view.get("frame"))
        frame_view = {**frame_view, "frame": frame}
        width, height = frame_size(frame)
        if width is not None and height is not None:
            frame_view = {**frame_view, "width": width, "height": height}
    prediction = engine.predict(None if frame_view is None else frame_view.get("frame"))
    detections = prediction["detections"]
    primary = max(detections, key=lambda item: item.get("confidence", 0), default=None)

    roi_pixels = (
        [] if frame_view is None else frame_view.get("roiPixels") or frame_view.get("pixels") or []
    )
    if not roi_pixels and frame_view is not None:
        roi_pixels = _pixels_from_frame(frame_view.get("frame"), recipe.get("roi") or {})
    color = analyze_color(
        roi_pixels, recipe.get("allowedColors", []), _white_reference(calibration)
    )
    supplied_color = trigger.get("colorClass")
    color_class = color["colorClass"] if color["colorClass"] != "UNKNOWN" else supplied_color

    pixel_width = None
    pixel_height = None
    if primary is not None:
        pixel_width = primary["width"]
        pixel_height = primary["height"]
    elif frame_view is not None and frame_view.get("roiWidth") and frame_view.get("roiHeight"):
        pixel_width = frame_view["roiWidth"]
        pixel_height = frame_view["roiHeight"]

    visual = None
    if pixel_width is not None and pixel_height is not None:
        visual = estimate_visual_size(
            pixel_width,
            pixel_height,
            calibration.get("pixelsPerMillimeter")
            if calibration.get("sourceWidth") == frame_view.get("width")
            and calibration.get("sourceHeight") == frame_view.get("height")
            else None,
        )
        visual = fuse_with_tof(visual, calibration.get("tofBaseline"))

    product_class = primary["label"] if primary is not None else trigger.get("productClass")
    confidence = primary["confidence"] if primary is not None else trigger.get("confidence")
    if confidence is None:
        confidence = 0 if prediction["error"] else color["confidence"]

    observation = {
        "observationSource": "model" if prediction["available"] else "manual",
        "capturedAt": captured_at,
        "productClass": product_class,
        "colorClass": color_class,
        "sizeClass": trigger.get("sizeClass"),
        "defectClasses": list(trigger.get("defectClasses") or []),
        "confidence": confidence,
        "visualWidth": None if visual is None else visual["visual"]["pixelWidth"],
        "visualHeight": None if visual is None else visual["visual"]["pixelHeight"],
        "estimatedWidthMm": None if visual is None else visual["visual"]["widthMm"],
        "estimatedHeightMm": None if visual is None else visual["visual"]["heightMm"],
        "simulation": bool(trigger.get("simulation")),
        "triggerSource": trigger.get("source", "manual"),
        "boundingBoxes": detections,
        "modelName": None if not prediction["available"] else recipe.get("model", {}).get("name"),
        "modelVersion": None
        if not prediction["available"]
        else recipe.get("model", {}).get("version"),
        "processingLatencyMs": None
        if prediction["latencyMs"] is None
        else int(round(prediction["latencyMs"])),
        "cameraSource": recipe.get("cameraProfile", {}).get("streamUrl"),
        "sourceWidth": None if frame_view is None else frame_view.get("width"),
        "sourceHeight": None if frame_view is None else frame_view.get("height"),
        "frameRole": None if frame_view is None else frame_view.get("role"),
        "captureSource": None if frame_view is None else frame_view.get("captureSource"),
        "previewFallback": False if frame_view is None else bool(frame_view.get("previewFallback")),
    }
    return {
        "capturedAt": captured_at,
        "observation": observation,
        "inference": {
            "available": prediction["available"],
            "backend": prediction["backend"],
            "error": prediction["error"],
            "latencyMs": prediction["latencyMs"],
        },
        "color": color,
        "visualSize": None if visual is None else visual["visual"],
        "sizeFusion": visual,
        "simulation": observation["simulation"],
    }


def _pixels_from_frame(frame, roi: dict) -> list[tuple[int, int, int]]:
    shape = getattr(frame, "shape", None)
    if shape is None or len(shape) < 2:
        return []
    height, width = int(shape[0]), int(shape[1])
    x0 = max(0, int(float(roi.get("x", 0)) * width))
    y0 = max(0, int(float(roi.get("y", 0)) * height))
    x1 = min(width, x0 + max(1, int(float(roi.get("width", 1)) * width)))
    y1 = min(height, y0 + max(1, int(float(roi.get("height", 1)) * height)))
    crop = frame[y0:y1, x0:x1]
    if getattr(crop, "size", 0) == 0:
        return []
    rgb = crop[:, :, ::-1] if len(crop.shape) == 3 and crop.shape[-1] >= 3 else crop
    flat = rgb.reshape(-1, rgb.shape[-1])
    if len(flat) > 5000:
        flat = flat[:: max(1, len(flat) // 5000)]
    return [tuple(int(channel) for channel in pixel[:3]) for pixel in flat]


def _white_reference(calibration: dict):
    color = calibration.get("colorReference")
    if not color:
        return None
    return color.get("red"), color.get("green"), color.get("blue")
