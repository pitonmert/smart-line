#!/bin/sh
set -eu

broker_host=${MQTT_BROKER_HOST:-mosquitto}
broker_port=${MQTT_BROKER_PORT:-8883}
cert_dir=${MQTT_TEST_CERT_DIR:-/test/certs}
ca_file="$cert_dir/ca.crt"

: "${MQTT_API_PASSWORD:?MQTT_API_PASSWORD is required}"
: "${MQTT_FIRMWARE_PASSWORD:?MQTT_FIRMWARE_PASSWORD is required}"
: "${MQTT_VISION_PASSWORD:?MQTT_VISION_PASSWORD is required}"

for certificate_name in \
    smartline-api \
    smartline-firmware \
    smartline-vision \
    untrusted-client
do
    if [ ! -r "$cert_dir/$certificate_name.crt" ] || \
        [ ! -r "$cert_dir/$certificate_name.key" ]; then
        echo "Missing MQTT test identity: $certificate_name" >&2
        exit 1
    fi
done

if [ ! -r "$ca_file" ]; then
    echo "Missing MQTT broker CA certificate: $ca_file" >&2
    exit 1
fi

mqtt_publish_with_certificate() {
    username=$1
    password=$2
    certificate_name=$3
    topic=$4
    payload=$5

    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        --cert "$cert_dir/$certificate_name.crt" \
        --key "$cert_dir/$certificate_name.key" \
        -V mqttv5 -q 1 -u "$username" -P "$password" \
        -t "$topic" -m "$payload"
}

mqtt_publish() {
    username=$1
    password=$2
    topic=$3
    payload=$4

    mqtt_publish_with_certificate \
        "$username" "$password" "$username" "$topic" "$payload"
}

mqtt_subscribe_with_certificate() {
    username=$1
    password=$2
    certificate_name=$3
    topic=$4
    wait_seconds=$5

    mosquitto_sub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        --cert "$cert_dir/$certificate_name.crt" \
        --key "$cert_dir/$certificate_name.key" \
        -V mqttv5 -q 1 -u "$username" -P "$password" \
        -t "$topic" -C 1 -W "$wait_seconds"
}

mqtt_subscribe() {
    username=$1
    password=$2
    topic=$3
    wait_seconds=$4

    mqtt_subscribe_with_certificate \
        "$username" "$password" "$username" "$topic" "$wait_seconds"
}

expect_connection_rejected() {
    description=$1
    shift

    if "$@"; then
        echo "MQTT connection unexpectedly succeeded: $description" >&2
        exit 1
    fi
}

expect_delivery_with_certificates() {
    subscriber_username=$1
    subscriber_password=$2
    subscriber_certificate=$3
    topic=$4
    publisher_username=$5
    publisher_password=$6
    publisher_certificate=$7
    payload=$8
    output_file=$(mktemp)

    mqtt_subscribe_with_certificate \
        "$subscriber_username" "$subscriber_password" \
        "$subscriber_certificate" "$topic" 5 > "$output_file" &
    subscriber_pid=$!
    sleep 1

    mqtt_publish_with_certificate \
        "$publisher_username" "$publisher_password" \
        "$publisher_certificate" "$topic" "$payload"

    if ! wait "$subscriber_pid"; then
        rm -f "$output_file"
        echo "Allowed MQTT delivery failed: $publisher_username -> $subscriber_username on $topic" >&2
        exit 1
    fi

    if ! grep -Fx "$payload" "$output_file" >/dev/null; then
        rm -f "$output_file"
        echo "Allowed MQTT payload was not received on $topic" >&2
        exit 1
    fi

    rm -f "$output_file"
}

expect_delivery() {
    subscriber_username=$1
    subscriber_password=$2
    topic=$3
    publisher_username=$4
    publisher_password=$5
    payload=$6

    expect_delivery_with_certificates \
        "$subscriber_username" "$subscriber_password" "$subscriber_username" \
        "$topic" \
        "$publisher_username" "$publisher_password" "$publisher_username" \
        "$payload"
}

expect_publish_denied_with_certificate() {
    publisher_username=$1
    publisher_password=$2
    publisher_certificate=$3
    topic=$4
    authorized_subscriber_username=$5
    authorized_subscriber_password=$6
    authorized_subscriber_certificate=$7
    output_file=$(mktemp)

    mqtt_subscribe_with_certificate \
        "$authorized_subscriber_username" \
        "$authorized_subscriber_password" \
        "$authorized_subscriber_certificate" \
        "$topic" 2 > "$output_file" &
    subscriber_pid=$!
    sleep 1

    mqtt_publish_with_certificate \
        "$publisher_username" "$publisher_password" "$publisher_certificate" \
        "$topic" denied || true

    if wait "$subscriber_pid" || [ -s "$output_file" ]; then
        rm -f "$output_file"
        echo "Forbidden MQTT publish reached a subscriber: $publisher_username -> $topic" >&2
        exit 1
    fi

    rm -f "$output_file"
}

expect_publish_denied() {
    publisher_username=$1
    publisher_password=$2
    topic=$3
    authorized_subscriber_username=$4
    authorized_subscriber_password=$5

    expect_publish_denied_with_certificate \
        "$publisher_username" "$publisher_password" "$publisher_username" \
        "$topic" \
        "$authorized_subscriber_username" "$authorized_subscriber_password" \
        "$authorized_subscriber_username"
}

