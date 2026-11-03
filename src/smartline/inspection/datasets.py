"""Single dataset writer with database/manifest rollback and restart recovery."""

import hashlib
import json
import os
import shutil
import threading
import uuid
from collections import Counter
from contextlib import contextmanager
from pathlib import Path

from django.conf import settings
from django.utils import timezone

from smartline.web.errors import ApiError, missing
from smartline.web.serialization import Encoder, fields, json_data
from .decision import normalize
from .models import DatasetSession, ProductRecipe
from .recipes import required
from .transactions import inspection_transaction

write_lock = threading.RLock()


def atomic_write(path, body):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_bytes(body if isinstance(body, bytes) else body.encode("utf-8"))
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def root():
    directory = Path(settings.DATASET_ROOT).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    return directory


def directory(session):
    expected = root() / session.id.hex
    actual = Path(session.storage_path).resolve()
    if actual != expected:
        raise ApiError("Dataset storage path is invalid.", "DATASET_INVALID", 503)
    return actual


def entries(session):
    manifest = directory(session) / "manifest.jsonl"
    if not manifest.exists():
        return []
    return [json.loads(line) for line in manifest.read_text().splitlines() if line.strip()]


def manifest_write(session, rows):
    atomic_write(
        directory(session) / "manifest.jsonl",
        "".join(json.dumps(r, cls=Encoder, allow_nan=False) + "\n" for r in rows),
    )


def response(session, rows=None):
    rows = entries(session) if rows is None else rows
    active = [r for r in rows if not r.get("rejected", False)]
    result = fields(
        session,
        "recipe_id recipe_name status product_class quality_label color_label size_label defect_labels notes capture_mode interval_ms started_at stopped_at",
        sessionId=str(session.id),
        simulation=session.is_simulation,
        imageCount=len(active),
        classCounts=dict(Counter(r.get("productClass") or "(unlabeled)" for r in active)),
        qualityCounts=dict(Counter(r.get("qualityLabel") or "(unlabeled)" for r in active)),
        defectCounts=dict(Counter(d for r in active for d in r.get("defectLabels") or ["(none)"])),
        labelStatus=("class-labeled" if session.product_class else "unlabeled")
        + "; bounding boxes are not inferred",
    )
    result["recentSamples"] = [
        {
            **{
                k: r.get(k)
                for k in [
                    "sampleId",
                    "capturedAt",
                    "productClass",
                    "qualityLabel",
                    "colorLabel",
                    "sizeLabel",
                    "defectLabels",
                    "notes",
                    "trigger",
                    "sourceWidth",
                    "sourceHeight",
                    "previewFallback",
                ]
            },
            "fileName": Path(r["fileName"]).name,
            "frameRole": r.get("frameRole") or "dataset",
            "captureSource": r.get("captureSource") or "import",
        }
        for r in sorted(active, key=lambda r: r["capturedAt"], reverse=True)[:12]
    ]
    return result


def session_required(identifier):
    session = DatasetSession.objects.filter(pk=identifier).first()
    if session is None:
        missing("Dataset session")
    return session


@contextmanager
def write(identifier=None):
    with write_lock:
        try:
            base = root()
            folders = set(base.iterdir())
            scope = base / uuid.UUID(str(identifier)).hex if identifier else None
            previous = set(scope.rglob("*")) if scope and scope.exists() else set()
            manifests = {
                p: p.read_bytes()
                for p in previous
                if p.is_file() and p.suffix in [".json", ".jsonl"]
            }
            try:
                with inspection_transaction():
                    yield
                    sessions = (
                        DatasetSession.objects.filter(pk=identifier)
                        if identifier
                        else DatasetSession.objects.all()
                    )
                    for session in sessions:
                        atomic_write(
                            directory(session) / "session.json",
                            json.dumps(response(session), cls=Encoder),
                        )
            except Exception:
                if scope and scope.exists():
                    for p in scope.rglob("*"):
                        if p.is_file() and p not in previous:
                            p.unlink(missing_ok=True)
                for p, body in manifests.items():
                    atomic_write(p, body)
                for p in base.iterdir():
                    if p not in folders and p.is_dir():
                        shutil.rmtree(p)
                raise
        except (OSError, json.JSONDecodeError) as error:
            raise ApiError(
                "Dataset storage is unavailable. Check storage and backup status.",
                "DATASET_INVALID",
                503,
            ) from error


