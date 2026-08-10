# SmartLine production deployment

Production hedefi tek aktif API instance çalışan fiziksel LAN'dır. Dashboard ve HTTP API yalnız Caddy üzerinden `https://smartline.local` origin'inde sunulur. MQTT cihaz erişimi yerel CA ile iki yönlü doğrulanan TLS `8883` üzerinden yapılır.

## Dağıtım kapısı

Geçmişte sızan Wi-Fi credential kullanıcı tarafından dış sistemde rotate edilip teyit edilmeden production yayın ve Git history rewrite yapılmamalıdır. Bu repository değişikliği tek başına sızmış değeri geçersiz kılmaz.

Mevcut PostgreSQL verisinin korunması hedeflenmez. Eski volume ancak açık yedek/teyit sonrasında kaldırılmalı ve tek yeni `InitialCreate` migration için temiz veritabanı kurulmalıdır. Compose komutları bu dokümanda volume'u otomatik silmez.

## Güvenlik sınırları

- Fiziksel hardwired E-stop ve safety relay yazılım kapsamı dışındadır ve zorunlu makine güvenliği katmanlarıdır.
- Firmware'deki `0°` servo konumu yalnız mevcut prototipin güvenli konum varsayımıdır; gerçek bant motorunun enerjisini kesmez.
- Production tek API instance varsayar. SignalR backplane, distributed lock ve event bus yoktur.
- `.env`, CA/broker/Caddy private key'leri ve firmware credential header'ları git dışında kalır.
- Public registration yoktur. İlk Admin yalnız hiç kullanıcı olmayan veritabanında bootstrap edilir.

## 1. Secret dosyası

```bash
cp .env.example .env
```

Tüm `replace-with-...` değerlerini bağımsız ve uzun rastgele secret'larla değiştirin:

- PostgreSQL parolası
- API/Firmware/Vision için üç ayrı MQTT parolası
- En az 32 karakter JWT signing key
- En az 32 karakter HTTP DeviceKey
- İlk Admin e-posta/parolası

`.env` dosyasını commit etmeyin, shell history/log çıktısında secret göstermeyin ve production secret'larını CI örnek değerleriyle aynı kullanmayın.

## 2. Yerel CA, server ve MQTT client sertifikaları

Broker/Caddy host'un fiziksel LAN IPv4 adresini vererek sertifikaları üretin:

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

Host'a açılan portlar:

|   Port | Servis    | Amaç                                  |
| -----: | --------- | ------------------------------------- |
|   `80` | Caddy     | HTTPS redirect                        |
|  `443` | Caddy     | Dashboard, `/api`, `/hubs`, `/health` |
| `8883` | Mosquitto | Firmware/Vision/API MQTT TLS          |

PostgreSQL ve API production'da host portuna publish edilmez. Caddy `/api`, `/hubs` ve `/health` yollarını API'ye proxy eder; diğer yollar production Dashboard static build'inden sunulur.

Caddy ve API yalnız kendilerine ait `frontend` Docker ağını paylaşır; Caddy bu ağda sabit `172.30.0.10` adresini kullanır ve API yalnız bu adresi `ReverseProxy__KnownProxy` olarak kabul eder. Diğer container'lardan gönderilen sahte forwarded header'lar trusted sayılmaz; IP tabanlı auth/sensor rate-limit partition'ları gerçek LAN istemci adresine göre ayrılır.

API startup sırasında tek `InitialCreate` migration'ı uygular, Identity rollerini hazırlar ve kullanıcı yoksa bootstrap Admin oluşturur. MQTT/JWT/DeviceKey/timezone/threshold ayarları fail-fast doğrulanır.

