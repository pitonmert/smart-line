# SmartLine

Factory monitoring, recipe-based visual inspection, and ESP32 device control. Python 3.12 and Django 5.2 run the web application and image processing as two processes of one project.

```text
Browser / TV ── Django web/system ── PostgreSQL
                       │
                       ├── Mosquitto ── ESP32 controller
                       │
                       └── image process ── ESP32 camera
```

The web/system process owns recipe decisions, records, dataset storage, device state and browser notifications. The image process acquires images and produces observations; it does not choose destinations or write datasets. Training is offline and separate from production processing.

Release notes are maintained in [GitHub Releases](https://github.com/pitonmert/smart-line/releases).

## Requirements

- Python 3.12 and [uv](https://docs.astral.sh/uv/getting-started/installation/) for local commands.
- Docker with Compose for the PostgreSQL/Mosquitto/application stack.
- PlatformIO for firmware builds; a C++17 compiler for firmware host tests.
- Camera/model hardware is optional for the read-only mock preview.

Run commands from the repository root unless a firmware guide says otherwise.

## Preview without hardware

```sh
uv sync --frozen
uv run --frozen smartline web --mock
```

Open <http://127.0.0.1:8000>. This preview uses example images and data without PostgreSQL, MQTT, a camera or a model. Writes return `409 MOCK_READ_ONLY`; production images omit the mock fixture.

## Development

```sh
cp .env.example .env
# Set the PostgreSQL password and camera configuration in .env.
docker compose --env-file .env -f deploy/compose.dev.yaml up --build
```

Open <http://127.0.0.1:8000>. Development Compose runs both Python processes and PostgreSQL, with separate development database/dataset volumes. MQTT is explicitly disabled in this camera-focused stack.

To run the processes locally against configured infrastructure:

```sh
uv sync --frozen --extra vision
uv run --frozen --extra vision --env-file .env smartline web
# In another terminal, with image-process MQTT/camera variables configured:
uv run --frozen --extra vision --env-file .env smartline vision
```

Add `--extra openvino` to both dependency installation and image-process commands when using OpenVINO locally. Production image builds include ONNX Runtime and OpenVINO by default.

## Production

```sh
cp .env.example .env
# Set PostgreSQL/MQTT passwords and the camera URL before starting services.
SMARTLINE_LAN_IP=192.168.1.20 sh deploy/certs/generate-local-ca.sh
docker compose --env-file .env -f deploy/compose.yaml up --build -d
```

Install `deploy/certs/generated/ca.crt` on browser clients. Open `https://smartline.local` after configuring name resolution, or use the server IP included in the certificate. Configure `ALLOWED_HOSTS` for another hostname. HTTPS terminates directly in the web process on container port 8443, mapped to host port 443; no reverse proxy is required. Mosquitto uses port 8883 with mutual TLS, passwords and topic ACLs. HTTP endpoints have no accounts, roles or device/service keys.

Compose files and relative bind mounts live under `deploy/`; build context remains the repository root. Certificates default to `deploy/certs/generated/`. Set `SMARTLINE_CERT_DIR` to an absolute path when overriding that location. The private image-control port 8090 is not published. Its HTTPS callback uses the internal `smartline.local` alias and trusts the generated CA.

The single web worker applies migrations, seeds recipe/calibration/system defaults, reconciles dataset storage and starts MQTT before accepting requests. Database startup failure prevents startup; unavailable dataset storage is reported without disabling unrelated functionality. The image process may be unavailable while web/system stays running. Persistent volumes are `python_postgres_data`, `python_datasets` and `mosquitto_data`; earlier databases/datasets are not imported or deleted.

### Configuration

See [.env.example](.env.example) for the common variables. Compose supplies process-specific connection and certificate values; direct commands read `.env` through `uv run --env-file .env`.

| Variable | Purpose |
| --- | --- |
| `DATABASE_URL` or `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` | Web PostgreSQL connection |
| `DATASET_ROOT` | Web-owned capture storage; local default `data/python/datasets`, Compose `/var/lib/smartline/datasets` |
| `VISION_URL`, `VISION_CAPTURE_TIMEOUT_SECONDS` | Private image-process origin and request budget; defaults `http://127.0.0.1:8090` and 35 seconds |
| `CAM_STREAM_URL`, `CAM_CAPTURE_URL` | Image-process MJPEG source and optional still URL; otherwise derived from the stream host |
| `VISION_CONTROL_HOST`, `VISION_CONTROL_PORT`, `API_BASE_URL`, `RECIPE_PATH` | Private image listener (default `127.0.0.1:8090`), web callback and optional fallback recipe |
| `STILL_CAPTURE_TIMEOUT`, `PREFER_RAW_CAPTURE`, `ALLOW_PREVIEW_FALLBACK` | Still budget 30 seconds, raw capture preferred, preview fallback disabled by default |
| `MODEL_PATH`, `MODEL_VERSION`, `MODEL_TASK`, `MODEL_LAYOUT`, `MODEL_INPUT_SIZE`, `MODEL_CONFIDENCE` | Configured artifact and inference options |
| `MQTT_HOST`, `MQTT_PORT`, `MQTT_CLIENT_ID`, `MQTT_USERNAME`, `MQTT_PASSWORD` | Web MQTT connection; the image process uses `MQTT_BROKER_HOST` / `MQTT_BROKER_PORT` |
| `MQTT_CA_CERT_PATH`, `MQTT_CLIENT_CERT_PATH`, `MQTT_CLIENT_KEY_PATH` | Process-specific MQTT certificates |
| `HTTPS_CERT_PATH`, `HTTPS_KEY_PATH`, `ALLOWED_HOSTS` | Direct web HTTPS and allowed hostnames |
| `SMARTLINE_ENV`, `SIMULATION_ENABLED`, `HARDWARE_ACTUATION_ENABLED` | Environment, development-only simulation and existing no-actuator guard |
| `SMARTLINE_TIME_ZONE`, `HIGH_TEMP_THRESHOLD`, `ALARM_TEMP_THRESHOLD`, `FAN_HYSTERESIS_CELSIUS` | Facility time zone and environment thresholds |
| `SHOW_PREVIEW` | Optional local OpenCV window; disabled by default |

MQTT principals remain `smartline-api`, `smartline-firmware` and `smartline-vision`. Compose maps `MQTT_API_PASSWORD`, `MQTT_FIRMWARE_PASSWORD` and `MQTT_VISION_PASSWORD` to these principals. Configure each process's username/certificate/password variables when running directly; supplying only the Compose password variables does not configure a local process.

Runtime artifacts and metadata sidecars belong in `deploy/models/`, mounted read-only at `/opt/smartline/models`; set container `MODEL_PATH` accordingly and restart Vision after changing the model. Registry metadata belongs in `tools/training/registry/`; local training datasets belong in `tools/training/data/`. Training tools, dependencies, test fixtures and local artifacts are excluded from production images.

```sh
docker build -f deploy/Dockerfile --target web -t smartline-web .
docker build -f deploy/Dockerfile --target vision -t smartline-vision .
# Optional ONNX-only image:
docker build -f deploy/Dockerfile --target vision --build-arg INFERENCE_BACKEND=onnx -t smartline-vision-onnx .
```

## Dashboard

Django templates, native JavaScript/CSS and a local Chart.js distribution provide three sections:

- **Production** (`/`): preview, last stored inspection, counters, active issues and environment readings. It sends no control or inspection commands.
- **History** (`/history`): searchable/filterable inspection records, pagination, expandable details and production summaries. Barcode statistics remain separate from inspection yield.
- **Management** (`/management`): conveyor controls, recipes, calibration, sensors, diagnostics, manual inspection, logical sorting, camera dataset collection and development simulators.

Management subpages are directly addressable. Legacy `/inspection`, `/analytics`, `/sensors`, `/sorting`, `/recipes`, `/calibration`, `/dataset` and `/diagnostics` URLs redirect to their current locations; back/forward navigation is supported.

English and dark theme are defaults. TR/EN and light/dark choices persist as `smartline.language` and `smartline.theme`; invalid/unavailable storage uses defaults. Preferences preserve form input, user text and wire values. After disconnects the last values remain visible with a warning; controls require fresh connectivity, and device controls also require MQTT and an online controller. Fire alerts remain global, with a short audible notification when allowed; resolving an alert does not reset the safety latch.

Preview and stored inspection are different moments; old detection boxes are not drawn on live images. Dataset interval capture stops on navigation, hidden pages, connection failure or a missing/stopped session.

## API and live events

JSON responses use `isSuccess`, `message`, `data` and, on error, `code`. Fields are camelCase; unexpected exception details are hidden. Unknown/retired routes return JSON `404`, unsupported methods return `405`. Collections use bounded pagination with stable ordering. The full REST inventory is [http_contracts.json](src/smartline/web/http_contracts.json).

| Area | Main paths |
| --- | --- |
| Monitoring and devices | `/health`, `/api/dashboard`, `/api/control-room`, `/api/diagnostics`, `/api/conveyor/*`, `/api/sensors/*` |
| Barcode statistics, alerts and logs | `/api/products/*`, `/api/statistics/*`, `/api/alerts/*`, `/api/logs/*` |
| Recipes and calibration | `/api/recipes/*`, `/api/calibration-profiles/*` |
| Inspection and tracking | `/api/inspections`, `/api/inspections/{id}`, `/api/inspections/vision`, `/api/tracking/*` |
| Datasets and simulation | `/api/dataset/sessions/*`, `/api/simulation/*` |

`POST /api/inspections/vision` accepts `{"requestId":"<uuid>"}`. Web pins the recipe/calibration, requests a Vision observation, computes the decision and atomically stores the result/track. Repeating a completed ID returns the stored result without another capture. `/api/inspections` records manual observations without claiming model metadata; simulation is explicitly marked. Production inspection counters exclude simulation; yield is pass divided by pass plus reject and excludes manual review.

Vision exposes private `/health`, `/frames/preview`, `/frames/dataset`, `/frames/inspection`, `/validate-recipe` and `/inspect` routes. It validates complete JPEG content, actual dimensions and the 16-million-pixel limit without recompressing valid JPEGs; barcode decoding retains MQTT publishing. Web alone writes dataset JPEGs, SHA-256 and manifests. Camera collection uses `POST /api/dataset/sessions/{id}/captures/camera`; session, discard and recovery operations coordinate writes. Missing referenced images mark `RECOVERY_REQUIRED`; legacy PNG reads remain supported. PC file upload is removed.

`GET /api/events` serves ASGI SSE with `ready`, 15-second heartbeats and the seven business events: `ReceiveConveyorUpdate`, `ReceiveEnvironmentUpdate`, `ReceiveProductUpdate`, `ReceiveAlertUpdate`, `ReceiveLogUpdate`, `ReceiveInspectionUpdate` and `ReceiveTrackingUpdate`. Reconnection reloads REST snapshots. SignalR routes return `404`; diagnostics reports `signalR: "retired"` and `liveUpdates: "sse_enabled"`.

## Repository and module guides

```text
src/smartline/   config, web, inspection, devices and vision packages
firmware/       camera and controller firmware
tests/          domain, API, image, browser and integration checks
  support/      MQTT and deployment integration helpers
tools/training/ offline training, registry metadata and ignored datasets
deploy/         Docker, Compose, certificates, broker and runtime models
```

- [Camera firmware and historical hardware observations](firmware/camera/README.md)
- [Controller setup, pins, MQTT and sensor diagnostics](firmware/controller/README.md)
- [Dataset preparation, training and model export](tools/training/README.md)

## Verification

```sh
uv sync --frozen --extra vision
uv run --frozen --extra vision smartline manage check
uv run --frozen --extra vision smartline manage makemigrations --check --dry-run
uv run --frozen --extra vision ruff check src tests tools/training
uv run --frozen --extra vision ruff format --check src tests tools/training
# DATABASE_URL must point to disposable PostgreSQL; its user must be able to create test databases.
uv run --frozen --extra vision --env-file .env pytest --ignore=tests/test_browser.py
uv run --frozen playwright install chromium
uv run --frozen pytest tests/test_browser.py
python3 -m unittest discover -s firmware/camera/tests -p 'test_*.py'
python3 -m unittest discover -s firmware/controller/tests -p 'test_*.py'
# After building the production targets with these tags:
docker build -f deploy/Dockerfile --target web -t smartline-web:ci .
docker build -f deploy/Dockerfile --target vision -t smartline-vision:ci .
python3 tests/support/stack/verify-compose.py
```

The Compose verifier creates disposable certificates, random ports and its own volumes, then removes only that test project. CI also runs firmware builds, MQTT mutual-TLS/topic-ACL checks and Gitleaks full-history scanning. Firmware host checks require no board; actual capture, electrical behavior, target-machine performance and factory deployment need separate hardware/environment verification.

Gitleaks uses its default rules. [.github/gitleaks.ignore](.github/gitleaks.ignore) lists only two reviewed historical false positives by fingerprint; it does not exclude entire files. With Gitleaks installed locally, use the same exceptions explicitly:

```sh
gitleaks git --log-opts="--all" --redact=100 --gitleaks-ignore-path=.github/gitleaks.ignore
```

## Current limitations

- Real inspection requires a configured detection model; color-only operation is not implemented. Default colors are RED/BLUE/GREEN; black/white/gray and automatic object-specific color regions remain future work.
- Inspection uses explicit API/manual/simulator triggers; automatic product arrival, duplicate suppression across arrivals and continuous inference are not implemented.
- Barcode products/statistics and recipe inspections/tracking remain separate. The pipeline selects one primary object; multiple-object inspection, classification and segmentation are not supported.
- Sorting is logical. Physical actuator transport is not implemented; the production servo stays neutral. Reserved calibration rotation/flip/perspective fields do not transform images or remap boxes.
- Model changes require a restart; automatic model replacement is not implemented. Historical inspection boxes are not overlaid on a different live frame.
