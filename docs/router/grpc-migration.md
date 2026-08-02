# Migrate Telemetry Transport: SignalR → gRPC

> **Status: Implemented, but narrower than this doc's original design.** `StreamEvents` (section 1-4
> below) is real: `src/Protos/telemetry.proto`, `TotallyHotArcRouter.Telemetry.TelemetryBroadcaster`/
> `TelemetryGrpcService`, and `TotallyHotArcRouter.Gui.Services.LiveDataStore`'s `GrpcChannel`-based client
> all exist and SignalR has been fully removed (`TelemetryHub.cs` deleted, `Microsoft.AspNetCore.SignalR`/
> `.Client` package references gone) - see [`telemetry.md`](telemetry.md#transport-grpc). **`GetModelSpend`
> (section 3.2) and the `ModelListEvent` oneof case were deliberately descoped**, not implemented: both
> depend on `IUsageLedger` ([`agent-cost-tracking.md`](agent-cost-tracking.md)), which has no code at
> all, and neither has any existing SignalR-era behavior to port (no `GET /governance/model-spend`
> endpoint, real or proposed-and-wired, and no model-list push ever shipped over the old hub - the GUI
> still gets its model list from the proxy's separate `GET /v1/models` REST endpoint, unchanged). The
> `.proto` below reflects this: it omits both. This repo's own environment has no .NET SDK (see
> [`telemetry.md`](telemetry.md)'s own banner for why), so none of this was build-verified here - but
> it has since been build-attempted on a real .NET 10 SDK/Visual Studio, which caught one real bug
> this doc's original design didn't anticipate: **.NET MAUI's `SingleProject` build doesn't reliably
> run Grpc.Tools' codegen**, so `TotallyHotArcRouter.Gui.csproj` originally compiling the `.proto` directly
> (as section 4 originally described) failed with `CS0234` and no `protoc` output at all. Fixed by
> moving that compile to `TotallyHotArcRouter.Gui.Telemetry` instead - see section 4's note and "Known
> limitations" below. **A second real bug surfaced once the build actually ran**: unencrypted HTTP/2
> (h2c, section 2 as originally written) failed 100% of the time on a managed/corporate machine with
> the HTTP/2-level `HTTP_1_1_REQUIRED` error - consistent with something on the network path not
> understanding or mangling the h2c preface. Section 2 now describes the fix: a dedicated,
> TLS-secured second port (self-signed cert, `Telemetry/TelemetryTlsCertificate.cs`) instead of
> unencrypted h2c on the shared proxy port. That fix is itself still unconfirmed to build clean
> end-to-end (still no local .NET SDK to verify with) - treat it as the current best understanding,
> not a closed loop yet.

## Why consider this at all

Nothing about SignalR is currently broken - this document exists because it's technically feasible
and closes one real, already-documented pain point, not because of an urgent problem. Two honest
motivations, and one important non-motivation:

- **The hand-synced DTO problem is real today.** `TotallyHotArcRouter.Gui.Telemetry.RoutingTelemetryEventDto`
  is an independent copy of `TotallyHotArcRouter.Telemetry.RoutingTelemetryEvent`, kept in sync *by hand*
  because the GUI project deliberately doesn't reference the proxy project (see both types' own doc
  comments). Every field added to one has to be remembered on the other - this repo has already had
  to edit both files together twice (the original record's full field set, then adding
  `RequestSummary`/`ResponseSummary` later), and once more edited only one side's doc comment without
  the other noticing anything was inconsistent (a comment, not a field, so nothing broke - but it's
  exactly the kind of silent drift a hand-synced pair of types allows). A shared `.proto` file
  generates both sides' types from one source of truth instead.
- **The proposed on-demand spend query is a more natural fit for gRPC.**
  [`../gui/governance-model-cards.md`](../gui/governance-model-cards.md) needed a plain REST endpoint
  specifically because `TelemetryHub` was push-only with no client-callable methods - a real
  impedance mismatch a unary gRPC RPC doesn't have. This motivation is still valid in the abstract,
  but section 3.2's `GetModelSpend` RPC was **not** actually built as part of this migration - see
  "Scope" below for why.
- **Not a motivation: performance.** This is a personal local dev tool with one dashboard and modest
  event volume - SignalR's JSON-over-WebSocket overhead is not a measured or likely problem here. If
  performance were the driver, this doc would say so explicitly; it isn't the reason to do this.

---

## Scope: full replacement (of what shipped)

Per project decision, this was a **clean swap, not a coexistence period** - `TelemetryHub`,
`TelemetryPublisher`'s SignalR-specific plumbing, and `LiveDataStore`'s `HubConnection` were replaced
outright, not run alongside a gRPC transport during a deprecation window. That part happened exactly
as designed.

**What did not happen**: this doc originally proposed that the migration would also **supersede**
[`governance-model-cards.md`](../gui/governance-model-cards.md)'s proposed
`GET /governance/model-spend` REST endpoint (section 4.2 of that doc) by building it as a gRPC unary
RPC instead (section 3.2 below). That did not ship - `GetModelSpend` was descoped along with its
`IUsageLedger` dependency (see the status banner above).

**The design question is nonetheless settled: the RPC won.** `governance-model-cards.md` §4.2 now
specifies `GetModelSpend` and no longer proposes a REST endpoint at all — internal, same-machine
surfaces between components we control prefer gRPC, where the shared `.proto` makes the contract drift
this migration existed to eliminate structurally impossible. So section 3.2 below is not "one of two
candidates"; it is the design of record for that query, still blocked on `IUsageLedger` and still
unbuilt. What was descoped was the *implementation*, not the decision.

The [`signalr-hub-security.md`](signalr-hub-security.md) proposal (TLS + shared-secret auth) still
applies conceptually to whatever transport serves this data - now that gRPC has shipped (not
SignalR), that doc's authentication piece (a token check before the call proceeds) still carries over
almost unchanged via a gRPC interceptor, and its TLS piece is still simpler under gRPC (see
"Transport: HTTP/2" below) than the SignalR-era self-signed-cert dance it currently describes - but
its code samples are now stale (written against `HubConnectionBuilder`/`MapHub`, both gone) and need
translating before that doc is actionable. See that doc's own updated banner.

---

## 1. Service contract

**One unified streaming RPC**, matching the old single-hub-connection model - one stream, one
connection lifecycle, the GUI subscribes once and receives every event type over it, exactly like the
old single `HubConnection`. A separate RPC per event type was considered and rejected: it would mean
three connections/lifecycles to manage on the GUI side instead of one, for no benefit this
single-client app needs.

**What actually shipped, in `src/Protos/telemetry.proto`, is a subset of the contract below**: the
`GetModelSpend` RPC and `GetModelSpendRequest`/`GetModelSpendResponse`/`ModelSpend` messages are
absent entirely (not built - see the status banner), and `TelemetryEvent`'s `oneof` only has the two
cases that had a real SignalR-era predecessor to port (`routing_telemetry`, `log_line`) -
`ModelListEvent`/`ModelListEntry` and the `model_list` oneof case don't exist in the shipped file
either. The full design below (including the descoped pieces) is kept as-is for reference and as a
sketch for whoever picks either piece up later; `csharp_namespace` is also set explicitly to
`TotallyHotArcRouter.Telemetry.Contract` in the shipped file (not shown below), to avoid colliding with the
hand-written `TotallyHotArcRouter.Telemetry.RoutingTelemetryEvent`/`LogLineEvent` domain types of the same
short name, and with the real `Grpc.Core`/`Grpc.AspNetCore` namespaces this code also imports.

```protobuf
syntax = "proto3";

package TotallyHotArcRouter.telemetry.v1;

import "google/protobuf/timestamp.proto";

service TelemetryService {
  // Replaces TelemetryHub: the server streams every event (routing telemetry, log lines, and a
  // one-time model list snapshot) to a connected client for the lifetime of the call - the gRPC
  // equivalent of today's push-only hub connection.
  rpc StreamEvents (StreamEventsRequest) returns (stream TelemetryEvent);

  // Replaces governance-model-cards.md's proposed GET /governance/model-spend REST endpoint.
  rpc GetModelSpend (GetModelSpendRequest) returns (GetModelSpendResponse);
}

message StreamEventsRequest {}

// Envelope: exactly one of these is set per message, matching SignalR's per-message-type dispatch
// ("RoutingTelemetry" / "LogLine" / "ModelList" client methods) with a single Protobuf idiom.
message TelemetryEvent {
  oneof event {
    RoutingTelemetryEvent routing_telemetry = 1;
    LogLineEvent log_line = 2;
    ModelListEvent model_list = 3;
  }
}

message RoutingTelemetryEvent {
  string session_id = 1;
  int32 turn_number = 2;
  bool is_session_synthesized = 3;
  string requested_model = 4;
  string resolved_model = 5;
  string provider = 6;
  bool is_fallback = 7;
  optional int32 prompt_tokens = 8;
  optional int32 completion_tokens = 9;
  optional string estimated_cost_usd = 10;   // decimal - see "Decimal encoding" below
  bool is_streaming = 11;
  int64 latency_to_headers_ms = 12;
  int64 total_duration_ms = 13;
  int32 status_code = 14;
  google.protobuf.Timestamp timestamp_utc = 15;
  optional string request_summary = 16;
  optional string response_summary = 17;
}

message LogLineEvent {
  google.protobuf.Timestamp timestamp_utc = 1;
  string level = 2;
  string message = 3;
}

message ModelListEvent {
  repeated ModelListEntry models = 1;
}

message ModelListEntry {
  string model_name = 1;
  string provider = 2;
  string provider_model_id = 3;
}

message GetModelSpendRequest {
  google.protobuf.Timestamp from = 1;
  google.protobuf.Timestamp to = 2;
}

message GetModelSpendResponse {
  repeated ModelSpend models = 1;
}

message ModelSpend {
  string model_name = 1;
  string accumulated_cost_usd = 2;           // decimal - see "Decimal encoding" below
}
```

Field names map 1:1 onto `RoutingTelemetryEvent`/`LogLineEvent`'s existing C# properties (just
`snake_case`, per Protobuf convention, instead of `PascalCase`) - this is a wire-format and codegen
change, not a data-model redesign.

