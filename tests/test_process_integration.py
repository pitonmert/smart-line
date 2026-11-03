import json
import os
import socket
import subprocess
import threading
import time
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen

import pytest
from django.db import connection

from smartline.inspection.models import InspectionRecord, ProductTrack


@pytest.mark.integration
def test_two_process_inspection_retry_dataset_and_live_events(app, settings, tmp_path):
    env = dict(
        os.environ,
        MQTT_ENABLED="false",
        SMARTLINE_MOCK="false",
        DATASET_ROOT=str(settings.DATASET_ROOT),
    )
    db = connection.settings_dict
    env["DATABASE_URL"] = (
        f"postgresql://{quote(db['USER'])}:{quote(db['PASSWORD'])}@{db['HOST']}:{db['PORT']}/{db['NAME']}"
    )
    image = subprocess.Popen(
        [str(Path.cwd() / ".venv/bin/python"), "tests/support/vision_process.py"],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    web = None
    try:
        ready = threading.Event()
        port_holder = []

        def read_port():
            line = image.stdout.readline()
            if line.strip().isdigit():
                port_holder.append(int(line.strip()))
            ready.set()

        reader = threading.Thread(target=read_port, daemon=True)
        reader.start()
        assert ready.wait(10) and port_holder, "Image process failed to start"
        image_port = port_holder[0]
        env["VISION_URL"] = f"http://127.0.0.1:{image_port}"
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            web_port = sock.getsockname()[1]
        with (tmp_path / "web.log").open("w+") as output:
            web = subprocess.Popen(
                [str(Path(".venv/bin/smartline").resolve()), "web", "--port", str(web_port)],
                env=env,
                stdout=output,
                stderr=output,
            )
            base = f"http://127.0.0.1:{web_port}"
            for _ in range(100):
                try:
                    with urlopen(base + "/health", timeout=0.5):
                        break
                except OSError:
                    time.sleep(0.1)
                assert web.poll() is None, "Web process exited"
            else:
                pytest.fail("Web process was not ready")

            def send(path, payload):
                with urlopen(
                    Request(
                        base + path,
                        data=json.dumps(payload).encode(),
                        headers={"Content-Type": "application/json"},
                    ),
                    timeout=10,
                ) as response:
                    return response.status, json.load(response)["data"]

            with urlopen(base + "/api/events", timeout=10) as events:
                while events.readline().strip():
                    pass
                identifier = str(uuid.uuid4())
                status, result = send("/api/inspections/vision", {"requestId": identifier})
                assert status == 201
                assert result["inspection"]["inspectionId"] == identifier
                assert result["inspection"]["observationSource"] == "model"
                assert result["inspection"]["captureSource"] == "esp32_capture_raw"
                assert result["inspection"]["geometry"]["sourceWidth"] == 640
                assert result["inspection"]["boundingBoxes"] == []
                status, retry = send("/api/inspections/vision", {"requestId": identifier})
                assert status == 200 and retry == result
                assert InspectionRecord.objects.count() == 1
                assert ProductTrack.objects.count() == 1
                notifications = []
                while "ReceiveInspectionUpdate" not in notifications:
                    line = events.readline().decode().strip()
                    if line.startswith("event: "):
                        notifications.append(line[7:])
                assert "ReceiveLogUpdate" in notifications
            _, session = send(
                "/api/dataset/sessions",
                {
                    "productClass": "BOX_RED",
                    "qualityLabel": "GOOD",
                    "colorLabel": "RED",
                    "sizeLabel": "LARGE",
                    "captureMode": "manual",
                },
            )
            _, capture = send(
                "/api/dataset/sessions/" + session["sessionId"] + "/captures/camera",
                {
                    "productClass": "BOX_RED",
                    "qualityLabel": "GOOD",
                    "colorLabel": "RED",
                    "sizeLabel": "LARGE",
                },
            )
            assert capture["session"]["imageCount"] == 1
            assert capture["sample"]["sourceWidth"] == 640
            image.terminate()
            image.wait(timeout=5)
            with urlopen(base + "/health", timeout=2) as response:
                assert response.status == 200
            with urlopen(base + "/api/diagnostics", timeout=3) as response:
                assert json.load(response)["data"]["vision"] == "OFFLINE"
            with pytest.raises(HTTPError) as error:
                send("/api/inspections/vision", {"requestId": str(uuid.uuid4())})
            assert error.value.code == 503
            assert InspectionRecord.objects.count() == 1
    finally:
        for process in [web, image]:
            if process and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


@pytest.mark.asyncio
async def test_sse_event_names_and_payloads():
    from smartline.web.events import bus

    async with bus.subscribe() as queue:
        for name in [
            "ReceiveAlertUpdate",
            "ReceiveConveyorUpdate",
            "ReceiveEnvironmentUpdate",
            "ReceiveInspectionUpdate",
            "ReceiveLogUpdate",
            "ReceiveProductUpdate",
            "ReceiveTrackingUpdate",
        ]:
            bus.publish(name, {"value": 1})
            message = await queue.get()
            assert f"event: {name}".encode() in message
            assert b'"value": 1' in message


@pytest.mark.asyncio
async def test_client_cancellation_does_not_abort_an_authorized_emergency(app, mqtt, monkeypatch):
    import asyncio
    from contextlib import suppress
    from django.test import AsyncClient
    from django.db import close_old_connections
    from smartline.devices import service
    from smartline.devices.models import ConveyorSetting

    entered, release, completed = threading.Event(), threading.Event(), threading.Event()
    finished = threading.Event()
    original = mqtt.state
    original_control = service.control

    def controlled(*args, **kwargs):
        try:
            return original_control(*args, **kwargs)
        finally:
            close_old_connections()
            finished.set()

    def blocked(*args):
        entered.set()
        assert release.wait(5)
        original(*args)
        completed.set()

    monkeypatch.setattr(mqtt, "state", blocked)
    monkeypatch.setattr(service, "control", controlled)
    request = asyncio.create_task(
        AsyncClient().post(
            "/api/conveyor/emergency-stop", data="{}", content_type="application/json"
        )
    )
    try:
        assert await asyncio.to_thread(entered.wait, 5)
        request.cancel()
        release.set()
        with suppress(asyncio.CancelledError):
            await request
        assert await asyncio.to_thread(completed.wait, 5)
        assert await asyncio.to_thread(finished.wait, 5)
        for _ in range(50):

            def committed():
                from django.db import close_old_connections

                try:
                    return ConveyorSetting.objects.get().is_emergency_stopped
                finally:
                    close_old_connections()

            if await asyncio.to_thread(committed):
                break
            await asyncio.sleep(0.02)
        else:
            pytest.fail("Emergency transition was cancelled with the HTTP connection")
    finally:
        release.set()
