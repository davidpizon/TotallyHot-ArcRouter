# TotallyHot Arc Router Proxy — Quick Start

`TotallyHot Arc Router` is a .NET 10 console host that runs a local Kestrel-based
HTTP proxy. It sits in front of your coding-agent client, inspects each
request's `model` field, and forwards the request to the correct upstream
provider (OpenAI, Anthropic, Alibaba, Zhipu, Moonshot, MiniMax, ...) with the
right base URL and auth header attached — similar in spirit to LiteLLM's
`model_list` proxy.

This guide covers configuring and running the proxy from `src/TotallyHotArcRouter`.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- API keys for whichever upstream providers you intend to route to

## 1. Restore and build

```bash
cd src/TotallyHotArcRouter
dotnet restore
dotnet build
```

## 2. Set provider API keys

`ModelRouting:Providers` in `appsettings.json` is the add-provider template
catalog. The live provider list is `model-routing.json`; a first run with no
such file starts empty and does not copy those templates in. Authentication
is an ordinary entry in each provider's `Headers` list — a header whose
value is read from an environment variable at request time. Set only the
variables for the providers you plan to use:

```bash
# OpenAI, Qwen, GLM, Kimi, and MiniMax authenticate with Authorization. A raw key is
# sent as "Bearer <key>". A value that already starts with a scheme is forwarded unchanged.
export OPENAI_API_KEY="<your-openai-key>"
export QWEN_API_KEY="<your-alibaba-key>"
export GLM_API_KEY="<your-zhipu-key>"
export KIMI_API_KEY="<your-moonshot-key>"
export MINIMAX_API_KEY="<your-minimax-key>"

# Anthropic sends its raw key with no scheme prefix.
export ANTHROPIC_API_KEY="<your-anthropic-key>"
```

If a provider's key is missing, requests to models routed to that provider
are forwarded without an auth header.

