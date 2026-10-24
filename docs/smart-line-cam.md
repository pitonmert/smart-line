# SmartLine.Cam - Teknik Referans

Kaynak proje: `SmartLine.Cam/`

`SmartLine.Cam`, ESP32-CAM modülünü görüntü işleme yapmayan bir IP kamera olarak çalıştırır. Bu proje MQTT broker'a bağlanmaz, sensör verisi göndermez ve API komutu dinlemez. Canlı önizleme `http://<cam-ip>:81/stream` üzerinden HQVGA kalır. Tanı ve tarayıcı JPEG'i `http://<cam-ip>/capture` yolundadır. Vision'un tercih ettiği hızlı kare `http://<cam-ip>/capture/raw` üzerinden ham RGB565'tir. Sağlık ve son yakalama süreleri `http://<cam-ip>/status` üzerindedir.

Bu stream, `SmartLine.Vision` tarafından okunur. Barkod/QR çözme, MQTT publish ve API entegrasyonu `SmartLine.Vision` sorumluluğundadır.

## Proje Dosyaları

```text
SmartLine.Cam/
├── include/
│   └── camera_pins.h
├── src/
│   ├── camera_control.cpp
│   ├── camera_control.h
│   ├── http_body.h
│   ├── http_transport.cpp
│   ├── http_transport.h
│   ├── tcp_diag.cpp
│   ├── tcp_diag.h
│   └── main.cpp
├── scripts/
│   ├── camera_smoke.py
│   ├── tcp_diag_probe.py
│   └── transport_probe.py
├── tests/
│   ├── test_camera_smoke.py
│   ├── test_http_body.cpp
│   ├── test_http_body.py
│   ├── test_tcp_diag.cpp
│   ├── test_tcp_diag.py
│   ├── test_tcp_diag_probe.py
│   └── test_transport_probe.py
├── .gitignore
└── platformio.ini
```

| Dosya                   | Sorumluluk                                                              |
| ----------------------- | ----------------------------------------------------------------------- |
| `platformio.ini`        | PlatformIO ortamı, board ayarı, PSRAM flag'leri ve WiFi build flag'leri |
| `include/camera_pins.h` | AI Thinker ESP32-CAM pin tanımları                                      |
| `src/main.cpp`          | Kamera başlatma, WiFi bağlantısı ve MJPEG stream HTTP server            |
| `src/camera_control.cpp`| `/capture`, `/capture/raw` ve `/status`; önizleme ile still arasında kilitli yeniden başlatma |
| `src/http_body.h`       | Parçalı gövde göndericisi. ESP bağımlılığı yoktur; host testi derler   |
| `src/tcp_diag.cpp`      | Geliştirme derlemesinde port 9001 ham TCP tanı göndericisi              |
| `src/http_transport.cpp`| `httpd_resp_send_chunk` sarmalayıcısı ve tanı aktarım uçları            |
| `scripts/camera_smoke.py` | Kartı flaşlamadan HTTP kabul kontrolleri                             |
| `scripts/transport_probe.py` | Kameraya dokunmadan gövde aktarımını ölçen tanı betiği             |
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
3. Pixel formatını `PIXFORMAT_RGB565` olarak ayarlar. GC2145 (PID `0x2145`) donanım JPEG üretmez.
4. Canlı önizleme çözünürlüğünü `FRAMESIZE_HQVGA` (`240x176`) tutar. JPEG quality `12`, framebuffer count `1` ve `CAMERA_GRAB_WHEN_EMPTY` kullanılır. Bu çözünürlük, yazılım JPEG dönüşümünün kare hızını çökertmemesi için seçilmiştir.
5. Algılanan PID `GC2145_PID` ise GC2145 olarak kaydedilir. OV2640 varsayılmaz.
6. `esp_camera_init` başarısız olursa 3 saniye bekleyip `ESP.restart()` çağırır.
7. `WIFI_STATIC_IP`, `WIFI_GATEWAY`, `WIFI_SUBNET` değerleriyle `WiFi.config` çağırır.
8. `WiFi.begin` ile ağa bağlanır.
9. WiFi bağlantısı 40 denemede, yani yaklaşık 20 saniyede kurulamazsa kartı yeniden başlatır.
10. HTTP server'ı port 81'de başlatır ve `/stream`, `/capture`, `/capture/raw`, `/status`, `/debug/throughput`, `/debug/throughput/internal`, `/debug/throughput/psram` yollarını kaydeder. Yanıt başlığı sınırı 32, URI işleyici sınırı 12'dir. Sunucu görevi çekirdek 1'de çalışır.
11. Serial monitor'a stream, capture, status ve tanı aktarım URL'lerini yazar.

