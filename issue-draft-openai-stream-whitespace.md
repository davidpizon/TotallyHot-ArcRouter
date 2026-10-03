# OpenAI-shaped streamed replies lose whitespace-only deltas in transcripts and telemetry

Suggested labels: `bug`. Plan: `docs/plans/issue-<this issue>-openai-stream-whitespace.md` (PR to follow).

## Bug

Verified 2026-09-30 against the real parser, and still present on `main` on 2026-10-03.

`OpenAiResponseTextParser.TryExtractFromStreamingBuffer` (`src/TotallyHotArcRouter/Telemetry/OpenAiResponseTextParser.cs:46-67`) passes each SSE delta's `content` through `MessageContentTextExtractor.ExtractText`. For a string, `ExtractText` returns `null` when the string is whitespace-only (`MessageContentTextExtractor.cs:21`), and the loop skips `null` deltas (`OpenAiResponseTextParser.cs:59-60`).

So every whitespace-only delta is dropped:

| Deltas | Extracted today | Expected |
|---|---|---|
| `"Hello"`, `"\n\n"`, `"World"`, `"\n"`, `"    "`, `"x = 1"` | `HelloWorldx = 1` | `Hello\n\nWorld\n    x = 1` |

`AnthropicResponseTextParser.TryExtractFromStreamingBuffer` appends `delta.text` verbatim and returns the expected text.

The client is not affected. The stream translators forward whitespace-only chunks (`AnthropicStreamTranslator.cs:279-281`, `GeminiStreamTranslator.cs:184`). Only the router's own extracted copy loses them.

## Which traffic is affected

The OpenAI parser runs whenever the bytes it parses are OpenAI-shaped (`ProxyMiddleware.cs:751`, `ProviderRegistrations.cs`):

- `openai` and `ollama` traffic;
- every translated route with no native capture: `gemini`, `bedrock-titan`, `bedrock-llama`, `bedrock-anthropic`, and the tool-calling translators.

**Correction to the first report.** Translated direct-`anthropic` traffic is normally *not* affected. `anthropic` is the one provider with a native parser, so its raw bytes are captured too (`UpstreamResponseWriter.cs:322,371`), and `RequestTelemetryPublisher` extracts both usage and text from those native bytes (`RequestTelemetryPublisher.cs:374-385`). It reaches the OpenAI parser only when usage cannot be read from the native capture and the publisher falls back to the translated bytes (`RequestTelemetryPublisher.cs:389-406`).

## Where the lossy text ends up

- `request_transcripts.response_text` in transcripts.db (`RequestTelemetryPublisher.cs:724`). `QualityRescanService` later re-scores this stored text.
- `RoutingTelemetryEvent.ResponseSummary`, the live feed.
- The quality verifier (`RequestTelemetryPublisher.cs:815-821`). `FencedCodeBlockParser` splits on `\n` and needs fences at the start of a line (`FencedCodeBlockParser.cs:22,26-27,41`). A lost newline before a fence therefore drops the whole block. Lost indentation corrupts the code, and Python is hit hardest.
- The LLM graders' response-text cache, and `ResponseLengthChars` in grader score records. The verbosity-skew analysis uses those lengths (`GraderReliabilityAnalyzer.cs:131-132`), so they undercount for these providers only.

David's requirement of 2026-09-30 is that stored session text be full, with truncation allowed only for display. ADR-0019 (proposed) lists this bug as "tracked separately". This issue is that item. Its per-session files would hold the same reply extract, so the fix applies whichever store holds the text.

**Separate display cause.** Today's Sessions tab bubbles (`SessionConversationPane.razor:19-26`, `.ls-chat-bubble-*` in `app.css:1404-1415`) do not set `white-space`. The browser therefore collapses newlines in every reply, including intact Anthropic replies. The #176 plan renders message bodies with `white-space: pre-wrap` (§4.3).

## Also checked

- **Non-streaming OpenAI path.** String `content` is OpenAI's documented shape, and every translator emits it (`AnthropicPayloadTranslator.cs:176`, `GeminiPayloadTranslator.cs:431`). It is returned whole, so interior whitespace survives. Array `content` is used by some OpenAI-compatible servers. For arrays, whitespace-only parts are dropped and the rest are joined with a space. That is the same class of loss on a rare shape.
- **Prompt side.** `RequestTextExtractor.ExtractNewestUserMessage` also uses `ExtractText`. Its callers rely on "blank prompt → `null`": `RequestInterceptor.cs:424,444`, `HeuristicRequestClassifier.cs:78`, and `RequestTelemetryPublisher.cs:603-604`.

## Done when

- A streamed delta sequence that includes whitespace-only chunks round-trips exactly, through the parser and into `response_text` and `ResponseSummary`.
- Prompt extraction behaves exactly as before.
- Builds are warning-free, all tests pass, and coverage stays at or above 80%.
