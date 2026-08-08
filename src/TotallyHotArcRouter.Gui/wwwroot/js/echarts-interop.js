// Apache ECharts interop for the dashboard's charts (Cost Analytics + Model Distribution).
//
// The whole app renders its charts through this one module. Blazor computes a plain, serializable
// "chart model" in C# (see TotallyHotArcRouter.Gui.Charts) and hands it here as JSON; this file owns the
// ECharts option construction - including the bespoke per-metric Cost Analytics chart formats from
// docs/gui/cost-analytics-visualization-spec.md and the "bold & showy" animation package. Keeping the
// verbose option-building in JS (rather than modelling every ECharts option in C#) keeps the .NET side
// small and testable while giving the charts full access to ECharts features (custom renderItem,
// effectScatter ripples, markLine/markArea, gradients).
//
// echarts.min.js (vendored under wwwroot/lib/echarts, Apache-2.0) is loaded ahead of this script in
// index.html, so `echarts` is a global by the time any chart renders.
window.echartsInterop = (function () {
  // element-id -> live ECharts instance, so re-renders reuse (and animate) the same chart and disposal
  // is reliable when a tab/metric switch tears a chart down.
  const instances = new Map();
  // element-id -> ResizeObserver, so charts track their flex container without a window resize.
  const observers = new Map();

  const AXIS_COLOR = "#475569";
  const GRID_COLOR = "#1e3a5f";
  const FONT = "JetBrains Mono, Fira Code, monospace";
  const ACCENT = "#1ed760";

  function disposeChart(elementId) {
    const inst = instances.get(elementId);
    if (inst) {
      inst.dispose();
      instances.delete(elementId);
    }
    const obs = observers.get(elementId);
    if (obs) {
      obs.disconnect();
      observers.delete(elementId);
    }
  }

  function getChart(el, elementId) {
    let inst = instances.get(elementId);
    // A disposed instance (e.g. hot tab switch) reports isDisposed(); rebuild in that case.
    if (!inst || inst.isDisposed()) {
      inst = echarts.init(el, null, { renderer: "canvas" });
      instances.set(elementId, inst);
      if (!observers.has(elementId) && typeof ResizeObserver !== "undefined") {
        // Look the instance up by id each time rather than closing over `inst`: a hot re-init
        // (isDisposed) replaces the instance while reusing this same observer, so a captured
        // reference would resize the stale/disposed chart and never the live one.
        const obs = new ResizeObserver(() => {
          const current = instances.get(elementId);
          if (current && !current.isDisposed()) {
            current.resize();
          }
        });
        obs.observe(el);
        observers.set(elementId, obs);
      }
    }
    return inst;
  }

  // ---- shared cosmetic helpers -------------------------------------------------

  function numberFmt(unit) {
    switch (unit) {
      case "$":
        return (v) => "$" + Number(v).toFixed(2);
      case "%":
        return (v) => Number(v).toFixed(0) + "%";
      case "ms":
        return (v) => Number(v).toFixed(0) + " ms";
      case "tok":
        return (v) => {
          const a = Math.abs(v);
          if (a >= 1e6) return (v / 1e6).toFixed(1) + "M";
          if (a >= 1e3) return (v / 1e3).toFixed(0) + "K";
          return Number(v).toFixed(0);
        };
      default:
        return (v) => Number(v).toFixed(0);
    }
  }

  // Escapes text before it goes into tooltip HTML. Tooltip strings carry model names and other
  // telemetry-derived fields, so treat them as untrusted to avoid HTML injection.
  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c],
    );
  }

  // Dashboard-styled tooltip box. `header` is the turn title; `lines` are pre-formatted in C#.
  function tooltipBox(header, lines, accent) {
    const body = (lines || [])
      .map(
        (l) =>
          `<div style="color:#cbd5e1;line-height:1.5;white-space:nowrap">${escapeHtml(l)}</div>`,
      )
      .join("");
    return (
      `<div style="font-family:${FONT};font-size:11px;min-width:180px">` +
      `<div style="color:${accent || ACCENT};font-weight:600;margin-bottom:4px;border-bottom:1px solid #334155;padding-bottom:3px">${escapeHtml(header)}</div>` +
      body +
      `</div>`
    );
  }

  const baseTooltip = {
    backgroundColor: "#1e293b",
    borderColor: "#334155",
    borderWidth: 1,
    padding: [8, 10],
    textStyle: { color: "#e2e8f0", fontFamily: FONT, fontSize: 11 },
    extraCssText: "box-shadow:0 8px 24px rgba(0,0,0,.45);border-radius:6px;",
  };

  function baseGrid(extra) {
    return Object.assign(
      { left: 58, right: 22, top: 24, bottom: 28, containLabel: false },
      extra || {},
    );
  }

  function timeAxis() {
    return {
      type: "time",
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: AXIS_COLOR, fontFamily: FONT, fontSize: 10, hideOverlap: true },
      splitLine: { show: false },
    };
  }

  function valueAxis(unit, opts) {
    return Object.assign(
      {
        type: "value",
        splitLine: { lineStyle: { color: GRID_COLOR, type: "dashed" } },
        axisLabel: {
          color: AXIS_COLOR,
          fontFamily: FONT,
          fontSize: 10,
          formatter: numberFmt(unit),
        },
      },
      opts || {},
    );
  }

  // Bold, staggered entrance shared by every Cost Analytics chart.
  const boldAnim = {
    animation: true,
    animationDuration: 1100,
    animationEasing: "cubicOut",
    animationDelay: (idx) => idx * 45,
    animationDurationUpdate: 600,
    animationEasingUpdate: "cubicInOut",
  };

  // Contiguous runs of the same model, used for TTFT background "silhouette" zoning.
  function modelRuns(points) {
    const runs = [];
    for (let i = 0; i < points.length; i++) {
      const p = points[i];
      const last = runs[runs.length - 1];
      if (last && last.model === p.model) {
        last.end = p.t;
      } else {
        runs.push({ model: p.model, color: p.color, start: p.t, end: p.t });
      }
    }
    return runs;
  }

  // Median spacing between points, so the last stepped rectangle/zone has a sensible width.
  function medianGap(points) {
    if (points.length < 2) return 60000;
    const gaps = [];
    for (let i = 1; i < points.length; i++) gaps.push(points[i].t - points[i - 1].t);
    gaps.sort((a, b) => a - b);
    return gaps[Math.floor(gaps.length / 2)] || 60000;
  }

  // ---- per-metric Cost Analytics builders --------------------------------------

  // 1. Routing ROI - dual-directional bars (savings above 0, remediation/failures below), each bar
  //    filled with its turn's model color.
  function optionDualDirectionalBars(m) {
    const gap = medianGap(m.points);
    const data = m.points.map((p) => ({
      value: [p.t, Number(p.value)],
      itemStyle: {
        color: Number(p.value) < 0 ? "#ef4444" : p.color,
        borderRadius: 2,
        shadowBlur: Number(p.value) < 0 ? 10 : 0,
        shadowColor: "rgba(239,68,68,.5)",
      },
      _p: p,
    }));
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis", axisPointer: { type: "line", lineStyle: { color: "#334155" } } }, baseTooltip, {
        formatter: (ps) => {
          const p = ps[0].data._p;
          return tooltipBox(p.label, p.tip, p.color);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis(m.unit, {
        axisLine: { show: true, lineStyle: { color: "#334155" } },
      }),
      series: [
        {
          type: "bar",
          barWidth: "60%",
          barMaxWidth: 26,
          data,
          markLine: {
            silent: true,
            symbol: "none",
            label: { show: false },
            lineStyle: { color: "#334155", width: 1 },
            data: [{ yAxis: 0 }],
          },
        },
      ],
    });
  }

  // 2. Total Turn Cost - stepped stacked-area running total. Each turn is a colored rectangle from 0 up
  //    to the cumulative total (custom series), so the fill color changes at each step to the active
  //    model. A stepped line rides the top edge.
  function optionSteppedCumulativeArea(m) {
    const gap = medianGap(m.points);
    const pts = m.points;
    const rectData = pts.map((p, i) => {
      const x1 = i + 1 < pts.length ? pts[i + 1].t : p.t + gap;
      return { value: [p.t, x1, Number(p.secondary)], _p: p };
    });
    const lineData = pts.map((p) => [p.t, Number(p.secondary)]);
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "item" }, baseTooltip, {
        formatter: (o) => {
          const p = (o.data && o.data._p) || null;
          return p ? tooltipBox(p.label, p.tip, p.color) : "";
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis(m.unit),
      series: [
        {
          type: "custom",
          renderItem: (params, api) => {
            const d = rectData[params.dataIndex];
            const p0 = api.coord([d.value[0], d.value[2]]);
            const p1 = api.coord([d.value[1], 0]);
            const color = d._p.color;
            return {
              type: "rect",
              shape: { x: p0[0], y: p0[1], width: Math.max(1, p1[0] - p0[0] - 1), height: p1[1] - p0[1] },
              style: {
                fill: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                  { offset: 0, color: color + "cc" },
                  { offset: 1, color: color + "33" },
                ]),
                stroke: color,
                lineWidth: 1,
              },
            };
          },
          data: rectData,
          encode: { x: [0, 1], y: 2 },
        },
        {
          type: "line",
          step: "end",
          data: lineData,
          symbol: "circle",
          symbolSize: 5,
          itemStyle: { color: "#f8fafc" },
          lineStyle: { color: "#f8fafc", width: 2 },
          z: 5,
          tooltip: { show: false },
        },
      ],
    });
  }

  // 3. Prompt + Completion Tokens - stepped cumulative area with a runaway indicator: flagged
  //    (exponential-growth) segments get a hatched warning overlay and a rippling alert point.
  function optionRunawayArea(m) {
    const pts = m.points;
    const lineData = pts.map((p) => [p.t, Number(p.value)]);
    const runawayPts = pts
      .filter((p) => p.flag)
      .map((p) => ({ value: [p.t, Number(p.value)], _p: p }));
    // Hatched markAreas spanning each runaway step.
    const gap = medianGap(pts);
    const areas = pts
      .filter((p) => p.flag)
      .map((p) => [{ xAxis: p.t }, { xAxis: p.t + gap }]);
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis" }, baseTooltip, {
        formatter: (ps) => {
          const idx = ps[0].dataIndex;
          const p = pts[idx];
          return tooltipBox(p.label, p.tip, p.flag ? "#f59e0b" : p.color);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis(m.unit),
      series: [
        {
          type: "line",
          step: "end",
          data: lineData,
          symbol: "circle",
          symbolSize: 5,
          smooth: false,
          itemStyle: { color: ACCENT },
          lineStyle: { color: ACCENT, width: 2.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(30,215,96,.45)" },
              { offset: 1, color: "rgba(30,215,96,.03)" },
            ]),
          },
          markArea: {
            silent: true,
            itemStyle: {
              color: new echarts.graphic.LinearGradient(0, 0, 1, 1, [
                { offset: 0, color: "rgba(245,158,11,.28)" },
                { offset: 0.5, color: "rgba(245,158,11,.08)" },
                { offset: 0.5, color: "rgba(245,158,11,.28)" },
                { offset: 1, color: "rgba(245,158,11,.08)" },
              ]),
            },
            data: areas,
          },
        },
        {
          type: "effectScatter",
          data: runawayPts,
          symbolSize: 14,
          showEffectOn: "render",
          rippleEffect: { scale: 4, brushType: "stroke" },
          itemStyle: { color: "#f59e0b", shadowBlur: 12, shadowColor: "#f59e0b" },
          z: 10,
          tooltip: { show: false },
        },
      ],
    });
  }

  // 4. Tool Execution Loop Count - a vertical bar per turn whose height is the step count, split into
  //    stacked segments colored by the model that handled each stretch of steps.
  function optionSegmentedStepBars(m) {
    // Union of models present across all turns' segments -> one stacked bar series each.
    const modelOrder = m.models.map((x) => x.model);
    const colorOf = {};
    m.models.forEach((x) => (colorOf[x.model] = x.color));
    const series = modelOrder.map((model) => ({
      type: "bar",
      stack: "steps",
      name: model,
      barWidth: "55%",
      barMaxWidth: 28,
      itemStyle: { color: colorOf[model], borderColor: "#0f172a", borderWidth: 1 },
      data: m.points.map((p) => {
        const seg = (p.segments || []).find((s) => s.model === model);
        return { value: [p.t, seg ? seg.steps : 0], _p: p };
      }),
    }));
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis", axisPointer: { type: "shadow" } }, baseTooltip, {
        formatter: (ps) => {
          const p = ps[0].data._p;
          return tooltipBox(p.label, p.tip, p.color);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis("steps", { minInterval: 1 }),
      series,
    });
  }

  // 5. Cache Hit Rate - stepped line 0-100% with a soft gradient track; per-turn markers carry the
  //    active model's color.
  function optionCacheGradientLine(m) {
    const pts = m.points;
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis" }, baseTooltip, {
        formatter: (ps) => {
          const p = pts[ps[0].dataIndex];
          return tooltipBox(p.label, p.tip, p.color);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis("%", { min: 0, max: 100 }),
      series: [
        {
          type: "line",
          step: "end",
          data: pts.map((p) => ({
            value: [p.t, Number(p.value)],
            itemStyle: { color: p.color, borderColor: "#0f172a", borderWidth: 1 },
          })),
          symbol: "circle",
          symbolSize: 7,
          lineStyle: { color: "#22c55e", width: 2.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(34,197,94,.42)" },
              { offset: 1, color: "rgba(34,197,94,.02)" },
            ]),
          },
        },
      ],
    });
  }

  // 6. TTFT / Routing Latency - unshaded stepped line over vertical model "silhouette" zones (the
  //    background color of each vertical slice is the active model).
  function optionZonedLatencyLine(m) {
    const pts = m.points;
    const gap = medianGap(pts);
    const runs = modelRuns(pts);
    // Background zones as a hidden line series carrying markAreas.
    const zones = runs.map((r) => [
      { xAxis: r.start, itemStyle: { color: r.color + "22" } },
      { xAxis: r.end + gap },
    ]);
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis" }, baseTooltip, {
        formatter: (ps) => {
          const p = pts[ps[0].dataIndex];
          return tooltipBox(p.label, p.tip, p.color);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis("ms"),
      series: [
        {
          type: "line",
          step: "middle",
          data: pts.map((p) => ({
            value: [p.t, Number(p.value)],
            itemStyle: { color: p.color },
          })),
          symbol: "circle",
          symbolSize: 6,
          lineStyle: { color: "#e2e8f0", width: 2 },
          markArea: { silent: true, data: zones },
          markPoint: {
            symbol: "pin",
            symbolSize: 42,
            itemStyle: { color: "#ef4444" },
            label: { color: "#fff", fontSize: 9, formatter: "spike" },
            data: pts
              .filter((p) => p.flag)
              .map((p) => ({ coord: [p.t, Number(p.value)] })),
          },
        },
      ],
    });
  }

  // 7. Context Buffer Margin - stepped line of % context used with a fixed dashed red 90% threshold;
  //    breaches get a rippling alert point (the spec's "pulsing" line) and the segment thickens.
  function optionThresholdLine(m) {
    const pts = m.points;
    const threshold = m.threshold != null ? m.threshold : 90;
    const breaches = pts
      .filter((p) => Number(p.value) >= threshold)
      .map((p) => ({ value: [p.t, Number(p.value)], _p: p }));
    return Object.assign({}, boldAnim, {
      grid: baseGrid(),
      tooltip: Object.assign({ trigger: "axis" }, baseTooltip, {
        formatter: (ps) => {
          const p = pts[ps[0].dataIndex];
          const accent = Number(p.value) >= threshold ? "#ef4444" : p.color;
          return tooltipBox(p.label, p.tip, accent);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis("%", { min: 0, max: 100 }),
      series: [
        {
          type: "line",
          step: "end",
          data: pts.map((p) => ({
            value: [p.t, Number(p.value)],
            itemStyle: { color: Number(p.value) >= threshold ? "#ef4444" : p.color },
          })),
          symbol: "circle",
          symbolSize: 6,
          lineStyle: { color: ACCENT, width: 2.5 },
          markLine: {
            silent: true,
            symbol: "none",
            label: {
              formatter: threshold + "% limit",
              color: "#ef4444",
              fontFamily: FONT,
              fontSize: 10,
              position: "insideEndTop",
            },
            lineStyle: { color: "#ef4444", type: "dashed", width: 1.5 },
            data: [{ yAxis: threshold }],
          },
        },
        {
          type: "effectScatter",
          data: breaches,
          symbolSize: 16,
          showEffectOn: "render",
          rippleEffect: { scale: 4.5, brushType: "stroke" },
          itemStyle: { color: "#ef4444", shadowBlur: 14, shadowColor: "#ef4444" },
          z: 10,
          tooltip: { show: false },
        },
      ],
    });
  }

  // ---- Providers card builder ---------------------------------------------------

  // Rate-limit trend: a dimension's remaining budget over time, stepped line with a soft gradient track
  // (Phase 5, §5.9). Structurally the same shape as the Cost Analytics cache-hit-rate line, but its own
  // function since the unit is a raw count (tokens/requests), not a fixed 0-100% axis.
  function optionRateLimitTrendLine(m) {
    const pts = m.points || [];
    return Object.assign({}, boldAnim, {
      grid: baseGrid({ left: 46 }),
      tooltip: Object.assign({ trigger: "axis" }, baseTooltip, {
        formatter: (ps) => {
          const p = pts[ps[0].dataIndex];
          const lines = [
            `Remaining: ${p.remaining == null ? "?" : Number(p.remaining).toLocaleString()}`,
          ];
          if (p.limit != null) lines.push(`Limit: ${Number(p.limit).toLocaleString()}`);
          return tooltipBox(p.label, lines, ACCENT);
        },
      }),
      xAxis: timeAxis(),
      yAxis: valueAxis("tok", { min: 0 }),
      series: [
        {
          type: "line",
          step: "end",
          connectNulls: true,
          data: pts.map((p) => [p.t, p.remaining == null ? null : Number(p.remaining)]),
          symbol: "circle",
          symbolSize: 5,
          lineStyle: { color: ACCENT, width: 2 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(30,215,96,.38)" },
              { offset: 1, color: "rgba(30,215,96,.02)" },
            ]),
          },
        },
      ],
    });
  }

  // ---- Model Distribution builders ---------------------------------------------

  function optionGroupedBars(m) {
    return Object.assign({}, boldAnim, {
      grid: baseGrid({ left: 52, bottom: 22 }),
      legend: { show: false },
      tooltip: Object.assign({ trigger: "axis", axisPointer: { type: "shadow" } }, baseTooltip, {
        valueFormatter: numberFmt("tok"),
      }),
      xAxis: {
        type: "category",
        data: m.categories,
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { color: AXIS_COLOR, fontFamily: FONT, fontSize: 10 },
      },
      yAxis: valueAxis("tok", { max: m.yMax || null }),
      series: (m.series || []).map((s) => ({
        type: "bar",
        name: s.name,
        data: s.data,
        barWidth: "34%",
        itemStyle: { color: s.color, borderRadius: [2, 2, 0, 0] },
      })),
    });
  }

  function optionDonut(m) {
    return Object.assign({}, boldAnim, {
      tooltip: Object.assign({ trigger: "item" }, baseTooltip, {
        formatter: (o) => `${o.name}: ${o.value}%`,
      }),
      legend: { show: false },
      series: [
        {
          type: "pie",
          radius: ["58%", "82%"],
          center: ["50%", "50%"],
          avoidLabelOverlap: false,
          itemStyle: { borderColor: "#1e293b", borderWidth: 2 },
          label: { show: false },
          labelLine: { show: false },
          data: (m.slices || []).map((s) => ({
            name: s.model,
            value: Number(s.value),
            itemStyle: { color: s.color },
          })),
          animationType: "scale",
          animationEasing: "elasticOut",
        },
      ],
    });
  }

  function buildOption(m) {
    switch (m.kind) {
      case "DualDirectionalBars":
        return optionDualDirectionalBars(m);
      case "SteppedCumulativeArea":
        return optionSteppedCumulativeArea(m);
      case "RunawayArea":
        return optionRunawayArea(m);
      case "SegmentedStepBars":
        return optionSegmentedStepBars(m);
      case "CacheGradientLine":
        return optionCacheGradientLine(m);
      case "ZonedLatencyLine":
        return optionZonedLatencyLine(m);
      case "ThresholdLine":
        return optionThresholdLine(m);
      case "RemainingLine":
        return optionRateLimitTrendLine(m);
      case "GroupedBars":
        return optionGroupedBars(m);
      case "Donut":
        return optionDonut(m);
      default:
        return { title: { text: "Unknown chart kind: " + m.kind, textStyle: { color: "#ef4444" } } };
    }
  }

  return {
    // Render (or re-render) the chart for `elementId` from a JSON-encoded chart model.
    render: function (elementId, modelJson) {
      const el = document.getElementById(elementId);
      if (!el) return;
      const model = typeof modelJson === "string" ? JSON.parse(modelJson) : modelJson;
      const chart = getChart(el, elementId);
      // notMerge:true so switching metric/kind never leaves stale series behind.
      chart.setOption(buildOption(model), true);
      chart.resize();
    },
    dispose: disposeChart,
    // Exposed for the standalone verification harness (docs/gui verification).
    _buildOption: buildOption,
  };
})();