def apply_labels(session, payload):
    recipe = required(ProductRecipe, session.recipe_id)
    definition = recipe.definition
    values = {
        key: normalize(payload.get(key))
        for key in ["productClass", "qualityLabel", "colorLabel", "sizeLabel"]
    }
    defects = list(
        dict.fromkeys(normalize(d) for d in payload.get("defectLabels") or [] if normalize(d))
    )
    errors = []
    if len(payload.get("notes") or "") > 1000:
        errors.append("Notes exceed 1000 characters.")
    for key, collection, label in [
        ("productClass", "productClasses", "Product class"),
        ("colorLabel", "allowedColors", "Color"),
        ("sizeLabel", "sizeClasses", "Size"),
    ]:
        if definition[collection] and values[key] not in {
            v["code"] for v in definition[collection]
        }:
            errors.append(label + " is not defined by the recipe.")
    if values["qualityLabel"] not in ["GOOD", "DEFECTIVE"]:
        errors.append("Quality label must be GOOD or DEFECTIVE.")
    if any(d not in {v["code"] for v in definition["defectClasses"]} for d in defects):
        errors.append("A defect label is not defined by the recipe.")
    if errors:
        raise ApiError(" ".join(errors), "DATASET_INVALID")
    session.product_class = values["productClass"]
    session.quality_label = values["qualityLabel"]
    session.color_label = values["colorLabel"]
    session.size_label = values["sizeLabel"]
    session.defect_labels = defects
    session.notes = (payload.get("notes") or "").strip() or None


def start(payload):
    mode = (payload.get("captureMode") or "").strip().lower()
    simulation = bool(payload.get("simulation"))
    interval = payload.get("intervalMs")
    if mode not in ["manual", "interval", "hardware"]:
        raise ApiError("Capture mode must be manual, interval, or hardware.", "DATASET_INVALID")
    if mode == "hardware" and not simulation:
        raise ApiError("Hardware capture is not integrated.", "DATASET_INVALID")
    if mode == "interval" and (not isinstance(interval, int) or not 200 <= interval <= 60000):
        raise ApiError(
            "Interval capture must be between 200 and 60000 milliseconds.", "DATASET_INVALID"
        )
    with write():
        if DatasetSession.objects.filter(status="ACTIVE").exists():
            raise ApiError(
                "A dataset session is already active. Stop it before starting another.",
                "DATASET_INVALID",
                409,
            )
        recipe = required(ProductRecipe, payload.get("recipeId"))
        session = DatasetSession(
            recipe_id=recipe.id,
            recipe_name=recipe.name,
            capture_mode=mode,
            interval_ms=interval if mode == "interval" else None,
            is_simulation=simulation,
        )
        session.storage_path = str(root() / session.id.hex)
        apply_labels(session, payload)
        (directory(session) / "images").mkdir(parents=True)
        session.save()
        result = response(session, [])
    return result


def stop(identifier):
    with write(identifier):
        session = session_required(identifier)
        if session.status != "ACTIVE":
            raise ApiError("The dataset session is already stopped.", "DATASET_INVALID")
        session.status = "STOPPED"
        session.stopped_at = timezone.now()
        session.save()
        result = response(session)
    return result


