import hashlib
import io
import json
import sys
import tempfile
import unittest
import urllib.request
import urllib.error
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from inference import InferenceEngine
from frame_codec import decode_bgr
import test_still_and_recipe as helpers

class ControlIntegrityTests(unittest.TestCase):
    def test_artifact_contract_detects_changed_bytes_and_malformed_json(self):
        with tempfile.TemporaryDirectory() as folder:
            model = Path(folder) / 'model.onnx'
            model.write_bytes(b'original')
            sidecar = model.with_name(model.name + '.smartline.json')
            sidecar.write_text(json.dumps({'inputSize': 640, 'task': 'detect', 'modelVersion': '1', 'classNames': ['BOX_RED'], 'artifactSha256': hashlib.sha256(b'original').hexdigest()}))
            self.assertTrue(InferenceEngine(str(model)).contract_verified)
            model.write_bytes(b'changed')
            self.assertFalse(InferenceEngine(str(model)).contract_verified)
            sidecar.write_text('{')
            self.assertFalse(InferenceEngine(str(model)).contract_verified)

    def test_jpeg_fallback_decodes_pixels_for_the_model(self):
        from PIL import Image
        output = io.BytesIO()
        Image.new('RGB', (4, 2), (255, 0, 0)).save(output, format='JPEG')
        pixels = decode_bgr({'jpeg': output.getvalue()})
        self.assertEqual((2, 4, 3), pixels.shape)
        self.assertGreater(int(pixels[0, 0, 2]), 240)
        self.assertLess(int(pixels[0, 0, 0]), 10)

    def test_control_auth_body_limit_and_hardware_trigger(self):
        context, thread = helpers.FrameEndpointPolicyTests()._server(None, False)
        context['service_key'] = 'secret'
        base = f"http://127.0.0.1:{context['port']}"
        def rejected(path, data=None, key=None, length=None):
            headers = {'Content-Type': 'application/json'}
            if key: headers['X-Vision-Key'] = key
            if length: headers['Content-Length'] = str(length)
            request = urllib.request.Request(base + path, data=data, headers=headers)
            with self.assertRaises(urllib.error.HTTPError) as raised:
                urllib.request.urlopen(request)
            status = raised.exception.code
            raised.exception.close()
            return status
        try:
            self.assertEqual(401, rejected('/frames/preview'))
            self.assertEqual(400, rejected('/inspect', b'{', 'secret'))
            self.assertEqual(413, rejected('/inspect', b'{}', 'secret', 70000))
            self.assertEqual(400, rejected('/inspect', b'{"source":"hardware"}', 'secret'))
            self.assertEqual(400, rejected('/inspect', b'{"confidence":1}', 'secret'))
        finally:
            context['server'].shutdown()
            context['server'].server_close()
            thread.join(2)
