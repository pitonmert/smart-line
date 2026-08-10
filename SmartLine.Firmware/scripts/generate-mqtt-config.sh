#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
firmware_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
repository_dir=$(CDPATH= cd -- "$firmware_dir/.." && pwd)
ca_certificate="$repository_dir/infra/certs/generated/ca.crt"
client_certificate="$repository_dir/infra/certs/generated/smartline-firmware.crt"
client_private_key="$repository_dir/infra/certs/generated/smartline-firmware.key"
output_file="$firmware_dir/include/MqttTlsConfig.h"
force=false

usage() {
    cat <<EOF
Usage: $0 [--ca PATH] [--client-cert PATH] [--client-key PATH] [--force]

Defaults:
  --ca           $ca_certificate
  --client-cert  $client_certificate
  --client-key   $client_private_key
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --ca|--client-cert|--client-key)
            if [ "$#" -lt 2 ]; then
                echo "$1 requires a path." >&2
                usage >&2
                exit 1
            fi
            case "$1" in
                --ca) ca_certificate=$2 ;;
                --client-cert) client_certificate=$2 ;;
                --client-key) client_private_key=$2 ;;
            esac
            shift 2
            ;;
        --force)
            force=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if [ -z "${MQTT_FIRMWARE_PASSWORD:-}" ]; then
    echo "MQTT_FIRMWARE_PASSWORD is required." >&2
    exit 1
fi

if [ ! -r "$ca_certificate" ]; then
    echo "CA certificate is missing or unreadable: $ca_certificate" >&2
    exit 1
fi

if [ ! -r "$client_certificate" ]; then
    echo "Firmware client certificate is missing or unreadable: $client_certificate" >&2
    exit 1
fi

if [ ! -r "$client_private_key" ]; then
    echo "Firmware client private key is missing or unreadable: $client_private_key" >&2
    exit 1
fi

if ! grep -q -- "-----BEGIN CERTIFICATE-----" "$ca_certificate" \
    || ! grep -q -- "-----END CERTIFICATE-----" "$ca_certificate"; then
    echo "CA certificate is not a PEM certificate: $ca_certificate" >&2
    exit 1
fi

if ! grep -q -- "-----BEGIN CERTIFICATE-----" "$client_certificate" \
    || ! grep -q -- "-----END CERTIFICATE-----" "$client_certificate"; then
    echo "Firmware client certificate is not a PEM certificate: $client_certificate" >&2
    exit 1
fi

if ! grep -Eq -- "-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----" "$client_private_key" \
    || ! grep -Eq -- "-----END ([A-Z0-9]+ )?PRIVATE KEY-----" "$client_private_key"; then
    echo "Firmware client private key is not a PEM private key: $client_private_key" >&2
    exit 1
fi

case "$MQTT_FIRMWARE_PASSWORD" in
    *')SL_PASSWORD"'*)
        echo "MQTT_FIRMWARE_PASSWORD contains a reserved delimiter." >&2
        exit 1
        ;;
esac

for pem_file in "$ca_certificate" "$client_certificate" "$client_private_key"; do
    if grep -Eq -- '\)(SL_CA|SL_CERT|SL_KEY)"' "$pem_file"; then
        echo "PEM file contains a reserved C++ raw-string delimiter: $pem_file" >&2
        exit 1
    fi
done

if [ -e "$output_file" ] && [ "$force" != true ]; then
    echo "$output_file already exists; pass --force to replace it." >&2
    exit 1
fi

umask 077
temporary_file=$(mktemp)
trap 'rm -f "$temporary_file"' EXIT INT TERM

{
    echo "#ifndef MQTT_TLS_CONFIG_H"
    echo "#define MQTT_TLS_CONFIG_H"
    echo
    echo 'static const char* MQTT_USERNAME = "smartline-firmware";'
    printf 'static const char* MQTT_PASSWORD = R"SL_PASSWORD(%s)SL_PASSWORD";\n' "$MQTT_FIRMWARE_PASSWORD"
    echo 'static const char* NTP_SERVER = "pool.ntp.org";'
    echo
    echo 'static const char MQTT_CA_CERT[] = R"SL_CA('
    sed -n '1,$p' "$ca_certificate"
    echo ')SL_CA";'
    echo
    echo 'static const char MQTT_CLIENT_CERT[] = R"SL_CERT('
    sed -n '1,$p' "$client_certificate"
    echo ')SL_CERT";'
    echo
    echo 'static const char MQTT_CLIENT_PRIVATE_KEY[] = R"SL_KEY('
    sed -n '1,$p' "$client_private_key"
    echo ')SL_KEY";'
    echo
    echo "#endif"
} > "$temporary_file"

install -m 0600 "$temporary_file" "$output_file"
echo "Created ignored firmware TLS configuration: $output_file"
