using System.Security.Claims;
using GridSim.Core.PowerFlow;
using GridSim.Web.Model;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Web batch E4 (November 2026). New regression tests only; no existing test is touched.
/// <list type="bullet">
/// <item>GR-732: the sign-in cookie slides over 8 hours (Auth:SlidingHours) and is capped at 14 days from sign-in
/// (Auth:AbsoluteDays), whatever renewals happen in between.</item>
/// <item>GR-235: one client is capped at GridSim:MaxSessionsPerClient sessions while others can still open theirs.</item>
/// </list>
/// </summary>
public sealed class GA7E4WebTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-ga7e4-" + Guid.NewGuid().ToString("N"));

    public GA7E4WebTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        string keys = Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-732: cookie lifetime settings
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Gr732_Defaults_Are_Eight_Hours_Sliding_And_Fourteen_Days_Absolute()
    {
        SessionCookieSettings s = SessionCookieSettings.Read(Config());
        Assert.Equal(TimeSpan.FromHours(8), s.Sliding);
        Assert.Equal(TimeSpan.FromDays(14), s.Absolute);
        Assert.Equal("Auth:SlidingHours", SessionCookieSettings.SlidingHoursSetting);
        Assert.Equal("Auth:AbsoluteDays", SessionCookieSettings.AbsoluteDaysSetting);
    }

    [Fact]
    public void Gr732_Valid_Values_Are_Read()
    {
        SessionCookieSettings s = SessionCookieSettings.Read(Config(("Auth:SlidingHours", "2"), ("Auth:AbsoluteDays", "3")));
        Assert.Equal(TimeSpan.FromHours(2), s.Sliding);
        Assert.Equal(TimeSpan.FromDays(3), s.Absolute);
    }

    [Theory]
    [InlineData("Auth:SlidingHours", "")]
    [InlineData("Auth:SlidingHours", "eight")]
    [InlineData("Auth:SlidingHours", "8h")]
    [InlineData("Auth:SlidingHours", "1.5")]
    [InlineData("Auth:SlidingHours", "-1")]
    [InlineData("Auth:SlidingHours", "0")]
    [InlineData("Auth:SlidingHours", "99999")]
    [InlineData("Auth:AbsoluteDays", "")]
    [InlineData("Auth:AbsoluteDays", "two weeks")]
    [InlineData("Auth:AbsoluteDays", "0")]
    [InlineData("Auth:AbsoluteDays", "-14")]
    [InlineData("Auth:AbsoluteDays", "366")]
    public void Gr732_Malformed_Values_Are_Refused_Naming_The_Setting(string key, string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SessionCookieSettings.Read(Config((key, value))));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr732_Cap_Shorter_Than_Window_Is_Refused()
    {
        // 48 hours of idle window against a 1 day cap: the cap would cut every long idle window short.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SessionCookieSettings.Read(Config(("Auth:SlidingHours", "48"), ("Auth:AbsoluteDays", "1"))));
        Assert.Contains("Auth:AbsoluteDays", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Auth:SlidingHours", ex.Message, StringComparison.Ordinal);
        // Equal is allowed.
        SessionCookieSettings ok = SessionCookieSettings.Read(Config(("Auth:SlidingHours", "24"), ("Auth:AbsoluteDays", "1")));
        Assert.Equal(ok.Sliding, ok.Absolute);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-732: the absolute cap (pure Judge, injected clock)
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset SignIn = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cap = TimeSpan.FromDays(14);

    [Fact]
    public void Gr732_Judge_Rejects_A_SignIn_Older_Than_The_Cap_Even_If_It_Was_Renewed()
    {
        // The stamp is the original sign-in; a renewal changes IssuedUtc, never the stamp, so age is measured from here.
        string stamp = SessionCookieSettings.FormatStamp(SignIn);
        Assert.Equal(CookieVerdict.Expired, SessionCookieSettings.Judge(stamp, SignIn + Cap + TimeSpan.FromSeconds(1), Cap));
        Assert.Equal(CookieVerdict.Expired, SessionCookieSettings.Judge(stamp, SignIn + TimeSpan.FromDays(60), Cap));
    }

    [Fact]
    public void Gr732_Judge_Accepts_A_SignIn_Inside_The_Cap()
    {
        string stamp = SessionCookieSettings.FormatStamp(SignIn);
        Assert.Equal(CookieVerdict.Accept, SessionCookieSettings.Judge(stamp, SignIn, Cap));
        Assert.Equal(CookieVerdict.Accept, SessionCookieSettings.Judge(stamp, SignIn + TimeSpan.FromDays(13), Cap));
        Assert.Equal(CookieVerdict.Accept, SessionCookieSettings.Judge(stamp, SignIn + Cap, Cap));   // exactly the cap
        // A stamp a little in the future (clock skew between instances) is accepted, not rejected.
        Assert.Equal(CookieVerdict.Accept, SessionCookieSettings.Judge(stamp, SignIn - TimeSpan.FromMinutes(2), Cap));
    }

    [Fact]
    public void Gr732_Judge_Asks_For_A_Stamp_When_There_Is_None_And_Refuses_An_Unreadable_One()
    {
        Assert.Equal(CookieVerdict.NeedsStamp, SessionCookieSettings.Judge(null, SignIn, Cap));
        Assert.Equal(CookieVerdict.Expired, SessionCookieSettings.Judge("", SignIn, Cap));
        Assert.Equal(CookieVerdict.Expired, SessionCookieSettings.Judge("yesterday", SignIn, Cap));
    }

    private static CookieValidatePrincipalContext ValidationContext(AuthenticationProperties props)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("test"));
        var ticket = new AuthenticationTicket(principal, props, IdentityConstants.ApplicationScheme);
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        return new CookieValidatePrincipalContext(new DefaultHttpContext(), scheme, new CookieAuthenticationOptions(), ticket);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Gr732_Validate_Keeps_A_Stamped_Cookie_Inside_The_Cap()
    {
        var props = new AuthenticationProperties { IssuedUtc = SignIn + TimeSpan.FromDays(10) };
        props.Items[SessionCookieSettings.SignedInItem] = SessionCookieSettings.FormatStamp(SignIn);
        CookieValidatePrincipalContext ctx = ValidationContext(props);
        await SessionCookieSettings.ValidateAsync(ctx, Cap, new FixedClock(SignIn + TimeSpan.FromDays(10)));
        Assert.NotNull(ctx.Principal);
        Assert.False(ctx.ShouldRenew);
        Assert.Equal(SessionCookieSettings.FormatStamp(SignIn), props.Items[SessionCookieSettings.SignedInItem]);
    }

    [Fact]
    public async Task Gr732_Validate_Stamps_An_Unstamped_Cookie_From_Its_IssuedUtc_And_Renews_It()
    {
        var props = new AuthenticationProperties { IssuedUtc = SignIn };
        CookieValidatePrincipalContext ctx = ValidationContext(props);
        await SessionCookieSettings.ValidateAsync(ctx, Cap, new FixedClock(SignIn + TimeSpan.FromHours(1)));
        Assert.NotNull(ctx.Principal);
        Assert.True(ctx.ShouldRenew);   // so the stamp is written back into the cookie
        Assert.Equal(SessionCookieSettings.FormatStamp(SignIn), props.Items[SessionCookieSettings.SignedInItem]);
    }

    [Fact]
    public void Gr732_Host_Cookie_Options_Carry_The_Eight_Hour_Sliding_Window()
    {
        using WebApplicationFactory<Program> f = Factory();
        CookieAuthenticationOptions o = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        Assert.True(o.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), o.ExpireTimeSpan);
    }

    [Fact]
    public void Gr732_Host_Cookie_Options_Follow_The_Setting()
    {
        using WebApplicationFactory<Program> f = Factory(("Auth:SlidingHours", "3"), ("Auth:AbsoluteDays", "2"));
        CookieAuthenticationOptions o = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        Assert.Equal(TimeSpan.FromHours(3), o.ExpireTimeSpan);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-235: the per-client session quota
    // ---------------------------------------------------------------------------------------------------------------

    private static SessionManager Manager(params (string Key, string Value)[] settings) =>
        new(new CaseCatalog(AppContext.BaseDirectory), Config(settings));

    [Fact]
    public void Gr235_Quota_Defaults_And_Malformed_Values_Are_Refused_Naming_The_Setting()
    {
        Assert.Equal("GridSim:MaxSessionsPerClient", SessionManager.MaxSessionsPerClientSetting);
        Assert.Equal(SessionManager.DefaultMaxSessionsPerClient, Manager(("Cycle:Enabled", "false")).MaxSessionsPerClient);
        Assert.Equal(16, SessionManager.DefaultMaxSessionsPerClient);
        Assert.Equal(3, Manager(("GridSim:MaxSessionsPerClient", "3"), ("Cycle:Enabled", "false")).MaxSessionsPerClient);
        foreach (string bad in new[] { "0", "-1", "two", "1.5", "4 sessions" })
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => Manager(("GridSim:MaxSessionsPerClient", bad)));
            Assert.Contains("GridSim:MaxSessionsPerClient", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Gr235_One_Client_Is_Capped_While_Another_Can_Still_Open_Sessions_And_Release_Frees_The_Quota()
    {
        SessionManager m = Manager(("GridSim:MaxSessionsPerClient", "2"), ("GridSim:MaxSessions", "10"), ("Cycle:Enabled", "false"));
        SimSession a1 = m.Create("case9", SolverKind.Auto, "ip:10.0.0.1");
        SimSession a2 = m.Create("case9", SolverKind.Auto, "ip:10.0.0.1");
        Assert.Equal(2, m.ActiveSessionsFor("ip:10.0.0.1"));

        // The third from the same client is refused with a message naming the setting; it holds no slot.
        ClientSessionLimitException ex = Assert.Throws<ClientSessionLimitException>(
            () => m.Create("case9", SolverKind.Auto, "ip:10.0.0.1"));
        Assert.Contains("GridSim:MaxSessionsPerClient", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Max);
        Assert.Equal(2, m.ActiveSessions);
        Assert.Equal(2, m.ActiveSessionsFor("ip:10.0.0.1"));

        // Another client is not affected, and neither is a caller with no key.
        SimSession b1 = m.Create("case9", SolverKind.Auto, "ip:10.0.0.2");
        SimSession anon = m.Create("case9");
        Assert.Equal(4, m.ActiveSessions);

        // Release frees the quota (and a second dispose gives nothing back twice).
        await a1.DisposeAsync();
        await a1.DisposeAsync();
        Assert.Equal(1, m.ActiveSessionsFor("ip:10.0.0.1"));
        SimSession a3 = m.Create("case9", SolverKind.Auto, "ip:10.0.0.1");
        Assert.Equal(2, m.ActiveSessionsFor("ip:10.0.0.1"));

        // A create that fails (unknown case) holds neither a global nor a client slot.
        Assert.Throws<KeyNotFoundException>(() => m.Create("no-such-case", SolverKind.Auto, "ip:10.0.0.2"));
        Assert.Equal(1, m.ActiveSessionsFor("ip:10.0.0.2"));

        foreach (SimSession s in new[] { a2, a3, b1, anon }) await s.DisposeAsync();
        Assert.Equal(0, m.ActiveSessions);
        Assert.Equal(0, m.ActiveSessionsFor("ip:10.0.0.1"));
        Assert.Equal(0, m.ActiveSessionsFor("ip:10.0.0.2"));
    }

    [Fact]
    public async Task Gr235_The_Global_Cap_Still_Applies_Across_Clients()
    {
        SessionManager m = Manager(("GridSim:MaxSessionsPerClient", "5"), ("GridSim:MaxSessions", "2"), ("Cycle:Enabled", "false"));
        SimSession a = m.Create("case9", SolverKind.Auto, "ip:10.0.0.1");
        SimSession b = m.Create("case9", SolverKind.Auto, "ip:10.0.0.2");
        Assert.Throws<SessionLimitException>(() => m.Create("case9", SolverKind.Auto, "ip:10.0.0.3"));
        Assert.Equal(0, m.ActiveSessionsFor("ip:10.0.0.3"));   // the refused client was not counted
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public void Gr235_Client_Key_Is_The_SignedIn_Subject_Or_The_Remote_Address()
    {
        var access = new AccessControl(GridSim.Core.Security.OperationRole.Viewer, true, AccessDenialAudit.None);
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ada") }, "test"));
        System.Net.IPAddress v4 = System.Net.IPAddress.Parse("203.0.113.9");

        // Signed in: the subject, whatever the address.
        Assert.Equal("user:ada", access.ClientKeyFor(signedIn, v4));
        Assert.Equal("user:ada", access.ClientKeyFor(signedIn, null));
        // Anonymous: the address; an IPv4-mapped IPv6 spelling is the same client.
        Assert.Equal("ip:203.0.113.9", access.ClientKeyFor(null, v4));
        Assert.Equal("ip:203.0.113.9", access.ClientKeyFor(new ClaimsPrincipal(new ClaimsIdentity()), v4.MapToIPv6()));
        // Anonymous with no address: no key (the global cap alone applies).
        Assert.Null(access.ClientKeyFor(null, null));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // GR-224: bridge projection on a worker of its own
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);   // a hang fails the test, never stalls the run

    [Fact]
    public async Task Gr224_The_Receive_Callback_Returns_Without_Waiting_For_A_Slow_Projection_And_Nothing_Is_Dropped_Or_Reordered()
    {
        using var release = new ManualResetEventSlim(false);
        using var firstStarted = new ManualResetEventSlim(false);
        var projected = new List<int>();
        var faults = new List<Exception>();

        await using var worker = new BridgeProjectionWorker<int>(
            item =>
            {
                firstStarted.Set();
                Assert.True(release.Wait(Deadline), "the test never released the projector");
                lock (projected) projected.Add(item);
            },
            ex => { lock (faults) faults.Add(ex); },
            capacity: 16);

        // A burst of ten frames while the projector is stuck on the first: every Enqueue returns at once, true.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) Assert.True(worker.Enqueue(i));
        watch.Stop();
        Assert.True(firstStarted.Wait(Deadline));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"enqueueing waited for the projection ({watch.Elapsed})");
        lock (projected) Assert.Empty(projected);   // nothing has finished: the receive side did not wait for it

        release.Set();
        await WaitUntilAsync(() => { lock (projected) return projected.Count == 10; });

        // Every frame, in arrival order: an older frame is never published after a newer one, and none is dropped.
        lock (projected) Assert.Equal(Enumerable.Range(0, 10), projected);
        lock (faults) Assert.Empty(faults);
        Assert.False(worker.Faulted);
    }

    [Fact]
    public async Task Gr224_A_Full_Queue_Fails_Loud_Once_And_Accepts_Nothing_More()
    {
        using var release = new ManualResetEventSlim(false);
        using var firstStarted = new ManualResetEventSlim(false);
        var projected = new List<int>();
        var faults = new List<Exception>();

        await using var worker = new BridgeProjectionWorker<int>(
            item =>
            {
                firstStarted.Set();
                Assert.True(release.Wait(Deadline));
                lock (projected) projected.Add(item);
            },
            ex => { lock (faults) faults.Add(ex); },
            capacity: 2);

        Assert.True(worker.Enqueue(0));
        Assert.True(firstStarted.Wait(Deadline));   // item 0 is now being projected, so the two slots are free
        Assert.True(worker.Enqueue(1));
        Assert.True(worker.Enqueue(2));
        Assert.False(worker.Enqueue(3));            // full: refused, loudly
        Assert.True(worker.Faulted);
        lock (faults)
        {
            Exception only = Assert.Single(faults);
            Assert.IsType<InvalidOperationException>(only);
            Assert.Contains("faster than they can be projected", only.Message, StringComparison.Ordinal);
        }

        // Nothing more is accepted, and no second report is made.
        Assert.False(worker.Enqueue(4));
        lock (faults) Assert.Single(faults);

        release.Set();
        await worker.DisposeAsync();
        // Whatever was projected is in arrival order, and 3 and 4 were never queued.
        lock (projected)
        {
            Assert.Equal(projected.OrderBy(x => x).ToList(), projected);
            Assert.DoesNotContain(3, projected);
            Assert.DoesNotContain(4, projected);
        }
    }

    [Fact]
    public async Task Gr224_A_Projection_That_Throws_Is_Reported_Once_And_Stops_The_Worker()
    {
        var faults = new List<Exception>();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new BridgeProjectionWorker<int>(
            _ => throw new InvalidOperationException("projection broke"),
            ex => { lock (faults) faults.Add(ex); reported.TrySetResult(); },
            capacity: 4);

        Assert.True(worker.Enqueue(1));
        await reported.Task.WaitAsync(Deadline);
        lock (faults) Assert.Equal("projection broke", Assert.Single(faults).Message);
        Assert.True(worker.Faulted);
        Assert.False(worker.Enqueue(2));
        lock (faults) Assert.Single(faults);
    }

    [Fact]
    public async Task Gr224_Dispose_Stops_The_Worker_And_Is_Safe_To_Repeat()
    {
        int calls = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new BridgeProjectionWorker<int>(
            _ => { Interlocked.Increment(ref calls); done.TrySetResult(); },
            _ => { },
            capacity: 4);

        Assert.True(worker.Enqueue(1));
        await done.Task.WaitAsync(Deadline);

        await worker.DisposeAsync().AsTask().WaitAsync(Deadline);   // returns: the worker has ended
        await worker.DisposeAsync().AsTask().WaitAsync(Deadline);   // a second dispose is harmless

        int before = Volatile.Read(ref calls);
        Assert.False(worker.Enqueue(2));                            // nothing is accepted after dispose
        await Task.Delay(100);
        Assert.Equal(before, Volatile.Read(ref calls));             // and nothing runs
        Assert.False(worker.Faulted);                               // a disposal is not a fault
    }

    [Fact]
    public async Task Gr224_Dispose_Waits_For_A_Projection_Already_Running_Then_Abandons_The_Queue()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var projected = new List<int>();
        await using var worker = new BridgeProjectionWorker<int>(
            item =>
            {
                started.Set();
                release.Wait(Deadline);
                lock (projected) projected.Add(item);
            },
            _ => { },
            capacity: 8);

        Assert.True(worker.Enqueue(0));
        Assert.True(started.Wait(Deadline));
        Assert.True(worker.Enqueue(1));   // waiting behind the running one

        Task disposing = worker.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);   // the running projection has not finished
        release.Set();
        await disposing.WaitAsync(Deadline);

        lock (projected) Assert.Equal(new[] { 0 }, projected);   // the running one finished; the queued one was abandoned
    }

    [Fact]
    public void Gr224_The_Session_Queue_Capacity_Is_Pinned()
    {
        // 64 frames behind: far above the lag of a healthy session, small enough that a stalled one cannot hold a
        // large number of solved frames in memory before it is stopped.
        Assert.Equal(64, SimSession.BridgeProjectionQueueCapacity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("the condition did not hold within the deadline");
            await Task.Delay(10);
        }
    }
}