`loop()` içinde aktif iş yapılmaz; ana iş stream handler içinde yürür.

## MJPEG Stream Server

Stream server `esp_http_server.h` ile kurulur.

| Özellik       | Değer                                                               |
| ------------- | ------------------------------------------------------------------- |
| Port          | `81`                                                                |
| Endpoint      | `/stream`                                                           |
| Method        | `GET`                                                               |
| Content-Type  | `multipart/x-mixed-replace;boundary=123456789000000000000987654321` |
| Frame formatı | RGB565 yakalanır, yazılımla JPEG'e çevrilir, MJPEG olarak yayınlanır |

`stream_handler` her döngüde:

1. Kamera kilidini 200 ms içinde alamazsa kısa bekleyip sürer. Bu bir kare hatası sayılmaz.
2. `esp_camera_fb_get()` ile frame alır. Alınamazsa kilidi bırakır, 20 ms bekler ve sayacı artırır. Arka arkaya 10 frame alınamazsa stream sonlanır.
3. Frame zaten `PIXFORMAT_JPEG` ise sürücü tamponunu kopyalar. `fb->buf` serbest bırakılmaz.
4. GC2145 RGB565 frame'ini kalite 60 ile yazılım JPEG'ine çevirir. Dönüşüm başarısız olursa sahte bir görüntü üretmez.
5. Sürücü tamponunu `esp_camera_fb_return` ile geri verir ve kamera kilidini JPEG gönderilmeden açar.
6. Parça başlığını, JPEG baytlarını 2048 baytlık parçalar halinde ve boundary ayırıcısını yazar. Sıfır uzunluklu HTTP chunk yazılmaz; o çağrı tüm yanıtı kapatır.
7. Gönderim bitince veya kesilince sahip olunan JPEG kopyası serbest bırakılır.

İki saniyede bir seri özete kare sayısı, alma süresi, JPEG süresi, JPEG baytları ve gönderim süresi yazılır. Her kare için ayrı satır yazılmaz. `GET /status` son karenin `lastStreamCaptureMs`, `lastStreamJpegMs`, `lastStreamJpegBytes` ve `lastStreamSendMs` değerlerini tutar.

Stream, `esp_camera_fb_get` ile `esp_camera_fb_return` arasında aynı kilidi tutar. JPEG kopyası sokete yazılırken kilit tutulmaz. Böylece still yakalama, yavaş bir gönderimin bitmesini beklemeden sensörü kapatabilir.

## Tek kare yakalama

`GET /capture` tam olarak bir `image/jpeg` döner. GC2145 RGB565 üretir. Firmware RGB565 kareyi kalite 85 ile yazılım JPEG'ine çevirir. Bu ölçek `frame2jpg` ölçeğidir: 1 en küçük dosya, 100 en keskin görüntüdür. Canlı yayın kalitesi 60 olarak kalır. Bu yol tarayıcı, tanı ve uyumluluk içindir.

`GET /capture/raw` aynı VGA kareyi JPEG'e çevirmeden döner. İçerik türü `application/octet-stream` olur. Gövde, kopyalanmış RGB565 baytlarıdır ve uzunluğu `width * height * 2` olmak zorundadır. Kopya, önizleme geri yüklenmeden önce alınır. Böylece kamera kapanırken tampon silinmez.

Kurulu `esp32-camera` sürümü `f0bb429` (Arduino-ESP32 2.0.17) RGB565 tamponunu ve DMA uzunluğunu `esp_camera_init` sırasında bir kez ayırır. Klasik ESP32'de `psram_mode` kapalıdır. JPEG olmayan bir kare `fb_size` bayt değilse sürücü onu kuyruğa koymaz (`FB-SIZE`). Daha uzun bir kare DMA kopyasını durdurur (`FB-OVF`). GC2145 `set_framesize` yalnız sensör penceresini değiştirir. VGA tamponu ayırıp önizlemeyi `set_framesize(HQVGA)` ile küçültmek her önizleme karesini düşürür. HQVGA tamponunu VGA'ya büyütmek ise taşar. Bu yüzden her still, kilit altındayken kamerayı kapatıp hedef boyutta yeniden başlatır. Bu güvenli yoldur. `set_framesize` ile çözünürlük değiştirilmez.

