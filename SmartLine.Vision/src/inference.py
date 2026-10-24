"""Local YOLO26 detection.

A ``.pt`` checkpoint runs through Ultralytics when that package is installed.
ONNX files and OpenVINO directories run through Ultralytics when it is installed,
and otherwise through ONNX Runtime or OpenVINO using the same export contract.
A missing artifact returns no boxes and no invented latency.
"""

from __future__ import annotations

import importlib.util
import hashlib
import json
import time
from pathlib import Path

from yolo_decode import decode_detections, letterbox_meta


BACKENDS = ("openvino", "onnxruntime", "ultralytics")


def available_backends() -> dict[str, bool]:
    return {name: importlib.util.find_spec(name) is not None for name in BACKENDS}


def resolve_artifact(model_path: str | Path | None) -> Path | None:
    if not model_path:
        return None
    path = Path(model_path)
    if path.is_file():
        return path
    if path.is_dir():
        xml_files = sorted(path.glob("*.xml"))
        return xml_files[0] if xml_files else None
    return None


def artifact_format(model_path: str | Path | None) -> str | None:
    path = Path(model_path) if model_path else None
    artifact = resolve_artifact(model_path)
    if path is None or artifact is None:
        return None
    if path.is_dir() or artifact.suffix.lower() == ".xml":
        return "openvino"
    if artifact.suffix.lower() == ".onnx":
        return "onnx"
    if artifact.suffix.lower() == ".pt":
        return "pt"
    return None


def load_export_contract(model_path: str | Path | None) -> dict:
    """Read the sidecar written by ``training/export.py`` when it is present."""

    if not model_path:
        return {}
    path = Path(model_path)
    candidates = []
    if path.is_dir():
        candidates.append(path / "smartline.json")
    else:
        candidates.append(path.with_name(path.name + ".smartline.json"))
        candidates.append(path.with_suffix(".smartline.json"))
        candidates.append(path.parent / f"{path.stem}.smartline.json")
    for candidate in candidates:
        if candidate.is_file():
            loaded = json.loads(candidate.read_text(encoding="utf-8"))
            return loaded if isinstance(loaded, dict) else {}
    return {}


def artifact_digest(path: Path) -> str | None:
    digest = hashlib.sha256()
    if path.is_file():
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1_048_576), b""): digest.update(chunk)
        return digest.hexdigest()
    if not path.is_dir(): return None
    members = sorted(p for p in path.rglob("*") if p.is_file() and p.suffix.lower() in (".xml", ".bin"))
    if not members: return None
    for member in members:
        digest.update(member.relative_to(path).as_posix().encode())
        digest.update(b"\0")
        with member.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1_048_576), b""): digest.update(chunk)
    return digest.hexdigest()


def select_backend(
    model_path: str | None,
    preference: str | None = None,
    available: dict[str, bool] | None = None,
) -> str | None:
    kind = artifact_format(model_path)
    if kind is None:
        return None
    installed = available if available is not None else available_backends()
    order = {
        "pt": ["ultralytics"],
        "onnx": ["ultralytics", "onnxruntime"],
        "openvino": ["ultralytics", "openvino"],
    }[kind]
    if preference == "cpu":
        preference = "openvino" if kind == "openvino" else "onnxruntime"
    if preference in order:
        order = [preference, *[item for item in order if item != preference]]
    for name in order:
        if installed.get(name):
            return name
    return None


