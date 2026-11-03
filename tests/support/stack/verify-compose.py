"""Start production services with disposable certificates, ports, and volumes."""

import argparse
import json
import os
from pathlib import Path
import ssl
import subprocess
import tempfile
import uuid
from urllib.request import urlopen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--web-image", default="smartline-web:ci")
    parser.add_argument("--vision-image", default="smartline-vision:ci")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    project = "smartline-stack-test-" + uuid.uuid4().hex[:8]
    with tempfile.TemporaryDirectory(prefix=project) as directory:
        temp = Path(directory)
        env = dict(
            os.environ,
            SMARTLINE_CERT_DIR=str(temp / "certs"),
            SMARTLINE_CERT_OUTPUT_DIR=str(temp / "certs"),
            SMARTLINE_LAN_IP="127.0.0.1",
            POSTGRES_DB="smartline_python",
            POSTGRES_USER="smartline",
            POSTGRES_PASSWORD="migration-stack-only",
            MQTT_API_PASSWORD="migration-api-password-0000000000000000",
            MQTT_FIRMWARE_PASSWORD="migration-firmware-password-00000000000",
            MQTT_VISION_PASSWORD="migration-vision-password-0000000000000",
            CAM_STREAM_URL="http://127.0.0.1:1/stream",
            CAM_CAPTURE_URL="",
            MODEL_PATH="",
            MODEL_VERSION="",
        )
        subprocess.run(
            ["sh", "deploy/certs/generate-local-ca.sh"],
            cwd=root,
            env=env,
            check=True,
            capture_output=True,
        )
        override = temp / "compose.yml"
        override.write_text(
            "services:\n  mosquitto:\n    ports: !reset []\n  web:\n    image: "
            + args.web_image
            + '\n    ports: !override ["127.0.0.1::8443"]\n  vision:\n    image: '
            + args.vision_image
            + "\n"
        )
        cmd = [
            "docker",
            "compose",
            "-p",
            project,
            "-f",
            str(root / "deploy/compose.yaml"),
            "-f",
            str(override),
        ]

        def run(arguments, **options):
            return subprocess.run(cmd + arguments, cwd=root, env=env, check=True, **options)

        with (temp / "compose.log").open("w+") as log:
            try:
                run(
                    ["up", "--no-build", "--detach", "--wait", "--wait-timeout", "120"],
                    stdout=log,
                    stderr=log,
                )
                port = (
                    run(["port", "web", "8443"], capture_output=True, text=True)
                    .stdout.strip()
                    .rsplit(":", 1)[1]
                )
                context = ssl.create_default_context(cafile=str(temp / "certs/ca.crt"))
                for route in [
                    "/health",
                    "/",
                    "/api/dashboard",
                    "/api/control-room",
                    "/api/diagnostics",
                    "/static/app.js",
                ]:
                    with urlopen(
                        f"https://127.0.0.1:{port}" + route, context=context, timeout=10
                    ) as response:
                        assert response.status == 200, (route, response.status)
                        payload = response.read()
                        if route == "/api/diagnostics":
                            result = json.loads(payload)["data"]
                            assert (
                                result["database"] == "healthy" and result["mqtt"] == "connected"
                            ), result
                    print(f"HTTPS {route}: 200", flush=True)
                package_check = "import importlib.util; from pathlib import Path; import smartline; assert Path('/proc/1/status').read_text().split('Uid:')[1].split()[0]=='10001'; assert importlib.util.find_spec('pytest') is None; assert importlib.util.find_spec('ultralytics') is None; assert not Path('/app/tools').exists()"
                web_check = (
                    package_check
                    + "; assert importlib.util.find_spec('cv2') is None; assert not Path(smartline.__file__).parent.joinpath('web/static/mock-data.json').exists(); print('Web runtime boundary: OK')"
                )
                run(["exec", "-T", "web", "python", "-c", web_check])
                vision_check = (
                    package_check
                    + "; import urllib.request,json,time,openvino,onnxruntime; time.sleep(3); data=json.load(urllib.request.urlopen('http://127.0.0.1:8090/health',timeout=5)); assert data['status']=='healthy',data; print('Vision health, OpenVINO and ONNX Runtime: OK')"
                )
                run(["exec", "-T", "vision", "python", "-c", vision_check])
                device_command = [
                    "docker",
                    "run",
                    "--rm",
                    "--network",
                    project + "_backend",
                    "--entrypoint",
                    "python",
                    "-v",
                    str(temp / "certs") + ":/test-certs:ro",
                    "-v",
                    str(root / "tests/support/stack/check-devices.py")
                    + ":/test/check-devices.py:ro",
                    "-e",
                    "MQTT_FIRMWARE_PASSWORD",
                    args.web_image,
                    "/test/check-devices.py",
                ]
                subprocess.run(device_command, env=env, check=True)
                run(["restart", "mosquitto"], stdout=log, stderr=log)
                import time

                for _ in range(50):
                    with urlopen(
                        f"https://127.0.0.1:{port}/api/diagnostics", context=context, timeout=5
                    ) as response:
                        if json.load(response)["data"]["mqtt"] == "connected":
                            break
                    time.sleep(0.2)
                else:
                    raise AssertionError("Web MQTT connection did not recover")
                subprocess.run(device_command + ["--replay"], env=env, check=True)
                print("Isolated production Compose startup: OK", flush=True)
            except BaseException:
                run(["logs", "--tail", "40"], stdout=log, stderr=log)
                log.seek(0)
                print(log.read())
                raise
            finally:
                run(["down", "--volumes", "--remove-orphans"], stdout=log, stderr=log)


if __name__ == "__main__":
    main()