See [Provider API keys](#provider-api-keys) below for the exact `Headers`
shape, including how to compose a scheme prefix (e.g. `Bearer`).

## 3. Configure routing

`src/TotallyHotArcRouter/appsettings.json` has two relevant sections:

- **`Routing`** — router policy defaults (`DefaultModel`, `MaxCandidates`,
  `MaxNeighborCount`, `EnableExploration`, `ExplorationRate`).
- **`ModelRouting`** — the proxy's allowlist. Only models listed in
  `ModelList` are routable; anything else gets a `400` response.

To add a model, add an entry under `ModelList` that points at an existing
provider key:

```json
{
  "ModelName": "my-alias",
  "Provider": "anthropic",
  "ProviderModelId": "claude-sonnet-4-6"
}
```

- `ModelName` is what the client sends in the request body's `model` field.
- `Provider` must match a key under `ModelRouting:Providers`.
- `ProviderModelId` is the identifier substituted into the forwarded request.

To add a new provider, add an entry under `ModelRouting:Providers`:

```json
"my-provider": {
  "BaseUrl": "https://api.my-provider.com",
  "AuthHeaderName": "Authorization",
  "Headers": [
    { "Name": "Authorization", "ValueEnvVar": "MY_PROVIDER_API_KEY" }
  ]
}
```

`AuthHeaderName` itself carries no credential — it only records which header
is "the" auth header, so the proxy can strip a client-sent header of the
same name before forwarding (see [Provider API keys](#provider-api-keys)
below for how the header's actual value is composed, including a scheme
prefix like `Bearer`).

### Provider base URLs

**`BaseUrl` is the provider's root — the part that is the same for every
endpoint. It is not the chat-completions URL.** What gets appended to it
depends on how the provider is reached:

| Provider style | Who supplies the path | Example `BaseUrl` | Forwarded URL |
| --- | --- | --- | --- |
| **Passthrough** (OpenAI, Ollama, LM Studio, and every OpenAI-compatible provider) | the client's own request path, forwarded as sent | `https://api.openai.com` | `https://api.openai.com/v1/chat/completions` |
| **Translated** (Gemini, Anthropic, Bedrock) | the provider's translator, via `IPayloadTranslator.BuildRequestUri` | `https://generativelanguage.googleapis.com` | `…/v1beta/models/{model}:generateContent` |

A path-less `BaseUrl` is correct for both styles and is what you should
write for a new provider. Two things make it not the only accepted form:

- **A gateway or reverse-proxy prefix is preserved.** If a provider is
  reached at `https://gw.corp/openai`, that `/openai` survives into the
  forwarded URL — the client's path is appended to it, not substituted for
  it.
- **A version segment you didn't need to write is tolerated, not
  duplicated.** Ollama and LM Studio are both *documented* with a `/v1`
  base (`http://localhost:11434/v1`), so operators reasonably copy that in,
  and `appsettings.json` ships it. But an OpenAI-shaped client already
  sends `/v1/chat/completions`, so naive joining would forward
  `/v1/v1/chat/completions`. Passthrough joining collapses segments the
  base and the request path *both* carry at the boundary between them, so
  `http://localhost:11434/v1` and `http://localhost:11434` forward to the
  same place.

Collapsing is exact (`/V1` does not match `/v1`) and only applies where the
base ends and the request path begins, so a segment that merely recurs
deeper in the path is never swallowed. Translated providers never reach
this logic at all — their translator builds the whole URL from `BaseUrl`,
which is why a `/v1beta` written into a Gemini `BaseUrl` would be appended
to, not merged with, the translator's own `/v1beta`.

The single implementation of this rule is
`ProviderUrlBuilder.BuildPassthroughUrl`; see
`docs/router/unified-api-translation.md` §4.1 for how it got here.

You can override any of these settings without editing `appsettings.json` by
using environment variables (ASP.NET Core configuration convention), e.g.:

```bash
export Routing__DefaultModel="glm-5"
```

### Provider API keys

Authentication is expressed purely as an entry in a provider's `Headers`
list — there is no dedicated credential field. Each header resolves its
value in this order:

1. **`Value`** — a literal value set directly on the header. If non-empty,
   it is used as-is and `ValueEnvVar` is not consulted.
2. **`ValueEnvVar`** — the name of an environment variable read at request
   time, used only when `Value` is empty or unset.
3. Neither set (or the named environment variable isn't present) — the
   header is omitted from the forwarded request.

```json
"my-provider": {
  "BaseUrl": "https://api.my-provider.com",
  "AuthHeaderName": "Authorization",
  "Headers": [
    { "Name": "Authorization", "ValueEnvVar": "MY_PROVIDER_API_KEY" }
  ]
}
```

An `Authorization` value that is only a raw credential is sent as
`Bearer` plus that value. A value that already starts with a scheme
(`Bearer sk-my-literal-key`) is forwarded unchanged. Other header names are
not rewritten: Anthropic's `x-api-key` and Gemini's `x-goog-api-key` stay
the raw key. See the matching entries in `appsettings.json`.

Prefer `ValueEnvVar` for anything checked into source control —
`appsettings.json` is typically committed to git, so a literal `Value`
belongs only in an untracked/local override file or a secret store, not in
the tracked base config.

The proxy only auto-loads `appsettings.json` plus
`appsettings.{DOTNET_ENVIRONMENT}.json` — there is no ad hoc
`appsettings.Local.json` convention wired up. To use an untracked override
file, set the `DOTNET_ENVIRONMENT` variable to match its name before
running, e.g. `appsettings.Development.json` requires
`DOTNET_ENVIRONMENT=Development` (already set for you by the `TotallyHotArcRouter`
launch profile in `Properties/launchSettings.json`).

**Note:** `Program.cs` builds the outer application host as a plain generic
host (`Host.CreateDefaultBuilder`), which reads `DOTNET_ENVIRONMENT` — not
`ASPNETCORE_ENVIRONMENT` — to decide whether to load
`appsettings.Development.json`. However, `ProxyServer` separately builds a
Kestrel web host (`ConfigureWebHostDefaults`) for the proxy's HTTP listener,
and *that* host does consult `ASPNETCORE_ENVIRONMENT`. To keep both hosting
stacks consistent, set both variables to the same value (the
`TotallyHotArcRouter` launch profile in `Properties/launchSettings.json` already
does this).

## 4. Run the proxy

```bash
dotnet run
```

By default, the proxy listens on `https://localhost:47101` - HTTPS by
default since Phase P7 of the web GUI migration, using a router-generated
local CA (see [`docs/router/client-tls-setup.md`](../docs/router/client-tls-setup.md)
for trusting it, or pass `curl --cacert`/`-k` for local testing without
trusting it system-wide). An opt-in, always-loopback plain-HTTP fallback
exists at `Proxy:PlainHttp:Enabled` (default port `47105`) for tools that
cannot be pointed at a custom CA - see `client-tls-setup.md`'s "opt-in
plain-HTTP listener" section.

Point an OpenAI-compatible client at **one** base URL —
`https://localhost:47101/v1` — and send `"model": "auto"` to let the router
pick. That pair is the shipped drop-in (see the repository
[`README.md`](../README.md#point-a-client)); a named `ModelList` alias still
works when you want a specific backend. The proxy forwards the path and query
string unchanged, rewrites the `model` field to the provider's
`ProviderModelId`, and injects the resolved auth header before sending the
request upstream.

## 5. Verify

With the proxy running, send a request (`-k` skips certificate verification
for local testing; drop it once you've trusted the local CA). `"model": "auto"`
is the drop-in; a configured `ModelList` alias still works when you want a
specific backend:

```bash
curl -k https://localhost:47101/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"hello"}]}'
```

An unconfigured model name returns a `400` with an `invalid_request_error`
body instead of being forwarded.

### Model discovery (`GET /v1/models`)

Many OpenAI-compatible clients (including VS Code's Copilot Chat BYOK
providers) call `GET /v1/models` before anything else, to discover which
models are available. Since this request has no body and your `ModelList`
can span multiple upstream providers, the proxy answers it locally from
configuration — mirroring how LiteLLM's proxy handles the same endpoint —
rather than forwarding it anywhere:

```bash
curl -k https://localhost:47101/v1/models
```

```json
{
  "object": "list",
  "data": [
    { "id": "gpt-5.4", "object": "model", "created": 0, "owned_by": "openai" },
    { "id": "kimi-k2.5", "object": "model", "created": 0, "owned_by": "moonshot" }
  ]
}
```

`id` is the client-facing `ModelName`, and `owned_by` is the configured
provider key. `created` is always `0` — there's no meaningful creation
timestamp for a statically configured route.

## Running with Docker

A `Dockerfile` is provided for containerized runs. Build from the **repository root**, not this directory - the
image also packages the web dashboard, a sibling project referenced from
`TotallyHotArcRouter.csproj`:

```bash
docker build -t totallyhot-arcrouter -f src/TotallyHotArcRouter/Dockerfile .
docker run -d --name arcrouter \
  -p 127.0.0.1:47101:47101 -p 127.0.0.1:47104:47104 \
  -v arcrouter-data:/data \
  -e ANTHROPIC_API_KEY="<your-anthropic-key>" \
  -e OPENAI_API_KEY="<your-openai-key>" \
  totallyhot-arcrouter
```

Only loopback-bound ports are published by default; see the `Dockerfile`'s
own header comment for the full first-time trust setup
(`--export-ca`/`--print-management-token` via `docker exec`) and
`docs/router/client-tls-setup.md`.

## Running tests

xUnit v3's own `dotnet test` integration has been unreliable in this repo across SDK updates.
Prefer building, then running the test **host** for the project you care about. On Windows the host
is `*.Tests.exe`; on Linux/macOS it is the extensionless `*.Tests` file (or `dotnet exec` the
`.dll`).

```bash
dotnet build src/TotallyHotArcRouter.Tests/TotallyHotArcRouter.Tests.csproj -c Release
# Windows
src/TotallyHotArcRouter.Tests/bin/Release/net10.0/TotallyHotArcRouter.Tests.exe
# Linux / macOS
src/TotallyHotArcRouter.Tests/bin/Release/net10.0/TotallyHotArcRouter.Tests
```

Sibling hosts follow the same pattern: `TotallyHotArcRouter.Quality.Tests`,
`TotallyHotArcRouter.Gui.Components.Tests`, `TotallyHotArcRouter.Gui.Admin.Tests`,
`TotallyHotArcRouter.Gui.Charts.Tests`, `TotallyHotArcRouter.Gui.Console.Tests`,
`TotallyHotArcRouter.Gui.Telemetry.Tests`, `TotallyHotArcRouter.Tray.Core.Tests`.
CI uses `dotnet test` with `TestingPlatformDotnetTestSupport` enabled in each test csproj.

