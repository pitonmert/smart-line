# SmartLine.API teknik referansı

> .NET 10, ASP.NET Core Minimal API, EF Core ve PostgreSQL

## Mimari

API, Minimal API + Vertical Slice düzenini korur. Endpoint, request/response modeli ve use-case handler'ı ilgili feature altında birlikte bulunur. Slice'lar EF Core `ApplicationDbContext`'ini doğrudan kullanır.

```text
SmartLine.API/
├── Domain/
│   ├── Entities/
│   ├── Enums/
│   └── Identity/
├── Features/
│   ├── Auth/
│   ├── Conveyor/
│   ├── Monitoring/
│   ├── Production/
│   └── Safety/
├── Infrastructure/
│   ├── Authentication/
│   ├── Configuration/
│   ├── Hosting/
│   ├── Messaging/Mqtt/
│   ├── Persistence/
│   └── Realtime/
├── Shared/
├── Migrations/
└── Program.cs
```

Generic repository, Unit of Work, MediatR, Carter, AutoMapper, FluentValidation, Swagger/OpenAPI ve event bus kullanılmaz. Use-case transaction ve `SaveChangesAsync` sahipliği feature handler'ındadır.

## Authentication ve kullanıcı yaşam döngüsü

ASP.NET Core Identity, `ApplicationUser<Guid>`, roller ve refresh token kayıtları aynı PostgreSQL veritabanındadır. Public registration endpoint'i yoktur.

- Access token JWT'dir ve varsayılan ömrü 15 dakikadır.
- Refresh token varsayılan 7 gün geçerlidir; veritabanında yalnız SHA-256 hash'i saklanır.
- Refresh token her kullanımda rotate edilir ve eski token replay'i reddedilir.
- Raw refresh token `smartline.refresh` adlı `HttpOnly`, `Secure`, `SameSite=Strict` cookie'dedir.
- Cookie yalnız `/api/auth` path'ine gönderilir.
- Beş hatalı parola denemesinden sonra kullanıcı 15 dakika kilitlenir.
- İlk Admin yalnız kullanıcı tablosu boşken `BootstrapAdmin__Email` ve `BootstrapAdmin__Password` ile oluşturulur. Sonraki kullanıcıları Admin endpoint'leri yönetir.

Device HTTP trafiği ayrı `DeviceKey` authentication scheme'i kullanır. `X-Device-Key` header değeri `DeviceAuthentication__Key` ile sabit zamanlı karşılaştırılır ve başarılı kimlik `Device` rolü üretir.

## Yetki matrisi

Roller hiyerarşik policy'lerle uygulanır: Admin, Operator ve Viewer yetkilerini; Operator da Viewer yetkilerini taşır. UI görünürlüğü yalnız kullanıcı deneyimidir; asıl kontrol API policy'leridir.

| Erişim    | HTTP/SignalR yüzeyi                                                                               |
| --------- | ------------------------------------------------------------------------------------------------- |
| Anonymous | `GET /health`, `POST /api/auth/login`, `POST /api/auth/refresh`                                   |
| Viewer+   | Tüm salt-okunur API endpoint'leri, `POST /api/auth/logout`, `GET /api/auth/me`, `/hubs/dashboard` |
| Operator+ | Ürün oluşturma, alarm çözme, hız/start/normal-stop/emergency-stop                                 |
| Admin     | Emergency reset, kullanıcı oluşturma ve rol değiştirme                                            |
| Device    | `POST /api/sensors/barcode`, `/environment`, `/fire`                                              |

Fallback policy nedeniyle açıkça anonymous yapılmayan yeni endpoint'ler de authentication gerektirir.

## Endpoint'ler

### Auth ve yönetim

| Method | Yol                          | Yetki              | Açıklama                                                    |
| ------ | ---------------------------- | ------------------ | ----------------------------------------------------------- |
| `POST` | `/api/auth/login`            | Anonymous          | Access token üretir, refresh cookie set eder                |
| `POST` | `/api/auth/refresh`          | Anonymous + cookie | Refresh token rotate eder                                   |
| `POST` | `/api/auth/logout`           | Viewer+            | Aktif refresh token'ı revoke eder ve cookie'yi siler        |
| `GET`  | `/api/auth/me`               | Viewer+            | Geçerli kullanıcı ve roller                                 |
| `POST` | `/api/admin/users`           | Admin              | Viewer/Operator/Admin kullanıcısı oluşturur                 |
| `PUT`  | `/api/admin/users/{id}/role` | Admin              | Tek kullanıcı rolünü değiştirir ve security stamp'i yeniler |

