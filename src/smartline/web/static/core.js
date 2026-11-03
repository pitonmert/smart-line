const catalogs = await fetch("/static/catalog.json").then((r) => r.json());
const errorMessages = await fetch("/static/errors.json").then((r) => r.json());
const statuses = {
  high_temperature: "High temperature",
  fire: "Fire alarm",
  emergency_stop: "Emergency stop",
  no_barcode_detected: "Barcode unreadable",
  healthy: "Healthy",
  connected: "Connected",
  online: "Online",
  offline: "Offline",
  unknown: "Unknown",
  not_configured: "Not configured",
  not_loaded: "Not loaded",
  not_integrated: "Not integrated",
  checking: "Checking",
  connecting: "Connecting…",
  reconnecting: "Reconnecting",
  disconnected: "Disconnected",
  registered: "Registered",
  stale: "Stale",
  active: "Active",
  stopped: "Stopped",
  queued: "Queued",
  sorted: "Sorted",
  failed: "Failed",
  manual: "Manual",
  interval: "Interval",
  model: "Model observation",
  simulation: "SIMULATION",
  manual_review: "Manual review",
  enabled: "Enabled",
  disabled: "Disabled",
  true: "Yes",
  false: "No",
  none: "None",
  available: "Available",
  unavailable: "Unavailable",
  mock: "Mock",
  preview: "Preview",
  inspection: "Inspection",
  dataset: "Dataset",
};
function read(key, fallback, allowed) {
  try {
    const v = localStorage.getItem(key);
    return allowed.includes(v) ? v : fallback;
  } catch {
    return fallback;
  }
}
export let language = read("smartline.language", "en", ["en", "tr"]);
export let theme = read("smartline.theme", "dark", ["dark", "light"]);
export const locale = () => (language === "tr" ? "tr-TR" : "en-US");
export const number = (value, options = {}) =>
  value == null ? "—" : new Intl.NumberFormat(locale(), options).format(value);
export const date = (value, timeOnly = false) =>
  !value
    ? "—"
    : Number.isNaN(new Date(value).getTime())
      ? String(value)
      : new Intl.DateTimeFormat(
          locale(),
          timeOnly
            ? { timeStyle: "medium" }
            : { dateStyle: "short", timeStyle: "medium" },
        ).format(new Date(value));
export const t = (key, params = {}) =>
  (catalogs[language]?.[key] ?? key).replace(/\{(\w+)\}/g, (m, k) =>
    params[k] === undefined
      ? m
      : typeof params[k] === "number"
        ? number(params[k])
        : params[k],
  );
export const text = (value) =>
  value == null
    ? "—"
    : typeof value === "object" && value.raw !== undefined
      ? value.raw
      : t(statuses[String(value).toLowerCase()] ?? String(value));
