# SmartLine.API teknik referansı

> .NET 10, ASP.NET Core Minimal API, EF Core ve PostgreSQL

## Mimari

API, Minimal API + Vertical Slice düzenini korur. Endpoint, request/response modeli ve use-case handler'ı ilgili feature altında birlikte bulunur. Slice'lar EF Core `ApplicationDbContext`'ini doğrudan kullanır.

```text
SmartLine.API/
├── Domain/
│   ├── Entities/
│   ├── Enums/
├── Features/
│   ├── Conveyor/
│   ├── Monitoring/
│   ├── Production/
│   └── Safety/
├── Infrastructure/
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

## HTTP access

The Dashboard, REST API and SignalR hub are available without login, roles, JWT, refresh cookies or device/service keys. Any client that can reach the API can invoke its endpoints. Input validation, transaction handling, simulation flags, fire interlocks and emergency latches still apply. MQTT retains its separate certificate/password authentication and ACLs.

User and session endpoints have been removed and return 404. Migration `RetireHttpIdentity` removes the obsolete Identity and refresh-token tables; business tables are retained.

## Endpoint'ler

### Operasyon ve veri

| Alan             | Method ve yollar                                                                                       | Yetki     |
| ---------------- | ------------------------------------------------------------------------------------------------------ | --------- |
| Conveyor read    | `GET /api/conveyor/status`                                                                             | Public |
| Conveyor control | `POST /api/conveyor/speed`, `/start`, `/stop`, `/emergency-stop`                                       | Public |
| Emergency reset  | `POST /api/conveyor/emergency-reset`                                                                   | Public |
| Products read    | `GET /api/products`, `/{id}`, `/barcode/{barcode}`, `/defective`, `/status/{status}`, `/color/{color}` | Public |
| Product create   | `POST /api/products`                                                                                   | Public |
| Alerts read      | `GET /api/alerts`, `/{id}`, `/active`, `/type/{type}`                                                  | Public |
| Alert resolve    | `POST /api/alerts/{id}/resolve`                                                                        | Public |
| Logs             | `GET /api/logs`, `/severity/{level}`, `/event/{eventType}`                                             | Public |
| Statistics       | `GET /api/statistics/daily`, `/hourly`                                                                 | Public |
| Dashboard        | `GET /api/dashboard`                                                                                   | Public |
| Sensor read      | `GET /api/sensors/status`                                                                              | Public |
| Sensor ingest    | `POST /api/sensors/barcode`, `/environment`, `/fire`                                                   | Public |
| Realtime         | `/hubs/dashboard`                                                                                      | Public |
| Recipes          | `GET /api/recipes`, `/active`, `/{id}`; `POST /api/recipes`; `PUT /api/recipes/{id}`; `POST /{id}/activate` | Public |
| Recipe snapshot  | `GET /api/recipes/active/snapshot`                                                                 | Public |
| Calibration      | `GET/POST /api/calibration-profiles`, `PUT /{id}`, `POST /{id}/activate`                               | Public |
| Inspections      | `GET /api/inspections`, `/{id}`                                                                        | Public |
| Inspection ingest| `POST /api/inspections`                                                                                | Public |
| Tracking         | `GET /api/tracking`, `/queue`; `POST /api/tracking/{id}/release`                                       | Public |
| Simulation       | `GET /api/simulation/status`; `POST /arrivals`, `/tick`, `/clear`                                      | Public |
| Dataset          | `GET /api/dataset/sessions`, `/{id}/images/{fileName}`; `POST` start, `/{id}/stop`, `/{id}/captures/camera`; `DELETE /{id}/samples/{sampleId}` | Public |
| Diagnostics      | `GET /api/diagnostics`, `GET /api/control-room`                                                        | Public |

HTTP request rate limiting is not configured. No HTTP authentication or authorization is configured. The API accepts direct connections and does not process forwarded headers.

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
| `RESOURCE_NOT_FOUND`         |        404 | Kaynak yok                                                                     |
| `PRODUCT_BARCODE_DUPLICATE`  |        409 | Barcode unique index çakışması                                                 |
| `PRODUCT_COLOR_INVALID`      |        400 | Tanımsız renk                                                                  |
| `PRODUCT_STATUS_INVALID`     |        400 | Tanımsız product status                                                        |
| `CONVEYOR_MUST_BE_STOPPED`   |        409 | Çalışırken hız değişikliği                                                     |
| `CONVEYOR_EMERGENCY_LATCHED` |        409 | Emergency latch interlock                                                      |
| `FIRE_SENSOR_ACTIVE`         |        409 | Aktif fire interlock                                                           |
| `FIRE_GENERATION_MISMATCH`   |        409 | Aynı cihaz epoch'unda geri saran güvenlik sayacı; yeniden provisioning gerekir |
| `MQTT_UNAVAILABLE`           |        503 | Broker komutu teslim edilemedi                                                 |
| `DATABASE_CONFLICT`          |        409 | Genel constraint/concurrency çakışması                                         |
| `UNEXPECTED_ERROR`           |        500 | Beklenmeyen sunucu hatası                                                      |
| `RECIPE_INVALID`             |        400 | Product Recipe tanımı geçersiz                                                 |
| `CALIBRATION_INVALID`        |        400 | Kalibrasyon değeri tehlikeli veya anlamsız                                     |
| `SIMULATION_DISABLED`        |        403 | Simülasyon bu ortamda kapalı                                                   |
| `TRACKING_TRANSITION_INVALID`|        400 | Ürün durum makinesi geçişi geçersiz                                            |
| `DATASET_INVALID`            | 400, 409, 503 | Dataset oturumu, etiket veya görüntü geçersiz. Kamera yakalama Vision yapılandırılmamışsa veya erişilemiyorsa 503, kare henüz yoksa 409 döner |

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

Tüm conveyor payload'ları kalıcı `resetGeneration=N` ile API'nin gördüğü son firmware güvenlik kimliği `acknowledgedFireEpoch=E` ve `acknowledgedFireGeneration=F` değerlerini taşır. Yalnız Manual reset `N` sayacını artırır. Firmware reset/start komutunu ancak `E` kendi kalıcı `deviceEpoch` değeriyle birebir eşleşiyor ve `F` yerel `fireGeneration` değerini kapsıyorsa uygular; sıradan stop veya eski retained mesaj daha yeni bir latch'i açamaz.

| İşlem          | Kalıcı durum                                                          | MQTT retained payload                                                                                                  |
| -------------- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| Normal stop    | `isRunning=false`, latch ve `N` değişmez                              | `{"isRunning":false,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`   |
| Emergency stop | stopped + latch; `N` değişmez                                         | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |
| Fire detected  | stopped + fire + latch; `E/F` güncellenir                             | `{"isRunning":false,"emergency":true,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |
| Fire clear     | fire temizlenir, latch ve `N` kalır                                   | Emergency retained state yeniden yayınlanır                                                                            |
| Manual reset    | fire temizse latch açılır, stopped kalır; `N+1`                       | `{"isRunning":false,"emergency":false,"resetGeneration":N+1,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}` |
| Explicit start | interlock ve epoch/generation uyuşmazlığı yoksa running; `N` değişmez | `{"isRunning":true,"emergency":false,"resetGeneration":N,"acknowledgedFireEpoch":E,"acknowledgedFireGeneration":F}`    |

Firmware fire mesajında üç alan da zorunludur: `{"isDetected":false,"deviceEpoch":"0123456789abcdef0123456789abcdef","fireGeneration":F}`. API daha önce görmediği yeni epoch veya daha yüksek generation değerini fire temizlenmiş olarak alsa bile çevrimdışı/commissioning güvenlik olayını kaydeder, emergency latch'i korur ve sonraki Manual reset'te bu çifti acknowledge eder. Aynı epoch içinde geri saran sayaç `FIRE_GENERATION_MISMATCH` ile fail-safe fault üretir; yeni bir cihaz epoch'u görülmeden start/reset yapılamaz.

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
- `Mqtt__BrokerAddress`, `Mqtt__Port=8883`, username/password, `CaCertificatePath`, `ClientCertificatePath` ve `ClientKeyPath`
- `TimeZone__Id`
- `EnvironmentThresholds__HighTempThreshold`, `AlarmTempThreshold`, `FanHysteresisCelsius`

MQTT topic/port/certificate path, timezone ve sıcaklık seçenekleri `ValidateOnStart` ile doğrulanır. API broker hostname'ini yerel CA ile doğrular ve yalnız kendi client cert/key çiftini sunar; Mosquitto ayrıca API username/password doğrulamasını uygular. Uygulama başlangıçta migration'ı uygular. Geçerli ayarlara rağmen broker geçici olarak erişilemiyorsa MQTT listener hatayı loglar ve beş saniyede bir yeniden bağlanmayı dener; komut isteyen endpoint'ler bu sırada `503 MQTT_UNAVAILABLE` döndürür.

## Migration ve testler

`20260809092419_InitialCreate` initially created domain, identity and session tables; `RetireHttpIdentity` removes the retired identity/session tables. `20261006094158_InspectionPlatform` product recipe, calibration, inspection history, product track ve dataset session tablolarını ekler. API açılışta `MigrateAsync` çalıştırır. Inspection sonuçları MQTT'ye yazılmaz; HTTP `POST /api/inspections` is used without a device key. Bu seçim mevcut Mosquitto ACL testini genişletmez.

Eski bir veritabanındaki DemoBox satırı, gömülü `demobox-legacy-320x240.json` belgesiyle birebir aynıysa kamera profili `320x240` değerinden `240x176` HQVGA profiline alınır ve sürüm bir artar. Karşılaştırma tüm tarifi kapsar. Ad, sınıf, akış adresi veya başka bir alan değişmişse satır olduğu gibi kalır. Yeni bir veritabanı doğrudan `240x176` ile başlar. Veritabanını silmek gerekmez.

Kamera veri kümesi yakalaması Vision `GET /frames/dataset` çağırır. Vision önce ham RGB565 `/capture/raw` karesini tercih eder ve JPEG'i hostta üretir. Bu örneğin `captureSource` değeri `esp32_capture_raw` olur. Ham yol kullanılamazsa kamera JPEG'i `esp32_capture` olarak kalır. `409` yanıtı görüntü saklamaz ve mesaj, önizleme yedeği kapalıyken still yakalamanın başarısız olduğunu söyler. Saklanan örnek `captureSource`, `previewFallback`, `sourceWidth` ve `sourceHeight` taşır. Inspection sonucu `ResultJson` içinde `recipeVersion`, `frameRole`, `captureSource` ve `previewFallback` alanlarını tutar. Yeni kolon veya migration yoktur. `GET /api/recipes/active/snapshot` does not require a device key ve etkin tarifin kimliğini, adını, sürümünü ve tanımını döner. All API clients can read this snapshot. Tanı yanıtı still boyunu, son still hatasını, sensör PID değerini ve etkin tarif sürümünü ekler. Bu alanlar yoksa istemci `Not reported` gösterir. Wi-Fi kimliği yoktur.

`Vision:HealthUrl` boşsa tanı `NOT_CONFIGURED` olur. Adres ayarlı ama health isteği başarısızsa sonuç `OFFLINE` olur. Yalnız gövdesi `healthy` olan bir yanıt `ONLINE` sayılır. Kamera, Vision `ONLINE` değilken `UNKNOWN` kalır. Development compose bu adresi koymaz. `docker-compose.vision.yml` onu `http://vision:8090/health` yapar. Production aynı iç adresi kullanır ve portu host'a açmaz. Dataset dosyaları `Dataset:Root` altındadır; compose bunu yazılabilir `/var/lib/smartline/datasets` volume'una bağlar.

`SmartLine.API.Tests` xUnit, `WebApplicationFactory` ve gerçek PostgreSQL Testcontainers kullanır. Test host'u MQTT hosted service'lerini recording fake ile değiştirir. Karşılıklı TLS, eksik/güvenilmeyen client certificate, parola ve ACL davranışı ayrıca disposable Mosquitto ile ayrı ephemeral test client container'ında CI'da doğrulanır; broker container'ına client private key mount edilmez.

```bash
dotnet restore SmartLine.slnx
dotnet build SmartLine.slnx --configuration Release --no-restore
dotnet test SmartLine.API.Tests/SmartLine.API.Tests.csproj --configuration Release --no-build
```

## Production sınırları

- Production yalnız tek aktif API instance varsayar; Redis backplane, distributed lock ve message bus yoktur.
- ASP.NET Core serves the Dashboard and API over the same HTTPS origin. Production publishes host port 443 to API port 5443; internal services use HTTP port 5047, which is not published. No reverse proxy or CORS middleware is used. Development retains Vite's API forwarding.
- Firmware'deki `0°` servo güvenli konum prototip varsayımıdır. Fiziksel hardwired E-stop ve safety relay yazılımın yerine geçemez.
- Credential rotation ve Git history remediation tamamlanmadan remote history rewrite/publish yapılmamalıdır. Ayrıntı için [deployment.md](deployment.md) kullanılır.


## v0.2.0 integration contract

- `POST /api/inspections/vision` accepts `{ "requestId": "<non-empty UUID>" }`. The API pins the active recipe revision and selected calibration before calling Vision. A changed snapshot returns 409; camera/model failure returns 503, and capture timeout returns 504. Failed inspections do not create success records. Retrying a persisted request returns 200 with the original inspection; the first successful request returns 201.
- `GET /api/camera/preview` returns the Vision preview JPEG. Browsers do not open another ESP32 stream.
- `POST /api/inspections` records operator/device observations. It does not infer model provenance from user-supplied model fields. Optional `recipeVersion` rejects a stale revision; optional `capturedAt` is normalized to UTC.
- Result JSON retains `calibrationProfileId` and `calibrationVersion`. It also includes `observationSource` (`manual`, `model`, `simulation`). Manual results have no invented model name, version or inference latency. Existing records remain readable.
- `control-room` counts and yield exclude simulation; latest result and current-product panels may still show explicitly marked simulated records.
- Activations and inspection writes use database transactions and a shared advisory lock. Dataset storage supports one API instance; it uses a unique ACTIVE-session index, atomic manifest replacement, rollback on ordinary write errors, and startup reconciliation. Database and dataset backups must be consistent. This is not distributed filesystem/database atomic commit.
- Calibration scale requires `sourceWidth` and `sourceHeight`; a different capture resolution produces no physical estimate. `calibrationProfileId` selects that exact stored profile; otherwise the active profile is used. Orientation, flip, perspective, physical ToF and servo actuation are not applied in this version.

Dataset capture accepts camera frames through Vision only. The former multipart `POST /api/dataset/sessions/{id}/captures` route is removed and returns the standard 404 error envelope. Existing JPEG and PNG images remain readable.
