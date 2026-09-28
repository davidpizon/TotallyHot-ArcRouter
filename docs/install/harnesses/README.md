# Harness presets

Ready-made setups that point a coding harness at a default Arc Router install. Each one uses the
[OpenAI-compatible drop-in](../openai-compatible-drop-in.md) — loopback base URL, placeholder API key,
`"model": "auto"` — except where that harness's own API shape requires a different base URL. Those
differences are stated in the guide, not papered over.

The router does not authenticate LLM forwarding. `not-needed` is a placeholder for clients that
refuse an empty key. Trust the router's local CA before the first HTTPS call; see
[client TLS setup](../../router/client-tls-setup.md).

| Harness | Guide | Preset file |
|---|---|---|
| Claude Code | [claude-code.md](claude-code.md) | [claude-code.settings.json](claude-code.settings.json) |
| Cursor | [cursor.md](cursor.md) | none — Cursor has no project file for this setting |
| Codex | [codex.md](codex.md) | [codex.config.toml](codex.config.toml) |
| Aider | [aider.md](aider.md) | [aider.conf.yml](aider.conf.yml) |

Checked against each vendor's docs on 2026-09-28. The citation list is at the bottom of each guide.