### Decimal encoding

Protobuf has no native decimal type. `EstimatedCostUsd`/`AccumulatedCostUsd` are represented as
**strings** (`decimal.ToString(CultureInfo.InvariantCulture)` / `decimal.Parse(..., CultureInfo.InvariantCulture)`
on each end) rather than `double` (binary floating point loses precision on currency values - not
acceptable for a cost-tracking feature) or a wrapper type like `google.type.Money` (an extra external
dependency for one field, not worth it here). This is the same pragmatic choice many real-world
Protobuf currency fields make.

### Nullable fields

`PromptTokens`/`CompletionTokens`/`EstimatedCostUsd`/`RequestSummary`/`ResponseSummary` are all
`T?` in the C# model. Proto3's `optional` keyword (explicit field presence, standard since protobuf
3.15 / widely supported by current `Grpc.Tools`) is used rather than the older wrapper-message
pattern (`google.protobuf.Int32Value` etc.) - simpler generated code, one concept instead of two.

---

## 2. Transport: HTTP/2 over TLS (self-signed cert), not unencrypted h2c

gRPC requires HTTP/2. **This section originally shipped as unencrypted HTTP/2 (h2c)** - see "Known
risk: no automatic transport fallback" below, kept for the record - **but that had to be reverted to
real TLS after actual use surfaced exactly the risk that section warned about.** On at least one
managed/corporate Windows machine, every single `StreamEvents` connection attempt failed with the
HTTP/2-level `HTTP_1_1_REQUIRED` error - the server-side signal that a connection didn't actually
negotiate as HTTP/2 - consistent with something on the network path (VPN client, endpoint security
agent, TLS-inspecting proxy) not understanding or silently mangling the h2c connection preface, even
for loopback traffic. Real TLS + ALPN negotiation is far less likely to be interfered with, since
inspected traffic almost always still respects standard TLS+ALPN (it looks like any other HTTPS
connection until decrypted) even when it wouldn't touch raw unencrypted HTTP/2 bytes correctly.

