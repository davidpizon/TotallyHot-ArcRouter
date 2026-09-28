# Claude Code

Point Claude Code at Arc Router with one settings file. Claude Code speaks the Anthropic Messages
API. Its base URL is the router origin, **without** `/v1`, because Claude Code appends
`/v1/messages` itself.

Preset: [`claude-code.settings.json`](claude-code.settings.json).

## Config

Merge the preset's `env` block into the user settings file
(`~/.claude/settings.json`, or `%USERPROFILE%\.claude\settings.json` on Windows). If that file
already exists, add these three variables to its `env` object instead of replacing the file. Do
not put the block in a project's `.claude/settings.json`; that file is committed and shared.

```json
{
  "env": {
    "ANTHROPIC_BASE_URL": "https://localhost:47101",
    "ANTHROPIC_AUTH_TOKEN": "not-needed",
    "ANTHROPIC_MODEL": "auto"
  }
}
```

| Setting | Value | Why |
|---|---|---|
| `ANTHROPIC_BASE_URL` | `https://localhost:47101` | Gateway origin. Claude Code posts to `$ANTHROPIC_BASE_URL/v1/messages`. Including `/v1` here would request `/v1/v1/messages`. |
| `ANTHROPIC_AUTH_TOKEN` | `not-needed` | Sent as `Authorization: Bearer …`. The router does not check this header; the value only satisfies Claude Code, which will not start a gateway session without a credential. Use `ANTHROPIC_API_KEY` instead only if you need the key on `x-api-key`. |
| `ANTHROPIC_MODEL` | `auto` | Model id sent on each request. Behind a custom `ANTHROPIC_BASE_URL`, Claude Code forwards that string as given. |

A shell export of the same three variables works for one terminal session. A settings-file `env`
block wins when both are set, and it is what background sessions read. Confirm with `/status`:
the Anthropic base URL line should show `https://localhost:47101`.

Trust the router's local CA first ([client TLS setup](../../router/client-tls-setup.md)). Claude Code
is a Node process; if it ignores the OS store, set `NODE_EXTRA_CA_CERTS` to `router-ca.crt`.

## Limitation

Claude Code sends Anthropic Messages bodies to `POST /v1/messages`. Arc Router already accepts
that path. It rewrites `model` to the selected backend's id and, when that backend is Anthropic,
forwards the rest of the Messages body unchanged. `"model": "auto"` is a routing request, so the
chosen backend can be something else. This router does not translate a native Messages body into
another provider's API. A turn whose route is not Anthropic fails unless that backend accepts the
Anthropic body on the forwarded path. This preset does not add that translation.

Two Claude Code behaviors change whenever `ANTHROPIC_BASE_URL` is not `api.anthropic.com`: Remote
Control stays off, and MCP tool search stays off unless you set `ENABLE_TOOL_SEARCH=true`. The
router does not implement Claude Code's `tool_reference` blocks, so leave tool search off.

## Sources

Checked 2026-09-28.

- [Connect Claude Code to an LLM gateway](https://code.claude.com/docs/en/llm-gateway-connect) — `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, the settings-file `env` block, and the `$ANTHROPIC_BASE_URL/v1/messages` probe.
- [Environment variables](https://code.claude.com/docs/en/env-vars) — `ANTHROPIC_MODEL`, `ANTHROPIC_API_KEY`, Remote Control, and tool search on a non-first-party host.
- [Model configuration](https://code.claude.com/docs/en/model-config) — a custom base URL passes the model string through; `ANTHROPIC_MODEL` outranks the `model` setting.
