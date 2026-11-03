import json
from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta
from pathlib import Path

import pytest
from django.db import close_old_connections
from django.test import Client
from django.utils import timezone

from smartline.devices.models import Alert, ConveyorSetting
from smartline.inspection import datasets
from smartline.inspection.models import (
    CalibrationProfile,
    DatasetSession,
    InspectionRecord,
    Product,
    ProductRecipe,
    ProductTrack,
    Statistic,
)


def post(client, path, payload=None):
    return client.post(path, data=json.dumps(payload or {}), content_type="application/json")


def data(response, status=200):
    assert response.status_code == status, response.content
    result = response.json()
    assert result["isSuccess"] is True, result
    return result["data"]


def error(response, status, code=None):
    assert response.status_code == status, response.content
    result = response.json()
    assert result["isSuccess"] is False
    if code:
        assert result["code"] == code
    return result


@pytest.mark.parametrize(
    "route",
    [
        "/api/products",
        "/api/products/defective",
        "/api/products/status/ok",
        "/api/products/color/red",
        "/api/alerts",
        "/api/alerts/active",
        "/api/alerts/type/fire",
        "/api/logs",
        "/api/logs/severity/warning",
        "/api/logs/event/PRODUCT_PASSED",
        "/api/statistics/daily",
        "/api/statistics/hourly",
        "/api/conveyor/status",
        "/api/sensors/status",
        "/api/dashboard",
        "/api/control-room",
        "/api/diagnostics",
        "/api/recipes",
        "/api/recipes/active",
        "/api/recipes/active/snapshot",
        "/api/calibration-profiles",
        "/api/tracking",
        "/api/tracking/queue",
        "/api/simulation/status",
        "/api/dataset/sessions",
    ],
)
def test_public_reads(app, client, route, monkeypatch):
    from smartline.inspection import vision_gateway

    monkeypatch.setattr(
        vision_gateway,
        "call",
        lambda *a, **k: {"status": "healthy", "camera": {"connected": True}, "model": {}},
    )
    data(client.get(route))


@pytest.mark.parametrize(
    "route",
    [
        "/api/auth/login",
        "/api/auth/refresh",
        "/api/users",
        "/api/no-such-route",
        "/hubs/dashboard",
        "/hubs/dashboard/negotiate",
        "/missing.js",
    ],
)
def test_retired_routes_remain_json_404(client, route):
    response = client.get(route)
    error(response, 404, "RESOURCE_NOT_FOUND")
    assert response["X-Frame-Options"] == "DENY"


@pytest.mark.parametrize(
    "body", ["{", "null", "[]", '{"confidence":NaN}', '{"confidence":Infinity}']
)
def test_malformed_json(app, client, body):
    error(
        client.post("/api/inspections", data=body, content_type="application/json"),
        400,
        "REQUEST_INVALID",
    )


@pytest.mark.parametrize(
    "route",
    [
        "/api/products/status/1",
        "/api/products/status/OK,DEFECTIVE",
        "/api/products/color/PURPLE",
        "/api/alerts/type/NONE",
        "/api/logs/severity/NONE",
        "/api/logs/event/0",
        "/api/statistics/hourly?date=bad",
        "/api/products?pageNumber=bad",
        "/api/inspections?simulation=bad",
    ],
)
def test_invalid_query_binding(app, client, route):
    error(client.get(route), 400)


def test_products_pagination_duplicate_and_filters(app, client):
    for i in range(4):
        data(
            post(client, "/api/products", {"barcode": f"BC{i}", "color": "red", "status": "OK"}),
            201,
        )
    result = data(client.get("/api/products?pageNumber=1&pageSize=2"))
    assert [r["barcode"] for r in result["items"]] == ["BC3", "BC2"]
    assert result["hasNextPage"] is True
    assert data(client.get("/api/products?pageNumber=2&pageSize=2"))["hasNextPage"] is False
    assert data(client.get("/api/products?pageNumber=-1&pageSize=1000"))["pageSize"] == 100
    error(post(client, "/api/products", {"barcode": "BC1"}), 409, "PRODUCT_BARCODE_DUPLICATE")
    assert data(client.get("/api/products/barcode/BC1"))["color"] == "RED"
    assert Statistic.objects.count() == 0


