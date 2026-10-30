using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace WebSite.Data.Sso;

// ---------------------------------------------------------------------------
// Cross-domain SSO login handoff for the estate. Built on the ONE thing every
// estate app already shares: the DataProtection key ring + application name
// (Auth:ApplicationName) + the IdentityConstants.ApplicationScheme cookie. Because every
// app can protect/unprotect with that ring, the login authority
// mints a short-lived, single-use, audience-bound ticket carrying the user's
// claims; any estate app on ANY registered domain verifies it and signs the
// user into its OWN host-only cookie. No OIDC server, no client secrets, and -
// crucially - NO SignInManager/UserManager, so it works in the decrypt-only
// realtime apps (such as GridSim.Web) too.
//
//   authority (login.example.com):  GET /sso/bridge  -> mints ticket
//   client    (app.example.org, ...):            GET /sso/land    -> local sign-in
//
// A client's unauthenticated hits auto-redirect to the authority's /sso/bridge.
// GridSim.Web itself is only ever a CLIENT (Program.cs refuses Sso:IsAuthority=true, D-DOC-170).
// ---------------------------------------------------------------------------
public static class EstateSso
{
    public static IServiceCollection AddEstateSso(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EstateSsoOptions>(config.GetSection(EstateSsoOptions.Section));
        // D-DOC-017: the one-time store that makes a ticket single-use on this process, with or without a distributed
        // cache (see SsoOneTimeStore). A singleton, so every request shares one record of spent tickets.
        services.AddSingleton<SsoOneTimeStore>();
        services.AddSingleton<EstateSsoTickets>();

        // A CLIENT sends unauthenticated users to the authority to get a ticket, and issues a
        // HOST-ONLY cookie (a cookie scoped to ".login.example.com" is rejected by the
        // browser on app.example.org, so sign-in would silently not stick). Post-configures the
        // shared Identity cookie scheme - every estate app uses IdentityConstants.ApplicationScheme.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<EstateSsoOptions>>((cookie, ssoOpt) =>
            {
                var sso = ssoOpt.Value;
                if (!sso.Enabled || sso.IsAuthority || string.IsNullOrWhiteSpace(sso.Authority)) return;
                var authority = sso.Authority.TrimEnd('/');

                cookie.Cookie.Domain = null;   // host-only

                cookie.Events.OnRedirectToLogin = ctx =>
                {
                    if (IsApiRequest(ctx.Request))   // don't bounce XHR into an HTML redirect loop
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    // D-DOC-171: the configured public origin when there is one, else the request's own.
                    var thisOrigin = ThisOrigin(ctx.Request, sso);
                    var to = Uri.EscapeDataString($"{thisOrigin}/sso/land");
                    var r = Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString);
                    ctx.Response.Redirect($"{authority}/sso/bridge?to={to}&r={r}");
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    /// <summary>
    /// D-DOC-171: check <c>Sso:PublicOrigin</c> at start-up. Empty is fine (the request's own origin is used); anything
    /// else must be an absolute http or https URL with no path, query or fragment, because it is compared with a
    /// ticket's audience origin exactly. Throws <see cref="InvalidOperationException"/> naming the setting.
    /// </summary>
    public static void ValidatePublicOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string v = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(v, UriKind.Absolute, out Uri? u)
            || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
            || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0)
            throw new InvalidOperationException(
                $"GridSim.Web: configuration setting Sso:PublicOrigin must be a bare origin such as https://app.example.org (scheme, host and optional port; no path); it is '{value}'.");
    }

    /// <summary>
    /// The origin this client answers as: <c>Sso:PublicOrigin</c> when configured (D-DOC-171), else the request's own
    /// scheme and host (which include what <c>UseForwardedHeaders</c> took from <c>X-Forwarded-Proto</c>).
    /// </summary>
    internal static string ThisOrigin(HttpRequest request, EstateSsoOptions o) =>
        string.IsNullOrWhiteSpace(o.PublicOrigin)
            ? $"{request.Scheme}://{request.Host}"
            : o.PublicOrigin.Trim().TrimEnd('/');

