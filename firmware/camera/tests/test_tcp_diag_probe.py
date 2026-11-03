"""TCP diagnostic client checks use a local socket. They do not flash or contact the camera."""

from __future__ import annotations

import socket
import sys
import threading
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import tcp_diag_probe


def pattern(size: int) -> bytes:
    return bytes(index & 0xFF for index in range(size))


class TcpDiagProbeTests(unittest.TestCase):
    def test_defaults_match_the_firmware_contract(self):
        self.assertEqual(9001, tcp_diag_probe.PORT)
        self.assertEqual(32768, tcp_diag_probe.PAYLOAD_BYTES)

    def test_short_and_mismatched_payloads_are_incomplete(self):
        short = tcp_diag_probe.evaluate("127.0.0.1", 9001, pattern(100), 0.2, "closed")
        self.assertFalse(short.complete)
        self.assertEqual(32768, short.expected)
        self.assertEqual(100, short.received)

        mismatch = tcp_diag_probe.evaluate("127.0.0.1", 9001, b"\x00" * 32768, 0.2, None)
        self.assertFalse(mismatch.complete)
        self.assertEqual("payload pattern mismatch", mismatch.note)

    def test_client_reads_until_the_sender_closes(self):
        row = _transfer(pattern(32768))
        self.assertTrue(row.complete, tcp_diag_probe.format_report(row))
        self.assertEqual(32768, row.received)
        self.assertGreater(row.bytes_per_sec, 0)

    def test_an_early_close_stays_incomplete(self):
        row = _transfer(pattern(40))
        self.assertFalse(row.complete, tcp_diag_probe.format_report(row))
        self.assertEqual(40, row.received)
        self.assertEqual(32768, row.expected)


def _transfer(payload: bytes) -> tcp_diag_probe.Transfer:
    """Serve one payload on an ephemeral port. Port 9001 stays a firmware constant."""
    ready = threading.Event()
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind(("127.0.0.1", 0))
        port = server.getsockname()[1]
        server.listen(1)
        server.settimeout(2.0)
    except OSError:
        server.close()
        raise

    def run() -> None:
        connection = None
        try:
            ready.set()
            connection, _address = server.accept()
            connection.sendall(payload)
            connection.shutdown(socket.SHUT_WR)
        except OSError:
            return
        finally:
            if connection is not None:
                connection.close()
            server.close()

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    try:
        if not ready.wait(1.0):
            raise AssertionError("diagnostic server did not start")
        return tcp_diag_probe.receive("127.0.0.1", port, 2.0)
    finally:
        thread.join(timeout=2.0)
        server.close()


if __name__ == "__main__":
    unittest.main()