class InferenceEngine:
    """Loads a backend lazily. A missing model returns an empty, explicit result."""

    def __init__(
        self,
        model_path: str | None = None,
        preference: str | None = None,
        confidence: float = 0.25,
        class_names: list[str] | None = None,
        input_size: int = 640,
        layout: str | None = None,
        task: str = "detect",
        model_version: str | None = None,
        executor=None,
    ) -> None:
        self.model_path = model_path
        self.preference = preference
        self.confidence = float(confidence)
        self.class_names = class_names
        self.input_size = int(input_size)
        contract_error = None
        try:
            contract = {} if executor is not None else load_export_contract(model_path)
            if not isinstance(contract.get("inputSize", 640), int) or not 32 <= contract.get("inputSize", 640) <= 4096:
                raise ValueError("invalid input size")
            if "classNames" in contract and (not isinstance(contract["classNames"], list) or not all(isinstance(n, str) and n for n in contract["classNames"])):
                raise ValueError("invalid class names")
        except (OSError, ValueError, TypeError):
            contract = {}
            contract_error = "model_contract_invalid"
        self.layout = layout if layout is not None else contract.get("layout")
        self.task = contract.get("task", task)
        self.input_size = int(contract.get("inputSize", self.input_size))
        self.model_version = model_version or contract.get("modelVersion")
        if class_names is None and contract.get("classNames"):
            self.class_names = [str(name) for name in contract["classNames"]]
        try:
            self.contract_verified = bool(model_path and contract.get("artifactSha256") and artifact_digest(Path(model_path)) == contract["artifactSha256"] and self.model_version == contract.get("modelVersion"))
        except OSError:
            self.contract_verified = False
            contract_error = "model_artifact_unreadable"
        self._executor = executor
        self._model = None
        self._input_name: str | None = None
        self.last_latency_ms: float | None = None
        self.backend = "executor" if executor is not None else select_backend(model_path, preference)
        self.load_error: str | None = contract_error
        if executor is None and model_path and self.backend is None:
            self.load_error = "model_not_found" if resolve_artifact(model_path) is None else "backend_unavailable"

    def status(self) -> dict:
        artifact = resolve_artifact(self.model_path)
        return {
            "contractVerified": self.contract_verified,
            "configured": bool(self.model_path) or self._executor is not None,
            "loaded": self._model is not None or self._executor is not None,
            "backend": None if self.backend == "executor" else self.backend,
            "task": self.task,
            "modelVersion": self.model_version,
            "artifact": None if artifact is None else artifact.name,
            "artifactFormat": artifact_format(self.model_path),
            "lastInferenceLatencyMs": self.last_latency_ms,
            "loadError": self.load_error,
            "confidenceThreshold": self.confidence,
            "availableBackends": available_backends(),
            "error": self.load_error,
        }

    def predict(self, frame) -> dict:
        if self._executor is None and (self.backend is None or not self.model_path):
            return _empty(self.load_error or "model_not_loaded")
        if frame is None:
            return _empty("frame_missing")
        started = time.perf_counter()
        try:
            from frame_codec import decode_bgr
            detections = self._predict(decode_bgr(frame))
        except Exception as exc:  # noqa: BLE001 - report the runtime failure instead of hiding it
            self.load_error = str(exc)
            return _empty(self.load_error, self.backend)
        elapsed_ms = round((time.perf_counter() - started) * 1000, 3)
        self.last_latency_ms = elapsed_ms
        self.load_error = None
        return {
            "available": True,
            "backend": None if self.backend == "executor" else self.backend,
            "detections": detections,
            "latencyMs": elapsed_ms,
            "error": None,
        }

    def _predict(self, frame) -> list[dict]:
        if self._executor is not None:
            return self._predict_contract(frame, self._executor)
        if self.backend == "ultralytics":
            return _ultralytics(self, frame)
        if self.backend == "onnxruntime":
            return self._predict_contract(frame, lambda blob: _run_onnx(self, blob))
        if self.backend == "openvino":
            return self._predict_contract(frame, lambda blob: _run_openvino(self, blob))
        return []

    def _predict_contract(self, frame, runner) -> list[dict]:
        blob, meta = prepare_input(frame, self.input_size)
        raw = runner(blob)
        return decode_detections(
            raw,
            meta,
            self.confidence,
            self.class_names,
            self.layout,
        )


