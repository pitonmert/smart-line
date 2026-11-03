"""RGB565 byte order matches esp32-camera f0bb429, without NumPy."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "smartline" / "vision"))

from smartline.vision.rgb565 import (
    bgr_array,
    decode_bgr565_be,
    decode_rgb565_be,
    expand_rgb565_be,
    expected_raw_length,
)


class Rgb565Tests(unittest.TestCase):
    def test_big_endian_channel_expansion_matches_the_esp32_jpeg_converter(self):
        self.assertEqual((0xF8, 0, 0), expand_rgb565_be(0xF8, 0x00))
        self.assertEqual((0, 0xFC, 0), expand_rgb565_be(0x07, 0xE0))
        self.assertEqual((0, 0, 0xF8), expand_rgb565_be(0x00, 0x1F))
        swapped = expand_rgb565_be(0x00, 0xF8)
        self.assertNotEqual((0xF8, 0, 0), swapped)
        self.assertLess(swapped[0], swapped[2] or 1)

    def test_frame_length_and_bgr_order_do_not_swap_channels(self):
        frame = bytes([0xF8, 0x00, 0x07, 0xE0, 0x00, 0x1F, 0xFF, 0xFF])
        self.assertEqual(8, expected_raw_length(2, 2))
        self.assertEqual(len(frame), expected_raw_length(2, 2))
        rgb = decode_rgb565_be(frame, 2, 2)
        bgr = decode_bgr565_be(frame, 2, 2)
        self.assertEqual([(0xF8, 0, 0), (0, 0xFC, 0), (0, 0, 0xF8), (0xF8, 0xFC, 0xF8)], rgb)
        self.assertEqual([(0, 0, 0xF8), (0, 0xFC, 0), (0xF8, 0, 0), (0xF8, 0xFC, 0xF8)], bgr)
        red_bgr = bgr[0]
        self.assertEqual(0, red_bgr[0])
        self.assertEqual(0, red_bgr[1])
        self.assertEqual(0xF8, red_bgr[2])
        with self.assertRaises(ValueError):
            decode_rgb565_be(frame[:-1], 2, 2)
        with self.assertRaises(ValueError):
            decode_rgb565_be(b"", 0, 1)

    def test_numpy_bgr_array_matches_the_pure_expansion(self):
        try:
            import numpy as np
        except ImportError:
            self.skipTest("numpy is not required for the unit suite")
        frame = bytes([0xF8, 0x00, 0x00, 0x1F])
        image = bgr_array(frame, 2, 1)
        self.assertEqual((1, 2, 3), image.shape)
        self.assertEqual(np.uint8, image.dtype)
        self.assertEqual([0, 0, 0xF8], image[0, 0].tolist())
        self.assertEqual([0xF8, 0, 0], image[0, 1].tolist())
        self.assertTrue(image.flags["C_CONTIGUOUS"])


if __name__ == "__main__":
    unittest.main()
