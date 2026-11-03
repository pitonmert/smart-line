"""Framework-independent edge cases retained from the previous HTTP test suite."""

from datetime import timedelta
from email.message import Message
from urllib.error import URLError

import pytest
from django.db import DatabaseError
from django.utils import timezone

from smartline.devices.models import Alert, ConveyorSetting, SystemLog
from smartline.inspection.models import (
    CalibrationProfile,
    InspectionRecord,
    Product,
    ProductRecipe,
    Statistic,
)
from smartline.inspection import recipes, vision_gateway
from smartline.web.errors import ApiError
from test_api import data, error, post


@pytest.mark.parametrize(
    "route",
    [
        "/api/products",
        "/api/products/defective",
        "/api/products/status/DEFECTIVE",
        "/api/products/color/RED",
        "/api/alerts",
        "/api/alerts/active",
        "/api/alerts/type/FIRE",
        "/api/logs",
        "/api/logs/severity/Warning",
        "/api/logs/event/PRODUCT_PASSED",
    ],
)
def test_filtered_collection_pages_have_stable_order_and_bounds(app, client, route):
    now = timezone.now()
    for i in range(5):
        Product.objects.create(barcode=f"PAGE-{i}", color="RED", status="DEFECTIVE", passed_at=now)
        Alert.objects.create(alert_type="FIRE", triggered_at=now)
        SystemLog.objects.create(event_type="PRODUCT_PASSED", severity="Warning", created_at=now)
    first = data(client.get(route + "?pageSize=2"))
    second = data(client.get(route + "?pageNumber=2&pageSize=2"))
    model = Product if "/products" in route else Alert if "/alerts" in route else SystemLog
    identifiers = list(model.objects.order_by("-id").values_list("id", flat=True))
    assert [r["id"] for r in first["items"]] == identifiers[:2]
    assert [r["id"] for r in second["items"]] == identifiers[2:4]
    assert second["hasNextPage"] and second["hasPreviousPage"]
    bounded = data(client.get(route + "?pageNumber=-2&pageSize=500"))
    assert bounded["pageNumber"] == 1 and bounded["pageSize"] == 100
    assert len(bounded["items"]) == 5


def test_error_contracts_hide_unexpected_exceptions_and_reject_padded_duplicates(
    app, client, monkeypatch
):
    from smartline.inspection import production

    error(client.get("/api/products/999999"), 404, "RESOURCE_NOT_FOUND")
    data(post(client, "/api/products", {"barcode": "EXISTS", "color": "RED"}), 201)
    error(post(client, "/api/products", {"barcode": " EXISTS "}), 409, "PRODUCT_BARCODE_DUPLICATE")
    error(
        post(client, "/api/products", {"barcode": "NUMBER", "color": "2"}),
        400,
        "PRODUCT_COLOR_INVALID",
    )

    def fail(*args, **kwargs):
        raise RuntimeError("internal connection secret")

    monkeypatch.setattr(production, "create_product", fail)
    result = error(post(client, "/api/products", {"barcode": "HIDDEN"}), 500, "UNEXPECTED_ERROR")
    assert "secret" not in str(result)


def test_statistics_date_bounds_and_daily_hourly_separation(app, client):
    today = timezone.localdate()
    Statistic.objects.create(date=today, hour=None, total_products=10)
    Statistic.objects.create(date=today, hour=8, total_products=4)
    assert data(client.get("/api/statistics/daily"))[0]["totalProducts"] == 10
    assert data(client.get("/api/statistics/hourly"))[0]["totalProducts"] == 4
    start = (today - timedelta(days=366)).isoformat()
    error(client.get(f"/api/statistics/daily?startDate={start}&endDate={today}"), 400)


def test_failed_activation_retains_previous_recipe(app, recipe, monkeypatch):
    original = ProductRecipe.objects.get(is_active=True)
    other = recipes.save({"name": "Second recipe", "definition": recipe})
    save = ProductRecipe.save

    def fail(item, *args, **kwargs):
        if str(item.pk) == other["id"] and item.is_active:
            raise DatabaseError("activation write failed")
        return save(item, *args, **kwargs)

    monkeypatch.setattr(ProductRecipe, "save", fail)
    with pytest.raises(DatabaseError):
        recipes.activate(other["id"])
    assert ProductRecipe.objects.get(is_active=True).pk == original.pk


def test_inspection_uses_recipe_explicit_calibration_identity(app, client, recipe, observation):
    explicit = data(
        post(
            client,
            "/api/calibration-profiles",
            {
                "name": "Explicit",
                "definition": {"pixelsPerMillimeter": 5, "sourceWidth": 640, "sourceHeight": 480},
            },
        ),
        201,
    )
    active = CalibrationProfile.objects.exclude(pk=explicit["id"]).first()
    recipes.activate(active.pk, calibration=True)
    recipe["calibrationProfileId"] = explicit["id"]
    saved = data(
        post(client, "/api/recipes", {"name": "Pinned calibration", "definition": recipe}), 201
    )
    result = data(
        post(
            client,
            "/api/inspections",
            dict(observation, recipeId=saved["id"], recipeVersion=saved["version"]),
        ),
        201,
    )
    assert result["inspection"]["calibrationProfileId"] == explicit["id"]
    assert InspectionRecord.objects.get().result["calibrationVersion"] == 1


def test_environment_database_failure_leaves_state_and_audits_unchanged(
    app, client, monkeypatch, mqtt
):
    previous = ConveyorSetting.objects.get()

    def fail(*args, **kwargs):
        raise DatabaseError("write failed")

    monkeypatch.setattr(SystemLog.objects, "create", fail)
    error(
        post(client, "/api/sensors/environment", {"temperature": 42, "humidity": 40}),
        409,
        "DATABASE_CONFLICT",
    )
    current = ConveyorSetting.objects.get()
    assert current.temperature == previous.temperature and current.fan_active == previous.fan_active
    assert not Alert.objects.exists() and not mqtt.records


@pytest.mark.parametrize(
    "reason,status,code",
    [
        (TimeoutError("timeout"), 504, "VISION_TIMEOUT"),
        (OSError("closed"), 503, "VISION_UNAVAILABLE"),
    ],
)
def test_vision_transport_failure_codes(settings, monkeypatch, reason, status, code):
    settings.VISION_URL = "http://127.0.0.1:1"

    def fail(*args, **kwargs):
        raise URLError(reason)

    monkeypatch.setattr(vision_gateway, "urlopen", fail)
    with pytest.raises(ApiError) as result:
        vision_gateway.call("/health")
    assert (result.value.status, result.value.code) == (status, code)


def test_vision_frame_metadata_keeps_still_dimensions_and_rejects_bad_bytes(monkeypatch, jpeg):
    headers = Message()
    for key, value in {
        "X-Frame-Width": "640",
        "X-Frame-Height": "480",
        "X-Frame-Origin": "still_capture",
        "X-Capture-Source": "esp32_capture_raw",
    }.items():
        headers[key] = value
    monkeypatch.setattr(vision_gateway, "call", lambda *args, **kwargs: (jpeg, headers))
    result = vision_gateway.frame("inspection")
    assert (result["width"], result["height"], result["captureSource"]) == (
        640,
        480,
        "esp32_capture_raw",
    )
    monkeypatch.setattr(vision_gateway, "call", lambda *args, **kwargs: (b"broken", headers))
    with pytest.raises(ApiError):
        vision_gateway.frame("inspection")
