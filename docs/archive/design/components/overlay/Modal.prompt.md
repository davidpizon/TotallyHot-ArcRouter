A centered modal dialog — used for Settings, including its "Destructive Actions Zone" pattern.

```jsx
<Modal open={open} title="Settings" onClose={() => setOpen(false)}>
  <p>Body content…</p>
</Modal>
```

Click-outside (backdrop) closes it; content clicks are stopped from propagating. No entrance animation in the source — it simply appears.

This is the shell **every new window in the app matches** — new modals and dialogs reuse it rather than styling their own chrome. In the Blazor source that means copying `SettingsModal.razor`'s backdrop/panel/header markup verbatim (`ProviderEditDialog.razor` already does); see `docs/gui/DESIGN.md` §4.1 for the exact contract.
