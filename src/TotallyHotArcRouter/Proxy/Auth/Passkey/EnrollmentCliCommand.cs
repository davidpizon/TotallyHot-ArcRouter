using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TotallyHot.ArcRouter.Hosting;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Client side of the elevated enrollment channel (ADR-0020). Operators run
/// <see cref="FlagName"/> from an elevated shell to mint a registration code or revoke a credential by id.
/// </summary>
public static class EnrollmentCliCommand
{
    /// <summary>CLI flag that mints a passkey enrollment code through the elevated channel.</summary>
    public const string FlagName = "--mint-passkey-enrollment-code";

    /// <summary>Optional flag prefix to revoke a credential: <c>--revoke-passkey=&lt;id&gt;</c>.</summary>
    public const string RevokePasskeyPrefix = "--revoke-passkey=";

    /// <summary>
    /// Executes a mint or revoke request when <paramref name="args"/> contain <see cref="FlagName"/> or
    /// <see cref="RevokePasskeyPrefix"/>.
    /// </summary>
    /// <returns>Process exit code, or <see langword="null"/> when no enrollment flag was present.</returns>
    public static int? TryRun(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => string.Equals(a, FlagName, StringComparison.OrdinalIgnoreCase)))
            return RunMint();

        var revoke = args.FirstOrDefault(a =>
            a.StartsWith(RevokePasskeyPrefix, StringComparison.OrdinalIgnoreCase));
        if (revoke is not null)
        {
            var id = revoke[RevokePasskeyPrefix.Length..];
            return RunRevoke(id);
        }

        return null;
    }

    /// <summary>Mints an enrollment code and prints it to stdout.</summary>
    public static int RunMint() => RunRequest(new { op = "mint" });

    /// <summary>Revokes the credential whose id is <paramref name="credentialIdBase64Url"/>.</summary>
    public static int RunRevoke(string credentialIdBase64Url) =>
        RunRequest(new { op = "revoke", id = credentialIdBase64Url });

    private static int RunRequest(object payload)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("Passkey enrollment commands require an elevated administrator shell.");
            return 2;
        }

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var responseLine = OperatingSystem.IsWindows()
                ? SendWindows(json)
                : SendUnix(json);

            using var doc = JsonDocument.Parse(responseLine);
            if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
            {
                if (doc.RootElement.TryGetProperty("code", out var codeProp) &&
                    codeProp.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine(codeProp.GetString());
                }

                return 0;
            }

            var error = doc.RootElement.TryGetProperty("error", out var errProp)
                ? errProp.GetString()
                : "Unknown error.";
            Console.Error.WriteLine(error);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException)
        {
            Console.Error.WriteLine($"Enrollment channel failed: {ex.Message}");
            return 1;
        }
    }

    private static string SendWindows(string requestJson)
    {
        using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: ElevatedEnrollmentChannel.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);
        client.Connect(timeout: 5000);
        // ADR-0020: never send anything to a pipe server that is not the router service. The name is
        // predictable, so an unelevated process could have created it first.
        VerifyWindowsPipeServer(client);
        return ExchangeLine(client, requestJson);
    }

    /// <summary>
    /// Throws <see cref="IOException"/> unless the process serving <paramref name="client"/> is this same
    /// executable running in session 0, where only services run: an unelevated user's squatting process
    /// lives in an interactive session, and a copy of the binary elsewhere has a different image path.
    /// </summary>
    private static void VerifyWindowsPipeServer(NamedPipeClientStream client)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
            throw new IOException("Could not identify the enrollment pipe's server process.");

        using var server = System.Diagnostics.Process.GetProcessById((int)serverPid);
        var serverPath = server.MainModule?.FileName;
        var ownPath = Environment.ProcessPath;
        if (server.SessionId != 0 ||
            serverPath is null ||
            !string.Equals(Path.GetFullPath(serverPath), Path.GetFullPath(ownPath ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The enrollment pipe is not served by the router service; refusing to send.");
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
    }

    private static string SendUnix(string requestJson)
    {
        var path = Path.Combine(AppDataPaths.ResolveMachineSharedDirectory(), "enrollment.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket, ownsSocket: true);
        return ExchangeLine(stream, requestJson);
    }

    private static string ExchangeLine(Stream stream, string requestJson)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StreamWriter(stream, utf8, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
        };
        using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        writer.WriteLine(requestJson);
        return reader.ReadLine() ?? throw new IOException("Enrollment channel closed without a response.");
    }

    private static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        return Environment.UserName == "root" || Environment.GetEnvironmentVariable("SUDO_UID") is not null;
    }
}
