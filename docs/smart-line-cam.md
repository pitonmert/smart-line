# SmartLine.Cam - Teknik Referans

Kaynak proje: `SmartLine.Cam/`

`SmartLine.Cam`, ESP32-CAM modülünü görüntü işleme yapmayan, yalnızca MJPEG stream üreten bir IP kamera olarak çalıştırır. Bu proje MQTT broker'a bağlanmaz, sensör verisi göndermez ve API komutu dinlemez. Tek görevi kamera görüntüsünü `http://<cam-ip>:81/stream` endpoint'i üzerinden yayınlamaktır.

Bu stream, `SmartLine.Vision` tarafından okunur. Barkod/QR çözme, MQTT publish ve API entegrasyonu `SmartLine.Vision` sorumluluğundadır.

## Proje Dosyaları

```text
SmartLine.Cam/
├── include/
│   └── camera_pins.h
├── src/
│   └── main.cpp
├── .gitignore
└── platformio.ini
```

| Dosya                   | Sorumluluk                                                              |
| ----------------------- | ----------------------------------------------------------------------- |
| `platformio.ini`        | PlatformIO ortamı, board ayarı, PSRAM flag'leri ve WiFi build flag'leri |
| `include/camera_pins.h` | AI Thinker ESP32-CAM pin tanımları                                      |
| `src/main.cpp`          | Kamera başlatma, WiFi bağlantısı ve MJPEG stream HTTP server            |
| `.gitignore`            | `.pio` ve PlatformIO/VS Code üretim dosyalarını dışarıda tutar          |

## PlatformIO Ortamı

Tek ortam vardır:

```ini
[env:esp32cam]
platform = espressif32
board = esp32cam
framework = arduino
monitor_speed = 115200
upload_speed = 921600
```

Build flag'leri:

```ini
build_flags =
    -DBOARD_HAS_PSRAM
    -mfix-esp32-psram-cache-issue
    -DWIFI_SSID=\"${sysenv.WIFI_SSID}\"
    -DWIFI_PASS=\"${sysenv.WIFI_PASS}\"
    -DWIFI_STATIC_IP=\"${sysenv.WIFI_STATIC_IP}\"
    -DWIFI_GATEWAY=\"${sysenv.WIFI_GATEWAY}\"
    -DWIFI_SUBNET=\"${sysenv.WIFI_SUBNET}\"
```

WiFi bilgileri kaynak koda veya `Config.h` dosyasına yazılmaz. Derleme sırasında ortam değişkenlerinden alınır. Bu değişkenlerden biri eksikse `main.cpp` içindeki `#error` direktifleri derlemeyi durdurur.

Gerekli ortam değişkenleri:

| Değişken         | Açıklama                          |
| ---------------- | --------------------------------- |
| `WIFI_SSID`      | ESP32-CAM'in bağlanacağı WiFi ağı |
| `WIFI_PASS`      | WiFi parolası                     |
| `WIFI_STATIC_IP` | Kameranın sabit IP adresi         |
| `WIFI_GATEWAY`   | Ağ gateway adresi                 |
| `WIFI_SUBNET`    | Subnet mask                       |

Sabit IP zorunluya yakın bir gereksinimdir. `SmartLine.Vision`, kamera stream URL'ini `CAM_STREAM_URL` ile bildiği için kamera IP'sinin reset sonrası değişmemesi gerekir.

## Donanım Notları

- Hedef kart `esp32cam` board tanımıdır.
- Kod AI Thinker ESP32-CAM pin dizilimini kullanır.
- ESP32-CAM modüllerinde genellikle USB portu yoktur; yükleme için FTDI/USB-TTL dönüştürücü gerekir.
- Yükleme sırasında `IO0` pini `GND`'ye alınır; yükleme sonrası bağlantı kaldırılıp kart resetlenir.
- Kart 5V pininden beslenmelidir. 3.3V besleme kamera ve WiFi için yetersiz kalabilir.
- PSRAM flag'leri etkin olmalıdır; aksi halde yüksek çözünürlükte kamera init başarısız olabilir.

## Kamera Pinleri

`include/camera_pins.h`, AI Thinker ESP32-CAM için pinleri tanımlar:

