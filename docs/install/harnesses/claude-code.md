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

## Cheaper models for helpers and light subagents

With `"model": "auto"`, Arc Router can send Claude Code's helper requests (session titles, summaries) and its
`Explore` and `claude-code-guide` subagents to a cheaper model. The two paths are not the same safeguard:
helpers use the utility rule (cost-aware, with the absolute quality floor; an unscored candidate can win on a
cold start), while `Explore` and `claude-code-guide` additionally require a known score close to the best
candidate's. Every other subagent routes as it would without the feature. The rules and options are in
[Route classes and options](../../router/utility-model-routing.md#route-classes-and-options-issue-163).

- **Turn the hint headers on.** Claude Code sends `x-claude-code-request-class` and `x-claude-code-agent-type` to a
  gateway only on version 2.1.273 or later, and only when `CLAUDE_CODE_GATEWAY_HINT_HEADERS=1` is set. On an older
  build the variable does nothing and every subagent routes normally. Add it to the same `env` block as the three
  variables above. Without it the router sees only `x-claude-code-agent-id`, which marks a subagent of unknown type:
  every subagent then routes normally and no helper is detected. That is safe but saves nothing.
- **List the auto-mode classifier's model in the router.** In auto mode, Claude Code decides whether an action may
  run with a classifier that uses Claude Sonnet 5 by default. Claude Code's gateway guide lists classifiers under the
  same `auxiliary` request class as titles. Whether the auto-mode classifier's own request carries it has not been
  confirmed, so treat this as a precaution. The router keeps such a request off the cheap path because it names its
  model instead of `auto`. A name the
  router does not know is still auto-routed (without the cheap bias), so the verdict could be served by whichever
  model the router picks. A configured name is an explicit pick and is served as asked while that model stays
  enabled and present on an enabled provider; if it is disabled, missing from the latest endpoint scan, or its
  provider is stopped, existing substitution still applies. Add `claude-sonnet-5` to your model list.
- **Don't point the model variables the classifier falls back to at `auto`.** The classifier falls back to the
  session's model or an Opus model in some cases. If `ANTHROPIC_DEFAULT_SONNET_MODEL` or
  `ANTHROPIC_DEFAULT_OPUS_MODEL` is `auto`, a classifier request that carries the `auxiliary` class would be routed
  as a helper, because the headers cannot tell it apart from a title. The preset leaves both unset. Note that the preset's `ANTHROPIC_MODEL` is `auto`, so
  a classifier that falls back to the session's model has the same exposure. If that matters to you, set
  `Routing:SubagentBias:ClaudeCodeHintHeaders` to `false`, which turns the helper and light-subagent bias off
  entirely.

The preset itself is unchanged by this: it does not set `CLAUDE_CODE_GATEWAY_HINT_HEADERS`, so the bias stays off
until you add it.

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
router forwards the rest of the Messages body unchanged, except for the fields described under
[Models that reject what Claude Code sends](#models-that-reject-what-claude-code-sends).

There is no supported end-to-end translation of that native Messages turn onto another provider.
A Gemini or Bedrock candidate still runs its request and response translator on `/v1/messages`;
only the Anthropic translator skips that path. Those translators read an OpenAI chat-completions
body and write an OpenAI-shaped response, which is the wrong shape for Claude Code. `"model":
"auto"` can select one of those backends. This preset does not add a Messages round trip.

Two Claude Code behaviors change whenever `ANTHROPIC_BASE_URL` is not `api.anthropic.com`: Remote
Control stays off, and MCP tool search stays off unless you set `ENABLE_TOOL_SEARCH=true`. The
router does not implement Claude Code's `tool_reference` blocks, so leave tool search off.

## Models that reject what Claude Code sends

With `"model": "auto"`, Claude Code assumes a current Claude model and sends adaptive thinking,
`output_config.effort` and `context_management`. Some Claude models reject some of these. Claude
Haiku 4.5, for example, rejects adaptive thinking and effort with a 400, and Claude Code then turns
the feature off for the rest of the conversation.

When the router picks a model for a `/v1/messages` request, it removes those fields from the copy sent
to a model whose own capability record says it rejects them. It keeps every other field, and a
failover to a capable model still gets the full request. The response's
`X-ArcRouter-Stripped-Features` header names what was removed. A model you name explicitly is sent the
request as Claude Code wrote it.

The records come from the provider's own model list, so the router needs one scan first. After
upgrading, open **Governance > Providers** and click **Refresh** on the `anthropic` provider once.
Each model row then shows **Thinking**, **Effort** and **Context mgmt** badges. A provider is scanned
only when you save it or refresh it. Until then nothing is removed, and Claude Code's own retry handles
any rejection. Decision record:
[ADR-0022 Amendment 1](../../adr/0022-route-harness-subagent-and-helper-traffic-by-kind.md#amendment-1-2026-10-02-strip-what-the-picked-model-rejects).

## Sources

Checked 2026-09-28.

- [Connect Claude Code to an LLM gateway](https://code.claude.com/docs/en/llm-gateway-connect) — `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, the settings-file `env` block, and the `$ANTHROPIC_BASE_URL/v1/messages` probe.
- [Environment variables](https://code.claude.com/docs/en/env-vars) — `ANTHROPIC_MODEL`, `ANTHROPIC_API_KEY`, Remote Control, and tool search on a non-first-party host.
- [Model configuration](https://code.claude.com/docs/en/model-config) — a custom base URL passes the model string through; `ANTHROPIC_MODEL` outranks the `model` setting.
- [BoringSSL `crypto/x509/v3_ncons.cc`](https://github.com/google/boringssl/blob/main/crypto/x509/v3_ncons.cc), checked 2026-10-02 — `nc_match_single` has no case for IP-address constraints and returns `X509_V_ERR_UNSUPPORTED_CONSTRAINT_TYPE`.
