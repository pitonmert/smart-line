"""Classical color measurement on an object ROI.

The detector does not own this result. Hue is matched against the active recipe
after a simple gray-world style white reference, and low-saturation pixels are ignored.
"""

from __future__ import annotations

import colorsys
import math


def analyze_color(pixels: list[tuple[int, int, int]], allowed_colors: list[dict], white_reference: tuple[int, int, int] | None = None) -> dict:
    normalized = [_normalize_pixel(pixel, white_reference) for pixel in pixels]
    samples = []
    for red, green, blue in normalized:
        hue, saturation, value = colorsys.rgb_to_hsv(red / 255, green / 255, blue / 255)
        if saturation >= 0.15 and value >= 0.15:
            samples.append((hue * 360, saturation, value))

    if not samples:
        return {
            "colorClass": "UNKNOWN",
            "confidence": 0,
            "chromaticPixelCount": 0,
            "meanHue": None,
            "method": "roi-hsv",
        }

    sin_sum = sum(math.sin(math.radians(sample[0])) for sample in samples)
    cos_sum = sum(math.cos(math.radians(sample[0])) for sample in samples)
    mean_hue = math.degrees(math.atan2(sin_sum, cos_sum)) % 360
    counts = []
    for color in allowed_colors:
        matched = sum(1 for hue, saturation, value in samples if _hue_in_range(hue, color) and saturation >= color.get("saturationMin", 0) and value >= color.get("valueMin", 0))
        counts.append((matched, color["code"]))
    matched_count, code = max(counts, default=(0, "UNKNOWN"))
    if matched_count == 0:
        code = "UNKNOWN"
    return {
        "colorClass": code,
        "confidence": round(matched_count / len(samples), 4),
        "chromaticPixelCount": len(samples),
        "meanHue": round(mean_hue, 2),
        "method": "roi-hsv",
    }


def _normalize_pixel(pixel: tuple[int, int, int], white_reference: tuple[int, int, int] | None) -> tuple[int, int, int]:
    if white_reference is None:
        return pixel
    scaled = []
    for channel, reference in zip(pixel, white_reference):
        if reference <= 1:
            scaled.append(channel)
        else:
            scaled.append(max(0, min(255, int(channel * 255 / reference))))
    return scaled[0], scaled[1], scaled[2]


def _hue_in_range(hue: float, color: dict) -> bool:
    start = float(color["hueMin"])
    end = float(color["hueMax"])
    if start <= end:
        return start <= hue <= end
    return hue >= start or hue <= end
