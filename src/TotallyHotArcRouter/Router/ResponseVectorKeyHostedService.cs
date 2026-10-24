namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Loads the response-vector key at startup. A missing key deletes every <c>response_vectors</c> row and
/// starts a new key before the proxy accepts traffic, which is the recovery a shred leaves behind.
/// </summary>
public sealed class ResponseVectorKeyHostedService : IHostedService
{
    private readonly ResponseVectorKeyHolder _keys;

    /// <summary>Initializes a new instance of the <see cref="ResponseVectorKeyHostedService"/> class.</summary>
    /// <param name="keys">The key holder to warm.</param>
    public ResponseVectorKeyHostedService(ResponseVectorKeyHolder keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.GetOrCreate();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
