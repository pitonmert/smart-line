# SmartLine.Firmware teknik referansı

`SmartLine.Firmware`, ESP32 üzerindeki sıcaklık, nem ve alev sensörlerini okur. Ortam ve yangın durumunu MQTT üzerinden API'ye gönderir; konveyör, hız ve fan komutlarını broker üzerinden alır. Ürün rengi kamera görüntüsünden Vision tarafından belirlenir. TCS34725 yalnız ayrı `sensor-test` ortamında yerel izleme için kullanılır.

## Güvenlik sınırı

Bu prototipte `0°` servo konumu güvenli konum kabul edilir. Firmware'in güvenlik önceliği şöyledir:

```text
localFireActive || emergencyLatched || !commandRunning
    -> servo her döngüde 0°
```

Bu servo gerçek bant motorunu enerjisiz bırakan bir PWM/relay çıkışı değildir. Yazılım latch'i, fiziksel hardwired acil durdurma butonu, safety relay ve uygun makine güvenliği devresinin yerine geçmez.

Firmware güvenli-durdurulmuş durumda açılır. Broker bağlantısı kesilirse komut durumu yeniden `stopped` yapılır. Fiziksel ayırma komutu henüz uygulanmadığından servo, start komutu alınsa da nötr `0°` konumunda kalır.

## Proje yapısı

```text
SmartLine.Firmware/
├── include/
│   ├── Config.h.example
│   └── MqttTlsConfig.h.example
├── scripts/
│   └── generate-mqtt-config.sh
├── src/
│   ├── firmware/main.cpp
│   └── sensor-test/main.cpp
└── platformio.ini
```

`Config.h` ve `MqttTlsConfig.h` gerçek credential içerdiği için git tarafından yok sayılır.

## İlk kurulum

Donanım ve ağ ayarını oluşturun:

```bash
cp SmartLine.Firmware/include/Config.h.example SmartLine.Firmware/include/Config.h
```

`Config.h` içinde 2.4 GHz Wi-Fi bilgilerini ve `SMARTLINE_LAN_IP` ile aynı broker IP'sini ayarlayın. MQTT portu yalnız `8883` olmalıdır.

Yerel CA üretildikten sonra firmware TLS dosyasını oluşturun:

```bash
export MQTT_FIRMWARE_PASSWORD='<firmware-principal-password>'
./SmartLine.Firmware/scripts/generate-mqtt-config.sh
```

Script, `infra/certs/generated/ca.crt`, `smartline-firmware.crt`, `smartline-firmware.key` ve firmware MQTT parolasını git dışında, `0600` modunda tutulan `include/MqttTlsConfig.h` dosyasına yazar. ESP32 broker sertifikasını CA ve hostname/IP SAN eşleşmesiyle doğrular, kendi client certificate/private key çiftini sunar ve ayrıca firmware username/password ile authenticate olur. TLS sertifika zamanı için NTP senkronizasyonu tamamlanamazsa cihaz güvenli durumda yeniden başlar.

## Pinler

| Sabit           | GPIO | Kullanım                  |
| --------------- | ---: | ------------------------- |
| `COLOR_LED_PIN` |   26 | Yalnız `sensor-test`: renk sensörü aydınlatması |
| `FLAME_PIN`     |   34 | Analog alev sensörü       |
| `FAN_PIN`       |   27 | Fan çıkışı                |
| `BUZZER_PIN`    |   25 | Yangın uyarısı            |
| `SERVO_PIN`     |   13 | Prototip ayırıcı servo    |

AHT10 `Wire` üzerinde GPIO 21/22 kullanır. Yalnız `sensor-test` ortamı, TCS34725 için GPIO 32/33 üzerinde `Wire1` başlatır. Normal firmware bu hattı ve renk sensörünü kullanmaz; sensör fiziksel olarak bağlı kalabilir.

## MQTT kimliği ve ACL

Firmware şu kimlikle bağlanır:

- Username: `smartline-firmware`
- TLS: yerel CA doğrulaması ve firmware client certificate sunumu zorunlu
- Publish: yalnız `smartline/sensors/environment` ve `smartline/sensors/fire`
- Subscribe: yalnız conveyor, speed ve fan command topic'leri

### Firmware -> API

| Topic                           | Payload                                                                                   | Davranış                                      |
| ------------------------------- | ----------------------------------------------------------------------------------------- | --------------------------------------------- |
| `smartline/sensors/environment` | `{"temperature":26.3,"humidity":48.7}`                                                    | Yaklaşık her 2.5 saniye                       |
| `smartline/sensors/fire`        | `{"isDetected":true,"deviceEpoch":"0123456789abcdef0123456789abcdef","fireGeneration":4}` | İlk bağlantıda ve durum değiştiğinde retained |

Yerel alev algısı MQTT veya API beklemeden `emergencyLatched=true` yapar, çalışma komutunu kapatır ve servoyu `0°` konumuna getirir. Rastgele 128-bit `deviceEpoch`, latch ve sayaçlar ESP32 NVS'de doğrulanan tek bir güvenlik kaydında tutulur; reboot latch'i temizlemez. Yeni/boş NVS güvenli durumda kilitli başlar. Eksik, bozuk veya yazılamayan kayıt persistence fault oluşturur ve start reddedilir. Fire mesajı ilk bağlantıda ve durum değişiminde retained yayınlanır; publish başarısızsa firmware başarılı olana kadar bağlı döngülerde yeniden dener. API tarafı `(deviceEpoch, fireGeneration)` çiftini idempotent işler.

### API -> Firmware

Conveyor state tek topic'ten gelir:

