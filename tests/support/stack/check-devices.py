"""Exercise production MQTT routing and conveyor safety over real transports."""

import argparse
import json
import os
from pathlib import Path
import ssl
import threading
import time
from urllib.request import Request, urlopen

from paho.mqtt import client as mqtt

parser = argparse.ArgumentParser()
parser.add_argument("--replay", action="store_true")
args = parser.parse_args()
certs = Path("/test-certs")
context = ssl.create_default_context(cafile=str(certs / "ca.crt"))
messages = []
ready = threading.Event()


def wait(predicate):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.05)
    raise AssertionError("Device integration condition timed out")


def api(path, payload=None):
    request = Request(
        "https://smartline.local:8443/api/" + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Content-Type": "application/json"} if payload is not None else {},
    )
    with urlopen(request, context=context, timeout=5) as response:
        envelope = json.load(response)
        assert envelope["isSuccess"], envelope
        return envelope["data"]


client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="smartline-stack-firmware")
client.username_pw_set("smartline-firmware", os.environ["MQTT_FIRMWARE_PASSWORD"])
client.tls_set(
    ca_certs=str(certs / "ca.crt"),
    certfile=str(certs / "smartline-firmware.crt"),
    keyfile=str(certs / "smartline-firmware.key"),
)


def connected(connection, userdata, flags, reason, properties):
    assert not reason.is_failure, str(reason)
    connection.subscribe("smartline/commands/#", qos=1)
    ready.set()


def received(connection, userdata, message):
    if message.topic == "smartline/commands/stop":
        messages.append(json.loads(message.payload))


client.on_connect = connected
client.on_message = received
try:
    client.connect("mosquitto", 8883)
    client.loop_start()
    assert ready.wait(10)
    if args.replay:
        wait(lambda: bool(messages))
        state = messages[-1]
        assert state["isRunning"] is False and state["emergency"] is False, state
        assert state["resetGeneration"] >= 2 and state["acknowledgedFireGeneration"] == 1, state
        print("Broker restart: retained desired state replayed.", flush=True)
    else:

        def publish(topic, payload):
            client.publish(
                "smartline/sensors/" + topic, json.dumps(payload), qos=1
            ).wait_for_publish(5)

        publish("environment", {"temperature": 26.5, "humidity": 45})
        wait(lambda: api("conveyor/status")["temperature"] == 26.5)
        epoch = "a" * 32
        publish("fire", {"isDetected": False, "deviceEpoch": epoch, "fireGeneration": 0})
        wait(lambda: api("conveyor/status")["fireDeviceEpoch"] == epoch)
        assert api("conveyor/status")["isEmergencyStopped"]
        api("conveyor/emergency-reset", {})
        api("conveyor/start", {})
        wait(lambda: any(m["isRunning"] and not m["emergency"] for m in messages))
        publish("fire", {"isDetected": True, "deviceEpoch": epoch, "fireGeneration": 1})
        wait(lambda: api("conveyor/status")["isFireDetected"])
        wait(lambda: any(m["emergency"] and m["acknowledgedFireGeneration"] == 1 for m in messages))
        publish("fire", {"isDetected": False, "deviceEpoch": epoch, "fireGeneration": 1})
        wait(lambda: not api("conveyor/status")["isFireDetected"])
        assert api("conveyor/status")["isEmergencyStopped"]
        api("conveyor/emergency-reset", {})
        assert not api("conveyor/status")["isRunning"]
        print("MQTT telemetry, fire, emergency latch/reset and firmware commands: OK", flush=True)
finally:
    client.disconnect()
    client.loop_stop()
