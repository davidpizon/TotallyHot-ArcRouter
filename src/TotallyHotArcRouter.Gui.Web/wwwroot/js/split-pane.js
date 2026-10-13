// Draggable / keyboard-resizable dividers for the Sessions three-pane layout (#176 §6.4).
// Blazor passes the container, divider, and resized panel; options pick left-edge (rail) or
// right-edge (details) math, clamps, defaults, and a localStorage key. Effective bounds fold in
// the CSS pixel minima so aria-valuenow always matches what the layout renders.
window.splitPane = {
  init: function (container, divider, panel, options) {
    if (!container || !divider || !panel || divider.dataset.splitInit) {
      return;
    }
    divider.dataset.splitInit = "1";

    options = options || {};
    const edge = options.edge === "right" ? "right" : "left";
    const minPctOpt = typeof options.minPct === "number" ? options.minPct : edge === "left" ? 15 : 20;
    const maxPctOpt = typeof options.maxPct === "number" ? options.maxPct : edge === "left" ? 35 : 40;
    const defaultPct = typeof options.defaultPct === "number" ? options.defaultPct : edge === "left" ? 22 : 28;
    const storageKey = options.storageKey || null;
    const minPx = typeof options.minPx === "number" ? options.minPx : edge === "left" ? 220 : 280;
    const siblingMinPx = typeof options.siblingMinPx === "number" ? options.siblingMinPx : edge === "left" ? 280 : 220;
    const middleMinPx = typeof options.middleMinPx === "number" ? options.middleMinPx : 360;
    const dividersPx = typeof options.dividersPx === "number" ? options.dividersPx : 24;

    divider.setAttribute("role", "separator");
    divider.setAttribute("aria-orientation", "vertical");
    divider.tabIndex = 0;

    function siblingPanel() {
      const kids = Array.prototype.slice.call(container.children);
      // Layout: panel0 | div0 | middle | div1 | panel1 — find the pane on the other edge.
      if (edge === "left") {
        return kids.length >= 5 ? kids[4] : null;
      }
      return kids.length >= 1 ? kids[0] : null;
    }

    function readStoredPct() {
      if (!storageKey) {
        return defaultPct;
      }
      try {
        const raw = localStorage.getItem(storageKey);
        if (raw == null) {
          return defaultPct;
        }
        const n = parseFloat(raw);
        return Number.isFinite(n) ? n : defaultPct;
      } catch (_) {
        return defaultPct;
      }
    }

    function writeStoredPct(pct) {
      if (!storageKey) {
        return;
      }
      try {
        localStorage.setItem(storageKey, String(pct));
      } catch (_) {
        /* private mode / quota — ignore */
      }
    }

    function bounds() {
      const W = container.getBoundingClientRect().width;
      if (W <= 0) {
        return { min: minPctOpt, max: maxPctOpt, W: 0 };
      }
      const sib = siblingPanel();
      const sibWidth = sib ? sib.getBoundingClientRect().width : siblingMinPx;
      const pctFloor = Math.max(minPx, (minPctOpt / 100) * W);
      const pctCeil = Math.min((maxPctOpt / 100) * W, W - dividersPx - middleMinPx - Math.max(sibWidth, siblingMinPx));
      let min = (pctFloor / W) * 100;
      let max = (pctCeil / W) * 100;
      if (min > max) {
        min = max = (minPx / W) * 100;
      }
      return { min: min, max: max, W: W };
    }

    function applyPct(pct) {
      const b = bounds();
      const clamped = Math.min(b.max, Math.max(b.min, pct));
      panel.style.width = clamped + "%";
      panel.style.flex = "0 0 auto";
      divider.setAttribute("aria-valuenow", String(Math.round(clamped)));
      divider.setAttribute("aria-valuemin", String(Math.round(b.min)));
      divider.setAttribute("aria-valuemax", String(Math.round(b.max)));
      writeStoredPct(clamped);
      return clamped;
    }

    function pctFromClientX(clientX) {
      const rect = container.getBoundingClientRect();
      if (rect.width === 0) {
        return defaultPct;
      }
      if (edge === "right") {
        return ((rect.right - clientX) / rect.width) * 100;
      }
      return ((clientX - rect.left) / rect.width) * 100;
    }

    applyPct(readStoredPct());

    let dragging = false;

    divider.addEventListener("pointerdown", function (e) {
      dragging = true;
      divider.setPointerCapture(e.pointerId);
      divider.classList.add("dragging");
      e.preventDefault();
    });

    divider.addEventListener("pointermove", function (e) {
      if (!dragging) {
        return;
      }
      applyPct(pctFromClientX(e.clientX));
    });

    const endDrag = function () {
      dragging = false;
      divider.classList.remove("dragging");
    };
    divider.addEventListener("pointerup", endDrag);
    divider.addEventListener("pointercancel", endDrag);

    divider.addEventListener("keydown", function (e) {
      const b = bounds();
      const current = parseFloat(panel.style.width) || defaultPct;
      const step = 2;
      if (e.key === "ArrowLeft") {
        e.preventDefault();
        applyPct(edge === "left" ? current - step : current + step);
      } else if (e.key === "ArrowRight") {
        e.preventDefault();
        applyPct(edge === "left" ? current + step : current - step);
      } else if (e.key === "Home") {
        e.preventDefault();
        applyPct(b.min);
      } else if (e.key === "End") {
        e.preventDefault();
        applyPct(b.max);
      }
    });

    if (typeof ResizeObserver !== "undefined") {
      const ro = new ResizeObserver(function () {
        const current = parseFloat(panel.style.width) || defaultPct;
        applyPct(current);
      });
      ro.observe(container);
    }
  },
};
