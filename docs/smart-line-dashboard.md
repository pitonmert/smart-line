# SmartLine.Dashboard teknik referansı

SmartLine.Dashboard, React 19, TypeScript, Vite ve Tailwind tabanlı aynı-origin SPA'dır. İlk durumu REST ile alır, sonraki ürün/konveyör/ortam/alarm/log değişikliklerini SignalR üzerinden uygular.

## Kaynak yapısı

```text
SmartLine.Dashboard/src/
├── api/client.ts
├── auth/AuthContext.tsx
├── components/
│   ├── auth/
│   ├── conveyor/
│   ├── gauges/
│   ├── charts/
│   ├── alerts/
│   ├── activity/
│   ├── layout/
│   └── simulator/
├── context/DashboardContext.tsx
├── hooks/
│   ├── useDashboard.ts
│   ├── useSignalR.ts
│   └── useAlertSound.ts
├── types/index.ts
├── App.tsx
└── main.tsx
```

## Auth yaşam döngüsü

Dashboard public registration sağlamaz. Kullanıcı `POST /api/auth/login` ile oturum açar.

```text
Login
  -> access token yalnız JavaScript belleğinde
  -> refresh token HttpOnly/Secure/SameSite=Strict cookie'de
  -> REST Authorization: Bearer <access token>
  -> SignalR accessTokenFactory aynı token kaynağını kullanır
```

Access token `localStorage` veya `sessionStorage` içine yazılmaz. Sayfa yenilendiğinde bellek boş olduğu için `POST /api/auth/refresh` çağrılır; geçerli cookie varsa yeni access token alınır. API isteği 401 döndürürse tek kontrollü refresh yapılır ve istek bir kez tekrar edilir. Eşzamanlı refresh'ler ortak promise ile birleştirilir.

Logout endpoint'i server-side refresh kaydını revoke eder ve cookie'yi siler; hata olsa bile yerel bellek oturumu temizlenir.

## Rol bazlı arayüz

| Rol      | Dashboard davranışı                                                          |
| -------- | ---------------------------------------------------------------------------- |
| Viewer   | Dashboard/alarmlar/loglar/istatistikler salt okunur; conveyor kontrolü gizli |
| Operator | Hız, start, normal stop, emergency-stop ve alarm resolve kontrolleri görünür |
| Admin    | Operator yetkilerine ek olarak emergency latch reset görünür                 |

Emergency reset butonu fire sensörü aktifken disabled olur. Reset yalnız latch'i açar; konveyör kendiliğinden başlamaz. Emergency-stop için kullanıcıdan ayrıca onay alınır.

Frontend rol kontrolü güvenlik sınırı değildir; API aynı işlemleri server-side policy ile tekrar doğrular.

## API client ve hata davranışı

Tüm istekler relative `/api` yolunu ve `credentials: include` kullanır. Success/failure envelope tipi:

```ts
interface ApiResponse<T> {
  isSuccess: boolean;
  message: string;
  data: T;
  code?: string;
}
```

`ApiError`, HTTP status yanında kararlı API `code` değerini taşır. Kullanıcı mesajları `REQUEST_INVALID`, `ACCESS_DENIED`, `CONVEYOR_EMERGENCY_LATCHED`, `FIRE_SENSOR_ACTIVE`, `MQTT_UNAVAILABLE` gibi kodlardan seçilir; UI mantığı Türkçe backend mesajına bağlanmaz.

Dashboard'un kullandığı ana REST çağrıları:

- `POST /api/auth/login`, `/refresh`, `/logout`; `GET /api/auth/me`
- `GET /api/dashboard`, `/api/statistics/hourly`
- `POST /api/conveyor/speed`, `/start`, `/stop`, `/emergency-stop`, `/emergency-reset`
- `POST /api/alerts/{id}/resolve`
- Development simulator için sensor POST endpoint'leri

