using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Capped-backoff retry for provider cost-reconciliation HTTP calls (§5.8's "capped-backoff retry on
/// 429/408/5xx" honeycomb discipline), shared by every <see cref="IProviderCostReconciler"/> so the
/// policy can't drift between providers.
/// </summary>
public static class CostReconciliationRetryPolicy
{
    private static readonly HashSet<int> RetryableStatusCodes = [408, 429, 500, 502, 503, 504];

    /// <summary>The maximum number of attempts total (the first try plus up to <c>MaxAttempts - 1</c> retries).</summary>
    public const int MaxAttempts = 4;

    /// <summary>
    /// Sends the request built by <paramref name="requestFactory"/> (invoked fresh on every attempt,
    /// since a sent <see cref="HttpRequestMessage"/> cannot be resent), retrying on a 408/429/5xx response
    /// or a transient <see cref="HttpRequestException"/>, up to <see cref="MaxAttempts"/> tries total, with
    /// exponential backoff capped at 30 seconds between attempts.
    /// </summary>
    /// <param name="httpClient">The client to send with.</param>
    /// <param name="requestFactory">Builds a fresh request for each attempt.</param>
    /// <param name="delayForAttempt">
    /// Computes the delay before attempt <c>n+1</c> given attempt number <c>n</c> (1-based). Defaults to
    /// <c>min(30s, 2^n s)</c>; overridable so tests can run without real sleeps.
    /// </param>
    /// <param name="logger">Optional logger for retry visibility.</param>
    /// <param name="cancellationToken">Cancels the whole retry loop, including any pending backoff delay.</param>
    /// <returns>The final response - either the first success, or the last attempt's response/exception once attempts are exhausted.</returns>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        Func<int, TimeSpan>? delayForAttempt = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(requestFactory);

        delayForAttempt ??= attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var isLastAttempt = attempt == MaxAttempts;
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (isLastAttempt || !RetryableStatusCodes.Contains((int)response.StatusCode))
                {
                    return response;
                }

                logger?.LogWarning(
                    "Cost reconciliation request returned {StatusCode}; retrying (attempt {Attempt}/{MaxAttempts}).",
                    (int)response.StatusCode,
                    attempt,
                    MaxAttempts);
                response.Dispose();
            }
            catch (HttpRequestException ex) when (!isLastAttempt)
            {
                response?.Dispose();
                logger?.LogWarning(
                    ex,
                    "Cost reconciliation request failed; retrying (attempt {Attempt}/{MaxAttempts}).",
                    attempt,
                    MaxAttempts);
            }

            await Task.Delay(delayForAttempt(attempt), cancellationToken).ConfigureAwait(false);
        }

        // Unreachable: the loop above always returns or retries until isLastAttempt, at which point either
        // the response is returned directly or the HttpRequestException catch's `when (!isLastAttempt)`
        // guard lets the exception propagate instead of being caught.
        throw new InvalidOperationException("Unreachable.");
    }
}
