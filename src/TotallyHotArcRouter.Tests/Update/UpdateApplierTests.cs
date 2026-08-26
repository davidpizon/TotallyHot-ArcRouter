using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Tests.CodeRouterBench;
using TotallyHot.ArcRouter.Update;

namespace TotallyHot.ArcRouter.Tests.Update;

/// <summary>
/// Covers <see cref="UpdateApplier"/>: the guard failures that don't require an actual sibling
/// <c>Updater\TotallyHotArcRouter.Updater.exe</c> layout, and the
/// download/verify/refresh-the-updater/hand-off sequence exercised against a fake
/// <see cref="IUpdateFileOperations"/> (so the backup/extract/restore failure paths run with no real
/// <c>%ProgramFiles%</c> access) plus a fake <see cref="IUpdaterProcessLauncher"/> (so no real process is
/// ever spawned).
/// </summary>
public sealed class UpdateApplierTests
{
    private const string RouterZipBody = "fake-router-zip-bytes";
    private const string UpdaterZipBody = "fake-updater-zip-bytes";
    private const string RouterUrl = "https://example.test/TotallyHotArcRouter-Router-win-x64.zip";
    private const string UpdaterUrl = "https://example.test/TotallyHotArcRouter-Updater-win-x64.zip";

    private static readonly string RouterSha = Sha256Of(RouterZipBody);
    private static readonly string UpdaterSha = Sha256Of(UpdaterZipBody);

    /// <summary>Records every call, and lets a test make any single operation throw. Starts out with the updater executable present, matching a healthy install.</summary>
    private sealed class FakeFileOperations : IUpdateFileOperations
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public Exception? MoveException { get; set; }

        public Exception? ExtractException { get; set; }

        /// <summary>When set, the second <see cref="MoveDirectory"/> call (the restore) throws this instead of the first (the backup).</summary>
        public Exception? RestoreMoveException { get; set; }

        /// <summary>When <see langword="true"/>, <see cref="ExtractZip"/> produces a directory that does not contain the updater executable.</summary>
        public bool ExtractOmitsUpdaterExecutable { get; set; }

        private int _moveCount;

        public void AddFile(string path) => _files.Add(path);

        public bool FileExists(string path) => _files.Contains(path);

        public void MoveDirectory(string source, string destination)
        {
            Calls.Add($"Move:{source}->{destination}");
            _moveCount++;
            if (_moveCount == 1 && MoveException is not null)
            {
                throw MoveException;
            }

            if (_moveCount == 2 && RestoreMoveException is not null)
            {
                throw RestoreMoveException;
            }

            Rename(source, destination);
        }

        public void ExtractZip(string zipPath, string destinationDirectory)
        {
            Calls.Add($"Extract:{zipPath}->{destinationDirectory}");
            if (ExtractException is not null)
            {
                throw ExtractException;
            }

            if (!ExtractOmitsUpdaterExecutable)
            {
                _files.Add(Path.Combine(destinationDirectory, "TotallyHotArcRouter.Updater.exe"));
            }
        }

        public void DeleteDirectory(string path)
        {
            Calls.Add($"Delete:{path}");
            _files.RemoveWhere(file => file.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private void Rename(string source, string destination)
        {
            foreach (var file in _files.Where(file => file.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _files.Remove(file);
                _files.Add(destination + file[source.Length..]);
            }
        }
    }

    private sealed class FakeLauncher : IUpdaterProcessLauncher
    {
        public int LaunchCount { get; private set; }

        public (string UpdaterPath, string InstallDirectory, string ZipPath, string ServiceName, string ExpectedSha256)? LastCall { get; private set; }

        public void Launch(string updaterPath, string installDirectory, string zipPath, string serviceName, string expectedSha256)
        {
            LaunchCount++;
            LastCall = (updaterPath, installDirectory, zipPath, serviceName, expectedSha256);
        }
    }

    private sealed class ThrowingLauncher : IUpdaterProcessLauncher
    {
        public void Launch(string updaterPath, string installDirectory, string zipPath, string serviceName, string expectedSha256) =>
            throw new InvalidOperationException("spawn failed");
    }

    // UpdateApplier resolves the updater as a sibling "Updater\TotallyHotArcRouter.Updater.exe" of this
    // test binary's own directory's *parent*. Nothing is created on disk: the fake IUpdateFileOperations
    // answers every existence probe, so these are only the paths the applier will compute.
    private static readonly string ParentDirectory =
        Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;

    private static readonly string UpdaterDirectory = Path.Combine(ParentDirectory, "Updater");
    private static readonly string UpdaterExePath = Path.Combine(UpdaterDirectory, "TotallyHotArcRouter.Updater.exe");

    private static string Sha256Of(string body) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));