export const escape = (value) =>
  String(value ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
export const tr = (key) =>
  `<span data-i18n="${escape(key)}">${escape(t(key))}</span>`;
export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [
  ...root.querySelectorAll(selector),
];
export function translateDom(root = document) {
  $$("[data-i18n]", root).forEach(
    (el) => (el.textContent = t(el.dataset.i18n)),
  );
  $$("[data-i18n-placeholder]", root).forEach(
    (el) => (el.placeholder = t(el.dataset.i18nPlaceholder)),
  );
  $$("[data-i18n-label]", root).forEach((el) =>
    el.setAttribute("aria-label", t(el.dataset.i18nLabel)),
  );
  $$("[data-error-code]", root).forEach((el) => {
    el.textContent = text(
      errorMessages[el.dataset.errorCode] ?? { raw: el.dataset.errorMessage },
    );
  });
  $$("[data-notice-key]", root).forEach((el) => {
    el.textContent = t(el.dataset.noticeKey);
  });
}
export function preferences(nextLanguage = language, nextTheme = theme) {
  language = nextLanguage;
  theme = nextTheme;
  try {
    localStorage.setItem("smartline.language", language);
    localStorage.setItem("smartline.theme", theme);
  } catch {}
  document.documentElement.lang = language;
  document.documentElement.dataset.theme = theme;
  document.title = t("Dashboard title");
  $$("[data-language]").forEach((b) =>
    b.setAttribute("aria-pressed", String(b.dataset.language === language)),
  );
  const toggle = $("#theme-toggle");
  toggle.dataset.i18n = theme === "dark" ? "Light" : "Dark";
  translateDom();
  window.dispatchEvent(new Event("smartline-preferences"));
}
export class ApiError extends Error {
  constructor(message, code, status) {
    super(message);
    this.code = code;
    this.status = status;
  }
}
export function errorText(error) {
  return error instanceof ApiError
    ? text(errorMessages[error.code] ?? { raw: error.message })
    : (error?.message ?? t("Request failed."));
}
export async function request(path, method = "GET", payload, signal) {
  const response = await fetch("/api" + path, {
    method,
    signal,
    headers:
      payload === undefined ? {} : { "Content-Type": "application/json" },
    body: payload === undefined ? undefined : JSON.stringify(payload),
  });
  let envelope;
  try {
    envelope = await response.json();
  } catch {
    throw new ApiError(
      "The API returned an invalid response.",
      `HTTP_${response.status}`,
      response.status,
    );
  }
  if (!response.ok || !envelope.isSuccess)
    throw new ApiError(
      envelope.message || "Request failed.",
      envelope.code || `HTTP_${response.status}`,
      response.status,
    );
  return envelope.data;
}
export function panel(title, body, action = "") {
  return `<section class="surface-panel"><div class="section-heading"><h2>${typeof title === "object" ? escape(title.raw) : tr(title)}</h2>${action}</div>${body}</section>`;
}
export function heading(title) {
  return `<div class="page-heading"><div><p class="eyebrow">SmartLine</p><h1>${tr(title)}</h1></div>${location.pathname.startsWith("/management/") ? `<a href="/management" class="text-link">${tr("Management")}</a>` : ""}</div>`;
}
export function input(name, label, type = "text", value = "", extra = "") {
  return `<label>${tr(label)}<input class="form-field" name="${name}" type="${type}" value="${escape(value)}" aria-label="${escape(t(label))}" data-i18n-label="${escape(label)}" ${extra}></label>`;
}
export function select(name, label, options, value = "") {
  return `<label>${tr(label)}<select class="form-field" name="${name}" aria-label="${escape(t(label))}" data-i18n-label="${escape(label)}">${options
    .map((o) => {
      const optionLabel = typeof o === "string" ? text(o) : o.label;
      const key = Object.keys(catalogs.en).find(
        (k) => catalogs.en[k] === optionLabel || catalogs.tr[k] === optionLabel,
      );
      return `<option value="${escape(typeof o === "string" ? o : o.value)}" ${value === (typeof o === "string" ? o : o.value) ? "selected" : ""} ${key ? `data-i18n="${escape(key)}"` : ""}>${escape(optionLabel)}</option>`;
    })
    .join("")}</select></label>`;
}
export const button = (label, action, extra = "") =>
  `<button type="button" class="utility-button" data-action="${action}" ${extra}>${tr(label)}</button>`;
export const details = (i) =>
  `<dl class="detail-list">${[
    ["Color", text(i.colorClass)],
    ["Size", text(i.sizeClass)],
    ["Defects", (i.defectClasses ?? []).map(text).join(", ") || t("None")],
    ["Quality", text(i.qualityStatus)],
    [
      "Confidence",
      number(i.confidence * 100, { maximumFractionDigits: 1 }) + "%",
    ],
    ["Reasons", (i.reasons ?? []).join(", ") || "—"],
    ["Model", [i.modelName, i.modelVersion].filter(Boolean).join(" · ") || "—"],
    ["Recipe", i.recipeVersion == null ? "—" : "v" + i.recipeVersion],
    [
      "Latency",
      i.processingLatencyMs == null
        ? "—"
        : number(i.processingLatencyMs) + " ms",
    ],
    [
      "Source",
      i.simulation
        ? t("SIMULATION")
        : i.captureSource || i.cameraSource || t("Live observation"),
    ],
    ["Visual size", `${number(i.visualWidth)} × ${number(i.visualHeight)} px`],
    [
      "Physical estimate",
      `${number(i.estimatedPhysicalDimensions?.widthMm)} × ${number(i.estimatedPhysicalDimensions?.heightMm)} mm`,
    ],
    ["Captured", date(i.capturedAt)],
    ["Processed", date(i.processedAt)],
    ["ID", i.inspectionId],
  ]
    .map(([k, v]) => `<div><dt>${tr(k)}</dt><dd>${escape(v)}</dd></div>`)
    .join("")}</dl>`;
export const state = {
  dashboard: null,
  room: null,
  diagnostics: null,
  recipes: null,
  connection: "connecting",
  errors: {},
  updated: {},
  liveProductCounts: [],
  recentLogs: [],
};
export const mock = document.body.dataset.mock === "true";
export const available = () =>
  state.connection === "connected" &&
  !state.errors.room &&
  !state.errors.diagnostics &&
  !!state.updated.room &&
  !!state.updated.diagnostics &&
  Date.now() - state.updated.room < 30000 &&
  Date.now() - state.updated.diagnostics < 30000;
export const controlsAvailable = () =>
  available() &&
  state.diagnostics?.esp32?.toLowerCase() === "online" &&
  state.diagnostics?.mqtt?.toLowerCase() === "connected" &&
  !!state.dashboard?.conveyorStatus;
export function changed() {
  window.dispatchEvent(new Event("smartline-state"));
}
export async function refresh(name, path) {
  try {
    state[name] = await request(path);
    state.errors[name] = null;
    state.updated[name] = Date.now();
  } catch (e) {
    state.errors[name] = e;
  }
  changed();
}
export function poll(fn, ms, signal) {
  let timer;
  const run = async () => {
    if (signal?.aborted) return;
    try {
      await fn();
    } catch (e) {
      if (e.name !== "AbortError") console.error(e);
    } finally {
      if (!signal?.aborted) timer = setTimeout(run, ms);
    }
  };
  run();
  signal?.addEventListener("abort", () => clearTimeout(timer), { once: true });
}
export function feedback(root, error = "", notice = "") {
  const e = $("[data-error]", root);
  if (e) {
    e.hidden = !error;
    if (error instanceof ApiError) {
      e.dataset.errorCode = error.code;
      e.dataset.errorMessage = error.message;
      e.textContent = errorText(error);
    } else {
      delete e.dataset.errorCode;
      delete e.dataset.errorMessage;
      e.textContent = typeof error === "string" ? error : errorText(error);
    }
  }
  const n = $("[data-notice]", root);
  if (n) {
    n.hidden = !notice;
    n.dataset.noticeKey = notice;
    n.textContent = t(notice);
  }
}
export const messages =
  '<p data-error role="alert" class="error-note" hidden></p><p data-notice role="status" hidden></p>';
export async function operation(root, fn, successKey = "Operation completed.") {
  feedback(root);
  try {
    const result = await fn();
    feedback(root, "", successKey);
    await Promise.all([
      refresh("room", "/control-room"),
      refresh("dashboard", "/dashboard"),
    ]);
    return result;
  } catch (error) {
    feedback(root, error);
    return null;
  }
}
export function cameraMarkup() {
  return `<section class="camera-panel"><div class="section-heading"><h2>${tr("Live preview")}</h2><span class="muted">240×176</span></div><div class="camera-image"><img data-camera alt="${escape(t("Live preview"))}" hidden><div data-roi class="camera-roi" hidden><span>ROI</span></div><span data-camera-empty>${tr("No image available")}</span></div><p class="muted">${tr("The live preview and stored inspection are different moments.")}</p><p data-camera-error class="error-note" hidden></p></section>`;
}
export function cameraPoll(root, signal) {
  const image = $("[data-camera]", root);
  if (!image) return;
  let currentUrl;
  const overlay = () => {
    const roi = state.recipes?.find((r) => r.isActive)?.definition.roi,
      box = $("[data-roi]", root);
    if (!roi || !image.naturalWidth || image.hidden) {
      box.hidden = true;
      return;
    }
    const width = image.clientWidth,
      height = image.clientHeight,
      scale = Math.min(
        width / image.naturalWidth,
        height / image.naturalHeight,
      );
    box.hidden = false;
    Object.assign(box.style, {
      left:
        image.offsetLeft +
        (width - image.naturalWidth * scale) / 2 +
        roi.x * image.naturalWidth * scale +
        "px",
      top:
        image.offsetTop +
        (height - image.naturalHeight * scale) / 2 +
        roi.y * image.naturalHeight * scale +
        "px",
      width: roi.width * image.naturalWidth * scale + "px",
      height: roi.height * image.naturalHeight * scale + "px",
    });
  };
  image.addEventListener("load", overlay, { signal });
  window.addEventListener("resize", overlay, { signal });
  poll(
    async () => {
      try {
        const response = await fetch("/api/camera/preview", { signal });
        if (!response.ok) throw new Error(t("Camera unavailable"));
        const blob = await response.blob();
        if (signal.aborted) return;
        const url = URL.createObjectURL(blob);
        image.src = url;
        image.hidden = false;
        $("[data-camera-empty]", root).hidden = true;
        if (currentUrl) URL.revokeObjectURL(currentUrl);
        currentUrl = url;
        $("[data-camera-error]", root).hidden = true;
      } catch (error) {
        if (signal.aborted) return;
        const e = $("[data-camera-error]", root);
        e.hidden = false;
        e.textContent = t(
          currentUrl
            ? "Camera disconnected · last frame"
            : "Camera unavailable",
        );
      }
    },
    1000,
    signal,
  );
  signal.addEventListener("abort", () => {
    if (currentUrl) URL.revokeObjectURL(currentUrl);
  });
}
