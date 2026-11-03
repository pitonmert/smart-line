#!/bin/sh
set -eu

if [ "$(id -u)" = 0 ]; then
    mkdir -p /run/smartline/certs
    chown smartline:smartline /run/smartline/certs
    chmod 700 /run/smartline/certs
    if [ -d /run/smartline/cert-source ]; then
        for source in /run/smartline/cert-source/*; do
            [ -f "$source" ] || continue
            destination="/run/smartline/certs/$(basename "$source")"
            cp "$source" "$destination"
            chmod 600 "$destination"
            chown smartline:smartline "$destination"
        done
    fi
    if [ -n "${DATASET_ROOT:-}" ]; then
        mkdir -p "$DATASET_ROOT"
        chown smartline:smartline "$DATASET_ROOT"
    fi
    exec gosu smartline "$@"
fi

exec "$@"
