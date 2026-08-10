# Cost Analytics Tab: Technical Visualization Specification

> **Status: Implemented.** The seven bespoke per-metric charts below are live in
> `Components/CostAnalytics.razor`, rendered with **Apache ECharts** (vendored under
> `wwwroot/lib/echarts`, driven by `wwwroot/js/echarts-interop.js`) via the reusable `<EChart>` host.
> The per-metric chart models are built by the pure, unit-tested
> `TotallyHot.ArcRouter.Gui.Charts.CostChartBuilder`; the whole dashboard moved off ApexCharts to ECharts in
> the same change (Model Distribution too). A few implementation notes on how the spec was realized:
>
> - **Layout / interaction.** The tab keeps its single **metric selector** (one chart on screen at a
>   time), so the cross-chart "synchronized hover" from the *Interaction & Synchronization Layer* below
>   is not applicable and was not built; each chart has its own in-chart crosshair/tooltip instead.
> - **Time axis.** Each chart plots **one point per turn** within the selected window (the spec's
>   per-metric sections are explicitly per-turn — "each bar represents a Turn"), so the range acts as
>   the filter window rather than driving the fixed 1-min/15-min/1-hr/6-hr bucket sizes in the
>   *Time-Axis Engine* section.
> - **Data.** Every rich tooltip figure the spec calls for (worst-case baseline cost, per-step model
>   attribution, cached/uncached token split, context token counts, TTFT cold-start split) is
>   **derived in `CostChartBuilder`** from the turn's existing fields and demonstrated on the
>   deterministic `MockData.BuildMetricHistory` corpus — the telemetry pipeline still doesn't capture
>   them directly (see [`../router/telemetry.md`](../router/telemetry.md) and [`backlog.md`](backlog.md)),
>   so live turns fall back to those derivations too.
> - **Animations.** Rendered in the "bold" style: staggered bar/line entrance, elastic donut growth,
>   rippling `effectScatter` alerts for token runaways and context breaches, and a pinned TTFT spike.

This document provides high-density, engineering-ready specifications for visualizing the operational
metrics of an agentic router.

---

## Global System Architecture

### Time-Axis Engine

The horizontal axis across all chart strategies dynamically buckets data based on the user's selected
time window.

- **Hour:** 1-minute granular buckets.
- **Day:** 15-minute granular buckets.
- **Week:** 1-hour granular buckets.
- **Month:** 6-hour granular buckets.
- **All / Custom:** Dynamic bucket sizing designed to maintain exactly 60 to 120 discrete data points
  on screen to avoid visual crowding.

### Interaction & Synchronization Layer

- **Synchronized Hover States:** When charts are displayed in a grid, hovering over a data point on
  one chart must instantly render a vertical crosshair rule (`rgba(255,255,255,0.15)`) at the exact
  same timestamp across all other active grids.
- **Model Color Locking:** Color mapping is bound directly to the unique model identifier, remaining
  completely static across all metric switches.

---

## Detailed Charting Strategies by Metric

### 1. Routing ROI (Cost Savings)

- **Primary Objective:** Immediately isolate whether the router's contextual multi-armed bandit
  choices are beating the baseline strategy of routing exclusively to the most powerful model.
- **Chart Format:** Dual-Directional Binned Bar Chart
- **Visual Layout:**
  - **Y-Axis Centerline:** Set firmly at 0.
  - **Positive Bars (Above 0):** Represent successful routing optimizations where an acceptable
    performance was achieved at a lower cost than the baseline.
  - **Negative Bars (Below 0):** Represent exploration failures, fallback triggers, or instances where
    a model failed a task and required costly remediation.
  - **Coloring:** Each individual vertical bar represents a Turn and is completely filled with the
    color of the specific model responsible.
- **Interaction Blueprint:** Hovering over a bar reveals a tooltip detailing the financial delta:

```
Turn #1042 (14:02:11) • Model: Claude 3.5 Sonnet
  • Actual Turn Cost: $0.04
  • Estimated Baseline Cost (GPT-4o): $0.12
  • Net ROI: +$0.08 (+200% Efficiency)
```

### 2. Total Turn Cost ($)

- **Primary Objective:** Audit the economic viability of full agentic Cognition-Action-Feedback
  (C-A-F) execution loops over time.
- **Chart Format:** Stepped Stacked Area Chart (Running Total)
- **Visual Layout:**
  - **Y-Axis:** Running total dollar amount ($) accumulated across the selected time window.
  - **X-Axis Trajectory:** A series of strict horizontal and vertical steps.
  - **Area Shading:** The vertical space underneath the line is completely filled. Because only one
    model executes per turn, the area color changes sharply at each vertical step to match the active
    model.
- **Interaction Blueprint:** Hovering over a vertical jump highlights that specific model block,
  dimming the rest of the canvas:

```
Turn #1043 (14:03:45) • Model: GPT-4o
  • Incremental Cost: +$0.24 (Heavy tool validation failures)
  • Window Cumulative Total: $14.82
```

