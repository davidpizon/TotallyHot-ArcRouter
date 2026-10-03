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

Trust the router's local CA first ([client TLS setup](../../router/client-tls-setup.md)). Keep
`localhost` in the base URL. The native Claude Code build rejects the router's certificate on an
IP-literal URL such as `https://127.0.0.1:47101`; see
[`UNSUPPORTED_CONSTRAINT_TYPE` on the HTTPS port](#unsupported_constraint_type-on-the-https-port).

## `UNSUPPORTED_CONSTRAINT_TYPE` on the HTTPS port

**Symptom.** Every request fails, and the router logs nothing. `claude -p` prints
`API Error: Unable to connect to API (UNSUPPORTED_CONSTRAINT_TYPE)`. The `--debug` log shows only
`API error (attempt N/11): undefined Connection error.`, even though it also says
`CA certs: Appended extra certificates from NODE_EXTRA_CA_CERTS`.

**Affected builds.** The native Claude Code build: the installer's `claude.exe` and the CLI that the
Claude desktop app bundles under `%APPDATA%\Claude\claude-code\<version>\`. Both are Bun executables,
not Node. Seen on 2026-10-02 with 2.1.246 and 2.1.286.

**Cause.** The router's local CA limits itself to `localhost`, `127.0.0.1` and `::1` with an RFC 5280
name constraint ([ADR-0013](../../adr/0013-name-constrained-local-ca-for-router-tls.md)). Bun checks
certificates with BoringSSL, which does not implement name constraints on IP addresses. A leaf that
lists an IP address under this CA fails there with `X509_V_ERR_UNSUPPORTED_CONSTRAINT_TYPE`. Trust is
not the problem, so neither `NODE_EXTRA_CA_CERTS` nor the OS store changes the result.

The router therefore serves two leaves
([ADR-0013 Amendment 1](../../adr/0013-name-constrained-local-ca-for-router-tls.md#amendment-1-2026-10-02-an-sni-selected-dns-only-leaf-for-boringssl-clients)).
A client that dials `localhost` gets a leaf naming only `localhost`, which Bun accepts. A client that
dials `127.0.0.1` or `[::1]` gets the leaf that lists those addresses, which Bun still rejects.

**Fix.** Use `https://localhost:47101`, never an IP literal.

**Router builds without the two-leaf fix** serve the IP-bearing leaf to every client, so Claude Code
fails on every HTTPS URL. On those builds, use the router's loopback-only plain-HTTP listener:

1. Enable it in the operator overlay, `%ProgramData%\TotallyHotArcRouter\appsettings.local.json`.
   Merge this into the existing file, then restart the router:

   ```json
   {
     "Proxy": {
       "PlainHttp": { "Enabled": true }
     }
   }
   ```

2. Set `ANTHROPIC_BASE_URL` to `http://localhost:47105` instead of `https://localhost:47101`. If you
   moved the ports, set `Proxy:PlainHttp:Port` and use that port.

That listener binds loopback only and serves LLM-proxy routes only. Traffic to it is unencrypted on
the loopback hop, and the router logs a warning at startup while it is enabled. Do not set
`NODE_TLS_REJECT_UNAUTHORIZED=0` to get past the error, because that turns off certificate checks for
every host Claude Code talks to, not only the router.

## Limitation

Claude Code sends Anthropic Messages bodies to `POST /v1/messages`. Arc Router accepts that path
and rewrites `model` to the selected backend's id. When the selected backend is Anthropic, the
router leaves the rest of the Messages body unchanged and forwards it.

There is no supported end-to-end translation of that native Messages turn onto another provider.
A Gemini or Bedrock candidate still runs its request and response translator on `/v1/messages`;
only the Anthropic translator skips that path. Those translators read an OpenAI chat-completions
body and write an OpenAI-shaped response, which is the wrong shape for Claude Code. `"model":
"auto"` can select one of those backends. This preset does not add a Messages round trip.

Two Claude Code behaviors change whenever `ANTHROPIC_BASE_URL` is not `api.anthropic.com`: Remote
Control stays off, and MCP tool search stays off unless you set `ENABLE_TOOL_SEARCH=true`. The
router does not implement Claude Code's `tool_reference` blocks, so leave tool search off.

## Sources

Checked 2026-09-28.

- [Connect Claude Code to an LLM gateway](https://code.claude.com/docs/en/llm-gateway-connect) — `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, the settings-file `env` block, and the `$ANTHROPIC_BASE_URL/v1/messages` probe.
- [Environment variables](https://code.claude.com/docs/en/env-vars) — `ANTHROPIC_MODEL`, `ANTHROPIC_API_KEY`, Remote Control, and tool search on a non-first-party host.
- [Model configuration](https://code.claude.com/docs/en/model-config) — a custom base URL passes the model string through; `ANTHROPIC_MODEL` outranks the `model` setting.
- [BoringSSL `crypto/x509/v3_ncons.cc`](https://github.com/google/boringssl/blob/main/crypto/x509/v3_ncons.cc), checked 2026-10-02 — `nc_match_single` has no case for IP-address constraints and returns `X509_V_ERR_UNSUPPORTED_CONSTRAINT_TYPE`.
