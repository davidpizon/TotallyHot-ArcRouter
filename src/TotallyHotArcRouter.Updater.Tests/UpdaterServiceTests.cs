using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TotallyHot.ArcRouter.Updater;

namespace TotallyHot.ArcRouter.Updater.Tests;

public class UpdaterServiceTests
{
    /// <summary>The digest every fixture expects; <see cref="FakeUpdateFileSystem"/> returns it by default so the happy path verifies.</summary>
    internal const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly UpdaterArguments Arguments = new(
        InstallDirectory: @"C:\Program Files\TotallyHotArcRouter\Router",
        ZipPath: @"C:\temp\update.zip",
        ServiceName: "TotallyHotArcRouter",
        WaitPid: 4242,
        ExpectedSha256: ValidSha256);

    private static UpdaterService CreateService(
        FakeProcessWaiter processWaiter,
        FakeServiceController serviceController,
        FakeUpdateFileSystem fileSystem) =>
        new(processWaiter, serviceController, fileSystem, NullLogger<UpdaterService>.Instance);

    [Fact]
    public async Task RunAsync_HappyPath_StopsSwapsStartsAndDeletesBackup()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController { RunningAfterStart = true };
        var fileSystem = new FakeUpdateFileSystem();
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(0);
        processWaiter.LastWaitedPid.Should().Be(Arguments.WaitPid);
        serviceController.Calls.Should().ContainInOrder("Stop:TotallyHotArcRouter", "Start:TotallyHotArcRouter", "IsRunning:TotallyHotArcRouter");
        fileSystem.Calls[0].Should().StartWith("Move:");
        fileSystem.Calls[1].Should().StartWith("Extract:");
        fileSystem.Calls[^1].Should().StartWith("Delete:");
    }

    [Fact]
    public async Task RunAsync_ChecksumMismatch_AbortsBeforeWaitingOrStoppingTheService()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem { Sha256Result = new string('f', 64) };
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        fileSystem.HashedPaths.Should().Equal(Arguments.ZipPath);
        // Nothing at all was touched: no PID wait, no service stop, no filesystem mutation.
        processWaiter.LastWaitedPid.Should().BeNull();
        serviceController.Calls.Should().BeEmpty();
        fileSystem.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ChecksumMatchesInDifferentCase_StillProceeds()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem { Sha256Result = ValidSha256.ToUpperInvariant() };
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_HashingThrows_AbortsWithoutTouchingAnything()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem { ComputeSha256Exception = new FileNotFoundException("gone") };
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        serviceController.Calls.Should().BeEmpty();
        fileSystem.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CallerProcessNeverExits_AbortsWithoutTouchingService()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = false };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem();
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        serviceController.Calls.Should().BeEmpty();
        fileSystem.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_StopServiceFails_AbortsWithoutTouchingFiles()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController { StopException = new TimeoutException("stop timed out") };
        var fileSystem = new FakeUpdateFileSystem();
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        fileSystem.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ExtractFails_RollsBackAndRestartsService()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem { ExtractException = new IOException("disk full") };
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        // Move (backup), Extract (fails), then rollback: Delete (the half-extracted install dir), Move (restore backup).
        fileSystem.Calls.Should().HaveCount(4);
        fileSystem.Calls[0].Should().StartWith("Move:");
        fileSystem.Calls[1].Should().StartWith("Extract:");
        fileSystem.Calls[2].Should().StartWith("Delete:");
        fileSystem.Calls[3].Should().StartWith("Move:");
        // Stopped once for the swap attempt, started again once for the rollback restart.
        serviceController.Calls.Should().Equal("Stop:TotallyHotArcRouter", "Start:TotallyHotArcRouter");
    }

    [Fact]
    public async Task RunAsync_ServiceFailsToStartAfterSwap_RollsBack()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController { StartException = new TimeoutException("start timed out") };
        var fileSystem = new FakeUpdateFileSystem();
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        // The rollback's own restart attempt also throws (StartException still set), so IsRunning never runs.
        serviceController.Calls.Should().NotContain(call => call.StartsWith("IsRunning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ServiceNotRunningAfterStart_RollsBack()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController { RunningAfterStart = false };
        var fileSystem = new FakeUpdateFileSystem();
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        // Started twice: once for the swap, once more for the rollback restart.
        serviceController.Calls.Count(call => call.StartsWith("Start:", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_BackupMoveFails_AttemptsRestartWithoutRollback()
    {
        var processWaiter = new FakeProcessWaiter { ExitResult = true };
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem { MoveException = new IOException("access denied") };
        var service = CreateService(processWaiter, serviceController, fileSystem);

        var exitCode = await service.RunAsync(Arguments, TestContext.Current.CancellationToken);

        exitCode.Should().Be(1);
        // Only one Move call (the failed backup attempt) - extraction never runs.
        fileSystem.Calls.Should().ContainSingle();
        serviceController.Calls.Should().Equal("Stop:TotallyHotArcRouter", "Start:TotallyHotArcRouter");
    }

    [Fact]
    public async Task RunAsync_NullArguments_Throws()
    {
        var service = CreateService(new FakeProcessWaiter(), new FakeServiceController(), new FakeUpdateFileSystem());

        var act = async () => await service.RunAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var processWaiter = new FakeProcessWaiter();
        var serviceController = new FakeServiceController();
        var fileSystem = new FakeUpdateFileSystem();
        var logger = NullLogger<UpdaterService>.Instance;

        ((Action)(() => new UpdaterService(null!, serviceController, fileSystem, logger))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new UpdaterService(processWaiter, null!, fileSystem, logger))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new UpdaterService(processWaiter, serviceController, null!, logger))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new UpdaterService(processWaiter, serviceController, fileSystem, null!))).Should().Throw<ArgumentNullException>();
    }
}
