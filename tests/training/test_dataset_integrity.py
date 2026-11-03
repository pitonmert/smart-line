import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools" / "training"))
import dataset_tools
from test_dataset_and_plan import JPEG


class DatasetIntegrityTests(unittest.TestCase):
    def test_rejected_records_are_not_missing_or_counted(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            manifest = root / "manifest.jsonl"
            manifest.write_text(
                json.dumps({"fileName": "discarded.jpg", "rejected": True, "annotated": True})
            )
            result = dataset_tools.validate_dataset(manifest, root)
            self.assertEqual([], result["missing"])
            self.assertEqual(0, result["images"])
            self.assertEqual({}, result["classCounts"])
            self.assertEqual(
                [], dataset_tools.split_annotated([{"rejected": True, "annotated": True}])["test"]
            )

    def test_checksum_mismatch_fails_validation_and_export(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "a.jpg").write_bytes(JPEG)
            row = {"fileName": "a.jpg", "sha256": "0" * 64, "annotated": True, "boundingBoxes": []}
            manifest = root / "manifest.jsonl"
            manifest.write_text(json.dumps(row))
            self.assertEqual(
                ["a.jpg"], dataset_tools.validate_dataset(manifest, root)["inconsistent"]
            )
            self.assertEqual(
                2,
                dataset_tools.main(
                    [
                        "export-yolo",
                        "--manifest",
                        str(manifest),
                        "--images",
                        str(root),
                        "--output",
                        str(root / "out"),
                        "--classes",
                        "BOX_RED",
                    ]
                ),
            )

    def test_duplicate_hash_never_crosses_split_boundaries(self):
        rows = [{"fileName": str(i), "sha256": str(i // 2), "annotated": True} for i in range(20)]
        result = dataset_tools.split_annotated(rows)
        keys = [set(row["sha256"] for row in result[split]) for split in ("train", "val", "test")]
        self.assertFalse(keys[0] & keys[1] or keys[0] & keys[2] or keys[1] & keys[2])

    def test_non_finite_and_zero_boxes_are_rejected(self):
        for width in (float("nan"), float("inf"), 0, -1):
            with self.subTest(width=width), self.assertRaises(ValueError):
                dataset_tools.yolo_line(
                    {
                        "x": 0,
                        "y": 0,
                        "width": width,
                        "height": 2,
                        "sourceWidth": 10,
                        "sourceHeight": 10,
                    },
                    0,
                )


class ExportContractTests(unittest.TestCase):
    def test_export_sidecar_binds_actual_bytes_classes_and_version(self):
        import hashlib
        import export

        with tempfile.TemporaryDirectory() as folder:
            weights = Path(folder) / "best.pt"
            weights.write_bytes(b"checkpoint")
            onnx = weights.with_suffix(".onnx")
            onnx.write_bytes(b"exported-onnx")
            export.write_contract(weights, ["BOX_RED"], model_version="demobox-1")
            contract = json.loads(onnx.with_name("best.onnx.smartline.json").read_text())
            self.assertEqual(["BOX_RED"], contract["classNames"])
            self.assertEqual("demobox-1", contract["modelVersion"])
            self.assertEqual(
                hashlib.sha256(b"exported-onnx").hexdigest(), contract["artifactSha256"]
            )
            pt = json.loads(weights.with_name("best.pt.smartline.json").read_text())
            self.assertEqual(hashlib.sha256(b"checkpoint").hexdigest(), pt["artifactSha256"])
