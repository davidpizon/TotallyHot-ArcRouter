namespace TotallyHot.ArcRouter.Gui.Services;

/// <summary>One error notification shown by <c>ToastHost</c>, at the top of the app window.</summary>
/// <param name="Id">Identifies this toast for dismissal.</param>
/// <param name="Title">The short, bold headline (e.g. the provider name or operation).</param>
/// <param name="Message">The failure detail shown under the title.</param>
public sealed record Toast(Guid Id, string Title, string Message);

/// <summary>
/// App-wide error-toast notifications, shared by every admin store (<see cref="ProviderAdminStore"/> and
/// future panes) so a failed admin action is never silent even when it doesn't throw all the way out to a
/// component's own inline error banner. Same singleton + <see cref="Changed"/>-event shape as
/// <see cref="ProviderAdminStore"/>/<see cref="LiveDataStore"/>; rendered by the single <c>ToastHost</c>
/// instance in <c>Dashboard.razor</c>.
/// </summary>
public sealed class ToastService
{
    /// <summary>How long a toast stays visible before auto-dismissing.</summary>
    public static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(6);

    private readonly List<Toast> _toasts = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _autoDismissAfter;

    /// <summary>Initializes a new instance of the <see cref="ToastService"/> class.</summary>
    /// <param name="timeProvider">Clock used to schedule auto-dismissal; defaults to <see cref="TimeProvider.System"/>. Overridable for deterministic tests.</param>
    /// <param name="autoDismissAfter">
    /// How long a toast stays visible before auto-dismissing; defaults to <see cref="AutoDismissAfter"/>.
    /// Overridable so a test can assert on the auto-dismiss path without waiting out the real 6 seconds.
    /// </param>
    public ToastService(TimeProvider? timeProvider = null, TimeSpan? autoDismissAfter = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _autoDismissAfter = autoDismissAfter ?? AutoDismissAfter;
    }

    /// <summary>The toasts currently visible, oldest first.</summary>
    public IReadOnlyList<Toast> Toasts => _toasts;

    /// <summary>Raised after <see cref="Toasts"/> changes (a toast was shown or dismissed).</summary>
    public event Action? Changed;

    /// <summary>Shows an error toast, auto-dismissing it after <see cref="AutoDismissAfter"/> unless closed first.</summary>
    /// <param name="title">The short, bold headline (e.g. the provider name or operation).</param>
    /// <param name="message">The failure detail shown under the title.</param>
    public void ShowError(string title, string message)
    {
        var toast = new Toast(Guid.NewGuid(), title, message);
        _toasts.Add(toast);
        Changed?.Invoke();

        _ = AutoDismissAsync(toast.Id);
    }

    /// <summary>Dismisses a toast early (the close glyph), or a no-op if it already auto-dismissed.</summary>
    /// <param name="id">The toast's <see cref="Toast.Id"/>.</param>
    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Removes a toast after <see cref="_autoDismissAfter"/>, unless it was already dismissed manually.</summary>
    /// <param name="id">The toast's <see cref="Toast.Id"/>.</param>
    private async Task AutoDismissAsync(Guid id)
    {
        await Task.Delay(_autoDismissAfter, _timeProvider).ConfigureAwait(false);
        Dismiss(id);
    }
}
