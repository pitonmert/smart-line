from datetime import timedelta
from urllib.parse import urlsplit
from zoneinfo import ZoneInfo

from django.conf import settings
from django.db.models import Count
from django.utils import timezone

from smartline.devices.models import Alert, ConveyorSetting, SystemLog
from smartline.devices.mqtt import transport
from smartline.devices.service import alert_data, log_data, status
from smartline.inspection.decision import yield_percent
from smartline.inspection.models import (
    CalibrationProfile,
    InspectionRecord,
    Product,
    ProductRecipe,
    ProductTrack,
    Statistic,
)
from smartline.inspection.production import product_data, statistic_data
from smartline.inspection.workflow import simulation_status, track_data
from smartline.inspection import vision_gateway


def today():
    return timezone.now().astimezone(ZoneInfo(settings.TIME_ZONE)).date()


def dashboard():
    statistic = Statistic.objects.filter(date=today(), hour=None).first()
    return {
        "conveyorStatus": status(),
        "activeAlerts": [
            alert_data(a)
            for a in Alert.objects.filter(is_active=True).order_by("-triggered_at", "-id")[:100]
        ],
        "todayStatistics": statistic_data(statistic) if statistic else None,
        "recentProducts": [
            product_data(p) for p in Product.objects.order_by("-passed_at", "-id")[:5]
        ],
    }


def control_room():
    records = InspectionRecord.objects.filter(is_simulation=False)
    counts = {
        r["decision"]: r["count"] for r in records.values("decision").annotate(count=Count("id"))
    }
    last = InspectionRecord.objects.order_by("-processed_at").first()
    current = (
        ProductTrack.objects.exclude(stage__in=["SORTED", "FAILED"]).order_by("-updated_at").first()
    )
    recipe = ProductRecipe.objects.filter(is_active=True).first()
    return {
        "activeRecipe": recipe.name if recipe else None,
        "currentProduct": track_data(current) if current else None,
        "lastInspection": last.result if last else None,
        "totalCount": records.count(),
        "acceptedCount": counts.get("PASS", 0),
        "rejectedCount": counts.get("REJECT", 0),
        "manualReviewCount": counts.get("MANUAL_REVIEW", 0),
        "yieldPercent": yield_percent(counts.get("PASS", 0), counts.get("REJECT", 0)),
        "binCounts": [
            {"destinationBin": r["destination_bin"], "count": r["count"]}
            for r in records.values("destination_bin").annotate(count=Count("id"))
        ],
        "recentEvents": [log_data(r) for r in SystemLog.objects.order_by("-created_at")[:8]],
        "simulationActive": simulation_status()["active"] or bool(last and last.is_simulation),
    }


def diagnostics():
    recipe = ProductRecipe.objects.filter(is_active=True).first()
    profile = CalibrationProfile.objects.filter(is_active=True).first()
    s = ConveyorSetting.objects.filter(pk=1).first()
    last = InspectionRecord.objects.order_by("-processed_at").first()
    reachable = False
    probe = {}
    try:
        probe = vision_gateway.call("/health", timeout=0.8)
        reachable = True
    except Exception:
        pass
    camera = probe.get("camera") or {}
    still = camera.get("still") or {}
    camera_status = still.get("cameraStatus") or {}
    model = probe.get("model") or {}
    configured_model = (recipe.definition.get("model") or {}) if recipe else {}
    configured = bool(model.get("configured") or configured_model.get("artifactFileName"))
    loaded = model.get("loaded", False)
    model_status = (
        "loaded"
        if loaded
        else "error"
        if configured and model.get("loadError")
        else "registered"
        if configured
        else "not_loaded"
    )
    model_data = {
        "status": model_status,
        "name": configured_model.get("name"),
        "version": model.get("modelVersion") or configured_model.get("version"),
        "configured": configured,
        "loaded": loaded,
        "backend": model.get("backend"),
        "task": model.get("task") or configured_model.get("task"),
        "artifact": model.get("artifact") or configured_model.get("artifactFileName"),
        "lastInferenceLatencyMs": model.get("lastInferenceLatencyMs"),
        "loadError": model.get("loadError"),
        "confidenceThreshold": model.get("confidenceThreshold"),
    }
    stream = (recipe.definition.get("cameraProfile") or {}).get("streamUrl") if recipe else None
    public_stream = (
        stream
        if stream and not urlsplit(stream).username
        else urlsplit(stream).hostname
        if stream
        else None
    )
    return {
        "api": "healthy",
        "database": "healthy",
        "mqtt": "connected" if transport.connected else "disconnected",
        "vision": "ONLINE"
        if reachable and probe.get("status") == "healthy"
        else "OFFLINE"
        if settings.VISION_URL
        else "NOT_CONFIGURED",
        "camera": "ONLINE"
        if reachable and camera.get("connected") is True
        else "OFFLINE"
        if reachable or settings.VISION_URL
        else "NOT_CONFIGURED",
        "lastInspectionAt": last.processed_at if last else None,
        "modelStatus": model_status,
        "modelName": configured_model.get("name"),
        "modelVersion": configured_model.get("version"),
        "esp32": "unknown"
        if not s
        else "online"
        if timezone.now() - s.last_updated < timedelta(seconds=30)
        else "stale",
        "signalR": "retired",
        "liveUpdates": "sse_enabled",
        "simulation": simulation_status(),
        "hardwareActuationEnabled": settings.HARDWARE_ACTUATION_ENABLED,
        "environment": "Development" if settings.DEBUG else "Production",
        "activeRecipe": recipe.name if recipe else None,
        "timeOfFlightSource": profile.definition.get("tofBaseline", {}).get(
            "source", "NOT_INTEGRATED"
        )
        if profile
        else "NOT_INTEGRATED",
        "cameraStreamUrl": public_stream,
        "lastFrameAt": camera.get("lastFrameAt"),
        "frameWidth": camera.get("frameWidth"),
        "frameHeight": camera.get("frameHeight"),
        "cameraConsecutiveFailures": camera.get("consecutiveFailures"),
        "cameraStreamHost": camera.get("streamHost") or urlsplit(stream).netloc
        if stream
        else camera.get("streamHost"),
        "model": model_data,
        "stillConfigured": still.get("configured"),
        "configuredStillWidth": camera_status.get("captureWidth"),
        "configuredStillHeight": camera_status.get("captureHeight"),
        "lastStillWidth": still.get("lastWidth"),
        "lastStillHeight": still.get("lastHeight"),
        "lastStillError": still.get("lastError"),
        "previewFallbackAllowed": still.get("previewFallbackAllowed"),
        "sensorPid": camera_status.get("sensorPid"),
        "activeRecipeVersion": recipe.version if recipe else None,
    }
