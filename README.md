# SmartLine

ESP32 tabanlı IIoT akıllı bant ve kalite kontrol sistemi.

SmartLine; sensör/vision verisini MQTT üzerinden alan, PostgreSQL'de işleyen, conveyor güvenlik state'ini yöneten ve React dashboard'a SignalR ile canlı yayınlayan bir prototiptir.

## Mimari

```text
ESP32 Firmware ---- environment/fire ----┐
                                         │ MQTT TLS/8883
ESP32-CAM -> Vision ---- barcode --------┤
                                         v
                                  Mosquitto ACL
                                         |
                                         v
                                  SmartLine.API -- EF Core --> PostgreSQL
                                         |
                                         | SignalR + REST
                                         v
                                      Caddy
                                         |
                                         v
                                  React Dashboard
```

API, Minimal API + Vertical Slice kullanır. MediatR, Carter, generic repository, Unit of Work, AutoMapper, Swagger/OpenAPI veya event bus yoktur.

## Projeler

| Proje                 | Sorumluluk                                                                 |
| --------------------- | -------------------------------------------------------------------------- |
| `SmartLine.API`       | Identity/JWT, DeviceKey, domain akışları, PostgreSQL, MQTT ve SignalR      |
| `SmartLine.API.Tests` | WebApplicationFactory + gerçek PostgreSQL Testcontainers integration suite |
| `SmartLine.Dashboard` | Login, rol bazlı operasyon, REST snapshot ve SignalR realtime SPA          |
| `SmartLine.Firmware`  | ESP32 sensörleri, MQTT TLS ve local emergency latch                        |
| `SmartLine.Cam`       | ESP32-CAM MJPEG stream                                                     |
| `SmartLine.Vision`    | Barkod/QR okuyup MQTT TLS ile yayınlayan Python servisi                    |

## Güvenlik özeti

- Kullanıcı rolleri: `Viewer`, `Operator`, `Admin`; cihaz trafiği ayrı `Device` kimliğidir.
- Access token 15 dakika; refresh token 7 gün, hash'li, rotate edilen hardened cookie'dir.
- Public registration yoktur. İlk Admin yalnız kullanıcı tablosu boşken environment variable ile bootstrap edilir.
- Dashboard token'ı yalnız bellekte tutar; SignalR `accessTokenFactory` kullanır.
- Mosquitto anonymous/plaintext veya istemci sertifikasız bağlantı kabul etmez; yalnız yerel CA imzalı karşılıklı TLS `8883`, ayrı username/password ve principal bazlı ACL vardır.
- Production HTTP trafiği aynı origin `https://smartline.local` üzerinden Caddy ile taşınır; API ve PostgreSQL host'a doğrudan publish edilmez.

## Acil durdurma sınırı

Normal stop, emergency-stop, fire latch ve Admin reset birbirinden ayrıdır. Firmware provisioning sırasında rastgele bir `deviceEpoch` üretir; her yerel yangın olayı bu epoch içindeki kalıcı `fireGeneration`, her Admin reset ise monoton bir `resetGeneration` üretir. Firmware reset/start komutunu yalnız API aynı `(deviceEpoch, fireGeneration)` çiftini acknowledge etmişse kabul eder. Epoch, latch ve sayaçlar NVS'de tek doğrulanan güvenlik kaydı olarak reboot boyunca korunur; eksik/bozuk kayıt fail-safe kilitlenir. Yeni cihaz veya NVS silinmesi yeni epoch ile yeniden Admin onayı gerektirir. Fire temizlense bile yeni bir Admin reset olmadan latch açılmaz; reset konveyörü başlatmaz, ayrıca start gerekir.

Firmware'de `0°` servo konumu mevcut prototipin güvenli konumu kabul edilir. Bu çıkış gerçek bant motorunun enerjisini kesmez. Fiziksel hardwired E-stop ve safety relay yazılımın yerine geçemez ve production safety kabulünün ayrı parçasıdır.

## Gereksinimler

| Araç             | Hedef                                                 |
| ---------------- | ----------------------------------------------------- |
| Docker + Compose | PostgreSQL, Mosquitto, API, Dashboard/Caddy ve Vision |
| .NET SDK 10      | API ve integration testleri                           |
| Node.js 22 + npm | Dashboard                                             |
| PlatformIO 6.x   | Firmware ve sensor-test                               |
| Python 3.11+     | Vision lokal geliştirme                               |
| OpenSSL          | Yerel CA, server ve MQTT client sertifikaları         |

## Development başlangıcı

Secret şablonunu oluşturup tüm placeholder'ları bağımsız rastgele değerlerle değiştirin:

```bash
cp .env.example .env
```

