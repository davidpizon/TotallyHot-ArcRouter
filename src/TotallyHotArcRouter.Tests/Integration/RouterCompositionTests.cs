using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Covers integration composition for router services.
/// </summary>
/// <remarks>
/// Every provider this class builds is pointed at a throwaway database under <see cref="_databaseDirectory"/>.
/// That is not tidiness: <see cref="RoutingOptions.EmbeddingMemoryDatabasePath"/> defaults to the bare file
/// name <c>router_embedding_memory.db</c>, and <c>RouterMemoryDatabase</c> resolves a relative path against
/// the machine-shared data directory - so a provider built from default options writes to the *installed
/// router's* database. On a developer machine where the service is running, that surfaces as
/// <c>SQLite Error 8: 'attempt to write a readonly database'</c>, because the live router holds the file in
/// WAL mode and its LocalSystem-owned <c>-wal</c> sidecar grants <c>BUILTIN\Users</c> read only. CI never
/// caught it: no router is installed there, so the default path is writable and the tests passed.
/// </remarks>
[Collection("Integration")]
public class RouterCompositionTests : IAsyncDisposable
{
    private readonly string _databaseDirectory =
        Path.Combine(path1: Path.GetTempPath(), path2: "arcrouter-tests", path3: Guid.NewGuid().ToString("N"));

    private readonly List<ServiceProvider> _providers = [];

    /// <summary>
    /// Disposes every provider this test built - releasing its SQLite handles - then deletes the throwaway
    /// database directory, sidecar files included.
    /// </summary>
    /// <remarks>
    /// Asynchronous because the composed graph reaches <c>OnnxTextGenerationClient</c>, which implements
    /// <see cref="IAsyncDisposable"/> and not <see cref="IDisposable"/>; a synchronous
    /// <c>ServiceProvider.Dispose()</c> over it throws rather than degrading.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            try
            {
                await provider.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // Best-effort teardown; a provider already torn down is not a test failure.
            }

        // ClearPool over the databases this test actually opened, never the process-global ClearAllPools:
        // under xUnit's parallel execution the latter can tear down a pooled native sqlite3 handle out from
        // under an unrelated test's in-flight query. Same reasoning as TempDatabase.Dispose.
        if (Directory.Exists(_databaseDirectory))
            foreach (var file in Directory.EnumerateFiles(path: _databaseDirectory, searchPattern: "*.db"))
                try
                {
                    // await using, not using: SqliteConnection closes asynchronously through
                    // IAsyncDisposable, and this teardown is already async - TempDatabase's synchronous
                    // Dispose has no such option, which is the only reason it spells this `using`.
                    await using var connection =
                        new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString());
                    SqliteConnection.ClearPool(connection);
                }
                catch (SqliteException)
                {
                    // Best-effort cleanup; a database mid-teardown on a busy CI box is not a test failure.
                }

        try
        {
            if (Directory.Exists(_databaseDirectory)) Directory.Delete(path: _databaseDirectory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on a busy CI box is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RouterComposition_ObserveThenRoute_SelectsBestModel()
    {
        var provider = BuildProvider(new RoutingOptions
        {
            EnableExploration = false,
            DefaultModel = RouterConstants.DefaultModel,
            EmbeddingMemoryDatabasePath = IsolatedDatabasePath()
        });

        var router = provider.GetRequiredService<AgentAsARouter>();

        await router.ObserveAsync(dimension: "code_gen", model: "gpt-5.4", 0.9);
        await router.ObserveAsync(dimension: "code_gen", model: "qwen3-max", 0.7);

        var decision = await router.SelectModelAsync(dimension: "code_gen",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "gpt-5.4", actual: decision.SelectedModel);
    }

    [Fact]
    public async Task RouterComposition_WithoutHistory_UsesFallbackDefaultModel()
    {
        var provider = BuildProvider(new RoutingOptions
        {
            EnableExploration = false,
            DefaultModel = RouterConstants.DefaultModel,
            EmbeddingMemoryDatabasePath = IsolatedDatabasePath()
        });

        var router = provider.GetRequiredService<AgentAsARouter>();

        var decision = await router.SelectModelAsync(dimension: "unknown_dimension",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: RouterConstants.DefaultModel, actual: decision.SelectedModel);
        Assert.Equal(expected: RouterConstants.FallbackReason, actual: decision.Rationale);
    }

    [Fact]
    public void RouterComposition_CanResolveCoreServices()
    {
        var provider = BuildProvider(new RoutingOptions { EmbeddingMemoryDatabasePath = IsolatedDatabasePath() });

        Assert.NotNull(provider.GetRequiredService<AgentAsARouter>());
        Assert.NotNull(provider.GetRequiredService<RouterMemory>());
        Assert.NotNull(provider.GetRequiredService<RequestInterceptor>());
        Assert.NotNull(provider.GetRequiredService<IRoutingPolicy>());
    }

    /// <summary>
    /// A unique, <b>rooted</b> database path under this test's own temp directory. Rooted is the
    /// load-bearing part: <c>RouterMemoryDatabase</c> resolves a relative path against the machine-shared
    /// directory, which is the installed router's database. See the type's remarks.
    /// </summary>
    private string IsolatedDatabasePath()
    {
        Directory.CreateDirectory(_databaseDirectory);
        return Path.Combine(path1: _databaseDirectory, path2: $"router_embedding_memory_{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// Builds a provider over <paramref name="options"/> and tracks it for disposal.
    /// </summary>
    /// <param name="options">
    /// Router options whose <see cref="RoutingOptions.EmbeddingMemoryDatabasePath"/> must already be a
    /// rooted path - use <see cref="IsolatedDatabasePath"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="options"/> carries a relative database path. Thrown rather than silently allowed:
    /// the whole point of this guard is that the failure it prevents is a write to the real router's
    /// database, which passes on CI and only breaks on a machine with the service installed.
    /// </exception>
    private ServiceProvider BuildProvider(RoutingOptions options)
    {
        if (!Path.IsPathRooted(options.EmbeddingMemoryDatabasePath))
            throw new InvalidOperationException(
                $"{nameof(RoutingOptions.EmbeddingMemoryDatabasePath)} must be a rooted path from " +
                $"{nameof(IsolatedDatabasePath)}(); a relative one resolves against the machine-shared " +
                "directory and writes to the installed router's database.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddTotallyHotArcRouter();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }
}