Yeniden başlatma yeni bir tampon ayırdığı ve sürücü kısa RGB565 kareyi zaten reddettiği için eski kareler boyut temizlemek için gerekmez. Varsayılan olarak VGA tarafında bir kare atılır. Bu, GC2145 yazmaç tablosu yeniden yüklendikten sonra pozlamanın oturması içindir. Önizleme tarafında varsayılan atılan kare sayısı sıfırdır. Yeni HQVGA tamponunda VGA baytı kalmaz. Canlı yayın sonraki karelerde pozlamayı oturtur. `SMARTLINE_STILL_STALE_FRAMES` ve `SMARTLINE_PREVIEW_STALE_FRAMES` derleme sabitleridir ve 0, 1 veya 2 olabilir. Boyut yine de ölçülür. Uyuşmazlıkta `200` dönülmez.

Ölçülen genişlik ve yükseklik yapılandırılan boyutla karşılaştırılır, kare kopyalanır, sonra önizleme HQVGA olarak geri yüklenir. Kilit, HTTP gövdesi gönderilmeden bırakılır. Gövde aktarımı hazırlık süresine yazılmaz. Her hatada önizleme geri yüklenmeye çalışılır. Önizleme geri yüklenemezse gövde gönderilmez ve `preview_restore_failed` döner. İstek içinden `ESP.restart` çağrılmaz.

Başarılı yanıt başlıkları: `X-Frame-Width`, `X-Frame-Height`, `X-Frame-Bytes`, `X-Body-Chunk-Bytes`, `X-Sensor-Pid`, `X-Frame-Size`, `X-Pixel-Format`. Ham yanıtta `X-Pixel-Format` değeri `RGB565` ve `X-Rgb565-Order` değeri `BE` olur. JPEG yanıtında piksel biçimi `JPEG` olur. Süre başlıkları `X-Timing-Total-Ms`, `X-Timing-Mutex-Ms`, `X-Timing-Still-Deinit-Ms`, `X-Timing-Still-Init-Ms`, `X-Timing-Stale1-Ms`, `X-Timing-Stale2-Ms`, `X-Timing-Acquire-Ms`, `X-Timing-Jpeg-Ms`, `X-Timing-Copy-Ms`, `X-Timing-Preview-Deinit-Ms`, `X-Timing-Preview-Init-Ms`, `X-Timing-Preview-Stale-Ms`, `X-Timing-Preview-Restore-Ms` ve `X-Timing-Http-Prep-Ms` şeklindedir. Ham yolda JPEG süresi 0'dır. Bu sayılar fiziksel doğrulama değildir. Kart yeni firmware ile ölçülünceye kadar gecikme iddiası yoktur.

RGB565 bayt sırası, aynı `f0bb429` `to_jpg.cpp` dönüştürücüsüyle aynıdır. Birinci bayt `RRRRRGGG`, ikinci bayt `GGGBBBBB` dir. Kırmızı, yeşil ve mavi 8 bite yayılırken düşük bitler sıfırlanır. Bu, küçük endian `uint16` okuması değildir.

Varsayılan still boyu `FRAMESIZE_VGA` (`640x480`) ve yalnız PSRAM varken denenir. VGA RGB565 yaklaşık 614 KB'tır ve dahili DRAM'e güvenli sığmaz. PSRAM yoksa her iki still yolu da `503` ve `still_requires_psram` döner; yapılandırılan still genişliği 0 rapor edilir. Daha yüksek bir çözünürlük sessizce iddia edilmez. Derleme sırasında `SMARTLINE_STILL_FRAMESIZE` başka bir `framesize_t` değeriyle değiştirilebilir. PSRAM yoksa bu değer yine kullanılmaz. Ham kopya 64 KB ve üzeri olduğunda önce PSRAM'den ayrılır.

