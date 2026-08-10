#!/bin/sh
set -eu

require_secret() {
    variable_name="$1"
    eval "variable_value=\${$variable_name:-}"
    if [ -z "$variable_value" ]; then
        echo "Required environment variable is missing: $variable_name" >&2
        exit 1
    fi
    case "$variable_value" in
        replace-*|REPLACE_*)
            echo "Placeholder value is not allowed for: $variable_name" >&2
            exit 1
            ;;
    esac
    if [ "${#variable_value}" -lt 32 ]; then
        echo "Secret must contain at least 32 characters: $variable_name" >&2
        exit 1
    fi
}

require_file() {
    if [ ! -r "$1" ]; then
        echo "Required certificate file is missing or unreadable: $1" >&2
        exit 1
    fi
}

require_secret MQTT_API_PASSWORD
require_secret MQTT_FIRMWARE_PASSWORD
require_secret MQTT_VISION_PASSWORD

require_file /mosquitto/cert-source/ca.crt
require_file /mosquitto/cert-source/mosquitto.crt
require_file /mosquitto/cert-source/mosquitto.key

install -d -m 0700 /mosquitto/runtime/certs
install -m 0644 /mosquitto/cert-source/ca.crt /mosquitto/runtime/certs/ca.crt
install -m 0644 /mosquitto/cert-source/mosquitto.crt /mosquitto/runtime/certs/mosquitto.crt
install -m 0600 /mosquitto/cert-source/mosquitto.key /mosquitto/runtime/certs/mosquitto.key

password_file=/mosquitto/runtime/passwords
mosquitto_passwd -H sha512-pbkdf2 -b -c "$password_file" smartline-api "$MQTT_API_PASSWORD"
mosquitto_passwd -H sha512-pbkdf2 -b "$password_file" smartline-firmware "$MQTT_FIRMWARE_PASSWORD"
mosquitto_passwd -H sha512-pbkdf2 -b "$password_file" smartline-vision "$MQTT_VISION_PASSWORD"
chmod 0600 "$password_file"

chown -R mosquitto:mosquitto /mosquitto/runtime /mosquitto/data
exec /usr/sbin/mosquitto -c /mosquitto/config/mosquitto.conf
