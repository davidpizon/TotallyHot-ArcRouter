namespace TotallyHot.ArcRouter.Gui.Console;

/// <summary>
/// One log line received live over the proxy's <c>TelemetryService.StreamEvents</c> gRPC stream's
/// <c>log_line</c> oneof case. Constructed by <c>TotallyHot.ArcRouter.Gui.Services.LiveDataStore</c> from the
/// generated <c>TotallyHot.ArcRouter.Telemetry.Contract.LogLineEvent</c> message - compiled from the same
/// <c>Protos/telemetry.proto</c> file the proxy's server-side code compiles, but in
/// TotallyHot.ArcRouter.Gui.Telemetry, not this project (see that project's csproj remarks for why: .NET
/// MAUI's SingleProject build doesn't reliably run Grpc.Tools' codegen). This project itself still
/// has no Grpc/Google.Protobuf dependency of its own - it only ever sees this hand-written DTO,
/// matching <c>TotallyHot.ArcRouter.Gui.Telemetry.RoutingTelemetryEventDto</c>'s decoupling rationale.
/// </summary>
public sealed record LogLineDto(DateTimeOffset TimestampUtc, string Level, string Message);