# SmartLine production deployment

Production hedefi tek aktif API instance çalışan fiziksel LAN'dır. Dashboard and HTTP API are served directly by ASP.NET Core at `https://smartline.local`; there is no reverse proxy. MQTT cihaz erişimi yerel CA ile iki yönlü doğrulanan TLS `8883` üzerinden yapılır.

## Dağıtım kapısı

Geçmişte sızan Wi-Fi credential kullanıcı tarafından dış sistemde rotate edilip teyit edilmeden production yayın ve Git history rewrite yapılmamalıdır. Bu repository değişikliği tek başına sızmış değeri geçersiz kılmaz.

Mevcut PostgreSQL verisinin korunması hedeflenmez. Eski volume ancak açık yedek/teyit sonrasında kaldırılmalı ve tek yeni `InitialCreate` migration için temiz veritabanı kurulmalıdır. Compose komutları bu dokümanda volume'u otomatik silmez.

## Güvenlik sınırları

- Fiziksel hardwired E-stop ve safety relay yazılım kapsamı dışındadır ve zorunlu makine güvenliği katmanlarıdır.
- Firmware'deki `0°` servo konumu yalnız mevcut prototipin güvenli konum varsayımıdır; gerçek bant motorunun enerjisini kesmez.
- Production tek API instance varsayar. SignalR backplane, distributed lock ve event bus yoktur.
- `.env`, CA/broker/API HTTPS private key'leri ve firmware credential header'ları git dışında kalır.
- Dashboard and HTTP API are open to all clients with network access; there is no account, role or HTTP key enforcement.

## 1. Secret dosyası

```bash
cp .env.example .env
```

Tüm `replace-with-...` değerlerini bağımsız ve uzun rastgele secret'larla değiştirin:

- PostgreSQL parolası
- API/Firmware/Vision için üç ayrı MQTT parolası

`.env` dosyasını commit etmeyin, shell history/log çıktısında secret göstermeyin ve production secret'larını CI örnek değerleriyle aynı kullanmayın.

## 2. Yerel CA, server ve MQTT client sertifikaları

Generate certificates using the factory server's LAN IPv4 address:

```bash
SMARTLINE_LAN_IP=192.168.1.20 ./infra/certs/generate-local-ca.sh
```

Compose varsayılan olarak bu dizini kullanır. Farklı, erişimi sınırlandırılmış bir dizin seçilirse sertifikaları `SMARTLINE_CERT_OUTPUT_DIR` ile üretip `.env` içindeki `SMARTLINE_CERT_DIR` değerini aynı dizine ayarlayın.

Script şunları `infra/certs/generated/` altında üretir:

- `ca.crt` ve private `ca.key`
- `mosquitto.crt` ve private `mosquitto.key`
- `smartline.local.crt` ve private `smartline.local.key`
- `smartline-api.crt` ve private `smartline-api.key`
- `smartline-firmware.crt` ve private `smartline-firmware.key`
- `smartline-vision.crt` ve private `smartline-vision.key`

`ca.crt` Dashboard istemcilerine güvenilir root CA olarak kurulmalıdır. LAN DNS veya hosts kaydı `smartline.local` adını aynı IP'ye çözmelidir. Broker sertifikası `mosquitto`, localhost ve verilen LAN IP SAN'larını içerir.

Private key'leri repository'ye eklemeyin ve erişimi sınırlandırılmış çevrimdışı yedekte tutun. `ca.key` hiçbir container'a mount edilmez. Broker yalnız CA public sertifikası ve kendi server key'ini; API ve Vision yalnız CA public sertifikası ile kendi client cert/key çiftini alır. Host'ta `0600` kalan client key'leri container başlangıcında root tarafından tmpfs'e `0400` kopyalanır, ardından uygulama API'de `app`, Vision'da `smartline` kullanıcısına düşürülür. Yeniden üretim güven zincirini değiştirir; script mevcut key varken işlemi reddeder, bilinçli rotation için `--force` gerekir.

## 3. Firmware TLS dosyaları

Donanım/ağ ayarı:

```bash
cp SmartLine.Firmware/include/Config.h.example SmartLine.Firmware/include/Config.h
```

`Config.h` içinde 2.4 GHz Wi-Fi ve broker LAN IP'sini ayarlayın. MQTT portu `8883` kalmalıdır.

CA, firmware client cert/key çifti ve firmware MQTT parolasıyla ignored TLS header'ı üretin:

