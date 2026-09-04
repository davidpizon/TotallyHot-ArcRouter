namespace TotallyHot.ArcRouter.Proxy.Management;

/// <summary>
/// Runs a <see cref="ManagementFacade"/> or <see cref="ManagementReportingService"/> write/read whose only
/// failure mode worth reporting is "something unexpected went wrong" - wraps the try/catch that pattern
/// otherwise repeats at each call site (docs/router/code-smell-refactoring-plan.md Phase 3 step 2) into one
/// place. Not a fit for a call site that needs to distinguish more than one exception type in its response
/// (e.g. <see cref="ManagementFacade.SetBudget"/>'s separate <see cref="ArgumentException"/> handling) -
/// those are left as their own explicit try/catch rather than forced through this.
/// </summary>
internal static class ManagementResultExecutor
{
    /// <summary>
    /// Runs <paramref name="action"/>, returning <see cref="ManagementResult{T}.Ok"/> on success or
    /// <see cref="ManagementErrorType.Internal"/> with <paramref name="failureMessage"/> on any exception.
    /// </summary>
    public static ManagementResult<T> TryExecute<T>(Func<T> action, string failureMessage)
    {
        try
        {
            return ManagementResult<T>.Ok(action());
        }
        catch (Exception)
        {
            return ManagementResult<T>.Fail(errorType: ManagementErrorType.Internal, message: failureMessage);
        }
    }

    /// <summary>
    /// The async counterpart to <see cref="TryExecute{T}"/>. <see cref="OperationCanceledException"/> is
    /// deliberately not caught - cancellation propagates to the caller rather than being reported as an
    /// internal failure.
    /// </summary>
    public static async Task<ManagementResult<T>> TryExecuteAsync<T>(Func<Task<T>> action, string failureMessage)
    {
        try
        {
            return ManagementResult<T>.Ok(await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ManagementResult<T>.Fail(errorType: ManagementErrorType.Internal, message: failureMessage);
        }
    }
}