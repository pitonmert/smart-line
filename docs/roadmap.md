# SmartLine production hardening durumu

Bu dosya yeni bir mimari geçiş önermiyor. Minimal API + Vertical Slice yapısı korunur; kalan işler mevcut tasarımı doğrulama, production hazırlığı ve secret/history remediation kapsamındadır.

## Mimari kararlar

- MediatR, Carter, generic repository, Unit of Work ve event bus eklenmeyecek.
- Slice handler'ları EF Core `ApplicationDbContext`'ini doğrudan kullanacak.
- The API serves the Dashboard build and HTTP endpoints on the same HTTPS origin without a reverse proxy or CORS middleware.
- MQTT yalnız yerel CA doğrulamalı TLS `8883` ve principal bazlı ACL kullanacak.
- Production tek aktif API instance varsayacak.
- Mevcut servo `0°` prototip güvenli konumudur; fiziksel motor izolasyonu veya hardwired E-stop değildir.

## Teslim durumu

| Alan                       | Durum      | Uygulanan sonuç                                                                      |
| -------------------------- | ---------- | ------------------------------------------------------------------------------------ |
| API mimarisi               | Tamamlandı | Minimal API + Vertical Slice; gereksiz katmanlar kaldırıldı                          |
| Error contract             | Tamamlandı | Failure cevaplarında kararlı `code`, success'te alan yok                             |
| Pagination                 | Tamamlandı | Products/Alerts/Logs filtreleri dahil `PagedResult`, maksimum 100 ve stable ordering |
| Emergency state            | Tamamlandı | Normal stop/emergency/reset/fire ayrımı, latch ve start interlock                    |
| Persistence                | Tamamlandı | Use-case transaction sahipliği, conditional update, atomic statistics upsert         |
| Migration                  | Tamamlandı | Additive migration chain; RetireHttpIdentity removes obsolete identity/session tables |
| MQTT security              | Tamamlandı | TLS `8883`, anonymous/plaintext kapalı, üç principal ve ACL                          |
| Firmware safety            | Tamamlandı | Yerel fire latch, retained conveyor state, speed'in latch'i temizlememesi            |
| Container tanımları        | Tamamlandı | Release API/Vision, API-hosted static Dashboard, separate dev Compose                         |
| Automated CI               | Kuruldu    | .NET integration, Dashboard, Firmware, Vision, MQTT TLS/ACL, Gitleaks                |
| Fiziksel LAN kabulü        | Bekliyor   | Gerçek hostname/IP, client CA trust, kamera, ESP32 ve compose health doğrulaması     |
| Secret/history remediation | Bloke      | Sızmış Wi-Fi credential rotation teyidi bekleniyor                                   |

## Tamamlanan güvenlik davranışı

### HTTP

- The Dashboard hides the sensor simulator in production. Development requests use Vite forwarding without HTTP credentials.

### MQTT

- Mosquitto yalnız `8883` dinler; `1883`, `9001` ve anonymous erişim yoktur.
- `smartline-api`: sensor read, command write.
- `smartline-firmware`: environment/fire write, conveyor/speed/fan read.
- `smartline-vision`: barcode write.
- Broker password file container başlangıcında environment secret'larından üretilir.
- API, firmware ve Vision broker certificate chain/hostname doğrulamasını yerel CA ile yapar.

### Fiziksel güvenlik

Conveyor desired state payload'ı tek topic'tedir:

```json
{
  "isRunning": false,
  "emergency": true,
  "resetGeneration": 3,
  "acknowledgedFireEpoch": "0123456789abcdef0123456789abcdef",
  "acknowledgedFireGeneration": 7
}
```

Normal stop `false/false`, emergency/fire `false/true`, reset `false/false`, start `true/false` yayınlar; her payload ayrıca monoton `resetGeneration` ile `acknowledgedFireEpoch/Generation` çiftini taşır. Firmware rastgele cihaz epoch'unu, latch'i, yerel fire neslini ve son reset neslini NVS'de doğrulanan tek güvenlik kaydında saklar; yalnız API'nin gördüğü aynı epoch içindeki yangın neslini kapsayan yeni manual reset latch'i açabilir. Yeni/bozuk NVS fail-safe kilitlenir ve eski retained komutları reddeder. Öncelik `localFireActive || emergencyLatched || persistenceFault || !commandRunning` koşuludur ve bu durumda servo her döngüde `0°` tutulur. Fire temizlenmesi latch'i açmaz; manual reset de konveyörü başlatmaz.

