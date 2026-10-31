"""Compile and run the host-side chunked body sender checks."""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tests" / "test_http_body.cpp"
INCLUDE = ROOT / "src"


class HttpBodyTests(unittest.TestCase):
    def test_chunked_sender_partial_send_and_cleanup(self):
        compiler = "g++"
        with tempfile.TemporaryDirectory(prefix="smartline-http-body-") as directory:
            binary = Path(directory) / "test_http_body"
            subprocess.run(
                [compiler, "-std=c++17", "-pthread", "-Wall", "-Wextra", "-Werror", "-I", str(INCLUDE), str(SOURCE), "-o", str(binary)],
                check=True,
            )
            completed = subprocess.run([str(binary)], check=False, capture_output=True, text=True)
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
        self.assertIn("http body checks passed", completed.stdout)


if __name__ == "__main__":
    unittest.main()
