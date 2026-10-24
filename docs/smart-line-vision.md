# SmartLine.Vision teknik referansı

`SmartLine.Vision`, ESP32-CAM MJPEG stream'ini OpenCV ile okur. Barkod/QR sonuçları MQTT üzerinden yayınlanmaya devam eder. Ürün inspection sonucu MQTT'ye yazılmaz; device key ile `POST /api/inspections` yapılır.

Canlı önizleme ve AI inspection ayrıdır. `main.py` her karede model çalıştırmaz. Canlı döngü yalnız MJPEG karesini `preview` rolüne yazar ve ara sıra `/status` okur. Still yakalamaz. Üç rol vardır. `preview` son `240x176` MJPEG karesidir ve `captureSource` değeri `mjpeg_preview` olur. `inspection` ve `dataset` önce ESP32-CAM `GET /capture/raw` yolunu ister. Bu gövde big-endian RGB565'tir: birinci bayt `RRRRRGGG`, ikinci bayt `GGGBBBBB`. Host bunu BGR diziye çevirir. Veri kümesi ve kare yanıtı için JPEG bu hostta, kalite 85 ile üretilir. Kaynak `esp32_capture_raw` olur. Ham yol yoksa, biçim veya uzunluk uyuşmazsa ya da NumPy yoksa `GET /capture` JPEG'ine düşülür ve kaynak `esp32_capture` olur. İkisi de başarısızsa yüksek çözünürlük uydurulmaz. `ALLOW_PREVIEW_FALLBACK` varsayılan `false` iken bu roller `409 still_capture_failed` döner ve görüntü saklanmaz. Bayrak `true` ise son önizleme açıkça `previewFallback=true` ile kullanılır. `PREFER_RAW_CAPTURE` varsayılan `true` dir. `STILL_CAPTURE_TIMEOUT` varsayılan 30 saniyedir ve bir performans iddiası değildir. İki sensör başlatmasını kapsayan istemci son tarihine yer bırakır. Inspection yalnız manuel, API veya simülasyon tetikleyicisiyle bir kare seçer. `hardware` tetikleyici, VL53L1X entegrasyonu yapılmadığı için reddedilir. Tarayıcı canlı önizlemesi değişmez.

Renk ölçümü tüm kareden değil, tespit kutusundan veya recipe ROI'sinden yapılır. Boyut, piksel genişliği ve kalibrasyondaki piksel/mm katsayısıyla görsel tahmindir. Derinlik üretilmez. Model dosyası yoksa kutu ve gecikme uydurulmaz. Gerçek bir YOLO26 `.pt` dosyası Ultralytics ile çalışır. `.onnx` ve OpenVINO dizini, Ultralytics yüklüyse onun AutoBackend yolunu, değilse ONNX Runtime veya OpenVINO ile aynı export sözleşmesini kullanır. Varsayılan export ham one-to-many başlıktır (`layout: raw`). `nms=False` export'u `(batch, max_det, 6)` uçtan uca düzendedir. Kutular kaynak görüntü pikseline `x, y, width, height, label, confidence` olarak normalize edilir.

