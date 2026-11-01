# SmartLine.Dashboard teknik referansı

SmartLine.Dashboard, React 19, TypeScript, Vite ve Tailwind tabanlı aynı-origin kontrol merkezidir. Sayfalar history API ile değişir: Control Room, AI Inspection, Production Analytics, Sensors, Sorting, Product Recipes, Calibration, Dataset & AI, Inspection History, System Diagnostics.

Kamera paneli MJPEG görüntüsünü `object-fit: contain` ile doldurur. Çözünürlük artırılmaz. Bounding box koordinatları kaynak pikseldedir. Canvas, görüntünün letterbox ölçeği ve boşluk payıyla aynı dönüşümü kullanır. Overlay son inspection'a aittir; her akan kareye kilitli değildir. SIMULATION sonuçları rozetle gösterilir. Acil durdurma mevcut onaylı kontrol olarak kalır. Gerçek servo komutu bu arayüzden gönderilmez.

## Kaynak yapısı

```text
SmartLine.Dashboard/src/
├── api/client.ts
├── components/
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

## Direct access

The control center opens immediately without login or session restoration. There are no users, roles, token refresh requests or logout controls. All actions are visible; operation-specific conditions still apply. Emergency reset remains disabled while a fire or generation fault is active, and it does not automatically restart the conveyor.

## API client ve hata davranışı

Tüm istekler relative `/api` yolunu kullanır. Success/failure envelope tipi:

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

`ConveyorStatusDto`, `isEmergencyStopped`, `isFireDetected`, `fireDeviceEpoch`, `lastFireGeneration` ve `isFireGenerationFault` alanlarını içerir. UI herhangi bir safety interlock aktifken start ve speed kontrollerini kapatır; generation fault durumunda cihazın yeniden provision edilmesi gerektiğini gösterir ve manual reset'i de devre dışı bırakır.

## Realtime akışı

`useSignalR`, `/hubs/dashboard` bağlantısını without an access token açar ve otomatik reconnect aralıklarını `0`, `2`, `5`, `10` saniye olarak ayarlar.

| SignalR event              | State etkisi                    |
| -------------------------- | ------------------------------- |
| `ReceiveConveyorUpdate`    | Conveyor state'i değiştirir     |
| `ReceiveEnvironmentUpdate` | Sıcaklık, nem ve fanı günceller |
| `ReceiveProductUpdate`     | Son ürünlere ekler              |
| `ReceiveAlertUpdate`       | Aktif alarm state'ini günceller |
| `ReceiveLogUpdate`         | Canlı log akışına ekler         |

Bağlantı yeniden kurulduğunda `GET /api/dashboard` ile snapshot tekrar alınır; bu, kaçırılmış realtime event'lerden sonra state'i düzeltir. Hub istemcilere broadcast yapan public `Send*` metotları sunmaz.

## Ekran bileşenleri

- `ConveyorCard`: hız, running, emergency/fire state ve operational controls.
- `EmergencyStop`: normal stop ile emergency-stop'u ayrı çağırır; manual reset ayrıdır.
- `EnvironmentPanel`: sıcaklık/nem/fan görünümü.
- `ProductionAndProducts`: canlı/saatlik üretim ve son ürünler.
- `AlertsPanel` ve `FireAlarmOverlay`: aktif alarm görünümü ve alarm resolution.
- `LiveLogFeed`: SignalR log akışı.
- `Header`: SignalR/device connection state and clock.

## Development simulator paneli

Browser tabanlı sensor simulator yalnız şu iki koşulda render edilir:

```text
import.meta.env.DEV
```

Production bundle'da panel görünmez. Sensor HTTP requests do not require credentials. Fire simulator, 32 karakterlik rastgele development `deviceEpoch` ve monoton `fireGeneration` değerini browser `localStorage` içinde korur; rising-edge yeni generation üretir, clear aynı generation'ı gönderir.

Lokal çalıştırma:

```bash
cd SmartLine.Dashboard
npm ci
API_TARGET=http://localhost:5047 \
npm run dev
```

Dashboard tarayıcıdan `http://localhost:5173` açılır; `/api` ve `/hubs` Vite proxy üzerinden API'ye gider. CORS kullanılmaz.

## Build ve production

```bash
cd SmartLine.Dashboard
npm run lint
npm run build
npm run e2e
```

`npm run e2e` Playwright ile production preview üzerinde direct startup, on ana sayfa, kamera paneli, simülasyon bandı ve dataset yakalama düğmesini route mock'larıyla doğrular. No authentication requests or bearer headers are sent. Trace ve ekran görüntüleri üretilen dosyalardır.

The production Dashboard is built by the Node stage in `SmartLine.API/Dockerfile` and copied into the API image's `wwwroot`. ASP.NET Core serves it directly at `https://smartline.local`, alongside `/api`, `/hubs` and `/health`. Browser navigation routes return `index.html`; unknown API/hub routes and missing assets remain 404. Vite remains the development server with its local API/WebSocket forwarding.

To serve the built Dashboard from a local API checkout instead of Vite, run from the repository root:

```sh
npm --prefix SmartLine.Dashboard run build
mkdir -p SmartLine.API/wwwroot
cp -R SmartLine.Dashboard/dist/. SmartLine.API/wwwroot/
```

The generated `wwwroot` directory is ignored by Git. Start the API with its usual local database and MQTT settings, then open its configured URL. Docker production builds perform the Dashboard build and copy automatically. Do not put secrets or dataset files in `wwwroot`: everything there is public.

Production kurulumu ve yerel CA trust adımları için [deployment.md](deployment.md) kullanılır.

## Dataset toplama

Dataset & AI sayfası canlı MJPEG önizlemeyi gösterir ve `Capture from camera` ile sunucudan bir JPEG ister. Tarayıcı, cross-origin MJPEG öğesinin piksellerini okumaz. API, Vision `GET /frames/dataset` karesini tek dosya olarak dataset oturumuna yazar. Ürün sınıfı, renk, boyut ve kusur seçenekleri aktif Product Recipe listelerinden gelir. Kalite `GOOD` veya `DEFECTIVE` olur. Aralık yakalama aynı kamera uç noktasını, ekrandaki güncel etiketlerle çağırır. Thumbnails are fetched directly and displayed as blob URLs. Ham görüntüler Git'e girmez.

Dataset collection uses camera capture only, manually or at the configured interval. There is no file picker or disk import action. Existing image viewing, session controls, and sample discard remain available.

## Sınırlar

- UI'nın connected göstergesi safety kanıtı değildir. Fiziksel safety relay/hardwired E-stop ayrı zorunluluktur.
- Production tek API instance varsayar; SignalR backplane yoktur.
