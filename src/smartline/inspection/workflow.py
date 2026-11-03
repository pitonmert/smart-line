import copy
import threading
import uuid
from datetime import timedelta, timezone as datetime_timezone

from django.conf import settings
from django.db import transaction
from django.utils import timezone
from django.utils.dateparse import parse_datetime

from smartline.devices.service import audit
from smartline.web.errors import ApiError, missing
from smartline.web.events import bus
from smartline.web.serialization import fields, json_data
from .decision import decide
from .models import InspectionRecord, ProductRecipe, ProductTrack
from .recipes import calibration_for, required
from .tracking import actuation, advance, delay_milliseconds, sort_due_at
from .transactions import inspection_transaction
from .validation import finite, validate_recipe

simulation_latch = threading.Event()


def timestamp(value):
    if value is None:
        return None
    parsed = parse_datetime(value) if isinstance(value, str) else value
    if parsed is None:
        raise ApiError("Invalid timestamp.")
    return (
        timezone.make_aware(parsed, datetime_timezone.utc) if timezone.is_naive(parsed) else parsed
    )


def track_data(track):
    return fields(
        track,
        "public_code recipe_id inspection_id stage destination_bin decision failure_reason detected_at captured_at inspecting_at decided_at queued_at sorted_at failed_at manual_review_at sort_due_at",
        productId=str(track.id),
        simulation=track.is_simulation,
    )


def simulation_status():
    count = (
        ProductTrack.objects.filter(is_simulation=True)
        .exclude(stage__in=["SORTED", "FAILED"])
        .count()
    )
    return {
        "enabled": settings.SIMULATION_ENABLED,
        "active": simulation_latch.is_set() or count > 0,
        "latched": simulation_latch.is_set(),
        "inFlightSimulatedProducts": count,
    }


def ensure_simulation():
    if not settings.SIMULATION_ENABLED:
        raise ApiError(
            "Simulation mode is disabled in this environment.", "SIMULATION_DISABLED", 403
        )