### 3. Prompt + Completion Tokens

- **Primary Objective:** Provide early detection of "hockey stick" curves — the exponential token
  accumulation caused by infinite agentic loops and recursive debugging cycles.
- **Chart Format:** Stepped Area Chart with Logarithmic Y-Axis Toggle & Exponential Runaway Indicator
- **Visual Layout:**
  - **Default State:** A stepped area chart mapping cumulative token counts.
  - **The Hockey Stick Flag:** When an active agent loop exhibits an exponential token growth velocity
    (Δ Tokens / Δ Turn > 2.5x), the chart area changes from the model's native color to a
    high-contrast diagonal hashing pattern.
- **Interaction Blueprint:** Hovering over the runaway phase exposes the token breakdown to help
  engineers diagnose prompt inflation:

```
Turn #1044 (14:05:00) • Model: DeepSeek-V3 [RUNAWAY ALERT]
  • Tokens Added: +142,000 (Input: 138k, Output: 4k)
  • Cause: Recursive loop error trace injected into context 4 consecutive times.
```

### 4. Tool Execution Loop Count (Steps per Turn)

- **Primary Objective:** Quantify the operational complexity and heavy debugging steps occurring
  inside the agent's execution sandbox.
- **Chart Format:** Grouped Segmented Bar Chart
- **Visual Layout:**
  - **Structure:** Each discrete time interval is represented by a single vertical bar where height
    equals the step count.
  - **Segmentation:** If a turn switches sub-models mid-loop, the single bar is split horizontally into
    segmented blocks of color, visually tracking how many steps were handled by each model during that
    single transaction.
- **Interaction Blueprint:** Hovering over a multi-colored bar reveals the execution path:

```
Turn #1045 (14:12:30) • 8 Total Steps
  • Steps 1-3: Llama-3-70B (3 Planning Steps)
  • Steps 4-8: Claude 3.5 Sonnet (5 Sandbox Code Repair Steps)
```

### 5. Cache Hit Rate

- **Primary Objective:** Measure the engineering efficiency of prefix-cache aware routing and track
  how effectively repetitive system prompts or code repositories are being cached.
- **Chart Format:** 100% Horizontal Stacked Bar (Grid Row) or Stepped Line with Gradient Track
- **Visual Layout:**
  - **Structure:** A sharp stepped line tracking a percentage value from 0% to 100%.
  - **Background Shading:** The canvas background directly beneath the line is shaded with a
    light-to-dark gradient of the active model's color, where darker shades represent a high cache
    efficiency.
- **Interaction Blueprint:** Hovering over a data point reveals exactly what text chunks were hit or
  missed:

```
Turn #1046 (14:18:22) • Model: GPT-4o
  • Cache Hit Rate: 88.4%
  • Cached Tokens: 112,000 (Repository Manifest + Core System Prompt)
  • Uncached Tokens: 14,700 (New user modification prompt)
```

### 6. Time-to-First-Token (TTFT) / Routing Latency

- **Primary Objective:** Track system latency spikes. Because agentic workflows prioritize accuracy
  over raw speed, this chart highlights anomalies rather than average performance.
- **Chart Format:** Stepped Line Chart with Silhouette Background Zoning
- **Visual Layout:**
  - **Line Element:** A clean, unshaded stepped line tracking execution time in milliseconds.
  - **Background Canvas:** The canvas itself is split into vertical, colored "silhouette" slices. The
    background color of the slice matches the active model, allowing immediate correlation between a
    latency spike and the model's identity.
- **Interaction Blueprint:** Hovering over a massive latency spike reveals structural delay details:

```
Turn #1047 (14:22:15) • TTFT Spike Alert
  • Total Routing Latency: 4,850 ms
  • Model: Claude 3 Opus
  • Diagnostic: 450ms router classification overhead + 4,400ms upstream provider cold-start queue.
```

### 7. Context Buffer Margin (% of Context Window Used)

- **Primary Objective:** Monitor safety constraints to ensure agent loops do not hit hard context
  limits and trigger catastrophic execution failures.
- **Chart Format:** Stepped Line Chart with Dynamic Threshold Alerts
- **Visual Layout:**
  - **Line Element:** A thin stepped line tracking the exact percentage of the active model's maximum
    context window currently filled.
  - **The Red Line Constraint:** A permanent, dashed horizontal red rule is fixed at 90%. If the
    stepped line crosses this threshold, the line thickness doubles and begins pulsing.
- **Interaction Blueprint:** Hovering over a high-capacity boundary point alerts the engineer to
  imminent failure risks:

```
Turn #1048 (14:31:02) • Model: Gemini 1.5 Pro
  • Context Used: 92.1% (1,842,000 / 2,000,000 tokens)
  • System Status: Warning. Router configured to trigger an automated context pruning sweep on next
    turn step.
```

