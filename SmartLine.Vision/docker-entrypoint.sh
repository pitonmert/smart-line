#!/bin/sh
set -eu

source_dir=/run/smartline/cert-source
target_dir=/run/smartline/certs
runtime_user=smartline
runtime_group=smartline

require_file() {
    if [ ! -r "$1" ]; then
        echo "Required MQTT TLS file is missing or unreadable: $1" >&2
        exit 1
    fi
}

require_file "$source_dir/ca.crt"
require_file "$source_dir/smartline-vision.crt"
require_file "$source_dir/smartline-vision.key"

install -d -m 0700 -o "$runtime_user" -g "$runtime_group" "$target_dir"
install -m 0444 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/ca.crt" "$target_dir/ca.crt"
install -m 0444 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/smartline-vision.crt" "$target_dir/smartline-vision.crt"
install -m 0400 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/smartline-vision.key" "$target_dir/smartline-vision.key"

exec gosu "$runtime_user:$runtime_group" "$@"