`GET /status` gizli bilgi içermez. Yakalamayı anlatan alanlar: `sensorPid`, `sensorName`, `psramDetected`, `previewWidth`, `previewHeight`, `configuredStillWidth`, `configuredStillHeight`, `stillAvailable`, `lastStillAtMs`, `lastStillWidth`, `lastStillHeight`, `lastCaptureError`, `cameraReady`, `lastCapturePath`, `lastMutexWaitMs`, `lastStillDeinitMs`, `lastStillInitMs`, `lastStaleFrame1Ms`, `lastStaleFrame2Ms`, `lastFrameAcquireMs`, `lastJpegEncodeMs`, `lastFrameCopyMs`, `lastPreviewDeinitMs`, `lastPreviewInitMs`, `lastPreviewStaleMs`, `lastPreviewRestoreMs`, `lastHttpPrepMs`, `lastCaptureTotalMs`, `stillStaleFrames`, `previewStaleFrames`. `lastCaptureTotalMs` hazırlık süresidir ve soket aktarımını içermez. `lastStillAtMs` açılıştan beri milisaniyedir. `lastCapturePath` `jpeg` veya `raw` olur. Soket alanları aşağıdadır ve `lastCaptureError` ile karıştırılmaz. Wi-Fi adı ve parolası yoktur.

## HTTP gövde aktarımı

Önceki firmware'de VGA JPEG hazırlığı kartta yaklaşık 2362 ms, ham RGB565 hazırlığı yaklaşık 1931 ms ölçüldü. Aynı kartta 29125 baytlık bir JPEG yanıtı 60 saniyede 25390 baytta kaldı. Bu yüzden gövde, tek bir `httpd_resp_send` ile gönderilmez.

Kurulu LwIP `TCP_SND_BUF_DEFAULT` değeri 5760 ve MSS 1436'dır. Parça boyu 2048'dir. İki parça, HTTP chunk çerçevesiyle birlikte 5760 baytlık gönderim tamponuna sığar. 4096 baytlık parça her yazımdan sonra pencerenin boşalmasını bekler. 1024 de sığar fakat 614400 baytlık ham karede sistem çağrısını ikiye katlar. Üretim yolu her parçayı dahili BSS çizik tamponuna kopyalayıp onu yazar. `malloc` 4096 bayttan büyük tamponları PSRAM'e koyduğu için JPEG de çoğu zaman PSRAM'dedir. Başarısız bir chunk'ın içinde kalan kısmi bayt sayısı `httpd_resp_send_chunk` tarafından bildirilmez. `lastBodyWritten` yalnız tamamı kabul edilen parçaların toplamıdır.

Gönderim bitmeden sahip olunan tampon serbest bırakılmaz. İstemci koparsa veya bir parça başarısız olursa tampon serbest bırakılır, kamera önizlemede kalır ve `lastCaptureError` değiştirilmez. `lastTransportError` ayrıdır: boşsa son gövde tamamlanmıştır, `send_timeout`, `client_disconnected`, `send_failed` veya `throughput_alloc_failed` olabilir. Diğer soket alanları `lastTransportErrno`, `lastTransportEspErr`, `lastTransportTimeout`, `lastBodyStartedAtMs`, `lastBodyBytes`, `lastBodyWritten`, `lastBodySendMs`, `lastBodyChunkCount`, `lastBodyMaxChunkMs`, `lastBodyChunkBytes`, `lastTransportPath` değerleridir. `lastTransportPath` `jpeg`, `raw`, `stream` veya `debug` olur ve son gövde gönderimini gösterir.

`send_wait_timeout` 15 saniye olarak kalır. Onu büyütmek yavaş bir yazımı gizler. `recv_wait_timeout` varsayılan 5 saniyedir. Görev önceliği ve yığın (20480) değişmez. `max_open_sockets` 7 kalır. Wi-Fi modem uykusu `setup()` içinde zaten kapalıdır. Görüntü ve tanı soketlerinde `TCP_NODELAY` açılır. Bu bir hız ölçümü değildir.

Tanı uçları kameraya girmez, JPEG üretmez ve yalnız istenen bellekten tampon ayırır:

```text
GET /debug/throughput?bytes=32768&mem=internal&stage=0
GET /debug/throughput/internal?bytes=1024
GET /debug/throughput/psram?bytes=65536
```

