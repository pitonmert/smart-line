import json
import logging
import os
import ssl

from paho.mqtt import client as mqtt
from paho.mqtt.enums import CallbackAPIVersion


log = logging.getLogger("smartline.mqtt")

BROKER_HOST = os.environ.get("MQTT_BROKER_HOST", "localhost")
BROKER_PORT = int(os.environ.get("MQTT_BROKER_PORT", "8883"))
CLIENT_ID = os.environ.get("MQTT_CLIENT_ID", "smartline-vision")
USERNAME = os.environ.get("MQTT_USERNAME", "smartline-vision")
PASSWORD = os.environ.get("MQTT_PASSWORD", "")
CA_CERT_PATH = os.environ.get("MQTT_CA_CERT_PATH", "/run/smartline/certs/ca.crt")
CLIENT_CERT_PATH = os.environ.get("MQTT_CLIENT_CERT_PATH", "")
CLIENT_KEY_PATH = os.environ.get("MQTT_CLIENT_KEY_PATH", "")
TOPIC_BARCODE = os.environ.get("TOPIC_BARCODE", "smartline/sensors/barcode")

client = mqtt.Client(CallbackAPIVersion.VERSION2, client_id=CLIENT_ID)


def on_connect(client, userdata, flags, reason_code, properties):
    if reason_code == 0:
        log.info("Connected to MQTT broker %s:%s with TLS", BROKER_HOST, BROKER_PORT)
    else:
        log.error("MQTT connection rejected: %s", reason_code)


def on_disconnect(client, userdata, disconnect_flags, reason_code, properties):
    if reason_code != 0:
        log.warning("MQTT connection lost: %s", reason_code)


client.on_connect = on_connect
client.on_disconnect = on_disconnect


def _validate_configuration():
    if len(PASSWORD) < 32 or PASSWORD.lower().startswith("replace-"):
        raise RuntimeError(
            "MQTT_PASSWORD must be a non-placeholder secret of at least 32 characters"
        )
    if BROKER_PORT != 8883:
        raise RuntimeError("MQTT_BROKER_PORT must be 8883")

    required_tls_files = (
        ("MQTT_CA_CERT_PATH", CA_CERT_PATH),
        ("MQTT_CLIENT_CERT_PATH", CLIENT_CERT_PATH),
        ("MQTT_CLIENT_KEY_PATH", CLIENT_KEY_PATH),
    )
    for variable_name, path in required_tls_files:
        if not path:
            raise RuntimeError(f"{variable_name} is required")
        if not os.path.isfile(path) or not os.access(path, os.R_OK):
            raise RuntimeError(f"{variable_name} is missing or unreadable: {path}")


def start_mqtt():
    """Start a TLS-verified asynchronous MQTT connection."""
    _validate_configuration()
    client.username_pw_set(USERNAME, PASSWORD)
    client.tls_set(
        ca_certs=CA_CERT_PATH,
        certfile=CLIENT_CERT_PATH,
        keyfile=CLIENT_KEY_PATH,
        cert_reqs=ssl.CERT_REQUIRED,
        tls_version=ssl.PROTOCOL_TLS_CLIENT,
    )
    client.tls_insecure_set(False)
    client.reconnect_delay_set(min_delay=1, max_delay=30)
    client.connect_async(BROKER_HOST, BROKER_PORT, keepalive=30)
    client.loop_start()


def stop_mqtt():
    """Stop the network loop and close the broker connection."""
    client.disconnect()
    client.loop_stop()


def publish_barcode(barcode_value: str) -> bool:
    """Publish a decoded barcode with at-least-once delivery."""
    if not client.is_connected():
        log.warning("Barcode not published because MQTT is disconnected")
        return False

    payload = json.dumps({"barcode": barcode_value, "color": None})
    result = client.publish(TOPIC_BARCODE, payload, qos=1, retain=False)
    if result.rc != mqtt.MQTT_ERR_SUCCESS:
        log.error("Barcode publish failed with MQTT result code %s", result.rc)
        return False

    log.info("Barcode published to %s", TOPIC_BARCODE)
    return True