@pytest.mark.parametrize(
    "payload,code",
    [
        ({"barcode": ""}, "REQUEST_INVALID"),
        ({"barcode": "A" * 51}, "REQUEST_INVALID"),
        ({"barcode": "A", "status": "unknown"}, "PRODUCT_STATUS_INVALID"),
        ({"barcode": "A", "color": "PURPLE"}, "PRODUCT_COLOR_INVALID"),
    ],
)
def test_invalid_products(app, client, payload, code):
    error(post(client, "/api/products", payload), 400, code)
    assert Product.objects.count() == 0


def test_barcode_atomicity_and_statistics(app, client):
    data(post(client, "/api/sensors/barcode", {"barcode": "first", "color": "red"}))
    data(post(client, "/api/sensors/barcode", {"barcode": "", "color": "blue"}))
    assert Product.objects.count() == 2
    assert Alert.objects.filter(alert_type="NO_BARCODE_DETECTED").count() == 1
    daily = Statistic.objects.get(hour=None)
    assert (
        daily.total_products,
        daily.defective_products,
        daily.red_products,
        daily.blue_products,
    ) == (2, 1, 1, 1)
    error(
        post(client, "/api/sensors/barcode", {"barcode": "first"}), 409, "PRODUCT_BARCODE_DUPLICATE"
    )
    assert Statistic.objects.get(hour=None).total_products == 2


def test_conveyor_controls_and_mqtt_failure(app, client, mqtt):
    data(post(client, "/api/conveyor/speed", {"speed": 66}))
    data(post(client, "/api/conveyor/start"))
    error(post(client, "/api/conveyor/speed", {"speed": 12}), 409, "CONVEYOR_MUST_BE_STOPPED")
    data(post(client, "/api/conveyor/stop"))
    assert mqtt.records[0] == ("smartline/commands/speed", {"speed": 66}, False)
    mqtt.failure = True
    error(post(client, "/api/conveyor/start"), 503, "MQTT_UNAVAILABLE")
    assert ConveyorSetting.objects.get().is_running


@pytest.mark.parametrize("value", [-1, 101, 1.5, None, True])
def test_invalid_speed(app, client, value):
    error(post(client, "/api/conveyor/speed", {"speed": value}), 400)


def test_emergency_fire_generations_and_resets(app, client, mqtt):
    epoch = "a" * 32
    reading = {"isDetected": False, "deviceEpoch": epoch, "fireGeneration": 0}
    data(post(client, "/api/sensors/fire", reading))
    assert ConveyorSetting.objects.get().is_emergency_stopped
    data(post(client, "/api/conveyor/emergency-reset"))
    data(post(client, "/api/conveyor/start"))
    reading.update(isDetected=True, fireGeneration=1)
    data(post(client, "/api/sensors/fire", reading))
    error(post(client, "/api/conveyor/emergency-reset"), 409, "FIRE_SENSOR_ACTIVE")
    reading["isDetected"] = False
    data(post(client, "/api/sensors/fire", reading))
    error(post(client, "/api/conveyor/start"), 409, "CONVEYOR_EMERGENCY_LATCHED")
    data(post(client, "/api/conveyor/emergency-reset"))
    assert not ConveyorSetting.objects.get().is_running
    reading["fireGeneration"] = 0
    data(post(client, "/api/sensors/fire", reading))
    assert ConveyorSetting.objects.get().is_fire_generation_fault
    error(post(client, "/api/conveyor/emergency-reset"), 409, "FIRE_GENERATION_MISMATCH")
    reading["deviceEpoch"] = "b" * 32
    data(post(client, "/api/sensors/fire", reading))
    data(post(client, "/api/conveyor/emergency-reset"))
    assert mqtt.records[-1][1]["acknowledgedFireEpoch"] == "b" * 32


@pytest.mark.parametrize(
    "reading",
    [
        {},
        {"isDetected": False, "deviceEpoch": "X" * 32, "fireGeneration": 0},
        {"isDetected": False, "deviceEpoch": "a" * 32, "fireGeneration": -1},
        {"isDetected": False, "deviceEpoch": "a" * 32, "fireGeneration": 4294967296},
        {"isDetected": "false", "deviceEpoch": "a" * 32, "fireGeneration": 0},
    ],
)
def test_invalid_fire_protocol(app, client, reading):
    error(post(client, "/api/sensors/fire", reading), 400)


