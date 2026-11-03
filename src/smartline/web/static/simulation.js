import {
  $,
  $$,
  available,
  button,
  controlsAvailable,
  feedback,
  heading,
  input,
  messages,
  mock,
  operation,
  panel,
  poll,
  request,
  select,
  state,
  t,
  tr,
} from "./core.js";

const presets = [
  {
    name: "Red large",
    productClass: "BOX_RED",
    colorClass: "RED",
    sizeClass: "LARGE",
    defectClasses: [],
    confidence: 0.93,
  },
  {
    name: "Blue medium",
    productClass: "BOX_BLUE",
    colorClass: "BLUE",
    sizeClass: "MEDIUM",
    defectClasses: [],
    confidence: 0.9,
  },
  {
    name: "Damaged",
    productClass: "BOX_GREEN",
    colorClass: "GREEN",
    sizeClass: "SMALL",
    defectClasses: ["DAMAGED"],
    confidence: 0.88,
  },
];
export function inspectionSimulator(root, signal) {
  let enabled = false,
    busy = false;
  root.innerHTML = panel(
    "Inspection simulator",
    `<p class="tool-note">${tr("SIMULATION observations only. These values are not sensor or model measurements. No actuator command is sent.")}</p><div class="toolbar">${presets.map((p, i) => button(p.name, "arrival", `data-preset="${i}"`)).join("")}${button("Advance due queue", "tick")}</div>${messages}`,
  );
  const update = () => {
    root.hidden = !enabled;
    $$("[data-action]", root).forEach(
      (b) => (b.disabled = busy || !available()),
    );
  };
  poll(
    async () => {
      try {
        const result = await request(
          "/simulation/status",
          "GET",
          undefined,
          signal,
        );
        enabled = result.enabled === true;
        if (!signal.aborted) update();
      } catch {
        enabled = false;
        if (!signal.aborted) update();
      }
    },
    8000,
    signal,
  );
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest("[data-action]");
      if (!b || busy || !enabled || !available()) return;
      busy = true;
      update();
      if (b.dataset.action === "arrival")
        await operation(root, () =>
          request("/simulation/arrivals", "POST", {
            ...presets[Number(b.dataset.preset)],
            distanceMm: 1000,
            beltSpeedMmPerSecond: 200,
            sourceWidth: 240,
            sourceHeight: 176,
          }),
        );
      else if (b.dataset.action === "tick")
        await operation(root, () =>
          request("/simulation/tick", "POST", {
            utcNow: new Date(Date.now() + 60000).toISOString(),
          }),
        );
      busy = false;
      update();
    },
    { signal },
  );
  return update;
}
function readFire() {
  try {
    const value = JSON.parse(
      localStorage.getItem("smartline.dev.fire-sensor-state"),
    );
    if (
      value &&
      /^[a-f0-9]{32}$/.test(value.deviceEpoch) &&
      Number.isSafeInteger(value.fireGeneration) &&
      value.fireGeneration >= 0 &&
      typeof value.isDetected === "boolean"
    )
      return value;
  } catch {}
  return {
    deviceEpoch: crypto.randomUUID().replaceAll("-", ""),
    fireGeneration: 0,
    isDetected: false,
  };
}
export function simulationPage(root, signal) {
  let intervalTimer = null,
    inFlight = false;
  root.innerHTML = `${heading("Simulation tools")}<p class="tool-note">${tr("Tools are separate from daily production monitoring.")}</p><div data-inspection-simulator></div><div data-development class="stack">${panel(
    "Environment simulation",
    `<form data-environment-form class="form-grid">${input("temperature", "Temperature", "number", 25, 'min="-40" max="80" step="0.1"')}${input("humidity", "Humidity", "number", 50, 'min="0" max="100" step="0.1"')}<button class="primary-button" type="submit" data-device>${tr("Send")}</button></form><div class="toolbar">${[
      ["Normal (25°C, 50%)", 25],
      ["Hot (38°C)", 38],
      ["Alarm (42°C)", 42],
      ["Extreme (55°C)", 55],
    ]
      .map(([label, temp]) =>
        button(label, "temperature", `data-temperature="${temp}" data-device`),
      )
      .join("")}</div>`,
  )}${panel("Fire alarm", `<div class="toolbar">${button("Trigger fire", "fire", "data-device")}${button("Clear sensor", "clear-fire", "data-device")}</div>`)}${panel("Product flow", `<form data-barcode-form class="form-grid">${input("barcode", "Barcode", "text", "", 'maxlength="50"')}${select("color", "Color", ["RED", "BLUE", "GREEN", "YELLOW"])}<button class="primary-button" type="submit" data-product>${tr("Send product")}</button></form><div class="toolbar">${button("Random", "random", "data-product")}${button("Automatic mode", "automatic", 'role="switch" aria-checked="false" data-product')}${input("autoInterval", "Interval:", "range", 2, 'min="1" max="5"')}<span data-sent></span></div>`)}</div>${messages}`;
  const simulator = inspectionSimulator(
    $("[data-inspection-simulator]", root),
    signal,
  );
  function stop() {
    if (intervalTimer) clearInterval(intervalTimer);
    intervalTimer = null;
    const toggle = $('[data-action="automatic"]', root);
    toggle?.setAttribute("aria-checked", "false");
  }
  const update = () => {
    simulator();
    const dev = mock || state.diagnostics?.environment === "Development";
    $("[data-development]", root).hidden = !dev;
    $$("[data-device]", root).forEach(
      (b) => (b.disabled = !dev || !available()),
    );
    const productAllowed =
      dev && controlsAvailable() && state.dashboard?.conveyorStatus?.isRunning;
    $$("[data-product]", root).forEach((b) => (b.disabled = !productAllowed));
    if (!productAllowed || document.hidden) stop();
  };
  const sendEnvironment = async (temp, hum) =>
    operation(root, () =>
      request("/sensors/environment", "POST", {
        temperature: temp,
        humidity: hum,
      }),
    );
  $("[data-environment-form]", root).addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!available()) return;
    const f = new FormData(e.target);
    await sendEnvironment(
      Number(f.get("temperature")),
      Number(f.get("humidity")),
    );
  });
  const randomBarcode = () =>
    "BC_" + crypto.randomUUID().replaceAll("-", "").slice(0, 12).toUpperCase();
  const sendProduct = async (automatic) => {
    if (
      inFlight ||
      !controlsAvailable() ||
      !state.dashboard?.conveyorStatus?.isRunning
    )
      return;
    inFlight = true;
    const barcode = automatic
      ? randomBarcode()
      : $('[name="barcode"]', root).value.trim() || randomBarcode();
    const color = automatic
      ? ["RED", "BLUE", "GREEN", "YELLOW"][Math.floor(Math.random() * 4)]
      : $('[name="color"]', root).value;
    const result = await operation(root, () =>
      request("/sensors/barcode", "POST", { barcode, color }),
    );
    if (!result) stop();
    else if (!automatic) $('[name="barcode"]', root).value = "";
    inFlight = false;
  };
  $("[data-barcode-form]", root).addEventListener("submit", (e) => {
    e.preventDefault();
    sendProduct(false);
  });
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest("[data-action]");
      if (!b || !available() || b.disabled) return;
      const action = b.dataset.action;
      if (action === "temperature") {
        const temp = Number(b.dataset.temperature);
        $('[name="temperature"]', root).value = temp;
        if (temp === 25) $('[name="humidity"]', root).value = 50;
        await sendEnvironment(temp, Number($('[name="humidity"]', root).value));
      }
      if (action === "fire" || action === "clear-fire") {
        let sensor = readFire(),
          detected = action === "fire";
        if (detected && !sensor.isDetected) {
          if (sensor.fireGeneration >= 0xffffffff)
            sensor = {
              deviceEpoch: crypto.randomUUID().replaceAll("-", ""),
              fireGeneration: 0,
              isDetected: false,
            };
          sensor.fireGeneration++;
        }
        sensor.isDetected = detected;
        try {
          localStorage.setItem(
            "smartline.dev.fire-sensor-state",
            JSON.stringify(sensor),
          );
        } catch {}
        await operation(root, () => request("/sensors/fire", "POST", sensor));
      }
      if (action === "random")
        $('[name="barcode"]', root).value = randomBarcode();
      if (action === "automatic") {
        if (intervalTimer) {
          stop();
          return;
        }
        let sent = 0;
        b.setAttribute("aria-checked", "true");
        intervalTimer = setInterval(
          async () => {
            if (inFlight || document.hidden) return;
            await sendProduct(true);
            if (!signal.aborted)
              $("[data-sent]", root).textContent = t("{count} products sent", {
                count: ++sent,
              });
          },
          Number($('[name="autoInterval"]', root).value) * 1000,
        );
      }
    },
    { signal },
  );
  document.addEventListener(
    "visibilitychange",
    () => {
      if (document.hidden) stop();
    },
    { signal },
  );
  signal.addEventListener("abort", stop);
  return update;
}
