using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebSite.Data.Sso;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-2 (docs-session findings, stream 2), the security half: D-DOC-016 replay-only covers every live route,
/// D-DOC-017 / 196 SSO tickets are single-use and the ticket endpoints are tested, D-DOC-171 the SSO audience behind a
/// proxy, D-DOC-064 key-ring encryption at rest, D-DOC-170 the login challenge and the authority refusal. Every host is
/// the real GridSim.Web app in memory (WebApplicationFactory) with its own temporary key folder, so no test reads or
/// writes a real key ring. No secret, personal name, host or address appears: origins are example.org / localhost.
/// </summary>
public sealed class WebSecurityDoc2Tests
{
    /// <summary>A factory for the web app with the given settings (Development environment) and a fresh key folder,
    /// or <paramref name="keysDir"/> when a test needs two hosts on one ring.</summary>
    private static WebApplicationFactory<Program> Factory(string? keysDir, params (string Key, string Value)[] settings)
    {
        string keys = keysDir ?? NewTempDir("gridsim-doc2-keys-");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
    }

    private static string NewTempDir(string prefix)
    {
        string d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static HttpClient Client(WebApplicationFactory<Program> f)
        => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-016 (SECURITY): replay-only covers every live route.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Dashboard addresses that would open something other than the raw replay: a built-in live case (also
    /// with a solver, other letter case, a trailing slash), the live-bridge card, every non-raw bridge source, an
    /// unknown source, a presolved run id and an empty id.</summary>
    public static TheoryData<string> LiveDashboardPaths => new()
    {
        "/dashboard/case9",
        "/dashboard/case9?solver=sparse",
        "/Dashboard/CASE118",
        "/dashboard/case9/",
        "/dashboard/gb-reduced",
        "/dashboard/live-bridge",
        "/dashboard/bridge:gb-full",
        "/dashboard/bridge:gb-full@presolved",
        "/dashboard/bridge:gb-full@estimated",
        "/dashboard/bridge:gb-full@live",
        "/dashboard/bridge:gb-full@solves",
        "/dashboard/bridge:gb-full@bogus",
        "/dashboard/run-anything",
        "/dashboard/",
    };

    [Theory]
    [MemberData(nameof(LiveDashboardPaths))]
    public async Task Replay_only_refuses_every_dashboard_route_but_the_raw_replay(string path)
    {
        // The hole: before D-DOC-016, GET /dashboard/case9 on a replay-only host answered 200 with a LIVE solver session
        // (header SOLVER picker, every tab, AUDIT LOG included). Now the page is refused before any session code runs.
        await using var f = Factory(null, ("GridSim:ReplayOnly", "true"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("solver-pick", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AUDIT LOG", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Initialising session", body, StringComparison.Ordinal);
        // No session was created on the way (the HTTP gate stops the request before the page).
        Assert.Equal(0, f.Services.GetRequiredService<SessionManager>().ActiveSessions);
    }

    [Fact]
    public async Task Replay_only_still_serves_the_raw_replay_page()
    {
        // The one route a replay-only instance offers stays open. No bridge is configured in the test, so the page
        // shows its load-error panel naming the missing endpoint, which proves the request reached the session code.
        await using var f = Factory(null, ("GridSim:ReplayOnly", "true"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/dashboard/" + SessionManager.RawLandingCaseId, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("No GDA bridge endpoint is configured", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_only_session_manager_refuses_live_sessions_on_every_path()
    {
        // The second lock: SessionManager.Create itself refuses, so a navigation inside an already-open Blazor circuit
        // (which makes no HTTP request and never meets the gate) or a future route cannot open a live session either.
        using var f = Factory(null, ("GridSim:ReplayOnly", "true"));
        SessionManager m = f.Services.GetRequiredService<SessionManager>();
        Assert.True(m.ReplayOnly);
        foreach (string id in new[] { "case9", "gb-reduced", "live-bridge", "bridge:gb-full@presolved", "bridge:gb-full@estimated", "bridge:x@nonsense", "" })
        {
            var ex = Assert.Throws<ReplayOnlyRefusedException>(() => m.Create(id));
            Assert.Contains("GridSim:ReplayOnly=true", ex.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, m.ActiveSessions);
    }

    [Fact]
    public void Only_bridge_raw_ids_are_allowed_on_a_replay_only_instance()
    {
        Assert.True(SessionManager.IsAllowedOnReplayOnly("bridge:gb-full@raw"));
        Assert.True(SessionManager.IsAllowedOnReplayOnly("BRIDGE:gb-full@RAW"));
        Assert.True(SessionManager.IsAllowedOnReplayOnly("bridge:@raw"));   // blank remote case means gb-full
        foreach (string? id in new[] { null, "", " ", "case9", "live-bridge", "bridge:gb-full", "bridge:gb-full@presolved", "bridge:gb-full@raw2", "bridge:gb-full@", "raw" })
            Assert.False(SessionManager.IsAllowedOnReplayOnly(id), id ?? "(null)");
    }

    [Fact]
    public async Task A_full_instance_still_opens_a_live_dashboard()
    {
        // The gate is replay-only's alone: with GridSim:ReplayOnly=false the live dashboard is served as before.
        await using var f = Factory(null, ("GridSim:ReplayOnly", "false"), ("Cycle:Enabled", "false"));
        using HttpClient c = Client(f);
        using HttpResponseMessage r = await c.GetAsync(new Uri("/dashboard/case9", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("solver-pick", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-017 (SECURITY) / D-DOC-196: SSO tickets are single-use; the ticket endpoints are tested.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>A signed-in visitor as the authority would see them (made-up values only).</summary>
    private static ClaimsPrincipal Visitor() => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "visitor-id-1"),
        new Claim(ClaimTypes.Name, "visitor-1"),
        new Claim(ClaimTypes.Role, "viewer"),
        new Claim("transient-claim", "dropped"),
    }, "test"));

    private static Uri LandUri(string ticket, string r = "/after")
        => new($"/sso/land?t={Uri.EscapeDataString(ticket)}&r={Uri.EscapeDataString(r)}", UriKind.Relative);

    private static bool SetsAuthCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? v)
        && v.Any(s => s.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal)
                      && !s.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));

    [Fact]
    public async Task A_sso_ticket_signs_in_once_and_a_replay_is_refused()
    {
        // The hole: GridSim.Web registers no distributed cache, so before D-DOC-017 the single-use check was skipped and
        // the same ticket signed in every time it was presented until it expired.
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        Assert.Null(f.Services.GetService<IDistributedCache>());   // the precondition of the hole: no shared cache
        using HttpClient c = Client(f);
        string ticket = f.Services.GetRequiredService<EstateSsoTickets>().Issue(Visitor(), "http://localhost");

        using HttpResponseMessage first = await c.GetAsync(LandUri(ticket));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/after", first.Headers.Location?.OriginalString);
        Assert.True(SetsAuthCookie(first), "the first use must sign the visitor in");

        using HttpResponseMessage replay = await c.GetAsync(LandUri(ticket));
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal("/", replay.Headers.Location?.OriginalString);
        Assert.False(SetsAuthCookie(replay), "a replayed ticket must not sign anyone in");
    }

    [Fact]
    public async Task The_same_ticket_raced_from_many_requests_signs_in_exactly_once()
    {
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        string ticket = tickets.Issue(Visitor(), "http://localhost");
        SsoTicketCheck[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => tickets.CheckAsync(ticket, "http://localhost"))));
        Assert.Equal(1, results.Count(r => r.Claims is not null));
        Assert.All(results.Where(r => r.Claims is null), r => Assert.Contains("already used", r.Refusal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ticket_carries_only_the_listed_claims()
    {
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        SsoTicketCheck ok = await tickets.CheckAsync(tickets.Issue(Visitor(), "http://localhost/"), "http://localhost");
        Assert.NotNull(ok.Claims);
        Assert.Null(ok.Refusal);
        Assert.Equal(new[] { ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Role }, ok.Claims!.Select(cl => cl.Type).ToArray());
        Assert.Equal("visitor-1", ok.Claims!.Single(cl => cl.Type == ClaimTypes.Name).Value);
    }

    [Fact]
    public async Task A_ticket_for_another_app_is_refused_and_not_spent()
    {
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        string ticket = tickets.Issue(Visitor(), "https://app.example.org");
        SsoTicketCheck wrong = await tickets.CheckAsync(ticket, "https://other.example.org");
        Assert.Null(wrong.Claims);
        Assert.Contains("'https://app.example.org'", wrong.Refusal, StringComparison.Ordinal);
        Assert.Contains("'https://other.example.org'", wrong.Refusal, StringComparison.Ordinal);
        // Not burned by the mis-addressed attempt: the right audience still signs in, once.
        Assert.NotNull((await tickets.CheckAsync(ticket, "https://app.example.org")).Claims);
        Assert.Null((await tickets.CheckAsync(ticket, "https://app.example.org")).Claims);
    }

    [Fact]
    public async Task Expired_tampered_and_nonce_less_tickets_are_refused_with_a_reason()
    {
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        ITimeLimitedDataProtector p = f.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(EstateSsoTickets.Purpose).ToTimeLimitedDataProtector();

        string expired = p.Protect("{\"a\":\"http://localhost\",\"j\":\"n1\",\"c\":[]}", DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Contains("expired", (await tickets.CheckAsync(expired, "http://localhost")).Refusal, StringComparison.Ordinal);

        string good = tickets.Issue(Visitor(), "http://localhost");
        string tampered = good[..^4] + (good.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        Assert.Contains("tampered", (await tickets.CheckAsync(tampered, "http://localhost")).Refusal, StringComparison.Ordinal);

        string noNonce = p.Protect("{\"a\":\"http://localhost\",\"j\":\"\",\"c\":[]}", TimeSpan.FromMinutes(1));
        Assert.Contains("no nonce", (await tickets.CheckAsync(noNonce, "http://localhost")).Refusal, StringComparison.Ordinal);

        Assert.Contains("no ticket", (await tickets.CheckAsync("", "http://localhost")).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sso_ticket_endpoints_answer_404_when_off_and_bridge_404_on_a_client()
    {
        await using (var off = Factory(null))
        {
            using HttpClient c = Client(off);
            using HttpResponseMessage land = await c.GetAsync(new Uri("/sso/land?t=x", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, land.StatusCode);
        }
        await using (var client = Factory(null, ("Sso:Enabled", "true"), ("Sso:Authority", "https://login.example.org")))
        {
            using HttpClient c = Client(client);
            using HttpResponseMessage bridge = await c.GetAsync(new Uri("/sso/bridge?to=x", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, bridge.StatusCode);
            // A garbage ticket is not an error page: home, anonymous, no cookie.
            using HttpResponseMessage land = await c.GetAsync(LandUri("garbage"));
            Assert.Equal(HttpStatusCode.Redirect, land.StatusCode);
            Assert.Equal("/", land.Headers.Location?.OriginalString);
            Assert.False(SetsAuthCookie(land));
        }
    }

    [Fact]
    public async Task Land_redirect_is_kept_on_this_site()
    {
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        using HttpClient c = Client(f);
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        foreach (string r in new[] { "//elsewhere.example.org/x", "https://elsewhere.example.org/", "relative" })
        {
            using HttpResponseMessage land = await c.GetAsync(LandUri(tickets.Issue(Visitor(), "http://localhost"), r));
            Assert.Equal(HttpStatusCode.Redirect, land.StatusCode);
            Assert.Equal("/", land.Headers.Location?.OriginalString);
        }
    }

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void One_time_store_spends_a_nonce_once_and_forgets_it_only_after_its_keep_time()
    {
        var clock = new ManualTime();
        var store = new SsoOneTimeStore(clock);
        DateTimeOffset until = clock.Now.AddMinutes(2.5);
        Assert.True(store.TryConsume("n1", until));
        Assert.False(store.TryConsume("n1", until));
        Assert.True(store.TryConsume("n2", until));
        Assert.Equal(2, store.Count);

        clock.Now = clock.Now.AddMinutes(1);           // within the keep time: still held after a sweep
        Assert.True(store.TryConsume("n3", clock.Now.AddMinutes(2.5)));
        Assert.False(store.TryConsume("n1", until));
        Assert.Equal(3, store.Count);

        clock.Now = clock.Now.AddMinutes(5);           // n1 and n2 are past their keep time: swept
        Assert.True(store.TryConsume("n4", clock.Now.AddMinutes(2.5)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task With_a_shared_cache_a_ticket_spent_on_one_instance_is_refused_on_another()
    {
        // Two instances behind one name: each has its own in-process store, both share one distributed cache (the
        // multi-instance set-up the class documents). The second instance refuses the ticket the first accepted.
        var dp = new EphemeralDataProtectionProvider();
        var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        IDistributedCache cache = shared.GetRequiredService<IDistributedCache>();
        EstateSsoTickets Instance()
        {
            var sp = new ServiceCollection().AddSingleton(cache).AddSingleton(new SsoOneTimeStore()).BuildServiceProvider();
            return new EstateSsoTickets(dp, Options.Create(new EstateSsoOptions { Enabled = true }), sp);
        }
        EstateSsoTickets a = Instance(), b = Instance();
        string ticket = a.Issue(Visitor(), "https://app.example.org");
        Assert.NotNull((await a.CheckAsync(ticket, "https://app.example.org")).Claims);
        SsoTicketCheck onB = await b.CheckAsync(ticket, "https://app.example.org");
        Assert.Null(onB.Claims);
        Assert.Contains("another instance", onB.Refusal, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-171: the SSO audience behind a proxy.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_http_vs_https_audience_mismatch_names_the_proxy_header_and_the_setting()
    {
        // The silent failure: a TLS proxy that does not forward X-Forwarded-Proto makes the app see http, the ticket
        // says https, and every sign-in quietly bounced home. The refusal now says why (and is logged by /sso/land).
        await using var f = Factory(null, ("Sso:Enabled", "true"));
        EstateSsoTickets tickets = f.Services.GetRequiredService<EstateSsoTickets>();
        SsoTicketCheck r = await tickets.CheckAsync(tickets.Issue(Visitor(), "https://app.example.org"), "http://app.example.org");
        Assert.Null(r.Claims);
        Assert.Contains("X-Forwarded-Proto", r.Refusal, StringComparison.Ordinal);
        Assert.Contains("Sso:PublicOrigin", r.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sso_public_origin_is_the_audience_when_configured()
    {
        // The test server is reached as http://localhost; with Sso:PublicOrigin the app answers as the public origin,
        // so a ticket minted for it signs in even though the request itself arrived as plain http on another name.
        await using var f = Factory(null, ("Sso:Enabled", "true"), ("Sso:PublicOrigin", "https://app.example.org/"));
        using HttpClient c = Client(f);
        string ticket = f.Services.GetRequiredService<EstateSsoTickets>().Issue(Visitor(), "https://app.example.org");
        using HttpResponseMessage land = await c.GetAsync(LandUri(ticket));
        Assert.Equal("/after", land.Headers.Location?.OriginalString);
        Assert.True(SetsAuthCookie(land));
    }

    [Theory]
    [InlineData("app.example.org")]
    [InlineData("https://app.example.org/path")]
    [InlineData("ftp://app.example.org")]
    [InlineData("https://app.example.org/?q=1")]
    public void A_malformed_sso_public_origin_stops_start_up(string value)
    {
        using var f = Factory(null, ("Sso:Enabled", "true"), ("Sso:PublicOrigin", value));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Sso:PublicOrigin", ex.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-170: no login page, so no redirect to one; GridSim is never the SSO authority.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cookie_challenge_answers_401_and_a_forbid_403_without_a_redirect()
    {
        // Before: the cookie handler's defaults redirected to /Account/Login and /Account/AccessDenied, pages this app
        // does not serve.
        using var f = Factory(null);
        CookieAuthenticationOptions o = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));

        var login = new DefaultHttpContext();
        o.Events.RedirectToLogin(new RedirectContext<CookieAuthenticationOptions>(login, scheme, o, new AuthenticationProperties(), "/Account/Login?ReturnUrl=%2F"));
        Assert.Equal(StatusCodes.Status401Unauthorized, login.Response.StatusCode);
        Assert.False(login.Response.Headers.ContainsKey("Location"));

        var denied = new DefaultHttpContext();
        o.Events.RedirectToAccessDenied(new RedirectContext<CookieAuthenticationOptions>(denied, scheme, o, new AuthenticationProperties(), "/Account/AccessDenied"));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
        Assert.False(denied.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public void Sso_authority_mode_stops_start_up_with_the_reason()
    {
        using var f = Factory(null, ("Sso:Enabled", "true"), ("Sso:IsAuthority", "true"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Sso:IsAuthority=true is not supported", ex.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-064: the key ring is encrypted at rest with the platform's protector.
    // ------------------------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    /// <summary>A throwaway self-signed certificate with its private key, written as a password-less PKCS#12 file.</summary>
    private static string WriteTestPfx(bool withPrivateKey = true)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GridSim key ring test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        string path = Path.Combine(NewTempDir("gridsim-doc2-cert-"), "keyring.pfx");
        if (withPrivateKey)
        {
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        }
        else
        {
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(cert.RawData);
            File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx));
        }
        return path;
    }

    [Fact]
    public void Auto_mode_picks_dpapi_on_windows_and_warns_elsewhere_without_a_certificate()
    {
        KeyRingProtection.Plan win = KeyRingProtection.Resolve(Config(), isWindows: true);
        Assert.Equal(KeyRingProtection.Protector.DpapiCurrentUser, win.Protector);
        Assert.False(win.IsWarning);

        KeyRingProtection.Plan linux = KeyRingProtection.Resolve(Config(), isWindows: false);
        Assert.Equal(KeyRingProtection.Protector.None, linux.Protector);
        Assert.True(linux.IsWarning);
        Assert.Contains("UNENCRYPTED", linux.LogLine, StringComparison.Ordinal);
        Assert.Contains(KeyRingProtection.CertificatePathSetting, linux.LogLine, StringComparison.Ordinal);

        string pfx = WriteTestPfx();
        KeyRingProtection.Plan linuxCert = KeyRingProtection.Resolve(Config((KeyRingProtection.CertificatePathSetting, pfx)), isWindows: false);
        Assert.Equal(KeyRingProtection.Protector.Certificate, linuxCert.Protector);
        Assert.NotNull(linuxCert.Certificate);
        linuxCert.Certificate!.Dispose();
    }

    [Fact]
    public void Explicit_modes_are_honoured_or_refused_naming_the_setting()
    {
        Assert.Equal(KeyRingProtection.Protector.DpapiLocalMachine,
            KeyRingProtection.Resolve(Config(("Auth:KeyProtection", "dpapi-machine")), isWindows: true).Protector);
        KeyRingProtection.Plan none = KeyRingProtection.Resolve(Config(("Auth:KeyProtection", "None")), isWindows: false);
        Assert.Equal(KeyRingProtection.Protector.None, none.Protector);
        Assert.False(none.IsWarning);

        void Refused(string expectInMessage, bool isWindows, params (string, string)[] s)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => KeyRingProtection.Resolve(Config(s), isWindows));
            Assert.Contains(expectInMessage, ex.Message, StringComparison.Ordinal);
        }
        Refused("needs Windows", false, ("Auth:KeyProtection", "dpapi"));
        Refused("needs Windows", false, ("Auth:KeyProtection", "dpapi-machine"));
        Refused("needs Auth:KeyCertificatePath", true, ("Auth:KeyProtection", "certificate"));
        Refused("use one of auto, dpapi", true, ("Auth:KeyProtection", "rot13"));
        Refused("does not exist", false, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".pfx")));
        Refused("has no private key", false, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", WriteTestPfx(withPrivateKey: false)));
    }

    /// <summary>Make the host create a key, then return the text of the key file(s) it wrote.</summary>
    private static string ForceKeyAndReadRing(WebApplicationFactory<Program> f, string keysDir)
    {
        IDataProtector p = f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("doc2-test");
        Assert.Equal("payload", p.Unprotect(p.Protect("payload")));
        return string.Concat(Directory.GetFiles(keysDir, "key-*.xml").Select(File.ReadAllText));
    }

    [Fact]
    public void Default_host_encrypts_new_keys_at_rest_on_windows()
    {
        // The hole: keys were written as plain XML (a <masterKey> element in the clear). On Windows the default is now
        // DPAPI; on other OSes the default stays plain with a start-up warning (the certificate test covers encryption).
        string keys = NewTempDir("gridsim-doc2-keys-");
        using var f = Factory(keys);
        string ring = ForceKeyAndReadRing(f, keys);
        object? encryptor = f.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("DpapiXmlEncryptor", encryptor?.GetType().Name);
            Assert.Contains("<encryptedSecret", ring, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", ring, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(encryptor);
            Assert.Contains("<masterKey", ring, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Certificate_mode_encrypts_new_keys_and_a_second_host_reads_them_back()
    {
        string keys = NewTempDir("gridsim-doc2-keys-");
        string pfx = WriteTestPfx();
        string protectedText;
        using (var f = Factory(keys, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", pfx)))
        {
            Assert.Equal("CertificateXmlEncryptor",
                f.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor?.GetType().Name);
            string ring = ForceKeyAndReadRing(f, keys);
            Assert.Contains("<encryptedSecret", ring, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", ring, StringComparison.Ordinal);
            protectedText = f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("doc2-test").Protect("across hosts");
        }
        // A fresh host on the same ring and certificate (a restart, or a second estate app) decrypts the stored key.
        using var g = Factory(keys, ("Auth:KeyProtection", "certificate"), ("Auth:KeyCertificatePath", pfx));
        Assert.Equal("across hosts", g.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("doc2-test").Unprotect(protectedText));
    }

    [Fact]
    public void An_unusable_key_protection_setting_stops_start_up()
    {
        using var f = Factory(null, ("Auth:KeyProtection", "certificate"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Auth:KeyCertificatePath", ex.ToString(), StringComparison.Ordinal);
    }
}