Role değişiklikleri Admin rol satırı üzerinde transaction lock ile serialize edilir. Sistemdeki son Admin'in rolü düşürülemez; eşzamanlı iki demotion isteğinden yalnız biri başarılı olabilir.

Login başarı verisi:

```json
{
  "accessToken": "<jwt>",
  "expiresAt": "2026-08-09T12:15:00Z",
  "user": {
    "id": "00000000-0000-0000-0000-000000000000",
    "email": "operator@example.invalid",
    "roles": ["Operator"]
  }
}
```

### Operasyon ve veri

| Alan             | Method ve yollar                                                                                       | Yetki     |
| ---------------- | ------------------------------------------------------------------------------------------------------ | --------- |
| Conveyor read    | `GET /api/conveyor/status`                                                                             | Viewer+   |
| Conveyor control | `POST /api/conveyor/speed`, `/start`, `/stop`, `/emergency-stop`                                       | Operator+ |
| Emergency reset  | `POST /api/conveyor/emergency-reset`                                                                   | Admin     |
| Products read    | `GET /api/products`, `/{id}`, `/barcode/{barcode}`, `/defective`, `/status/{status}`, `/color/{color}` | Viewer+   |
| Product create   | `POST /api/products`                                                                                   | Operator+ |
| Alerts read      | `GET /api/alerts`, `/{id}`, `/active`, `/type/{type}`                                                  | Viewer+   |
| Alert resolve    | `POST /api/alerts/{id}/resolve`                                                                        | Operator+ |
| Logs             | `GET /api/logs`, `/severity/{level}`, `/event/{eventType}`                                             | Viewer+   |
| Statistics       | `GET /api/statistics/daily`, `/hourly`                                                                 | Viewer+   |
| Dashboard        | `GET /api/dashboard`                                                                                   | Viewer+   |
| Sensor read      | `GET /api/sensors/status`                                                                              | Viewer+   |
| Sensor ingest    | `POST /api/sensors/barcode`, `/environment`, `/fire`                                                   | Device    |
| Realtime         | `/hubs/dashboard`                                                                                      | Viewer+   |

Auth endpoint'leri istemci IP + endpoint başına dakikada 20; barcode/environment endpoint'leri IP + endpoint başına saniyede 100; fire endpoint'i ayrı safety bucket'ında saniyede 1000; normal conveyor control endpoint'leri kullanıcı + endpoint başına 10 saniyede 200 istekle sınırlıdır. Emergency-stop normal control limiter'ından bağımsızdır. Production, yalnız izole frontend ağındaki sabit Caddy IP'sini trusted proxy kabul eder; bu sayede rate-limit anahtarı doğrulanmış `X-Forwarded-For` istemci IP'sini kullanır.

## Standart response ve hata kodları

`/health` health-check çıktısı dışında feature API cevapları camelCase `ApiResponse<T>` envelope'u kullanır:

```json
{
  "isSuccess": false,
  "message": "İstek açıklaması",
  "data": null,
  "code": "REQUEST_INVALID"
}
```

Success cevaplarında `code` JSON'dan atlanır. Failure cevaplarında karar verilmesi gereken kararlı alan `code`'dur; Türkçe `message` kullanıcıya gösterilebilir ama program mantığı buna bağlanmamalıdır.

| Code                         | Tipik HTTP | Anlam                                                                          |
| ---------------------------- | ---------: | ------------------------------------------------------------------------------ |
| `REQUEST_INVALID`            |        400 | JSON, binding veya genel doğrulama hatası                                      |
| `AUTHENTICATION_REQUIRED`    |        401 | Eksik/geçersiz kullanıcı veya device kimliği                                   |
| `ACCESS_DENIED`              |        403 | Rol/policy yetersiz                                                            |
| `RATE_LIMIT_EXCEEDED`        |        429 | Rate limit reddi                                                               |
| `RESOURCE_NOT_FOUND`         |        404 | Kaynak yok                                                                     |
| `PRODUCT_BARCODE_DUPLICATE`  |        409 | Barcode unique index çakışması                                                 |
| `PRODUCT_COLOR_INVALID`      |        400 | Tanımsız renk                                                                  |
| `PRODUCT_STATUS_INVALID`     |        400 | Tanımsız product status                                                        |
| `CONVEYOR_MUST_BE_STOPPED`   |        409 | Çalışırken hız değişikliği                                                     |
| `CONVEYOR_EMERGENCY_LATCHED` |        409 | Emergency latch interlock                                                      |
| `FIRE_SENSOR_ACTIVE`         |        409 | Aktif fire interlock                                                           |
| `FIRE_GENERATION_MISMATCH`   |        409 | Aynı cihaz epoch'unda geri saran güvenlik sayacı; yeniden provisioning gerekir |
| `LAST_ADMIN_REQUIRED`        |        409 | Son Admin hesabının yetkisi düşürülemez                                        |
| `MQTT_UNAVAILABLE`           |        503 | Broker komutu teslim edilemedi                                                 |
| `DATABASE_CONFLICT`          |        409 | Genel constraint/concurrency çakışması                                         |
| `UNEXPECTED_ERROR`           |        500 | Beklenmeyen sunucu hatası                                                      |

