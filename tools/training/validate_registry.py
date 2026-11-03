"""Check a model registry record against the artifact it names.

An untrained record is schema-valid and is not activatable. The tool does not
download weights and does not fill in metrics. A directory digest covers the
sorted relative paths and bytes of ``*.xml`` and ``*.bin`` files only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

SUPPORTED = ("pt", "onnx", "openvino")
REQUIRED = (
    "recipeId",
    "modelName",
    "modelVersion",
    "task",
    "artifactType",
    "artifactPath",
    "sha256",
    "classNames",
    "trainedAt",
    "trainingRunId",
    "metrics",
    "inputSize",
    "confidenceThreshold",
    "exportFormat",
)


def artifact_sha256(path: Path) -> str:
    """Hash one file, or the xml/bin members of an OpenVINO directory."""

    digest = hashlib.sha256()
    if path.is_file():
        digest.update(path.read_bytes())
        return digest.hexdigest()
    members = sorted(
        [
            item
            for item in path.rglob("*")
            if item.is_file() and item.suffix.lower() in {".xml", ".bin"}
        ],
        key=lambda item: item.relative_to(path).as_posix(),
    )
    if not members:
        raise FileNotFoundError(f"No OpenVINO xml/bin files were found in {path}.")
    for item in members:
        relative = item.relative_to(path).as_posix().encode("utf-8")
        digest.update(relative)
        digest.update(b"\0")
        digest.update(item.read_bytes())
    return digest.hexdigest()


def detect_format(path: Path) -> str | None:
    if path.is_dir():
        return (
            "openvino"
            if any(item.suffix.lower() == ".xml" for item in path.glob("*.xml"))
            else None
        )
    return {"pt": "pt", "onnx": "onnx", "xml": "openvino"}.get(path.suffix.lower().lstrip("."))


def schema_errors(document: object) -> list[str]:
    if not isinstance(document, dict):
        return ["Registry must be a JSON object."]
    errors: list[str] = []
    for key in REQUIRED:
        if key not in document:
            errors.append(f"Missing {key}.")
    if errors:
        return errors
    if not isinstance(document["modelName"], str) or not document["modelName"].strip():
        errors.append("modelName must be a non-empty string.")
    if not isinstance(document["modelVersion"], str) or not document["modelVersion"].strip():
        errors.append("modelVersion must be a non-empty string.")
    if document["task"] not in {"detect", "classify", "segment"}:
        errors.append("task must be detect, classify, or segment.")
    if not isinstance(document["classNames"], list) or not all(
        isinstance(item, str) and item for item in document["classNames"]
    ):
        errors.append("classNames must be a list of class codes.")
    if (
        not isinstance(document["inputSize"], int)
        or isinstance(document["inputSize"], bool)
        or document["inputSize"] <= 0
    ):
        errors.append("inputSize must be a positive integer.")
    threshold = document["confidenceThreshold"]
    if (
        isinstance(threshold, bool)
        or not isinstance(threshold, (int, float))
        or not 0 <= float(threshold) <= 1
    ):
        errors.append("confidenceThreshold must be between 0 and 1.")
    if document["metrics"] is not None and not isinstance(document["metrics"], dict):
        errors.append("metrics must be an object or null.")
    for key in ("recipeId", "trainedAt", "trainingRunId"):
        if document[key] is not None and not isinstance(document[key], str):
            errors.append(f"{key} must be a string or null.")
    for key in ("artifactType", "exportFormat"):
        if document[key] is not None and document[key] not in SUPPORTED:
            errors.append(f"{key} must be pt, onnx, openvino, or null.")
    if document["artifactPath"] is not None and (
        not isinstance(document["artifactPath"], str) or not document["artifactPath"].strip()
    ):
        errors.append("artifactPath must be a path or null.")
    sha = document["sha256"]
    if sha is not None and (
        not isinstance(sha, str)
        or len(sha) != 64
        or any(char not in "0123456789abcdef" for char in sha.lower())
    ):
        errors.append("sha256 must be 64 hexadecimal characters or null.")
    registered = document["artifactPath"] is not None
    if not registered and any(
        document[key] is not None for key in ("sha256", "artifactType", "exportFormat")
    ):
        errors.append(
            "An unregistered artifact must leave sha256, artifactType, and exportFormat null."
        )
    if registered and document["sha256"] is None:
        errors.append("A registered artifact requires sha256.")
    return errors


def recipe_classes(recipe: dict) -> list[str]:
    classes = recipe.get("productClasses") or []
    return [
        str(item.get("code")) for item in classes if isinstance(item, dict) and item.get("code")
    ]


def validate_registry(document: dict, root: Path, recipe: dict | None = None) -> dict:
    errors = schema_errors(document)
    if errors:
        return {
            "schemaValid": False,
            "activatable": False,
            "code": "schema_invalid",
            "errors": errors,
        }
    if document["artifactPath"] is None:
        errors.extend(_class_errors(document, recipe))
        return {
            "schemaValid": True,
            "activatable": False,
            "code": "class_mismatch" if errors else "artifact_not_registered",
            "errors": errors or ["No artifact is registered. The record cannot be activated."],
        }

    path = Path(document["artifactPath"])
    if not path.is_absolute():
        path = root / path
    if not path.exists():
        return {
            "schemaValid": True,
            "activatable": False,
            "code": "artifact_missing",
            "errors": [f"Artifact was not found: {path}"],
        }
    detected = detect_format(path)
    if detected is None:
        return {
            "schemaValid": True,
            "activatable": False,
            "code": "format_unsupported",
            "errors": [f"Unsupported artifact format: {path}"],
        }
    for key in ("artifactType", "exportFormat"):
        if document[key] not in (None, detected):
            errors.append(f"{key} is {document[key]} but the artifact is {detected}.")
    try:
        actual = artifact_sha256(path)
    except FileNotFoundError as exc:
        return {
            "schemaValid": True,
            "activatable": False,
            "code": "format_unsupported",
            "errors": [str(exc)],
        }
    if document["sha256"].lower() != actual:
        errors.append("sha256 does not match the artifact bytes.")
    errors.extend(_class_errors(document, recipe))
    if errors:
        code = (
            "checksum_mismatch"
            if any(item.startswith("sha256") for item in errors)
            else "class_mismatch"
            if any("class" in item.lower() for item in errors)
            else "format_unsupported"
        )
        return {"schemaValid": True, "activatable": False, "code": code, "errors": errors}
    return {"schemaValid": True, "activatable": True, "code": "ok", "errors": []}


def _class_errors(document: dict, recipe: dict | None) -> list[str]:
    if recipe is None:
        return []
    expected = recipe_classes(recipe)
    if expected and list(document["classNames"]) != expected:
        return [f"classNames {document['classNames']} do not match the recipe classes {expected}."]
    recipe_id = recipe.get("id")
    if recipe_id and document.get("recipeId") and document["recipeId"] != recipe_id:
        return [f"recipeId {document['recipeId']} does not match the recipe id {recipe_id}."]
    return []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate a SmartLine model registry record.")
    parser.add_argument("--registry", type=Path)
    parser.add_argument("--recipe", type=Path)
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--print-sha256", type=Path)
    args = parser.parse_args(argv)
    if args.print_sha256 is not None:
        if not args.print_sha256.exists():
            print(f"Artifact was not found: {args.print_sha256}")
            return 2
        print(artifact_sha256(args.print_sha256))
        return 0
    if args.registry is None:
        print("Pass --registry or --print-sha256.")
        return 2
    try:
        document = json.loads(args.registry.read_text(encoding="utf-8"))
        recipe = json.loads(args.recipe.read_text(encoding="utf-8")) if args.recipe else None
    except (OSError, json.JSONDecodeError) as exc:
        print(str(exc))
        return 2
    result = validate_registry(document, args.root, recipe)
    print(json.dumps(result, indent=2))
    return 0 if result["activatable"] else 2


if __name__ == "__main__":
    sys.exit(main())