İzinli boyutlar 1024, 8192, 32768 ve 65536'dır. `stage=0` sokete ayrılan tamponun kendisini verir. `stage=1` üretimdeki dahili kopyayı dener. PSRAM yoksa psram yolu `503` ve `throughput_psram_unavailable` döner. Ayrılmayan tampon `503` ve `throughput_alloc_failed` olur; sessizce öbür belleğe düşülmez. Başlıklar `X-Debug-Payload-Bytes`, `X-Debug-Chunk-Bytes`, `X-Debug-Memory` ve `X-Debug-Staged` şeklindedir. Bu uçlar ürün davranışı değildir.

Kartta ölçülen HTTP sonucu: 8192 baytlık dahili tampon 0.187 saniyede bitti. 32768 baytlık dahili tampon `stage=1` ile 8.358 saniye sürdü. `lastBodySendMs` 8269, `lastBodyChunkCount` 16, `lastBodyMaxChunkMs` 2865, `lastBodyWritten` 32768 ve `lastTransportError` boştı. JPEG hazırlığı yaklaşık 2.15 saniyedir. Yavaşlık gövde aktarımında kaldı.

## Ham TCP tanı sunucusu

Port 9001, `esp_http_server` dışında düz bir TCP dinleyicisidir. Normal `esp32cam` derlemesi bu soketi açmaz. Yalnızca `SMARTLINE_TCP_DIAG=1` ile açılır. Sunucu kameraya girmez, JPEG üretmez ve tam 32768 baytı dahili RAM'den yollar. Yazılar HTTP gövdesiyle aynı 2048 baytlık parçalardır. HTTP chunk çerçevesi ve ikinci kopya yoktur. `send` kısa dönerse kabul edilen bayttan devam edilir. Aktarım bitince bağlantı kapatılır. Seri kayıt yazılan baytı, yazım sayısını, en uzun yazımı, yazım süresini, bağlantıdan bitişe süreyi ve errno değerini yazar. `SO_SNDTIMEO` 15 saniye kalır; HTTP `send_wait_timeout` ile aynı tavandır.

Bu yol bir hız iddiası değildir. Ham TCP hızlı ve HTTP yavaşsa bakılacak yer `esp_http_server` gönderimidir. İkisi de yavaşsa bakılacak yer TCP, LwIP, Wi-Fi veya donanım yoludur. Sonuç, bu derleme karta yüklenip ölçülünceye kadar yoktur.

```bash
export PLATFORMIO_BUILD_FLAGS="-DSMARTLINE_TCP_DIAG=1"
export WIFI_SSID="YOUR_WIFI"
export WIFI_PASS="YOUR_PASSWORD"
export WIFI_STATIC_IP="192.168.1.50"
export WIFI_GATEWAY="192.168.1.1"
export WIFI_SUBNET="255.255.255.0"
cd SmartLine.Cam
pio run -e esp32cam -t upload
```

Depo kökünden:

```bash
python3 SmartLine.Cam/scripts/tcp_diag_probe.py --host 192.168.1.50 --port 9001
```

