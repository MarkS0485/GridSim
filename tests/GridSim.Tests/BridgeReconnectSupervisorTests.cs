using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Diagnostics;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// NCSC CAF B5 / NIS principle 4: the supervised reconnect that drives <see cref="ResilienceOptions.BackoffDelay"/>.
/// Every test injects the wait (it records the delay asked for and returns at once, or blocks until cancelled) and the
/// reconnect (a script of outcomes), so nothing sleeps and no socket is opened. What is pinned:
/// <list type="bullet">
/// <item>the waits are exactly the schedule, attempt by attempt, capped at the ceiling;</item>
/// <item>the bound (MaxReconnectAttempts) is honoured and the give-up is reported exactly once;</item>
/// <item>a successful reconnect resets the attempt count, so the next outage starts the schedule from the top;</item>
/// <item>disposal (and Stop) end a pending wait with no further attempt and no give-up report;</item>
/// <item>a horizon rejection is never retried (the restriction invariant), and a loss during an attempt fails it.</item>
/// </list>
/// </summary>
public sealed class BridgeReconnectSupervisorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);   // a hang fails the test, never stalls the run

    // 1 s doubling to a 30 s ceiling (the default schedule) with the bound given.
    private static ResilienceOptions Schedule(int maxAttempts) =>
        ResilienceOptions.Default with { MaxReconnectAttempts = maxAttempts };

    // A recording, non-sleeping wait. The supervisor's outage loop is sequential, so the list is written by one task at a
    // time and read only after the test has awaited that task.
    private sealed class RecordingDelay
    {
        public List<TimeSpan> Asked { get; } = new();

        public Task Wait(TimeSpan d, CancellationToken ct)
        {
            Asked.Add(d);
            return Task.CompletedTask;
        }
    }

    // A scripted reconnect: attempt n fails when fails(n) is true, else succeeds. Every call is recorded.
    private sealed class ScriptedReconnect(Func<int, bool> fails)
    {
        public List<int> Calls { get; } = new();

        public Task Run(int attempt, CancellationToken ct)
        {
            Calls.Add(attempt);
            return fails(attempt)
                ? Task.FromException(new IOException($"scripted failure of attempt {attempt}"))
                : Task.CompletedTask;
        }
    }

    // The supervisor's three reports, collected.
    private sealed class Reports
    {
        public List<BridgeReconnectSchedule> Scheduled { get; } = new();
        public List<BridgeReconnectAttempt> Attempts { get; } = new();
        public List<BridgeReconnectGiveUp> GiveUps { get; } = new();

        public void Attach(BridgeReconnectSupervisor s)
        {
            s.ReconnectScheduled += Scheduled.Add;
            s.AttemptCompleted += Attempts.Add;
            s.GaveUp += g => { lock (GiveUps) GiveUps.Add(g); };
        }
    }

    [Fact]
    public async Task The_waits_follow_the_backoff_schedule_exactly_attempt_by_attempt()
    {
        var delay = new RecordingDelay();
        var reconnect = new ScriptedReconnect(_ => true);   // every attempt fails, so the whole schedule is walked
        ResilienceOptions options = Schedule(maxAttempts: 7);
        await using var supervisor = new BridgeReconnectSupervisor(options, reconnect.Run, delay.Wait);
        var reports = new Reports();
        reports.Attach(supervisor);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("The bridge closed the connection.")));
        await supervisor.Outage.WaitAsync(Deadline);

        // Exactly BackoffDelay(n) for n = 1..7: 1, 2, 4, 8, 16, then the 30 s ceiling twice. No jitter, no extra wait.
        TimeSpan[] expected = Enumerable.Range(1, 7).Select(options.BackoffDelay).ToArray();
        Assert.Equal(new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 30.0 }, expected.Select(t => t.TotalSeconds));
        Assert.Equal(expected, delay.Asked);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, reconnect.Calls);
        // Each attempt was announced with the delay it waited and the bound, then reported as failed.
        Assert.Equal(expected, reports.Scheduled.Select(s => s.Delay));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, reports.Scheduled.Select(s => s.Attempt));
        Assert.All(reports.Scheduled, s => Assert.Equal(7, s.MaxAttempts));
        Assert.Equal(expected, reports.Attempts.Select(a => a.Delay));
        Assert.All(reports.Attempts, a => Assert.False(a.Succeeded));
        // The first announcement carries the fault that lost the stream; each later one the previous attempt's failure.
        Assert.Equal("The bridge closed the connection.", reports.Scheduled[0].Cause.Message);
        Assert.Equal("scripted failure of attempt 1", reports.Scheduled[1].Cause.Message);
    }

    [Fact]
    public async Task The_bound_is_honoured_and_the_give_up_is_reported_exactly_once()
    {
        var delay = new RecordingDelay();
        var reconnect = new ScriptedReconnect(_ => true);
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 3), reconnect.Run, delay.Wait);
        var reports = new Reports();
        reports.Attach(supervisor);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor.Outage.WaitAsync(Deadline);

        // Three attempts, no fourth.
        Assert.Equal(new[] { 1, 2, 3 }, reconnect.Calls);
        Assert.Equal(3, delay.Asked.Count);
        Assert.Equal(BridgeReconnectState.GaveUp, supervisor.State);
        Assert.Equal(3, supervisor.Attempt);
        BridgeReconnectGiveUp giveUp = Assert.Single(reports.GiveUps);
        Assert.Equal(3, giveUp.Attempts);
        Assert.Equal("scripted failure of attempt 3", giveUp.LastFailure.Message);
        Assert.Contains("not re-established after 3 attempt(s)", giveUp.Reason, StringComparison.Ordinal);
        Assert.Same(giveUp, supervisor.LastGiveUp);

        // A further loss after the give-up starts nothing and is not reported again: the host must reconnect by hand.
        Assert.False(supervisor.ConnectionLost(new BridgeStreamException("lost again")));
        await supervisor.Outage.WaitAsync(Deadline);
        Assert.Equal(new[] { 1, 2, 3 }, reconnect.Calls);
        Assert.Single(reports.GiveUps);
        Assert.Equal(BridgeReconnectState.GaveUp, supervisor.State);
        Assert.Null(supervisor.SupervisorFailure);
    }

    [Fact]
    public async Task A_successful_reconnect_resets_the_attempt_count_so_the_next_outage_starts_from_the_top()
    {
        var delay = new RecordingDelay();
        // The first outage: attempts 1 and 2 fail, attempt 3 succeeds. The second outage: attempt 1 succeeds at once.
        int call = 0;
        var calls = new List<int>();
        Task Reconnect(int attempt, CancellationToken ct)
        {
            calls.Add(attempt);
            call++;
            return call <= 2 ? Task.FromException(new IOException("refused")) : Task.CompletedTask;
        }
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 5), Reconnect, delay.Wait);
        var reports = new Reports();
        reports.Attach(supervisor);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor.Outage.WaitAsync(Deadline);
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
        Assert.Equal(0, supervisor.Attempt);   // reset on success
        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, delay.Asked.Select(t => t.TotalSeconds));
        Assert.True(reports.Attempts[^1].Succeeded);
        Assert.Equal(3, reports.Attempts[^1].Attempt);
        Assert.Empty(reports.GiveUps);

        // The next loss is a new outage: the schedule starts again at attempt 1 with the initial 1 s wait, not at 4 with 8 s.
        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost again")));
        await supervisor.Outage.WaitAsync(Deadline);
        Assert.Equal(new[] { 1, 2, 3, 1 }, calls);
        Assert.Equal(new[] { 1.0, 2.0, 4.0, 1.0 }, delay.Asked.Select(t => t.TotalSeconds));
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
        Assert.Equal(0, supervisor.Attempt);
        Assert.Empty(reports.GiveUps);
    }

    [Fact]
    public async Task Disposal_ends_a_pending_wait_with_no_further_attempt_and_no_give_up()
    {
        var scheduled = new TaskCompletionSource<BridgeReconnectSchedule>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reconnects = 0;
        // A wait that only ends when it is cancelled (the real Task.Delay of a long backoff, without the time passing).
        static Task BlockUntilCancelled(TimeSpan d, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0),
            (_, _) => { Interlocked.Increment(ref reconnects); return Task.CompletedTask; }, BlockUntilCancelled);
        var reports = new Reports();
        reports.Attach(supervisor);
        supervisor.ReconnectScheduled += s => scheduled.TrySetResult(s);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        BridgeReconnectSchedule first = await scheduled.Task.WaitAsync(Deadline);   // the supervisor is now waiting
        Assert.Equal(1, first.Attempt);
        Task outage = supervisor.Outage;

        await supervisor.DisposeAsync().AsTask().WaitAsync(Deadline);

        Assert.True(outage.IsCompletedSuccessfully);   // the outage task ended, and did not fault
        Assert.Equal(0, Volatile.Read(ref reconnects));   // no attempt was made
        Assert.Empty(reports.GiveUps);                    // and no give-up was reported
        Assert.Equal(BridgeReconnectState.Disposed, supervisor.State);
        // After disposal a loss starts nothing, and disposing again is harmless.
        Assert.False(supervisor.ConnectionLost(new BridgeStreamException("lost after dispose")));
        await supervisor.DisposeAsync();
        Assert.Equal(0, Volatile.Read(ref reconnects));
        Assert.Single(reports.Scheduled);
    }

    [Fact]
    public async Task Stop_ends_a_pending_wait_with_no_attempt_and_the_supervisor_can_be_used_again()
    {
        var scheduled = new TaskCompletionSource<BridgeReconnectSchedule>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = new List<TimeSpan>();
        bool block = true;
        Task Wait(TimeSpan d, CancellationToken ct)
        {
            lock (waits) waits.Add(d);
            return Volatile.Read(ref block) ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask;
        }
        var calls = new List<int>();
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0),
            (a, _) => { lock (calls) calls.Add(a); return Task.CompletedTask; }, Wait);
        var reports = new Reports();
        reports.Attach(supervisor);
        supervisor.ReconnectScheduled += s => scheduled.TrySetResult(s);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await scheduled.Task.WaitAsync(Deadline);
        Task outage = supervisor.Outage;
        supervisor.Stop();
        await outage.WaitAsync(Deadline);
        Assert.Empty(calls);
        Assert.Empty(reports.GiveUps);
        Assert.Equal(BridgeReconnectState.Idle, supervisor.State);
        Assert.Equal(0, supervisor.Attempt);

        // Used again after the stop: a new loss is a new outage from attempt 1, and this time it reconnects.
        Volatile.Write(ref block, false);
        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost again")));
        await supervisor.Outage.WaitAsync(Deadline);
        Assert.Equal(new[] { 1 }, calls);
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
        lock (waits) Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) }, waits);
    }

    [Fact]
    public async Task A_horizon_rejection_is_never_retried_and_is_reported_once()
    {
        var delay = new RecordingDelay();
        var reconnect = new ScriptedReconnect(_ => false);
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0), reconnect.Run, delay.Wait);
        var reports = new Reports();
        reports.Attach(supervisor);

        // The restriction invariant: a frame inside the 169 h horizon ends the stream and is NOT reconnected (a reconnect
        // would replay the same stream into the same refusal). The supervisor gives up at once and says why.
        var breach = new ForwardInferenceException("bridge state is newer than the historical horizon");
        Assert.False(supervisor.ConnectionLost(breach));
        await supervisor.Outage.WaitAsync(Deadline);

        Assert.Empty(reconnect.Calls);
        Assert.Empty(delay.Asked);
        Assert.Empty(reports.Scheduled);
        BridgeReconnectGiveUp giveUp = Assert.Single(reports.GiveUps);
        Assert.Equal(0, giveUp.Attempts);
        Assert.Same(breach, giveUp.LastFailure);
        Assert.Contains("169 h forward-inference horizon", giveUp.Reason, StringComparison.Ordinal);
        Assert.Equal(BridgeReconnectState.GaveUp, supervisor.State);

        // A reconnect that itself hits the horizon guard is not retried either.
        var supervisor2Calls = new List<int>();
        await using var supervisor2 = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0),
            (a, _) => { supervisor2Calls.Add(a); return Task.FromException(new ForwardInferenceException("refused on reconnect")); },
            delay.Wait);
        var reports2 = new Reports();
        reports2.Attach(supervisor2);
        Assert.True(supervisor2.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor2.Outage.WaitAsync(Deadline);
        Assert.Equal(new[] { 1 }, supervisor2Calls);
        Assert.Equal(1, Assert.Single(reports2.GiveUps).Attempts);
        Assert.Equal(BridgeReconnectState.GaveUp, supervisor2.State);
    }

    [Fact]
    public async Task A_loss_reported_while_an_attempt_runs_fails_that_attempt_and_a_second_loss_starts_no_second_schedule()
    {
        var delay = new RecordingDelay();
        BridgeReconnectSupervisor? self = null;
        var calls = new List<int>();
        bool secondStarted = true;
        Task Reconnect(int attempt, CancellationToken ct)
        {
            calls.Add(attempt);
            // Attempt 1 "connects", but its new stream is lost before the attempt returns: the host reports the loss.
            if (attempt == 1) secondStarted = self!.ConnectionLost(new BridgeStreamException("lost during attempt 1"));
            return Task.CompletedTask;
        }
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0), Reconnect, delay.Wait);
        self = supervisor;
        var reports = new Reports();
        reports.Attach(supervisor);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor.Outage.WaitAsync(Deadline);

        Assert.False(secondStarted);   // one outage at a time
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.False(reports.Attempts[0].Succeeded);
        Assert.Equal("lost during attempt 1", reports.Attempts[0].Failure!.Message);
        Assert.True(reports.Attempts[1].Succeeded);
        Assert.Equal(new[] { 1.0, 2.0 }, delay.Asked.Select(t => t.TotalSeconds));
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
    }

    [Fact]
    public async Task With_no_bound_it_keeps_trying_on_the_capped_backoff_and_never_gives_up()
    {
        var delay = new RecordingDelay();
        var reconnect = new ScriptedReconnect(n => n <= 12);   // twelve failures, then success
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0), reconnect.Run, delay.Wait);
        var reports = new Reports();
        reports.Attach(supervisor);

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor.Outage.WaitAsync(Deadline);

        Assert.Equal(Enumerable.Range(1, 13), reconnect.Calls);
        Assert.All(delay.Asked, d => Assert.True(d <= ResilienceOptions.Default.MaxBackoff));
        Assert.Equal(ResilienceOptions.Default.MaxBackoff, delay.Asked[^1]);
        Assert.All(reports.Scheduled, s => Assert.Equal(0, s.MaxAttempts));
        Assert.Empty(reports.GiveUps);
        Assert.Equal(BridgeReconnectState.Connected, supervisor.State);
    }

    [Fact]
    public async Task A_failing_report_handler_stops_the_supervisor_loudly_instead_of_silently()
    {
        var delay = new RecordingDelay();
        var reconnect = new ScriptedReconnect(_ => true);
        await using var supervisor = new BridgeReconnectSupervisor(Schedule(maxAttempts: 0), reconnect.Run, delay.Wait);
        var giveUps = new List<BridgeReconnectGiveUp>();
        supervisor.GaveUp += giveUps.Add;
        supervisor.ReconnectScheduled += _ => throw new InvalidOperationException("a broken handler");

        Assert.True(supervisor.ConnectionLost(new BridgeStreamException("lost")));
        await supervisor.Outage.WaitAsync(Deadline);   // never faults

        Assert.Empty(reconnect.Calls);
        BridgeReconnectGiveUp giveUp = Assert.Single(giveUps);
        Assert.Contains("failed itself", giveUp.Reason, StringComparison.Ordinal);
        Assert.Equal("a broken handler", supervisor.SupervisorFailure!.Message);
        Assert.Equal(BridgeReconnectState.GaveUp, supervisor.State);
    }

    [Fact]
    public void A_nonsensical_schedule_or_a_missing_reconnect_is_refused_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeReconnectSupervisor(
            ResilienceOptions.Default with { InitialBackoff = TimeSpan.Zero }, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeReconnectSupervisor(
            ResilienceOptions.Default with { MaxReconnectAttempts = -1 }, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new BridgeReconnectSupervisor(ResilienceOptions.Default, null!));
        Assert.Throws<ArgumentNullException>(() => new BridgeReconnectSupervisor(null!, (_, _) => Task.CompletedTask));
    }
}