def prepare_input(frame, input_size: int) -> tuple[object, dict]:
    """Build network input and the letterbox map back to source pixels.

    Tests pass ``{"meta": ..., "blob": ...}`` so decoding does not need an image
    library. A real frame is a BGR array.
    """

    if isinstance(frame, dict) and "meta" in frame:
        return frame.get("blob"), frame["meta"]
    return _preprocess_bgr(frame, input_size)


def _preprocess_bgr(frame, input_size: int) -> tuple[object, dict]:
    import numpy as np

    array = np.asarray(frame)
    if array.ndim != 3 or array.shape[2] != 3:
        raise ValueError("A camera frame must be a color image.")
    source_height, source_width = int(array.shape[0]), int(array.shape[1])
    meta = letterbox_meta(source_width, source_height, input_size)
    try:
        import cv2

        resized = cv2.resize(array, (round(source_width * meta["scale"]), round(source_height * meta["scale"])))
        canvas = np.full((input_size, input_size, 3), 114, dtype=np.uint8)
        top, left = int(meta["padTop"]), int(meta["padLeft"])
        canvas[top : top + resized.shape[0], left : left + resized.shape[1]] = resized
        rgb = canvas[:, :, ::-1]
    except ImportError:
        from PIL import Image

        rgb_array = array[:, :, ::-1]
        image = Image.fromarray(rgb_array.astype("uint8"), "RGB")
        resized = image.resize((round(source_width * meta["scale"]), round(source_height * meta["scale"])))
        canvas = Image.new("RGB", (input_size, input_size), (114, 114, 114))
        canvas.paste(resized, (int(meta["padLeft"]), int(meta["padTop"])))
        rgb = np.asarray(canvas)
    blob = np.transpose(rgb.astype("float32") / 255.0, (2, 0, 1))[None]
    return blob, meta


def _empty(error: str, backend: str | None = None) -> dict:
    return {
        "available": False,
        "backend": None if backend in (None, "executor") else backend,
        "detections": [],
        "latencyMs": None,
        "error": error,
    }


def _ultralytics(engine: InferenceEngine, frame) -> list[dict]:
    if engine._model is None:
        from ultralytics import YOLO

        engine._model = YOLO(str(resolve_artifact(engine.model_path)))
    source = frame["image"] if isinstance(frame, dict) and "image" in frame else frame
    results = engine._model.predict(source, conf=engine.confidence, verbose=False)
    return _from_ultralytics(results, engine.confidence, engine.class_names)


def _run_onnx(engine: InferenceEngine, blob):
    if engine._model is None:
        import onnxruntime as ort

        engine._model = ort.InferenceSession(str(resolve_artifact(engine.model_path)), providers=["CPUExecutionProvider"])
        engine._input_name = engine._model.get_inputs()[0].name
    outputs = engine._model.run(None, {engine._input_name: blob})
    return outputs[0]


def _run_openvino(engine: InferenceEngine, blob):
    if engine._model is None:
        import openvino as ov

        core = ov.Core()
        model = core.read_model(str(resolve_artifact(engine.model_path)))
        engine._model = core.compile_model(model, "CPU")
    result = engine._model(blob)
    return result[engine._model.output(0)]


def _from_ultralytics(results, confidence: float, class_names: list[str] | None) -> list[dict]:
    detections = []
    for result in results:
        boxes = getattr(result, "boxes", None)
        if boxes is None:
            continue
        names = getattr(result, "names", {}) or {}
        for box in boxes:
            xyxy = box.xyxy[0].tolist()
            cls = int(box.cls[0])
            score = float(box.conf[0])
            if score < confidence:
                continue
            label = class_names[cls] if class_names is not None and 0 <= cls < len(class_names) else names.get(cls, str(cls))
            detections.append(
                {
                    "x": float(xyxy[0]),
                    "y": float(xyxy[1]),
                    "width": float(xyxy[2] - xyxy[0]),
                    "height": float(xyxy[3] - xyxy[1]),
                    "label": label,
                    "confidence": score,
                }
            )
    return detections
