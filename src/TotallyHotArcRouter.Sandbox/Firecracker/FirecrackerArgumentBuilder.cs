using System.Globalization;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Sandbox.Firecracker;

/// <summary>
/// Builds Firecracker process arguments and API payloads. Pure/OS-agnostic so it is unit-testable; the
/// launcher feeds these to the real Firecracker binary and API socket. The microVM is configured with
/// <b>no</b> network interface, so the guest is air-gapped at the device-model level (architecture §5.1).
/// </summary>
public static class FirecrackerArgumentBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Builds the Firecracker process arguments for an API-driven (snapshot-restore) launch.</summary>
    /// <param name="apiSocketPath">The path of the API Unix socket Firecracker should create.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> BuildFirecrackerArgs(string apiSocketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiSocketPath);
        return ["--api-sock", apiSocketPath];
    }

    /// <summary>Builds the <c>PUT /machine-config</c> payload with the given vCPU and memory sizing.</summary>
    /// <param name="vcpuCount">The number of vCPUs.</param>
    /// <param name="memoryMaxBytes">The memory ceiling in bytes (converted to MiB).</param>
    /// <returns>A JSON payload.</returns>
    public static string BuildMachineConfig(int vcpuCount, long memoryMaxBytes)
    {
        var memMib = Math.Max(1, memoryMaxBytes / (1024 * 1024));
        var config = new
        {
            vcpu_count = Math.Max(1, vcpuCount),
            mem_size_mib = memMib,
            smt = false,
            // No network_interfaces are ever configured: the guest has no virtio-net device.
        };

        return JsonSerializer.Serialize(config, SerializerOptions);
    }

    /// <summary>Builds the <c>PUT /vsock</c> payload wiring the guest's vsock device to a host-side UDS.</summary>
    /// <param name="guestCid">The guest vsock context id.</param>
    /// <param name="udsPath">The host-side Unix socket path Firecracker creates and listens on once the guest boots.</param>
    /// <returns>A JSON payload.</returns>
    public static string BuildVsockConfig(uint guestCid, string udsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(udsPath);

        var config = new
        {
            // vsock_id is an arbitrary device identifier Firecracker's API requires but never surfaces
            // elsewhere - this microVM has exactly one vsock device, so a fixed name is fine.
            vsock_id = "vsock0",
            guest_cid = guestCid,
            uds_path = udsPath,
        };

        return JsonSerializer.Serialize(config, SerializerOptions);
    }

    /// <summary>Builds the <c>PUT /snapshot/load</c> payload that restores a pre-booted guest.</summary>
    /// <param name="snapshotPath">The VM state snapshot file path.</param>
    /// <param name="memoryPath">The guest memory snapshot file path.</param>
    /// <param name="resumeVm">Whether to resume the guest immediately after loading.</param>
    /// <returns>A JSON payload.</returns>
    public static string BuildSnapshotLoadPayload(string snapshotPath, string memoryPath, bool resumeVm)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        ArgumentException.ThrowIfNullOrEmpty(memoryPath);

        var payload = new
        {
            snapshot_path = snapshotPath,
            mem_backend = new { backend_type = "File", backend_path = memoryPath },
            enable_diff_snapshots = false,
            resume_vm = resumeVm,
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>Builds the <c>PATCH /vm</c> payload that pauses or resumes the guest.</summary>
    /// <param name="resumed"><see langword="true"/> to resume; <see langword="false"/> to pause.</param>
    /// <returns>A JSON payload.</returns>
    public static string BuildVmStatePayload(bool resumed)
    {
        var payload = new { state = resumed ? "Resumed" : "Paused" };
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>Formats a memory size in bytes as MiB for logging/diagnostics.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The MiB count as an invariant string.</returns>
    public static string ToMib(long bytes) =>
        (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
}

