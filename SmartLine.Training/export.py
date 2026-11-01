"""Export a real trained checkpoint to ONNX and OpenVINO.

The default Ultralytics export is the raw one-to-many detection head. Vision
decodes that layout, and also the NMS-free ``(batch, max_det, 6)`` layout when
the sidecar says ``layout`` is ``end2end``. Missing weights are an error. This
script does not create a placeholder model.
"""

from __future__ import annotations

import argparse
from validate_registry import artifact_sha256
import json
import sys
from pathlib import Path


def export_plan(weights: Path) -> dict:
    if not weights.is_file():
        raise FileNotFoundError(
            f"Checkpoint was not found: {weights}. Download the trained best.pt before exporting."
        )
    if weights.suffix.lower() != ".pt":
        raise ValueError("Export expects the Ultralytics best.pt checkpoint.")
    return {
        "weights": str(weights),
        "onnx": str(weights.with_suffix(".onnx")),
        "openvino": str(weights.with_suffix("")) + "_openvino_model/",
        "device": "cpu",
        "task": "detect",
        "layout": "raw",
        "nms": None,
        "executed": False,
    }


def write_contract(weights: Path, class_names: list[str] | None = None, input_size: int = 640, model_version: str | None = None) -> Path:
    contract = {
        "task": "detect",
        "layout": "raw",
        "nms": None,
        "inputSize": input_size,
        "modelVersion": model_version or weights.stem,
        "classNames": class_names or [],
        "onnx": str(weights.with_suffix(".onnx")),
        "openvino": str(weights.with_suffix("")) + "_openvino_model/",
    }
    onnx = weights.with_suffix(".onnx")
    contract["artifactSha256"] = artifact_sha256(onnx) if onnx.is_file() else None
    destination = weights.with_suffix(".smartline.json")
    destination.write_text(json.dumps(contract, indent=2) + "\n", encoding="utf-8")
    onnx.with_name(onnx.name + ".smartline.json").write_text(json.dumps(contract, indent=2) + "\n", encoding="utf-8")
    pt_contract = {**contract, "artifactSha256": artifact_sha256(weights)}
    weights.with_name(weights.name + ".smartline.json").write_text(json.dumps(pt_contract, indent=2) + "\n", encoding="utf-8")
    openvino = Path(contract["openvino"])
    if openvino.is_dir():
        ov_contract = {**contract, "artifactSha256": artifact_sha256(openvino)}
        (openvino / "smartline.json").write_text(json.dumps(ov_contract, indent=2) + "\n", encoding="utf-8")
    return destination


def execute(weights: Path, model_version: str | None = None) -> None:
    from ultralytics import YOLO

    model = YOLO(str(weights))
    model.export(format="onnx", dynamic=False)
    model.export(format="openvino")
    names = getattr(model, "names", None)
    class_names = [names[key] for key in sorted(names)] if isinstance(names, dict) else None
    write_contract(weights, class_names, model_version=model_version)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Export a trained SmartLine checkpoint.")
    parser.add_argument("--weights", required=True, type=Path)
    parser.add_argument("--model-version", help="Version recorded in the artifact sidecar; defaults to the checkpoint stem.")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args(argv)
    try:
        plan = export_plan(args.weights)
    except (FileNotFoundError, ValueError) as exc:
        print(str(exc))
        return 2
    print(f"ONNX target: {plan['onnx']}")
    print(f"OpenVINO target: {plan['openvino']}")
    if not args.execute:
        print("Plan only. Pass --execute after the checkpoint has been reviewed.")
        return 0
    execute(args.weights, args.model_version)
    print("Export returned. Confirm the ONNX and OpenVINO files on disk before registering the artifact.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
