"""Receive the development TCP diagnostic on port 9001.

This script does not flash firmware and does not open the camera. The normal
firmware build does not listen on this port. A timeout is an incomplete
result, not a throughput claim.
"""

from __future__ import annotations

import argparse
import socket
import sys
import time
from dataclasses import dataclass


PORT = 9001
PAYLOAD_BYTES = 32768


@dataclass(frozen=True)
class Transfer:
    host: str
    port: int
    expected: int
    received: int
    elapsed_s: float
    bytes_per_sec: float
    complete: bool
    note: str


def pattern_ok(body: bytes) -> bool:
    return all(body[index] == (index & 0xFF) for index in range(len(body)))


def evaluate(host: str, port: int, body: bytes, elapsed_s: float, error: str | None) -> Transfer:
    elapsed = elapsed_s if elapsed_s > 0 else 0.0
    received = len(body)
    rate = received / elapsed if elapsed > 0 else 0.0
    note = error or ""
    complete = error is None and received == PAYLOAD_BYTES and pattern_ok(body)
    if not complete and not note:
        if received != PAYLOAD_BYTES:
            note = "short body"
        elif not pattern_ok(body):
            note = "payload pattern mismatch"
        else:
            note = "incomplete"
    return Transfer(host, port, PAYLOAD_BYTES, received, elapsed, rate, complete, note)


def read_until_close(sock: socket.socket, deadline: float) -> tuple[bytes, str | None]:
    body = bytearray()
    limit = PAYLOAD_BYTES + 1
    while len(body) < limit:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return bytes(body), "timeout"
        sock.settimeout(min(1.0, remaining))
        try:
            block = sock.recv(4096)
        except (TimeoutError, socket.timeout):
            if time.monotonic() >= deadline:
                return bytes(body), "timeout"
            continue
        except OSError as exc:
            return bytes(body), str(exc)
        if not block:
            return bytes(body), None
        body.extend(block)
    return bytes(body), "too long"


def receive(host: str, port: int, timeout: float) -> Transfer:
    started = time.monotonic()
    deadline = started + timeout
    try:
        sock = socket.create_connection((host, port), timeout=timeout)
    except (TimeoutError, socket.timeout, OSError) as exc:
        return evaluate(host, port, b"", time.monotonic() - started, str(exc))
    try:
        body, error = read_until_close(sock, deadline)
    finally:
        sock.close()
    return evaluate(host, port, body, time.monotonic() - started, error)


def format_report(row: Transfer) -> str:
    result = "complete" if row.complete else f"incomplete: {row.note}"
    return (
        f"tcp {row.host}:{row.port}  expected {row.expected}  received {row.received}  "
        f"sec {row.elapsed_s:.3f}  B/s {row.bytes_per_sec:.1f}  {result}"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Read the ESP32 raw TCP diagnostic. Does not flash firmware.")
    parser.add_argument("--host", default="192.168.1.50", help="Camera address")
    parser.add_argument("--port", type=int, default=PORT, help="Diagnostic port. The firmware uses 9001.")
    parser.add_argument("--timeout", type=float, default=30.0, help="Seconds before an unfinished read is incomplete.")
    args = parser.parse_args(argv)
    if args.port != PORT:
        print(f"Warning: firmware diagnostic port is {PORT}.", file=sys.stderr)
    row = receive(args.host, args.port, args.timeout)
    print(format_report(row))
    if not row.complete:
        print("TCP diagnostic incomplete. This is not a physical throughput claim.", file=sys.stderr)
        return 1
    print("TCP diagnostic payload matched. This is one measurement, not a speed claim.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