def submit(payload, model_source=False, request_id=None, expected=None, simulation=False):
    observation = copy.deepcopy(payload)
    observation["observationSource"] = "model" if model_source else "manual"
    observation.setdefault("confidence", 0)
    observation.setdefault("triggerSource", "api")
    observation["simulation"] = (
        bool(observation.get("simulation"))
        or simulation
        or (observation.get("triggerSource") or "").lower() == "simulator"
    )
    if (observation.get("triggerSource") or "").lower() == "hardware" and not observation[
        "simulation"
    ]:
        raise ApiError(
            "VL53L1X hardware trigger is not integrated. Use a manual, API, or simulator trigger."
        )
    captured = timestamp(observation.get("capturedAt"))
    if captured and captured > timezone.now() + timedelta(seconds=5):
        raise ApiError("Capture time cannot be in the future.")
    if (
        observation.get("processingLatencyMs") is not None
        and observation["processingLatencyMs"] < 0
        or len(observation.get("modelName") or "") > 80
        or len(observation.get("modelVersion") or "") > 80
        or len(observation.get("imageReference") or "") > 300
    ):
        raise ApiError("Observation metadata is invalid.")
    for box in observation.get("boundingBoxes") or []:
        if (
            not isinstance(box, dict)
            or any(not finite(box.get(k)) for k in ["x", "y", "width", "height"])
            or box["x"] < 0
            or box["y"] < 0
            or box["width"] <= 0
            or box["height"] <= 0
        ):
            raise ApiError("Bounding boxes must have finite positive dimensions.")
    with inspection_transaction():
        if request_id:
            previous = InspectionRecord.objects.filter(pk=request_id).first()
            if previous:
                return {
                    "inspection": previous.result,
                    "track": track_data(ProductTrack.objects.get(pk=previous.product_id)),
                    "actuation": actuation(settings.HARDWARE_ACTUATION_ENABLED),
                }, False
        recipe = required(ProductRecipe, observation.get("recipeId"))
        definition = recipe.definition
        profile = calibration_for(definition)
        if model_source and (
            not recipe.is_active
            or expected
            != (
                recipe.version,
                profile.id if profile else None,
                profile.version if profile else None,
                profile.definition if profile else None,
            )
        ):
            raise ApiError(
                "Active recipe or calibration changed. Inspect again.", "RECIPE_INVALID", 409
            )
        if (
            observation.get("recipeVersion") is not None
            and recipe.version != observation["recipeVersion"]
        ):
            raise ApiError(
                "Recipe changed. Inspect again using the current version.", "RECIPE_INVALID", 409
            )
        errors = validate_recipe(definition)
        if errors:
            raise ApiError(" ".join(errors), "RECIPE_INVALID")
        now = timezone.now()
        existing = observation.get("productId")
        if existing:
            track = ProductTrack.objects.filter(pk=existing).first()
            if track is None:
                missing("Product")
            if track.is_simulation:
                observation["simulation"] = True
            elif observation["simulation"]:
                raise ApiError("A real product cannot accept a simulated inspection.")
        else:
            track = ProductTrack(
                is_simulation=observation["simulation"], detected_at=now, updated_at=now
            )
            track.public_code = "PRD-" + track.id.hex[:16].upper()
        try:
            result = decide(definition, observation)
        except ValueError as error:
            raise ApiError(str(error)) from error
        try:
            if track.stage == "DETECTED":
                advance(track, "CAPTURED", now)
            if track.stage == "CAPTURED":
                advance(track, "INSPECTING", now)
            if track.stage != "INSPECTING":
                raise ValueError(
                    f"Invalid product tracking transition {track.stage} -> INSPECTING."
                )
            advance(track, "DECIDED", now)
            track.destination_bin = result["destinationBin"]
            track.decision = result["decision"]
            if result["decision"] == "MANUAL_REVIEW":
                advance(track, "MANUAL_REVIEW", now, "Recipe decision requires manual review.")
            else:
                advance(track, "QUEUED_FOR_SORT", now)
                calibration = profile.definition if profile else {}
                distance = (
                    observation.get("distanceMm", calibration.get("inspectionToSorterDistanceMm"))
                    if result["simulation"]
                    else calibration.get("inspectionToSorterDistanceMm")
                )
                speed = (
                    observation.get("beltSpeedMmPerSecond", calibration.get("beltSpeedMmPerSecond"))
                    if result["simulation"]
                    else calibration.get("beltSpeedMmPerSecond")
                )
                track.sort_due_at = sort_due_at(now, delay_milliseconds(distance, speed))
        except (ValueError, OverflowError) as error:
            raise ApiError(
                str(error),
                "TRACKING_TRANSITION_INVALID"
                if "transition" in str(error)
                else "CALIBRATION_INVALID",
            ) from error
        identifier = uuid.UUID(str(request_id)) if request_id else uuid.uuid4()
        track.recipe_id = recipe.id
        track.inspection_id = identifier
        track.save()
        result.update(
            {
                "inspectionId": identifier,
                "productId": track.id,
                "recipeId": recipe.id,
                "capturedAt": captured or now,
                "processedAt": now,
                "confidence": observation["confidence"],
                "boundingBoxes": observation.get("boundingBoxes") or [],
                "geometry": {
                    "sourceWidth": observation.get(
                        "sourceWidth", definition["cameraProfile"].get("sourceWidth")
                    ),
                    "sourceHeight": observation.get(
                        "sourceHeight", definition["cameraProfile"].get("sourceHeight")
                    ),
                    "roi": definition["roi"],
                },
                "visualWidth": observation.get("visualWidth"),
                "visualHeight": observation.get("visualHeight"),
                "modelName": observation.get("modelName") if model_source else None,
                "modelVersion": observation.get("modelVersion") if model_source else None,
                "processingLatencyMs": observation.get("processingLatencyMs")
                if model_source
                else None,
                "cameraSource": observation.get("cameraSource")
                or definition["cameraProfile"].get("streamUrl")
                or "",
                "imageReference": observation.get("imageReference"),
                "recipeVersion": recipe.version,
                "frameRole": observation.get("frameRole"),
                "captureSource": observation.get("captureSource"),
                "previewFallback": bool(observation.get("previewFallback")),
                "observationSource": "simulation"
                if result["simulation"]
                else observation["observationSource"],
                "calibrationProfileId": profile.id if profile else None,
                "calibrationVersion": profile.version if profile else None,
            }
        )
        if (
            observation.get("estimatedWidthMm") is not None
            or observation.get("estimatedHeightMm") is not None
        ):
            result["estimatedPhysicalDimensions"] = {
                "widthMm": observation.get("estimatedWidthMm"),
                "heightMm": observation.get("estimatedHeightMm"),
                "method": "visual_estimate",
                "depthAvailable": False,
                "distanceMm": None,
            }
        if result["simulation"] and "SIMULATION" not in result["reasons"]:
            result["reasons"].append("SIMULATION")
        result = json_data(result)
        InspectionRecord.objects.create(
            id=identifier,
            product_id=track.id,
            recipe_id=recipe.id,
            captured_at=captured or now,
            processed_at=now,
            product_class=result["productClass"],
            color_class=result["colorClass"],
            size_class=result["sizeClass"],
            defect_classes=result["defectClasses"],
            quality_status=result["qualityStatus"],
            confidence=result["confidence"],
            decision=result["decision"],
            destination_bin=result["destinationBin"],
            model_name=result["modelName"],
            model_version=result["modelVersion"],
            processing_latency_ms=result["processingLatencyMs"],
            image_reference=result["imageReference"],
            is_simulation=result["simulation"],
            result=result,
        )
        audit(
            "SIMULATION_ARRIVAL" if result["simulation"] else "INSPECTION_RECORDED",
            ("[SIMULATION] " if result["simulation"] else "")
            + f"{track.public_code} {track.decision} -> {track.destination_bin}",
        )
        td = track_data(track)
        transaction.on_commit(lambda: bus.publish("ReceiveInspectionUpdate", result))
        transaction.on_commit(lambda: bus.publish("ReceiveTrackingUpdate", td))
    return {
        "inspection": result,
        "track": td,
        "actuation": actuation(settings.HARDWARE_ACTUATION_ENABLED),
    }, True


def release(identifier, now=None):
    with inspection_transaction():
        track = ProductTrack.objects.filter(pk=identifier).first()
        if track is None:
            missing("Product")
        try:
            advance(track, "SORTED", now or timezone.now())
        except ValueError as error:
            raise ApiError(str(error), "TRACKING_TRANSITION_INVALID") from error
        track.save()
        result = track_data(track)
        transaction.on_commit(lambda: bus.publish("ReceiveTrackingUpdate", result))
    return {"track": result, "actuation": actuation(settings.HARDWARE_ACTUATION_ENABLED)}
