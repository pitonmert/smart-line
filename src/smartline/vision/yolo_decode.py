"""YOLO26 detection decode for the Ultralytics export contract.

Training exports two detection layouts. The default ``model.export()`` path is the
raw one-to-many head: ``(batch, 4 + classes, anchors)`` with center ``xywh`` in the
letterboxed network input and one score per class. ``nms=False`` exports the
one-to-one head: ``(batch, max_det, 6)`` as ``x1, y1, x2, y2, confidence, class id``.

Both layouts are mapped back to source-image pixels. This module does not load
weights and does not invent a box when the tensor is empty.
"""

from __future__ import annotations


def letterbox_meta(source_width: int, source_height: int, input_size: int) -> dict:
    """Match Ultralytics ``scale_boxes`` padding for a square network input."""

    if source_width <= 0 or source_height <= 0 or input_size <= 0:
        raise ValueError("Image dimensions must be positive.")
    gain = min(input_size / source_height, input_size / source_width)
    pad_x = round((input_size - source_width * gain) / 2 - 0.1)
    pad_y = round((input_size - source_height * gain) / 2 - 0.1)
    return {
        "scale": gain,
        "padLeft": pad_x,
        "padTop": pad_y,
        "inputSize": input_size,
        "sourceWidth": source_width,
        "sourceHeight": source_height,
    }


def infer_layout(shape: tuple[int, ...], class_count: int | None = None) -> str:
    """Choose raw or end-to-end from an output shape.

    A declared layout from export metadata always wins over this heuristic.
    ``(anchors, 6)`` with a large anchor count is the two-class raw head, not
    the end-to-end ``(max_det, 6)`` head.
    """

    if len(shape) == 3:
        shape = shape[1:]
    if len(shape) != 2:
        raise ValueError(f"Detection output must be rank 2 or 3, got {shape}.")
    rows, cols = shape
    if class_count is not None and cols == 4 + class_count and rows > 300:
        return "raw"
    if class_count is not None and rows == 4 + class_count and cols > 300:
        return "raw"
    if cols == 6 and 0 < rows <= 1000:
        return "end2end"
    return "raw"


def decode_detections(
    output,
    meta: dict,
    confidence: float,
    class_names: list[str] | None = None,
    layout: str | None = None,
    iou: float = 0.45,
) -> list[dict]:
    """Return canonical boxes in source pixels: x, y, width, height, label, confidence."""

    rows = _rows(output)
    if not rows:
        return []
    kind = layout or infer_layout(_shape(output), None if class_names is None else len(class_names))
    if kind == "end2end":
        candidates = _end2end(rows, confidence)
    elif kind == "raw":
        candidates = _nms(
            _raw(rows, confidence, None if class_names is None else len(class_names)), iou
        )
    else:
        raise ValueError(f"Unsupported detection layout '{kind}'.")
    detections = []
    for box, score, class_id in candidates:
        scaled = _to_source(box, meta)
        if scaled is None:
            continue
        x, y, width, height = scaled
        detections.append(
            {
                "x": x,
                "y": y,
                "width": width,
                "height": height,
                "label": _label(class_id, class_names),
                "confidence": round(float(score), 6),
            }
        )
    return detections


def _rows(output) -> list[list[float]]:
    values = output.tolist() if hasattr(output, "tolist") else output
    if values is None or values == []:
        return []
    shape = _shape(values)
    if len(shape) == 3:
        values = values[0]
        if not values:
            return []
    elif len(shape) != 2:
        raise ValueError(f"Detection output must be rank 2 or 3, got {shape}.")
    return [[float(item) for item in row] for row in values]


def _shape(output) -> tuple[int, ...]:
    if hasattr(output, "shape"):
        return tuple(int(item) for item in output.shape)
    if not isinstance(output, list):
        raise ValueError("Detection output is not an array.")
    if not output:
        return (0,)
    if isinstance(output[0], list):
        return (len(output),) + _shape(output[0])
    return (len(output),)


