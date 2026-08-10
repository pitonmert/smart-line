#!/bin/sh
set -eu

output_dir=${SMARTLINE_MQTT_TEST_CERT_DIR:-/tmp/smartline-mqtt-test-certs}

umask 077
mkdir -p "$output_dir"

openssl req -x509 -newkey rsa:2048 -sha256 -days 1 -nodes \
    -keyout "$output_dir/untrusted-client.key" \
    -out "$output_dir/untrusted-client.crt" \
    -subj "/CN=SmartLine Untrusted Test Client/O=SmartLine Tests"

chmod 0600 "$output_dir/untrusted-client.key"
chmod 0644 "$output_dir/untrusted-client.crt"

echo "Disposable untrusted MQTT client certificate created in $output_dir"
