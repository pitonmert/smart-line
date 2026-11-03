import copy
from io import BytesIO

import pytest
from PIL import Image


@pytest.fixture
def recipe():
    from smartline.inspection.recipes import contract

    return contract("recipes/demobox.json")


@pytest.fixture
def observation():
    return {
        "productClass": "BOX_GREEN",
        "colorClass": "GREEN",
        "sizeClass": "SMALL",
        "confidence": 0.9,
    }


class RecordingMqtt:
    connected = True

    def __init__(self):
        self.records = []
        self.failure = False
        self.last_heartbeat = None

    def publish(self, topic, payload, retain=False):
        from smartline.web.errors import ApiError

        if self.failure:
            raise ApiError("MQTT unavailable", "MQTT_UNAVAILABLE", 503)
        self.records.append((topic, copy.deepcopy(payload), retain))

    def state(self, running, emergency, generation, epoch, fire_generation):
        self.publish(
            "smartline/commands/stop",
            {
                "isRunning": running,
                "emergency": emergency,
                "resetGeneration": generation,
                "acknowledgedFireEpoch": epoch,
                "acknowledgedFireGeneration": fire_generation,
            },
            True,
        )


@pytest.fixture
def mqtt(monkeypatch):
    from smartline.devices import mqtt as module, service
    from smartline.web import monitoring

    fake = RecordingMqtt()
    monkeypatch.setattr(module, "transport", fake)
    monkeypatch.setattr(service, "transport", fake)
    monkeypatch.setattr(monitoring, "transport", fake)
    return fake


@pytest.fixture
def app(transactional_db, settings, tmp_path, mqtt):
    from smartline.inspection.recipes import seed
    from smartline.inspection.workflow import simulation_latch

    settings.DATASET_ROOT = tmp_path / "datasets"
    settings.SIMULATION_ENABLED = True
    settings.MOCK = False
    simulation_latch.clear()
    seed()
    yield
    simulation_latch.clear()


@pytest.fixture
def jpeg():
    stream = BytesIO()
    Image.new("RGB", (16, 12), (230, 20, 20)).save(stream, "JPEG")
    return stream.getvalue()


@pytest.fixture
def frame(jpeg):
    return {
        "jpeg": jpeg,
        "width": 16,
        "height": 12,
        "role": "dataset",
        "origin": "still_capture",
        "captureSource": "esp32_capture_raw",
        "previewFallback": False,
    }
