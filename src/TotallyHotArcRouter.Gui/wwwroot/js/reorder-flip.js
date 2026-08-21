// FLIP (First-Last-Invert-Play) reorder animation for drag-to-rank card lists (PriceSourcesAdmin).
// Blazor re-keys the list on drop rather than transforming it (see PriceSourcesAdmin.razor), so any
// card whose slot changed simply teleports to its new DOM position on render. capture() records where
// each card sits right before that render; play() - called after the render completes - measures where
// it landed, then animates from the old spot to the new one with a CSS transition. No animation
// library: same "JS drives layout, CSS drives the transition" split as splitPane.
window.reorderFlip = {
  _pending: null,

  capture: function (containerSelector, itemSelector) {
    var container = document.querySelector(containerSelector);
    if (!container) {
      this._pending = null;
      return;
    }

    var rects = {};
    container.querySelectorAll(itemSelector).forEach(function (el) {
      var key = el.getAttribute("data-flip-key");
      if (key) {
        rects[key] = el.getBoundingClientRect();
      }
    });
    this._pending = rects;
  },

  play: function (containerSelector, itemSelector, durationMs, easing) {
    var rects = this._pending;
    this._pending = null;
    if (!rects) {
      return;
    }

    var container = document.querySelector(containerSelector);
    if (!container) {
      return;
    }

    container.querySelectorAll(itemSelector).forEach(function (el) {
      var key = el.getAttribute("data-flip-key");
      var first = key ? rects[key] : null;
      if (!first) {
        return;
      }

      var last = el.getBoundingClientRect();
      var dx = first.left - last.left;
      var dy = first.top - last.top;
      if (dx === 0 && dy === 0) {
        return;
      }

      // Invert: jump to the old position with no transition, then clear the transform on the next
      // frame so the browser animates the return trip instead of the jump.
      el.style.transition = "none";
      el.style.transform = "translate(" + dx + "px, " + dy + "px)";
      // Reading a layout property forces the browser to apply the transform above before the
      // transition below is attached - otherwise the two style writes coalesce into one paint and
      // there is nothing to animate from.
      void el.getBoundingClientRect();

      el.style.transition = "transform " + durationMs + "ms " + easing;
      el.style.transform = "";

      el.addEventListener("transitionend", function onDone() {
        el.style.transition = "";
        el.removeEventListener("transitionend", onDone);
      });
    });
  },
};
