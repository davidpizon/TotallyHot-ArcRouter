using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Talks to the in-guest exec agent over Firecracker's vsock, which is surfaced host-side as a Unix socket.
/// Connection uses Firecracker's vsock handshake (<c>CONNECT &lt;port&gt;\n</c> → <c>OK ...\n</c>), then
/// exchanges a single JSON request/response. The guest agent, snapshot, and rootfs are built out of band
/// (architecture §6); this client is the host end of that contract.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class VsockGuestAgentClient : IGuestAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<GuestExecResult> ExecuteAsync(
        string vsockUdsPath,
        uint port,
        SandboxLanguage language,
        string code,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(vsockUdsPath);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(vsockUdsPath), cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        // Firecracker vsock handshake to reach the guest-side listening port. A successful reply is
        // "OK <host_port>"; anything else means the guest port is unreachable, so fail fast with a clear
        // error instead of proceeding into a confusing JSON/IO failure.
        await WriteLineAsync(stream, $"CONNECT {port}", cancellationToken).ConfigureAwait(false);
        var handshake = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!handshake.StartsWith("OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Firecracker vsock handshake to guest port {port} failed: '{handshake}'.");
        }

        var request = new GuestExecRequest(language.ToString().ToLowerInvariant(), code, timeoutMs);
        var requestJson = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        await stream.WriteAsync(requestJson, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var responseLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<GuestExecResponse>(responseLine, JsonOptions)
            ?? throw new InvalidOperationException("Guest agent returned an empty response.");

        return new GuestExecResult(
            response.ExitCode,
            response.Stdout ?? string.Empty,
            response.Stderr ?? string.Empty,
            response.TimedOut,
            response.PeakMemoryBytes);
    }

    /// <summary>Writes a single newline-terminated ASCII line to the socket stream and flushes it, used for the vsock handshake.</summary>
    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads from the socket stream until a newline or end of stream, returning the decoded line without the terminator.</summary>
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, newline));
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    /// <summary>The JSON payload sent to the in-guest exec agent describing the code to run.</summary>
    /// <param name="Language">The language of the code, lowercased (e.g. "python", "csharp").</param>
    /// <param name="Code">The source code to execute inside the guest.</param>
    /// <param name="TimeoutMs">The maximum time, in milliseconds, the guest agent should allow the execution to run.</param>
    private sealed record GuestExecRequest(
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("timeout_ms")] int TimeoutMs);

    /// <summary>The JSON payload returned by the in-guest exec agent describing the outcome of an execution.</summary>
    private sealed record GuestExecResponse
    {
        /// <summary>The process exit code reported by the guest.</summary>
        [JsonPropertyName("exit_code")]
        public int ExitCode { get; init; }

        /// <summary>The captured standard output, if any.</summary>
        [JsonPropertyName("stdout")]
        public string? Stdout { get; init; }

        /// <summary>The captured standard error, if any.</summary>
        [JsonPropertyName("stderr")]
        public string? Stderr { get; init; }

        /// <summary>Whether the guest agent killed the execution after it exceeded its timeout.</summary>
        [JsonPropertyName("timed_out")]
        public bool TimedOut { get; init; }

        /// <summary>The peak resident memory, in bytes, observed by the guest agent during execution.</summary>
        [JsonPropertyName("peak_memory_bytes")]
        public long PeakMemoryBytes { get; init; }
    }
}

