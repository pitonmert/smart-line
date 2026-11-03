"""Decode ESP32-CAM RGB565 without inventing a channel order.

The installed esp32-camera commit f0bb429 converts PIXFORMAT_RGB565 in
``to_jpg.cpp`` ``convert_line_format``. Byte 0 is the high byte ``RRRRRGGG``
and byte 1 is the low byte ``GGGBBBBB``. The 5-bit red and blue values and the
6-bit green value are expanded to 8 bits by shifting up and leaving the low
bits zero. This is big-endian RGB565. It is not a little-endian uint16 and it
does not swap red and blue.

OpenCV frames in this project are BGR. ``bgr_array`` builds that layout when
NumPy is installed. The pure-Python functions are the same expansion and are
what the unit tests lock down.
"""

from __future__ import annotations


BYTE_ORDER = "BE"


def expected_raw_length(width: int, height: int) -> int:
    return width * height * 2


def expand_rgb565_be(high: int, low: int) -> tuple[int, int, int]:
    """Return RGB888 for one big-endian RGB565 pixel."""

    red = high & 0xF8
    green = ((high & 0x07) << 5) | ((low & 0xE0) >> 3)
    blue = (low & 0x1F) << 3
    return red, green, blue


def decode_rgb565_be(data: bytes, width: int, height: int) -> list[tuple[int, int, int]]:
    """Return RGB tuples in row-major order."""

    _require_frame(data, width, height)
    pixels = []
    for index in range(0, len(data), 2):
        pixels.append(expand_rgb565_be(data[index], data[index + 1]))
    return pixels


def decode_bgr565_be(data: bytes, width: int, height: int) -> list[tuple[int, int, int]]:
    """Return BGR tuples in the same order OpenCV uses."""

    return [(blue, green, red) for red, green, blue in decode_rgb565_be(data, width, height)]


def bgr_array(data: bytes, width: int, height: int):
    """Return a contiguous HxWx3 BGR uint8 array. Requires NumPy."""

    import numpy as np

    _require_frame(data, width, height)
    raw = np.frombuffer(data, dtype=np.uint8)
    high = raw[0::2].astype(np.uint16)
    low = raw[1::2].astype(np.uint16)
    red = (high & 0xF8).astype(np.uint8)
    green = (((high & 0x07) << 5) | ((low & 0xE0) >> 3)).astype(np.uint8)
    blue = ((low & 0x1F) << 3).astype(np.uint8)
    image = np.dstack(
        (blue.reshape(height, width), green.reshape(height, width), red.reshape(height, width))
    )
    return np.ascontiguousarray(image)


def _require_frame(data: bytes, width: int, height: int) -> None:
    if width < 1 or height < 1 or len(data) != expected_raw_length(width, height):
        raise ValueError("raw_length")
