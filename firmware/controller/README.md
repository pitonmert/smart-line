# ESP32 controller

The controller reads temperature, humidity and flame sensors, publishes environment/fire state to the web/system process over MQTT, and receives conveyor, speed and fan commands. Product color comes from camera/Vision observations. TCS34725 is used only in the separate local `sensor-test` firmware.

See the [project setup and certificate instructions](../../README.md#production). Commands below run from the repository root.

## Setup and pins

```text
firmware/controller/
  include/Config.h.example
  include/MqttTlsConfig.h.example
  scripts/generate-mqtt-config.sh
  src/firmware/main.cpp
  src/sensor-test/main.cpp
  platformio.ini
```

```sh
cp firmware/controller/include/Config.h.example firmware/controller/include/Config.h
# Edit Config.h with 2.4 GHz Wi-Fi settings and the broker LAN address.
export MQTT_FIRMWARE_PASSWORD='<firmware-principal-password>'
./firmware/controller/scripts/generate-mqtt-config.sh
```

The broker address must match the address used to generate the LAN certificate. MQTT port is 8883. After local CA generation, the script reads `deploy/certs/generated/ca.crt`, `smartline-firmware.crt` and `smartline-firmware.key` and writes the firmware credentials/certificates to `include/MqttTlsConfig.h` with mode `0600`. Both local headers are ignored by Git. ESP32 verifies the broker CA and hostname/IP SAN, presents its client certificate and authenticates with username/password. Failure to synchronize certificate time through NTP causes a safe restart.

| Constant | GPIO | Use |
| --- | ---: | --- |
| `COLOR_LED_PIN` | 26 | Color-sensor illumination, `sensor-test` only |
| `FLAME_PIN` | 34 | Analog flame sensor |
| `FAN_PIN` | 27 | Fan output |
| `BUZZER_PIN` | 25 | Fire alarm |
| `SERVO_PIN` | 13 | Prototype sorting servo |

AHT10 uses `Wire` on GPIO 21/22. Only `sensor-test` initializes `Wire1` on GPIO 32/33 for TCS34725. Normal firmware does not initialize that bus or read the color sensor; it can remain physically connected.

## MQTT protocol

The `smartline-firmware` principal requires local-CA validation, a client certificate and a password. ACLs permit publishing only environment/fire topics and subscribing to conveyor/speed/fan command topics.

| Firmware publication | Payload | Delivery |
| --- | --- | --- |
| `smartline/sensors/environment` | `{"temperature":26.3,"humidity":48.7}` | Approximately every 2.5 seconds |
| `smartline/sensors/fire` | `{"isDetected":true,"deviceEpoch":"0123456789abcdef0123456789abcdef","fireGeneration":4}` | Retained on connection and state changes |

Local flame detection sets `emergencyLatched=true`, clears the running command and returns the servo to `0°` without waiting for MQTT or the server. A random 128-bit `deviceEpoch`, latch and counters are stored together in one validated ESP32 NVS safety record. Reboot does not clear the latch. New/empty NVS starts latched; missing, corrupt or unwritable records cause a persistence fault and block start. Failed fire publications are retried while connected until successful. The server processes `(deviceEpoch, fireGeneration)` idempotently.

Conveyor desired state uses `smartline/commands/stop`, QoS 1, retained:

| Operation | Payload | Effect |
| --- | --- | --- |
| Normal stop | `{"isRunning":false,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` | Stops without changing latch/reset generation |
| Emergency stop / fire | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` | Latches emergency state and persists it |
| Manual reset | `{"isRunning":false,"emergency":false,"resetGeneration":N+1,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` | Clears the latch only for a newer reset, matching E/F and clear local fire; does not start |
| Start | `{"isRunning":true,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` | Runs only with matching epoch/generation and no fire/latch/persistence fault |

`resetGeneration` increases only for an explicit server reset. `acknowledgedFireEpoch` must exactly match the current device epoch, and `acknowledgedFireGeneration` must cover the local fire generation. The reset must also be newer than the last applied reset stored in NVS. Start requires the same epoch/generation match.

If local fire is active or the server acknowledgement is behind, firmware consumes the new reset generation without releasing the latch. After the sensor clears and the server observes the event, another manual reset is required. Erasing NVS or replacing the device creates a new epoch, so stale retained reset/start messages cannot clear the new latch. Normal stop is not a reset.

`smartline/commands/speed` accepts `{"speed":75}`, clamps to `0..100` and never clears running/emergency state. This prototype does not connect speed to a physical motor PWM output. `smartline/commands/fan` accepts `{"active":true}`.

## Safety state and output priority

The prototype treats servo `0°` as its neutral/safe position:

```text
localFireActive || emergencyLatched || !commandRunning
    -> servo returns to 0° on every loop
```

Every loop reads the flame sensor first. Active fire latches the stop, enables the buzzer and sets servo `0°`; the latch holds that position after fire clears. A stopped conveyor also holds `0°`. Even with all interlocks clear and running requested, production firmware keeps the servo at `0°` because physical sorting commands are not implemented. The former color-triggered `90°` action is absent from production.

Firmware boots safely stopped and clears the running command after broker disconnection. Reset clears only the latch; start is a separate command. The servo is not a power-cutoff PWM/relay output for the belt motor, and the software latch does not replace a hardwired emergency stop, safety relay or machine safety circuit.

## Build, upload and monitor

```sh
pio run --project-dir firmware/controller -e firmware
pio run --project-dir firmware/controller -e sensor-test
# Upload only to the intended connected board:
pio run --project-dir firmware/controller -e firmware -t upload
pio device monitor --baud 115200
```

### Local sensor/servo diagnostics

Upload `sensor-test` instead of production firmware to observe TCS34725 values:

```sh
pio run --project-dir firmware/controller -e sensor-test -t upload
pio device monitor --baud 115200
```

This environment has no Wi-Fi or MQTT. RGB/clear readings appear only on Serial Monitor. For hardware testing, `R > G + 15`, `R > B + 15` and `clear > 20` move the servo to `90°`; otherwise it stays at `0°`. Fire or an unavailable color reading forces `0°`. Fan control follows temperature, buzzer control follows fire; color readings do not affect either output.

Failed TCS34725 initialization skips readings. I²C timeout is 50 ms. A later access error or incomplete read disables color monitoring until restart and reports `unavailable`, not a fabricated zero measurement. Illumination is enabled only during measurement and disabled on error paths. A color-sensor failure does not stop the other sensor workflows.

```sh
python3 -m unittest discover -s firmware/controller/tests -p 'test_*.py' -v
```

Host tests require a C++17 compiler and execute the real diagnostics code with fake I²C, sensor and GPIO drivers. Builds/simulations do not verify the soldered board, electrical failures or real actuator behavior.

## Operating boundaries

- One active web/system instance owns desired state; firmware waits for retained state after broker reconnect.
- Emergency latch, local fire generation and last reset generation persist in NVS. Reboot is not a reset; NVS failures block start.
- Local credentials and generated CA headers are not committed. Credentials previously exposed outside the current files require rotation in the corresponding external system.
