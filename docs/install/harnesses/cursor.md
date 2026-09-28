# Cursor

Cursor has no project file for a custom model endpoint, so this guide is the whole preset. There
is no adapter to copy.

The setting that can address Arc Router is the OpenAI-compatible base URL override named in
Cursor's privacy docs. The documented bring-your-own-key flow cannot: it has no base URL field,
and it sends the key to Cursor's backend because prompt building goes through Cursor's servers.
A loopback router is not reachable that way.

## Config

In Cursor Settings → Models:

1. Enable the OpenAI API key and set it to `not-needed`. The router does not check
   `Authorization`; the value only fills a field Cursor will not leave empty.
2. Turn on **Override OpenAI Base URL** and set it to `https://localhost:47101/v1`. Cursor
   appends `/chat/completions`. Do not include that path yourself.
3. Add a model named `auto` and select it. That is the same `"model": "auto"` the
   [drop-in](../openai-compatible-drop-in.md) uses. If the picker only offers names the router
   advertises, choose `totallyhot-arcrouter` — the same routing decision, listed by
   `GET /v1/models`.

Trust the router's local CA first ([client TLS setup](../../router/client-tls-setup.md)). On
Windows and macOS, Cursor reads the OS trust store. Chrome-on-Linux's separate certificate
database is the case the TLS doc calls out; Cursor on Linux can hit the same gap.

## Limitation

The API-keys page does not document the override toggle. The steps above use the control's name
from Cursor staff on the forum, plus the privacy doc's name for the same mechanism ("an
OpenAI-compatible base URL override"). If a build has no such toggle, the documented API-key
flow still cannot target this router.

While the override is on, Cursor sends OpenAI-family requests — including names from its own
model list — to that base URL. There is no per-model base URL. Turn the override off to use
Cursor's own models again. Tab completion keeps using Cursor's built-in models either way.

Cursor's API-keys page also says bring-your-own-key requests are routed through Cursor's servers
for prompt building. This guide does not claim the override skips that. It claims only what the
privacy doc claims: a custom model reached through the override carries the gateway's region,
which for this preset is the machine running Arc Router.

## Sources

Checked 2026-09-28.

- [Bring your own API key](https://cursor.com/docs/settings/api-keys) — Cursor Settings → Models, the OpenAI key field, server-side prompt building, and Tab staying on Cursor's models. The same page is published under [Help](https://cursor.com/help/models-and-usage/api-keys).
- [Privacy and data governance](https://cursor.com/docs/enterprise/privacy-and-data-governance) — "a custom model reached via an OpenAI-compatible base URL override".
- [Cursor staff, forum thread 169088](https://forum.cursor.com/t/cursor-managed-models-are-routed-through-override-openai-base-url/169088) — the override control's label, and that enabling it sends OpenAI-family requests, including Cursor-managed model names, to the custom endpoint.
