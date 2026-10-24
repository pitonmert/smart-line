"""Validate captured datasets and export only images that are actually annotated.

Unannotated captures stay in the manifest. This module never writes a label file
for them and never invents a bounding box.
"""

from __future__ import annotations

import argparse
import hashlib
import math
import json
import random
import shutil
import sys
from pathlib import Path


JPEG_SOI = b"\xff\xd8"
JPEG_EOI = b"\xff\xd9"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
IMBALANCE_RATIO = 5.0


def image_kind(data: bytes) -> str | None:
    if len(data) >= 4 and data.startswith(JPEG_SOI) and JPEG_EOI in data[2:]:
        return "jpeg"
    if len(data) >= 24 and data.startswith(PNG_SIGNATURE):
        return "png"
    return None


def decode_error(data: bytes) -> str | None:
    try:
        from PIL import Image, UnidentifiedImageError
    except ModuleNotFoundError:
        return "Pillow is required to validate image content"
    import io

    try:
        with Image.open(io.BytesIO(data)) as image:
            image.verify()
    except (UnidentifiedImageError, OSError, ValueError, SyntaxError) as exc:
        return str(exc)
    return None


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_manifest(path: Path) -> list[dict]:
    if not path.is_file():
        raise FileNotFoundError(f"Manifest was not found: {path}")
    rows = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        text = line.strip()
        if not text:
            continue
        try:
            row = json.loads(text)
        except json.JSONDecodeError as exc:
            raise ValueError(f"Manifest line {number} is not JSON.") from exc
        if not isinstance(row, dict):
            raise ValueError(f"Manifest line {number} must be an object.")
        rows.append(row)
    return rows


