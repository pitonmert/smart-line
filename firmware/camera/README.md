# ESP32 camera

The ESP32-CAM is an IP camera. It does not connect to MQTT, read production sensors, process images or accept API commands. `smartline.vision` consumes its stream, acquires stills and owns barcode/QR decoding and MQTT publication. See [project setup](../../README.md#production) and [image-process configuration](../../README.md#configuration).

## Board and build configuration

The PlatformIO `esp32cam` environment uses `espressif32`, Arduino, monitor speed `115200`, upload speed `921600`, `BOARD_HAS_PSRAM` and `-mfix-esp32-psram-cache-issue`. The pin map targets AI Thinker ESP32-CAM. Upload through an FTDI/USB-TTL adapter with `IO0` connected to `GND`; disconnect that jumper and reset after upload. Supply the board through its 5V pin; 3.3V may be insufficient for camera/Wi-Fi operation. PSRAM flags must remain enabled.

Wi-Fi credentials/addressing are build-time environment variables, not source/header settings. Missing variables cause `#error` during compilation. Use a stable camera address that matches `CAM_STREAM_URL`.

```sh
export WIFI_SSID="YOUR_WIFI"
export WIFI_PASS="YOUR_PASSWORD"
export WIFI_STATIC_IP="192.168.1.50"
export WIFI_GATEWAY="192.168.1.1"
export WIFI_SUBNET="255.255.255.0"
pio run --project-dir firmware/camera -e esp32cam
# Upload only to the intended connected board:
pio run --project-dir firmware/camera -e esp32cam -t upload
pio device monitor --baud 115200
```

Commands in this guide run from the repository root. Serial output lists stream, capture, raw, status and diagnostic URLs.

### Pins

Defined in [camera_pins.h](include/camera_pins.h):

| Signal | GPIO |
| --- | ---: |
| `PWDN_GPIO_NUM` | 32 |
| `RESET_GPIO_NUM` | -1 |
| `XCLK_GPIO_NUM` | 0 |
| `SIOD_GPIO_NUM` | 26 |
| `SIOC_GPIO_NUM` | 27 |
| `Y9_GPIO_NUM` | 35 |
| `Y8_GPIO_NUM` | 34 |
| `Y7_GPIO_NUM` | 39 |
| `Y6_GPIO_NUM` | 36 |
| `Y5_GPIO_NUM` | 21 |
| `Y4_GPIO_NUM` | 19 |
| `Y3_GPIO_NUM` | 18 |
| `Y2_GPIO_NUM` | 5 |
| `VSYNC_GPIO_NUM` | 25 |
| `HREF_GPIO_NUM` | 23 |
| `PCLK_GPIO_NUM` | 22 |

### Source layout

| File | Responsibility |
| --- | --- |
| `platformio.ini` | Board, PSRAM and Wi-Fi build flags |
| `src/main.cpp` | Camera/Wi-Fi initialization and MJPEG server |
| `src/camera_control.cpp` | Still/raw/status routes and locked preview/still reinitialization |
| `src/http_body.h` | Chunked body sender, independently host-tested |
| `src/http_transport.cpp` | HTTP chunk wrapper and opt-in throughput diagnostics |
| `src/tcp_diag.cpp` | Development-only raw TCP diagnostic server |
| `scripts/camera_smoke.py` | Camera acceptance checks without flashing |
| `scripts/transport_probe.py` | Status/body transport measurements |
| `scripts/tcp_diag_probe.py` | Opt-in TCP transport measurements |

## HTTP interface and capture lifecycle

| Port | GET route | Response |
| --- | --- | --- |
| 81 | `/stream` | HQVGA `240x176` MJPEG preview |
| 80 | `/capture` | One VGA `640x480` JPEG for browsers/diagnostics |
| 80 | `/capture/raw` | One VGA RGB565 still, `application/octet-stream` |
| 80 | `/status` | Camera state, dimensions and capture/transport timings |
| 80 | `/debug/throughput` and memory-specific variants | Opt-in transport diagnostics |

At startup the serial port uses 115200 baud. GC2145 (`PID 0x2145`) is identified explicitly rather than assumed to be OV2640; it produces RGB565, not hardware JPEG. Preview uses `FRAMESIZE_HQVGA`, JPEG quality setting `12`, one framebuffer and `CAMERA_GRAB_WHEN_EMPTY`. Failed camera initialization waits 3 seconds before `ESP.restart()`. Wi-Fi uses `WiFi.config`; failure to connect after 40 attempts (approximately 20 seconds) restarts the board. Modem sleep is disabled. Port 81 serves only streaming; port 80 serves capture/status/optional diagnostics. Both HTTP tasks run on core 1 with response-header limit 32 and URI-handler limit 12; `loop()` performs no active work.

MJPEG content type is `multipart/x-mixed-replace;boundary=123456789000000000000987654321`. The stream tries the camera mutex for 200 ms; lock contention is not a frame failure. A failed `esp_camera_fb_get()` releases the lock and waits 20 ms; 10 consecutive failures end the stream. Driver buffers are copied for JPEG frames, or RGB565 is encoded in software at quality 60. The driver framebuffer is returned and the mutex released before socket transmission. Owned JPEG copies are freed after completion/failure; failed encoding never fabricates an image. Each send uses its own internal-RAM staging buffer, so still and stream HTTP tasks do not share mutable scratch space. Zero-length HTTP chunks are not written mid-stream because they close the response.

A serial summary is emitted every two seconds rather than for every frame. `/status` exposes `lastStreamCaptureMs`, `lastStreamJpegMs`, `lastStreamJpegBytes` and `lastStreamSendMs`. A disconnected stream handler exits; the client must reconnect. Normal operation should have one stream consumer, `smartline.vision`; Dashboard preview is relayed through web/Vision.

### Still acquisition and RGB565

`/capture` encodes at quality 85 (`frame2jpg` scale 1 smallest to 100 sharpest); preview remains quality 60. `/capture/raw` copies the same frame without JPEG conversion and requires exactly `width * height * 2` bytes: 614400 for VGA. The copy is made before preview restoration can release the driver buffer.

The installed `esp32-camera` revision `f0bb429` (Arduino-ESP32 2.0.17) allocates RGB565 buffers/DMA lengths once at camera initialization; classic ESP32 has `psram_mode` disabled. Changing only GC2145 `set_framesize` would cause `FB-SIZE` drops when shrinking or `FB-OVF` when growing. Each still therefore deinitializes/reinitializes the camera at the target size under the mutex, verifies actual dimensions, copies the frame and restores HQVGA preview. It does not switch resolution with `set_framesize`.

By default one VGA frame is discarded for exposure settling and zero preview frames are discarded. `SMARTLINE_STILL_STALE_FRAMES` and `SMARTLINE_PREVIEW_STALE_FRAMES` accept 0, 1 or 2. Frame size mismatches never return success. The mutex is released before the HTTP body is sent. Every error attempts preview restoration; `preview_restore_failed` suppresses the body. Requests do not call `ESP.restart()`.

RGB565 order matches `f0bb429` `to_jpg.cpp`: first byte `RRRRRGGG`, second `GGGBBBBB`; expanding to 8-bit channels leaves low bits zero. Do not interpret the body as little-endian `uint16`.

`FRAMESIZE_VGA` stills require PSRAM (approximately 614 KB). Without it, both still routes return `503 still_requires_psram` and configured still width is 0; no higher resolution is claimed. `SMARTLINE_STILL_FRAMESIZE` can select another `framesize_t`, still unavailable without PSRAM. Raw copies of at least 64 KB prefer PSRAM allocation.

Response headers include `X-Frame-Width`, `X-Frame-Height`, `X-Frame-Bytes`, `X-Body-Chunk-Bytes`, `X-Sensor-Pid`, `X-Frame-Size`, `X-Pixel-Format`; raw responses use `RGB565` and `X-Rgb565-Order: BE`, JPEG responses use `JPEG`. Timing headers are:

```text
X-Timing-Total-Ms, X-Timing-Mutex-Ms, X-Timing-Still-Deinit-Ms,
X-Timing-Still-Init-Ms, X-Timing-Stale1-Ms, X-Timing-Stale2-Ms,
X-Timing-Acquire-Ms, X-Timing-Jpeg-Ms, X-Timing-Copy-Ms,
X-Timing-Preview-Deinit-Ms, X-Timing-Preview-Init-Ms,
X-Timing-Preview-Stale-Ms, X-Timing-Preview-Restore-Ms, X-Timing-Http-Prep-Ms
```

JPEG time is 0 on the raw path. `X-Timing-Total-Ms` and `lastCaptureTotalMs` measure preparation, not body transmission. `lastStillAtMs` is uptime in milliseconds; `lastCapturePath` is `jpeg` or `raw`. Status contains no Wi-Fi name/password. Capture fields are:

```text
sensorPid, sensorName, psramDetected, previewWidth, previewHeight,
configuredStillWidth, configuredStillHeight, stillAvailable, lastStillAtMs,
lastStillWidth, lastStillHeight, lastCaptureError, cameraReady, lastCapturePath,
lastMutexWaitMs, lastStillDeinitMs, lastStillInitMs, lastStaleFrame1Ms,
lastStaleFrame2Ms, lastFrameAcquireMs, lastJpegEncodeMs, lastFrameCopyMs,
lastPreviewDeinitMs, lastPreviewInitMs, lastPreviewStaleMs,
lastPreviewRestoreMs, lastHttpPrepMs, lastCaptureTotalMs,
stillStaleFrames, previewStaleFrames
```

### Body transport and optional diagnostics

Bodies use 2048-byte chunks. LwIP `TCP_SND_BUF_DEFAULT` is 5760 with MSS 1436: two chunks plus HTTP framing fit; 4096-byte chunks wait for window drainage, while 1024 doubles calls for raw VGA. A per-transfer internal-RAM staging buffer avoids sharing scratch space between port 80 and 81 tasks or using another 2 KB of task stack. `malloc` places buffers larger than 4096 bytes in PSRAM, so JPEG storage is often there. Owned buffers stay alive until send completion/failure. `lastBodyWritten` counts only fully accepted chunks because `httpd_resp_send_chunk` does not expose partial bytes from a failed chunk.

`lastCaptureError` and `lastTransportError` are separate. Transport errors include `send_timeout`, `client_disconnected`, `send_failed` and `throughput_alloc_failed`; an empty value means the latest body completed. Transport fields are:

```text
lastTransportErrno, lastTransportEspErr, lastTransportTimeout,
lastBodyStartedAtMs, lastBodyBytes, lastBodyWritten, lastBodySendMs,
lastBodyChunkCount, lastBodyMaxChunkMs, lastBodyChunkBytes, lastTransportPath
```

`lastTransportPath` is `jpeg`, `raw`, `stream` or `debug`. Send timeout remains 15 seconds, receive timeout 5 seconds, task stack 20480 and `max_open_sockets` 7; task priority is unchanged. Image/diagnostic sockets enable `TCP_NODELAY`. These settings alone are not throughput evidence.

HTTP diagnostics are disabled unless built with `SMARTLINE_HTTP_DIAG=1`. They allocate test data without accessing the camera or encoding JPEG:

```text
GET /debug/throughput?bytes=32768&mem=internal&stage=0
GET /debug/throughput/internal?bytes=1024
GET /debug/throughput/psram?bytes=65536
```

Allowed sizes are 1024, 8192, 32768 and 65536. `stage=0` sends the allocated buffer directly; `stage=1` uses the production internal staging copy. Missing PSRAM returns `503 throughput_psram_unavailable`; failed allocation returns `503 throughput_alloc_failed` without silently choosing other memory. Headers are `X-Debug-Payload-Bytes`, `X-Debug-Chunk-Bytes`, `X-Debug-Memory` and `X-Debug-Staged`.

Raw TCP diagnostics on port 9001 are separately disabled unless `SMARTLINE_TCP_DIAG=1`. They send exactly 32768 internal-RAM bytes in 2048-byte writes without HTTP framing or a second copy. Short writes resume from accepted bytes; the connection closes at completion. Serial output records bytes, writes, longest write, send/connection durations and errno. `SO_SNDTIMEO` is 15 seconds. Fast TCP with slow HTTP points toward the HTTP send path; both being slow calls for TCP/LwIP/Wi-Fi/hardware investigation, not a claimed diagnosis.

```sh
# Development-only diagnostic build, with the Wi-Fi variables above already set:
PLATFORMIO_BUILD_FLAGS="-DSMARTLINE_TCP_DIAG=1" pio run --project-dir firmware/camera -e esp32cam -t upload
python3 firmware/camera/scripts/tcp_diag_probe.py --host 192.168.1.50 --port 9001
python3 firmware/camera/scripts/transport_probe.py --base http://192.168.1.50:81
```

## Hardware acceptance

Host tests do not flash a board:

```sh
python3 -m unittest discover -s firmware/camera/tests -p 'test_*.py'
python3 firmware/camera/scripts/camera_smoke.py --base http://192.168.1.50:81 --captures 3
# Extended acceptance on a flashed, connected board:
python3 firmware/camera/scripts/camera_smoke.py --base http://192.168.1.50:81 --captures 50 --raw
```

On hardware, confirm serial PID `0x2145`, PSRAM, HQVGA preview and configured still dimensions. Stream must remain `240x176` after captures. Raw captures must contain `640 * 480 * 2` bytes with RGB565/BE headers; JPEG must match `X-Frame-Bytes` and begin `FF D8`. Check `/status` after capture: `lastBodyWritten` must equal `lastBodyBytes`, or transport error must be populated. Dataset collection must store the actual still dimensions/source. Vision `/inspect` requests a still; it does not continuously infer on MJPEG.

`transport_probe.py` measures status, 1K/8K/32K/64K internal/PSRAM diagnostics, one JPEG and one raw capture; it neither flashes nor performs 50 captures. Timeouts are incomplete results. Add `--raw` to the smoke test to check raw as well as JPEG. Run extended capture acceptance after single-capture times are usable, then verify the stream/status again. Camera reset must retain its configured IP. These are procedures, not newly executed hardware results.

## Historical hardware observations

The following records are carried forward from earlier reports. They were not rerun while reorganizing documentation. TCS34725 is outside camera/production processing; local controller diagnostics are described in the [controller guide](../controller/README.md#local-sensorservo-diagnostics).

### Earlier transport measurements

Earlier firmware prepared VGA JPEG in approximately 2362 ms and raw RGB565 in approximately 1931 ms. A 29125-byte JPEG response stalled at 25390 bytes after 60 seconds, motivating chunked transport instead of one `httpd_resp_send`.

Another recorded board measurement completed 8192 internal-RAM bytes in 0.187 seconds. A 32768-byte internal buffer with `stage=1` took 8.358 seconds: `lastBodySendMs=8269`, `lastBodyChunkCount=16`, `lastBodyMaxChunkMs=2865`, `lastBodyWritten=32768`, empty `lastTransportError`. JPEG preparation was approximately 2.15 seconds; the remaining delay was in body transport. These older measurements do not describe current sustained throughput.

### Concurrent capture and Wi-Fi recovery acceptance

The reported board used static IP `192.168.1.150`, ESP32-CAM (`ESP32-D0WD-V3`), GC2145 (`PID 0x2145`), working PSRAM, CP2102 UART and 2.4 GHz Wi-Fi. Network credentials were not recorded.

- Initial JPEG stills: 640x480, 3/3 PASS.
- `camera_smoke.py --captures 1 --raw --concurrent`: VGA RGB565 and concurrent requests PASS.
- `camera_smoke.py --captures 50 --concurrent`: PASS.
- With an independent continuously read MJPEG stream, Pillow fully decoded 50/50 JPEG stills; 1,943,552 stream bytes were received, with 0 new stream-send errors and 0 reader errors.
- Wi-Fi interruption: OFFLINE at 90.9 seconds, ONLINE at 155.3 seconds; recovery PASS. A subsequent `/capture` returned valid 640x480 JPEG.

These results did not reproduce the shared-staging corruption while long-lived port 81 MJPEG overlapped port 80 still transfers. They do not establish sustained low latency or suitability for capturing moving conveyor objects. HTTP throughput diagnostics remain disabled without `SMARTLINE_HTTP_DIAG=1`.

### Recovery observations — 2026-11-02

Two developer-supplied hardware reports used ESP32-D0WD-V3, GC2145 (`0x2145`), PSRAM, HQVGA MJPEG and VGA still settings. Reported source commit `c9c2511a53bcd60a43703306fe91d2cee90e002a` is not a device-verified binary hash. Raw archives are not in this repository.

| Experiment | Valid JPEGs | Invalid JPEGs | Stream-failure counter | Longest frame interval |
| --- | ---: | ---: | --- | ---: |
| 120-second connected baseline | 207 | 0 | `0→1` after client closure at the end | 7.789 s |
| Three controlled 90-second client-disconnect runs | 670 | 0 | `+6` for six closures | 8.467 s |

- All 25 baseline status samples reported `cameraReady=true`; no client read errors or camera overflow warnings were reported. Mean rate was 1.73 frames/s; 16 frame intervals exceeded two seconds.
- The client closed midway through and at the end of each controlled run. Each closure matched one counter increment, with `client_disconnected` or `send_failed`. Valid frames resumed after reconnect; no reboot or client stream-read error was reported.
- Two `EV-VSYNC-OVF` warnings occurred in the second controlled run without a Wi-Fi outage. Valid nearby frames were received; related counter increments, corrupt JPEGs or a persistent stall were not demonstrated. `EV-EOF-OVF` was not seen in any of the three runs.
- Wi-Fi was not deliberately interrupted in these controlled runs. Client closure is not a Wi-Fi recovery test and does not explain the earlier counter rise from `5→9`.
- Multi-second frame intervals also occurred during connected periods. Exact frame loss and the source of delay remain undetermined. These are MJPEG observations, not direct still-capture duration measurements. Counter increments/warnings alone did not lead to firmware changes.

Archive references: `smartline-camera-recovery-baseline.tar.gz` (`camera-recovery-baseline-20261102T104218Z`) and `camera-recovery-disconnect-tests.tar.gz` (`camera-recovery-intentional-disconnect-20261102T105744Z/run-{1,2,3}`).
