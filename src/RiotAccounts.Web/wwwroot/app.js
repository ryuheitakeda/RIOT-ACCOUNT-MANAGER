"use strict";

// While a refresh runs, follow its progress; pages showing data reload once it finishes so they show the new data.
(() => {
  const running = document.querySelector("[data-job-running]");
  if (!running) return;
  const progress = running.querySelector("[data-progress]");
  const reload = document.body.dataset.autoreload === "true";
  const poll = async () => {
    try {
      const response = await fetch("/api/status", { headers: { accept: "application/json" }, cache: "no-store" });
      if (response.ok) {
        const status = await response.json();
        if (!status.current) {
          if (reload) { location.reload(); return; }
          running.textContent = "更新が終わりました。";
          return;
        }
        if (progress && status.current.progress) progress.textContent = status.current.progress;
      }
    } catch { /* the server may be restarting; keep polling */ }
    setTimeout(poll, 4000);
  };
  setTimeout(poll, 4000);
})();

// History chart: one series (LP per observation, or average tier per match) in the accent color, broken where the rank changes.
(() => {
  const source = document.getElementById("chart-data");
  const canvas = document.getElementById("history-chart");
  if (!source || !canvas || !window.Chart) return;
  const spec = JSON.parse(source.textContent);
  const when = new Intl.DateTimeFormat("ja-JP", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" });
  const day = new Intl.DateTimeFormat("ja-JP", { month: "numeric", day: "numeric" });
  const tier = spec.kind === "tier";
  let chart;

  // Draws a crosshair at the hovered x so a reader can line points up with the axis.
  const crosshair = {
    id: "crosshair",
    afterDatasetsDraw(c) {
      const active = c.tooltip?.getActiveElements?.() ?? [];
      if (!active.length) return;
      const x = active[0].element.x;
      const { top, bottom } = c.chartArea;
      const ctx = c.ctx;
      ctx.save();
      ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue("--ink-3");
      ctx.lineWidth = 1;
      ctx.setLineDash([3, 3]);
      ctx.beginPath(); ctx.moveTo(x, top); ctx.lineTo(x, bottom); ctx.stroke();
      ctx.restore();
    }
  };

  // Names each LP segment at its first point; the colour stays the same, so the label carries the tier.
  const segmentLabels = {
    id: "segmentLabels",
    afterDatasetsDraw(c) {
      if (tier) return;
      const css = getComputedStyle(document.documentElement);
      const ctx = c.ctx;
      ctx.save();
      ctx.font = "12px system-ui, sans-serif";
      ctx.fillStyle = css.getPropertyValue("--ink-2");
      ctx.textBaseline = "bottom";
      c.data.datasets.forEach((dataset, i) => {
        const first = c.getDatasetMeta(i).data[0];
        if (!first) return;
        const x = Math.min(first.x + 4, c.chartArea.right - ctx.measureText(dataset.label).width);
        ctx.fillText(dataset.label, x, Math.max(first.y - 8, c.chartArea.top + 12));
      });
      ctx.restore();
    }
  };

  const render = () => {
    const css = getComputedStyle(document.documentElement);
    const color = css.getPropertyValue("--accent").trim();
    const surface = css.getPropertyValue("--surface").trim();
    const ink2 = css.getPropertyValue("--ink-2").trim();
    const grid = css.getPropertyValue("--grid").trim();
    chart?.destroy();
    chart = new Chart(canvas, {
      type: "scatter",
      data: {
        datasets: spec.segments.map(segment => ({
          label: segment.label,
          data: segment.points.map(p => ({ x: p.x, y: p.y, label: p.label })),
          showLine: true,
          borderColor: color,
          backgroundColor: color,
          borderWidth: 2,
          pointRadius: 4,
          pointHoverRadius: 6,
          pointBorderColor: surface,
          pointBorderWidth: 2,
          tension: 0
        }))
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { mode: "nearest", axis: "x", intersect: false },
        plugins: {
          legend: { display: false },
          tooltip: {
            displayColors: false,
            callbacks: {
              title: items => when.format(items[0].parsed.x),
              label: item => item.raw.label
            }
          }
        },
        scales: {
          x: {
            type: "linear",
            grid: { color: grid },
            border: { display: false },
            ticks: { color: ink2, maxTicksLimit: 6, maxRotation: 0, callback: value => day.format(value) }
          },
          y: tier
            ? {
                min: spec.yMin - 0.5,
                max: spec.yMax + 0.5,
                grid: { color: grid },
                border: { display: false },
                afterBuildTicks: axis => { axis.ticks = Object.keys(spec.yLabels).map(k => ({ value: Number(k) })); },
                ticks: { color: ink2, autoSkip: false, callback: value => spec.yLabels[value] ?? "" }
              }
            : {
                grid: { color: grid },
                border: { display: false },
                title: { display: true, text: "LP", color: ink2 },
                ticks: { color: ink2, precision: 0 }
              }
        }
      },
      plugins: [crosshair, segmentLabels]
    });
  };

  render();
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", render);
})();
