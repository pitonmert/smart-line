import {
  $,
  $$,
  available,
  changed,
  date,
  errorText,
  mock,
  number,
  poll,
  preferences,
  refresh,
  request,
  state,
  t,
  text,
  theme,
  translateDom,
} from "./core.js";
import { production } from "./production.js";
import { historyPage } from "./history.js";
import { management } from "./management.js";

let controller = null,
  updatePage = () => {},
  lastHeartbeat = Date.now(),
  audio = null,
  previousAlertCount = 0;
const aliases = {
  "/inspection": "/management/inspection",
  "/analytics": "/history",
  "/sensors": "/management/sensors",
  "/sorting": "/management/sorting",
  "/recipes": "/management/recipes",
  "/calibration": "/management/calibration",
  "/dataset": "/management/dataset",
  "/diagnostics": "/management/diagnostics",
};
function mount() {
  controller?.abort();
  controller = new AbortController();
  const root = $("#page");
  if (aliases[location.pathname])
    history.replaceState({}, "", aliases[location.pathname]);
  const page = location.pathname;
  root.className =
    page === "/"
      ? ""
      : page.startsWith("/management")
        ? "management-page"
        : "history-page";
  updatePage =
    (page === "/"
      ? production
      : page === "/history"
        ? historyPage
        : management)(root, controller.signal) ?? (() => {});
  $$(".main-nav a").forEach((link) => {
    if (
      link.pathname === (page.startsWith("/management") ? "/management" : page)
    )
      link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  updatePage();
  translateDom();
}
document.addEventListener("click", (e) => {
  const link = e.target.closest("a[href]");
  if (
    !link ||
    e.defaultPrevented ||
    e.ctrlKey ||
    e.metaKey ||
    e.shiftKey ||
    link.target ||
    link.origin !== location.origin ||
    link.hash ||
    link.pathname.startsWith("/api/")
  )
    return;
  if (
    !["/", "/history", "/management", ...Object.keys(aliases)].includes(
      link.pathname,
    ) &&
    !link.pathname.startsWith("/management/")
  )
    return;
  e.preventDefault();
  history.pushState({}, "", link.href);
  mount();
  window.scrollTo(0, 0);
});
window.addEventListener("popstate", mount);
$$("[data-language]").forEach((b) =>
  b.addEventListener("click", () => preferences(b.dataset.language)),
);
$("#theme-toggle").addEventListener("click", () =>
  preferences(undefined, theme === "dark" ? "light" : "dark"),
);
$("#fullscreen").addEventListener("click", async () => {
  try {
    if (document.fullscreenElement) await document.exitFullscreen();
    else await document.documentElement.requestFullscreen();
  } catch {
    $("#connection-banner").hidden = false;
    $("#connection-banner").textContent = t("Full screen is unavailable.");
  }
});
document.addEventListener("fullscreenchange", () => {
  $("#fullscreen").dataset.i18n = document.fullscreenElement
    ? "Exit full screen"
    : "Full screen";
  translateDom();
});
function alarm() {
  try {
    audio ??= new AudioContext();
    audio.resume().then(() => {
      const oscillator = audio.createOscillator(),
        gain = audio.createGain();
      oscillator.frequency.value = 880;
      oscillator.type = "square";
      gain.gain.value = 0.15;
      oscillator.connect(gain).connect(audio.destination);
      oscillator.start();
      oscillator.stop(audio.currentTime + 0.3);
    });
  } catch {}
}
$("#resolve-fire").addEventListener("click", async () => {
  if (
    !available() ||
    state.dashboard?.conveyorStatus?.isFireDetected ||
    !location.pathname.startsWith("/management")
  )
    return;
  const button = $("#resolve-fire");
  button.disabled = true;
  try {
    for (const alert of state.dashboard?.activeAlerts ?? [])
      if (alert.alertType === "FIRE" && alert.isActive)
        await request("/alerts/" + alert.id + "/resolve", "POST");
    await refresh("dashboard", "/dashboard");
    $("#fire-error").hidden = true;
  } catch (error) {
    $("#fire-error").textContent = errorText(error);
    $("#fire-error").hidden = false;
  } finally {
    button.disabled = !available();
  }
});
function update() {
  const c = state.dashboard?.conveyorStatus;
  $("#line-state").textContent = t(
    !c
      ? "Line status unknown"
      : c.isEmergencyStopped
        ? "Emergency latch active"
        : c.isRunning
          ? "Line running"
          : "Line stopped",
  );
  $("#active-recipe").textContent =
    t("Recipe") + ": " + (state.room?.activeRecipe ?? "—");
  $("#connection").textContent = available()
    ? t("Live")
    : text(state.connection === "connected" ? "Unavailable" : state.connection);
  const banner = $("#connection-banner");
  banner.hidden = available();
  banner.textContent = t(
    "Connection lost. Showing the last known values. Controls are unavailable.",
  );
  const fire =
    state.dashboard?.activeAlerts?.some(
      (a) => a.alertType === "FIRE" && a.isActive,
    ) === true;
  $("#fire-overlay").hidden = !fire;
  $("#fire-message").textContent = t(
    c?.isFireDetected
      ? "The fire sensor is active; the emergency latch cannot be reset."
      : "The sensor is clear. The alarm can be resolved.",
  );
  $("#resolve-fire").hidden =
    !location.pathname.startsWith("/management") || !c || c.isFireDetected;
  $("#resolve-fire").disabled = !available();
  const alerts = state.dashboard?.activeAlerts ?? [];
  if (
    alerts.length > previousAlertCount &&
    alerts.some(
      (a) => a.alertType === "FIRE" || a.alertType === "EMERGENCY_STOP",
    )
  )
    alarm();
  previousAlertCount = alerts.length;
  updatePage();
  translateDom();
}
window.addEventListener("smartline-state", update);
window.addEventListener("smartline-preferences", update);
preferences();
mount();
setInterval(() => {
  $("#clock").textContent = date(new Date().toISOString(), true);
  if (state.connection === "connected" && Date.now() - lastHeartbeat > 30000) {
    state.connection = "disconnected";
    changed();
  }
}, 1000);
poll(() => refresh("dashboard", "/dashboard"), 5000);
poll(() => refresh("room", "/control-room"), 3000);
poll(() => refresh("diagnostics", "/diagnostics"), 5000);
poll(() => refresh("recipes", "/recipes"), 15000);
const source = new EventSource("/api/events");
source.addEventListener("ready", () => {
  lastHeartbeat = Date.now();
  state.connection = "connected";
  changed();
  Promise.all([
    refresh("dashboard", "/dashboard"),
    refresh("room", "/control-room"),
    refresh("diagnostics", "/diagnostics"),
  ]);
});
source.addEventListener("heartbeat", () => {
  lastHeartbeat = Date.now();
});
source.onerror = () => {
  state.connection = "reconnecting";
  changed();
};
const on = (name, fn) =>
  source.addEventListener(name, (e) => {
    lastHeartbeat = Date.now();
    try {
      fn(JSON.parse(e.data));
      changed();
    } catch (error) {
      console.error(error);
    }
  });
on("ReceiveConveyorUpdate", (value) => {
  if (state.dashboard) state.dashboard.conveyorStatus = value;
});
on("ReceiveEnvironmentUpdate", (value) => {
  if (state.dashboard?.conveyorStatus)
    Object.assign(state.dashboard.conveyorStatus, value);
});
on("ReceiveProductUpdate", (value) => {
  if (state.dashboard)
    state.dashboard.recentProducts = [
      value,
      ...state.dashboard.recentProducts.filter((p) => p.id !== value.id),
    ].slice(0, 5);
  const now = new Date(),
    key = `${now.getHours()}:${String(now.getMinutes()).padStart(2, "0")}`;
  let count = state.liveProductCounts.at(-1);
  if (count?.time !== key) {
    count = { time: key, ok: 0, defective: 0 };
    state.liveProductCounts.push(count);
    if (state.liveProductCounts.length > 10) state.liveProductCounts.shift();
  }
  if (value.status === "OK") count.ok++;
  else count.defective++;
});
on("ReceiveAlertUpdate", (value) => {
  if (state.dashboard)
    state.dashboard.activeAlerts = [
      ...(value.isActive ? [value] : []),
      ...state.dashboard.activeAlerts.filter((a) => a.id !== value.id),
    ];
});
on("ReceiveLogUpdate", (value) => {
  if (value.eventType !== "PRODUCT_PASSED")
    state.recentLogs = [value, ...state.recentLogs].slice(0, 10);
});
on("ReceiveInspectionUpdate", () => refresh("room", "/control-room"));
on("ReceiveTrackingUpdate", () => refresh("room", "/control-room"));
window.addEventListener("pagehide", () => {
  controller?.abort();
  source.close();
});