@pytest.mark.parametrize("temperature,fan", [(38, True), (36, False), (32, False)])
def test_environment_thresholds(app, client, mqtt, temperature, fan):
    data(post(client, "/api/sensors/environment", {"temperature": temperature, "humidity": 50}))
    assert ConveyorSetting.objects.get().fan_active is fan


def test_environment_hysteresis_alert_failure_commits(app, client, mqtt):
    data(post(client, "/api/sensors/environment", {"temperature": 38, "humidity": 50}))
    data(post(client, "/api/sensors/environment", {"temperature": 35, "humidity": 50}))
    assert ConveyorSetting.objects.get().fan_active
    data(post(client, "/api/sensors/environment", {"temperature": 32, "humidity": 50}))
    assert not ConveyorSetting.objects.get().fan_active
    mqtt.failure = True
    error(
        post(client, "/api/sensors/environment", {"temperature": 42, "humidity": 50}),
        503,
        "MQTT_UNAVAILABLE",
    )
    assert float(ConveyorSetting.objects.get().temperature) == 42
    assert Alert.objects.filter(alert_type="HIGH_TEMPERATURE").exists()


@pytest.mark.parametrize("temperature,humidity", [(-41, 50), (81, 50), (25, -1), (25, 101)])
def test_invalid_environment(app, client, temperature, humidity):
    error(
        post(
            client, "/api/sensors/environment", {"temperature": temperature, "humidity": humidity}
        ),
        400,
    )


def test_manual_inspection_source_and_simulation_counters(app, client, observation):
    observation.update(
        modelName="pretend", modelVersion="fake", observationSource="model", processingLatencyMs=50
    )
    result = data(post(client, "/api/inspections", observation), 201)
    assert result["inspection"]["observationSource"] == "manual"
    assert result["inspection"]["modelName"] is None
    assert result["inspection"]["processingLatencyMs"] is None
    assert result["inspection"]["decision"] == "PASS"
    simulation = data(
        post(
            client,
            "/api/simulation/arrivals",
            dict(observation, distanceMm=1000, beltSpeedMmPerSecond=200),
        ),
        201,
    )
    assert simulation["inspection"]["observationSource"] == "simulation"
    assert data(client.get("/api/control-room"))["totalCount"] == 1
    assert data(client.get("/api/simulation/status"))["active"] is True
    assert data(client.get("/api/inspections?simulation=true"))["items"][0]["simulation"] is True
    now = (timezone.now() + timedelta(seconds=60)).isoformat()
    assert data(post(client, "/api/simulation/tick", {"utcNow": now}))["sortedCount"] == 1
    data(post(client, "/api/tracking/" + result["track"]["productId"] + "/release"))
    error(
        post(client, "/api/tracking/" + result["track"]["productId"] + "/release"),
        400,
        "TRACKING_TRANSITION_INVALID",
    )
    assert Product.objects.count() == 0
    assert Statistic.objects.count() == 0


@pytest.mark.parametrize(
    "patch",
    [
        {"confidence": -1},
        {"confidence": 1.1},
        {"estimatedWidthMm": -1},
        {"estimatedHeightMm": -1},
        {"triggerSource": "hardware"},
        {"boundingBoxes": [None]},
        {"boundingBoxes": [{"x": -1, "y": 0, "width": 1, "height": 1}]},
        {"capturedAt": "2999-01-01T00:00:00Z"},
        {"recipeVersion": 999},
    ],
)
def test_invalid_inspection_does_not_persist(app, client, observation, patch):
    error(
        post(client, "/api/inspections", dict(observation, **patch)),
        409 if "recipeVersion" in patch else 400,
    )
    assert InspectionRecord.objects.count() == 0
    assert ProductTrack.objects.count() == 0


