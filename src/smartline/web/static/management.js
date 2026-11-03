import {
  $,
  $$,
  available,
  button,
  cameraMarkup,
  cameraPoll,
  controlsAvailable,
  date,
  details,
  errorText,
  escape,
  feedback,
  heading,
  input,
  messages,
  number,
  operation,
  panel,
  poll,
  request,
  select,
  state,
  t,
  text,
  tr,
} from "./core.js";
import { datasetPage } from "./dataset.js";
import { simulationPage, inspectionSimulator } from "./simulation.js";
import { alertsPanel } from "./alerts.js";

export const groups = [
  {
    label: "Line control",
    description: "Operate the conveyor and manage its stop latch.",
    links: [["control", "Conveyor controls"]],
  },
  {
    label: "Settings & maintenance",
    description: "Configure recipes and calibration; inspect device health.",
    links: [
      ["recipes", "Product Recipes"],
      ["calibration", "Calibration"],
      ["sensors", "Sensors"],
      ["diagnostics", "System Diagnostics"],
    ],
  },
  {
    label: "Tools",
    description:
      "Manual checks, dataset collection and simulation, outside the production screen.",
    links: [
      ["inspection", "Manual inspection"],
      ["sorting", "Logical sorting"],
      ["dataset", "Dataset collection"],
      ["simulation", "Simulation tools"],
    ],
  },
];
export function management(root, signal) {
  const route = location.pathname.split("/")[2];
  if (!route) {
    const render = () => {
      root.innerHTML = `${heading("Management")}<div class="management-grid">${groups.map((g, i) => `<section class="management-group"><span class="group-number">0${i + 1}</span><h2>${tr(g.label)}</h2><p>${tr(g.description)}</p><ul>${g.links.map(([p, l]) => `<li><a href="/management/${p}">${tr(l)}<span aria-hidden="true">→</span></a></li>`).join("")}</ul></section>`).join("")}</div>`;
    };
    render();
    return render;
  }
  const updater = {
    control: lineControl,
    recipes: recipePage,
    calibration: calibrationPage,
    sensors: sensorsPage,
    diagnostics: diagnosticsPage,
    inspection: inspectionPage,
    sorting: sortingPage,
    dataset: datasetPage,
    simulation: simulationPage,
  }[route](root, signal);
  if (route === "control" || route === "diagnostics") {
    const holder = document.createElement("div");
    holder.className = "alerts-section";
    root.append(holder);
    const updateAlerts = alertsPanel(holder, signal);
    return () => {
      updater();
      updateAlerts();
    };
  }
  return updater;
}
function lineControl(root, signal) {
  root.innerHTML = `${heading("Conveyor controls")}<div class="control-layout">${panel("Line control", `<dl class="detail-list" data-line-details></dl><div class="toolbar">${button("Start", "start", "data-control")}${button("Stop", "stop", "data-control")}${button("Emergency stop", "emergency-stop", 'data-control class="danger-button"')}${button("Reset emergency stop", "emergency-reset", "data-control")}</div>${messages}`)}${panel("Speed", `<form class="stack">${input("speed", "Speed", "range", 50, 'min="0" max="100" step="1" data-control')}<output data-speed>50%</output><button type="submit" class="primary-button" data-control>${tr("Apply speed")}</button></form><p class="muted">${tr("Stop the conveyor before changing its speed.")}</p>`)}</div>`;
  let speedInitialized = false;
  const update = () => {
    const s = state.dashboard?.conveyorStatus;
    $("[data-line-details]", root).innerHTML = [
      [
        "State",
        s
          ? text(
              s.isEmergencyStopped
                ? "Emergency latch active"
                : s.isRunning
                  ? "Running"
                  : "Stopped",
            )
          : "—",
      ],
      ["Speed", s ? number(s.speed) + "%" : "—"],
      ["Fire", s ? text(s.isFireDetected) : "—"],
      ["Temperature", s ? number(s.temperature) + " °C" : "—"],
    ]
      .map(([k, v]) => `<div><dt>${tr(k)}</dt><dd>${escape(v)}</dd></div>`)
      .join("");
    $$("[data-control]", root).forEach(
      (el) => (el.disabled = !controlsAvailable()),
    );
    if (s) {
      $('[data-action="start"]', root).disabled =
        !controlsAvailable() ||
        s.isEmergencyStopped ||
        s.isFireDetected ||
        s.isFireGenerationFault;
      $('[data-action="emergency-reset"]', root).disabled =
        !controlsAvailable() || s.isFireDetected || s.isFireGenerationFault;
      $('[name="speed"]', root).disabled =
        !controlsAvailable() ||
        s.isRunning ||
        s.isEmergencyStopped ||
        s.isFireDetected;
      $('button[type="submit"]', root).disabled = $(
        '[name="speed"]',
        root,
      ).disabled;
      if (!speedInitialized) {
        $('[name="speed"]', root).value = s.speed;
        $("[data-speed]", root).textContent = number(s.speed) + "%";
        speedInitialized = true;
      }
    }
  };
  $('[name="speed"]', root).addEventListener(
    "input",
    (e) =>
      ($("[data-speed]", root).textContent =
        number(Number(e.target.value)) + "%"),
  );
  $("form", root).addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!controlsAvailable()) return;
    await operation(root, () =>
      request("/conveyor/speed", "POST", {
        speed: Number($('[name="speed"]', root).value),
      }),
    );
  });
  root.addEventListener(
    "click",
    async (e) => {
      const action = e.target.closest("[data-action]")?.dataset.action;
      if (!action || !controlsAvailable()) return;
      if (
        ["emergency-stop", "emergency-reset"].includes(action) &&
        !confirm(
          t(
            action === "emergency-stop"
              ? "Activate emergency stop?"
              : "Reset the emergency-stop latch? The conveyor will remain stopped.",
          ),
        )
      )
        return;
      await operation(root, () => request("/conveyor/" + action, "POST"));
    },
    { signal },
  );
  return update;
}
function recipePage(root, signal) {
  let selected = null,
    signature;
  root.innerHTML = `${heading("Product Recipes")}<div class="recipe-layout"><section class="surface-panel"><h2>${tr("Recipes")}</h2><ul data-recipes></ul></section><div data-definition class="stack"></div></div>${messages}`;
  const update = () => {
    const list = state.recipes ?? [];
    const item =
      list.find((r) => r.id === selected) ??
      list.find((r) => r.isActive) ??
      list[0];
    const next = JSON.stringify([
      list,
      selected,
      document.documentElement.lang,
    ]);
    if (signature !== next) {
      signature = next;
      $("[data-recipes]", root).innerHTML = list
        .map(
          (r) =>
            `<li><button class="text-link" type="button" data-recipe="${escape(r.id)}">${escape(r.name)} ${r.isActive ? tr("Active") : ""}</button></li>`,
        )
        .join("");
      $("[data-definition]", root).innerHTML = item
        ? `${panel({ raw: item.name }, `<p>${escape(item.description)}</p><p class="muted">${tr("Version")} ${number(item.version)}</p>`, item.isActive ? "" : button("Activate", "activate", `data-id="${escape(item.id)}"`))}<div class="two-column">${[
            ["Classes", "productClasses"],
            ["Colors", "allowedColors"],
            ["Sizes", "sizeClasses"],
            ["Defects", "defectClasses"],
          ]
            .map(([title, key]) =>
              panel(
                title,
                `<ul>${item.definition[key].map((d) => `<li>${escape(d.code)} · ${escape(d.expectedColor ?? (d.hueMin !== undefined ? number(d.hueMin) + "–" + number(d.hueMax) : d.minWidthMm !== undefined ? number(d.minWidthMm) + "–" + number(d.maxWidthMm) + " mm" : d.rejects ? t("Rejects") : t("Holds")))}</li>`).join("")}</ul>`,
              ),
            )
            .join("")}</div>${panel(
            "Sorting rules",
            `<ul>${item.definition.sortingRules
              .map(
                (r) =>
                  `<li>${number(r.priority)}. ${escape(r.id)} → ${escape(r.destinationBin)} ${tr("when")} ${
                    Object.values(r.when)
                      .filter(Boolean)
                      .map((v) => escape(text(v)))
                      .join(" + ") || t("Any")
                  }</li>`,
              )
              .join("")}</ul>`,
          )}${panel(
            "Model and camera",
            `<dl class="detail-list">${[
              [
                "Model",
                `${item.definition.model.family} ${item.definition.model.name}`,
              ],
              ["Version", item.definition.model.version ?? "—"],
              [
                "Artifact:",
                item.definition.model.artifactFileName ?? t("Not registered"),
              ],
              [
                "Camera",
                `${item.definition.cameraProfile.sensor ?? ""} ${item.definition.cameraProfile.pixelFormat ?? ""} ${item.definition.cameraProfile.transport ?? ""}`,
              ],
              [
                "Source",
                `${item.definition.cameraProfile.sourceWidth ?? "—"}×${item.definition.cameraProfile.sourceHeight ?? "—"}`,
              ],
            ]
              .map(
                ([k, v]) => `<div><dt>${tr(k)}</dt><dd>${escape(v)}</dd></div>`,
              )
              .join("")}</dl>`,
          )}`
        : `<p class="empty-state">${tr("Waiting for data")}</p>`;
    }
    $$('[data-action="activate"]', root).forEach(
      (b) => (b.disabled = !available()),
    );
    if (state.errors.recipes) feedback(root, state.errors.recipes);
  };
  root.addEventListener(
    "click",
    async (e) => {
      const choose = e.target.closest("[data-recipe]");
      if (choose) {
        selected = choose.dataset.recipe;
        update();
      }
      const activate = e.target.closest('[data-action="activate"]');
      if (activate && available())
        await operation(root, async () => {
          await request(
            "/recipes/" + activate.dataset.id + "/activate",
            "POST",
          );
          state.recipes = await request("/recipes");
          update();
        });
    },
    { signal },
  );
  return update;
}
function calibrationPage(root, signal) {
  let profiles = [];
  root.innerHTML = `${heading("Calibration")}<p class="tool-note">${tr("Scale applies only to the specified still image dimensions. Orientation, flip and perspective are reserved settings; they do not transform images in v0.2.0.")}</p>${panel("Profiles", "<ul data-profiles></ul>")}${panel("New profile", `<form class="form-grid">${input("name", "Name", "text", "", "required")}${input("description", "Description")}${select("orientation", "Orientation", ["0", "90", "180", "270"])}${input("sourceWidth", "Calibration image width px", "number", "", 'min="1" max="10000"')}${input("sourceHeight", "Calibration image height px", "number", "", 'min="1" max="10000"')}${input("pixels", "Pixels per mm", "number", "", 'step="0.01"')}${input("speed", "Belt mm/s", "number", "", 'step="1"')}${input("distance", "Inspection to sorter mm", "number", "", 'step="1"')}${input("red", "White reference R", "number", "", 'min="0" max="255"')}${input("green", "G", "number", "", 'min="0" max="255"')}${input("blue", "B", "number", "", 'min="0" max="255"')}<label><input type="checkbox" name="flipHorizontal" disabled>${tr("Flip horizontal")}</label><label><input type="checkbox" name="flipVertical" disabled>${tr("Flip vertical")}</label>${input("notes", "Notes")}<button type="submit" class="primary-button" data-write>${tr("Save profile")}</button></form>${messages}`)}`;
  $('[name="orientation"]', root).disabled = true;
  const update = () => {
    $("[data-profiles]", root).innerHTML =
      profiles
        .map(
          (p) =>
            `<li class="list-row"><span>${escape(p.name)} v${number(p.version)} ${p.isActive ? tr("Active") : ""} ${p.simulationDataPresent ? tr("SIMULATION") : ""}</span>${p.isActive ? "" : button("Activate", "activate", `data-id="${escape(p.id)}" data-write`)}</li>`,
        )
        .join("") || `<li>${tr("No calibration profile is stored.")}</li>`;
    $$("[data-write]", root).forEach((b) => (b.disabled = !available()));
  };
  const load = async () => {
    try {
      profiles = await request(
        "/calibration-profiles",
        "GET",
        undefined,
        signal,
      );
      if (!signal.aborted) update();
    } catch (e) {
      if (!signal.aborted) feedback(root, e);
    }
  };
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest('[data-action="activate"]');
      if (b && available())
        await operation(root, async () => {
          await request(
            "/calibration-profiles/" + b.dataset.id + "/activate",
            "POST",
          );
          await load();
        });
    },
    { signal },
  );
  $("form", root).addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!available()) return;
    const f = new FormData(e.target);
    const optional = (k) =>
      String(f.get(k) ?? "").trim() === "" ? null : Number(f.get(k));
    const red = optional("red");
    const definition = {
      sourceWidth: optional("sourceWidth"),
      sourceHeight: optional("sourceHeight"),
      pixelsPerMillimeter: optional("pixels"),
      orientationDegrees: 0,
      flipHorizontal: false,
      flipVertical: false,
      colorReference:
        red == null
          ? null
          : { red, green: optional("green") ?? 0, blue: optional("blue") ?? 0 },
      beltSpeedMmPerSecond: optional("speed"),
      inspectionToSorterDistanceMm: optional("distance"),
      cameraRoi: null,
      inspectionZone: null,
      perspectiveReference: null,
      gateMappings: [
        "BIN_A",
        "BIN_B",
        "BIN_C",
        "REJECT_BIN",
        "MANUAL_REVIEW",
      ].map((binCode) => ({ binCode, servoDegrees: 0, enabled: false })),
      tofBaseline: { source: "NOT_INTEGRATED" },
      hardwareActuationEnabled: false,
      hardwareActuationConfirmed: false,
      notes: String(f.get("notes") || ""),
    };
    const result = await operation(
      root,
      () =>
        request("/calibration-profiles", "POST", {
          name: String(f.get("name")),
          description: String(f.get("description") || ""),
          definition,
        }),
      "Calibration profile stored. Gates remain disabled and no VL53L1X reading was saved.",
    );
    if (result) {
      e.target.reset();
      await load();
    }
  });
  poll(load, 8000, signal);
  return update;
}
function sensorsPage(root) {
  root.innerHTML = `${heading("Sensors")}<div data-environment></div>${panel("Time of flight", `<p>${tr("Source:")} <strong data-tof></strong></p><p class="muted">${tr("VL53L1X is not integrated. productPresent, distanceMm, triggerTimestamp, and sensorHealthy are not read from hardware. No distance is displayed as a measurement.")}</p>`)}`;
  return () => {
    const c = state.dashboard?.conveyorStatus;
    $("[data-environment]", root).innerHTML = `<div class="metric-grid">${[
      ["Temperature", c ? number(c.temperature) + " °C" : "—"],
      ["Humidity", c ? number(c.humidity) + " %" : "—"],
      ["Fan", c ? text(c.fanActive) : "—"],
      ["Fire", c ? text(c.isFireDetected) : "—"],
    ]
      .map(
        ([k, v]) =>
          `<section class="surface-panel"><h2>${tr(k)}</h2><strong>${escape(v)}</strong></section>`,
      )
      .join("")}</div>`;
    $("[data-tof]", root).textContent = text(
      state.diagnostics?.timeOfFlightSource ?? "NOT_INTEGRATED",
    );
  };
}
function diagnosticsPage(root) {
  const statusFields = new Set([
    "api",
    "database",
    "mqtt",
    "vision",
    "camera",
    "esp32",
    "modelStatus",
    "status",
    "signalR",
    "liveUpdates",
    "environment",
    "timeOfFlightSource",
  ]);
  const diagnosticValue = (key, value) =>
    statusFields.has(key) || typeof value === "boolean"
      ? text(value)
      : (value ?? "—");
  root.innerHTML = `${heading("System Diagnostics")}<div data-diagnostics class="stack"></div>`;
  return () => {
    const d = state.diagnostics;
    const holder = $("[data-diagnostics]", root);
    if (!d) {
      holder.innerHTML = `<p>${tr("Waiting for data")}</p>`;
      return;
    }
    holder.innerHTML = `<div class="metric-grid">${[
      ["API", d.api],
      ["Database", d.database],
      ["MQTT", d.mqtt],
      ["Vision", d.vision],
      ["Camera", d.camera],
      ["ESP32", d.esp32],
      ["Live updates", d.liveUpdates ?? d.signalR],
    ]
      .map(
        ([k, v]) =>
          `<section class="surface-panel"><h2>${tr(k)}</h2><strong>${escape(text(v))}</strong></section>`,
      )
      .join("")}</div>${panel(
      "Model and camera",
      `<dl class="detail-list">${Object.entries(d)
        .filter(
          ([k, v]) =>
            !["model", "simulation"].includes(k) && typeof v !== "object",
        )
        .map(
          ([k, v]) =>
            `<div><dt>${escape(k)}</dt><dd>${escape(diagnosticValue(k, v))}</dd></div>`,
        )
        .join("")}${Object.entries(d.model ?? {})
        .map(
          ([k, v]) =>
            `<div><dt>model.${escape(k)}</dt><dd>${escape(diagnosticValue(k, v))}</dd></div>`,
        )
        .join("")}</dl>`,
    )}${panel("Simulation tools", `<pre>${escape(JSON.stringify(d.simulation, null, 2))}</pre>`)}`;
  };
}
function inspectionPage(root, signal) {
  let requestId = null,
    busy = false,
    last;
  root.innerHTML = `${heading("Manual inspection")}<p class="tool-note">${tr("This records an operator observation through the API. It does not claim a model produced the class or a box.")}</p><div class="two-column">${cameraMarkup()}${panel("Last inspection", "<div data-last-inspection></div>")}</div><div class="toolbar">${button("Inspect camera with Vision", "inspect", "data-write")}</div>${panel("Manual inspection trigger", `<form class="form-grid">${input("productClass", "Product class", "text", "", "required")}${input("colorClass", "Color class", "text", "", "required")}${input("sizeClass", "Size class", "text", "", "required")}${input("defects", "Defects, comma separated")}${input("confidence", "Confidence", "number", 0.8, 'required min="0" max="1" step="0.01"')}<button type="submit" class="primary-button" data-write>${tr("Record inspection")}</button></form>${messages}`)}`;
  const update = () => {
    $$("[data-write]", root).forEach(
      (el) => (el.disabled = busy || !available()),
    );
    const i = state.room?.lastInspection,
      signature = JSON.stringify([i, document.documentElement.lang]);
    if (last !== signature) {
      last = signature;
      $("[data-last-inspection]", root).innerHTML = i
        ? `<p>${escape(i.productClass)} · ${escape(text(i.decision))} · ${escape(i.destinationBin)}</p>${details(i)}`
        : `<p>${tr("No inspection has been stored.")}</p>`;
    }
  };
  $("form", root).addEventListener("submit", async (e) => {
    e.preventDefault();
    if (busy || !available()) return;
    busy = true;
    update();
    const f = new FormData(e.target);
    await operation(root, () =>
      request("/inspections", "POST", {
        productClass: f.get("productClass"),
        colorClass: f.get("colorClass"),
        sizeClass: f.get("sizeClass"),
        defectClasses: String(f.get("defects") || "")
          .split(",")
          .map((v) => v.trim())
          .filter(Boolean),
        confidence: Number(f.get("confidence")),
        triggerSource: "api",
        simulation: false,
        boundingBoxes: [],
      }),
    );
    busy = false;
    update();
  });
  $('[data-action="inspect"]', root).addEventListener("click", async () => {
    if (busy || !available()) return;
    busy = true;
    update();
    requestId ??= crypto.randomUUID();
    const result = await operation(root, () =>
      request("/inspections/vision", "POST", { requestId }),
    );
    if (result) requestId = null;
    busy = false;
    update();
  });
  cameraPoll(root, signal);
  return update;
}
function sortingPage(root, signal) {
  let queue = [];
  root.innerHTML = `${heading("Logical sorting")}${panel("Actuation", `<p class="tool-note">${tr("Logical destinations stay separate from servo motion. The release action records SORTED and does not publish a motion command.")}</p>`)}${panel("Sorting queue", "<div data-queue></div>" + messages)}<div data-simulator></div>`;
  const simulator = inspectionSimulator($("[data-simulator]", root), signal);
  const update = () => {
    $("[data-queue]", root).innerHTML = queue.length
      ? `<div class="table-scroll"><table class="history-table"><thead><tr>${["Product", "Decision", "Destination", "Due", "Release"].map((v) => `<th>${tr(v)}</th>`).join("")}</tr></thead><tbody>${queue.map((q) => `<tr><td>${escape(q.publicCode)} ${q.simulation ? tr("SIMULATION") : ""}</td><td>${escape(text(q.decision))}</td><td>${escape(q.destinationBin)}</td><td>${date(q.sortDueAt)}</td><td>${button("Release", "release", `data-id="${escape(q.productId)}" data-code="${escape(q.publicCode)}"`)}</td></tr>`).join("")}</tbody></table></div>`
      : `<p>${tr("The queue is empty.")}</p>`;
    $$('[data-action="release"]', root).forEach(
      (b) => (b.disabled = !available()),
    );
    simulator();
  };
  const load = async () => {
    try {
      queue = await request("/tracking/queue", "GET", undefined, signal);
      if (!signal.aborted) update();
    } catch (e) {
      if (!signal.aborted) feedback(root, e);
    }
  };
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest('[data-action="release"]');
      if (!b || !available()) return;
      if (
        !confirm(
          t(
            "Record a logical sort for {code}? No servo command will be sent.",
            { code: b.dataset.code },
          ),
        )
      )
        return;
      await operation(root, async () => {
        const result = await request(
          "/tracking/" + b.dataset.id + "/release",
          "POST",
        );
        await load();
        return result;
      });
    },
    { signal },
  );
  poll(load, 3000, signal);
  return update;
}