def _end2end(rows: list[list[float]], confidence: float) -> list[tuple[list[float], float, int]]:
    found = []
    for row in rows:
        if len(row) < 6:
            continue
        score = row[4]
        if score < confidence:
            continue
        found.append((_denormalize(row[:4], None), score, int(round(row[5]))))
    return found


def _raw(
    rows: list[list[float]],
    confidence: float,
    class_count: int | None,
) -> list[tuple[list[float], float, int]]:
    oriented = _orient_raw(rows, class_count)
    found = []
    for row in oriented:
        if len(row) < 6:
            continue
        scores = row[4:]
        class_id = max(range(len(scores)), key=lambda index: scores[index])
        score = scores[class_id]
        if score < confidence:
            continue
        cx, cy, width, height = row[:4]
        box = [cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2]
        found.append((_denormalize(box, None), score, class_id))
    return found


def _orient_raw(rows: list[list[float]], class_count: int | None) -> list[list[float]]:
    if not rows:
        return rows
    row_count = len(rows)
    col_count = len(rows[0])
    channels = None if class_count is None else 4 + class_count
    if channels is not None and row_count == channels and col_count != channels:
        return _transpose(rows)
    if row_count < col_count and (channels is None or row_count != channels):
        return _transpose(rows)
    return rows


def _transpose(rows: list[list[float]]) -> list[list[float]]:
    return [list(column) for column in zip(*rows, strict=True)]


def _denormalize(box: list[float], input_size: int | None) -> list[float]:
    """Ultralytics pixel outputs stay as-is. A 0–1 export is scaled by the network input."""

    if input_size is None:
        return box
    if max(abs(value) for value in box) <= 1.5:
        return [value * input_size for value in box]
    return box


def _nms(
    items: list[tuple[list[float], float, int]],
    iou_threshold: float,
) -> list[tuple[list[float], float, int]]:
    order = sorted(range(len(items)), key=lambda index: items[index][1], reverse=True)
    kept: list[int] = []
    while order:
        current = order.pop(0)
        kept.append(current)
        order = [
            index
            for index in order
            if items[index][2] != items[current][2]
            or _iou(items[current][0], items[index][0]) < iou_threshold
        ]
    return [items[index] for index in kept]


def _iou(first: list[float], second: list[float]) -> float:
    left = max(first[0], second[0])
    top = max(first[1], second[1])
    right = min(first[2], second[2])
    bottom = min(first[3], second[3])
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    if intersection <= 0:
        return 0.0
    area_a = max(0.0, first[2] - first[0]) * max(0.0, first[3] - first[1])
    area_b = max(0.0, second[2] - second[0]) * max(0.0, second[3] - second[1])
    union = area_a + area_b - intersection
    if union <= 0:
        return 0.0
    return intersection / union


def _to_source(box: list[float], meta: dict) -> tuple[float, float, float, float] | None:
    scale = float(meta["scale"])
    if scale == 0:
        return None
    pad_x = float(meta["padLeft"])
    pad_y = float(meta["padTop"])
    input_size = meta.get("inputSize")
    x1, y1, x2, y2 = _denormalize(box, None if input_size is None else int(input_size))
    x1 = (x1 - pad_x) / scale
    y1 = (y1 - pad_y) / scale
    x2 = (x2 - pad_x) / scale
    y2 = (y2 - pad_y) / scale
    source_width = float(meta["sourceWidth"])
    source_height = float(meta["sourceHeight"])
    x1 = min(max(x1, 0.0), source_width)
    y1 = min(max(y1, 0.0), source_height)
    x2 = min(max(x2, 0.0), source_width)
    y2 = min(max(y2, 0.0), source_height)
    width = x2 - x1
    height = y2 - y1
    if width <= 0 or height <= 0:
        return None
    return (round(x1, 3), round(y1, 3), round(width, 3), round(height, 3))


def _label(class_id: int, class_names: list[str] | None) -> str:
    if class_names is not None and 0 <= class_id < len(class_names):
        return class_names[class_id]
    return str(class_id)
