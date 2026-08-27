using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Telemetry.Tests;

/// <summary>
/// Covers <see cref="MsiUpdateApplier"/>'s download/verify/launch pipeline against a faked
/// <see cref="HttpMessageHandler"/> and a fake <see cref="IElevatedProcessLauncher"/> - no real network
/// call, no real elevated process, no UAC prompt.
/// </summary>
public sealed class MsiUpdateApplierTests
{
    private const string MsiContent = "fake-msi-bytes";

    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeLauncher : IElevatedProcessLauncher
    {
        public (string FileName, IReadOnlyList<string> Arguments)? LastCall { get; private set; }
        public Exception? ThrowOnLaunch { get; set; }

        public void Launch(string fileName, IReadOnlyList<string> arguments)
        {
            if (ThrowOnLaunch is not null)
            {
                throw ThrowOnLaunch;
            }

            LastCall = (fileName, arguments);
        }
    }

    private static MsiUpdateApplier CreateApplier(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        FakeLauncher launcher) =>
        new(new HttpClient(new FakeHandler(respond)), NullLogger<MsiUpdateApplier>.Instance, launcher);

    [Fact]
    public async Task ApplyAsync_ValidChecksum_LaunchesMsiexecElevated()
    {
        var launcher = new FakeLauncher();
        var applier = CreateApplier(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MsiContent) },
            launcher);

        var result = await applier.ApplyAsync(
            "https://example.test/a.msi",
            Sha256Of(MsiContent),
            "2.0.0",
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        launcher.LastCall.Should().NotBeNull();
        launcher.LastCall!.Value.FileName.Should().Be("msiexec.exe");
        launcher.LastCall.Value.Arguments.Should().Contain("/qn");
        launcher.LastCall.Value.Arguments.Should().Contain("REBOOT=ReallySuppress");
    }

    [Fact]
    public async Task ApplyAsync_ChecksumMismatch_FailsWithoutLaunching()
    {
        var launcher = new FakeLauncher();
        var applier = CreateApplier(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MsiContent) },
            launcher);

        var result = await applier.ApplyAsync(
            "https://example.test/a.msi",
            "0000000000000000000000000000000000000000000000000000000000000",
            "2.0.0",
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        launcher.LastCall.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_DownloadFails_ReportsFailure()
    {
        var launcher = new FakeLauncher();
        var applier = CreateApplier(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), launcher);

        var result = await applier.ApplyAsync(
            "https://example.test/a.msi",
            Sha256Of(MsiContent),
            "2.0.0",
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        launcher.LastCall.Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_LaunchThrows_ReportsFailure()
    {
        var launcher = new FakeLauncher { ThrowOnLaunch = new InvalidOperationException("no elevation") };
        var applier = CreateApplier(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MsiContent) },
            launcher);

        var result = await applier.ApplyAsync(
            "https://example.test/a.msi",
            Sha256Of(MsiContent),
            "2.0.0",
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("no elevation");
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var act1 = () => new MsiUpdateApplier(null!, NullLogger<MsiUpdateApplier>.Instance);
        var act2 = () => new MsiUpdateApplier(new HttpClient(), null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ElevatedProcessLauncher_CanBeConstructed()
    {
        var launcher = new ElevatedProcessLauncher();

        launcher.Should().NotBeNull();
    }
}