```text
Topic: smartline/commands/stop
QoS: 1
Retained: true
```

`resetGeneration`, yalnız API üzerinden manuel reset isteğinde artan monoton sayıdır. `acknowledgedFireEpoch` ve `acknowledgedFireGeneration`, API'nin işlediği son yerel güvenlik olayının kimliğidir. Firmware NVS'deki son uygulanmış değerden daha yeni bir reset, kendi epoch'u ile birebir eşleşen acknowledgement ve kendi generation değerini kapsayan sayaç birlikte gelmedikçe latch'i açmaz. Start için de aynı epoch/generation eşleşmesi zorunludur.

| İşlem                 | Payload                                                                                                                | Firmware etkisi                                                               |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Normal stop           | `{"isRunning":false,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`   | Durur; latch ve reset nesli değişmez                                          |
| Emergency stop / fire | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    | Emergency latch'i aktive eder ve güvenlik kaydına yazar                       |
| Manual reset          | `{"isRunning":false,"emergency":false,"resetGeneration":N+1,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` | Reset yeni, `E/F` cihazla eşleşiyor ve fire temizse latch'i açar; çalıştırmaz |
| Start                 | `{"isRunning":true,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    | Epoch/generation eşleşiyor, fire/latch/persistence fault temizse çalışır      |

Normal stop ile reset artık aynı komut değildir: yalnız yeni `resetGeneration` ve eşleşen `(acknowledgedFireEpoch, acknowledgedFireGeneration)` birlikte yetkili reset kanıtıdır. Firmware yerel fire aktifken veya API acknowledgement değeri gerideyken yeni reset neslini tüketir fakat latch'i açmaz; fire temizlenip API olayı gördükten sonra yeniden manuel reset gerekir. NVS silinmesi/cihaz değişimi yeni epoch ürettiği için eski retained reset veya start yeni yerel latch'i temizleyemez.

Speed payload'ı `{"speed":75}` biçimindedir, `0..100` aralığına clamp edilir ve hiçbir running/emergency durumunu temizlemez. Mevcut prototipte bu değer fiziksel motor PWM çıkışına bağlı değildir. Fan payload'ı `{"active":true}` biçimindedir.

## Eyleyici önceliği

Her ana döngüde alev sensörü önce okunur. Öncelik sırası:

1. Yerel fire aktifse latch edilir, buzzer açılır ve servo `0°` olur.
2. Emergency latch aktifse fire temizlense dahi servo `0°` kalır.
3. Conveyor running değilse servo `0°` kalır.
4. Tüm interlock'lar temiz ve running olsa da servo nötr `0°` konumunda kalır. Eski renge göre `90°` hareketi kaldırılmıştır; yeni fiziksel ayırma komutu bu değişikliğin kapsamında değildir.

Manual reset clears only the latch; starting the conveyor requires a separate start command.

## Build ve upload

```bash
cd SmartLine.Firmware
pio run -e firmware
pio run -e sensor-test
pio run -e firmware -t upload
pio device monitor --baud 115200
```

### Yerel renk sensörü izlemesi

Renk ölçümlerini görmek için normal firmware yerine `sensor-test` yüklenmelidir:

```bash
pio run -e sensor-test -t upload
pio device monitor --baud 115200
```

Bu ortam Wi-Fi veya MQTT kullanmaz; RGB ve clear değerleri yalnız seri monitörde gösterilir. Donanım testi için `R > G + 15`, `R > B + 15` ve `clear > 20` olduğunda servo `90°`, diğer durumlarda `0°` konumuna gider. Alev algılanırsa veya renk ölçümü kullanılamıyorsa servo `0°` kalır. Fan sıcaklığa, buzzer alev sensörüne göre çalışır; renk ölçümleri bunları etkilemez. Bu yerel test normal firmware'in üretim akışında bulunmaz.

TCS34725 başlatılamazsa ölçüm atlanır. I²C işlem zaman aşımı 50 ms'dir; çalışma sırasında erişim hatası veya eksik veri alınırsa renk izlemesi yeniden başlatılana kadar kapatılır. Çıktı `unavailable` olur; geçersiz ölçüm sıfır gibi gösterilmez. Aydınlatma yalnız ölçüm sırasında açılır ve hata yollarında kapatılır. Renk sensörü arızası diğer sensörlerin yazılım akışını durdurmaz.

Donanımsız regresyon kontrolleri:

```bash
python3 -m unittest discover -s SmartLine.Firmware/tests -p 'test_*.py' -v
```

Bu komut depo kökünde çalıştırılır ve bir C++17 derleyicisi gerektirir. Gerçek tanılama kodunu sahte I²C, sensör ve GPIO sürücüleriyle çalıştırır. Build ve simülasyon; lehimli kartın elektriksel durumu, gerçek sensör kopması veya gerçek eyleyici davranışının doğrulaması değildir.

## Operasyon sınırları

- Son desired state'in sahibi tek aktif API instance'tır; firmware broker reconnect sonrasında retained state'i bekler.
- Emergency latch, yerel fire nesli ve son reset nesli NVS'de kalıcıdır; cihaz reboot'u yetkili reset sayılmaz. NVS açma/yazma hatası firmware'i fail-safe persistence fault durumuna geçirir ve start'ı engeller.
- MQTT/Wi-Fi credential ve generated CA header'ları commit edilmez. Geçmişte sızmış credential dış sistemde rotate edilmeden eski Git geçmişi güvenli kabul edilmez.
- Sertifika/secret hazırlığı ve fiziksel acceptance sırası için [deployment.md](deployment.md) kullanılır.
