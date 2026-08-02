using System.Text.Json;
using TotallyHot.ArcRouter.Sandbox.Firecracker;

namespace TotallyHot.ArcRouter.Sandbox.Tests;

/// <summary>Covers Firecracker argument/payload construction, including the no-network guarantee.</summary>
public class FirecrackerArgumentBuilderTests
{
    [Fact]
    public void BuildFirecrackerArgs_UsesApiSocket()
    {
        var args = FirecrackerArgumentBuilder.BuildFirecrackerArgs("/tmp/api.sock");

        Assert.Equal(new[] { "--api-sock", "/tmp/api.sock" }, args);
    }

    [Fact]
    public void BuildMachineConfig_HasNoNetworkInterface()
    {
        var json = FirecrackerArgumentBuilder.BuildMachineConfig(vcpuCount: 2, memoryMaxBytes: 134217728);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("vcpu_count").GetInt32());
        Assert.Equal(128, root.GetProperty("mem_size_mib").GetInt64());
        Assert.False(root.TryGetProperty("network_interfaces", out _));
    }

    [Fact]
    public void BuildVsockConfig_SetsGuestCidAndUdsPath()
    {
        var json = FirecrackerArgumentBuilder.BuildVsockConfig(guestCid: 3, udsPath: "/tmp/run/firecracker-vsock.sock");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(3u, root.GetProperty("guest_cid").GetUInt32());
        Assert.Equal("/tmp/run/firecracker-vsock.sock", root.GetProperty("uds_path").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("vsock_id").GetString()));
    }

    [Fact]
    public void BuildVsockConfig_RejectsEmptyUdsPath()
    {
        Assert.Throws<ArgumentException>(() => FirecrackerArgumentBuilder.BuildVsockConfig(guestCid: 3, udsPath: string.Empty));
    }

    [Fact]
    public void BuildSnapshotLoadPayload_RequestsResume()
    {
        var json = FirecrackerArgumentBuilder.BuildSnapshotLoadPayload("/snap/state", "/snap/mem", resumeVm: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("/snap/state", root.GetProperty("snapshot_path").GetString());
        Assert.True(root.GetProperty("resume_vm").GetBoolean());
        Assert.Equal("/snap/mem", root.GetProperty("mem_backend").GetProperty("backend_path").GetString());
    }

    [Theory]
    [InlineData(true, "Resumed")]
    [InlineData(false, "Paused")]
    public void BuildVmStatePayload_SetsState(bool resumed, string expected)
    {
        var json = FirecrackerArgumentBuilder.BuildVmStatePayload(resumed);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void ToMib_ConvertsBytesToMebibytes()
    {
        Assert.Equal("128", FirecrackerArgumentBuilder.ToMib(134217728));
        Assert.Equal("0", FirecrackerArgumentBuilder.ToMib(1024));
    }
}

