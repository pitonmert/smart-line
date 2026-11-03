"""Compile the raw TCP sender checks. They do not contact the camera."""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tests" / "test_tcp_diag.cpp"
INCLUDE = ROOT / "src"


class TcpDiagSenderTests(unittest.TestCase):
    def test_payload_chunks_partial_writes_and_failure(self):
        with tempfile.TemporaryDirectory(prefix="smartline-tcp-diag-") as directory:
            binary = Path(directory) / "test_tcp_diag"
            subprocess.run(
                ["g++", "-std=c++17", "-Wall", "-Wextra", "-Werror", "-I", str(INCLUDE), str(SOURCE), "-o", str(binary)],
                check=True,
            )
            completed = subprocess.run([str(binary)], check=False, capture_output=True, text=True)
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("tcp diagnostic checks passed", completed.stdout)


if __name__ == "__main__":
    unittest.main()
