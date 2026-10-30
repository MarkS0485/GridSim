using GridSim.Web.Sessions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Known limitation "Web output conventions" (chapter 36, Web host): a bridge pause, speed change or seek that failed
/// was swallowed with no word, so the dashboard looked as if the stream had obeyed. The web session now runs each
/// command through <see cref="SimSession.DescribeBridgeControlFailure"/> and shows a failure as a Warning. These tests
/// drive that rule directly, with commands that succeed, fail and are cancelled, so no live bridge is needed.
/// </summary>
public sealed class BridgeControlFailureTests
{
    [Fact]
    public async Task A_command_that_succeeds_gives_no_warning()
    {
        bool ran = false;
        string? warning = await SimSession.DescribeBridgeControlFailure("seek", () => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
        Assert.Null(warning);
    }

    [Fact]
    public async Task A_command_that_fails_gives_a_warning_naming_the_command_and_the_error()
    {
        string? warning = await SimSession.DescribeBridgeControlFailure(
            "seek", () => Task.FromException(new InvalidOperationException("the socket is closed")));
        Assert.NotNull(warning);
        Assert.StartsWith("Bridge seek failed", warning, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
        Assert.Contains("the socket is closed", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_throws_before_it_starts_gives_a_warning_too()
    {
        // A command can fail synchronously (for example a disposed socket) rather than through its task.
        string? warning = await SimSession.DescribeBridgeControlFailure(
            "speed change", () => throw new System.Net.WebSockets.WebSocketException("connection reset"));
        Assert.NotNull(warning);
        Assert.StartsWith("Bridge speed change failed", warning, StringComparison.Ordinal);
        Assert.Contains("connection reset", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_command_is_the_session_closing_and_gives_no_warning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string? warning = await SimSession.DescribeBridgeControlFailure("pause", () => Task.FromCanceled(cts.Token));
        Assert.Null(warning);
        string? thrown = await SimSession.DescribeBridgeControlFailure("pause", () => throw new OperationCanceledException());
        Assert.Null(thrown);
    }
}
