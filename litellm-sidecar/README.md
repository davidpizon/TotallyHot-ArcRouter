# LiteLLM Parity Sidecar (temporary, dev/test-only)

This is **not** part of TotallyHotArcRouter's runtime architecture. It exists solely to give
[PLAN.md](../PLAN.md)'s parity workstream (TODO 1 → TODO 3 → TODO 4) a real, working
OpenAI-compatible reference implementation to compare TotallyHotArcRouter's own proxy against —
routing, retries, streaming, and error-handling behavior. It is never deployed alongside
TotallyHotArcRouter, never a build/runtime dependency of it, and should be deleted (this whole
directory) once TODO 4 closes.

## What it is

[LiteLLM](https://github.com/BerriAI/litellm) running as a Docker container, configured with
one `mock_response` entry per model in `RouterConstants.SupportedModels`. Every request gets a
deterministic canned response — **no real provider is ever called**: no API keys, no network
egress to a provider, no cost, no flakiness from live rate limits. See `config.yaml`'s comments
for the exact mechanism (LiteLLM's built-in `mock_response` feature).

## Running it

```bash
cd litellm-sidecar
docker compose up
```

The proxy listens on `http://localhost:4000`, OpenAI-compatible (`/chat/completions`, etc.).
Any of the 8 `model` values in `config.yaml` will get a mock response instantly. Health check:
`http://localhost:4000/health/liveliness`.

```bash
docker compose down
```

## What it is not

- Not a permanent sidecar. Not started by CI, not started by any TotallyHotArcRouter startup path,
  not referenced by any `appsettings.json`.
- Not a source of real model behavior — the mock responses are static strings, not actual LLM
  output. Parity tests (TODO 3) validate *proxy/routing behavior* (does TotallyHotArcRouter's model
  rewrite, retry, and error handling match LiteLLM's), not model output quality.

## When to delete this directory

Once PLAN.md's TODO 4 ("port every parity gap TODO 3 proves") closes and TotallyHotArcRouter no
longer needs a reference to validate against, delete `litellm-sidecar/` entirely. Nothing in
`src/` should ever come to depend on it existing.