    /// <summary>An "update available" result carrying both assets, as a real release check produces.</summary>
    private static ReleaseCheckResult Available(string? routerSha = null, string? updaterSha = null) =>
        ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, RouterUrl, routerSha ?? RouterSha, UpdaterUrl, updaterSha ?? UpdaterSha);

    /// <summary>Responds to the Router URL and the Updater URL with their respective bodies, and 500s anything else.</summary>
    private static FakeHttpMessageHandler BothAssetsHandler() =>
        new(request => request.RequestUri!.AbsoluteUri switch
        {
            RouterUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RouterZipBody) },
            UpdaterUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UpdaterZipBody) },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        });

    private static FakeFileOperations HealthyInstall()
    {
        var operations = new FakeFileOperations();
        operations.AddFile(UpdaterExePath);
        return operations;
    }

    private static UpdateApplier CreateApplier(
        HttpMessageHandler? handler = null,
        IUpdaterProcessLauncher? launcher = null,
        IUpdateFileOperations? fileOperations = null) =>
        new(
            handler is null ? new HttpClient() : new HttpClient(handler),
            Options.Create(new UpdateOptions { ServiceName = "TotallyHotArcRouter" }),
            NullLogger<UpdateApplier>.Instance,
            launcher,
            fileOperations);

    [Fact]
    public async Task ApplyAsync_UpdateNotAvailable_FailsWithoutDownloading()
    {
        var applier = CreateApplier();
        var notAvailable = ReleaseCheckResult.Resolved("1.0.0", "1.0.0", false, null, null);

        var result = await applier.ApplyAsync(notAvailable, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterAssetMissingFromResult_FailsWithoutDownloading()
    {
        var launcher = new FakeLauncher();
        var applier = CreateApplier(launcher: launcher, fileOperations: HealthyInstall());
        var routerOnly = ReleaseCheckResult.Resolved("1.0.0", "2.0.0", true, RouterUrl, RouterSha);

        var result = await applier.ApplyAsync(routerOnly, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterExecutableMissing_FailsWithoutDownloading()
    {
        // No file registered with the fake, so the layout guard trips before any HttpClient call.
        var applier = CreateApplier(fileOperations: new FakeFileOperations());

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Updater", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_NullUpdate_Throws()
    {
        var applier = CreateApplier();

        await Assert.ThrowsAsync<ArgumentNullException>(() => applier.ApplyAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var httpClient = new HttpClient();
        var options = Options.Create(new UpdateOptions());
        var logger = NullLogger<UpdateApplier>.Instance;

        Assert.Throws<ArgumentNullException>(() => new UpdateApplier(null!, options, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateApplier(httpClient, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new UpdateApplier(httpClient, options, null!));
    }

    [Fact]
    public async Task ApplyAsync_HappyPath_RefreshesUpdaterThenLaunchesItWithTheRouterChecksum()
    {
        var operations = HealthyInstall();
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(UpdaterExePath, launcher.LastCall!.Value.UpdaterPath);
        Assert.Equal("TotallyHotArcRouter", launcher.LastCall!.Value.ServiceName);
        // The Router zip's own checksum is handed to the updater so it can re-verify at its trust boundary.
        Assert.Equal(RouterSha, launcher.LastCall!.Value.ExpectedSha256);

        // Backup aside, extract the new updater in place, delete the backup - in that order, and all of it
        // before the launch.
        Assert.Equal(3, operations.Calls.Count);
        Assert.StartsWith($"Move:{UpdaterDirectory}->{UpdaterDirectory}.backup-", operations.Calls[0], StringComparison.Ordinal);
        Assert.StartsWith("Extract:", operations.Calls[1], StringComparison.Ordinal);
        Assert.StartsWith($"Delete:{UpdaterDirectory}.backup-", operations.Calls[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_RouterChecksumMismatch_FailsWithoutTouchingTheUpdater()
    {
        var operations = HealthyInstall();
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(routerSha: new string('0', 64)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterChecksumMismatch_FailsWithoutTouchingAnything()
    {
        var operations = HealthyInstall();
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(updaterSha: new string('0', 64)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Updater", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public async Task ApplyAsync_RouterDownloadFails_FailsWithoutLaunching()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var operations = HealthyInstall();
        var launcher = new FakeLauncher();
        var applier = CreateApplier(handler, launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Download failed", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterDownloadFails_FailsWithoutTouchingAnything()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsoluteUri == RouterUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RouterZipBody) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var operations = HealthyInstall();
        var launcher = new FakeLauncher();
        var applier = CreateApplier(handler, launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Updater", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterBackupFails_AbortsWithoutExtractingOrLaunching()
    {
        var operations = HealthyInstall();
        operations.MoveException = new IOException("access denied");
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Single(operations.Calls);
        Assert.StartsWith("Move:", operations.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_UpdaterExtractionFails_RestoresBackupAndNeverTouchesTheRouter()
    {
        var operations = HealthyInstall();
        operations.ExtractException = new InvalidDataException("corrupt archive");
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("restored", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        // Backup, failed extract, delete the half-written directory, move the backup back.
        Assert.Equal(4, operations.Calls.Count);
        Assert.StartsWith("Move:", operations.Calls[0], StringComparison.Ordinal);
        Assert.StartsWith("Extract:", operations.Calls[1], StringComparison.Ordinal);
        Assert.StartsWith($"Delete:{UpdaterDirectory}", operations.Calls[2], StringComparison.Ordinal);
        Assert.EndsWith($"->{UpdaterDirectory}", operations.Calls[3], StringComparison.Ordinal);
        // And the previous updater is back where it belongs.
        Assert.True(operations.FileExists(UpdaterExePath));
    }

    [Fact]
    public async Task ApplyAsync_ExtractedUpdaterMissingExecutable_RestoresBackupAndAborts()
    {
        var operations = HealthyInstall();
        operations.ExtractOmitsUpdaterExecutable = true;
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.True(operations.FileExists(UpdaterExePath));
    }

    [Fact]
    public async Task ApplyAsync_UpdaterRestoreAlsoFails_AbortsWithoutProceedingToTheRouterSwap()
    {
        var operations = HealthyInstall();
        operations.ExtractException = new IOException("disk full");
        operations.RestoreMoveException = new IOException("backup is locked");
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Manual intervention", result.Message, StringComparison.Ordinal);
        Assert.Contains("Router was not modified", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task ApplyAsync_BackupDeletionFails_StillSucceeds()
    {
        var operations = new DeleteFailingFileOperations();
        operations.AddFile(UpdaterExePath);
        var launcher = new FakeLauncher();
        var applier = CreateApplier(BothAssetsHandler(), launcher, operations);

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, launcher.LaunchCount);
    }

    /// <summary>A <see cref="FakeFileOperations"/> whose backup cleanup always fails - a cosmetic failure that must not abort a successful updater refresh.</summary>
    private sealed class DeleteFailingFileOperations : IUpdateFileOperations
    {
        private readonly FakeFileOperations _inner = new();

        public void AddFile(string path) => _inner.AddFile(path);

        public bool FileExists(string path) => _inner.FileExists(path);

        public void MoveDirectory(string source, string destination) => _inner.MoveDirectory(source, destination);

        public void ExtractZip(string zipPath, string destinationDirectory) => _inner.ExtractZip(zipPath, destinationDirectory);

        public void DeleteDirectory(string path) => throw new IOException("directory in use");
    }

    [Fact]
    public async Task ApplyAsync_LauncherThrows_FailsGracefully()
    {
        var applier = CreateApplier(BothAssetsHandler(), new ThrowingLauncher(), HealthyInstall());

        var result = await applier.ApplyAsync(Available(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not launch", result.Message, StringComparison.Ordinal);
    }
}
