"""Cloud training entry point.

This script refuses to train when the dataset has no label files. It does not
create metrics. Pass --execute only on a machine that has a real labeled dataset
and the pinned ultralytics environment, such as a Kaggle GPU notebook.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path


TASKS = {"detect"}
INT_FIELDS = ("epochs", "imgsz", "batch", "workers", "patience", "seed")
REQUIRED = ("model", "task", "dataset", "epochs", "imgsz", "batch", "device", "workers", "patience", "seed", "project", "name")


def load_config(path: Path) -> dict:
    if not path.is_file():
        raise FileNotFoundError(f"Training config was not found: {path}")
    values: dict[str, object] = {}
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        if ":" not in line:
            raise ValueError(f"{path}:{number} is not a key: value line.")
        key, value = (part.strip() for part in line.split(":", 1))
        if key in INT_FIELDS:
            try:
                values[key] = int(value)
            except ValueError as exc:
                raise ValueError(f"{path}:{number} {key} must be an integer.") from exc
        else:
            values[key] = value
    missing = [key for key in REQUIRED if key not in values]
    if missing:
        raise ValueError("Training config is missing: " + ", ".join(missing))
    if values["task"] not in TASKS:
        raise ValueError("Only detection training is supported in v0.2.0.")
    for key in INT_FIELDS:
        if int(values[key]) < 0:
            raise ValueError(f"{key} cannot be negative.")
    return values


def dataset_status(dataset: Path, task: str) -> tuple[bool, str]:
    if not dataset.is_dir():
        return False, f"Dataset directory does not exist: {dataset}"
    data_yaml = dataset / "data.yaml"
    if task != "classify" and not data_yaml.is_file():
        return False, "data.yaml is missing. Export a labeled YOLO dataset before training."
    labels = [path for path in dataset.rglob("*.txt") if path.name != "classes.txt"]
    if task == "classify":
        class_dirs = [path for path in dataset.iterdir() if path.is_dir() and any(path.glob("*"))]
        if not class_dirs:
            return False, "Classification dataset has no class directories."
        return True, f"{len(class_dirs)} class directories are present."
    if not labels:
        return False, "No label files were found. Unannotated images are not a training set, and no metric will be invented."
    nonempty = [path for path in labels if path.read_text(encoding="utf-8").strip()]
    if not nonempty:
        return False, "Label files are empty. Empty files are not treated as a trained dataset."
    return True, f"{len(nonempty)} non-empty label files are present."


def training_plan(config: dict) -> dict:
    ready, reason = dataset_status(Path(str(config["dataset"])), str(config["task"]))
    return {
        "ready": ready,
        "reason": reason,
        "model": config["model"],
        "task": config["task"],
        "dataset": config["dataset"],
        "epochs": config["epochs"],
        "imgsz": config["imgsz"],
        "batch": config["batch"],
        "device": config["device"],
        "workers": config["workers"],
        "patience": config["patience"],
        "seed": config["seed"],
        "project": config["project"],
        "name": config["name"],
        "executed": False,
        "metrics": None,
    }


def execute(config: dict) -> None:
    from ultralytics import YOLO

    model = YOLO(str(config["model"]))
    model.train(
        data=str(config["dataset"] if config["task"] == "classify" else Path(config["dataset"]) / "data.yaml"),
        epochs=int(config["epochs"]),
        imgsz=int(config["imgsz"]),
        batch=int(config["batch"]),
        device=str(config["device"]),
        workers=int(config["workers"]),
        patience=int(config["patience"]),
        seed=int(config["seed"]),
        project=str(config["project"]),
        name=str(config["name"]),
        exist_ok=False,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Prepare or run a SmartLine cloud training job.")
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--execute", action="store_true", help="Run ultralytics. Refused when labels are missing.")
    args = parser.parse_args(argv)
    plan = training_plan(load_config(args.config))
    print(
        f"model={plan['model']} task={plan['task']} dataset={plan['dataset']} "
        f"epochs={plan['epochs']} imgsz={plan['imgsz']} batch={plan['batch']} device={plan['device']}"
    )
    print(plan["reason"])
    if not plan["ready"]:
        print("Training was not started. No accuracy or mAP was produced.")
        return 2
    if not args.execute:
        print("Plan only. Pass --execute on the cloud GPU runner to train.")
        return 0
    execute(plan)
    print("Training returned. Read the run directory for the real metrics. This script does not summarize invented scores.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
