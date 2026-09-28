# Codex

Codex reads provider settings only from the user config, `~/.codex/config.toml` (or
`$CODEX_HOME/config.toml`). A project `.codex/config.toml` ignores `model_provider`,
`model_providers`, and `openai_base_url`. Copy the keys; do not rely on a checked-in project file.

Preset: [`codex.config.toml`](codex.config.toml).

## Config

```toml
model = "auto"
model_provider = "arcrouter"

[model_providers.arcrouter]
name = "TotallyHot Arc Router"
base_url = "https://localhost:47101/v1"
env_key = "OPENAI_API_KEY"
wire_api = "responses"
```

`arcrouter` is a custom provider id. `openai`, `ollama`, and `lmstudio` are reserved and cannot
be redefined. `env_key` names an environment variable; it does not store the key. In the
environment Codex inherits:

```bash
export OPENAI_API_KEY=not-needed
```

`not-needed` is the [drop-in](../openai-compatible-drop-in.md) placeholder. `requires_openai_auth`
is left unset, so it stays false and Codex does not require a ChatGPT login. The built-in
`openai` provider's `openai_base_url` shortcut is the wrong knob here: that provider expects
OpenAI authentication, and this router does not.

`model = "auto"` is the model id Codex sends. `wire_api = "responses"` is required. The
configuration reference lists `responses` as the only supported value, and it is the default
when the key is omitted. The preset sets it so a copied `chat` value from an older guide is
not left in place.

Trust the router's local CA first ([client TLS setup](../../router/client-tls-setup.md)). The Codex
CLI uses the OS trust store.

## Limitation

Codex posts to `{base_url}/responses`, which is `https://localhost:47101/v1/responses`. Arc
Router does not translate the Responses API into Chat Completions. It rewrites `model`, then
forwards the client path for a backend that is not reshaped onto its own URL. That backend has
to accept a Responses body. A backend that only implements `/v1/chat/completions` will not
complete a Codex turn, and a translated backend (Anthropic, Gemini, Bedrock) reshapes the body
as if it were Chat Completions, which is not a Responses exchange. Setting `wire_api = "chat"`
does not fix that; current Codex rejects every value other than `responses`.

## Sources

Checked 2026-09-28.

- [Advanced configuration](https://developers.openai.com/codex/config-advanced) — custom `model_providers`, reserved ids, `env_key`, and why `openai_base_url` is only for the built-in OpenAI provider. Also the list of keys a project config ignores.
- [Configuration reference](https://developers.openai.com/codex/config-reference) — `model`, `model_provider`, `base_url`, `env_key`, and `wire_api` (`responses` only).
- [Authentication](https://developers.openai.com/codex/auth) — `env_key` versus `requires_openai_auth`, and a provider with neither requiring no authentication.