expect_subscribe_denied() {
    subscriber_username=$1
    subscriber_password=$2
    topic=$3
    authorized_publisher_username=$4
    authorized_publisher_password=$5
    payload=$6
    output_file=$(mktemp)

    mqtt_subscribe \
        "$subscriber_username" "$subscriber_password" \
        "$topic" 2 > "$output_file" 2>&1 &
    subscriber_pid=$!
    sleep 1

    mqtt_publish \
        "$authorized_publisher_username" "$authorized_publisher_password" \
        "$topic" "$payload"

    wait "$subscriber_pid" || true
    if grep -Fx "$payload" "$output_file" >/dev/null; then
        rm -f "$output_file"
        echo "Forbidden MQTT subscription received a message: $subscriber_username <- $topic" >&2
        exit 1
    fi

    rm -f "$output_file"
}

# Authentication is deliberately two-factor: a CA-trusted client certificate
# and a valid MQTT username/password are both required.
expect_connection_rejected \
    "valid credentials without a client certificate" \
    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        -V mqttv5 -q 1 \
        -u smartline-api -P "$MQTT_API_PASSWORD" \
        -t smartline/commands/speed -m missing-certificate

expect_connection_rejected \
    "untrusted client certificate" \
    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        --cert "$cert_dir/untrusted-client.crt" \
        --key "$cert_dir/untrusted-client.key" \
        -V mqttv5 -q 1 \
        -u smartline-api -P "$MQTT_API_PASSWORD" \
        -t smartline/commands/speed -m untrusted-certificate

expect_connection_rejected \
    "trusted client certificate without MQTT credentials" \
    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        --cert "$cert_dir/smartline-api.crt" \
        --key "$cert_dir/smartline-api.key" \
        -V mqttv5 -q 1 \
        -t smartline/commands/speed -m anonymous

expect_connection_rejected \
    "trusted client certificate with a wrong password" \
    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" --cafile "$ca_file" \
        --cert "$cert_dir/smartline-api.crt" \
        --key "$cert_dir/smartline-api.key" \
        -V mqttv5 -q 1 \
        -u smartline-api -P "${MQTT_API_PASSWORD}-wrong" \
        -t smartline/commands/speed -m wrong-password

expect_connection_rejected \
    "plaintext connection to the TLS listener" \
    mosquitto_pub \
        -h "$broker_host" -p "$broker_port" \
        -V mqttv5 -q 1 \
        -u smartline-api -P "$MQTT_API_PASSWORD" \
        -t smartline/commands/speed -m plaintext

# Allowed paths: API publishes every command family and subscribes to sensors;
# firmware publishes environment/fire and subscribes to actuator commands;
# vision publishes barcodes and intentionally has no subscribe permission.
expect_delivery \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline/commands/speed \
    smartline-api "$MQTT_API_PASSWORD" api-speed-command
expect_delivery \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline/commands/stop \
    smartline-api "$MQTT_API_PASSWORD" api-stop-command
expect_delivery \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline/commands/fan \
    smartline-api "$MQTT_API_PASSWORD" api-fan-command
expect_delivery \
    smartline-api "$MQTT_API_PASSWORD" smartline/sensors/environment \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" firmware-environment
expect_delivery \
    smartline-api "$MQTT_API_PASSWORD" smartline/sensors/fire \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" firmware-fire
expect_delivery \
    smartline-api "$MQTT_API_PASSWORD" smartline/sensors/barcode \
    smartline-vision "$MQTT_VISION_PASSWORD" vision-barcode

# Mosquitto checks certificate trust and MQTT credentials independently when
# use_identity_as_username is disabled. Authorization must remain username-based.
expect_delivery_with_certificates \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline-firmware \
    smartline/commands/speed \
    smartline-api "$MQTT_API_PASSWORD" smartline-vision \
    trusted-cross-certificate-api-command

expect_publish_denied_with_certificate \
    smartline-vision "$MQTT_VISION_PASSWORD" smartline-api \
    smartline/commands/speed \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline-firmware

# Every authenticated principal is also checked against forbidden publish and
# subscribe paths so a future ACL broadening fails CI.
expect_publish_denied \
    smartline-api "$MQTT_API_PASSWORD" smartline/sensors/fire \
    smartline-api "$MQTT_API_PASSWORD"
expect_subscribe_denied \
    smartline-api "$MQTT_API_PASSWORD" smartline/commands/speed \
    smartline-api "$MQTT_API_PASSWORD" api-forbidden-subscribe

expect_publish_denied \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline/commands/speed \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD"
expect_subscribe_denied \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" smartline/sensors/barcode \
    smartline-vision "$MQTT_VISION_PASSWORD" firmware-forbidden-subscribe

expect_publish_denied \
    smartline-vision "$MQTT_VISION_PASSWORD" smartline/sensors/environment \
    smartline-api "$MQTT_API_PASSWORD"
expect_subscribe_denied \
    smartline-vision "$MQTT_VISION_PASSWORD" smartline/sensors/environment \
    smartline-firmware "$MQTT_FIRMWARE_PASSWORD" vision-forbidden-subscribe

echo "Mosquitto mutual TLS, password authentication, and topic ACL checks passed."