## Derleme ve yükleme

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
Stream: http://192.168.1.50:81/stream
Capture: http://192.168.1.50/capture
Raw: http://192.168.1.50/capture/raw
Status: http://192.168.1.50/status
Throughput: http://192.168.1.50/debug/throughput/internal?bytes=32768
```

Bu betik kartı flaşlamaz. Donanım bağlı değilken yalnız derleme çalıştırılır:

```bash
WIFI_SSID=YOUR_WIFI_SSID WIFI_PASS=YOUR_WIFI_PASSWORD \
WIFI_STATIC_IP=192.168.1.50 WIFI_GATEWAY=192.168.1.1 WIFI_SUBNET=255.255.255.0 \
pio run --project-dir SmartLine.Cam --environment esp32cam
```

Bağlı bir kameraya karşı host tarafı duman testi:

```bash
python3 SmartLine.Cam/scripts/camera_smoke.py --base http://192.168.1.50:81 --captures 3
```

Ham kareyi de denetlemek için aynı komuta `--raw` eklenir. Bu, JPEG denetimini kaldırmaz. Elli yakalama bu oturumda çalıştırılmaz. Gövde aktarımı, kullanıcı bu firmware'i yükleyip aşağıdaki tanı komutunu çalıştırana kadar doğrulanmış sayılmaz.

```bash
python3 SmartLine.Cam/scripts/transport_probe.py --base http://192.168.1.50:81
```

## Fiziksel kabul

Bu adımlar yazılım doğrulamasının yerine geçmez ve kart bağlıyken elle çalıştırılır:

1. ESP32-CAM açılır ve seri kayıtta PID `0x2145` görünür.
2. `GET /status` PSRAM algısını, önizleme `240x176` değerini ve yapılandırılan still boyutunu döner.
3. `GET /stream` canlı JPEG içerir ve önizleme `240x176` kalır.
4. `python3 SmartLine.Cam/scripts/transport_probe.py --base http://<cam-ip>:81` durum, 1K/8K/32K/64K dahili ve PSRAM tanı gövdeleri, bir JPEG ve bir ham kare ölçer. Zaman aşımı eksik sonuçtur. Betik firmware yüklemez ve 50 yakalama yapmaz. JPEG `X-Frame-Bytes` kadar ve `FF D8` ile başlar. Ham gövde `640 * 480 * 2` bayttır; `X-Pixel-Format: RGB565` ve `X-Rgb565-Order: BE` taşır. Ham yolda JPEG süresi 0 olur.
5. Hemen ardından `GET /status` okunur. `lastCaptureError` ile `lastTransportError` ayrıdır. `lastBodyWritten`, `lastBodyBytes` değerine eşit değilse aktarım hatası dolu olmalıdır. `X-Timing-Total-Ms` hazırlık süresidir; gövde süresi `lastBodySendMs` içindedir.
6. Elli ardışık yakalama, tek kare süreleri kullanılabilir düzeye inmeden çalıştırılmaz. Ardından `/stream` yine JPEG verir ve `/status` önizlemeyi `240x176` göstermeye devam eder.
7. Dashboard `Capture from camera` gerçek still görüntüsünü, piksel boyutunu ve kaynağı saklar.
8. Vision `POST /inspect` bir inspection karesi ister ve canlı MJPEG üzerinde sürekli çıkarım yapmaz. Ham yol varsa onu tercih eder.

Kabulün uzun hali, tek kare süreleri kullanılabilir olduktan sonra çalıştırılır. Bu komut firmware yüklemez:

```bash
python3 SmartLine.Cam/scripts/camera_smoke.py --base http://192.168.1.50:81 --captures 50 --raw
```

## Sistem İçindeki Yeri

```text
SmartLine.Cam
    ESP32-CAM
      |
      | HTTP MJPEG /stream, JPEG /capture, ham RGB565 /capture/raw
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
- Tarayıcıda `http://<cam-ip>:81/stream` açıldığında canlı HQVGA görüntü gelir.
- `GET /capture` tek JPEG döner ve ardından önizleme HQVGA kalır.
- `SmartLine.Vision`, `CAM_STREAM_URL` ile stream'e bağlanabilir ve aynı hosttaki `/capture` yolunu kullanır.
- Kamera reset sonrası aynı IP ile erişilebilir kalır.
- `python3 SmartLine.Cam/scripts/camera_smoke.py` bağlı kamerada çıkar. Betik firmware yüklemez.

## Sınırlamalar

- MQTT yoktur.
- API ile doğrudan konuşmaz.
- Renk sensörü bu projede değildir; renk bilgisi ana ESP32 firmware sorumluluğundadır.
- Aynı anda birden fazla stream tüketicisi ESP32-CAM'i zorlayabilir. Normal kullanımda tek tüketici `SmartLine.Vision` olmalıdır.
- Kamera bağlantısı koptuğunda stream handler sonlanır; yeniden bağlanma istemci tarafında yapılmalıdır.


## v0.2.0 concurrent capture acceptance

Port 81 serves the long-lived MJPEG preview. An independent HTTP server on port 80 serves `/capture`, `/capture/raw` and `/status`. Vision is the single stream consumer; Dashboard reads preview frames through API/Vision. HTTP throughput diagnostics require an explicit `SMARTLINE_HTTP_DIAG=1` build flag; they are unavailable in normal firmware.

On the real board, run `python SmartLine.Cam/scripts/camera_smoke.py --base http://<cam-ip> --captures 50 --raw --concurrent` with Dashboard open. Run the concurrent phase with the external Vision stream stopped, because the script itself owns the single stream connection; separately verify still/status while Vision owns it. Check color, VGA dimensions, PSRAM, preview recovery and Wi-Fi reconnect. These hardware checks were not executed during integration; a successful build does not replace them.
