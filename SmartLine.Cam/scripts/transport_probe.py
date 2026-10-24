"""Measure HTTP body delivery on a running ESP32-CAM.

This script does not compile or flash firmware and does not open the camera
unless you pass --base. A timeout is an incomplete result. It is not a crash
and it is not a throughput claim.
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from dataclasses import dataclass
from urllib.parse import urlsplit, urlunsplit


SIZES = (1024, 8192, 32768, 65536)
CHUNK_SIZES = (1024, 2048, 4096)


@dataclass(frozen=True)
class Sample:
    status: int | None
    headers: dict[str, str]
    body: bytes
    error: str | None = None


@dataclass(frozen=True)
class ProbeRow:
    name: str
    status: int | None
    expected: int | None
    received: int
    elapsed_s: float
    bytes_per_sec: float
    complete: bool
    note: str


def join_url(base: str, path: str) -> str:
    parts = urlsplit(base if "://" in base else f"http://{base}")
    if parts.scheme != "http" or not parts.netloc:
        return ""
    query = ""
    resource = path
    if "?" in path:
        resource, query = path.split("?", 1)
    return urlunsplit((parts.scheme, parts.netloc, resource, query, ""))


def plan(base: str) -> list[tuple[str, str, str, float]]:
    """Name, absolute URL, kind, and deadline in seconds."""

    rows: list[tuple[str, str, str, float]] = [("status", join_url(base, "/status"), "status", 5.0)]
    for memory in ("internal", "psram"):
        for size in SIZES:
            path = f"/debug/throughput/{memory}?bytes={size}"
            rows.append((f"throughput {memory} {size}", join_url(base, path), "throughput", 30.0))
    rows.append(("capture jpeg", join_url(base, "/capture"), "jpeg", 45.0))
    rows.append(("capture raw", join_url(base, "/capture/raw"), "raw", 45.0))
    return rows


def _header(headers: dict[str, str], name: str) -> str | None:
    for key, value in headers.items():
        if key.lower() == name.lower():
            return value
    return None


def _header_int(headers: dict[str, str], name: str) -> int | None:
    value = _header(headers, name)
    if value is not None and value.isdigit():
        return int(value)
    return None


def _pattern_ok(body: bytes) -> bool:
    return all(body[index] == (index & 0xFF) for index in range(len(body)))


def _json_object(body: bytes) -> bool:
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return False
    return isinstance(payload, dict)


def evaluate(name: str, kind: str, sample: Sample, elapsed_s: float) -> ProbeRow:
    elapsed = elapsed_s if elapsed_s > 0 else 0.0
    received = len(sample.body)
    rate = received / elapsed if elapsed > 0 else 0.0
    note = sample.error or ""
    expected: int | None = None
    complete = False

    if kind == "status":
        expected = _header_int(sample.headers, "Content-Length")
        if expected is None and sample.status == 200 and sample.error is None:
            expected = received
        complete = sample.status == 200 and sample.error is None and _json_object(sample.body) and received == expected
        if not complete and not note:
            note = "status incomplete"
    elif kind == "throughput":
        expected = _header_int(sample.headers, "X-Debug-Payload-Bytes")
        chunk = _header_int(sample.headers, "X-Debug-Chunk-Bytes")
        pattern = expected is not None and received == expected and _pattern_ok(sample.body)
        complete = (
            sample.status == 200
            and sample.error is None
            and pattern
            and chunk in CHUNK_SIZES
        )
        if not complete and not note:
            if expected is not None and received != expected:
                note = "short body"
            elif expected is not None and received == expected and not _pattern_ok(sample.body):
                note = "payload pattern mismatch"
            elif chunk not in CHUNK_SIZES:
                note = "chunk size missing"
            else:
                note = "throughput incomplete"
    elif kind == "jpeg":
        expected = _header_int(sample.headers, "X-Frame-Bytes")
        if expected is None:
            expected = _header_int(sample.headers, "Content-Length")
        complete = (
            sample.status == 200
            and sample.error is None
            and sample.body.startswith(b"\xff\xd8")
            and expected is not None
            and received == expected
        )
        if not complete and not note:
            note = "jpeg incomplete"
    elif kind == "raw":
        expected = _header_int(sample.headers, "X-Frame-Bytes")
        width = _header_int(sample.headers, "X-Frame-Width")
        height = _header_int(sample.headers, "X-Frame-Height")
        pixel_format = _header(sample.headers, "X-Pixel-Format")
        order = _header(sample.headers, "X-Rgb565-Order")
        length_ok = width is not None and height is not None and received == width * height * 2
        complete = (
            sample.status == 200
            and sample.error is None
            and pixel_format == "RGB565"
            and order == "BE"
            and length_ok
            and expected is not None
            and received == expected
        )
        if not complete and not note:
            note = "raw incomplete"
    else:
        note = note or "unknown probe"

    return ProbeRow(name, sample.status, expected, received, elapsed, rate, complete, note)


def run_probe(base: str, fetch, clock) -> list[ProbeRow]:
    rows: list[ProbeRow] = []
    for name, url, kind, timeout in plan(base):
        if not url:
            rows.append(ProbeRow(name, None, None, 0, 0.0, 0.0, False, "Camera base URL is not absolute."))
            continue
        started = clock()
        try:
            sample = fetch(url, timeout)
        except Exception as exc:  # noqa: BLE001 - one bad route must not hide the others
            sample = Sample(None, {}, b"", str(exc))
        elapsed = clock() - started
        if not isinstance(sample, Sample):
            sample = Sample(None, {}, b"", "probe fetch did not return a sample")
        rows.append(evaluate(name, kind, sample, elapsed))
    return rows


def format_report(rows: list[ProbeRow]) -> str:
    header = f"{'name':<28} {'http':>5} {'expected':>10} {'received':>10} {'sec':>8} {'B/s':>10}  result"
    lines = [header]
    for row in rows:
        status = "-" if row.status is None else str(row.status)
        expected = "-" if row.expected is None else str(row.expected)
        result = "complete" if row.complete else "incomplete"
        if row.note and not row.complete:
            result = f"{result}: {row.note}"
        lines.append(
            f"{row.name:<28} {status:>5} {expected:>10} {received_text(row.received):>10} {row.elapsed_s:8.3f} {row.bytes_per_sec:10.1f}  {result}"
        )
    return "\n".join(lines)


def received_text(received: int) -> str:
    return str(received)


def _recv(sock: socket.socket, deadline: float) -> tuple[bytes | None, str | None]:
    """Return payload bytes, or None at EOF. An empty payload means try again."""

    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return b"", "timeout"
    sock.settimeout(min(1.0, remaining))
    try:
        block = sock.recv(4096)
    except (TimeoutError, socket.timeout):
        if time.monotonic() >= deadline:
            return b"", "timeout"
        return b"", None
    except OSError as exc:
        return b"", str(exc)
    if block == b"":
        return None, None
    return block, None


def _next_block(sock: socket.socket, deadline: float) -> tuple[bytes | None, str | None]:
    while True:
        block, error = _recv(sock, deadline)
        if error is not None or block is None or block:
            return block, error


def _read_body(raw: bytes, headers: dict[str, str], sock: socket.socket, deadline: float) -> tuple[bytes, str | None]:
    pending = bytearray(raw)
    encoding = (headers.get("transfer-encoding") or "").lower()
    if "chunked" in encoding:
        body = bytearray()
        while True:
            while b"\r\n" not in pending:
                block, error = _next_block(sock, deadline)
                if error is not None:
                    return bytes(body), error
                if block is None:
                    return bytes(body), "incomplete"
                pending.extend(block)
            line, _, pending = pending.partition(b"\r\n")
            size_text = line.split(b";", 1)[0].strip()
            try:
                size = int(size_text, 16)
            except ValueError:
                return bytes(body), "bad chunk"
            if size == 0:
                return bytes(body), None
            while len(pending) < size:
                block, error = _next_block(sock, deadline)
                if error is not None or block is None:
                    body.extend(pending[:size])
                    return bytes(body), error or "incomplete"
                pending.extend(block)
            body.extend(pending[:size])
            pending = pending[size:]
            while len(pending) < 2:
                block, error = _next_block(sock, deadline)
                if error is not None or block is None:
                    return bytes(body), error or "incomplete"
                pending.extend(block)
            pending = pending[2:]
    length_text = headers.get("content-length")
    if length_text is not None and length_text.isdigit():
        needed = int(length_text)
        while len(pending) < needed:
            block, error = _next_block(sock, deadline)
            if error is not None or block is None:
                return bytes(pending[:needed]), error or "incomplete"
            pending.extend(block)
        return bytes(pending[:needed]), None
    while True:
        block, error = _next_block(sock, deadline)
        if error is not None:
            return bytes(pending), error
        if block is None:
            return bytes(pending), None
        pending.extend(block)


def http_get(url: str, timeout: float) -> Sample:
    """Read until the body ends or the deadline. A short read stays in the sample."""

    parts = urlsplit(url)
    if parts.scheme != "http" or not parts.hostname:
        return Sample(None, {}, b"", "URL is not http")
    path = parts.path or "/"
    if parts.query:
        path = f"{path}?{parts.query}"
    host = parts.hostname
    port = parts.port or 80
    deadline = time.monotonic() + max(timeout, 0.1)
    try:
        sock = socket.create_connection((host, port), timeout=max(timeout, 0.1))
    except (TimeoutError, socket.timeout, OSError) as exc:
        return Sample(None, {}, b"", str(exc))
    raw = bytearray()
    status: int | None = None
    headers: dict[str, str] = {}
    try:
        request = f"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n"
        sock.sendall(request.encode("ascii"))
        while b"\r\n\r\n" not in raw:
            block, error = _recv(sock, deadline)
            if error is not None:
                return Sample(None, {}, b"", error)
            if block is None:
                return Sample(None, {}, bytes(raw), "incomplete")
            if not block:
                continue
            raw.extend(block)
            if len(raw) > 16384 and b"\r\n\r\n" not in raw:
                return Sample(None, {}, b"", "headers too large")
        head, _, rest = bytes(raw).partition(b"\r\n\r\n")
        lines = head.decode("iso-8859-1", errors="replace").split("\r\n")
        if not lines or " " not in lines[0]:
            return Sample(None, {}, rest, "bad status")
        status_text = lines[0].split(" ", 2)[1]
        status = int(status_text) if status_text.isdigit() else None
        for line in lines[1:]:
            if ":" not in line:
                continue
            name, value = line.split(":", 1)
            headers[name.strip().lower()] = value.strip()
        body, error = _read_body(rest, headers, sock, deadline)
        return Sample(status, headers, body, error)
    except (TimeoutError, socket.timeout, OSError, ValueError) as exc:
        return Sample(status, headers, b"", str(exc))
    finally:
        sock.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Measure ESP32-CAM HTTP body delivery. Does not flash firmware.")
    parser.add_argument("--base", default="http://192.168.1.50:81", help="Camera origin, for example http://192.168.1.50:81")
    args = parser.parse_args(argv)
    rows = run_probe(args.base, http_get, time.monotonic)
    print(format_report(rows))
    if any(not row.complete for row in rows):
        print("Transport probe incomplete. This is not a physical throughput claim.", file=sys.stderr)
        return 1
    print("Transport probe complete.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