Mosquitto başlangıç script'i `MQTT_API_PASSWORD`, `MQTT_FIRMWARE_PASSWORD` ve `MQTT_VISION_PASSWORD` değerlerinden SHA-512 PBKDF2 password file üretir. Broker hem yerel CA imzalı client certificate hem geçerli username/password ister; ACL kimliği username'dir ve sertifika kimliği ACL yetkisini değiştirmez. Bu nedenle her principal'ın client key'i ve parolası birlikte rotate edilmelidir. Broker anonymous bağlantıyı ve plaintext `1883`/WebSocket `9001` listener'larını açmaz.

## 5. İlk kullanıcı hazırlığı

İlk login'den sonra bootstrap Admin ile gerekli Viewer ve Operator hesaplarını oluşturun:

- `POST /api/admin/users`
- `PUT /api/admin/users/{id}/role`

Bootstrap parolası ilk kullanıcı oluşturulduktan sonra secret store'dan kaldırılabilir; restart mevcut kullanıcı varken yeniden Admin oluşturmaz. Kullanıcı parolaları en az 12 karakter, büyük/küçük harf, rakam ve sembol içermelidir.

## 6. Acceptance smoke test

En az aşağıdakileri doğrulayın:

1. `https://smartline.local/health` başarılı ve browser CA'ya güveniyor.
2. Viewer okuma yapabiliyor ama control 403; Operator control yapabiliyor; Admin reset/user yönetebiliyor.
3. Eksik/geçersiz `X-Device-Key` sensor POST için 401; geçerli key başarılı.
4. Viewer SignalR bağlanıyor; anonymous negotiate 401.
5. MQTT bağlantısı client certificate olmadan, güvenilmeyen certificate ile veya yanlış parola ile reddediliyor; her principal yalnız ACL'deki topic'lerine erişiyor.
6. Normal stop `false/false`, emergency-stop/fire `false/true` retained MQTT state üretiyor ve her mesaj mevcut `resetGeneration`, `acknowledgedFireEpoch` ve `acknowledgedFireGeneration` değerlerini taşıyor.
7. Fire ikinci kez geldiğinde yeni alert/log oluşmuyor.
8. Fire clear latch'i koruyor; Admin reset `resetGeneration` değerini artırıp son `(deviceEpoch, fireGeneration)` çiftini acknowledge ederek stopped bırakıyor; explicit start gerekiyor. NVS silme/yeni cihaz epoch'u tekrar latch oluşturuyor ve eski retained start/reset reddediliyor.
9. Broker restart/reconnect sonrası API son conveyor/speed/fan desired state'ini tekrar yayınlıyor.
10. Firmware yerel fire algısında API/MQTT beklemeden servo `0°` ve latch durumuna geçiyor.
11. Hardwired E-stop/safety relay yazılımdan bağımsız kabul testini geçiyor.

Log inceleme:

```bash
docker compose logs --no-log-prefix api mosquitto caddy vision
```

Loglarda JWT, refresh token, device key, MQTT veya Wi-Fi credential bulunmamalıdır.

## 7. Development

Source mount, `dotnet watch`, Vite dev server ve host PostgreSQL/API portları yalnız development compose dosyasındadır:

```bash
docker compose --env-file .env -f docker-compose.dev.yml up --build
```

Development MQTT de aynı TLS/ACL sözleşmesini kullanır. Vite `SMARTLINE_DEVICE_KEY` değerini yalnız server-side proxy process'inde okuyup sensor çağrılarına `X-Device-Key` ekler; değer frontend bundle'a yazılmaz. Browser `/api` ve `/hubs` yollarını aynı dev origin'den kullanır, CORS yoktur.

Vision development'ta opsiyonel profildir:

```bash
docker compose --env-file .env -f docker-compose.dev.yml --profile vision up --build
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
- Refresh token, MQTT credential ve DeviceKey rotation prosedürleri ayrı işletilmelidir.
- Backup/restore, PostgreSQL retention ve log rotation deployment sahibinin operasyon sorumluluğudur.
- Ayrıntılı API ve safety sözleşmesi için [smart-line-api.md](smart-line-api.md) ve [smart-line-firmware.md](smart-line-firmware.md) kullanılır.