Broker ve `smartline.local` host'unun LAN IP'siyle disposable olmayan yerel CA, server ve ayrı MQTT client sertifikalarını üretin:

```bash
SMARTLINE_LAN_IP=192.168.1.20 ./infra/certs/generate-local-ca.sh
```

Development stack:

```bash
docker compose --env-file .env -f docker-compose.dev.yml up --build
```

| Servis     | Development adresi             |
| ---------- | ------------------------------ |
| Dashboard  | `http://localhost:5173`        |
| API        | `http://localhost:5047`        |
| Health     | `http://localhost:5047/health` |
| PostgreSQL | `localhost:5432`               |
| MQTT TLS   | `localhost:8883`               |

Development Dashboard, `/api` ve `/hubs` isteklerini Vite proxy ile API'ye gönderir. Sensor simulator çağrılarındaki device key proxy process'inde kalır; browser bundle'a yazılmaz. CORS kullanılmaz.

## Production deployment

Production static Dashboard ve API yalnız Caddy üzerinden sunulur:

```bash
docker compose --env-file .env config --quiet
docker compose --env-file .env up -d --build
docker compose ps
```

Client cihazlara `infra/certs/generated/ca.crt` trusted root olarak kurulmalı ve `smartline.local` production host IP'sine çözülmelidir. Ayrıntılı secret, certificate, firmware ve acceptance sırası için [production deployment rehberini](docs/deployment.md) kullanın.

## İlk kullanıcı ve roller

`BOOTSTRAP_ADMIN_EMAIL` ve `BOOTSTRAP_ADMIN_PASSWORD`, yalnız hiç kullanıcı bulunmayan ilk startup'ta Admin oluşturur. Sonraki kullanıcılar Admin tarafından:

- `POST /api/admin/users`
- `PUT /api/admin/users/{id}/role`

endpoint'leriyle yönetilir. Viewer salt-okur, Operator operasyon yapar, Admin emergency reset ve kullanıcı yönetimine sahiptir.

## Firmware

```bash
cp SmartLine.Firmware/include/Config.h.example SmartLine.Firmware/include/Config.h
export MQTT_FIRMWARE_PASSWORD='<same-value-as-env>'
./SmartLine.Firmware/scripts/generate-mqtt-config.sh

cd SmartLine.Firmware
pio run -e firmware
pio run -e sensor-test
```

Gerçek credential içeren `Config.h` ve `MqttTlsConfig.h` git tarafından yok sayılır.

## Build ve test

```bash
dotnet restore SmartLine.slnx
dotnet build SmartLine.slnx --configuration Release --no-restore
dotnet test SmartLine.API.Tests/SmartLine.API.Tests.csproj --configuration Release --no-build

cd SmartLine.Dashboard
npm ci
npm run lint
npm run build
```

GitHub Actions ayrıca iki PlatformIO environment'ı, Vision Python syntax, Mosquitto TLS/ACL ve Gitleaks full-history taramasını çalıştırır.

API için yalnız son modeli içeren tek `InitialCreate` migration vardır. Eski PostgreSQL verisinin korunması hedeflenmez; temiz veritabanı gerekir.

## Dokümantasyon

| Dosya                                                        | İçerik                                                                             |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------- |
| [docs/smart-line-api.md](docs/smart-line-api.md)             | API mimarisi, auth/policy, endpoint, error, pagination ve transaction sözleşmeleri |
| [docs/smart-line-dashboard.md](docs/smart-line-dashboard.md) | Dashboard auth, rol bazlı UI, API client ve SignalR                                |
| [docs/smart-line-firmware.md](docs/smart-line-firmware.md)   | Firmware MQTT TLS ve emergency latch                                               |
| [docs/smart-line-vision.md](docs/smart-line-vision.md)       | Vision kamera/MQTT akışı                                                           |
| [docs/deployment.md](docs/deployment.md)                     | Production certificate, secret, compose ve acceptance adımları                     |
| [docs/roadmap.md](docs/roadmap.md)                           | Hardening teslim durumu ve kalan işler                                             |
| [docs/smart-line-cam.md](docs/smart-line-cam.md)             | ESP32-CAM stream                                                                   |
| [docs/issues.md](docs/issues.md)                             | Bilinen sorun/remediation kayıtları                                                |

## Açık operasyonel blokaj

Geçmişte sızan Wi-Fi credential kullanıcı tarafından rotate edilip teyit edilmeden Git remote geçmişi yeniden yazılmayacak veya yayınlanmayacaktır. Rotation sonrasında şifreli offline bundle, orphan tek commit `main`, koordineli force-update ve branch protection/secret scanning yeniden etkinleştirme sırası izlenir. Production tek aktif API instance varsayar.
