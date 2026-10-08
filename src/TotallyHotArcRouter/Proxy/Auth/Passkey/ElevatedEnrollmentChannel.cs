using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Hosted listener for elevated enrollment commands (ADR-0020). Windows uses a named pipe ACL'd to
/// <c>LocalSystem</c> and Administrators; Unix uses a domain socket under the machine-shared directory.
/// Only this channel may mint enrollment codes or remove credentials without going through WebAuthn.
/// </summary>
public sealed class ElevatedEnrollmentChannel : BackgroundService
{
    /// <summary>Windows pipe name (without the <c>\\.\pipe\</c> prefix).</summary>
    public const string PipeName = "TotallyHotArcRouter-Enrollment";

    private readonly EnrollmentCodeService _enrollmentCodes;
    private readonly IPasskeyCredentialStore _credentialStore;

    /// <summary>Initializes a new instance of the <see cref="ElevatedEnrollmentChannel"/> class.</summary>
    public ElevatedEnrollmentChannel(EnrollmentCodeService enrollmentCodes, IPasskeyCredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(enrollmentCodes);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _enrollmentCodes = enrollmentCodes;
        _credentialStore = credentialStore;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A listener that cannot start (a second router instance already owns the pipe, an unwritable
        // socket directory) must not take the router down with it: BackgroundService would otherwise stop
        // the host on an unhandled exception. The gate fails closed instead - with no channel, no
        // enrollment code can be minted, so content stays locked.
        try
        {
            if (OperatingSystem.IsWindows())
                await RunWindowsAsync(stoppingToken).ConfigureAwait(false);
            else
                await RunUnixAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException)
        {
            Log.Error(ex, "The elevated passkey enrollment channel failed to start or stopped unexpectedly; enrollment is unavailable");
        }
    }

    private async Task RunWindowsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var server = CreateWindowsPipe();
            await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            await HandleConnectionAsync(server, stoppingToken).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsPipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            identity: new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            rights: PipeAccessRights.FullControl,
            type: AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            identity: new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            rights: PipeAccessRights.FullControl,
            type: AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName: PipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private async Task RunUnixAsync(CancellationToken stoppingToken)
    {
        var path = Path.Combine(AppDataPaths.ResolveMachineSharedDirectory(), "enrollment.sock");
        if (File.Exists(path)) File.Delete(path);

        var endpoint = new UnixDomainSocketEndPoint(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(backlog: 5);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleUnixClientAsync(client, stoppingToken), stoppingToken);
        }
    }

    private async Task HandleUnixClientAsync(Socket client, CancellationToken stoppingToken)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);
        await HandleConnectionAsync(stream, stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken stoppingToken)
    {
        // BOM-free UTF-8 on both sides: the client reads with BOM detection off and hands the line to a JSON
        // parser, which rejects a leading BOM.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        // Set AutoFlush after construction: an object initializer on a using/await-using variable would
        // skip Dispose if the initializer threw.
        await using var writer = new StreamWriter(stream, utf8, bufferSize: 4096, leaveOpen: true);
        writer.AutoFlush = true;

        var line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
        if (line is null) return;

        EnrollmentRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EnrollmentRequest>(line);
        }
        catch (JsonException)
        {
            await writer.WriteLineAsync(SerializeFail("Invalid JSON.")).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await writer.WriteLineAsync(SerializeFail("Invalid request.")).ConfigureAwait(false);
            return;
        }

        switch (request.Op?.ToLowerInvariant())
        {
            case "mint":
                var code = _enrollmentCodes.Mint();
                await writer.WriteLineAsync(SerializeOk(code)).ConfigureAwait(false);
                break;
            case "revoke":
                if (string.IsNullOrWhiteSpace(request.Id))
                {
                    await writer.WriteLineAsync(SerializeFail("Missing id.")).ConfigureAwait(false);
                    break;
                }

                var removed = _credentialStore.Remove(request.Id);
                await writer.WriteLineAsync(removed
                        ? SerializeOk(code: null)
                        : SerializeFail("Credential not found."))
                    .ConfigureAwait(false);
                break;
            default:
                await writer.WriteLineAsync(SerializeFail("Unknown op.")).ConfigureAwait(false);
                break;
        }
    }

    private sealed class EnrollmentRequest
    {
        [JsonPropertyName("op")]
        public string? Op { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }

    private static string SerializeOk(string? code) =>
        JsonSerializer.Serialize(new { ok = true, code });

    private static string SerializeFail(string error) =>
        JsonSerializer.Serialize(new { ok = false, error });
}
