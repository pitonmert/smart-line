"""Visual size estimation.

Widths are pixel measurements converted with a calibration factor when one exists.
They are not a depth measurement. VL53L1X fusion is intentionally unavailable.
"""

from __future__ import annotations


def estimate_visual_size(pixel_width: float, pixel_height: float, pixels_per_millimeter: float | None) -> dict:
    if pixel_width < 0 or pixel_height < 0:
        raise ValueError("Visual size cannot be negative.")
    width_mm = None
    height_mm = None
    if pixels_per_millimeter is not None and pixels_per_millimeter > 0:
        width_mm = pixel_width / pixels_per_millimeter
        height_mm = pixel_height / pixels_per_millimeter
    return {
        "pixelWidth": pixel_width,
        "pixelHeight": pixel_height,
        "widthMm": width_mm,
        "heightMm": height_mm,
        "method": "visual_estimate",
        "depthAvailable": False,
        "distanceMm": None,
    }


def fuse_with_tof(visual: dict, tof: dict | None) -> dict:
    """Keep the visual estimate. Do not invent a distance from a missing sensor."""
    source = None if tof is None else tof.get("source")
    distance = None
    if source == "SIMULATION" and tof is not None:
        distance = tof.get("distanceMm")
    return {
        "visual": visual,
        "distanceMm": distance,
        "productPresent": None if source != "SIMULATION" or tof is None else tof.get("productPresent"),
        "sensorHealthy": None if source != "SIMULATION" or tof is None else tof.get("sensorHealthy"),
        "heightSource": "UNAVAILABLE" if distance is None else "SIMULATION",
        "note": "Physical height requires VL53L1X integration, which is deferred.",
    }
