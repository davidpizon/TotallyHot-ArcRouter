// Geometry and pointer tracking for the drag-to-rank card list (PriceSourcesAdmin). Three related
// jobs, all of the "JS reads the DOM, CSS drives the transition" shape splitPane already uses - no
// animation library:
//
//   SETTLE (capture/play) - FLIP the list into its new order, both mid-drag and on drop.
//   DRAG   (startDrag)    - track the pointer, move the dragged card, decide when the order changes.
//   LIFT   (_beginLift)   - detach the dragged card from its scroll container so it can follow the
//                           cursor over the rest of the app without being clipped.
//
// They live in one object because they are ordering-coupled: capture() must run immediately before
// the reorder it is animating, and the pinned card must be excluded from play() (see below).
//
// Ownership split with Blazor: JS owns the dragged card's POSITION frame to frame, because routing
// pointermove through interop at 60-120Hz would visibly lag the card behind the cursor. Blazor still
// owns the ORDER - JS only calls back when the target index actually changes, which is a handful of
// times per drag. Blazor also owns every DOM element here; this file never inserts, removes, or
// reorders nodes, only reads rects and writes inline styles.
//
// Both settle and drag address the SLOT wrapper (.ds-card-slot, which carries data-flip-key), never
// the card itself. Slots are always in flow, so they always measure true layout even while the card
// inside one is detached. See DESIGN.md §5.5.
window.reorderFlip = {
  // ---------------------------------------------------------------------------------------------
  // SETTLE
  // ---------------------------------------------------------------------------------------------

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

    // The dragged card's own slot must never be transformed: a transform on an ancestor establishes a
    // containing block for position: fixed descendants, which would yank the pinned card back into the
    // clip chain it was detached from (DESIGN.md §5.5). Skipping it costs nothing visually - that slot
    // is an empty gap while its card is under the cursor.
    var skipKey = this._drag && this._drag.lifted ? this._drag.key : null;

    container.querySelectorAll(itemSelector).forEach(function (el) {
      var key = el.getAttribute("data-flip-key");
      if (!key || key === skipKey) {
        return;
      }

      var first = rects[key];
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

  // ---------------------------------------------------------------------------------------------
  // DRAG + LIFT
  // ---------------------------------------------------------------------------------------------

  // Largest grow, per side, in px, once the card is lifted. A pixel target rather than a percentage:
  // scale(1.02) grows 1% of the card's own width per side, which on a wide window is ~14px and lands
  // outside the app window. This renders identically at every window size instead.
  MAX_LIFT_PX: 10,

  // How far .card-lifted's box-shadow reaches sideways past the card's box: `blur / 2 + spread`, so
  // `0 12px 28px -12px` gives `14 - 12` = 2. Subtracted from the measured budget below so the shadow,
  // not just the card's own edge, stays inside the window. RECOMPUTE if that shadow value changes.
  SHADOW_REACH_PX: 2,

  // Pointer travel before a press becomes a drag. Below this a press is a click - which matters
  // because pointerdown on the enable/disable toggle bubbles up to the card, and lifting on that
  // would make every toggle click flicker.
  DRAG_THRESHOLD_PX: 3,

  // How long the card takes to settle into its slot on release, and with what curve. Kept in sync
  // with --dur-default / --ease-settle; passed to CSS via .card-dropping rather than read from it.
  DROP_MS: 200,

  _drag: null,

  /**
   * Begins a drag on the card whose slot carries data-flip-key === key. Called from Blazor's
   * pointerdown handler. Does NOT lift anything yet - the press has to travel DRAG_THRESHOLD_PX first.
   * Everything the drag needs is measured once here: heights do not change mid-drag, and re-measuring
   * a list that is currently animating would read the in-flight positions rather than the real ones.
   */
  startDrag: function (containerSelector, itemSelector, key, pointerY, dotNetRef) {
    this._teardown();

    var container = document.querySelector(containerSelector);
    if (!container) {
      return;
    }

    var slots = Array.prototype.slice.call(container.querySelectorAll(itemSelector));
    if (slots.length < 2) {
      return;
    }

    var slot = null;
    var index = -1;
    for (var i = 0; i < slots.length; i++) {
      // Compared by iterating rather than interpolated into a selector: source names are arbitrary
      // strings and would need CSS.escape. capture() addresses items the same way.
      if (slots[i].getAttribute("data-flip-key") === key) {
        slot = slots[i];
        index = i;
        break;
      }
    }

    var card = slot ? slot.querySelector(".ds-surface-card-draggable") : null;
    if (!card) {
      return;
    }

    var rect = slot.getBoundingClientRect();
    var firstRect = slots[0].getBoundingClientRect();
    var secondRect = slots[1].getBoundingClientRect();

    // The stack's row gap, measured rather than assumed - it comes from --space-card-gap, and reading
    // it from the layout means this keeps working if that token changes.
    var gap = Math.max(0, secondRect.top - (firstRect.top + firstRect.height));

    // Heights of every OTHER slot, in order. Their order relative to each other never changes during a
    // drag (only the dragged card moves through them), so one measurement stays valid throughout.
    var others = [];
    for (var j = 0; j < slots.length; j++) {
      if (slots[j] !== slot) {
        others.push(slots[j].getBoundingClientRect().height);
      }
    }

    var self = this;
    var drag = {
      containerSelector: containerSelector,
      itemSelector: itemSelector,
      key: key,
      slot: slot,
      card: card,
      dotNetRef: dotNetRef,
      index: index,
      lifted: false,
      startY: pointerY,
      // Where inside the card the grab happened, so the card doesn't snap its top edge to the cursor.
      grabOffsetY: pointerY - rect.top,
      left: rect.left,
      width: rect.width,
      height: rect.height,
      listTop: firstRect.top,
      gap: gap,
      others: others,
    };

    drag.onMove = function (e) {
      self._onMove(e);
    };
    drag.onUp = function (e) {
      self._onUp(e);
    };

    this._drag = drag;

    // On document, not on the card or the stack: the drag has to survive the pointer leaving the list
    // entirely (and the card itself is pointer-transparent once lifted). This is also why the list no
    // longer cancels a drag on pointerleave the way it used to - leaving is no longer an ending.
    document.addEventListener("pointermove", drag.onMove);
    document.addEventListener("pointerup", drag.onUp);
    document.addEventListener("pointercancel", drag.onUp);
  },

  _onMove: function (e) {
    var drag = this._drag;
    if (!drag) {
      return;
    }

    // The tab subtree can be torn down mid-drag (a tab switch re-keys it). Blazor's Dispose is
    // synchronous and cannot await interop, so this is what actually stops the listeners leaking.
    if (!document.contains(drag.card)) {
      this._teardown();
      return;
    }

    if (!drag.lifted) {
      if (Math.abs(e.clientY - drag.startY) < this.DRAG_THRESHOLD_PX) {
        return;
      }
      this._beginLift();
    }

    var top = e.clientY - drag.grabOffsetY;
    drag.card.style.top = top + "px";

    var index = this._targetIndex(top);
    if (index !== drag.index) {
      drag.index = index;
      // Captured immediately before the reorder it animates, while the list is still in its old
      // arrangement. Blazor re-renders in response to MoveDraggedTo, then calls play() from
      // OnAfterRenderAsync - so the other cards glide to their new slots instead of jumping.
      this.capture(drag.containerSelector, drag.itemSelector);
      drag.dotNetRef.invokeMethodAsync("MoveDraggedTo", index);
    }
  },

  /**
   * Detaches the card: holds its row open at the measured height, pins the card over that row in
   * viewport coordinates, and sizes the grow against however much room there actually is before the
   * window edge. One synchronous block, so no intermediate layout is ever painted.
   */
  _beginLift: function () {
    var drag = this._drag;
    drag.lifted = true;

    // Before the card leaves the flow, or the list below it jumps up by a card's height.
    drag.slot.style.height = drag.height + "px";

    // Measured, not assumed: today the room comes from <main>'s 12px padding, but nothing here should
    // depend on knowing that. Three-way clamp - never more than MAX_LIFT_PX, never more than the
    // budget leaves once the shadow has taken its share, never more than 1% per side so scale(1.02)
    // stays the ceiling and a narrow window cannot produce a cartoonish grow.
    var budget = Math.min(drag.left, window.innerWidth - (drag.left + drag.width));
    var lift = Math.max(
      0,
      Math.min(this.MAX_LIFT_PX, budget - this.SHADOW_REACH_PX, 0.01 * drag.width),
    );
    var scale = drag.width > 0 ? 1 + (2 * lift) / drag.width : 1;

    // Unrounded on purpose: rounding to whole pixels guarantees a visible jump on pickup. Left and
    // width are frozen at their measured values - this is a vertical list, so the drag is Y-only and
    // the card never moves or resizes horizontally.
    drag.card.style.left = drag.left + "px";
    drag.card.style.width = drag.width + "px";
    drag.card.style.height = drag.height + "px";
    drag.card.style.setProperty("--lift-scale", scale);

    // Blazor also renders this class off its own drag state, and must - it rewrites the whole class
    // attribute on the render DragStarted triggers below, which would otherwise strip a JS-only class
    // straight back off. Setting it here as well is purely so the card detaches on this frame instead
    // of an interop round-trip later; the two writes agree, so whichever lands second is a no-op.
    drag.card.classList.add("card-pinned");

    drag.dotNetRef.invokeMethodAsync("DragStarted");
  },

  /**
   * Which index the dragged card is currently closest to, by its top edge.
   *
   * This is the variable-height generalisation of the usual `round(y / rowHeight)`: with rows of
   * differing heights there is no single row height to divide by, so the candidate positions are
   * accumulated from the real heights of the other slots instead, and the nearest one wins.
   */
  _targetIndex: function (cardTop) {
    var drag = this._drag;
    var y = drag.listTop;
    var best = 0;
    var bestDistance = Math.abs(cardTop - y);

    for (var k = 0; k < drag.others.length; k++) {
      y += drag.others[k] + drag.gap;
      var distance = Math.abs(cardTop - y);
      if (distance < bestDistance) {
        bestDistance = distance;
        best = k + 1;
      }
    }

    return best;
  },

  _onUp: function () {
    var drag = this._drag;
    if (!drag) {
      return;
    }

    // A press that never travelled far enough to lift: a click, not a drag. Nothing was detached and
    // nothing was reordered, so there is nothing to settle or commit.
    if (!drag.lifted) {
      this._teardown();
      return;
    }

    document.removeEventListener("pointermove", drag.onMove);
    document.removeEventListener("pointerup", drag.onUp);
    document.removeEventListener("pointercancel", drag.onUp);

    var self = this;
    var card = drag.card;
    var slot = drag.slot;

    if (!document.contains(card)) {
      this._teardown();
      return;
    }

    // Settle into the slot rather than snapping: the card is wherever the cursor left it, which can be
    // most of a row away from where it belongs. .card-dropping is what adds `top` to the transition
    // list - during the drag `top` is deliberately untransitioned so the card tracks the cursor 1:1.
    var target = slot.getBoundingClientRect();
    card.classList.add("card-dropping");
    card.style.top = target.top + "px";
    card.style.setProperty("--lift-scale", 1);

    var released = false;
    var release = function () {
      if (released) {
        return;
      }
      released = true;
      card.removeEventListener("transitionend", release);
      self._release(card, slot);
      if (self._drag === drag) {
        self._drag = null;
      }

      // Told only now, not on pointerup. EndDrag clears the component's drag state, which re-renders
      // the card WITHOUT .card-pinned - so announcing the release before the settle finished would
      // drop the card back into the flow mid-animation, still carrying viewport top/left, and fling it
      // out of the list. Delaying the commit by the settle costs nothing the user can perceive; the
      // working order stays on screen throughout either way.
      drag.dotNetRef.invokeMethodAsync("EndDrag");
    };

    card.addEventListener("transitionend", release);
    // transitionend does not fire when there is nothing to animate - a drop with the card already
    // exactly on its slot - so the card would stay detached forever without this.
    window.setTimeout(release, this.DROP_MS + 50);
  },

  /** Returns one card to the flow and its slot to auto height. Idempotent. */
  _release: function (card, slot) {
    card.classList.remove("card-pinned");
    card.classList.remove("card-dropping");
    card.style.left = "";
    card.style.top = "";
    card.style.width = "";
    card.style.height = "";
    card.style.removeProperty("--lift-scale");
    slot.style.height = "";
  },

  /**
   * Abandons any drag immediately, with no settle animation. The hard-stop path: a fresh drag starting
   * while one is somehow still live, or the card leaving the DOM under us.
   */
  _teardown: function () {
    var drag = this._drag;
    this._drag = null;
    if (!drag) {
      return;
    }

    document.removeEventListener("pointermove", drag.onMove);
    document.removeEventListener("pointerup", drag.onUp);
    document.removeEventListener("pointercancel", drag.onUp);

    if (drag.lifted) {
      this._release(drag.card, drag.slot);
    }
  },
};
