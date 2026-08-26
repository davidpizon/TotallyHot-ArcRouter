namespace TotallyHot.ArcRouter.Updater.Tests;

/// <summary>Fake <see cref="IProcessWaiter"/> whose result and outcome are set directly by the test.</summary>
internal sealed class FakeProcessWaiter : IProcessWaiter
{
    public bool ExitResult { get; set; } = true;
    public int? LastWaitedPid { get; private set; }

    public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        LastWaitedPid = processId;
        return Task.FromResult(ExitResult);
    }
}

/// <summary>Fake <see cref="IServiceController"/> recording calls and throwing whatever the test configures.</summary>
internal sealed class FakeServiceController : IServiceController
{
    public List<string> Calls { get; } = [];
    public Exception? StopException { get; set; }
    public Exception? StartException { get; set; }
    public bool RunningAfterStart { get; set; } = true;

    public void Stop(string serviceName, TimeSpan timeout)
    {
        Calls.Add($"Stop:{serviceName}");
        if (StopException is not null)
        {
            throw StopException;
        }
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        Calls.Add($"Start:{serviceName}");
        if (StartException is not null)
        {
            throw StartException;
        }
    }

    public bool IsRunning(string serviceName)
    {
        Calls.Add($"IsRunning:{serviceName}");
        return RunningAfterStart;
    }
}

/// <summary>Fake <see cref="IUpdateFileSystem"/> tracking calls in memory, throwing whatever the test configures.</summary>
internal sealed class FakeUpdateFileSystem : IUpdateFileSystem
{
    public List<string> Calls { get; } = [];
    public Exception? MoveException { get; set; }
    public Exception? ExtractException { get; set; }
    public Exception? ComputeSha256Exception { get; set; }

    /// <summary>What <see cref="ComputeSha256"/> returns; defaults to <see cref="UpdaterServiceTests.ValidSha256"/> so the happy path needs no setup.</summary>
    public string Sha256Result { get; set; } = UpdaterServiceTests.ValidSha256;

    private readonly HashSet<string> _directories = [];

    /// <summary>Paths passed to <see cref="ComputeSha256"/>. Tracked separately from <see cref="Calls"/>, which records only mutating operations so "touched nothing" assertions stay meaningful.</summary>
    public List<string> HashedPaths { get; } = [];

    public string ComputeSha256(string path)
    {
        HashedPaths.Add(path);
        if (ComputeSha256Exception is not null)
        {
            throw ComputeSha256Exception;
        }

        return Sha256Result;
    }

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public void MoveDirectory(string source, string destination)
    {
        Calls.Add($"Move:{source}->{destination}");
        if (MoveException is not null)
        {
            throw MoveException;
        }

        _directories.Remove(source);
        _directories.Add(destination);
    }

    public void ExtractZip(string zipPath, string destinationDirectory)
    {
        Calls.Add($"Extract:{zipPath}->{destinationDirectory}");
        if (ExtractException is not null)
        {
            throw ExtractException;
        }

        _directories.Add(destinationDirectory);
    }

    public void DeleteDirectory(string path)
    {
        Calls.Add($"Delete:{path}");
        _directories.Remove(path);
    }
}
