// Auto-scroll + smart-disengage behavior for the Console tab's log viewport (see
// docs/gui/console-tab-plan.md). scrollToBottom() is invoked from Blazor after a new log line
// renders while auto-scroll is enabled. init() wires a single wheel listener per viewport that
// detects the user scrolling upward with a mouse wheel or trackpad and calls back into .NET to
// switch auto-scroll off - the "Smart-Disengage" safeguard so incoming lines don't yank the view
// away while the user is reading an earlier entry.
window.consoleScroll = {
  init: function (viewport, dotNetRef) {
    if (!viewport || viewport.dataset.consoleScrollInit) {
      return;
    }
    viewport.dataset.consoleScrollInit = "1";

    viewport.addEventListener(
      "wheel",
      function (e) {
        if (e.deltaY < 0) {
          dotNetRef.invokeMethodAsync("OnManualScrollUp");
        }
      },
      { passive: true },
    );
  },
  scrollToBottom: function (viewport) {
    if (viewport) {
      viewport.scrollTop = viewport.scrollHeight;
    }
  },
};