`ConveyorStatusDto`, `isEmergencyStopped`, `isFireDetected`, `fireDeviceEpoch`, `lastFireGeneration` ve `isFireGenerationFault` alanlarını içerir. UI herhangi bir safety interlock aktifken start ve speed kontrollerini kapatır; generation fault durumunda cihazın yeniden provision edilmesi gerektiğini gösterir ve Admin reset'i de devre dışı bırakır.

## Realtime akışı

`useSignalR`, `/hubs/dashboard` bağlantısını `accessTokenFactory` ile açar ve otomatik reconnect aralıklarını `0`, `2`, `5`, `10` saniye olarak ayarlar.

| SignalR event              | State etkisi                    |
| -------------------------- | ------------------------------- |
| `ReceiveConveyorUpdate`    | Conveyor state'i değiştirir     |
| `ReceiveEnvironmentUpdate` | Sıcaklık, nem ve fanı günceller |
| `ReceiveProductUpdate`     | Son ürünlere ekler              |
| `ReceiveAlertUpdate`       | Aktif alarm state'ini günceller |
| `ReceiveLogUpdate`         | Canlı log akışına ekler         |

Bağlantı yeniden kurulduğunda `GET /api/dashboard` ile snapshot tekrar alınır; bu, kaçırılmış realtime event'lerden sonra state'i düzeltir. Hub istemcilere broadcast yapan public `Send*` metotları sunmaz.

## Ekran bileşenleri

- `LoginPage`: e-posta/parola formu ve code-aware hata gösterimi.
- `ConveyorCard`: hız, running, emergency/fire state ve rol bazlı kontrol.
- `EmergencyStop`: normal stop ile emergency-stop'u ayrı çağırır; Admin reset ayrıdır.
- `EnvironmentPanel`: sıcaklık/nem/fan görünümü.
- `ProductionAndProducts`: canlı/saatlik üretim ve son ürünler.
- `AlertsPanel` ve `FireAlarmOverlay`: aktif alarm görünümü ve Operator resolve.
- `LiveLogFeed`: SignalR log akışı.
- `Header`: kullanıcı/roller, SignalR/cihaz durumu ve logout.

## Development simulator paneli

Browser tabanlı sensor simulator yalnız şu iki koşulda render edilir:

```text
import.meta.env.DEV && role >= Operator
```

Production bundle'da panel görünmez. Sensor POST endpoint'leri yalnız Device authentication kabul ettiği için development Vite proxy, `SMARTLINE_DEVICE_KEY` process environment değerini server-side `X-Device-Key` header'ına ekler. Bu değer `VITE_` prefix'i taşımaz ve frontend bundle'a gömülmez. Fire simulator, 32 karakterlik rastgele development `deviceEpoch` ve monoton `fireGeneration` değerini browser `localStorage` içinde korur; rising-edge yeni generation üretir, clear aynı generation'ı gönderir.

Lokal çalıştırma:

```bash
cd SmartLine.Dashboard
npm ci
API_TARGET=http://localhost:5047 \
SMARTLINE_DEVICE_KEY='<development-device-key>' \
npm run dev
```

Dashboard tarayıcıdan `http://localhost:5173` açılır; `/api` ve `/hubs` Vite proxy üzerinden API'ye gider. CORS kullanılmaz.

## Build ve production

```bash
cd SmartLine.Dashboard
npm run lint
npm run build
```

Production static build, `infra/caddy/Dockerfile` içindeki Node build stage'inde oluşturulur. Caddy `https://smartline.local` üzerinden static dosyaları sunar ve `/api`, `/hubs`, `/health` yollarını API container'ına proxy eder. Browser ile API aynı origin'dedir.

Production kurulumu ve yerel CA trust adımları için [deployment.md](deployment.md) kullanılır.

## Sınırlar

- Access token bellek içi olduğu için sayfa yenileme geçerli refresh cookie gerektirir; bu beklenen davranıştır.
- UI'nın connected göstergesi safety kanıtı değildir. Fiziksel safety relay/hardwired E-stop ayrı zorunluluktur.
- Production tek API instance varsayar; SignalR backplane yoktur.
