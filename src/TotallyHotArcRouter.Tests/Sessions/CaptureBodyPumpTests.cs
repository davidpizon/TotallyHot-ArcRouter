using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins the background pump that keeps compression, encryption and disk writes off the proxy's relay path: bytes
/// are applied in the order they were queued, a body that cannot be finished is reported as missing rather than
/// as a prefix, a full queue drops the body that would overflow it, and no spool is left behind in any case.
/// </summary>
[Collection(SessionStorageCollection.Name)]
public sealed class CaptureBodyPumpTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestScratchDirectory.RunRoot, "pump-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Scratch cleanup is also done by TestTempDirectorySweeper.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>Queued writes become one complete spool; the bytes themselves are pinned byte-exact through the proxy tests.</summary>
    [Fact]
    public async Task WritesThenComplete_YieldsOneCompleteSpool()
    {
        using var pump = await StartAsync();
        var body = pump.CreateBody(NewSpool);

        body.Write("one "u8);
        body.Write("two "u8);
        body.Write("three"u8);
        var spool = await body.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await pump.StopAsync(CancellationToken.None);

        Assert.NotNull(spool);
        Assert.True(spool.IsComplete);
        spool.Dispose();
        Assert.Empty(SpoolFiles());
    }

    /// <summary>A body with no bytes still completes to a spool, so it is stored as empty and not as missing.</summary>
    [Fact]
    public async Task CompleteWithNoWrites_YieldsACompleteEmptySpool()
    {
        using var pump = await StartAsync();

        var spool = await pump.CreateBody(NewSpool).CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(spool);
        Assert.True(spool.IsComplete);
        spool.Dispose();
    }

    /// <summary>A released body takes no further writes, completes to nothing, and leaves no file.</summary>
    [Fact]
    public async Task Release_DropsTheBodyAndDeletesItsFile()
    {
        using var pump = await StartAsync();
        var body = pump.CreateBody(NewSpool);
        body.Write("before"u8);

        body.Release();
        body.Write("after"u8);
        var spool = await body.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await pump.StopAsync(CancellationToken.None);

        Assert.Null(spool);
        Assert.Empty(SpoolFiles());
    }

    /// <summary>A write that would push the queue past its byte limit drops that body, and nothing is stored as a prefix.</summary>
    [Fact]
    public async Task QueueLimit_DropsTheBodyThatWouldOverflowIt()
    {
        using var pump = await StartAsync(new SessionCaptureOptions { PumpMaxQueuedBytes = 16 });
        var body = pump.CreateBody(NewSpool);

        body.Write(new byte[64]);
        var spool = await body.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await pump.StopAsync(CancellationToken.None);

        Assert.Null(spool);
        Assert.Empty(SpoolFiles());
    }

    /// <summary>The byte budget is returned as writes are applied, so a stream of small writes is never starved.</summary>
    [Fact]
    public async Task QueueLimit_IsGivenBackAsWritesAreApplied()
    {
        using var pump = await StartAsync(new SessionCaptureOptions { PumpMaxQueuedBytes = 1024 });

        // Each body is finished (so its write was applied) before the next starts: two 512-byte writes would
        // already exceed the 1 KiB limit if applied writes were not given back.
        for (var i = 0; i < 100; i++)
        {
            var body = pump.CreateBody(NewSpool);
            body.Write(new byte[512]);
            var spool = await body.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotNull(spool);
            spool.Dispose();
        }

        await pump.StopAsync(CancellationToken.None);
    }

    /// <summary>A spool that cannot be created is reported as a missing body and does not stop the worker.</summary>
    [Fact]
    public async Task SpoolCreationFailure_IsReportedAsMissing_AndTheWorkerSurvives()
    {
        using var pump = await StartAsync();
        var failing = pump.CreateBody(() => throw new IOException("disk gone"));
        failing.Write("x"u8);

        Assert.Null(await failing.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        var healthy = pump.CreateBody(NewSpool);
        healthy.Write("y"u8);
        var spool = await healthy.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(spool);
        spool.Dispose();
    }

    /// <summary>Once the pump has stopped, a body is dropped immediately instead of waiting for a worker that is gone.</summary>
    [Fact]
    public async Task AfterStop_CompleteReturnsNothingWithoutWaiting()
    {
        using var pump = await StartAsync();
        var body = pump.CreateBody(NewSpool);
        await pump.StopAsync(CancellationToken.None);

        body.Write("late"u8);
        var spool = await body.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(spool);
    }

    /// <summary>Stopping drains what is queued, so a turn finished just before shutdown still gets its spool.</summary>
    [Fact]
    public async Task Stop_DrainsQueuedWork()
    {
        using var pump = await StartAsync();
        var body = pump.CreateBody(NewSpool);
        body.Write("queued"u8);
        var completion = body.CompleteAsync();

        await pump.StopAsync(CancellationToken.None);
        var spool = await completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(spool);
        Assert.True(spool.IsComplete);
        spool.Dispose();
        Assert.Empty(SpoolFiles());
    }

    private SessionBodySpool NewSpool() => SessionBodySpool.Create(_folder);

    private string[] SpoolFiles() =>
        Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*" + SessionBodySpool.FileExtension) : [];

    private static async Task<CaptureBodyPump> StartAsync(SessionCaptureOptions? options = null)
    {
        var pump = new CaptureBodyPump(Options.Create(options ?? new SessionCaptureOptions()), NullLogger<CaptureBodyPump>.Instance);
        await pump.StartAsync(CancellationToken.None);
        return pump;
    }
}