```bash
export MQTT_FIRMWARE_PASSWORD='<same-value-as-env>'
./SmartLine.Firmware/scripts/generate-mqtt-config.sh
```

```bash
cd SmartLine.Firmware
pio run -e firmware
pio run -e sensor-test
```

Build sonrası gerçek cihazda broker certificate doğrulaması, firmware client certificate sunumu, retained state alma ve fire latch davranışı gözlenmelidir.

## 4. Production başlatma

Önce render edilen Compose konfigürasyonunu secret'ları ekrana dökmeden kontrollü ortamda doğrulayın:

```bash
docker compose --env-file .env config --quiet
docker compose --env-file .env up -d --build
docker compose ps
```

If an older Compose deployment still has a Caddy container, stop/remove that obsolete container before starting the API on host port 443. `docker compose --env-file .env up -d --build --remove-orphans` removes obsolete services from this Compose project; it does not remove named data volumes. Review the project name before running it. No running deployment was changed by this source refactor.

Host'a açılan portlar:

|   Port | Servis    | Amaç                                  |
| -----: | --------- | ------------------------------------- |
|  `443` | API HTTPS | Dashboard, `/api`, `/hubs`, `/health` |
| `8883` | Mosquitto | Firmware/Vision/API MQTT TLS          |

PostgreSQL and API HTTP port 5047 are not published in production. Host port 443 maps directly to API HTTPS port 5443. The API image includes the Dashboard build in `wwwroot`. Kestrel uses `smartline.local.crt` and `smartline.local.key`; the entrypoint copies these to its private runtime directory before dropping privileges. There is no port 80 listener or HTTP redirect: clients must use `https://smartline.local`.

Services share the backend Docker network without fixed proxy addresses. The API does not process `X-Forwarded-For` or `X-Forwarded-Proto`; HTTP request rate limiting is not configured. Internal Vision calls continue to use `http://api:5047`.

API startup applies the migration chain and recovers contract/dataset state. MQTT, timezone and temperature options are validated; there are no HTTP identity settings.

Mosquitto başlangıç script'i `MQTT_API_PASSWORD`, `MQTT_FIRMWARE_PASSWORD` ve `MQTT_VISION_PASSWORD` değerlerinden SHA-512 PBKDF2 password file üretir. Broker hem yerel CA imzalı client certificate hem geçerli username/password ister; ACL kimliği username'dir ve sertifika kimliği ACL yetkisini değiştirmez. Bu nedenle her principal'ın client key'i ve parolası birlikte rotate edilmelidir. Broker anonymous bağlantıyı ve plaintext `1883`/WebSocket `9001` listener'larını açmaz.

## 5. HTTP access

Open `https://smartline.local` directly. No account provisioning or HTTP credentials are required. Migration `RetireHttpIdentity` drops the obsolete Identity and refresh-token tables when applied. It does not drop products, inspections, recipes, calibrations, datasets or conveyor state. Migration changes have only been exercised against disposable test databases during this refactor.

## 6. Acceptance smoke test

En az aşağıdakileri doğrulayın:

1. `https://smartline.local/health` başarılı ve browser CA'ya güveniyor.
2. Dashboard opens directly; API reads, conveyor controls and manual reset work without credentials.
3. HTTP sensor POST requests work without a device key; invalid measurements are still rejected.
4. SignalR negotiate and WebSocket handshake succeed without tokens.
5. MQTT bağlantısı client certificate olmadan, güvenilmeyen certificate ile veya yanlış parola ile reddediliyor; her principal yalnız ACL'deki topic'lerine erişiyor.
6. Normal stop `false/false`, emergency-stop/fire `false/true` retained MQTT state üretiyor ve her mesaj mevcut `resetGeneration`, `acknowledgedFireEpoch` ve `acknowledgedFireGeneration` değerlerini taşıyor.
7. Fire ikinci kez geldiğinde yeni alert/log oluşmuyor.
8. Fire clear latch'i koruyor; manual reset `resetGeneration` değerini artırıp son `(deviceEpoch, fireGeneration)` çiftini acknowledge ederek stopped bırakıyor; explicit start gerekiyor. NVS silme/yeni cihaz epoch'u tekrar latch oluşturuyor ve eski retained start/reset reddediliyor.
9. Broker restart/reconnect sonrası API son conveyor/speed/fan desired state'ini tekrar yayınlıyor.
10. Firmware yerel fire algısında API/MQTT beklemeden servo `0°` ve latch durumuna geçiyor.
11. Hardwired E-stop/safety relay yazılımdan bağımsız kabul testini geçiyor.