def test_recipes_calibration_crud_activation_and_uniqueness(app, client, recipe):
    result = data(post(client, "/api/recipes", {"name": "Other", "definition": recipe}), 201)
    data(post(client, "/api/recipes/" + result["id"] + "/activate"))
    assert ProductRecipe.objects.filter(is_active=True).count() == 1
    assert data(client.get("/api/recipes/active"))["name"] == "Other"
    error(
        post(client, "/api/recipes", {"name": "Other", "definition": recipe}), 409, "RECIPE_INVALID"
    )
    calibration = data(
        post(
            client,
            "/api/calibration-profiles",
            {
                "name": "Scale",
                "definition": {"pixelsPerMillimeter": 4, "sourceWidth": 640, "sourceHeight": 480},
            },
        ),
        201,
    )
    data(post(client, "/api/calibration-profiles/" + calibration["id"] + "/activate"))
    assert CalibrationProfile.objects.filter(is_active=True).count() == 1
    update = client.put(
        "/api/recipes/" + result["id"],
        data=json.dumps({"name": "Other", "definition": recipe}),
        content_type="application/json",
    )
    assert data(update)["version"] == 2


def start_payload():
    return {
        "productClass": "BOX_RED",
        "qualityLabel": "GOOD",
        "colorLabel": "RED",
        "sizeLabel": "LARGE",
        "defectLabels": [],
        "captureMode": "manual",
    }


def test_dataset_capture_discard_png_and_stop(app, client, frame, monkeypatch):
    from smartline.inspection import vision_gateway

    monkeypatch.setattr(vision_gateway, "frame", lambda role: frame)
    session = data(post(client, "/api/dataset/sessions", start_payload()), 201)
    identifier = session["sessionId"]
    error(
        post(client, "/api/dataset/sessions/" + identifier + "/captures", {}),
        404,
        "RESOURCE_NOT_FOUND",
    )
    result = data(
        post(client, "/api/dataset/sessions/" + identifier + "/captures/camera", start_payload())
    )
    assert result["session"]["imageCount"] == 1
    assert result["sample"]["captureSource"] == "esp32_capture_raw"
    assert (result["sample"]["sourceWidth"], result["sample"]["sourceHeight"]) == (16, 12)
    response = client.get(
        "/api/dataset/sessions/" + identifier + "/images/" + result["sample"]["fileName"]
    )
    assert response.content == frame["jpeg"]
    path = Path(DatasetSession.objects.get().storage_path)
    row = json.loads((path / "manifest.jsonl").read_text())
    assert row["boundingBoxes"] == [] and not row["annotated"]
    (path / "images/legacy.png").write_bytes(b"legacy-png")
    assert (
        client.get("/api/dataset/sessions/" + identifier + "/images/legacy.png")["Content-Type"]
        == "image/png"
    )
    data(
        client.delete(
            "/api/dataset/sessions/" + identifier + "/samples/" + result["sample"]["sampleId"]
        )
    )
    assert not (path / "images" / result["sample"]["fileName"]).exists()
    data(post(client, "/api/dataset/sessions/" + identifier + "/stop"))
    error(
        post(client, "/api/dataset/sessions/" + identifier + "/captures/camera", start_payload()),
        400,
        "DATASET_INVALID",
    )


@pytest.mark.parametrize(
    "patch",
    [
        {"productClass": "PURPLE"},
        {"qualityLabel": "UNKNOWN"},
        {"colorLabel": "PURPLE"},
        {"sizeLabel": "HUGE"},
        {"defectLabels": ["SCRATCHED"]},
        {"captureMode": "interval"},
        {"captureMode": "hardware"},
        {"captureMode": "interval", "intervalMs": 199},
    ],
)
def test_invalid_dataset_labels_and_mode(app, client, patch):
    error(
        post(client, "/api/dataset/sessions", dict(start_payload(), **patch)),
        400,
        "DATASET_INVALID",
    )
    assert not DatasetSession.objects.exists()


def test_dataset_failed_database_write_rolls_back_files(app, client, frame, monkeypatch):
    session = data(post(client, "/api/dataset/sessions", start_payload()), 201)
    path = Path(DatasetSession.objects.get().storage_path)
    original = DatasetSession.save

    def fail(self, *args, **kwargs):
        raise RuntimeError("injected failure")

    monkeypatch.setattr(DatasetSession, "save", fail)
    with pytest.raises(RuntimeError):
        datasets.capture(session["sessionId"], start_payload(), frame)
    assert not list((path / "images").iterdir())
    assert not (path / "manifest.jsonl").exists()
    assert DatasetSession.objects.get().image_count == 0
    monkeypatch.setattr(DatasetSession, "save", original)
    datasets.capture(session["sessionId"], start_payload(), frame)


