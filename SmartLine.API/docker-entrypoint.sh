#!/bin/sh
set -eu

source_dir=/run/smartline/cert-source
target_dir=/run/smartline/certs
runtime_user=${APP_UID:?APP_UID is required}
runtime_group=$APP_UID

require_file() {
    if [ ! -r "$1" ]; then
        echo "Required MQTT TLS file is missing or unreadable: $1" >&2
        exit 1
    fi
}

require_file "$source_dir/ca.crt"
require_file "$source_dir/smartline-api.crt"
require_file "$source_dir/smartline-api.key"

install -d -m 0700 -o "$runtime_user" -g "$runtime_group" "$target_dir"
install -m 0444 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/ca.crt" "$target_dir/ca.crt"
install -m 0444 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/smartline-api.crt" "$target_dir/smartline-api.crt"
install -m 0400 -o "$runtime_user" -g "$runtime_group" \
    "$source_dir/smartline-api.key" "$target_dir/smartline-api.key"

# Development-only named volumes start as root-owned. The directories are
# absent from the production image, so this has no production-side effect.
for writable_dir in /home/app/.nuget/packages /src/bin /src/obj; do
    if [ -d "$writable_dir" ]; then
        chown "$runtime_user:$runtime_group" "$writable_dir"
    fi
done

exec gosu "$runtime_user:$runtime_group" "$@"
