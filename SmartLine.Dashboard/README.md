# SmartLine Dashboard

React, TypeScript, Vite and Tailwind UI for a single SmartLine production line. Production serves the Dashboard, `/api`, `/hubs` and `/health` from the API's origin. No additional UI framework is required.

## Navigation

- **Production:** read-only monitoring with balanced camera/last-evaluation panels, inspection counters, environment readings and active problems. The preview is independent of the stored evaluation; previous inspection boxes are never drawn on a newer frame.
- **History:** searchable, paginated inspections with expandable metadata. The optional production summary includes separate inspection totals and barcode statistics.
- **Management:** conveyor control; recipe, calibration, sensor and diagnostic maintenance; manual inspection, logical sorting, camera dataset collection and simulation tools.

The main addresses are `/`, `/history` and `/management`. Management tasks have their own `/management/...` URLs. Old page URLs redirect to their new locations; `/analytics` redirects to History.

The Dashboard does not implement automatic product detection or trigger an inspection when Production opens. Manual inspection remains an explicit tool. Logical sorting records do not assert physical servo movement.

## Language and appearance

The header offers TR / EN, dark / light themes and full screen. English and dark are the defaults. Preferences are stored as `smartline.language` and `smartline.theme`. Invalid or inaccessible storage uses the defaults; switching remains possible without storage. Dates and numbers follow the selected language. Identifiers, user content and unknown API messages remain unchanged.

## Local development

```sh
npm ci
npm run dev
```

Open `http://localhost:5173`. Set `API_TARGET` to override the development API target (default `http://localhost:5047`). API and SignalR calls are relative and forwarded by Vite. HTTP accounts and device/service keys are not used.

Sensor simulators appear only in development under Management → Tools → Simulation tools. The inspection simulator also requires the API's existing simulation setting. Automatic browser sends stop when their screen unmounts; dataset intervals pause on hidden/offline pages and failed requests.

## Mock preview

```sh
npm run dev:mock
```

Browse without PostgreSQL, MQTT, Vision, the API or camera hardware. A Vite-only mock server supplies example data, a SignalR connection and an illustrated camera frame. A `MOCK PREVIEW` banner identifies this mode. Writes return `409 MOCK_READ_ONLY` and never reach a real backend. Records reset on restart. Mock mode is excluded from the production server.

## Verification

```sh
npm run lint
npm test
npm run build
npm run e2e
```

Unit tests cover localization, theme preferences, diagnostic states and overlay geometry. Playwright uses a production preview with mocked API and SignalR responses to verify navigation, read-only monitoring, controls, failures, dataset capture, filtering and preferences. It does not validate hardware behavior.
