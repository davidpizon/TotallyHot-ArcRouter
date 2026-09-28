# Aider

Aider speaks OpenAI Chat Completions, which is the router's drop-in. The model id on the wire is
`auto`. Aider's own name for that model is `openai/auto`: the `openai/` prefix selects the
OpenAI-compatible client, and the suffix is what Aider sends.

Preset: [`aider.conf.yml`](aider.conf.yml). Copy it to `.aider.conf.yml` in your home directory,
the git root, or the directory you launch Aider from (later files win). Or pass it explicitly:

```bash
aider --config path/to/aider.conf.yml
```

## Config

```yaml
openai-api-base: https://localhost:47101/v1
openai-api-key: not-needed
model: openai/auto
weak-model: openai/auto
editor-model: openai/auto
edit-format: diff
show-model-warnings: false
```

The same endpoint and key as environment variables, from Aider's OpenAI-compatible page:

```bash
export OPENAI_API_BASE=https://localhost:47101/v1
export OPENAI_API_KEY=not-needed
aider --model openai/auto
```

| Key | Value | Why |
|---|---|---|
| `openai-api-base` | `https://localhost:47101/v1` | OpenAI-compatible base URL. Aider appends `/chat/completions`. |
| `openai-api-key` | `not-needed` | Placeholder. The router does not authenticate LLM forwarding. |
| `model` | `openai/auto` | Main chat. Sends `"model": "auto"`. |
| `weak-model` | `openai/auto` | Commit messages and history summaries. Without this, Aider picks a catalog model that is not `auto`. |
| `editor-model` | `openai/auto` | Editor tasks, same reason. |
| `edit-format` | `diff` | Aider's default depends on a known model. `openai/auto` is not in its catalog, so the preset picks `diff` instead of leaving the format unset. |
| `show-model-warnings` | `false` | Skips the unknown-model prompt. Aider's own docs say an unknown context window can be ignored; a metadata file is optional and this preset does not add one. |

Trust the router's local CA first ([client TLS setup](../../router/client-tls-setup.md)). Aider is a
Python client; point `SSL_CERT_FILE` or `REQUESTS_CA_BUNDLE` at `router-ca.crt` if it does not
use the OS store. `verify-ssl: false` exists and is not set here.

## Limitation

Aider will warn, once, that it has no catalog entry for `openai/auto` unless
`show-model-warnings` is false. The preset turns that prompt off. It does not teach Aider the
context-window size of whichever backend the router picks; Aider reports token-limit errors from
the API rather than enforcing its own. That is the documented behavior for an unknown model, and
it is acceptable here because `auto` is not one backend.

## Sources

Checked 2026-09-28.

- [OpenAI compatible APIs](https://aider.chat/docs/llms/openai-compat.html) — `OPENAI_API_BASE`, `OPENAI_API_KEY`, and `--model openai/<model-name>`.
- [YAML config file](https://aider.chat/docs/config/aider_conf.html) — `.aider.conf.yml` search order and the `openai-api-base`, `model`, `weak-model`, and `editor-model` keys.
- [Options reference](https://aider.chat/docs/config/options.html) — `--openai-api-base`, `--model`, `--weak-model`, `--editor-model`, `--edit-format`, `--show-model-warnings`, and `--verify-ssl`.
- [Advanced model settings](https://aider.chat/docs/config/adv-model-settings.html) — unknown context windows can be ignored; `.aider.model.metadata.json` is optional.
