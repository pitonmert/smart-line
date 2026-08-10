# SmartLine production hardening durumu

Bu dosya yeni bir mimari geçiş önermiyor. Minimal API + Vertical Slice yapısı korunur; kalan işler mevcut tasarımı doğrulama, production hazırlığı ve secret/history remediation kapsamındadır.

## Mimari kararlar

- MediatR, Carter, generic repository, Unit of Work ve event bus eklenmeyecek.
- Slice handler'ları EF Core `ApplicationDbContext`'ini doğrudan kullanacak.
- HTTP Dashboard/API aynı origin'de Caddy arkasında çalışacak; CORS kullanılmayacak.
- MQTT yalnız yerel CA doğrulamalı TLS `8883` ve principal bazlı ACL kullanacak.
- Production tek aktif API instance varsayacak.
- Mevcut servo `0°` prototip güvenli konumudur; fiziksel motor izolasyonu veya hardwired E-stop değildir.

## Teslim durumu

| Alan                       | Durum      | Uygulanan sonuç                                                                      |
| -------------------------- | ---------- | ------------------------------------------------------------------------------------ |
| API mimarisi               | Tamamlandı | Minimal API + Vertical Slice; gereksiz katmanlar kaldırıldı                          |
| Identity/JWT               | Tamamlandı | Viewer/Operator/Admin, 15 dk access, 7 gün rotate refresh, hash'li DB kaydı          |
| Device auth                | Tamamlandı | `X-Device-Key`, ayrı scheme/Device rolü, sabit zamanlı karşılaştırma                 |
| Authorization              | Tamamlandı | Anonymous/Viewer/Operator/Admin/Device policy matrisi ve protected SignalR hub       |
| Error contract             | Tamamlandı | Failure cevaplarında kararlı `code`, success'te alan yok                             |
| Pagination                 | Tamamlandı | Products/Alerts/Logs filtreleri dahil `PagedResult`, maksimum 100 ve stable ordering |
| Emergency state            | Tamamlandı | Normal stop/emergency/reset/fire ayrımı, latch ve start interlock                    |
| Persistence                | Tamamlandı | Use-case transaction sahipliği, conditional update, atomic statistics upsert         |
| Migration                  | Tamamlandı | Son model için tek `InitialCreate`; Identity/refresh/emergency/index/seed dahil      |
| MQTT security              | Tamamlandı | TLS `8883`, anonymous/plaintext kapalı, üç principal ve ACL                          |
| Firmware safety            | Tamamlandı | Yerel fire latch, retained conveyor state, speed'in latch'i temizlememesi            |
| Dashboard                  | Tamamlandı | Login/logout, bellek içi JWT, refresh, role UI ve authenticated SignalR              |
| Container tanımları        | Tamamlandı | Release API/Vision, Caddy static dashboard, ayrı dev compose                         |
| Automated CI               | Kuruldu    | .NET integration, Dashboard, Firmware, Vision, MQTT TLS/ACL, Gitleaks                |
| Fiziksel LAN kabulü        | Bekliyor   | Gerçek hostname/IP, client CA trust, kamera, ESP32 ve compose health doğrulaması     |
| Secret/history remediation | Bloke      | Sızmış Wi-Fi credential rotation teyidi bekleniyor                                   |

## Tamamlanan güvenlik davranışı

### HTTP

- Yalnız `/health`, `/api/auth/login` ve `/api/auth/refresh` anonymous'dur.
- Public registration yoktur; ilk Admin boş kullanıcı tablosunda environment variable ile bootstrap edilir.
- Admin rol değişiklikleri serialize edilir; son Admin'in rolü düşürülemez.
- Refresh token raw değeri yalnız hardened cookie'dedir; veritabanında hash tutulur ve her refresh'te rotate edilir.
- SignalR Viewer+ gerektirir; hub istemcilerin sahte event broadcast etmesine izin veren public metot içermez.
- Dashboard production'da simulator panelini render etmez; development device key'i Vite proxy process'inde kalır.

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

Normal stop `false/false`, emergency/fire `false/true`, reset `false/false`, start `true/false` yayınlar; her payload ayrıca monoton `resetGeneration` ile `acknowledgedFireEpoch/Generation` çiftini taşır. Firmware rastgele cihaz epoch'unu, latch'i, yerel fire neslini ve son reset neslini NVS'de doğrulanan tek güvenlik kaydında saklar; yalnız API'nin gördüğü aynı epoch içindeki yangın neslini kapsayan yeni Admin reset latch'i açabilir. Yeni/bozuk NVS fail-safe kilitlenir ve eski retained komutları reddeder. Öncelik `localFireActive || emergencyLatched || persistenceFault || !commandRunning` koşuludur ve bu durumda servo her döngüde `0°` tutulur. Fire temizlenmesi latch'i açmaz; Admin reset de konveyörü başlatmaz.

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

`SmartLine.API.Tests`, `WebApplicationFactory`, xUnit ve PostgreSQL Testcontainers kullanır. Test host'unda MQTT hosted service'leri kaldırılır ve recording fake kullanılır. Suite; auth/role, DeviceKey, gerçek SignalR WebSocket handshake, error codes, pagination, duplicate barcode concurrency, serialized emergency/fire interlock, client-cancellation preemption ve offline-fire acknowledgement, MQTT failure/reconnect, high-temperature, transaction rollback ve statistics akışlarını kapsar.

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

Integration suite; logout ve Admin user/role akışlarını, 429/500 envelope'larını, numeric enum reddini, tüm filtreli collection'ların boundary/second-page/stable-order davranışını, fan hysteresis'i, concurrent statistics upsert'ini ve MQTT reconnect sonrası desired-state republish'i doğrudan kapsar. Gerçek TestServer WebSocket bağlantıları anonymous erişimin `401` aldığını ve Viewer'ın SignalR protocol handshake'ini tamamladığını doğrular. Kontrollü PostgreSQL trigger hatası da high-temperature akışında kısmi conveyor, alert veya log state'i kalmadan transaction rollback gerçekleştiğini kanıtlar.

## Production kabul adımları

1. Wi-Fi credential dış sistemde rotate edilir ve teyit edilir.
2. `.env` gerçek rastgele secret'larla hazırlanır; hiçbir değer Git'e eklenmez.
3. LAN IP ile yerel CA/Mosquitto/Caddy sertifikaları üretilir.
4. `ca.crt` dashboard istemcilerine trusted root olarak kurulur; `smartline.local` aynı LAN IP'ye çözülür.
5. Firmware ignored credential/TLS header'ları oluşturulur ve iki PlatformIO environment'ı build edilir.
6. Production compose Release image'larla başlatılır; API, broker, database, Vision ve Caddy health kontrol edilir.
7. Login/role matrisi, SignalR, sensor DeviceKey, normal/emergency/reset/fire ve broker reconnect gerçek ortamda smoke-test edilir.
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
- Fiziksel fire → latch → clear → Admin reset → explicit start akışı doğrulanır.
- Wi-Fi credential rotation ve kontrollü Git history remediation tamamlanır.
- Branch protection ile secret scanning/push protection yeniden etkinleştirilir.
