"""Transport probe checks use a fake camera or a local socket. They do not flash."""

from __future__ import annotations

import socket
import sys
import threading
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import transport_probe


def pattern(size: int) -> bytes:
    return bytes(index & 0xFF for index in range(size))


class TransportProbeTests(unittest.TestCase):
    def test_short_and_mismatched_payloads_are_incomplete(self):
        short = transport_probe.evaluate(
            "throughput internal 1024",
            "throughput",
            transport_probe.Sample(200, {"X-Debug-Payload-Bytes": "1024", "X-Debug-Chunk-Bytes": "2048"}, pattern(100), "timeout"),
            0.4,
        )
        self.assertFalse(short.complete)
        self.assertEqual(1024, short.expected)
        self.assertEqual(100, short.received)
        self.assertIn("timeout", short.note)

        mismatch = transport_probe.evaluate(
            "throughput internal 8",
            "throughput",
            transport_probe.Sample(200, {"X-Debug-Payload-Bytes": "8", "X-Debug-Chunk-Bytes": "2048"}, b"\x00" * 8),
            0.1,
        )
        self.assertFalse(mismatch.complete)
        self.assertEqual("payload pattern mismatch", mismatch.note)

    def test_jpeg_and_raw_require_the_full_body(self):
        jpeg = transport_probe.evaluate(
            "capture jpeg",
            "jpeg",
            transport_probe.Sample(200, {"X-Frame-Bytes": "4"}, b"\xff\xd8\xff\xd9"),
            0.2,
        )
        self.assertTrue(jpeg.complete)
        self.assertEqual(4, jpeg.received)

        partial_jpeg = transport_probe.evaluate(
            "capture jpeg",
            "jpeg",
            transport_probe.Sample(200, {"X-Frame-Bytes": "29125"}, b"\xff\xd8" + b"\x00" * 10, "timeout"),
            60.0,
        )
        self.assertFalse(partial_jpeg.complete)
        self.assertEqual(29125, partial_jpeg.expected)
        self.assertEqual(12, partial_jpeg.received)

        raw = b"\xf8\x00" * 4
        good_raw = transport_probe.evaluate(
            "capture raw",
            "raw",
            transport_probe.Sample(
                200,
                {
                    "X-Frame-Bytes": str(len(raw)),
                    "X-Frame-Width": "2",
                    "X-Frame-Height": "2",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                },
                raw,
            ),
            0.2,
        )
        self.assertTrue(good_raw.complete)
        short_raw = transport_probe.evaluate(
            "capture raw",
            "raw",
            transport_probe.Sample(
                200,
                {
                    "X-Frame-Bytes": "8",
                    "X-Frame-Width": "2",
                    "X-Frame-Height": "2",
                    "X-Pixel-Format": "RGB565",
                    "X-Rgb565-Order": "BE",
                },
                raw[:2],
            ),
            0.2,
        )
        self.assertFalse(short_raw.complete)

    def test_probe_plan_covers_status_both_memories_and_one_capture_each(self):
        calls: list[tuple[str, float]] = []

        def fetch(url: str, timeout: float):
            calls.append((url, timeout))
            if url.endswith("/status"):
                body = b'{"ok":true}'
                return transport_probe.Sample(200, {"Content-Length": str(len(body))}, body)
            if "/debug/throughput/" in url:
                size = int(url.rsplit("bytes=", 1)[1])
                body = pattern(size)
                return transport_probe.Sample(
                    200,
                    {"X-Debug-Payload-Bytes": str(size), "X-Debug-Chunk-Bytes": "2048"},
                    body,
                )
            if url.endswith("/capture/raw"):
                body = b"\xf8\x00" * (640 * 480)
                return transport_probe.Sample(
                    200,
                    {
                        "X-Frame-Bytes": str(len(body)),
                        "X-Frame-Width": "640",
                        "X-Frame-Height": "480",
                        "X-Pixel-Format": "RGB565",
                        "X-Rgb565-Order": "BE",
                    },
                    body,
                )
            if url.endswith("/capture"):
                return transport_probe.Sample(200, {"X-Frame-Bytes": "4"}, b"\xff\xd8\xff\xd9")
            raise AssertionError(url)

        rows = transport_probe.run_probe("http://192.168.1.50:81", fetch, lambda: 0.0)
        self.assertTrue(all(row.complete for row in rows))
        urls = [url for url, _timeout in calls]
        self.assertTrue(all("/stream" not in url for url in urls))
        self.assertEqual(1 + 8 + 2, len(urls))
        for memory in ("internal", "psram"):
            for size in (1024, 8192, 32768, 65536):
                self.assertIn(f"http://192.168.1.50:81/debug/throughput/{memory}?bytes={size}", urls)
        self.assertEqual(5.0, calls[0][1])
        self.assertEqual(30.0, calls[1][1])
        self.assertEqual(45.0, calls[-2][1])
        self.assertEqual(45.0, calls[-1][1])
        report = transport_probe.format_report(rows)
        self.assertIn("complete", report)
        self.assertNotIn("incomplete", report)

    def test_a_fetch_exception_stays_incomplete(self):
        def fetch(_url, _timeout):
            raise TimeoutError("stall")

        rows = transport_probe.run_probe("http://192.168.1.50:81", fetch, lambda: 1.0)
        self.assertTrue(all(not row.complete for row in rows))
        self.assertIn("stall", rows[0].note)

    def test_local_socket_keeps_a_partial_body_and_decodes_chunks(self):
        partial = _serve(
            b"HTTP/1.1 200 OK\r\nContent-Length: 100\r\nX-Debug-Payload-Bytes: 100\r\n"
            b"X-Debug-Chunk-Bytes: 2048\r\nConnection: close\r\n\r\n" + (b"ab" * 20),
            hold=True,
        )
        sample = transport_probe.http_get(partial, 0.4)
        row = transport_probe.evaluate("partial", "throughput", sample, 0.4)
        self.assertEqual(200, sample.status)
        self.assertEqual(40, len(sample.body))
        self.assertEqual(100, row.expected)
        self.assertFalse(row.complete)
        self.assertEqual("timeout", sample.error)

        payload = pattern(16)
        framed = b"10\r\n" + payload + b"\r\n0\r\n\r\n"
        header = (
            b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n"
            b"X-Debug-Payload-Bytes: 16\r\nX-Debug-Chunk-Bytes: 2048\r\nConnection: close\r\n\r\n"
        )
        complete = _serve(header + framed, hold=False)
        decoded = transport_probe.http_get(complete, 2.0)
        decoded_row = transport_probe.evaluate("chunked", "throughput", decoded, 0.1)
        self.assertIsNone(decoded.error)
        self.assertEqual(payload, decoded.body)
        self.assertTrue(decoded_row.complete)


def _serve(payload: bytes, hold: bool) -> str:
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", 0))
    server.listen(1)
    port = server.getsockname()[1]

    def run() -> None:
        connection, _address = server.accept()
        connection.settimeout(2.0)
        try:
            request = bytearray()
            while b"\r\n\r\n" not in request:
                block = connection.recv(1024)
                if not block:
                    break
                request.extend(block)
            connection.sendall(payload)
            if hold:
                time_sleep(0.8)
        except OSError:
            pass
        finally:
            connection.close()
            server.close()

    threading.Thread(target=run, daemon=True).start()
    return f"http://127.0.0.1:{port}/debug/throughput/internal?bytes=100"


def time_sleep(seconds: float) -> None:
    import time

    time.sleep(seconds)


if __name__ == "__main__":
    unittest.main()
