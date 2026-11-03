"""Exercise the Python MQTT consumer against an isolated mutually authenticated broker."""

import json
import os
import time

os.environ.setdefault("DJANGO_SETTINGS_MODULE", "smartline.config.settings")
import django

django.setup()

from paho.mqtt import client as mqtt  # noqa: E402
from smartline.devices import service  # noqa: E402
from smartline.devices.mqtt import MqttTransport  # noqa: E402

events = []
service.publish_desired_state = lambda: events.append(("connected", None))
service.environment = lambda value: events.append(("environment", value))
service.fire = lambda value: events.append(("fire", value))
transport = MqttTransport()
publisher = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="smartline-test-firmware")


def wait(predicate):
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.02)
    raise AssertionError("MQTT test condition timed out")


try:
    transport.start()
    wait(lambda: sum(e[0] == "connected" for e in events) == 1)
    directory = os.environ["MQTT_TEST_CERT_DIR"]
    publisher.username_pw_set("smartline-firmware", os.environ["MQTT_FIRMWARE_PASSWORD"])
    publisher.tls_set(
        ca_certs=directory + "/ca.crt",
        certfile=directory + "/smartline-firmware.crt",
        keyfile=directory + "/smartline-firmware.key",
    )
    publisher.connect(os.environ.get("MQTT_HOST", "mosquitto"), 8883)
    publisher.loop_start()
    wait(publisher.is_connected)
    info = publisher.publish(
        "smartline/sensors/environment", json.dumps({"temperature": 26.5, "humidity": 45}), qos=1
    )
    info.wait_for_publish(timeout=5)
    wait(lambda: any(e[0] == "environment" for e in events))
    assert next(e[1] for e in events if e[0] == "environment") == {
        "temperature": 26.5,
        "humidity": 45,
    }
    publisher.publish(
        "smartline/sensors/fire",
        json.dumps({"isDetected": False, "deviceEpoch": "a" * 32, "fireGeneration": 0}),
        qos=1,
    ).wait_for_publish(timeout=5)
    wait(lambda: any(e[0] == "fire" for e in events))
    transport.client.disconnect()
    wait(lambda: not transport.connected)
    transport.client.reconnect()
    # Paho's network loop exits after an intentional disconnect.
    transport.client.loop_stop()
    transport.client.loop_start()
    wait(lambda: sum(e[0] == "connected" for e in events) == 2)
    print("Python MQTT delivery and reconnect checks passed.")
finally:
    publisher.disconnect()
    publisher.loop_stop()
    transport.stop()
