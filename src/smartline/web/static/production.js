import {
  $,
  available,
  cameraMarkup,
  cameraPoll,
  date,
  details,
  escape,
  heading,
  language,
  number,
  state,
  t,
  text,
  tr,
} from "./core.js";

export function counters(room) {
  return `<dl class="production-counters">${[
    ["Total", room?.totalCount],
    ["Accepted", room?.acceptedCount],
    ["Rejected", room?.rejectedCount],
    ["Manual review", room?.manualReviewCount],
    ["Yield", room?.yieldPercent],
  ]
    .map(
      ([label, value]) =>
        `<div><dt>${tr(label)}</dt><dd>${number(value)}${label === "Yield" && value != null ? "%" : ""}</dd></div>`,
    )
    .join("")}</dl>`;
}
export function production(root, signal) {
  root.innerHTML = `<div class="production-page">${heading("Production")}<div data-counters></div><div class="monitor-grid">${cameraMarkup()}<section class="result-card"><div data-result></div></section></div><div data-problems class="problem-strip" hidden></div><div class="production-footer" data-environment></div></div>`;
  let lastSignature;
  const update = () => {
    $("[data-counters]", root).innerHTML = counters(state.room);
    const inspection = state.room?.lastInspection;
    const signature = JSON.stringify([inspection, language]);
    if (signature !== lastSignature) {
      lastSignature = signature;
      const result = $("[data-result]", root);
      if (!inspection)
        result.innerHTML = `<h2>${tr("Last inspection")}</h2><div class="empty-result"><span>◇</span><h3>${tr("No inspection yet")}</h3><p>${tr("Waiting for data")}</p></div>`;
      else {
        const tone =
          inspection.decision === "PASS"
            ? "good"
            : inspection.decision === "REJECT"
              ? "bad"
              : "warn";
        result.parentElement.className = `result-card result-${tone}`;
        result.innerHTML = `<div class="section-heading"><h2>${tr("Stored inspection")}</h2>${inspection.simulation ? `<span class="simulation-tag">${tr("SIMULATION")}</span>` : ""}</div><div class="result-decision">${escape(text(inspection.decision))}</div><p class="result-product">${escape(inspection.productClass)}</p><dl class="result-summary"><div><dt>${tr("Confidence")}</dt><dd>${number(inspection.confidence * 100, { maximumFractionDigits: 1 })}%</dd></div><div><dt>${tr("Destination")}</dt><dd>${escape(inspection.destinationBin)}</dd></div><div><dt>${tr("Processed")}</dt><dd>${date(inspection.processedAt)}</dd></div></dl><details class="inline-details"><summary>${tr("Record details")}</summary>${details(inspection)}</details>`;
      }
    }
    const issues = [];
    if (!available()) issues.push(t("Live updates unavailable"));
    const d = state.diagnostics;
    const c = state.dashboard?.conveyorStatus;
    if (c?.isEmergencyStopped) issues.push(t("Emergency latch active"));
    if (c?.isFireDetected) issues.push(t("Fire sensor active"));
    if (c?.isFireGenerationFault)
      issues.push(t("Fire sensor must be reprovisioned"));
    if (d && !["loaded", "mock"].includes(String(d.modelStatus).toLowerCase()))
      issues.push(t("Model unavailable"));
    if (d)
      for (const [key, label] of [
        ["camera", "Camera unavailable"],
        ["vision", "Vision unavailable"],
        ["mqtt", "MQTT unavailable"],
        ["esp32", "Device unavailable"],
      ])
        if (
          [
            "offline",
            "disconnected",
            "stale",
            "unknown",
            "not_configured",
          ].includes(String(d[key]).toLowerCase())
        )
          issues.push(t(label));
    for (const alert of state.dashboard?.activeAlerts ?? [])
      issues.push(text(alert.alertType));
    if (state.room?.simulationActive)
      issues.push(t("Simulation data is visible."));
    const problem = $("[data-problems]", root);
    problem.hidden = !issues.length;
    problem.innerHTML = `<strong>${tr("Review required")}</strong><ul>${issues.map((v) => `<li>${escape(v)}</li>`).join("")}</ul>`;
    $("[data-environment]", root).innerHTML =
      `<span>${tr("Temperature")}<strong>${number(c?.temperature)} °C</strong></span><span>${tr("Humidity")}<strong>${number(c?.humidity)} %</strong></span><span>${tr("Fan")}<strong>${c ? text(c.fanActive) : "—"}</strong></span>`;
  };
  cameraPoll(root, signal);
  return update;
}