def capture(identifier, payload, frame):
    jpeg = frame["jpeg"]
    if not jpeg or len(jpeg) > 900000:
        raise ApiError("Image must be a non-empty file under 900 KB.", "DATASET_INVALID")
    if len(jpeg) < 4 or not jpeg.startswith(b"\xff\xd8\xff") or not jpeg.endswith(b"\xff\xd9"):
        raise ApiError("Vision must return a JPEG frame.", "DATASET_INVALID")
    with write(identifier):
        session = session_required(identifier)
        if session.status != "ACTIVE":
            raise ApiError("The dataset session is stopped.", "DATASET_INVALID")
        apply_labels(session, payload)
        sample = uuid.uuid4()
        now = timezone.now()
        name = now.strftime("%Y%m%dT%H%M%S") + f"{now.microsecond // 1000:03d}-{sample.hex}.jpg"
        relative = "images/" + name
        (directory(session) / relative).write_bytes(jpeg)
        row = json_data(
            {
                "sampleId": sample,
                "sessionId": session.id,
                "capturedAt": now,
                "recipeId": session.recipe_id,
                "recipeName": session.recipe_name,
                "productClass": session.product_class,
                "qualityLabel": session.quality_label,
                "colorLabel": session.color_label,
                "sizeLabel": session.size_label,
                "defectLabels": session.defect_labels,
                "notes": session.notes,
                "fileName": relative,
                "sha256": hashlib.sha256(jpeg).hexdigest(),
                "sourceFileName": "vision-dataset-frame.jpg",
                "annotated": False,
                "boundingBoxes": [],
                "trigger": "camera",
                "simulation": session.is_simulation,
                "labelsComplete": bool(session.product_class),
                "camera": {
                    "sensor": "GC2145",
                    "pixelFormat": "RGB565",
                    "transport": "MJPEG",
                    "frameRole": "dataset",
                    "origin": frame["origin"],
                    "captureSource": frame["captureSource"],
                    "previewFallback": str(frame["previewFallback"]).lower(),
                },
                "rejected": False,
                "frameRole": "dataset",
                "sourceWidth": frame["width"],
                "sourceHeight": frame["height"],
                "captureSource": frame["captureSource"],
                "previewFallback": frame["previewFallback"],
            }
        )
        rows = entries(session) + [row]
        manifest_write(session, rows)
        session.image_count = sum(not r.get("rejected", False) for r in rows)
        session.save()
        result = response(session, rows)
        item = next(r for r in result["recentSamples"] if r["sampleId"] == str(sample))
    return {"session": result, "sample": item}


def discard(identifier, sample_id):
    with write(identifier):
        session = session_required(identifier)
        if session.status != "ACTIVE":
            raise ApiError("A stopped dataset session cannot discard captures.", "DATASET_INVALID")
        rows = entries(session)
        target = next(
            (r for r in rows if r["sampleId"] == str(sample_id) and not r.get("rejected", False)),
            None,
        )
        if target is None:
            missing("Dataset sample")
        target["rejected"] = True
        path = directory(session) / "images" / Path(target["fileName"]).name
        manifest_write(session, rows)
        session.image_count = sum(not r.get("rejected", False) for r in rows)
        session.save()
        result = response(session, rows)
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass
    return result


def read_image(identifier, name):
    session = session_required(identifier)
    if Path(name).name != name or ".." in name or "\\" in name:
        raise ApiError("Image name is invalid.", "DATASET_INVALID")
    path = directory(session) / "images" / name
    if not path.is_file():
        missing("Image")
    return path.read_bytes(), "image/png" if name.lower().endswith(".png") else "image/jpeg"


def recover():
    with write():
        for session in DatasetSession.objects.all():
            image_dir = directory(session) / "images"
            image_dir.mkdir(parents=True, exist_ok=True)
            rows = [r for r in entries(session) if not r.get("rejected", False)]
            session.image_count = len(rows)
            if any(not (directory(session) / r["fileName"]).is_file() for r in rows):
                session.status = "RECOVERY_REQUIRED"
                session.stopped_at = session.stopped_at or timezone.now()
            else:
                kept = {Path(r["fileName"]).name for r in rows}
                for path in image_dir.iterdir():
                    if path.is_file() and path.name not in kept:
                        path.unlink()
            session.save()
