# SmartLine Dashboard

React, TypeScript ve Vite tabanlı SmartLine operasyon ekranıdır. Production build aynı origin üzerinden `/api`, `/hubs` ve `/health` yollarına erişecek şekilde tasarlanmıştır.

## Authentication

- Kullanıcı girişinden dönen access token yalnız uygulama belleğinde tutulur.
- Sayfa yenilendiğinde `/api/auth/refresh` çağrısı, `HttpOnly` refresh cookie ile oturumu yeniden kurar.
- API isteği `401` döndürürse refresh token bir kez rotate edilir ve ilk istek yalnız bir kez tekrarlanır.
- SignalR bağlantısı güncel bearer token'ı `accessTokenFactory` üzerinden alır.
- `Viewer` salt-okunur ekranı, `Operator` kontrol işlemlerini, `Admin` ise emergency reset işlemini kullanabilir.

## Development

```bash
npm ci
npm run dev
```

Vite proxy ayarları process environment üzerinden okunur:

- `API_TARGET`: API adresi; varsayılan `http://localhost:5047`.
- `SMARTLINE_DEVICE_KEY`: yalnız `/api/sensors/*` proxy isteklerine `X-Device-Key` olarak eklenir.

`SMARTLINE_DEVICE_KEY` için `VITE_` öneki kullanılmaz. Böylece değer frontend bundle içine dahil edilmez. Simulator paneli yalnız development build'de ve `Operator` veya `Admin` kullanıcılar için gösterilir.

## Verification

```bash
npm test
npm run lint
npm run build
npm audit --omit=dev
```

`npm test` checks the camera overlay geometry and diagnostic status colors. The control center pages are Control Room, AI Inspection, Production Analytics, Sensors, Sorting, Product Recipes, Calibration, Dataset & AI, Inspection History, and System Diagnostics. The live MJPEG image uses `object-fit: contain`. Inspection boxes are drawn on a canvas from source pixels, using the same letterbox scale as the image. Dataset capture asks the API for the vision dataset frame. The page does not read pixels from the preview image. Simulated values stay labeled SIMULATION.
