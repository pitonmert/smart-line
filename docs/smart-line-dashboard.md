# SmartLine.Dashboard

The Dashboard uses React, TypeScript, Vite and Tailwind. It is an operator monitoring interface with separate maintenance and manual tools. API decisions and backend contracts are unchanged by the UI layout.

## Navigation and responsibilities

| Main area | Contents |
| --- | --- |
| Production (`/`) | Camera preview, last stored evaluation, real inspection counters, environment status and active problems. No conveyor commands or inspection triggers. |
| History (`/history`) | Search, decision/class/source filters, pagination and expandable inspection metadata. Optional summary keeps barcode counts separate from inspection counts. |
| Management (`/management`) | Task groups for line control, settings/maintenance and technical tools. Only the selected task is mounted. |

Management routes: `/management/control`, `/management/recipes`, `/management/calibration`, `/management/sensors`, `/management/diagnostics`, `/management/inspection`, `/management/sorting`, `/management/dataset` and `/management/simulation`.

Previous `/inspection`, `/recipes`, `/calibration`, `/sensors`, `/diagnostics`, `/sorting` and `/dataset` URLs are replaced with their management equivalents. `/analytics` redirects to `/history`. Browser back/forward and direct loading remain supported; unrecognized addresses show a return link.

## Production monitoring

The camera and last evaluation have equal visual weight on wide screens and stack on small screens. At 1920×1080, the normal overview fits without scrolling; expanded details and unusual amounts of alert content may require scrolling.

The camera polls the existing API preview endpoint. It is not a continuous inference trigger. The ROI is mapped using the existing source geometry and letterbox calculations. Stored inspection boxes are not drawn over a later preview. Camera failure is marked even if the previous frame remains visible.

The result shows class, decision, confidence, destination and processing time; expandable metadata includes color, size, defects, reasons, model, recipe version, source and dimensions. Simulation remains explicitly labeled. Missing values are unknown, not zero or healthy.

Inspection counters exclude simulation. Yield is `pass / (pass + reject)`; manual review is outside that denominator. No decided population is displayed as a dash. Destination counts represent logical destinations, not confirmed physical arrivals.

## Data and failures

`PlantContext` shares control-room, diagnostics and recipe queries across the header and screens. Inspection/tracking SignalR events refresh monitoring data. Tool-specific polling starts only when the task mounts and stops on unmount. History analytics mounts only while its summary is open.

`DashboardContext` holds conveyor, environment, product, alert and connection state. `useSignalR` connects to `/hubs/dashboard` without credentials, using reconnect delays of 0, 2, 5 and 10 seconds. On reconnection, `/api/dashboard` refreshes missed state.

Failed polls retain the last known response with an error indicator. Conveyor commands require a connected hub, recent monitoring responses, an online ESP32 and connected MQTT. Existing emergency/fire/generation interlocks remain enforced. Resetting a latch does not start the conveyor. Write errors preserve form input and show feedback; successful requests use the returned server state.

Global fire reporting and alert sound remain available in every section. Alarm acknowledgment is performed in Management. No user accounts, roles, token restoration or HTTP device/service keys are used.

## Management and tools

- Line control provides speed, start/normal stop, emergency stop, reset and alert acknowledgment.
- Settings/maintenance provides recipes, calibration profiles, sensor readings and full diagnostics. Unintegrated measurements are not presented as real hardware data.
- Tools provides explicit camera inspection and manual observations, logical sorting, dataset collection and simulation.

Logical release records SORTED without sending a servo command. Camera dataset collection preserves sessions, labels, image browsing, discard and manual/interval captures; disk uploads are absent. Dataset intervals stop on page unmount and pause on visibility loss, offline state or request failures. Sensor simulators are development-only; inspection simulation additionally follows the API's simulation flag. No hidden simulator is kept running by the global layout.

## Language, theme and accessibility

TR/EN and dark/light selections apply without reloading or resetting forms. Defaults are English and dark. Browser storage keys are `smartline.language` and `smartline.theme`; corrupt or unavailable storage falls back safely. Locale formatting and document language/title follow the language. API wire values, recipe names, user notes and unknown server text are preserved.

Theme colors use shared CSS tokens, including gauges. Visible keyboard focus, semantic links, expanded-state controls and textual status labels support operation without relying on color. The header offers application full screen, independently of camera-only full screen. Responsive layouts support desktop monitoring and smaller maintenance screens.

## Development and mock preview

```sh
cd SmartLine.Dashboard
npm ci
npm run dev
# Hardware-free, read-only fixtures instead:
npm run dev:mock
```

Vite forwards API and hub traffic using `API_TARGET` (default `http://localhost:5047`). Mock mode replaces that forwarding with local fixtures, a SignalR mock and an illustrated frame; writes return `409 MOCK_READ_ONLY`. Normal development and production do not enable the mock server.

## Verification and deployment

```sh
npm run lint
npm test
npm run build
npm run e2e
```

Playwright tests run against a production preview with mocked services. Test/build success does not establish live hardware or production availability.

The Node stage of `SmartLine.API/Dockerfile` builds the Dashboard and copies `dist` into the API's `wwwroot`. ASP.NET Core serves the application at the configured HTTPS origin. Browser routes return `index.html`; unknown API/hub routes and missing assets remain 404. See [deployment.md](deployment.md) for LAN certificates and deployment.

For a local API checkout, build the Dashboard and copy `SmartLine.Dashboard/dist/.` into the ignored `SmartLine.API/wwwroot/` directory. Do not place secrets or dataset files there.
