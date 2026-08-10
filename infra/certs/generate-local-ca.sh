#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir=${SMARTLINE_CERT_OUTPUT_DIR:-"$script_dir/generated"}
lan_ip=${SMARTLINE_LAN_IP:-}

if [ -z "$lan_ip" ]; then
    echo "SMARTLINE_LAN_IP is required (for example 192.168.1.20)." >&2
    exit 1
fi

if ! printf '%s\n' "$lan_ip" | awk -F. '
    NF != 4 { exit 1 }
    {
        for (octet = 1; octet <= 4; octet++) {
            if ($octet !~ /^[0-9]+$/ || $octet < 0 || $octet > 255) {
                exit 1
            }
        }
    }
'; then
    echo "SMARTLINE_LAN_IP must be a valid IPv4 address." >&2
    exit 1
fi

if [ -e "$output_dir/ca.key" ] && [ "${1:-}" != "--force" ]; then
    echo "Certificates already exist in $output_dir; use --force to replace them." >&2
    exit 1
fi

umask 077
mkdir -p "$output_dir"
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT INT TERM

ca_config_file="$temporary_dir/ca.cnf"
{
    echo "[req]"
    echo "distinguished_name=req_distinguished_name"
    echo "x509_extensions=v3_ca"
    echo "prompt=no"
    echo "[req_distinguished_name]"
    echo "CN=SmartLine Local CA"
    echo "O=SmartLine"
    echo "[v3_ca]"
    echo "subjectKeyIdentifier=hash"
    echo "authorityKeyIdentifier=keyid:always,issuer"
    echo "basicConstraints=critical,CA:TRUE"
    echo "keyUsage=critical,keyCertSign,cRLSign"
} > "$ca_config_file"

openssl genrsa -out "$output_dir/ca.key" 4096
openssl req -x509 -new -sha256 -days 3650 \
    -key "$output_dir/ca.key" \
    -out "$output_dir/ca.crt" \
    -config "$ca_config_file"

issue_server_certificate() {
    certificate_name="$1"
    common_name="$2"
    subject_alt_names="$3"
    config_file="$temporary_dir/$certificate_name.cnf"

    {
        echo "[req]"
        echo "distinguished_name=req_distinguished_name"
        echo "req_extensions=req_ext"
        echo "prompt=no"
        echo "[req_distinguished_name]"
        echo "CN=$common_name"
        echo "O=SmartLine"
        echo "[req_ext]"
        echo "subjectAltName=$subject_alt_names"
        echo "[cert_ext]"
        echo "subjectAltName=$subject_alt_names"
        echo "basicConstraints=critical,CA:FALSE"
        echo "keyUsage=critical,digitalSignature,keyEncipherment"
        echo "extendedKeyUsage=serverAuth"
        echo "subjectKeyIdentifier=hash"
        echo "authorityKeyIdentifier=keyid,issuer"
    } > "$config_file"

    openssl genrsa -out "$output_dir/$certificate_name.key" 3072
    openssl req -new \
        -key "$output_dir/$certificate_name.key" \
        -out "$temporary_dir/$certificate_name.csr" \
        -config "$config_file"
    openssl x509 -req -sha256 -days 825 \
        -in "$temporary_dir/$certificate_name.csr" \
        -CA "$output_dir/ca.crt" \
        -CAkey "$output_dir/ca.key" \
        -CAcreateserial \
        -out "$output_dir/$certificate_name.crt" \
        -extensions cert_ext \
        -extfile "$config_file"
}

issue_client_certificate() {
    certificate_name="$1"
    common_name="$2"
    config_file="$temporary_dir/$certificate_name.cnf"

    {
        echo "[req]"
        echo "distinguished_name=req_distinguished_name"
        echo "prompt=no"
        echo "[req_distinguished_name]"
        echo "CN=$common_name"
        echo "O=SmartLine"
        echo "[cert_ext]"
        echo "basicConstraints=critical,CA:FALSE"
        echo "keyUsage=critical,digitalSignature,keyEncipherment"
        echo "extendedKeyUsage=clientAuth"
        echo "subjectKeyIdentifier=hash"
        echo "authorityKeyIdentifier=keyid,issuer"
    } > "$config_file"

    openssl genrsa -out "$output_dir/$certificate_name.key" 3072
    openssl req -new \
        -key "$output_dir/$certificate_name.key" \
        -out "$temporary_dir/$certificate_name.csr" \
        -config "$config_file"
    openssl x509 -req -sha256 -days 825 \
        -in "$temporary_dir/$certificate_name.csr" \
        -CA "$output_dir/ca.crt" \
        -CAkey "$output_dir/ca.key" \
        -CAcreateserial \
        -out "$output_dir/$certificate_name.crt" \
        -extensions cert_ext \
        -extfile "$config_file"
}

issue_server_certificate \
    mosquitto \
    mosquitto \
    "DNS:mosquitto,DNS:localhost,IP:127.0.0.1,IP:$lan_ip"
issue_server_certificate \
    smartline.local \
    smartline.local \
    "DNS:smartline.local,IP:127.0.0.1,IP:$lan_ip"

issue_client_certificate smartline-api smartline-api
issue_client_certificate smartline-firmware smartline-firmware
issue_client_certificate smartline-vision smartline-vision

chmod 0600 "$output_dir"/*.key
chmod 0644 "$output_dir"/*.crt

echo "Certificates created in $output_dir"
echo "Install ca.crt as a trusted root CA on dashboard clients."
echo "Distribute each MQTT client only its own certificate, private key, and ca.crt."
echo "Do not commit generated private keys or environment credentials."