**This is a second, dedicated port, not the same port as the plain proxy.** The LLM-forwarding proxy
(port 5001) has real external clients already connecting to it as plain HTTP; it can't also become an
HTTPS/2-only endpoint. `ProxyServer.DefaultGrpcPort` (5002) is a new, separate Kestrel listener
specifically for the gRPC telemetry endpoint.

**Server (`Proxy/ProxyServer.cs`):** a self-signed certificate (`Telemetry/TelemetryTlsCertificate.cs`,
generated once and persisted under `%LOCALAPPDATA%\TotallyHotArcRouter\telemetry-cert.pfx` with a random
per-installation password stored alongside it, so it survives restarts instead of forcing the client
to re-trust a new one every launch) is bound to the dedicated gRPC port:

```csharp
var certificate = TelemetryTlsCertificate.GetOrCreate();
options.ListenLocalhost(grpcPort, listenOptions =>
{
    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    listenOptions.UseHttps(certificate);
});
```

`UseHttps` means ALPN does the HTTP/1.1-vs-HTTP/2 negotiation the normal, standard way - no h2c
prior-knowledge trickery needed on this port at all.

**Client (`Services/LiveDataStore.cs`):** no `Http2UnencryptedSupport` AppContext switch needed
anymore (that was h2c-specific); instead, a custom `HttpClientHandler` trusts the self-signed
certificate by subject name (`CN=localhost`), since both processes run as the same OS user on the
same machine and there's no CA to issue a "real" certificate for a loopback address:

