A single-color line-icon glyph, recolorable via CSS mask — use for any small functional icon (settings, search, close, chevrons, status glyphs).

```jsx
<Icon name="settings" size={18} color="var(--text-secondary)" />
```

Variants: pass any Heroicons Solid icon filename (from `github.com/tailwindlabs/heroicons`, `optimized/24/solid/`, e.g. `trash`, `cog-6-tooth`) directly as `name` if it's not in the curated list. Common uses in the dashboard: `settings` (header gear), `search` (conversation filter), `close` (modal dismiss), `chevron-down`/`chevron-right` (expand/collapse), `alert-triangle` (warning banner), `bot` (routing/voter model glyph).