    public static IEndpointRouteBuilder MapEstateSso(this IEndpointRouteBuilder app)
    {
        // AUTHORITY - mint a ticket carrying the signed-in user's claims, bound to the target app.
        app.MapGet("/sso/bridge", (HttpContext ctx, IOptions<EstateSsoOptions> opt, EstateSsoTickets tickets) =>
        {
            var o = opt.Value;
            if (!o.Enabled || !o.IsAuthority) return Results.NotFound();

            if (ctx.User?.Identity?.IsAuthenticated != true)
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = ctx.Request.Path + ctx.Request.QueryString },
                    new[] { IdentityConstants.ApplicationScheme });

            var to = ctx.Request.Query["to"].ToString();
            var r = ctx.Request.Query["r"].ToString();
            if (!TryValidateLanding(to, o, out var origin)) return Results.BadRequest("invalid target");

            var ticket = tickets.Issue(ctx.User, origin);
            var sep = to.Contains('?') ? '&' : '?';
            return Results.Redirect($"{to}{sep}t={Uri.EscapeDataString(ticket)}&r={Uri.EscapeDataString(r)}");
        }).AllowAnonymous();   // does its own auth check; must not be caught by a global FallbackPolicy

        // CLIENT - verify the ticket and establish THIS app's own session cookie on THIS domain,
        // WITHOUT SignInManager (so decrypt-only apps work): rebuild the principal from the ticket
        // claims and sign into the shared cookie scheme directly.
        app.MapGet("/sso/land", async (HttpContext ctx, IOptions<EstateSsoOptions> opt, EstateSsoTickets tickets) =>
        {
            var o = opt.Value;
            if (!o.Enabled) return Results.NotFound();

            var t = ctx.Request.Query["t"].ToString();
            var r = ctx.Request.Query["r"].ToString();
            var thisOrigin = ThisOrigin(ctx.Request, o);

            SsoTicketCheck check = await tickets.CheckAsync(t, thisOrigin);
            if (check.Claims is null)
            {
                // D-DOC-171: the visitor still lands on the home page (anonymous, every page usable), but the reason is
                // now in the log. Before, every refusal (expired, tampered, replayed, or an audience that differed only
                // in http vs https behind a proxy) was the same silent redirect, so a broken proxy set-up looked like
                // "sign-in does nothing" with no trace. The ticket itself is never logged.
                EstateSsoTickets.LogRefused(tickets.Logger, check.Refusal ?? "", null);
                return Results.LocalRedirect("/");
            }

            var identity = new ClaimsIdentity(check.Claims, IdentityConstants.ApplicationScheme, ClaimTypes.Name, ClaimTypes.Role);
            await ctx.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.LocalRedirect(SafeLocal(r));
        }).AllowAnonymous();   // the visitor is not yet authenticated ON THIS domain - must be reachable

        // Local logout for this app (clears this domain's session). GR-710: like /sso/bridge and /sso/land it answers 404
        // when sign-on is off, so a page elsewhere cannot use it on an instance that has no sign-on at all.
        app.MapGet("/sso/logout", async (HttpContext ctx, IOptions<EstateSsoOptions> opt) =>
        {
            if (!opt.Value.Enabled) return Results.NotFound();
            await ctx.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.LocalRedirect("/");
        }).AllowAnonymous();

        return app;
    }

    private static bool TryValidateLanding(string to, EstateSsoOptions o, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(to, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (!u.AbsolutePath.Equals("/sso/land", StringComparison.OrdinalIgnoreCase)) return false;
        origin = $"{u.Scheme}://{u.Authority}";
        var originLocal = origin;
        // N227 (October 2026): compare origins, not strings: an allowlist entry "https://app.example.org:443" names the
        // same origin as "https://app.example.org" (Uri.Authority leaves the default port out), and used to be refused.
        return o.AllowedClients.Any(c => SameOrigin(c, originLocal));
    }

    /// <summary>
    /// N227 (October 2026): an origin in its canonical form, <c>scheme://host[:port]</c> with the scheme and host in lower
    /// case and the scheme's default port left out (443 for https, 80 for http), so <c>https://Host:443/</c> and
    /// <c>https://host</c> are the same origin (RFC 6454: an origin is the scheme, host and port). A value that is not an
    /// absolute http or https URL is returned trimmed of a trailing '/', unchanged otherwise, so it still compares only
    /// with itself (case-insensitively, as before).
    /// </summary>
    internal static string CanonicalOrigin(string value)
    {
        string v = (value ?? "").Trim().TrimEnd('/');
        if (Uri.TryCreate(v, UriKind.Absolute, out Uri? u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            return u.IsDefaultPort ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}" : $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{u.Port}";
        return v;
    }

    /// <summary>N227: true when the two values name the same origin (<see cref="CanonicalOrigin"/>, compared
    /// case-insensitively). An exact match before is still a match; the only new matches are spellings of the same
    /// origin that differ by an explicit default port, letter case of the host, or a trailing '/'.</summary>
    internal static bool SameOrigin(string a, string b) =>
        string.Equals(CanonicalOrigin(a), CanonicalOrigin(b), StringComparison.OrdinalIgnoreCase);

    private static string SafeLocal(string r)
        => (!string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//", StringComparison.Ordinal)) ? r : "/";

    private static bool IsApiRequest(HttpRequest req)
    {
        if (req.Headers.XRequestedWith == "XMLHttpRequest") return true;
        var accept = req.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
               && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The outcome of checking a ticket: the carried claims when it is accepted, else null and the reason
/// (plain English, never containing the ticket or any claim value, so it is safe to log).</summary>
public sealed record SsoTicketCheck(List<Claim>? Claims, string? Refusal)
{
    internal static SsoTicketCheck Refused(string reason) => new(null, reason);
}

/// <summary>
/// D-DOC-017: the in-process record of spent SSO tickets, so a ticket is accepted ONCE.
/// <para>
/// Before D-DOC-017 single use relied entirely on an <see cref="IDistributedCache"/>, and GridSim.Web registers none, so
/// the check was skipped and a ticket (in a browser history, a proxy log or a Referer header) could be replayed to sign
/// in again and again until it expired (confirmed by signing in twice with one ticket). This store needs nothing
/// configured: each ticket's nonce (<c>jti</c>) is added atomically on first use and kept until the ticket could no
/// longer be valid anyway (its lifetime plus a margin for clock skew), so a second use within that window finds it and
/// is refused, and the store never grows beyond the tickets of the last few minutes.
/// </para>
/// <para>
/// It is per process. A deployment that runs several instances behind one name must also register a shared
/// <see cref="IDistributedCache"/>; <see cref="EstateSsoTickets"/> then checks both, so a ticket spent on one instance
/// is refused on the others.
/// </para>
/// </summary>
public sealed class SsoOneTimeStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _spent = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _lastPruneTicks;

    /// <summary>How often expired entries are swept out (a sweep walks the whole store, so not on every call).</summary>
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(30);

    public SsoOneTimeStore() : this(TimeProvider.System) { }

    /// <summary>Test seam: a store on a controllable clock.</summary>
    internal SsoOneTimeStore(TimeProvider time)
    {
        _time = time;
        _lastPruneTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>Entries held now (spent tickets not yet swept).</summary>
    public int Count => _spent.Count;

    /// <summary>
    /// Spend <paramref name="nonce"/>: true the first time (and the nonce is then held until
    /// <paramref name="keepUntil"/>, which the caller sets past the ticket's own expiry), false when it was already spent
    /// and is still held. Atomic: two requests racing with one ticket cannot both win.
    /// </summary>
    public bool TryConsume(string nonce, DateTimeOffset keepUntil)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        DateTimeOffset now = _time.GetUtcNow();
        PruneIfDue(now);
        if (_spent.TryAdd(nonce, keepUntil)) return true;
        // Held: still a replay while its keep-until lies ahead. An entry past its time (not swept yet) is a ticket that
        // has expired by now anyway, so the protector refused it before this point; refuse here too, never re-admit.
        return false;
    }

    private void PruneIfDue(DateTimeOffset now)
    {
        long last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last < PruneEvery.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last) return;   // another thread sweeps
        foreach (KeyValuePair<string, DateTimeOffset> kv in _spent)
            if (kv.Value <= now) _spent.TryRemove(kv.Key, out _);
    }
}

/// <summary>
/// Mints and verifies the cross-domain tickets. A ticket is a DataProtection time-limited,
/// authenticated JSON payload (audience + jti + the user's claims incl. the Identity security
/// stamp) - so it can't be forged or used past its lifetime, and only an app holding the
/// shared key ring can read it. Single use (D-DOC-017) is enforced by the in-process
/// <see cref="SsoOneTimeStore"/> always, and also against a shared <see cref="IDistributedCache"/> when the host
/// registers one (several instances behind one name).
/// </summary>
public sealed class EstateSsoTickets
{
    /// <summary>The data-protection purpose of a ticket. Changing it invalidates every ticket in flight.</summary>
    internal const string Purpose = "EstateSso.Ticket.v2";

    /// <summary>How long past a ticket's own lifetime its nonce is kept as spent, to cover clock skew between the
    /// authority that minted it and this client.</summary>
    internal static readonly TimeSpan SpentMargin = TimeSpan.FromSeconds(30);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IDistributedCache? _cache;
    private readonly SsoOneTimeStore _spent;
    private readonly EstateSsoOptions _o;

    /// <summary>Where refusals and cache faults are logged (used by the /sso/land endpoint too).</summary>
    internal ILogger Logger { get; }

    /// <summary>D-DOC-171: the log line for a refused sign-in (the reason only; never the ticket or a claim value).</summary>
    internal static readonly Action<ILogger, string, Exception?> LogRefused = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1701, "SsoTicketRefused"), "SSO sign-in refused at /sso/land: {Reason}");

    /// <summary>D-DOC-017: the log line for a shared-cache fault (single use then rests on this instance's store).</summary>
    private static readonly Action<ILogger, Exception?> LogCacheFaultMessage = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1702, "SsoTicketCacheFault"),
        "SSO shared ticket cache unavailable; single use is enforced on this instance only.");

    private static void LogCacheFault(ILogger logger, Exception ex) => LogCacheFaultMessage(logger, ex);

    // Claim types worth carrying across the handoff: identity, name, email, roles, the Identity
    // security stamp (so full-Identity clients pass SecurityStampValidator), plus the estate's
    // DisplayName. Everything else (transient/analytics claims) is dropped to keep the ticket small.
    private static readonly HashSet<string> Carry = new(StringComparer.Ordinal)
    {
        ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Email, ClaimTypes.Role,
        "AspNet.Identity.SecurityStamp", "DisplayName", "amr", "preferred_username",
    };

    public EstateSsoTickets(IDataProtectionProvider dp, IOptions<EstateSsoOptions> o, IServiceProvider sp)
    {
        _protector = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _o = o.Value;
        _cache = sp.GetService<IDistributedCache>();   // optional: only a multi-instance deployment needs one
        _spent = sp.GetService<SsoOneTimeStore>() ?? new SsoOneTimeStore();
        Logger = (ILogger?)sp.GetService<ILogger<EstateSsoTickets>>() ?? NullLogger.Instance;
    }

    public string Issue(ClaimsPrincipal user, string audienceOrigin)
    {
        var claims = user.Claims
            .Where(c => Carry.Contains(c.Type))
            .Select(c => new[] { c.Type, c.Value })
            .ToList();
        var payload = JsonSerializer.Serialize(new TicketDto
        {
            a = audienceOrigin.TrimEnd('/'),
            j = Guid.NewGuid().ToString("N"),
            c = claims,
        });
        return _protector.Protect(payload, TimeSpan.FromSeconds(_o.TicketSeconds));
    }

    /// <returns>the carried claims iff the ticket is valid, unexpired, unused, and bound to
    /// <paramref name="thisOrigin"/>; otherwise null. Kept for callers that need no reason; see <see cref="CheckAsync"/>.</returns>
    public async Task<List<Claim>?> ValidateAsync(string ticket, string thisOrigin) =>
        (await CheckAsync(ticket, thisOrigin)).Claims;

    /// <summary>
    /// Check a ticket and SPEND it: accepted at most once. The order matters: the protector first (forged, tampered and
    /// expired tickets never reach the store), then the audience (a ticket for another app is refused without being
    /// spent, so it cannot be used to burn a real visitor's ticket), then the single-use spend.
    /// </summary>
    public async Task<SsoTicketCheck> CheckAsync(string ticket, string thisOrigin)
    {
        if (string.IsNullOrEmpty(ticket)) return SsoTicketCheck.Refused("no ticket in the request (parameter t is missing or empty).");
        TicketDto? dto;
        DateTimeOffset expires;
        try { dto = JsonSerializer.Deserialize<TicketDto>(_protector.Unprotect(ticket, out expires)); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return SsoTicketCheck.Refused(
                "the ticket is expired, tampered with, or was minted with a different key ring or application name (Auth:KeysDirectory, Auth:ApplicationName).");
        }
        if (dto is null || dto.c is null) return SsoTicketCheck.Refused("the ticket carries no claims.");

        string expected = thisOrigin.TrimEnd('/');
        // N227 (October 2026): the audience is compared as an origin (EstateSso.SameOrigin), so "https://host:443" (a
        // Host header or Sso:PublicOrigin carrying the default port) matches a ticket for "https://host". It used to be
        // an exact string match and refused the same origin spelt two ways. A different scheme, host or port still fails.
        if (!EstateSso.SameOrigin(dto.a, expected))
            return SsoTicketCheck.Refused(AudienceRefusal(dto.a, expected));

        // D-DOC-017: a ticket without a nonce cannot be made single-use, so it is refused rather than accepted forever.
        if (string.IsNullOrEmpty(dto.j)) return SsoTicketCheck.Refused("the ticket carries no nonce (jti), so it cannot be made single-use.");

        // Held until the ticket's OWN expiry (as minted by the authority, whatever lifetime it used) plus a margin for
        // clock skew: by then the protector refuses the ticket anyway, so the entry can go.
        DateTimeOffset keepUntil = expires + SpentMargin;
        if (!_spent.TryConsume(dto.j, keepUntil))
            return SsoTicketCheck.Refused("the ticket was already used (each ticket signs in once).");

        if (_cache is not null)
        {
            var key = $"sso:jti:{dto.j}";
            try
            {
                if (await _cache.GetAsync(key) is not null)
                    return SsoTicketCheck.Refused("the ticket was already used on another instance (shared cache).");
                await _cache.SetAsync(key, new byte[] { 1 },
                    new DistributedCacheEntryOptions { AbsoluteExpiration = keepUntil });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A shared-cache fault no longer leaves the ticket replayable: the in-process spend above already holds
                // it on this instance. Logged so an operator sees the shared check is down.
                LogCacheFault(Logger, ex);
            }
        }

        return new SsoTicketCheck(dto.c.Where(p => p.Length == 2).Select(p => new Claim(p[0], p[1])).ToList(), null);
    }

    /// <summary>D-DOC-171: say WHY the audience differs. The common case behind a TLS proxy is the same host with http
    /// where the ticket says https: the proxy is not forwarding X-Forwarded-Proto. Origins are not secrets (they are
    /// the public addresses of the estate's apps), so naming them makes the log line actionable.</summary>
    private static string AudienceRefusal(string ticketAudience, string thisOrigin)
    {
        bool sameHostOtherScheme =
            Uri.TryCreate(ticketAudience, UriKind.Absolute, out Uri? a) && Uri.TryCreate(thisOrigin, UriKind.Absolute, out Uri? b)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Scheme != b.Scheme;
        string reason = $"the ticket is for '{ticketAudience}' but this app answers as '{thisOrigin}'.";
        if (sameHostOtherScheme)
            reason += " Only the scheme differs: the reverse proxy is probably not sending X-Forwarded-Proto. Forward it, or set Sso:PublicOrigin to this app's public origin.";
        else
            reason += " Check Sso:PublicOrigin (or the Host header the proxy forwards) and the authority's AllowedClients list.";
        return reason;
    }

    private sealed class TicketDto
    {
        public string a { get; set; } = "";          // audience origin
        public string j { get; set; } = "";          // jti (single-use nonce)
        public List<string[]>? c { get; set; }        // claims: [[type,value],...]
    }
}