Kontrol HTTP servisi `VISION_CONTROL_HOST` / `VISION_CONTROL_PORT` (varsayılan `0.0.0.0:8090`) üzerinde `GET /health`, `GET /frames/{preview|inspection|dataset}` ve `POST /inspect` sunar. Kare yanıtı `image/jpeg` olur ve `X-Frame-Width`, `X-Frame-Height`, `X-Capture-Source`, `X-Preview-Fallback` başlıklarını taşır. Veri kümesine ikinci bir kopya yazmaz. Ürün tarifi veritabanıdır. Vision, device key ile `GET /api/recipes/active/snapshot` okur. Anahtar süreç ortamında kalır ve health yanıtına yazılmaz. İstek gövdesindeki doğrulanmış tarif anlık görüntüsü varsa o kullanılır. API geçici olarak erişilemezse son başarılı anlık görüntü korunur. O da yoksa `RECIPE_PATH` dosyası yalnız geliştirme ve çevrimdışı yedektir. İki kaynak yarıştırılmaz. Her `POST /inspect` sonucu `recipeId`, `recipeVersion` ve `recipeSource` taşır. Tarif yoksa veya istek gövdesindeki tarif geçersizse `409` döner ve çıkarım çalışmaz. `VISION_REQUIRE_MQTT` varsayılan `true` olduğu için mevcut barkod MQTT başlangıcı korunur. Health yanıtı stream hostunu, still durumunu ve tarif sürümünü döner; kullanıcı adı, parola ve sertifika içeriği yoktur.

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
| `CAM_CAPTURE_URL`       | stream hostunda `/capture`      | İsteğe bağlı JPEG still adresi. Ham yol bunun yanındaki `/capture/raw` olur |
| `PREFER_RAW_CAPTURE`    | `true`                          | Inspection ve dataset önce ham RGB565 ister   |
| `STILL_CAPTURE_TIMEOUT` | `30`                            | Still isteğinin saniye cinsinden son tarihi   |
| `ALLOW_PREVIEW_FALLBACK`| `false`                         | Still yokken önizlemeyi dataset/inspection sayma |
| `API_BASE_URL`          | boş                             | Aktif tarif anlık görüntüsü için API kökü     |
| `DEVICE_KEY`            | yok                             | Yalnız Vision sürecinde duran cihaz anahtarı  |
| `RECIPE_PATH`           | boş                             | API yokken dosya yedeği                       |
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

Development compose'ta Vision opsiyonel profildir. Tam yığın komutu API'ye `Vision__HealthUrl=http://vision:8090/health` verir ve kontrol portunu yalnız development host'una açar. `MODEL_PATH` boştur; ağırlık yokken kutu üretilmez. Vision `API_BASE_URL=http://api:5047` ve `DEVICE_KEY` ile aktif tarifi okur. `RECIPE_PATH` gömülü DemoBox dosyasını yalnız API erişilemezken kullanır. Production aynı iç adresi kullanır, `8090` portunu yayınlamaz ve cihaz anahtarını tarayıcıya koymaz.

```bash
docker compose --env-file .env -f docker-compose.dev.yml --profile vision up
docker compose --env-file .env -f docker-compose.dev.yml -f docker-compose.vision.yml --profile vision up --build
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
- Görsel tespit yalnız yapılandırılmış bir model artifaktı varsa ve bir inspection tetiklenirse çalışır. Eksik ağırlıkta kutu üretilmez.
- Debounce process belleğindedir ve servis yeniden başladığında sıfırlanır.
- Vision credential ile CA/client certificate path'leri environment/read-only mount ile sağlanır; gerçek değerler repository'ye yazılmaz.
- Production certificate ve compose hazırlığı için [deployment.md](deployment.md) kullanılır.


## v0.2.0 runtime boundary

Production orchestration runs through the authenticated API. `VISION_SERVICE_KEY` protects frame/inspection requests with `X-Vision-Key`; Compose shares it with `Vision__ServiceKey`, defaulting to the existing device key. Standalone control binds loopback by default; containers explicitly bind internally, and the development host port is loopback-only.

Model classes come from the exported artifact contract, not a newly activated recipe. Activation checks artifact name, checksum, ordered classes, detection task, model version and input size against the configured artifact. Model replacement requires a process restart. Missing artifacts produce an unavailable response. A malformed contract disables model use; it does not invent detections. Run `training/validate_registry.py` against real artifacts before deployment.

Still capture shares one 30-second raw/JPEG budget by default; API capture has a configurable 35-second budget. Client cancellation prevents API persistence, although a native inference already running in Vision can finish locally. Body size, simultaneous requests and capture concurrency are bounded. JPEG fallback is decoded into pixels before inference. API-provided calibration applies only to matching image dimensions. Offline recipe fallback is for local diagnostics; API requests always carry the selected snapshot.

The independent Python dataset writer and observation publisher are deferred. API is the only persisted inspection/dataset authority. Detection preparation is supported; classification, segmentation and live model switching are deferred.
