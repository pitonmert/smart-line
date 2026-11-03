"""Compile and run diagnostic firmware against fake I2C, sensors and actuators."""

import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class SensorDiagnosticsTests(unittest.TestCase):
    def test_sensor_failures_and_independent_outputs(self):
        with tempfile.TemporaryDirectory(prefix="smartline-sensor-tests-") as directory:
            temporary = Path(directory)
            for name in ("Arduino.h", "Wire.h", "Adafruit_AHTX0.h",
                         "Adafruit_TCS34725.h", "ESP32Servo.h", "Config.h"):
                (temporary / name).write_text('#include "sensor_fakes.h"\n')
            binary = temporary / "sensor_tests"
            subprocess.run([
                "c++", "-std=c++17", "-Wall", "-Wextra", "-Werror",
                "-I", str(temporary), "-I", str(ROOT / "tests"),
                str(ROOT / "tests" / "test_sensor_diagnostics.cpp"), "-o", str(binary),
            ], check=True)
            for scenario in ("missing", "bus-failure", "valid", "zero",
                             "independent-outputs", "disconnected", "timeout",
                             "register-failure", "truncated", "empty", "read-failure",
                             "red-servo", "red-threshold", "fire-during-capture"):
                with self.subTest(scenario=scenario):
                    result = subprocess.run([str(binary), scenario], capture_output=True, text=True)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