def test_dataset_restart_recovery(app, client, frame):
    session = data(post(client, "/api/dataset/sessions", start_payload()), 201)
    result = datasets.capture(session["sessionId"], start_payload(), frame)
    path = Path(DatasetSession.objects.get().storage_path)
    (path / "images/orphan.jpg").write_bytes(b"orphan")
    datasets.recover()
    assert not (path / "images/orphan.jpg").exists()
    (path / "images" / result["sample"]["fileName"]).unlink()
    datasets.recover()
    assert DatasetSession.objects.get().status == "RECOVERY_REQUIRED"
    data(client.get("/api/recipes"))


def test_concurrent_dataset_start_and_barcode(app):
    def start(_):
        close_old_connections()
        try:
            return post(Client(), "/api/dataset/sessions", start_payload()).status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(start, range(2))) == [201, 409]

    def scan(_):
        close_old_connections()
        try:
            return post(Client(), "/api/sensors/barcode", {"barcode": "same"}).status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(scan, range(2))) == [200, 409]
    assert Statistic.objects.get(hour=None).total_products == 1


def test_concurrent_emergency_preempts_start_and_reconnect_republishes(app, mqtt):
    import threading
    import time
    from smartline.devices import service

    entered, release = threading.Event(), threading.Event()
    publish = mqtt.publish

    def blocked(topic, payload, retain=False):
        if topic.endswith("/stop") and payload.get("isRunning") and not payload.get("emergency"):
            entered.set()
            assert release.wait(5)
        publish(topic, payload, retain)

    mqtt.publish = blocked

    def invoke(action):
        close_old_connections()
        try:
            return post(Client(), "/api/conveyor/" + action).status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=2) as pool:
        start = pool.submit(invoke, "start")
        assert entered.wait(5)
        emergency = pool.submit(invoke, "emergency-stop")
        deadline = time.monotonic() + 5
        while not any(p[1].get("emergency") for p in mqtt.records) and time.monotonic() < deadline:
            time.sleep(0.01)
        assert any(p[1].get("emergency") for p in mqtt.records)
        release.set()
        assert start.result() == 200
        assert emergency.result() == 200
    setting = ConveyorSetting.objects.get()
    assert setting.is_emergency_stopped and not setting.is_running
    assert mqtt.records[-1][1]["emergency"] is True
    assert Alert.objects.filter(alert_type="EMERGENCY_STOP").count() == 1
    setting.speed = 73
    setting.emergency_reset_generation = 7
    setting.fire_device_epoch = "a" * 32
    setting.last_fire_generation = 9
    setting.fan_active = True
    setting.save()
    mqtt.records.clear()
    service.publish_desired_state()
    service.publish_desired_state()
    assert len(mqtt.records) == 6
    assert mqtt.records[0][1]["resetGeneration"] == 7
    assert mqtt.records[0][1]["acknowledgedFireGeneration"] == 9
    assert mqtt.records[1][1] == {"speed": 73}
    assert mqtt.records[2][1] == {"active": True}


def test_concurrent_fire_only_audits_one_transition(app):
    def invoke(_):
        close_old_connections()
        try:
            return post(
                Client(),
                "/api/sensors/fire",
                {"isDetected": True, "deviceEpoch": "a" * 32, "fireGeneration": 1},
            ).status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=8) as pool:
        assert list(pool.map(invoke, range(16))) == [200] * 16
    assert Alert.objects.filter(alert_type="FIRE").count() == 1
    from smartline.devices.models import SystemLog

    assert SystemLog.objects.filter(event_type="FIRE_DETECTED").count() == 1


def test_emergency_persists_even_if_mqtt_is_down(app, client, mqtt):
    mqtt.failure = True
    error(post(client, "/api/conveyor/emergency-stop"), 503, "MQTT_UNAVAILABLE")
    setting = ConveyorSetting.objects.get()
    assert setting.is_emergency_stopped and not setting.is_running
    assert Alert.objects.filter(alert_type="EMERGENCY_STOP").count() == 1


