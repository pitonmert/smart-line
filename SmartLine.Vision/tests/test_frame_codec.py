import io
import sys
import unittest
import urllib.error
import urllib.request
import json
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from frame_codec import decode_bgr, encode_jpeg
from still_capture import StillImage
import test_still_and_recipe as helpers


def image_bytes(format="JPEG"):
    output = io.BytesIO()
    Image.new("RGB", (4, 2), (220, 20, 20)).save(output, format=format)
    return output.getvalue()


class FrameCodecTests(unittest.TestCase):
    def test_existing_jpeg_is_decoded_without_reencoding_and_has_actual_dimensions(self):
        jpeg = image_bytes()
        for frame in (jpeg, bytearray(jpeg), {"jpeg": jpeg, "width": 999, "height": 999}):
            with self.subTest(frame_type=type(frame).__name__):
                payload, width, height = encode_jpeg(frame)
                self.assertEqual(jpeg, payload)
                self.assertEqual((4, 2), (width, height))

    def test_invalid_or_truncated_images_are_rejected(self):
        jpeg = image_bytes()
        for payload in (b"", b"\xff\xd8\xff\xd9", jpeg[:-1], jpeg[:100] + b"\xff\xd9", image_bytes("PNG")):
            for frame in (payload, {"jpeg": payload}):
                with self.subTest(length=len(payload), frame_type=type(frame).__name__):
                    with self.assertRaises(ValueError):
                        encode_jpeg(frame)
            with self.assertRaises(ValueError):
                decode_bgr({"jpeg": payload})

    def test_oversized_jpeg_is_rejected_before_pixels_are_loaded(self):
        jpeg = bytearray(image_bytes())
        marker = jpeg.index(b"\xff\xc0")
        jpeg[marker + 5:marker + 7] = (2001).to_bytes(2, "big")
        jpeg[marker + 7:marker + 9] = (8000).to_bytes(2, "big")
        with self.assertRaisesRegex(ValueError, "pixel limit"):
            encode_jpeg(bytes(jpeg))

    def test_array_encoding_preserves_dimensions_and_bgr_color(self):
        frame = np.zeros((2, 4, 3), dtype=np.uint8)
        frame[:, :, 2] = 255
        jpeg, width, height = encode_jpeg(frame)
        self.assertEqual((4, 2), (width, height))
        decoded = decode_bgr({"jpeg": jpeg})
        self.assertGreater(int(decoded[0, 0, 2]), 240)
        self.assertLess(int(decoded[0, 0, 0]), 10)

    def test_empty_and_oversized_arrays_are_rejected_without_encoding(self):
        for shape in ((0, 4, 3), (2, 0, 3), (2001, 8000, 3)):
            frame = np.broadcast_to(np.zeros((1, 1, 3), dtype=np.uint8), shape)
            with self.subTest(shape=shape), self.assertRaisesRegex(ValueError, "pixel limit"):
                encode_jpeg(frame)

    def test_dataset_endpoint_rejects_bad_jpeg_and_reports_actual_dimensions(self):
        context, thread = helpers.FrameEndpointPolicyTests()._server(None, False)
        url = f"http://127.0.0.1:{context['port']}/frames/dataset"
        try:
            for payload in (b"\xff\xd8\xff\xd9", image_bytes()):
                still = StillImage(payload, 999, 999, datetime.now(timezone.utc))
                with patch.object(context["stills"], "capture", return_value=still):
                    if len(payload) == 4:
                        with self.assertRaises(urllib.error.HTTPError) as raised:
                            urllib.request.urlopen(url)
                        with raised.exception as response:
                            self.assertEqual(409, response.code)
                            self.assertEqual("frame_encode_failed", json.load(response)["error"])
                    else:
                        with urllib.request.urlopen(url) as response:
                            self.assertEqual(payload, response.read())
                            self.assertEqual("4", response.headers["X-Frame-Width"])
                            self.assertEqual("2", response.headers["X-Frame-Height"])
        finally:
            context["server"].shutdown()
            context["server"].server_close()
            thread.join(2)
