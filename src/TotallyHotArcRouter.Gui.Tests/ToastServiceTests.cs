using TotallyHot.ArcRouter.Gui.Services;
using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Tests for <see cref="ToastService"/>: the app-wide error-toast notification store backing
/// <see cref="ToastHost"/>. The auto-dismiss tests override the constructor's <c>autoDismissAfter</c> to a
/// few milliseconds rather than waiting out the real 6-second default, per the project's 5-second test
/// ceiling.
/// </summary>
public sealed class ToastServiceTests
{
    [Fact]
    public void ShowError_addsAToastAndRaisesChanged()
    {
        var toasts = new ToastService();
        var raised = false;
        toasts.Changed += () => raised = true;

        toasts.ShowError("Providers unreachable", "connection refused");

        toasts.Toasts.Should().ContainSingle();
        toasts.Toasts[0].Title.Should().Be("Providers unreachable");
        toasts.Toasts[0].Message.Should().Be("connection refused");
        raised.Should().BeTrue();
    }

    [Fact]
    public void ShowError_calledTwice_stacksBothToasts()
    {
        var toasts = new ToastService();

        toasts.ShowError("First", "a");
        toasts.ShowError("Second", "b");

        toasts.Toasts.Should().HaveCount(2);
    }

    [Fact]
    public void Dismiss_removesTheToastAndRaisesChanged()
    {
        var toasts = new ToastService();
        toasts.ShowError("title", "message");
        var id = toasts.Toasts[0].Id;
        var raised = false;
        toasts.Changed += () => raised = true;

        toasts.Dismiss(id);

        toasts.Toasts.Should().BeEmpty();
        raised.Should().BeTrue();
    }

    [Fact]
    public void Dismiss_ofAnUnknownId_doesNotRaiseChanged()
    {
        var toasts = new ToastService();
        toasts.ShowError("title", "message");
        var raised = false;
        toasts.Changed += () => raised = true;

        toasts.Dismiss(Guid.NewGuid());

        raised.Should().BeFalse();
        toasts.Toasts.Should().ContainSingle();
    }

    [Fact]
    public async Task AShownToast_autoDismissesAfterTheConfiguredDelay()
    {
        var toasts = new ToastService(autoDismissAfter: TimeSpan.FromMilliseconds(20));

        toasts.ShowError("title", "message");
        toasts.Toasts.Should().ContainSingle();

        await WaitUntilAsync(() => toasts.Toasts.Count == 0);

        toasts.Toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task AManuallyDismissedToast_doesNotDoubleFireOnAutoDismiss()
    {
        var toasts = new ToastService(autoDismissAfter: TimeSpan.FromMilliseconds(20));
        toasts.ShowError("title", "message");
        var id = toasts.Toasts[0].Id;

        toasts.Dismiss(id);
        var changedCount = 0;
        toasts.Changed += () => changedCount++;
        await Task.Delay(TimeSpan.FromMilliseconds(60), TestContext.Current.CancellationToken);

        changedCount.Should().Be(0);
    }

    /// <summary>Polls until <paramref name="condition"/> is true or a short timeout elapses, for asserting on the auto-dismiss background continuation without a fixed sleep.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