Enum parser case-insensitive çalışır; numeric string'leri ve `Enum.IsDefined` dışında kalan değerleri reddeder. Public product status değerleri `OK`, `DEFECTIVE`, `NO_BARCODE`, `WRONG_COLOR`; renkler `RED`, `BLUE`, `GREEN`, `YELLOW` olarak korunur.

## Pagination

Collection ve filtreli collection endpoint'leri aynı sözleşmeyi kullanır:

```json
{
  "items": [],
  "hasNextPage": false,
  "pageNumber": 1,
  "pageSize": 20,
  "hasPreviousPage": false
}
```

`totalCount` ve `totalPages` yoktur. `pageNumber` en az 1'e normalize edilir, `pageSize` en fazla 100'dür. Products varsayılanı 10; Alerts ve Logs varsayılanı 20'dir. Sorgular zaman alanından sonra `Id` tie-breaker kullanarak deterministik sıralanır.

Paginated yollar:

- Products: root, `defective`, `status/{status}`, `color/{color}`
- Alerts: root, `active`, `type/{type}`
- Logs: root, `severity/{level}`, `event/{eventType}`

Daily statistics yalnız `Hour IS NULL` satırlarını döndürür ve inclusive tarih aralığı en fazla 366 gündür. Hourly sorgu tek güne aittir ve en fazla 24 satır döndürür.

## Emergency-stop ve kalıcılık sözleşmesi

`ConveyorSetting` singleton satırı migration tarafından `Id=1`, stopped ve latch kapalı olarak seed edilir. Status GET hiçbir zaman veritabanına yazmaz.

Tüm conveyor payload'ları kalıcı `resetGeneration=N` ile API'nin gördüğü son firmware güvenlik kimliği `acknowledgedFireEpoch=E` ve `acknowledgedFireGeneration=F` değerlerini taşır. Yalnız Admin reset `N` sayacını artırır. Firmware reset/start komutunu ancak `E` kendi kalıcı `deviceEpoch` değeriyle birebir eşleşiyor ve `F` yerel `fireGeneration` değerini kapsıyorsa uygular; sıradan stop veya eski retained mesaj daha yeni bir latch'i açamaz.