## Kalıcılık doğruluğu

- Barcode, high-temperature ve fire akışları kendi transaction sınırına sahiptir.
- Domain writer'ları staged tracked state hazırlar; orchestration handler tek `SaveChangesAsync` sahibidir.
- SignalR commit sonrasında best-effort yayınlanır.
- Normal MQTT komutları DB commit'inden sonra gider; broker yoksa `503 MQTT_UNAVAILABLE` döner.
- Emergency MQTT komutu safety-first olarak DB'den önce denenir; ardından state/audit kaydedilir.
- Barcode pre-check kullanıcı mesajı optimizasyonudur; doğruluk kaynağı unique index ve PostgreSQL `23505` eşlemesidir.
- Conveyor hız/start/stop ve alarm resolve conditional update kullanır; conveyor transition + retained publish ortak command gate ile serialize edilir ve emergency normal komutu preempt eder.
- Statistics `(Date, Hour)` unique index'i `NULLS NOT DISTINCT`; sayaçlar atomic upsert'tir.
- Status GET veritabanına yazmaz; singleton satır migration'da seed edilir.

## Test ve CI


Mosquitto ayrı disposable container ile şu davranışlar için doğrulanır:

- TLS/CA bağlantısı
- Anonymous ve plaintext reddi
- Her principal için izin verilen publish/subscribe
- Yasak topic erişiminin mesaj teslim etmemesi

GitHub Actions işleri:

1. `.NET` restore, Release build ve PostgreSQL integration test
2. Dashboard `npm ci`, lint ve production build
3. PlatformIO `firmware` ve `sensor-test`
4. Mosquitto TLS/ACL integration
5. Vision Python compile
6. Gitleaks full-history scan

### Regresyon matrisi


## Production kabul adımları

1. Wi-Fi credential dış sistemde rotate edilir ve teyit edilir.
2. `.env` gerçek rastgele secret'larla hazırlanır; hiçbir değer Git'e eklenmez.
3. LAN IP ile yerel CA/Mosquitto/API HTTPS sertifikaları üretilir.
4. `ca.crt` dashboard istemcilerine trusted root olarak kurulur; `smartline.local` aynı LAN IP'ye çözülür.
5. Firmware ignored credential/TLS header'ları oluşturulur ve iki PlatformIO environment'ı build edilir.
6. Production compose Release image'larla başlatılır; API, broker, database ve Vision health kontrol edilir.
8. Hardwired E-stop/safety relay bağımsız fiziksel safety acceptance'tan geçirilir.

## Credential ve Git history remediation

Bu bölümdeki sıra güvenlik gereğidir; henüz remote rewrite yapılmamalıdır.

1. Geçmişte sızan Wi-Fi credential kullanıcı tarafından rotate edilir.
2. Rotation teyidinden sonra eski repository için remote dışında şifreli `git bundle --all` alınır.
3. Final tree'den orphan `main` oluşturulur ve proje tek temiz commit olarak kaydedilir.
4. Remote `main` koordineli force-update edilir; eski geçmişe işaret eden remote branch/tag bırakılmaz.
5. Collaborator klonları yeniden oluşturulur; cached PR/ref temizliği gerekiyorsa GitHub Support ile koordine edilir.
6. Branch protection yeniden açılır; secret scanning ve push protection etkinleştirilir.

Rotation teyidi gelene kadar Gitleaks full-history kontrolünün eski commit'i bulması beklenebilir. Offline bundle dışında eski geçmiş remote'ta korunmayacaktır.

## Definition of Done

Production hardening ancak aşağıdakilerin tamamı gerçekleştiğinde kapanır:

- API integration suite ve MQTT TLS/ACL testi başarılıdır.
- Dashboard lint/production build, iki Firmware build ve Vision syntax kontrolü başarılıdır.
- Gerçek `.env`/sertifikalarla production compose health check geçer.
- Fiziksel fire → latch → clear → manual reset → explicit start akışı doğrulanır.
- Wi-Fi credential rotation ve kontrollü Git history remediation tamamlanır.
- Branch protection ile secret scanning/push protection yeniden etkinleştirilir.

## HTTP access simplification

Dashboard, API and SignalR now run without user accounts, roles or HTTP keys. Public-access regression tests replace identity-specific tests. MQTT TLS/ACL and physical safety behavior are unchanged. The RetireHttpIdentity migration removes obsolete identity tables.