@pytest.mark.parametrize("change", ["recipe", "calibration"])
def test_vision_rejects_changed_snapshot(app, client, observation, monkeypatch, change):
    from smartline.inspection import vision_gateway

    recipe = ProductRecipe.objects.get(is_active=True)
    calibration = CalibrationProfile.objects.first()
    calibration.is_active = True
    calibration.save()

    def call(*args, **kwargs):
        if change == "recipe":
            ProductRecipe.objects.filter(pk=recipe.id).update(version=2)
        else:
            CalibrationProfile.objects.filter(pk=calibration.id).update(version=2)
        return {
            "recipeId": str(recipe.id),
            "recipeVersion": recipe.version,
            "inference": {"available": True},
            "observation": observation,
            "frame": {"capturedAt": timezone.now().isoformat()},
        }

    monkeypatch.setattr(vision_gateway, "call", call)
    import uuid

    error(
        post(client, "/api/inspections/vision", {"requestId": str(uuid.uuid4())}),
        409,
        "RECIPE_INVALID",
    )
    assert InspectionRecord.objects.count() == 0 and ProductTrack.objects.count() == 0


def test_concurrent_vision_retry_only_captures_once(app, observation, monkeypatch):
    from smartline.inspection import vision_gateway
    import uuid

    identifier = str(uuid.uuid4())
    recipe = ProductRecipe.objects.get(is_active=True)
    calls = []

    def call(*args, **kwargs):
        calls.append(1)
        return {
            "recipeId": str(recipe.id),
            "recipeVersion": recipe.version,
            "inference": {"available": True},
            "observation": dict(observation),
            "frame": {"capturedAt": timezone.now().isoformat()},
        }

    monkeypatch.setattr(vision_gateway, "call", call)

    def invoke(_):
        close_old_connections()
        try:
            return post(Client(), "/api/inspections/vision", {"requestId": identifier}).status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(invoke, range(2))) == [200, 201]
    assert calls == [1]
    assert InspectionRecord.objects.count() == 1


def test_concurrent_recipe_activation(app, client, recipe):
    identifiers = [
        data(post(client, "/api/recipes", {"name": f"Recipe {n}", "definition": recipe}), 201)["id"]
        for n in range(3)
    ]

    def activate(identifier):
        close_old_connections()
        try:
            return post(Client(), "/api/recipes/" + identifier + "/activate").status_code
        finally:
            close_old_connections()

    with ThreadPoolExecutor(max_workers=3) as pool:
        assert list(pool.map(activate, identifiers)) == [200] * 3
    assert ProductRecipe.objects.filter(is_active=True).count() == 1


def test_inspection_failure_rolls_back_track_and_notifications(
    app, client, observation, monkeypatch
):
    from smartline.web.events import bus

    notices = []
    monkeypatch.setattr(bus, "publish", lambda *args: notices.append(args))

    def fail(*args, **kwargs):
        raise RuntimeError("sensitive synthetic detail")

    monkeypatch.setattr(InspectionRecord.objects, "create", fail)
    response = post(client, "/api/inspections", observation)
    result = error(response, 500, "UNEXPECTED_ERROR")
    assert "sensitive" not in result["message"]
    assert not ProductTrack.objects.exists()
    assert not notices
    assert response["X-Frame-Options"] == "DENY"


def test_partial_image_failure_restores_previous_manifest(app, client, frame, monkeypatch):
    session = data(post(client, "/api/dataset/sessions", start_payload()), 201)
    datasets.capture(session["sessionId"], start_payload(), frame)
    directory = Path(DatasetSession.objects.get().storage_path)
    before = (directory / "manifest.jsonl").read_bytes()
    write = Path.write_bytes

    def fail(path, body):
        if path.suffix == ".jpg":
            write(path, body[:10])
            raise OSError("injected failure")
        return write(path, body)

    monkeypatch.setattr(Path, "write_bytes", fail)
    from smartline.web.errors import ApiError

    with pytest.raises(ApiError) as result:
        datasets.capture(session["sessionId"], start_payload(), frame)
    assert result.value.status == 503
    assert (directory / "manifest.jsonl").read_bytes() == before
    assert len(list((directory / "images").iterdir())) == 1
    assert DatasetSession.objects.get().image_count == 1
