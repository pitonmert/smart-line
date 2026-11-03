"""MQTT transport; persistence and notifications belong to the web process."""

import json
import logging
import os
import queue
import ssl
import threading
import time

from django.db import close_old_connections
from paho.mqtt import client as mqtt

from smartline.web.errors import ApiError

log = logging.getLogger(__name__)


class MqttTransport:
    def __init__(self):
        self.client = None
        self.messages = queue.Queue(maxsize=1000)
        self.stopping = threading.Event()
        self.thread = None
        self.last_heartbeat = None

    @property
    def connected(self):
        return bool(self.client and self.client.is_connected())

    def publish(self, topic, payload, retain=False):
        if not self.connected:
            raise ApiError("MQTT broker bağlantısı kullanılamıyor", "MQTT_UNAVAILABLE", 503)
        try:
            info = self.client.publish(topic, json.dumps(payload), qos=1, retain=retain)
            info.wait_for_publish(timeout=5)
            if info.rc != mqtt.MQTT_ERR_SUCCESS or not info.is_published():
                raise RuntimeError("MQTT publish was not acknowledged")
        except Exception as error:
            raise ApiError("MQTT broker komutu teslim edemedi", "MQTT_UNAVAILABLE", 503) from error

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
            retain=True,
        )

    def start(self):
        password = os.getenv("MQTT_PASSWORD", "")
        if len(password) < 32 or password.lower().startswith("replace-"):
            raise RuntimeError(
                "MQTT_PASSWORD must contain a non-placeholder secret of at least 32 characters"
            )
        if int(os.getenv("MQTT_PORT", "8883")) != 8883:
            raise RuntimeError("MQTT_PORT must be 8883")
        tls = [
            os.getenv(k, "")
            for k in ["MQTT_CA_CERT_PATH", "MQTT_CLIENT_CERT_PATH", "MQTT_CLIENT_KEY_PATH"]
        ]
        if any(not p or not os.path.isfile(p) for p in tls):
            raise RuntimeError("MQTT CA and client certificate/key files are required")
        self.client = mqtt.Client(
            mqtt.CallbackAPIVersion.VERSION2, client_id=os.getenv("MQTT_CLIENT_ID", "smartline-api")
        )
        self.client.username_pw_set(os.getenv("MQTT_USERNAME", "smartline-api"), password)
        self.client.tls_set(
            ca_certs=tls[0],
            certfile=tls[1],
            keyfile=tls[2],
            cert_reqs=ssl.CERT_REQUIRED,
            tls_version=ssl.PROTOCOL_TLS_CLIENT,
        )
        self.client.reconnect_delay_set(min_delay=1, max_delay=30)

        def connected(client, userdata, flags, reason_code, properties):
            if reason_code == 0:
                client.subscribe("smartline/sensors/#", qos=1)
                self.messages.put(("__connected__", {}))

        def received(client, userdata, message):
            self.messages.put((message.topic, message.payload))

        self.client.on_connect = connected
        self.client.on_message = received
        self.stopping.clear()
        self.thread = threading.Thread(target=self._process, name="smartline-sensors", daemon=True)
        self.thread.start()
        self.client.connect_async(os.getenv("MQTT_HOST", "localhost"), 8883, keepalive=30)
        self.client.loop_start()

    def _process(self):
        from . import service

        while not self.stopping.is_set():
            try:
                topic, payload = self.messages.get(timeout=0.2)
            except queue.Empty:
                continue
            close_old_connections()
            try:
                if topic == "__connected__":
                    service.publish_desired_state()
                else:
                    payload = json.loads(payload)
                    if not isinstance(payload, dict):
                        raise ValueError("MQTT payload must be an object")
                    payload = {
                        next(
                            (
                                k
                                for k in [
                                    "temperature",
                                    "humidity",
                                    "isDetected",
                                    "deviceEpoch",
                                    "fireGeneration",
                                    "barcode",
                                    "color",
                                ]
                                if k.lower() == key.lower()
                            ),
                            key,
                        ): v
                        for key, v in payload.items()
                    }
                    if topic == "smartline/sensors/environment":
                        service.environment(payload)
                        self.last_heartbeat = time.monotonic()
                    elif topic == "smartline/sensors/fire":
                        service.fire(payload)
                        self.last_heartbeat = time.monotonic()
                    elif topic == "smartline/sensors/barcode":
                        from smartline.inspection.production import barcode

                        barcode(payload)
            except Exception:
                log.exception("MQTT message processing failed: %s", topic)
            finally:
                close_old_connections()
                self.messages.task_done()

    def stop(self):
        self.stopping.set()
        if self.client:
            self.client.disconnect()
            self.client.loop_stop()
        if self.thread:
            self.thread.join(timeout=6)


transport = MqttTransport()