def class_distribution(rows: list[dict]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for row in rows:
        if row.get("rejected") is True: continue
        label = str(row.get("productClass") or "").strip() or "(unlabeled)"
        counts[label] = counts.get(label, 0) + 1
    return counts


def class_imbalance(counts: dict[str, int]) -> dict:
    labeled = {name: count for name, count in counts.items() if name != "(unlabeled)"}
    if len(labeled) < 2:
        return {"severe": False, "ratio": None, "reason": "fewer than two labeled classes"}
    values = list(labeled.values())
    if min(values) == 0:
        return {"severe": True, "ratio": None, "reason": "a labeled class has zero samples"}
    ratio = max(values) / min(values)
    severe = ratio >= IMBALANCE_RATIO
    return {
        "severe": severe,
        "ratio": round(ratio, 3),
        "reason": "max/min class count is at least 5" if severe else "class counts are within the imbalance threshold",
    }


def validate_dataset(manifest: Path, image_root: Path) -> dict:
    rows = [row for row in load_manifest(manifest) if row.get("rejected") is not True]
    corrupt: list[str] = []
    missing: list[str] = []
    duplicates: list[dict] = []
    unlabeled: list[str] = []
    annotated: list[str] = []
    inconsistent: list[str] = []
    seen: dict[str, str] = {}
    for row in rows:
        name = Path(str(row.get("fileName") or "")).name
        if not name:
            missing.append("")
            continue
        file = image_root / name
        if not file.is_file():
            missing.append(name)
            continue
        data = file.read_bytes()
        kind = image_kind(data)
        pillow_error = decode_error(data) if kind else "file is not a JPEG or PNG"
        if kind is None or pillow_error:
            corrupt.append(name)
            continue
        digest = sha256(data)
        if row.get("sha256") and row["sha256"].lower() != digest:
            inconsistent.append(name)
        if digest in seen:
            duplicates.append({"fileName": name, "matches": seen[digest], "sha256": digest})
        else:
            seen[digest] = name
        boxes = row.get("boundingBoxes") or []
        if row.get("annotated") is True:
            annotated.append(name)
            if not isinstance(boxes, list):
                inconsistent.append(name)
        else:
            unlabeled.append(name)
            if boxes:
                inconsistent.append(name)
    counts = class_distribution(rows)
    return {
        "images": len(rows),
        "annotated": len(annotated),
        "unlabeled": unlabeled,
        "corrupt": corrupt,
        "missing": missing,
        "duplicates": duplicates,
        "inconsistent": inconsistent,
        "classCounts": counts,
        "imbalance": class_imbalance(counts),
        "labelPolicy": "Only records with annotated=true are labeled. Bounding boxes are never inferred.",
    }


def split_annotated(rows: list[dict], seed: int = 42, train: float = 0.7, val: float = 0.2, test: float = 0.1) -> dict[str, list[dict]]:
    if any(not math.isfinite(v) or v < 0 or v > 1 for v in (train, val, test)) or abs((train + val + test) - 1) > 1e-9:
        raise ValueError("Train, validation, and test fractions must sum to 1.")
    active = [row for row in rows if row.get("rejected") is not True]
    labeled = [row for row in active if row.get("annotated") is True]
    groups = {}
    for row in sorted(labeled, key=lambda row: str(row.get("fileName") or "")):
        key = row.get("sha256") or row.get("fileName")
        groups.setdefault(key, []).append(row)
    ordered = list(groups.values())
    random.Random(seed).shuffle(ordered)
    count = len(ordered)
    train_end = int(count * train)
    val_end = train_end + int(count * val)
    return {
        "train": [row for group in ordered[:train_end] for row in group],
        "val": [row for group in ordered[train_end:val_end] for row in group],
        "test": [row for group in ordered[val_end:] for row in group],
        "skippedUnannotated": len(active) - len(labeled),
    }


def yolo_line(box: dict, class_index: int) -> str:
    source_w = box.get("sourceWidth")
    source_h = box.get("sourceHeight")
    if not source_w or not source_h:
        raise ValueError("A bounding box needs sourceWidth and sourceHeight before it can be normalized.")
    x = float(box["x"])
    y = float(box["y"])
    width = float(box["width"])
    height = float(box["height"])
    if not all(math.isfinite(float(v)) for v in (x, y, width, height, source_w, source_h)) or width <= 0 or height <= 0 or float(source_w) <= 0 or float(source_h) <= 0 or min(x, y, width, height) < 0 or x + width > float(source_w) + 1 or y + height > float(source_h) + 1:
        raise ValueError("Bounding box falls outside the source frame.")
    cx = (x + width / 2) / float(source_w)
    cy = (y + height / 2) / float(source_h)
    return f"{class_index} {cx:.6f} {cy:.6f} {width / float(source_w):.6f} {height / float(source_h):.6f}"


def export_yolo(rows: list[dict], image_root: Path, output: Path, class_names: list[str], seed: int = 42) -> dict:
    if not class_names:
        raise ValueError("YOLO export needs the class names from the Product Recipe.")
    checked = []
    rejected = []
    for row in rows:
        if row.get("rejected") is True: continue
        if row.get("annotated") is not True:
            checked.append(row)
            continue
        name = Path(str(row.get("fileName") or "")).name
        source = image_root / name
        if not name or not source.is_file():
            rejected.append({"fileName": name, "reason": "image file is missing"})
            continue
        data = source.read_bytes()
        digest = sha256(data)
        if decode_error(data) or not image_kind(data) or (row.get("sha256") and row["sha256"].lower() != digest):
            rejected.append({"fileName": name, "reason": "invalid image or checksum mismatch"})
            continue
        checked.append({**row, "sha256": digest})
    splits = split_annotated(checked, seed=seed)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Export directory must be empty to prevent stale labels.")
    output.mkdir(parents=True, exist_ok=True)
    written = {"train": 0, "val": 0, "test": 0}
    for split, items in splits.items():
        if split == "skippedUnannotated":
            continue
        image_dir = output / "images" / split
        label_dir = output / "labels" / split
        image_dir.mkdir(parents=True, exist_ok=True)
        label_dir.mkdir(parents=True, exist_ok=True)
        for row in items:
            name = Path(str(row.get("fileName") or "")).name
            source = image_root / name
            if not name or not source.is_file():
                rejected.append({"fileName": name, "reason": "image file is missing"})
                continue
            lines = []
            failed = False
            for box in row.get("boundingBoxes") or []:
                label = str(box.get("label") or row.get("productClass") or "")
                if label not in class_names:
                    rejected.append({"fileName": name, "reason": f"label {label or '(empty)'} is not in the class list"})
                    failed = True
                    break
                try:
                    lines.append(yolo_line(box, class_names.index(label)))
                except (KeyError, TypeError, ValueError) as exc:
                    rejected.append({"fileName": name, "reason": str(exc)})
                    failed = True
                    break
            if failed:
                continue
            shutil.copyfile(source, image_dir / name)
            (label_dir / f"{Path(name).stem}.txt").write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
            written[split] += 1
    names = "\n".join(f"  {index}: {name}" for index, name in enumerate(class_names))
    (output / "data.yaml").write_text(
        "path: .\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n" + names + "\n",
        encoding="utf-8",
    )
    return {
        "written": written,
        "skippedUnannotated": splits["skippedUnannotated"],
        "rejected": rejected,
        "dataYaml": str(output / "data.yaml"),
        "inventedLabels": 0,
    }


def validate_label_files(dataset: Path, class_count: int) -> dict:
    problems = []
    files = sorted((dataset / "labels").rglob("*.txt")) if (dataset / "labels").is_dir() else []
    for file in files:
        for number, line in enumerate(file.read_text(encoding="utf-8").splitlines(), start=1):
            if not line.strip():
                continue
            parts = line.split()
            if len(parts) != 5:
                problems.append(f"{file}:{number} does not have 5 fields")
                continue
            try:
                cls = int(parts[0])
                values = [float(part) for part in parts[1:]]
            except ValueError:
                problems.append(f"{file}:{number} is not numeric")
                continue
            if cls < 0 or cls >= class_count:
                problems.append(f"{file}:{number} class index is outside the recipe")
            if any(not math.isfinite(value) or value < 0 or value > 1 for value in values):
                problems.append(f"{file}:{number} normalized geometry is outside 0..1")
    return {"labelFiles": len(files), "problems": problems}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate a SmartLine dataset manifest and export real labels only.")
    sub = parser.add_subparsers(dest="command", required=True)
    validate = sub.add_parser("validate")
    validate.add_argument("--manifest", required=True, type=Path)
    validate.add_argument("--images", required=True, type=Path)
    export = sub.add_parser("export-yolo")
    export.add_argument("--manifest", required=True, type=Path)
    export.add_argument("--images", required=True, type=Path)
    export.add_argument("--output", required=True, type=Path)
    export.add_argument("--classes", required=True, help="Comma-separated class names")
    export.add_argument("--seed", type=int, default=42)
    args = parser.parse_args(argv)
    if args.command == "validate":
        report = validate_dataset(args.manifest, args.images)
        print(json.dumps(report, indent=2))
        failed = report["corrupt"] or report["missing"] or report["inconsistent"]
        return 2 if failed else 0
    report = export_yolo(
        load_manifest(args.manifest),
        args.images,
        args.output,
        [item.strip() for item in args.classes.split(",") if item.strip()],
        args.seed,
    )
    print(json.dumps(report, indent=2))
    return 2 if report["rejected"] else 0


if __name__ == "__main__":
    sys.exit(main())
