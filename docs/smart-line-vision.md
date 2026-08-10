# SmartLine.Vision teknik referansı

`SmartLine.Vision`, ESP32-CAM MJPEG stream'ini OpenCV ile okuyup PyZbar ile barkod/QR çözer. Tespit edilen değerleri API'ye doğrudan HTTP ile değil, MQTT üzerinden gönderir.

## Akış

```text
ESP32-CAM --MJPEG--> Vision --MQTT/TLS--> Mosquitto --> API
```

`src/main.py` kamera reconnect ve process lifecycle'ını, `src/qr_reader.py` frame skip/debounce davranışını, `src/mqtt_publisher.py` ise güvenli broker bağlantısını yönetir.

## MQTT yapılandırması

| Environment variable    | Varsayılan                      | Açıklama                                      |
| ----------------------- | ------------------------------- | --------------------------------------------- |
| `MQTT_BROKER_HOST`      | `localhost`                     | Broker hostname; sertifika SAN'ıyla eşleşmeli |
| `MQTT_BROKER_PORT`      | `8883`                          | Zorunlu TLS portu                             |
| `MQTT_CLIENT_ID`        | `smartline-vision`              | Client id                                     |
| `MQTT_USERNAME`         | `smartline-vision`              | ACL principal                                 |
| `MQTT_PASSWORD`         | yok                             | Zorunlu secret                                |
| `MQTT_CA_CERT_PATH`     | `/run/smartline/certs/ca.crt`   | Yerel CA public sertifikası                   |
| `MQTT_CLIENT_CERT_PATH` | yok                             | Zorunlu Vision client certificate             |
| `MQTT_CLIENT_KEY_PATH`  | yok                             | Zorunlu Vision private key                    |
| `TOPIC_BARCODE`         | `smartline/sensors/barcode`     | Tek izinli publish topic'i                    |
| `CAM_STREAM_URL`        | `http://192.168.1.50:81/stream` | Kamera MJPEG URL'i                            |
| `SHOW_PREVIEW`          | `false`                         | Lokal OpenCV penceresi                        |

Servis yalnız karşılıklı doğrulanan TLS `8883` bağlantısını kabul eder; broker hostname doğrulaması kapatılamaz ve Vision kendi client certificate/private key çiftini sunar. Eksik parola, yanlış port veya okunamayan CA/client TLS dosyası startup hatasıdır. Mosquitto ayrıca username/password doğrular; ACL `smartline-vision` kullanıcısına yalnız barcode topic'ini yayınlama izni verir.

## MQTT payload

```json
{
  "barcode": "BC001",
  "color": null
}
```

Publish QoS 1 ve retained olmayan mesajla yapılır. Renk tespiti Vision kapsamında değildir; `color` daima `null` gönderilir. Broker bağlantısı yoksa barcode başarılı yayınlanmış gibi loglanmaz.

## Container

Production compose CA ve yalnız Vision'a ait client cert/key kaynaklarını read-only mount eder ve parolayı environment secret olarak verir. Entrypoint host'ta `0600` olan key'i tmpfs'e `0400` kopyaladıktan sonra process root olmayan `smartline` kullanıcısıyla çalışır; CA private key container'a girmez.

```bash
docker compose build vision
docker compose up -d vision
```

Development compose'ta Vision opsiyonel profildir:

```bash
docker compose --env-file .env -f docker-compose.dev.yml --profile vision up
```

## Lokal çalışma

```bash
cd SmartLine.Vision
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

export MQTT_BROKER_HOST=localhost
export MQTT_BROKER_PORT=8883
export MQTT_USERNAME=smartline-vision
export MQTT_PASSWORD='<vision-principal-password>'
export MQTT_CA_CERT_PATH=../infra/certs/generated/ca.crt
export MQTT_CLIENT_CERT_PATH=../infra/certs/generated/smartline-vision.crt
export MQTT_CLIENT_KEY_PATH=../infra/certs/generated/smartline-vision.key
python src/main.py
```

Headless sistemlerde `SHOW_PREVIEW=false` kullanılmalıdır.

## Sınırlar

- Kamera stream'i halen fiziksel LAN üzerinde HTTP'dir; MQTT hattı TLS ile korunur.
- Görsel kusur tespiti aktif ürün davranışı değildir; mevcut üretim akışı yalnız barkod/QR okur.
- Debounce process belleğindedir ve servis yeniden başladığında sıfırlanır.
- Vision credential ile CA/client certificate path'leri environment/read-only mount ile sağlanır; gerçek değerler repository'ye yazılmaz.
- Production certificate ve compose hazırlığı için [deployment.md](deployment.md) kullanılır.