| İşlem          | Kalıcı durum                                                          | MQTT retained payload                                                                                                  |
| -------------- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| Normal stop    | `isRunning=false`, latch ve `N` değişmez                              | `{"isRunning":false,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`   |
| Emergency stop | stopped + latch; `N` değişmez                                         | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |
| Fire detected  | stopped + fire + latch; `E/F` güncellenir                             | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |
| Fire clear     | fire temizlenir, latch ve `N` kalır                                   | Emergency retained state yeniden yayınlanır                                                                            |
| Admin reset    | fire temizse latch açılır, stopped kalır; `N+1`                       | `{"isRunning":false,"emergency":false,"resetGeneration":N+1,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` |
| Explicit start | interlock ve epoch/generation uyuşmazlığı yoksa running; `N` değişmez | `{"isRunning":true,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |

Firmware fire mesajında üç alan da zorunludur: `{"isDetected":false,"deviceEpoch":"0123456789abcdef0123456789abcdef","fireGeneration":F}`. API daha önce görmediği yeni epoch veya daha yüksek generation değerini fire temizlenmiş olarak alsa bile çevrimdışı/commissioning güvenlik olayını kaydeder, emergency latch'i korur ve sonraki Admin reset'te bu çifti acknowledge eder. Aynı epoch içinde geri saran sayaç `FIRE_GENERATION_MISMATCH` ile fail-safe fault üretir; yeni bir cihaz epoch'u görülmeden start/reset yapılamaz.

Speed mesajı stop/emergency durumunu temizlemez. Tek API instance içindeki conveyor transition'ları ve retained publish sırası ortak async command gate ile serialize edilir; emergency sinyali kapıyı beklemeden bounded internal token ile safety komutunu yollar ve istemci bağlantısı kesilse bile kalıcı transition/final retained publish tamamlanır. Normal MQTT komutlarında veritabanı commit'i önce, publish sonra yapılır; broker yoksa `503 MQTT_UNAVAILABLE` döner. Tekrarlanan ve eşzamanlı fire/emergency sinyalleri yeni alert/log üretmez.

Barcode, yüksek sıcaklık ve fire akışlarında domain değişiklikleri use-case transaction'ında tutulur. Barcode doğruluk kaynağı PostgreSQL `IX_Products_Barcode` unique index'idir. Statistics günlük/saatlik sayaçları `INSERT ... ON CONFLICT DO UPDATE` ile atomik artar; `(Date, Hour)` unique index'i `NULLS NOT DISTINCT` davranışındadır.

SignalR yayınları commit sonrasında best-effort'tur ve DB işlemini rollback etmez. Hub'da istemci tarafından çağrılabilen broadcast metodu yoktur. Event'ler:

- `ReceiveConveyorUpdate`
- `ReceiveEnvironmentUpdate`
- `ReceiveProductUpdate`
- `ReceiveAlertUpdate`
- `ReceiveLogUpdate`

MQTT bağlantısı tekrar kurulduğunda API sensör topic'lerine yeniden abone olur ve son kalıcı conveyor, speed ve fan desired state'ini tekrar yayınlar.

## Konfigürasyon ve fail-fast

Zorunlu production ayarları environment variable ile verilir:

- `ConnectionStrings__DefaultConnection`
- `Jwt__SigningKey` (en az 32 karakter), issuer/audience ve token süreleri
- `DeviceAuthentication__Key` (en az 32 karakter)
- `BootstrapAdmin__Email`, `BootstrapAdmin__Password` (yalnız ilk boş kullanıcı tablosu)
- `Mqtt__BrokerAddress`, `Mqtt__Port=8883`, username/password, `CaCertificatePath`, `ClientCertificatePath` ve `ClientKeyPath`
- `TimeZone__Id`
- `EnvironmentThresholds__HighTempThreshold`, `AlarmTempThreshold`, `FanHysteresisCelsius`
- `ReverseProxy__KnownProxy` (production Compose'ta izole frontend ağındaki sabit Caddy IP'si)

JWT, device key, MQTT topic/port/certificate path, timezone ve sıcaklık seçenekleri `ValidateOnStart` ile doğrulanır. API broker hostname'ini yerel CA ile doğrular ve yalnız kendi client cert/key çiftini sunar; Mosquitto ayrıca API username/password doğrulamasını uygular. Uygulama başlangıçta migration'ı uygular. Geçerli ayarlara rağmen broker geçici olarak erişilemiyorsa MQTT listener hatayı loglar ve beş saniyede bir yeniden bağlanmayı dener; komut isteyen endpoint'ler bu sırada `503 MQTT_UNAVAILABLE` döndürür.

## Migration ve testler

Mevcut model tek `20260809092419_InitialCreate` migration'ında toplanmıştır. Migration; domain tablolarını, Identity/role tablolarını, hash'li refresh token tablosunu, emergency/fire state ile device epoch/reset/fire generation alanlarını, singleton conveyor seed'ini ve düzeltilmiş unique index'leri içerir. Eski PostgreSQL verisiyle upgrade hedeflenmez; temiz veritabanı gerekir.

`SmartLine.API.Tests` xUnit, `WebApplicationFactory` ve gerçek PostgreSQL Testcontainers kullanır. Test host'u MQTT hosted service'lerini recording fake ile değiştirir. Karşılıklı TLS, eksik/güvenilmeyen client certificate, parola ve ACL davranışı ayrıca disposable Mosquitto ile ayrı ephemeral test client container'ında CI'da doğrulanır; broker container'ına client private key mount edilmez.

```bash
dotnet restore SmartLine.slnx
dotnet build SmartLine.slnx --configuration Release --no-restore
dotnet test SmartLine.API.Tests/SmartLine.API.Tests.csproj --configuration Release --no-build
```

## Production sınırları

- Production yalnız tek aktif API instance varsayar; Redis backplane, distributed lock ve message bus yoktur.
- Caddy aynı origin'de HTTPS sağlar; API doğrudan host portuna publish edilmez ve CORS middleware'i yoktur.
- Firmware'deki `0°` servo güvenli konum prototip varsayımıdır. Fiziksel hardwired E-stop ve safety relay yazılımın yerine geçemez.
- Credential rotation ve Git history remediation tamamlanmadan remote history rewrite/publish yapılmamalıdır. Ayrıntı için [deployment.md](deployment.md) kullanılır.
