using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Api;
using GridSim.Web.Projection;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Web batch 2 (November 2026). New regression tests only; no existing test is touched.
/// <list type="bullet">
/// <item>GR-240: a raw replay frame is not reported as converged; its lamp reads NO SOLVE.</item>
/// <item>GR-245: the CONFORMANCE, HUMAN SCALE and HARMONICS tabs (and the EXPERIMENTAL wave field) are empty for a
/// non-converged frame, one test per tab, and present for a converged one.</item>
/// <item>GR-241: a raw seek is clamped to the forward-inference horizon as well as the raw range.</item>
/// <item>GR-218: the solve concurrency cap (503), the solve timeout (504), the settings' fail-loud parsing, and the role
/// gate in front of the slot.</item>
/// <item>GR-221: the per-socket token bucket on step and start frames, with an injected clock (no wall-clock waits).</item>
/// </list>
/// </summary>
public sealed class GA7E3Batch2WebTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static SimFrame Case9Frame(double frequencyHz = 50.0)
    {
        GridModel model = IeeeCases.Case9();
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(model);
        Assert.True(r.Converged);
        GridModel solved = solver.RegulatedModel;
        return new SimFrame(solved, r, Hour: 0, Tick: 0, FrequencyHz: frequencyHz,
            GenerationMw: solved.Generators.Sum(g => g.Pg), DemandMw: solved.Buses.Sum(b => b.Pd));
    }

    /// <summary>A genuinely non-converged frame: IEEE 14-bus stopped after one Newton iteration.</summary>
    private static SimFrame NonConvergedFrame()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult r = new SteadyStateSolver().Solve(model, new PowerFlowOptions { MaxIterations = 1 });
        Assert.False(r.Converged);
        return new SimFrame(model, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);
    }

    /// <summary>A clock a test moves by hand. It also serves timers, so Task.WaitAsync(timeout, clock) is driven by it.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() { lock (_gate) return _ticks; }

        public override DateTimeOffset GetUtcNow() { lock (_gate) return new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks); }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var t = new ManualTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + dueTime.Ticks);
                _timers.Add(t);
                return t;
            }
        }

        /// <summary>Move time forward and fire every timer that has come due (outside the lock).</summary>
        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = _timers.Where(t => t.IsDue(_ticks)).ToArray();
            }
            foreach (ManualTimer t in due) t.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _dueTicks;
        private bool _done;

        public ManualTimer(TimerCallback callback, object? state, long dueTicks)
        {
            _callback = callback;
            _state = state;
            _dueTicks = dueTicks;
        }

        public bool IsDue(long nowTicks) { lock (this) return !_done && nowTicks >= _dueTicks; }

        public void Fire()
        {
            lock (this) { if (_done) return; _done = true; }
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this) { _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _dueTicks; return !_done; }
        }

        public void Dispose() { lock (this) _done = true; }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static WebApplicationFactory<Program> Factory(WebLimits? limits, params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-ga7e3b2-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
            // The test's own limits (with its manual clock) replace the host's: the last registration wins.
            if (limits is not null) b.ConfigureTestServices(s => s.AddSingleton(limits));
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-240: a raw replay frame is not converged and the lamp reads NO SOLVE.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void GR240_a_raw_replay_frame_is_reported_not_converged_and_not_solved()
    {
        FrameEnvelope env = FrameProjector.ProjectRaw(Case9Frame(), inertiaGvaS: 150.0, label: "raw");
        Assert.False(env.Headline.Converged);
        Assert.True(env.Headline.NotSolved);
        Assert.Empty(env.Nodes);
        Assert.Empty(env.Edges);
        Assert.Empty(env.Boundaries);
        // The real raw scalars are still carried.
        Assert.Equal(50.0, env.Headline.FrequencyHz);
        Assert.Equal(150.0, env.Headline.InertiaGvaS);
    }

    [Fact]
    public void GR240_a_solved_frame_is_not_marked_not_solved()
    {
        FrameEnvelope env = FrameProjector.Project(Case9Frame());
        Assert.True(env.Headline.Converged);
        Assert.False(env.Headline.NotSolved);
    }

    [Fact]
    public void GR240_the_lamp_reads_NO_SOLVE_for_a_raw_frame_and_never_normal_or_not_converged()
    {
        HeadlineDto raw = FrameProjector.ProjectRaw(Case9Frame(), 150.0).Headline;
        Assert.Equal(StatusLamp.NoSolve, StatusLamp.Evaluate(raw, anyBoundaryOverloaded: false));
        Assert.Equal(("lamp-nosolve", "NO SOLVE"), StatusLamp.NoSolve);
        // A corridor flag cannot turn a no-solve frame into a verdict drawn from placeholders.
        Assert.Equal(StatusLamp.NoSolve, StatusLamp.Evaluate(raw, anyBoundaryOverloaded: true));
        Assert.NotEqual(StatusLamp.NotConverged, StatusLamp.NoSolve);
    }

    [Fact]
    public void GR240_a_real_raw_frequency_event_is_still_shown_ahead_of_NO_SOLVE()
    {
        // The raw frequency is a measurement, so a 0.3 Hz deviation is a real event on a raw frame.
        HeadlineDto raw = FrameProjector.ProjectRaw(Case9Frame(frequencyHz: 49.7), 150.0).Headline;
        Assert.Equal("FREQUENCY EVENT", StatusLamp.Evaluate(raw, anyBoundaryOverloaded: false).Text);
    }

    [Fact]
    public void GR240_a_solve_that_failed_to_converge_still_reads_NOT_CONVERGED()
    {
        FrameEnvelope env = FrameProjector.Project(NonConvergedFrame());
        Assert.False(env.Headline.NotSolved);
        Assert.Equal(StatusLamp.NotConverged, StatusLamp.Evaluate(env.Headline, anyBoundaryOverloaded: false));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-245: one test per tab, gated on convergence like the boundaries.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void GR245_conformance_is_empty_for_a_non_converged_frame_and_present_for_a_converged_one()
    {
        Assert.Null(FrameProjector.Project(NonConvergedFrame()).Conformance);
        Assert.NotNull(FrameProjector.Project(Case9Frame()).Conformance);
    }

    [Fact]
    public void GR245_experimental_has_no_wave_field_for_a_non_converged_frame_but_keeps_its_not_converged_note()
    {
        ExperimentalDto bad = FrameProjector.Project(NonConvergedFrame()).Experimental!;
        Assert.Null(bad.WaveField);
        Assert.True(bad.TraceNotConverged);
        Assert.Empty(bad.TopFlows);
        Assert.NotNull(FrameProjector.Project(Case9Frame()).Experimental!.WaveField);
    }

    [Fact]
    public void GR245_human_scale_is_empty_for_a_non_converged_frame_and_present_for_a_converged_one()
    {
        Assert.Null(FrameProjector.Project(NonConvergedFrame()).HumanScale);
        Assert.NotNull(FrameProjector.Project(Case9Frame()).HumanScale);
    }

    [Fact]
    public void GR245_harmonics_is_empty_for_a_non_converged_frame_and_present_for_a_converged_one()
    {
        Assert.Null(FrameProjector.Project(NonConvergedFrame()).Harmonics);
        Assert.NotNull(FrameProjector.Project(Case9Frame()).Harmonics);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-241: a raw seek is clamped to the horizon as well as the raw range.
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime HorizonAtNow => Now.AddHours(-GridSim.Core.Diagnostics.ForwardInferenceGuard.HorizonHours);

    [Fact]
    public void GR241_a_seek_past_the_horizon_but_inside_the_raw_range_lands_on_the_horizon()
    {
        // The raw range ends yesterday, which is newer than the 169 h horizon.
        DateTime rawMax = Now.AddDays(-1);
        DateTime lo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(rawMax > HorizonAtNow);
        Assert.Equal(HorizonAtNow, SimSession.ClampSeekInstant(rawMax, lo, rawMax, Now));
        Assert.Equal(HorizonAtNow, SimSession.ClampSeekInstant(Now.AddDays(5), lo, rawMax, Now));
        Assert.Equal(HorizonAtNow, SimSession.RawSeekLimit(rawMax, Now));
    }

    [Fact]
    public void GR241_an_old_instant_inside_the_range_is_left_exactly_as_asked()
    {
        DateTime lo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime rawMax = Now.AddDays(-1);
        DateTime wanted = new(2024, 6, 1, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(wanted, SimSession.ClampSeekInstant(wanted, lo, rawMax, Now));
    }

    [Fact]
    public void GR241_an_instant_before_the_range_lands_on_the_range_start_and_an_old_range_end_still_wins()
    {
        DateTime lo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(lo, SimSession.ClampSeekInstant(new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), lo, Now.AddDays(-1), Now));
        // A range that ends long before the horizon keeps its own end.
        DateTime oldMax = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(oldMax, SimSession.ClampSeekInstant(Now, lo, oldMax, Now));
        Assert.Equal(oldMax, SimSession.RawSeekLimit(oldMax, Now));
    }

    [Fact]
    public void GR241_with_no_known_range_only_the_horizon_applies()
    {
        Assert.Null(SimSession.RawSeekLimit(null, Now));
        Assert.Equal(HorizonAtNow, SimSession.ClampSeekInstant(Now, null, null, Now));
        DateTime old = new(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(old, SimSession.ClampSeekInstant(old, null, null, Now));
    }

    [Fact]
    public void GR241_the_transport_bar_scrub_and_date_handlers_and_the_session_seek_all_use_the_clamp()
    {
        string bar = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Web", "Components", "TransportBar.razor"));
        Assert.Equal(2, bar.Split("_session.ClampSeek(", StringSplitOptions.None).Length - 1);   // OnRawScrub and OnRawDate
        Assert.Contains("RawSeekMax", bar, StringComparison.Ordinal);                           // the picker's max
        string session = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Web", "Sessions", "SimSession.cs"));
        Assert.Contains("_bridge.SeekAsync(ClampSeek(at)", session, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-218 and GR-221: the settings.
    // ---------------------------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Fact]
    public void Limits_default_when_the_settings_are_unset()
    {
        WebLimits l = WebLimits.FromConfiguration(Config());
        Assert.Equal(8, l.MaxConcurrentSolves);
        Assert.Equal(TimeSpan.FromSeconds(120), l.SolveTimeout);
        Assert.Equal(5, l.WsControlRatePerSecond);
        Assert.Equal(20, l.WsControlBurst);
        Assert.Equal(8, l.FreeSolveSlots);
    }

    [Fact]
    public void Limits_read_each_setting_by_its_documented_name()
    {
        WebLimits l = WebLimits.FromConfiguration(Config(
            ("GridSim:MaxConcurrentSolves", "3"), ("GridSim:SolveTimeoutSeconds", "45"),
            ("GridSim:WsControlRatePerSecond", "2"), ("GridSim:WsControlBurst", "7")));
        Assert.Equal(3, l.MaxConcurrentSolves);
        Assert.Equal(TimeSpan.FromSeconds(45), l.SolveTimeout);
        Assert.Equal(2, l.WsControlRatePerSecond);
        Assert.Equal(7, l.WsControlBurst);
    }

    [Theory]
    [InlineData("GridSim:MaxConcurrentSolves", "0")]
    [InlineData("GridSim:MaxConcurrentSolves", "257")]
    [InlineData("GridSim:MaxConcurrentSolves", "many")]
    [InlineData("GridSim:SolveTimeoutSeconds", "0")]
    [InlineData("GridSim:SolveTimeoutSeconds", "-5")]
    [InlineData("GridSim:SolveTimeoutSeconds", "3601")]
    [InlineData("GridSim:SolveTimeoutSeconds", "1.5")]
    [InlineData("GridSim:WsControlRatePerSecond", "0")]
    [InlineData("GridSim:WsControlRatePerSecond", "fast")]
    [InlineData("GridSim:WsControlBurst", "0")]
    [InlineData("GridSim:WsControlBurst", "10001")]
    public void A_malformed_limit_stops_start_up_naming_the_setting(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WebLimits.FromConfiguration(Config((key, value))));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_limit_stops_the_host_at_start_up_not_on_the_first_request()
    {
        using var f = Factory(null, ("GridSim:SolveTimeoutSeconds", "0"));
        var startup = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("GridSim:SolveTimeoutSeconds", startup.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-218: the slot cap and the timeout.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GR218_a_full_house_is_refused_without_running_the_work_and_a_freed_slot_is_reusable()
    {
        WebLimits limits = WebLimits.Create(1, TimeSpan.FromSeconds(120), 5, 20);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<SolveRun<int>> held = limits.RunSolveAsync(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return 1; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));
        Assert.Equal(0, limits.FreeSolveSlots);

        bool ran = false;
        SolveRun<int> refused = await limits.RunSolveAsync(() => { ran = true; return 2; });
        Assert.Equal(SolveRunStatus.Busy, refused.Status);
        Assert.False(ran);

        release.Set();
        SolveRun<int> first = await held;
        Assert.Equal(SolveRunStatus.Completed, first.Status);
        Assert.Equal(1, first.Value);
        Assert.Equal(1, limits.FreeSolveSlots);
        SolveRun<int> again = await limits.RunSolveAsync(() => 3);
        Assert.Equal(3, again.Value);
        limits.Dispose();
    }

    [Fact]
    public async Task GR218_a_solve_past_the_timeout_is_reported_timed_out_and_keeps_its_slot_until_it_ends()
    {
        var clock = new ManualClock();
        WebLimits limits = WebLimits.Create(1, TimeSpan.FromSeconds(30), 5, 20, clock);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The call starts the work and arms the timeout on the manual clock before it first yields.
        Task<SolveRun<int>> call = limits.RunSolveAsync(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return 7; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));
        Assert.False(call.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(call.IsCompleted);           // not yet
        clock.Advance(TimeSpan.FromSeconds(1));
        SolveRun<int> run = await call;
        Assert.Equal(SolveRunStatus.TimedOut, run.Status);

        // The work was not stopped, so its slot is still taken and a second call is refused.
        Assert.Equal(0, limits.FreeSolveSlots);
        Assert.Equal(SolveRunStatus.Busy, (await limits.RunSolveAsync(() => 0)).Status);

        release.Set();   // the abandoned solve ends; its slot comes back
        Assert.True(SpinWait.SpinUntil(() => limits.FreeSolveSlots == 1, TimeSpan.FromSeconds(60)));
        limits.Dispose();
    }

    [Fact]
    public async Task GR218_a_fault_in_the_work_reaches_the_caller_and_frees_the_slot()
    {
        WebLimits limits = WebLimits.Create(1, TimeSpan.FromSeconds(120), 5, 20);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => limits.RunSolveAsync<int>(() => throw new InvalidOperationException("solver failed")));
        Assert.Equal("solver failed", ex.Message);
        Assert.Equal(1, limits.FreeSolveSlots);
        limits.Dispose();
    }

    [Fact]
    public async Task GR218_the_rest_helper_answers_504_with_a_clear_error_on_timeout()
    {
        var clock = new ManualClock();
        WebLimits limits = WebLimits.Create(1, TimeSpan.FromSeconds(30), 5, 20, clock);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ctx = new DefaultHttpContext();
        Task<IResult> call = RestApi.RunLimited(limits, ctx,
            () => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return Results.Ok(); }, CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));
        clock.Advance(TimeSpan.FromSeconds(30));
        (int status, string body) = await Execute(await call);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, status);
        Assert.Contains("GridSim:SolveTimeoutSeconds", body, StringComparison.Ordinal);
        Assert.Contains("30 seconds", body, StringComparison.Ordinal);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => limits.FreeSolveSlots == 1, TimeSpan.FromSeconds(60)));
        limits.Dispose();
    }

    private static async Task<(int Status, string Body)> Execute(IResult result)
    {
        var services = new ServiceCollection().AddLogging();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        using var body = new MemoryStream();
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task GR218_solve_and_topology_answer_503_with_retry_after_when_every_slot_is_taken_and_recover_when_one_frees()
    {
        await using var f = Factory(null, ("GridSim:MaxConcurrentSolves", "1"), ("GridSim:AnonymousRole", "Operator"));
        using HttpClient c = Client(f);
        WebLimits limits = f.Services.GetRequiredService<WebLimits>();
        Assert.Equal(1, limits.MaxConcurrentSolves);

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<SolveRun<int>> held = limits.RunSolveAsync(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return 1; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));

        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.True(r.Headers.Contains("Retry-After"));
            Assert.Contains("GridSim:MaxConcurrentSolves", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (HttpResponseMessage r = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.True(r.Headers.Contains("Retry-After"));
        }
        // A malformed request is still told it is malformed: it never needed a slot.
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { }))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        release.Set();
        Assert.Equal(SolveRunStatus.Completed, (await held).Status);
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (HttpResponseMessage r = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, limits.FreeSolveSlots);   // every request gave its slot back
    }

    [Fact]
    public async Task GR218_the_role_gate_is_in_front_of_the_slot()
    {
        await using var f = Factory(null, ("GridSim:MaxConcurrentSolves", "1"), ("GridSim:AnonymousRole", "Viewer"));
        using HttpClient c = Client(f);
        WebLimits limits = f.Services.GetRequiredService<WebLimits>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<SolveRun<int>> held = limits.RunSolveAsync(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return 1; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(60)));

        // A Viewer is below the Analyst band: refused 403 by the gate, not 503 by the cap, and no slot was wanted.
        using (HttpResponseMessage r = await c.PostAsJsonAsync(new Uri("/api/solve", UriKind.Relative), new { @case = "case9" }))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        // A Viewer may read a topology, which does need a slot, so with the cap full it is 503.
        using (HttpResponseMessage r = await c.GetAsync(new Uri("/api/topology?case=case9", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);

        release.Set();
        await held;
    }

    [Fact]
    public void GR218_the_default_cap_and_timeout_are_far_above_what_an_existing_test_asks_for()
    {
        // No existing web test makes more than a handful of REST calls on one host, and each host has its own WebLimits.
        // The defaults are 8 slots and 120 s; this pins them so a later change is a deliberate one.
        Assert.Equal(8, WebLimits.DefaultMaxConcurrentSolves);
        Assert.Equal(120, WebLimits.DefaultSolveTimeoutSeconds);
        Assert.Equal(5, WebLimits.DefaultWsControlRatePerSecond);
        Assert.Equal(20, WebLimits.DefaultWsControlBurst);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-221: the token bucket and the socket.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void GR221_the_bucket_allows_the_burst_then_refuses_then_refills_with_the_clock()
    {
        var clock = new ManualClock();
        var bucket = new TokenBucket(ratePerSecond: 2, burst: 3, clock);
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());            // burst spent, no time has passed
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.False(bucket.TryTake());            // 0.8 of a token
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(bucket.TryTake());             // 1.0 token
        Assert.False(bucket.TryTake());
        clock.Advance(TimeSpan.FromHours(1));      // a long idle fills the bucket but never past the burst
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void GR221_the_bucket_rejects_a_zero_rate_or_burst_and_a_missing_clock()
    {
        var clock = new ManualClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, 3, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(double.NaN, 3, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, 0, clock));
        Assert.Throws<ArgumentNullException>(() => new TokenBucket(1, 1, null!));
    }

    [Fact]
    public void GR221_only_step_and_start_frames_count()
    {
        Assert.True(SimWebSocket.IsSolveFrame("step"));
        Assert.True(SimWebSocket.IsSolveFrame(" START "));
        foreach (string other in new[] { "play", "pause", "seek", "speed", "trip", "", "bogus" })
            Assert.False(SimWebSocket.IsSolveFrame(other));
        Assert.False(SimWebSocket.IsSolveFrame(null));
    }

    private static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<JsonDocument> ReadNext(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult res;
        do
        {
            res = await ws.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, res.Count);
        } while (!res.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray());
    }

    /// <summary>Read frames until one of type <paramref name="type"/> arrives (state frames may be interleaved, so a wide bound).</summary>
    private static async Task<JsonDocument> ReadUntil(WebSocket ws, string type, CancellationToken ct)
    {
        for (int k = 0; k < 500; k++)
        {
            JsonDocument d = await ReadNext(ws, ct);
            if (d.RootElement.GetProperty("type").GetString() == type) return d;
            d.Dispose();
        }
        throw new InvalidOperationException($"no '{type}' frame within 500 messages");
    }

    private static async Task<string> NextErrorMessage(WebSocket ws, CancellationToken ct)
    {
        using JsonDocument d = await ReadUntil(ws, "error", ct);
        return d.RootElement.GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task GR221_a_socket_over_its_step_and_start_budget_gets_an_error_frame_and_keeps_its_session()
    {
        var clock = new ManualClock();
        // Burst 3 and 1 a second; the clock only moves when the test moves it.
        WebLimits limits = WebLimits.Create(8, TimeSpan.FromSeconds(120), wsRatePerSecond: 1, wsBurst: 3, clock);
        await using var f = Factory(limits, ("GridSim:AnonymousRole", "Operator"));
        using WebSocket ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        await Send(ws, "{\"action\":\"start\",\"case\":\"case9\"}");          // token 1
        using (await ReadUntil(ws, "topology", timeout.Token)) { }
        await Send(ws, "{\"action\":\"pause\"}");                              // not counted
        await Send(ws, "{\"action\":\"step\"}");                               // token 2
        await Send(ws, "{\"action\":\"step\"}");                               // token 3
        await Send(ws, "{\"action\":\"step\"}");                               // over the budget
        string limited = await NextErrorMessage(ws, timeout.Token);
        Assert.Contains("rate limit", limited, StringComparison.Ordinal);
        Assert.Contains("GridSim:WsControlBurst", limited, StringComparison.Ordinal);
        Assert.Equal(1, f.Services.GetRequiredService<SessionManager>().ActiveSessions);   // the session was left alone

        // Frames that do not run a solve are not counted: five pauses and then a bad action, whose own error comes first.
        for (int k = 0; k < 5; k++) await Send(ws, "{\"action\":\"pause\"}");
        await Send(ws, "{\"action\":\"bogus\"}");
        Assert.Contains("unknown action", await NextErrorMessage(ws, timeout.Token), StringComparison.Ordinal);

        // One second on the injected clock buys one token: the next step runs (no rate error before the bad action's).
        clock.Advance(TimeSpan.FromSeconds(1));
        await Send(ws, "{\"action\":\"step\"}");
        await Send(ws, "{\"action\":\"bogus\"}");
        Assert.Contains("unknown action", await NextErrorMessage(ws, timeout.Token), StringComparison.Ordinal);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task GR221_each_socket_has_its_own_budget()
    {
        var clock = new ManualClock();
        WebLimits limits = WebLimits.Create(8, TimeSpan.FromSeconds(120), wsRatePerSecond: 1, wsBurst: 1, clock);
        await using var f = Factory(limits, ("GridSim:AnonymousRole", "Operator"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using WebSocket a = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);
        using WebSocket b = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri(f.Server.BaseAddress, "/ws/sim"), CancellationToken.None);

        // Socket A spends its single token on a start, so its next step is refused ...
        await Send(a, "{\"action\":\"start\",\"case\":\"case9\"}");
        using (await ReadUntil(a, "topology", timeout.Token)) { }
        await Send(a, "{\"action\":\"step\"}");
        Assert.Contains("rate limit", await NextErrorMessage(a, timeout.Token), StringComparison.Ordinal);

        // ... while socket B still has its own full bucket for its start.
        await Send(b, "{\"action\":\"start\",\"case\":\"case9\"}");
        using (await ReadUntil(b, "topology", timeout.Token)) { }

        await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }
}