```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        cert is not null && cert.Subject.Contains("CN=localhost", StringComparison.Ordinal),
};
var channel = GrpcChannel.ForAddress("https://localhost:5002", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
var client = new TelemetryService.TelemetryServiceClient(channel);
```

This is the same targeted-validation-callback pattern [`signalr-hub-security.md`](signalr-hub-security.md)'s
section 1 sketched for SignalR (subject-name matching, not a blanket
`DangerousAcceptAnyServerCertificateValidator` accept-all) - not a thumbprint pin yet; that would be a
reasonable follow-up hardening (read the same `.pfx` the proxy persists and pin its exact public
certificate).

### Known risk: no automatic transport fallback (still true, now for TLS+ALPN instead of h2c)

SignalR negotiates down through WebSockets → Server-Sent Events → Long Polling if the preferred
transport is unavailable. gRPC has no equivalent fallback - if TLS+ALPN itself somehow becomes
unavailable in some environment, the connection simply fails, with no automatic degraded mode. This
is a real, if narrow, regression from SignalR's resilience story, worth stating plainly rather than
glossing over. TLS is a meaningfully more robust choice than h2c was (see above), but it is not an
absolute guarantee against every possible network environment.

---

## 3. Server-side implementation

New `Grpc.AspNetCore` package reference. `Proxy/ProxyServer.cs`'s inner host swaps
`services.AddSignalR()` / `endpoints.MapHub<TelemetryHub>("/telemetry/hub")` for (the shipped code
also explicitly sets Kestrel's `Protocols` to `Http1AndHttp2` per section 2, omitted here for brevity):

```csharp
webBuilder.ConfigureServices(services => services.AddGrpc());
webBuilder.Configure(app =>
{
    app.UseRouting();
    app.UseEndpoints(endpoints => endpoints.MapGrpcService<TelemetryGrpcService>());
    app.Run(context => proxyMiddleware.InvokeAsync(context, _ => Task.CompletedTask));
});
```

### 3.1 `StreamEvents`: replacing `IHubContext.Clients.All.SendAsync`

SignalR's `IHubContext<TelemetryHub>` gives `TelemetryPublisher` a ready-made "broadcast to every
connected client" primitive. gRPC server-streaming has no direct equivalent - each `StreamEvents`
call gets its own `IServerStreamWriter<TelemetryEvent>`, tied to that one call's lifetime. The
replacement is a small fan-out registry, using `System.Threading.Channels` (the idiomatic modern .NET
building block for this):

```csharp
public sealed class TelemetryGrpcService : TelemetryService.TelemetryServiceBase
{
    // Registered per active StreamEvents call; TelemetryBroadcaster (below) writes to every
    // registered channel instead of SignalR's Clients.All.
    private readonly TelemetryBroadcaster _broadcaster;

    public override async Task StreamEvents(
        StreamEventsRequest request,
        IServerStreamWriter<TelemetryEvent> responseStream,
        ServerCallContext context)
    {
        var channel = Channel.CreateUnbounded<TelemetryEvent>();
        _broadcaster.Register(channel.Writer);
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(evt);
            }
        }
        finally
        {
            _broadcaster.Unregister(channel.Writer);
        }
    }

    // NOT SHIPPED - see section 3.2.
    public override async Task<GetModelSpendResponse> GetModelSpend(
        GetModelSpendRequest request, ServerCallContext context)
    {
        // Backed by IUsageLedger.GetAccumulatedCostAsync (agent-cost-tracking.md) per configured
        // model - the same query governance-model-cards.md's REST endpoint would have run.
    }
}
```

The shipped `TelemetryGrpcService`/`TelemetryBroadcaster` match this shape closely, minus
`GetModelSpend` and the `ModelListEvent` push described below (both not built), and with one
robustness fix this sketch's `Channel.CreateUnbounded<TelemetryEvent>()` didn't have: the shipped
channel is **bounded** (capacity 1024, `BoundedChannelFullMode.DropOldest`) so a stalled or unusually
slow client can't make its per-call buffer grow without limit while the broadcaster keeps publishing
- telemetry is explicitly best-effort, so dropping the stalest buffered event to make room is the
right tradeoff, not blocking the publisher or growing memory unboundedly. `TelemetryBroadcaster`
replaced `TelemetryPublisher`'s dependency on `IHubContext<TelemetryHub>`: `Publish`/`PublishLogLine`
construct a `TelemetryEvent` envelope and write it to every registered channel, catching/logging
(never throwing) per-writer failures the same way `TelemetryPublisher.PublishAsync` isolated SignalR
send failures before - same fault-isolation contract, different plumbing underneath. One difference
from this sketch: `TelemetryBroadcaster` has no hosting dependency at all (unlike the old
`IHubContext<TelemetryHub>`, only available post-Kestrel-start), so `TelemetryPublisher` no longer
needs an `AttachHubContext`-style two-phase init - it takes `TelemetryBroadcaster` as a plain
constructor dependency instead. (The `ModelListEvent`-on-connect line below never applied, since that
event type wasn't built.)

### 3.2 `GetModelSpend`: NOT IMPLEMENTED (but it is the design of record)

Unbuilt, and unchanged from before this migration shipped — but no longer merely a sketch: this is now
the specified mechanism for the spend query, and `governance-model-cards.md` §4.2 points here rather
than proposing the REST endpoint it used to. Same query (`IUsageLedger`-backed, per-model accumulated
cost over a date range), same [proxy-only architecture boundary](telemetry.md#gui-consumption) (the GUI
still never opens `agent_telemetry.db` directly, it calls this RPC), just a gRPC method signature
instead of `from`/`to` query-string parameters on an HTTP GET. Blocked on `IUsageLedger`
([`agent-cost-tracking.md`](agent-cost-tracking.md)) having no implementation; build that first.

---

## 4. Client-side implementation (`Services/LiveDataStore.cs`)

New `Grpc.Net.Client` + `Google.Protobuf` package references, plus a `Grpc.Tools`-driven `.proto`
codegen build step (a genuinely new piece of this project's build - nothing here uses protoc today).
**As shipped, these package references and the codegen step live in `TotallyHotArcRouter.Gui.Telemetry`,
not `TotallyHotArcRouter.Gui`** - see the note right after the code sample below for why.

```csharp
public sealed class LiveDataStore : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly TelemetryService.TelemetryServiceClient _client;
    private CancellationTokenSource? _streamCts;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ConsumeStreamWithReconnectAsync(_streamCts.Token);
    }

    private async Task ConsumeStreamWithReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var call = _client.StreamEvents(new StreamEventsRequest(), cancellationToken: cancellationToken);
                await foreach (var evt in call.ResponseStream.ReadAllAsync(cancellationToken))
                {
                    Dispatch(evt); // switch on evt.EventCase: RoutingTelemetry / LogLine / ModelList
                }
            }
            catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Telemetry stream disconnected; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
```

The shipped `LiveDataStore` follows this shape closely, with two differences this sketch glosses over.

**First**, `Dispatch(evt)` doesn't hand the generated proto message straight to
`ConversationAggregator`/`LogBuffer`. It maps `Contract.RoutingTelemetryEvent`/`Contract.LogLineEvent`
into the *same* `RoutingTelemetryEventDto`/`LogLineDto` records that already existed pre-migration
(private `MapToDto` methods, handling proto3 `optional` field presence via the generated `HasXxx`
properties, `Timestamp.ToDateTimeOffset()`, and `decimal.Parse` for the cost string). Those two DTOs
were **not deleted**, despite this doc's "Why consider this at all" framing them as the problem being
solved: `ConversationAggregator`/`LogBuffer` and their existing tests were left completely untouched,
kept decoupled from the wire message shape. The hand-synced-DTO problem is still solved in the sense
that mattered: the DTOs' *shape* is now compiler-verified against the same `.proto` the server
compiles (a mismatched field is a build error in `MapToDto`, not silent drift), even though the DTO
types themselves remain hand-written.

**Second, and not originally anticipated by this doc**: the client-side `.proto` compile does **not**
happen in `TotallyHotArcRouter.Gui` itself, despite the section heading above and this doc's original
"New `Grpc.Net.Client` + `Google.Protobuf` package references... in this project" framing. It's
compiled in `TotallyHotArcRouter.Gui.Telemetry` instead (a plain, non-MAUI sibling project, already
referenced by `TotallyHotArcRouter.Gui`), because **.NET MAUI's `SingleProject` build (`Microsoft.NET.Sdk.Razor`
+ `UseMaui=true`) does not reliably run Grpc.Tools' codegen target** - confirmed empirically, not
theoretically: a full rebuild of `TotallyHotArcRouter.Gui.csproj` with a correctly-restored `Grpc.Tools`
package reference and a correctly-resolving `<Protobuf>` item path produced zero `protoc`/`Protobuf`/
`Grpc`-related build output at all, and no `obj/.../Protos/` generated-file output - the codegen
target simply never ran, silently, with no warning or error. `TotallyHotArcRouter.csproj` (plain
`Microsoft.NET.Sdk.Web`, no MAUI) has no such problem. Moving the `.proto` compile to
`TotallyHotArcRouter.Gui.Telemetry` fixed it: `Grpc.Net.Client`/`Google.Protobuf` now reach
`TotallyHotArcRouter.Gui` transitively through its existing `ProjectReference`, with no `Protobuf` item or
Grpc package reference of `TotallyHotArcRouter.Gui`'s own needed. `TotallyHotArcRouter.Gui.Telemetry` does now
carry a `Grpc`/`Google.Protobuf` dependency it didn't have before, purely for this build-tooling
reason - `ConversationAggregator`'s own logic still has no gRPC awareness.

### Known gap: no built-in reconnect

`HubConnectionBuilder().WithAutomaticReconnect()` is a one-line SignalR feature with backoff and
connection-state events built in. `Grpc.Net.Client` has no equivalent - the retry loop above is
hand-rolled and deliberately simple (fixed 2-second delay, no exponential backoff, no jitter) as a
starting point, not a finished resilience story. A real implementation should decide on backoff
policy, max-retry behavior, and how `Dashboard.razor`'s "System Status" indicator learns about
connection state (today driven implicitly by whether `LiveDataStore.Conversations` has data; gRPC's
stream-level exceptions give more explicit signal than SignalR's automatic reconnect events did, but
that signal isn't wired to anything yet in this sketch).

---

## 5. Testing changes (what actually shipped)

- **`TelemetryPublisherTests.cs`** no longer mocks `IHubContext<TelemetryHub>`/`IClientProxy`. It
  constructs a real `TelemetryBroadcaster`, registers a real `Channel<TelemetryEvent>`'s writer (or,
  for the fault-isolation tests, a `Mock<ChannelWriter<TelemetryEvent>>` whose `TryWrite` throws -
  `ChannelWriter<T>.TryWrite` is `virtual`, so Moq can override it directly), and asserts on what
  comes out the other end. Simpler than the SignalR mock chain, as predicted.
- **`TelemetryBroadcasterTests.cs`** (new) covers the fan-out registry directly: full field mapping
  (including proto3 `optional` presence via `HasXxx`, the decimal-as-string round trip, and the
  timestamp round trip), multi-writer fan-out, unregister, and fault isolation when one writer throws.
- **`TelemetryGrpcServiceTests.cs`** (new) covers `StreamEvents` - but **not** via the
  `Microsoft.AspNetCore.TestHost`/`Grpc.Net.Client` integration harness this section originally
  proposed. Instead: a hand-rolled in-memory `IServerStreamWriter<T>` fake (collects written items into
  a list) plus `Grpc.Core.Testing`'s `TestServerCallContext.Create(...)` (a real, officially-shipped
  `ServerCallContext` fake built for exactly this) to invoke `StreamEvents` directly as a plain async
  method call. Lighter than a full ASP.NET Core `TestServer` pipeline for what this method's logic
  actually needs to prove (register → forward published events → unregister on cancellation); the
  heavier integration-harness approach remains a reasonable option if this service ever needs
  end-to-end wire-format coverage too.
- **`GetModelSpend`** has no tests - it wasn't built (see section 3.2).
- **`ConversationAggregator`/`LogBuffer` and their existing tests are genuinely unaffected**, exactly
  as predicted - they still operate on `RoutingTelemetryEventDto`/`LogLineDto`, unchanged in shape;
  see section 4's note on why those types weren't deleted.

---

## 6. Known limitations

- **This was a full rewrite of a working, tested feature**, for a real but non-urgent motivation (see
  "Why consider this at all"). `TelemetryHub`, `TelemetryPublisher`, and `LiveDataStore` all changed
  (`TelemetryHub` deleted outright); `RoutingTelemetryEventDto`/`LogLineDto` did **not** change shape
  or get deleted - see section 4. Every test touching the server-side types changed accordingly (see
  section 5).
- **No automatic transport fallback** (see "Known risk" in section 2) - shipped as designed, still a
  real, narrow regression from SignalR's negotiated-transport resilience, not something a follow-up
  fixed.
- **Unencrypted h2c was tried first and had to be reverted to TLS** - see section 2. This wasn't a
  hypothetical risk that stayed hypothetical: it broke 100% of connections on a real machine. Anyone
  reasoning about this migration from the original design (h2c on the shared proxy port) rather than
  what actually shipped (TLS on a dedicated second port) will draw the wrong conclusions about attack
  surface, port count, and certificate lifecycle.
- **A second Kestrel port and a persisted self-signed certificate are new operational surface.**
  `%LOCALAPPDATA%\TotallyHotArcRouter\telemetry-cert.pfx`/`telemetry-cert-pwd.txt` need to exist (or get
  generated on first run) for the gRPC endpoint to start at all; deleting them just regenerates a new
  cert on next start (the client re-trusts by subject name, not a pinned thumbprint, so this doesn't
  require any client-side re-configuration - see section 2).
- **No built-in reconnect** (see section 4) - shipped as a fixed 2-second retry delay, exactly the
  "starting point, not a finished design" this doc originally described. `WithAutomaticReconnect()`'s
  backoff/jitter/event model was never ported; still true.
- **New build-time toolchain.** `Grpc.Tools`/protoc codegen from `src/Protos/telemetry.proto` is a new
  piece of this project's build that nothing here used before - both `TotallyHotArcRouter.csproj`
  (`GrpcServices="Server"`) and `TotallyHotArcRouter.Gui.Telemetry.csproj` (`GrpcServices="Client"`) compile
  the same file independently.
- **`.NET MAUI's `SingleProject` build doesn't reliably run Grpc.Tools' codegen** - discovered only
  once this was actually built (this repo's own sandbox has no .NET SDK, so this genuinely wasn't
  caught until a real build - see the status banner's caveat about that). `TotallyHotArcRouter.Gui.csproj`
  originally compiled the `.proto` directly, per section 4's original design; that produced a
  `CS0234` ("the type or namespace name 'Telemetry' does not exist in the namespace 'TotallyHotArcRouter'")
  with zero `protoc`/`Grpc`/`Protobuf` output anywhere in the build log - the codegen target simply
  never ran. Fixed by moving the client-side compile to `TotallyHotArcRouter.Gui.Telemetry` (plain,
  non-MAUI, already referenced by `TotallyHotArcRouter.Gui`) - see section 4's note. Worth remembering for
  any *future* MAUI-hosted codegen tool in this repo, not just this one.
- **Decimal-as-string is a convention, not a protobuf-enforced contract** - nothing stops a future
  field from being added as `double` by mistake; this needs to be a documented team convention (this
  doc), not something the schema itself enforces.
- **`GetModelSpend` and `ModelListEvent` were descoped, and the race that framing implied is over.**
  This doc originally framed the model-spend query as a race between its own section 3.2 and
  `governance-model-cards.md` section 4.2's REST endpoint — whichever got built first. Neither did, but
  the choice was made on its merits rather than by implementation order: **§4.2 now specifies this
  doc's RPC and the REST endpoint is gone from the design.** Section 3.2 is therefore the design of
  record for that query, not an alternative to it — still unbuilt, still blocked on `IUsageLedger`.

