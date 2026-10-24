import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import validate_registry


ROOT = Path(__file__).resolve().parents[2]


def registry(**overrides) -> dict:
    document = {
        "recipeId": None,
        "modelName": "yolo26n",
        "modelVersion": "demobox-1",
        "task": "detect",
        "artifactType": "onnx",
        "artifactPath": "model.onnx",
        "sha256": "a" * 64,
        "classNames": ["BOX_RED", "BOX_BLUE", "BOX_GREEN"],
        "trainedAt": "2026-10-06T00:00:00Z",
        "trainingRunId": "run-1",
        "metrics": {"mAP50": 0.5},
        "inputSize": 640,
        "confidenceThreshold": 0.25,
        "exportFormat": "onnx",
    }
    document.update(overrides)
    return document


class RegistryValidationTests(unittest.TestCase):
    def test_committed_demobox_record_is_schema_valid_and_not_activatable(self):
        document = json.loads((ROOT / "models" / "registry" / "demobox.json").read_text(encoding="utf-8"))
        recipe = json.loads((ROOT / "SmartLine.API" / "Contracts" / "recipes" / "demobox.json").read_text(encoding="utf-8"))
        result = validate_registry.validate_registry(document, ROOT, recipe)
        self.assertTrue(result["schemaValid"])
        self.assertFalse(result["activatable"])
        self.assertEqual("artifact_not_registered", result["code"])
        self.assertEqual(2, validate_registry.main(["--registry", str(ROOT / "models" / "registry" / "demobox.json"), "--recipe", str(ROOT / "SmartLine.API" / "Contracts" / "recipes" / "demobox.json"), "--root", str(ROOT)]))

    def test_onnx_checksum_and_recipe_classes_must_match(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            artifact = root / "model.onnx"
            artifact.write_bytes(b"onnx-bytes")
            digest = hashlib.sha256(b"onnx-bytes").hexdigest()
            recipe = {"productClasses": [{"code": "BOX_RED"}, {"code": "BOX_BLUE"}, {"code": "BOX_GREEN"}]}
            ready = validate_registry.validate_registry(registry(sha256=digest), root, recipe)
            self.assertTrue(ready["activatable"])
            self.assertEqual("ok", ready["code"])
            mismatch = validate_registry.validate_registry(registry(sha256="b" * 64), root, recipe)
            self.assertEqual("checksum_mismatch", mismatch["code"])
            classes = validate_registry.validate_registry(registry(sha256=digest, classNames=["BOX_RED"]), root, recipe)
            self.assertEqual("class_mismatch", classes["code"])

    def test_missing_and_unsupported_artifacts_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            missing = validate_registry.validate_registry(registry(artifactPath="missing.onnx"), root, None)
            self.assertEqual("artifact_missing", missing["code"])
            text = root / "notes.txt"
            text.write_bytes(b"nope")
            unsupported = validate_registry.validate_registry(
                registry(artifactPath="notes.txt", artifactType="pt", exportFormat="pt", sha256=hashlib.sha256(b"nope").hexdigest()),
                root,
                None,
            )
            self.assertEqual("format_unsupported", unsupported["code"])

    def test_openvino_directory_digest_uses_sorted_xml_and_bin_only(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "best_openvino_model"
            directory.mkdir()
            (directory / "best.xml").write_bytes(b"<net/>")
            (directory / "best.bin").write_bytes(b"123")
            (directory / "notes.txt").write_bytes(b"ignore")
            expected = hashlib.sha256()
            expected.update(b"best.bin\0")
            expected.update(b"123")
            expected.update(b"best.xml\0")
            expected.update(b"<net/>")
            self.assertEqual(expected.hexdigest(), validate_registry.artifact_sha256(directory))
            document = registry(
                artifactType="openvino",
                exportFormat="openvino",
                artifactPath=directory.name,
                sha256=expected.hexdigest(),
            )
            result = validate_registry.validate_registry(document, Path(temp), {"productClasses": [{"code": "BOX_RED"}, {"code": "BOX_BLUE"}, {"code": "BOX_GREEN"}]})
            self.assertTrue(result["activatable"])
            self.assertEqual(0, validate_registry.main(["--print-sha256", str(directory)]))


if __name__ == "__main__":
    unittest.main()
