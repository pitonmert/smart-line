import {
  $,
  $$,
  available,
  button,
  cameraMarkup,
  cameraPoll,
  date,
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

export function datasetPage(root, signal) {
  let sessions = null,
    active = null,
    sessionsError = null,
    busy = false,
    intervalRunning = false,
    timer = null,
    synced = null,
    recipeSignature = "",
    previewSignature = "";
  root.innerHTML = `${heading("Dataset collection")}<div class="two-column">${cameraMarkup()}${panel("Session", '<div data-session></div><div class="toolbar">' + button("Capture from camera", "capture", 'data-testid="dataset-capture" data-write') + button("Start interval", "interval", "hidden") + "</div>")}</div>${panel("Session labels", `<form data-labels class="form-grid">${select("productClass", "Product class", [])}${select("qualityLabel", "Quality", ["GOOD", "DEFECTIVE"], "GOOD")}${select("colorLabel", "Color", [])}${select("sizeLabel", "Size", [])}<fieldset class="wide"><legend>${tr("Defects")}</legend><div data-defects class="toolbar"></div></fieldset><label class="wide">${tr("Notes")}<textarea name="notes" class="form-field" rows="2"></textarea></label></form><form data-start class="form-grid">${select("recipeId", "Recipe", [])}${select("captureMode", "Capture mode", ["manual", "interval"], "manual")}${input("intervalMs", "Interval milliseconds", "number", 1000, 'min="200" max="60000"')}<label><input type="checkbox" name="simulation">${tr("Mark SIMULATION")}</label><button class="primary-button" type="submit" data-write>${tr("Start session")}</button></form>`)}${panel("Recent captures", '<ul data-captures class="capture-grid"></ul>')}<div data-counts class="three-column"></div>${panel("Sessions", "<ul data-sessions></ul>")}${messages}<p class="tool-note">${tr("Each press stores one high-quality JPEG. If that still fails and preview fallback is disabled, nothing is stored. Interval capture uses the labels currently selected on this page. Hardware trigger stays deferred.")}</p>`;
  const labels = () => {
    const f = new FormData($("[data-labels]", root));
    return {
      productClass: String(f.get("productClass") || ""),
      qualityLabel: String(f.get("qualityLabel") || "GOOD"),
      colorLabel: String(f.get("colorLabel") || ""),
      sizeLabel: String(f.get("sizeLabel") || ""),
      defectLabels: f.getAll("defectLabels").map(String),
      notes: String(f.get("notes") || ""),
    };
  };
  const canCapture = () =>
    available() &&
    !sessionsError &&
    !!active &&
    !busy &&
    !!labels().productClass &&
    !!labels().colorLabel &&
    !!labels().sizeLabel;
  const stopInterval = () => {
    intervalRunning = false;
    if (timer) clearTimeout(timer);
    timer = null;
  };
  const syncLabels = () => {
    const list = state.recipes ?? [];
    const recipeSelect = $('[name="recipeId"]', root);
    const recipeId =
      active?.recipeId ||
      recipeSelect.value ||
      list.find((r) => r.isActive)?.id ||
      list[0]?.id;
    const recipe = list.find((r) => r.id === recipeId);
    const signature = JSON.stringify([
      list.map((r) => [r.id, r.version]),
      recipeId,
    ]);
    if (signature !== recipeSignature) {
      recipeSignature = signature;
      recipeSelect.innerHTML = list
        .map(
          (r) => `<option value="${escape(r.id)}">${escape(r.name)}</option>`,
        )
        .join("");
      recipeSelect.value = recipeId ?? "";
      for (const [name, key] of [
        ["productClass", "productClasses"],
        ["colorLabel", "allowedColors"],
        ["sizeLabel", "sizeClasses"],
      ]) {
        const select = $(`[name="${name}"]`, root),
          old = select.value;
        select.innerHTML = (recipe?.definition[key] ?? [])
          .map(
            (o) =>
              `<option value="${escape(o.code)}">${escape(o.displayName || o.code)} (${escape(o.code)})</option>`,
          )
          .join("");
        if ([...select.options].some((o) => o.value === old))
          select.value = old;
      }
      const checked = new Set(labels().defectLabels);
      $("[data-defects]", root).innerHTML = (
        recipe?.definition.defectClasses ?? []
      )
        .map(
          (d) =>
            `<label><input type="checkbox" name="defectLabels" value="${escape(d.code)}" ${checked.has(d.code) ? "checked" : ""}>${escape(d.displayName || d.code)} (${escape(d.code)})</label>`,
        )
        .join("");
    }
    if (active && synced !== active.sessionId) {
      synced = active.sessionId;
      for (const key of [
        "productClass",
        "qualityLabel",
        "colorLabel",
        "sizeLabel",
        "notes",
      ])
        $(`[name="${key}"]`, root).value = active[key] ?? "";
      $$('[name="defectLabels"]', root).forEach(
        (b) => (b.checked = active.defectLabels.includes(b.value)),
      );
    }
    if (!active) synced = null;
  };
  const update = () => {
    if (!available() || sessionsError || document.hidden || !active)
      stopInterval();
    syncLabels();
    $("[data-start]", root).hidden = !!active;
    $('[name="intervalMs"]', root).closest("label").hidden =
      $('[name="captureMode"]', root).value !== "interval";
    $("[data-session]", root).innerHTML = active
      ? `<p><strong>${escape(active.recipeName)}</strong> · ${escape(text(active.status))}</p><p data-testid="dataset-count">${escape(t("{count} captures", { count: active.imageCount }))}</p><p>${escape(active.productClass)} / ${escape(text(active.qualityLabel))} / ${escape(text(active.colorLabel))} / ${escape(text(active.sizeLabel))}</p><p class="muted">${escape(active.labelStatus)}</p>${button("Stop session", "stop", "data-write")}`
      : `<p>${tr("No dataset session is active. Choose the recipe labels, then start a session.")}</p>`;
    $('[data-action="capture"]', root).disabled = !canCapture();
    const interval = $('[data-action="interval"]', root);
    interval.hidden = active?.captureMode !== "interval";
    interval.disabled = !available() || !!sessionsError || !active;
    interval.innerHTML = tr(
      intervalRunning ? "Stop interval" : "Start interval",
    );
    $$("[data-write]", root)
      .filter((el) => el.dataset.action !== "capture")
      .forEach((el) => (el.disabled = !available() || busy || !!sessionsError));
    $("[data-sessions]", root).innerHTML =
      (sessions ?? [])
        .map(
          (s) =>
            `<li class="list-row">${date(s.startedAt)} · ${escape(text(s.status))} · ${escape(t("{count} images", { count: s.imageCount }))} · ${escape(s.productClass)} · ${escape(text(s.captureMode))}${s.simulation ? `<span class="simulation-tag">${tr("SIMULATION")}</span>` : ""}</li>`,
        )
        .join("") ||
      (sessions
        ? `<li>${tr("No dataset sessions yet.")}</li>`
        : `<li>${tr("Waiting for data")}</li>`);
    const signature = JSON.stringify([
      active?.recentSamples,
      document.documentElement.lang,
    ]);
    if (signature !== previewSignature) {
      previewSignature = signature;
      $("[data-captures]", root).innerHTML =
        (active?.recentSamples ?? [])
          .map(
            (s) =>
              `<li><img src="/api/dataset/sessions/${encodeURIComponent(active.sessionId)}/images/${encodeURIComponent(s.fileName)}" alt=""><div><p>${escape(s.fileName)}</p><p>${date(s.capturedAt)}</p><p>${escape(s.productClass)} / ${escape(text(s.qualityLabel))} / ${escape(text(s.colorLabel))} / ${escape(text(s.sizeLabel))}</p><p class="muted">${escape((s.defectLabels ?? []).map(text).join(", ") || t("No defects"))} · ${escape(s.captureSource)} · ${number(s.sourceWidth)}×${number(s.sourceHeight)}</p>${s.previewFallback ? `<p class="warn">${tr("Preview fallback was used. This is not the high-quality still.")}</p>` : ""}${button("Discard", "discard", `data-id="${escape(s.sampleId)}" data-write`)}</div></li>`,
          )
          .join("") || `<li>${tr("No captures in this session yet.")}</li>`;
    }
    $$('[data-action="discard"]', root).forEach(
      (b) => (b.disabled = !available() || busy || !!sessionsError),
    );
    $("[data-counts]", root).innerHTML = active
      ? [
          ["Class distribution", active.classCounts],
          ["Quality distribution", active.qualityCounts],
          ["Defect distribution", active.defectCounts],
        ]
          .map(([title, counts]) =>
            panel(
              title,
              `<ul>${
                Object.entries(counts ?? {})
                  .map(
                    ([key, value]) =>
                      `<li class="list-row"><span>${escape(text(key))}</span><strong>${number(value)}</strong></li>`,
                  )
                  .join("") || `<li>${tr("No captures yet.")}</li>`
              }</ul>`,
            ),
          )
          .join("")
      : "";
    if (sessionsError) feedback(root, sessionsError);
  };
  const load = async () => {
    try {
      const data = await request("/dataset/sessions", "GET", undefined, signal);
      if (signal.aborted) return;
      sessions = data;
      active = data.find((s) => s.status === "ACTIVE") ?? null;
      sessionsError = null;
      update();
    } catch (e) {
      if (signal.aborted) return;
      sessionsError = e;
      stopInterval();
      update();
    }
  };
  const capture = async () => {
    if (!canCapture()) return false;
    busy = true;
    update();
    const result = await operation(root, () =>
      request(
        "/dataset/sessions/" + active.sessionId + "/captures/camera",
        "POST",
        labels(),
      ),
    );
    if (result) {
      active = result.session;
      sessions = (sessions ?? []).map((s) =>
        s.sessionId === active.sessionId ? active : s,
      );
      await load();
    } else stopInterval();
    busy = false;
    update();
    return !!result;
  };
  function next() {
    if (!intervalRunning || signal.aborted) return;
    timer = setTimeout(async () => {
      if (!intervalRunning || !canCapture() || document.hidden) {
        stopInterval();
        update();
        return;
      }
      await capture();
      next();
    }, active?.intervalMs ?? 1000);
  }
  $("[data-start]", root).addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!available() || busy || sessionsError) return;
    const f = new FormData(e.target);
    const result = await operation(root, () =>
      request("/dataset/sessions", "POST", {
        ...labels(),
        recipeId: String(f.get("recipeId")),
        captureMode: f.get("captureMode"),
        intervalMs: Number(f.get("intervalMs")),
        simulation: f.get("simulation") === "on",
      }),
    );
    if (result) {
      active = result;
      await load();
    }
  });
  $('[name="recipeId"]', root).addEventListener("change", () => {
    recipeSignature = "";
    syncLabels();
  });
  $('[name="captureMode"]', root).addEventListener("change", update);
  root.addEventListener(
    "click",
    async (e) => {
      const b = e.target.closest("[data-action]");
      if (!b || b.disabled) return;
      if (b.dataset.action === "capture") await capture();
      if (b.dataset.action === "interval") {
        if (intervalRunning) stopInterval();
        else if (available() && !sessionsError && active) {
          intervalRunning = true;
          next();
        }
        update();
      }
      if (b.dataset.action === "stop" && active) {
        stopInterval();
        await operation(root, () =>
          request("/dataset/sessions/" + active.sessionId + "/stop", "POST"),
        );
        await load();
      }
      if (b.dataset.action === "discard" && active) {
        await operation(root, () =>
          request(
            "/dataset/sessions/" +
              active.sessionId +
              "/samples/" +
              b.dataset.id,
            "DELETE",
          ),
        );
        await load();
      }
    },
    { signal },
  );
  document.addEventListener(
    "visibilitychange",
    () => {
      if (document.hidden) {
        stopInterval();
        update();
      }
    },
    { signal },
  );
  signal.addEventListener("abort", stopInterval);
  cameraPoll(root, signal);
  poll(load, 4000, signal);
  return update;
}