Log inceleme:

```bash
docker compose logs --no-log-prefix api mosquitto vision
```

Loglarda MQTT or Wi-Fi credentials bulunmamalıdır.

## 7. Development

Source mount, `dotnet watch`, Vite dev server ve host PostgreSQL/API portları yalnız development compose dosyasındadır:

```bash
docker compose --env-file .env -f docker-compose.dev.yml up --build
```

Development MQTT de aynı TLS/ACL sözleşmesini kullanır. The Vite server forwards requests without HTTP credentials. Browser `/api` ve `/hubs` yollarını aynı dev origin'den kullanır, CORS yoktur.

Vision development'ta opsiyonel profildir. Profil tek başına süreci başlatır; API health adresi boş kaldığı için tanı `NOT_CONFIGURED` kalır. PostgreSQL, MQTT, API, Dashboard ve Vision'ı adres yazmadan birlikte açmak için development overlay kullanılır. Overlay host'a `8090` yayınlar. Production bu portu yayınlamaz ve API'ye yalnız iç adres `http://vision:8090/health` verir.

```bash
docker compose --env-file .env -f docker-compose.dev.yml --profile vision up --build
docker compose --env-file .env -f docker-compose.dev.yml -f docker-compose.vision.yml --profile vision up --build
```

## 8. CI ve release doğrulaması

GitHub Actions şu işleri çalıştırır:

- .NET restore/Release build ve PostgreSQL integration suite
- Dashboard `npm ci`, lint ve production build
- PlatformIO firmware + sensor-test
- Disposable certificate ile Mosquitto TLS/ACL testi
- Vision Python compile
- Gitleaks full-history scan

Gerçek production `.env`, sertifika trust ve fiziksel cihaz davranışı CI'ın yerine geçmez.

## 9. Credential ve history remediation

Rotation teyidinden sonra:

1. Eski repository için remote dışında şifreli `git bundle --all` alınır.
2. Final tree'den orphan `main` ve tek temiz commit oluşturulur.
3. Remote `main` koordineli force-update edilir; eski geçmişe işaret eden remote branch/tag bırakılmaz.
4. Collaborator klonları yeniden oluşturulur; gerekirse cached PR/ref temizliği GitHub Support ile koordine edilir.
5. Branch protection yeniden açılır; secret scanning ve push protection etkinleştirilir.

Rotation teyidi ve arşiv parolası olmadan bu adımlar otomatik uygulanmaz. Gitleaks full-history taramasının history rewrite öncesinde eski commit'i bulması beklenebilir.

## Operasyon notları

- Sertifikalar süre dolmadan kontrollü rotate edilmelidir; firmware ve client trust store'ları birlikte güncellenir.
- MQTT credentials and TLS certificates have their own rotation procedures.
- Backup/restore, PostgreSQL retention ve log rotation deployment sahibinin operasyon sorumluluğudur.
- Ayrıntılı API ve safety sözleşmesi için [smart-line-api.md](smart-line-api.md) ve [smart-line-firmware.md](smart-line-firmware.md) kullanılır.


## v0.2.0 dataset and model operations

Back up PostgreSQL and dataset files together. Pause the API writer while copying, and restart it even if a backup step fails. Do not use a host `datasets/` directory as a substitute for the Compose named volume.

```sh
mkdir -p backup
# Run during a maintenance window; these commands are documentation only.
docker compose stop api
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' > backup/database.sql
docker compose cp api:/var/lib/smartline/datasets backup/datasets
docker compose start api
```

Use a copied session's `manifest.jsonl` and `images/` as the training tool inputs. Retain the matching database backup for restoration. Dataset startup reconciliation reports missing accepted images rather than silently treating them as valid.

Place reviewed artifacts and their export sidecars in `models/runtime`, mounted read-only at `/opt/smartline/models`. Configure `MODEL_PATH` with the container path and `MODEL_VERSION` matching the sidecar and recipe. Restart Vision after replacement. Configure `CAM_CAPTURE_URL` when capture/status are not on camera port 80. Keep `STILL_CAPTURE_TIMEOUT` below `VISION_CAPTURE_TIMEOUT_SECONDS`; the defaults are 30 and 35 seconds. No real model acceptance or physical camera verification was performed during integration.
