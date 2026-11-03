import {
  $,
  $$,
  button,
  date,
  details,
  errorText,
  escape,
  heading,
  number,
  panel,
  poll,
  request,
  select,
  state,
  t,
  text,
  tr,
} from "./core.js";
import { counters } from "./production.js";

export function historyPage(root, signal) {
  let page = 1,
    query = "",
    rows = null,
    expanded = null,
    summary = false,
    stats = null,
    chart = null,
    chartMode = "bar",
    barcodeMode = "live";
  root.innerHTML = `${heading("History")}<div class="toolbar">${button("Production summary", "summary", 'aria-expanded="false"')}</div><div data-summary hidden></div>${panel(
    "Inspection history",
    `<form class="history-filters"><input class="form-field" name="q" aria-label="${t("Search class, bin, decision, product")}" data-i18n-placeholder="Search class, bin, decision, product" placeholder="${t("Search class, bin, decision, product")}"><input class="form-field" name="productClass" aria-label="${t("Class")}" placeholder="${t("Class")}" data-i18n-placeholder="Class">${select("decision", "Decision", [{ value: "", label: t("Any decision") }, ...["PASS", "REJECT", "MANUAL_REVIEW"].map((v) => ({ value: v, label: text(v) }))])}${select(
      "simulation",
      "Source",
      [
        { value: "", label: t("Any source") },
        { value: "true", label: t("SIMULATION") },
        { value: "false", label: t("Not simulation") },
      ],
    )}<button class="primary-button" type="submit">${tr("Search")}</button></form><p data-history-error class="error-note" hidden></p><div class="table-scroll"><table class="history-table"><thead><tr>${["Time", "Product", "Decision", "Destination", "Record details"].map((v) => `<th>${tr(v)}</th>`).join("")}</tr></thead><tbody></tbody></table><p data-empty class="empty-state">${tr("Waiting for data")}</p></div><div class="toolbar spread">${button("Previous", "previous", "disabled")}<span data-page></span>${button("Next", "next", "disabled")}</div>`,
  )}`;
  function renderRows() {
    $("tbody", root).innerHTML = (rows?.items ?? [])
      .map(
        (i) =>
          `<tr><td>${date(i.processedAt)}</td><td>${escape(i.productClass)}${i.simulation ? `<span class="simulation-tag">${tr("SIMULATION")}</span>` : ""}</td><td class="${i.decision === "PASS" ? "good" : i.decision === "REJECT" ? "bad" : "warn"}">${escape(text(i.decision))}</td><td>${escape(i.destinationBin)}</td><td><button type="button" class="text-link" data-expand="${escape(i.inspectionId)}" aria-expanded="${expanded === i.inspectionId}">${tr(expanded === i.inspectionId ? "Hide details" : "View details")}</button></td></tr>${expanded === i.inspectionId ? `<tr><td colspan="5">${details(i)}</td></tr>` : ""}`,
      )
      .join("");
    const empty = $("[data-empty]", root);
    empty.hidden = !!rows?.items?.length;
    empty.textContent = t(
      rows ? "No stored inspections match these filters." : "Waiting for data",
    );
    $("[data-page]", root).textContent = t("Page") + " " + number(page);
    $('[data-action="previous"]', root).disabled = page <= 1;
    $('[data-action="next"]', root).disabled = !rows?.hasNextPage;
  }
  async function load() {
    try {
      const result = await request(
        "/inspections?" +
          query +
          (query ? "&" : "") +
          `pageNumber=${page}&pageSize=20`,
        "GET",
        undefined,
        signal,
      );
      if (signal.aborted) return;
      rows = result;
      $("[data-history-error]", root).hidden = true;
      renderRows();
    } catch (e) {
      if (signal.aborted) return;
      const el = $("[data-history-error]", root);
      el.hidden = false;
      el.textContent = errorText(e);
    }
  }
  function renderSummary() {
    if (!summary) return;
    const holder = $("[data-summary]", root);
    if (chart) {
      chart.destroy();
      chart = null;
    }
    holder.innerHTML = `${counters(state.room)}${panel("Inspection destinations", `<div class="metric-grid">${(state.room?.binCounts ?? []).map((v) => `<div>${escape(v.destinationBin)}<strong>${number(v.count)}</strong></div>`).join("")}</div><p class="muted">${tr("Yield is pass divided by pass plus reject. Manual review is outside that denominator. An empty decided population is shown as a dash.")}</p>`)}${panel("Barcode production counter", `<div class="toolbar">${button("Bar", "bar")}${button("Line", "line")}</div><div class="chart"><canvas aria-label="${escape(t("Barcode production counter"))}" role="img"></canvas></div><p class="muted">${tr("This chart is the existing barcode counter. It is separate from recipe inspection yield.")}</p>`)}`;
    const tableRows =
      barcodeMode === "products"
        ? (state.dashboard?.recentProducts ?? [])
            .map(
              (p) =>
                `<tr><td>${escape(p.barcode)}</td><td>${escape(text(p.color))}</td><td>${escape(text(p.status))}</td><td>${date(p.passedAt, true)}</td></tr>`,
            )
            .join("")
        : barcodeMode === "historical"
          ? (stats ?? [])
              .map(
                (s) =>
                  `<tr><td>${number(s.hour)}:00</td><td>${number(s.totalProducts - s.defectiveProducts)}</td><td>${number(s.defectiveProducts)}</td><td>${number(s.totalProducts)}</td></tr>`,
              )
              .join("")
          : state.liveProductCounts
              .map(
                (s) =>
                  `<tr><td>${escape(s.time)}</td><td>${number(s.ok)}</td><td>${number(s.defective)}</td><td>${number(s.ok + s.defective)}</td></tr>`,
              )
              .join("");
    holder.insertAdjacentHTML(
      "beforeend",
      panel(
        "Barcode production counter",
        `<div class="toolbar">${button("Live", "barcode-live")}${button("Hourly production", "barcode-historical")}${button("Recent products", "barcode-products")}</div><div class="table-scroll"><table class="history-table"><thead><tr>${(barcodeMode === "products" ? ["Barcode", "Color", "Status", "Time"] : ["Time", "Accepted", "Rejected", "Total"]).map((v) => `<th>${tr(v)}</th>`).join("")}</tr></thead><tbody>${tableRows}</tbody></table>${tableRows ? "" : `<p class="muted">${tr(barcodeMode === "products" ? "No products yet" : barcodeMode === "historical" ? "No hourly data" : "No data yet — waiting for products")}</p>`}</div>`,
      ),
    );
    if (stats && window.Chart) {
      const css = getComputedStyle(document.documentElement);
      chart = new window.Chart($("canvas", holder), {
        type: chartMode,
        data: {
          labels: stats.map((s) => number(s.hour) + ":00"),
          datasets: [
            {
              label: t("Total"),
              data: stats.map((s) => s.totalProducts),
              backgroundColor: css.getPropertyValue("--color-primary"),
              borderColor: css.getPropertyValue("--color-primary-light"),
            },
            {
              label: t("Rejected"),
              data: stats.map((s) => s.defectiveProducts),
              backgroundColor: css.getPropertyValue("--color-danger"),
              borderColor: css.getPropertyValue("--color-danger-light"),
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          color: css.getPropertyValue("--color-text"),
          plugins: {
            legend: { labels: { color: css.getPropertyValue("--color-text") } },
          },
          scales: {
            x: { ticks: { color: css.getPropertyValue("--color-text-muted") } },
            y: {
              beginAtZero: true,
              ticks: { color: css.getPropertyValue("--color-text-muted") },
            },
          },
        },
      });
    }
  }
  $("form", root).addEventListener("submit", (e) => {
    e.preventDefault();
    const params = new URLSearchParams();
    for (const [k, v] of new FormData(e.currentTarget))
      if (String(v).trim()) params.set(k, String(v).trim());
    query = params.toString();
    page = 1;
    load();
  });
  root.addEventListener(
    "click",
    async (e) => {
      const expand = e.target.closest("[data-expand]");
      if (expand) {
        expanded =
          expanded === expand.dataset.expand ? null : expand.dataset.expand;
        renderRows();
        return;
      }
      const action = e.target.closest("[data-action]")?.dataset.action;
      if (action === "next" || action === "previous") {
        page = Math.max(1, page + (action === "next" ? 1 : -1));
        load();
      }
      if (action === "summary") {
        summary = !summary;
        $("[data-summary]", root).hidden = !summary;
        $('[data-action="summary"]', root).setAttribute(
          "aria-expanded",
          String(summary),
        );
        if (summary) {
          try {
            stats = await request(
              "/statistics/hourly",
              "GET",
              undefined,
              signal,
            );
          } catch (e) {
            if (!signal.aborted)
              $("[data-history-error]", root).textContent = errorText(e);
          }
          if (!signal.aborted) renderSummary();
        }
      }
      if (action === "bar" || action === "line") {
        chartMode = action;
        renderSummary();
      }
      if (action?.startsWith("barcode-")) {
        barcodeMode = action.slice(8);
        renderSummary();
      }
    },
    { signal },
  );
  signal.addEventListener("abort", () => chart?.destroy());
  poll(load, 5000, signal);
  return () => {
    renderRows();
    renderSummary();
  };
}
