import {
  $,
  $$,
  available,
  date,
  escape,
  messages,
  operation,
  panel,
  request,
  state,
  text,
  tr,
} from "./core.js";

export function alertsPanel(root, signal) {
  root.innerHTML = panel("Active alerts", "<div data-alerts></div>" + messages);
  let resolving = null;
  const update = () => {
    const alerts = (state.dashboard?.activeAlerts ?? []).filter(
      (a) => a.alertType !== "FIRE" && a.isActive,
    );
    $("[data-alerts]", root).innerHTML =
      alerts
        .map(
          (a) =>
            `<div class="list-row"><span>${escape(text(a.alertType))}<small class="muted"> · ${date(a.triggeredAt, true)}</small></span><button type="button" class="utility-button" data-resolve="${a.id}" ${resolving === a.id || !available() ? "disabled" : ""}>${tr("Resolve")}</button></div>`,
        )
        .join("") || `<p class="muted">${tr("No active alerts")}</p>`;
  };
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest("[data-resolve]");
      if (!b || !available()) return;
      resolving = Number(b.dataset.resolve);
      update();
      await operation(root, () =>
        request("/alerts/" + resolving + "/resolve", "POST"),
      );
      resolving = null;
      update();
    },
    { signal },
  );
  return update;
}
