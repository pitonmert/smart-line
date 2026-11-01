import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import dataset_tools
import evaluate
import export
import train


def tiny_jpeg() -> bytes:
    try:
        from PIL import Image
    except ModuleNotFoundError:
        return b"\xff\xd8\xff\xd9"
    buffer = io.BytesIO()
    Image.new("RGB", (1, 1), (220, 20, 20)).save(buffer, format="JPEG")
    return buffer.getvalue()


JPEG = tiny_jpeg()


class DatasetToolsTests(unittest.TestCase):
    def test_manifest_validation_duplicates_and_unannotated_boxes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            images = root / "images"
            images.mkdir()
            (images / "a.jpg").write_bytes(JPEG)
            (images / "b.jpg").write_bytes(JPEG)
            (images / "bad.jpg").write_bytes(b"not-an-image")
            rows = [
                {"fileName": "a.jpg", "productClass": "BOX_RED", "annotated": False, "boundingBoxes": []},
                {"fileName": "b.jpg", "productClass": "BOX_BLUE", "annotated": False, "boundingBoxes": [{"x": 1}]},
                {"fileName": "bad.jpg", "productClass": "BOX_RED", "annotated": False, "boundingBoxes": []},
                {"fileName": "missing.jpg", "productClass": "BOX_GREEN", "annotated": False, "boundingBoxes": []},
            ]
            manifest = root / "manifest.jsonl"
            manifest.write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            report = dataset_tools.validate_dataset(manifest, images)
            self.assertEqual(["b.jpg"], report["duplicates"] and [report["duplicates"][0]["fileName"]])
            self.assertIn("bad.jpg", report["corrupt"])
            self.assertIn("missing.jpg", report["missing"])
            self.assertIn("b.jpg", report["inconsistent"])
            self.assertIn("a.jpg", report["unlabeled"])
            self.assertEqual(0, report["annotated"])

    def test_imbalance_threshold(self):
        self.assertFalse(dataset_tools.class_imbalance({"BOX_RED": 4, "BOX_BLUE": 2})["severe"])
        self.assertTrue(dataset_tools.class_imbalance({"BOX_RED": 10, "BOX_BLUE": 1})["severe"])
        self.assertFalse(dataset_tools.class_imbalance({"BOX_RED": 3})["severe"])

    def test_export_skips_unannotated_and_writes_real_boxes_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            images = root / "images"
            images.mkdir()
            (images / "kept.jpg").write_bytes(JPEG)
            (images / "raw.jpg").write_bytes(JPEG + b"\x00")
            rows = [
                {
                    "fileName": "kept.jpg",
                    "productClass": "BOX_RED",
                    "annotated": True,
                    "boundingBoxes": [{"x": 10, "y": 10, "width": 20, "height": 20, "label": "BOX_RED", "sourceWidth": 240, "sourceHeight": 176}],
                },
                {"fileName": "raw.jpg", "productClass": "BOX_BLUE", "annotated": False, "boundingBoxes": []},
            ]
            report = dataset_tools.export_yolo(rows, images, root / "yolo", ["BOX_RED", "BOX_BLUE"], seed=1)
            self.assertEqual(1, report["skippedUnannotated"])
            self.assertEqual(0, report["inventedLabels"])
            label = (root / "yolo" / "labels").rglob("kept.txt")
            text = next(label).read_text(encoding="utf-8").strip()
            self.assertTrue(text.startswith("0 "))
            self.assertFalse((root / "yolo" / "labels").joinpath("raw.txt").exists())
            yaml = (root / "yolo" / "data.yaml").read_text(encoding="utf-8")
            self.assertIn("0: BOX_RED", yaml)
            checked = dataset_tools.validate_label_files(root / "yolo", 2)
            self.assertEqual([], checked["problems"])

    def test_split_is_deterministic_and_ignores_unannotated(self):
        rows = [{"fileName": f"{index}.jpg", "annotated": True} for index in range(10)]
        rows.append({"fileName": "raw.jpg", "annotated": False})
        first = dataset_tools.split_annotated(rows, seed=7)
        second = dataset_tools.split_annotated(rows, seed=7)
        self.assertEqual([row["fileName"] for row in first["train"]], [row["fileName"] for row in second["train"]])
        self.assertEqual(1, first["skippedUnannotated"])
        self.assertEqual(10, len(first["train"]) + len(first["val"]) + len(first["test"]))


class TrainingPlanTests(unittest.TestCase):
    def test_missing_dataset_does_not_invent_metrics(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "detect.yaml"
            config.write_text(Path("SmartLine.Training/configs/detect.yaml").read_text(encoding="utf-8").replace("datasets/yolo/demobox", str(Path(temp) / "missing")), encoding="utf-8")
            plan = train.training_plan(train.load_config(config))
            self.assertFalse(plan["ready"])
            self.assertIsNone(plan["metrics"])
            self.assertEqual(2, train.main(["--config", str(config)]))

    def test_evaluate_and_export_refuse_missing_artifacts(self):
        self.assertEqual(2, evaluate.main(["--results", "/tmp/smartline-no-results.csv"]))
        self.assertEqual(2, export.main(["--weights", "/tmp/smartline-no-best.pt"]))

    def test_evaluate_reads_only_existing_rows(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "results.csv"
            path.write_text("epoch,metrics/mAP50(B)\n1,0.25\n", encoding="utf-8")
            report = evaluate.read_results(path)
            self.assertEqual("0.25", report["last"]["metrics/mAP50(B)"])
            self.assertEqual(1, report["rows"])


if __name__ == "__main__":
    unittest.main()
