// Sessions messages pane (#176 §4.3 / §6.2): stick-to-bottom follow, overflow probe for Show more,
// and an explicit dispose so @key tab teardown leaves no observer or .NET reference behind.
window.chatScroll = (function () {
  const states = new WeakMap();

  function getState(list) {
    return states.get(list);
  }

  function ensureState(list, dotNetRef) {
    let state = states.get(list);
    if (!state) {
      state = {
        dotNetRef: null,
        disposed: false,
        atBottom: true,
        scrollRaf: 0,
        resizeObserver: null,
        overflowingKeys: null,
        onScroll: null,
      };
      states.set(list, state);
    }
    if (dotNetRef) {
      state.dotNetRef = dotNetRef;
      state.disposed = false;
    }
    return state;
  }

  function isAtBottom(list) {
    return list.scrollHeight - list.scrollTop - list.clientHeight <= 24;
  }

  function reportAtBottom(list, state) {
    if (state.disposed || !state.dotNetRef || teardownIfDetached(list, state)) {
      return;
    }
    const next = isAtBottom(list);
    if (next === state.atBottom) {
      return;
    }
    state.atBottom = next;
    state.dotNetRef.invokeMethodAsync("OnAtBottomChanged", next);
  }

  // A list removed from the DOM (the pane's @key swaps it on a session change) must stop reporting: its
  // observers can still fire once on removal, and the shared .NET reference would apply that empty
  // measurement to the new session's rows.
  function teardownIfDetached(list, state) {
    if (list.isConnected) {
      return false;
    }
    dispose(list);
    return true;
  }

  function measureOverflow(list, state) {
    if (state.disposed || !state.dotNetRef || teardownIfDetached(list, state)) {
      return;
    }
    const bodies = list.querySelectorAll("[data-msg-key]");
    const overflowing = [];
    bodies.forEach(function (el) {
      el.classList.add("ls-msg-measure");
    });
    bodies.forEach(function (el) {
      if (el.scrollHeight > el.clientHeight + 0.5) {
        overflowing.push(el.getAttribute("data-msg-key"));
      }
    });
    bodies.forEach(function (el) {
      el.classList.remove("ls-msg-measure");
    });
    const key = overflowing.slice().sort().join("\0");
    if (key === state.overflowingKeys) {
      return;
    }
    state.overflowingKeys = key;
    state.dotNetRef.invokeMethodAsync("OnOverflowKeysChanged", overflowing);
  }

  function dispose(list) {
    if (!list) {
      return;
    }
    const state = getState(list);
    if (!state) {
      return;
    }
    state.disposed = true;
    if (state.scrollRaf) {
      cancelAnimationFrame(state.scrollRaf);
      state.scrollRaf = 0;
    }
    if (state.onScroll) {
      list.removeEventListener("scroll", state.onScroll);
      state.onScroll = null;
    }
    if (state.resizeObserver) {
      state.resizeObserver.disconnect();
      state.resizeObserver = null;
    }
    state.dotNetRef = null;
    states.delete(list);
  }

  return {
    observeOverflow: function (list, dotNetRef) {
      if (!list || !dotNetRef) {
        return;
      }
      const state = ensureState(list, dotNetRef);
      if (!state.onScroll) {
        state.onScroll = function () {
          if (state.scrollRaf) {
            return;
          }
          state.scrollRaf = requestAnimationFrame(function () {
            state.scrollRaf = 0;
            reportAtBottom(list, state);
          });
        };
        list.addEventListener("scroll", state.onScroll, { passive: true });
      }
      if (!state.resizeObserver) {
        state.resizeObserver = new ResizeObserver(function () {
          measureOverflow(list, state);
        });
        state.resizeObserver.observe(list);
      }
      measureOverflow(list, state);
      state.atBottom = isAtBottom(list);
    },

    scrollToBottom: function (list) {
      if (!list) {
        return;
      }
      list.scrollTop = list.scrollHeight;
      const state = getState(list);
      if (state) {
        state.atBottom = true;
      }
    },

    scrollIntoView: function (id) {
      if (!id) {
        return;
      }
      const el = document.getElementById(id);
      if (!el) {
        return;
      }
      el.scrollIntoView({ block: "nearest" });
      // Keyboard row navigation must move focus to the newly selected select button; otherwise
      // repeated ArrowUp/ArrowDown keep firing on the previous row with the old current index.
      const btn = el.querySelector(".ls-msg-select");
      if (btn && typeof btn.focus === "function") {
        btn.focus({ preventScroll: true });
      }
    },

    // Shared by the rail listbox and details tablist: move DOM focus after a roving-tabindex change.
    focusById: function (id) {
      if (!id) {
        return;
      }
      const el = document.getElementById(id);
      if (el && typeof el.focus === "function") {
        el.focus({ preventScroll: true });
      }
    },

    dispose: dispose,
  };
})();