| Sinyal           | GPIO |
| ---------------- | ---: |
| `PWDN_GPIO_NUM`  |   32 |
| `RESET_GPIO_NUM` |   -1 |
| `XCLK_GPIO_NUM`  |    0 |
| `SIOD_GPIO_NUM`  |   26 |
| `SIOC_GPIO_NUM`  |   27 |
| `Y9_GPIO_NUM`    |   35 |
| `Y8_GPIO_NUM`    |   34 |
| `Y7_GPIO_NUM`    |   39 |
| `Y6_GPIO_NUM`    |   36 |
| `Y5_GPIO_NUM`    |   21 |
| `Y4_GPIO_NUM`    |   19 |
| `Y3_GPIO_NUM`    |   18 |
| `Y2_GPIO_NUM`    |    5 |
| `VSYNC_GPIO_NUM` |   25 |
| `HREF_GPIO_NUM`  |   23 |
| `PCLK_GPIO_NUM`  |   22 |

## Çalışma Akışı

`src/main.cpp` şu sırayla çalışır:

1. Serial portu `115200` baud ile başlatır.
2. `camera_config_t` nesnesini AI Thinker pinleriyle doldurur.
3. Pixel formatını `PIXFORMAT_JPEG` olarak ayarlar.
4. PSRAM varsa `FRAMESIZE_VGA`, JPEG quality `10`, framebuffer count `2` kullanır.
5. PSRAM yoksa `FRAMESIZE_QVGA`, JPEG quality `15`, framebuffer count `1` kullanır.
6. `esp_camera_init` başarısız olursa 3 saniye bekleyip `ESP.restart()` çağırır.
7. `WIFI_STATIC_IP`, `WIFI_GATEWAY`, `WIFI_SUBNET` değerleriyle `WiFi.config` çağırır.
8. `WiFi.begin` ile ağa bağlanır.
9. WiFi bağlantısı 40 denemede, yani yaklaşık 20 saniyede kurulamazsa kartı yeniden başlatır.
10. HTTP stream server'ı başlatır.
11. Serial monitor'a stream URL'ini yazar.

`loop()` içinde aktif iş yapılmaz; ana iş stream handler içinde yürür.

## MJPEG Stream Server

Stream server `esp_http_server.h` ile kurulur.

| Özellik       | Değer                                                               |
| ------------- | ------------------------------------------------------------------- |
| Port          | `81`                                                                |
| Endpoint      | `/stream`                                                           |
| Method        | `GET`                                                               |
| Content-Type  | `multipart/x-mixed-replace;boundary=123456789000000000000987654321` |
| Frame formatı | JPEG                                                                |

`stream_handler` her döngüde:

1. `esp_camera_fb_get()` ile frame alır.
2. Frame alınamazsa 100 ms bekleyip tekrar dener.
3. Arka arkaya 10 frame alınamazsa stream'i sonlandırır.
4. Her frame için `Content-Type: image/jpeg` ve `Content-Length` header parçasını yazar.
5. JPEG buffer'ını chunk olarak gönderir.
6. Boundary separator gönderir.
7. Frame buffer'ı `esp_camera_fb_return` ile geri verir.

## Derleme ve Yükleme

Örnek lokal kullanım:

```bash
export WIFI_SSID="YOUR_WIFI"
export WIFI_PASS="YOUR_PASSWORD"
export WIFI_STATIC_IP="192.168.1.50"
export WIFI_GATEWAY="192.168.1.1"
export WIFI_SUBNET="255.255.255.0"

cd SmartLine.Cam
pio run -e esp32cam -t upload
pio device monitor --baud 115200
```

Serial monitor'da şu formatta URL görülmelidir:

```text
Kamera yayini hazır! Sunu kullan: 'http://192.168.1.50:81/stream'
```

## Sistem İçindeki Yeri

```text
SmartLine.Cam
    ESP32-CAM
      |
      | HTTP MJPEG
      v
SmartLine.Vision
      |
      | MQTT smartline/sensors/barcode
      v
Mosquitto Broker
      |
      v
SmartLine.API
```

## Kabul Kriterleri

- Kod `pio run -e esp32cam` ile derlenir.
- FTDI üzerinden karta yüklenir.
- Serial monitor'da WiFi bağlantısı ve stream URL'i görünür.
- Tarayıcıda `http://<cam-ip>:81/stream` açıldığında canlı görüntü gelir.
- `SmartLine.Vision`, `CAM_STREAM_URL` ile stream'e bağlanabilir.
- Kamera reset sonrası aynı IP ile erişilebilir kalır.

## Sınırlamalar

- MQTT yoktur.
- API ile doğrudan konuşmaz.
- Renk sensörü bu projede değildir; renk bilgisi ana ESP32 firmware sorumluluğundadır.
- Aynı anda birden fazla stream tüketicisi ESP32-CAM'i zorlayabilir. Normal kullanımda tek tüketici `SmartLine.Vision` olmalıdır.
- Kamera bağlantısı koptuğunda stream handler sonlanır; yeniden bağlanma istemci tarafında yapılmalıdır.
