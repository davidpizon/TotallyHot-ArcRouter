using AwesomeAssertions;

namespace TotallyHot.ArcRouter.Tray.Tests;

/// <summary>
/// Covers <see cref="TrayStatusPresenter"/>'s two formatting methods against every
/// (<see cref="RouterServiceStatus"/>?, <see cref="RouterConnectionState"/>) case the tray can encounter -
/// a direct port of <c>TrayWindowManager.BuildRouterStatusLabel</c>/<c>ShowRouterUnavailableBalloon</c>'s
/// switch logic, verified independently of any Windows service or native window.
/// </summary>
public sealed class TrayStatusPresenterTests
{
    [Theory]
    [InlineData(RouterServiceStatus.Stopped, RouterConnectionState.Unreachable, "Router service: stopped")]
    [InlineData(RouterServiceStatus.Stopped, RouterConnectionState.Rejected, "Router service: stopped")]
    [InlineData(RouterServiceStatus.StartPending, RouterConnectionState.Unreachable, "Router service: starting")]
    [InlineData(RouterServiceStatus.StopPending, RouterConnectionState.Unreachable, "Router service: stopping")]
    [InlineData(RouterServiceStatus.Paused, RouterConnectionState.Unreachable, "Router service: paused")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Rejected, "Router: request rejected")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Unreachable, "Router: not responding yet")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Connected, "Router: not responding yet")]
    [InlineData(null, RouterConnectionState.Rejected, "Router: request rejected")]
    [InlineData(null, RouterConnectionState.Unreachable, "Router: not responding")]
    public void BuildStatusLabel_ReturnsTheExpectedCaption(RouterServiceStatus? serviceStatus,
        RouterConnectionState connectionState, string expected)
    {
        var actual = TrayStatusPresenter.BuildStatusLabel(serviceStatus, connectionState);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(RouterServiceStatus.Stopped, RouterConnectionState.Unreachable, "The Windows service is stopped.")]
    [InlineData(RouterServiceStatus.StartPending, RouterConnectionState.Unreachable,
        "The Windows service is still starting.")]
    [InlineData(RouterServiceStatus.StopPending, RouterConnectionState.Unreachable,
        "The Windows service is shutting down.")]
    [InlineData(RouterServiceStatus.Paused, RouterConnectionState.Unreachable, "The Windows service is paused.")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Rejected,
        "The Windows service is running but rejected this app's request. Its management token may not match; restarting the service regenerates it.")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Unreachable,
        "The Windows service is running but is not accepting connections on its management port yet.")]
    [InlineData(RouterServiceStatus.Running, RouterConnectionState.Connected,
        "The router is temporarily unavailable.")]
    [InlineData(null, RouterConnectionState.Rejected,
        "The router rejected this app's request. Its management token may not match.")]
    [InlineData(null, RouterConnectionState.Unreachable, "The router is not responding.")]
    public void BuildBalloonMessage_ReturnsTheExpectedSentence(RouterServiceStatus? serviceStatus,
        RouterConnectionState connectionState, string expected)
    {
        var actual = TrayStatusPresenter.BuildBalloonMessage(serviceStatus, connectionState);

        actual.Should().Be(expected);
    }
}
