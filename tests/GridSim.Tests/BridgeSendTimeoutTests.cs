using System.Reflection;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #5 (regression test for bug hunt 2026-10-29 finding 58, fixed then but not pinned): a query whose timeout
/// fires while its request is still being sent must fail with the documented <see cref="TimeoutException"/>, not a bare
/// <see cref="OperationCanceledException"/>, because the caller cancelled nothing.
/// <para>Socket back-pressure is not reproducible on demand, so the test blocks the send the same way at the step the
/// fix guards: it holds the client's send lock (the first thing every send awaits, with the query's linked token), so the
/// request cannot go out and the query timeout fires mid-send. The client is never connected; nothing reaches a socket.</para>
/// </summary>
public sealed class BridgeSendTimeoutTests
{
    private static SemaphoreSlim SendLock(GridBridgeClient c) =>
        (SemaphoreSlim)typeof(GridBridgeClient).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;

    [Fact]
    public async Task A_timeout_while_the_request_is_still_being_sent_is_a_TimeoutException()
    {
        await using var client = new GridBridgeClient { QueryTimeout = TimeSpan.FromMilliseconds(200) };
        SemaphoreSlim gate = SendLock(client);
        // Hold the send path, as a send blocked by back-pressure would.
        Assert.True(gate.Wait(0), "the send lock should be free on a fresh client");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.QueryPropagation(DateTime.UtcNow.AddDays(-30)));
            Assert.Contains("while its request was still being sent", ex.Message, StringComparison.Ordinal);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the query took {sw.Elapsed} to time out");
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task A_caller_cancel_while_the_request_is_still_being_sent_stays_a_cancellation()
    {
        // The other side of the same rule: when the CALLER cancels mid-send, the result is a cancellation, not a timeout.
        await using var client = new GridBridgeClient { QueryTimeout = TimeSpan.FromSeconds(60) };
        SemaphoreSlim gate = SendLock(client);
        Assert.True(gate.Wait(0));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryPropagation(DateTime.UtcNow.AddDays(-30), cts.Token));
        }
        finally { gate.Release(); }
    }
}
