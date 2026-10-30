using System.Globalization;
using GridSim.Core.Licensing;
using GridSim.Web.Api;
using GridSim.Web.Components;
using GridSim.Web.Model;
using GridSim.Web.Realtime;
using GridSim.Web.Sessions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

// GridSim.Web - Blazor Server dashboard + REST/WebSocket API over GridSim.Core.
// Pin InvariantCulture up front for the same reason the CLI does: every number we parse or emit
// (JSON, query strings) must be locale-independent so a comma-decimal host can't mis-read "0.5".
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Public replay-only instance (GridSim:ReplayOnly=true): offer ONLY the raw public-data replay. The solve REST
// API + the sim WebSocket are not mapped, the realtime estimator stays off, and the Blazor UI hides every
// solver/physics surface (Home redirects straight to the raw replay; Dashboard shows only the raw-safe tabs).
bool replayOnly = builder.Configuration.GetValue<bool>("GridSim:ReplayOnly");

// Software licence, resolved once, offline. Precedence: the Licence:Key config value (set as the environment
// variable Licence__Key: ASP.NET Core maps a double underscore to the ':' separator, D-DOC-198), then the
// GRIDSIM_LICENCE environment variable, then %LOCALAPPDATA%\GridSim\licence.gridsim, then the free Personal tier.
// Registered as a singleton so components (footer) and the catalog can read it.
LicenceGate licence = LicenceGate.Resolve(builder.Configuration["Licence:Key"]);
builder.Services.AddSingleton(licence);

// One catalog for the process (filesystem discovery runs once); the session factory is stateless.
var catalog = new CaseCatalog(AppContext.BaseDirectory, licence);
// A gda-out/ or results/ folder that could not be read is skipped, not fatal - but say which and why.
foreach (string skipped in catalog.SkippedFolders)
    Console.Error.WriteLine($"GridSim.Web: model scan skipped an unreadable folder: {skipped}");
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<SessionManager>();
// Anonymised presence (who's online + the replay time they're viewing) and server CPU/RAM, shared by every
// Blazor circuit and shown in the LIVE tab.
builder.Services.AddSingleton<PresenceService>();

// Non-finite doubles (inf Q-limits, +inf purchased time) must serialize as null on EVERY
// HTTP JSON path, not just /ws/sim - /api/solve 500'd on any model carrying them (GS-H7).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new NonFiniteDoubleConverter()));

// N086: the deployment's own identity (analytics, share-card metadata, security contact, footer links) comes from the
// Site:* settings, read and checked ONCE here so a malformed value stops start-up with a message naming the setting.
// Nothing configured = a neutral page: no third-party script, no host-specific metadata, security.txt answers 404.
var site = GridSim.Web.Site.SiteSettings.FromConfiguration(builder.Configuration, DateTimeOffset.UtcNow);
builder.Services.AddSingleton(site);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---- Shared estate single-sign-on: lightweight CONSUMER (recognise only) ------------------------
// This is a public raw-replay demo with no user store of its own. It joins the estate SSO purely to
// RECOGNISE a visitor who is already signed in on the parent domain - nothing is forced, no login/
// register UI is added, no Identity DB is touched. The estate auth cookie is a self-contained,
// DataProtection-encrypted cookie: to DECRYPT it we only need (1) the SAME key ring, (2) the SAME
// application name and (3) a cookie authentication scheme bound to the SAME cookie name/domain.
// All of this is ADDITIVE - every page stays fully accessible anonymously.
//
// KeysDirectory MUST point at the shared estate key ring on the host (the same folder the estate's login site
// persists) for decryption to succeed; if it is unset we fall back to a local dir, which simply means
// the estate cookie won't decrypt and every visitor is treated as anonymous - the app still runs.
// N087: the application name is a deployment setting. Unset, it is the neutral product name (SiteSettings.
// DefaultApplicationName, "GridSim"); before N087 the code fell back to one specific estate's name, so every
// deployment silently shared that estate's cookie-protection purpose. An estate deployment sets Auth__ApplicationName.
var authAppName = builder.Configuration["Auth:ApplicationName"];
if (string.IsNullOrWhiteSpace(authAppName)) authAppName = GridSim.Web.Site.SiteSettings.DefaultApplicationName;
var authKeysDir = builder.Configuration["Auth:KeysDirectory"];
if (string.IsNullOrWhiteSpace(authKeysDir))
    authKeysDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(authKeysDir);
// D-DOC-064: the key ring is encrypted at rest with the platform's protector (DPAPI on Windows, a certificate where
// Auth:KeyCertificatePath is set), chosen by Auth:KeyProtection and checked here, before anything else is built, so a
// setting that cannot be honoured stops start-up naming itself. See Site/KeyRingProtection.cs for every mode.
GridSim.Web.Site.KeyRingProtection.Plan keyPlan =
    GridSim.Web.Site.KeyRingProtection.Resolve(builder.Configuration, OperatingSystem.IsWindows(), WebSecretStore(builder.Configuration));
Console.Error.WriteLine(keyPlan.LogLine);
IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(authKeysDir))
    .SetApplicationName(authAppName);
GridSim.Web.Site.KeyRingProtection.Apply(dataProtection, keyPlan);

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        var cookieName = builder.Configuration["Auth:CookieName"];
        options.Cookie.Name = string.IsNullOrWhiteSpace(cookieName)
            ? ".AspNetCore.Identity.Application"
            : cookieName;

        var cookieDomain = builder.Configuration["Auth:CookieDomain"];
        if (!string.IsNullOrWhiteSpace(cookieDomain)) options.Cookie.Domain = cookieDomain;

        options.Cookie.SameSite = SameSiteMode.Lax;
        // GR-733 (October 2026): with sign-on on, the sign-in cookie is ALWAYS Secure. It used to follow the request
        // scheme (SameAsRequest), so behind a proxy that does not send X-Forwarded-Proto (or one GridSim:TrustedProxies
        // does not trust) the estate sign-in cookie went out without Secure and could be sent over plain http. Sign-on
        // is documented as HTTPS-only, so nothing valid changes; with sign-on off this app issues no sign-in cookie of
        // its own and the policy stays as it was.
        options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Sso:Enabled")
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        // Demo app: never bounce anyone to a login form - an unauthenticated visitor is simply anonymous.
        // D-DOC-170: this app serves no login or access-denied page, so a challenge must not redirect to the cookie
        // handler's defaults (/Account/Login, /Account/AccessDenied), which answered "Nothing at this address". A
        // challenge here answers 401 and a forbid 403, with no redirect. An SSO client (Sso:Enabled with an Authority)
        // replaces the login answer with the redirect to the authority's /sso/bridge (EstateSso.AddEstateSso, which
        // configures these options after this block).
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

// D-DOC-170: GridSim is never the SSO AUTHORITY. The authority's /sso/bridge challenges a visitor who is not signed in,
// and this app has no login page to send them to, so Sso:IsAuthority=true could only ever produce a dead end. Refused at
// start-up with the reason, rather than discovered by the first visitor. (The shared EstateSso code keeps its authority
// branch for the estate's own login site.)
if (builder.Configuration.GetValue<bool>("Sso:Enabled") && builder.Configuration.GetValue<bool>("Sso:IsAuthority"))
    throw new InvalidOperationException(
        "GridSim.Web: configuration setting Sso:IsAuthority=true is not supported: GridSim has no login page, so it cannot mint SSO tickets. Point Sso:Authority at the estate's login site instead.");

// D-DOC-171: Sso:PublicOrigin, when set, must be a bare http(s) origin; checked here so a typo stops start-up.
WebSite.Data.Sso.EstateSso.ValidatePublicOrigin(builder.Configuration["Sso:PublicOrigin"]);

// ---- Cross-domain SSO (client): federate login to the estate authority; host-only session. ----
WebSite.Data.Sso.EstateSso.AddEstateSso(builder.Services, builder.Configuration);

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// The resident real-time estimator runs only when explicitly enabled (Realtime:Enabled=true) with a case
// and a measurement-window directory - it streams replayed/permitted-age history through the guarded engine.
// Default off keeps the app's behaviour unchanged.
// D-DOC-060 (October 2026): the web host's audit log. Audit:LogPath (environment variable Audit__LogPath) names the
// tamper-evident log (gridsim-audit/1) the resident estimator records its run in, as `gridsim serve --audit-log` does;
// Audit:AnchorPath (Audit__AnchorPath) optionally keeps its anchor elsewhere (default <log>.anchor, D-DOC-062). The
// resident estimator is the only part of the host that produces evidence (estimates from recorded windows); the
// dashboard's live sessions and POST /api/solve are model solves, which the CLI does not audit either. A log path on
// a host that runs no resident estimator is refused at start-up, naming the settings, rather than ignored: nobody may
// believe a run was logged when it was not (the CLI refuses --audit-log on an unaudited verb for the same reason).
string? auditLogPath = builder.Configuration["Audit:LogPath"];
string? auditAnchorPath = builder.Configuration["Audit:AnchorPath"];
// NCSC CAF C1 wiring, as the CLI's flags (each only with Audit:LogPath; a value without the log stops start-up):
//   Audit:ForwardPath                 every entry also appended as a JSON line to this file (SIEM input / WORM copy)
//   Audit:IncidentLogPath             security events raised as incidents (single significant events and bursts)
//   Audit:AnchorSigningKeyPath        PEM ECDSA P-256 PRIVATE key: the anchor is signed after every entry
//   Audit:AnchorVerificationKeyPath   PEM ECDSA P-256 PUBLIC key: the anchor signature must verify at start-up
string?[] auditExtras =
{
    builder.Configuration["Audit:ForwardPath"], builder.Configuration["Audit:IncidentLogPath"],
    builder.Configuration["Audit:AnchorSigningKeyPath"], builder.Configuration["Audit:AnchorVerificationKeyPath"],
};
if (auditExtras.Any(v => !string.IsNullOrWhiteSpace(v)) && string.IsNullOrWhiteSpace(auditLogPath))
    throw new InvalidOperationException(
        "GridSim.Web: an Audit:ForwardPath, Audit:IncidentLogPath, Audit:AnchorSigningKeyPath or Audit:AnchorVerificationKeyPath "
        + "setting is set but Audit:LogPath is not; they all act on the audit log. Set Audit:LogPath too, or remove them.");
bool realtimeEnabled = !replayOnly && builder.Configuration.GetValue<bool>("Realtime:Enabled");
if (!string.IsNullOrWhiteSpace(auditAnchorPath) && string.IsNullOrWhiteSpace(auditLogPath))
    throw new InvalidOperationException(
        "GridSim.Web: configuration setting Audit:AnchorPath is set but Audit:LogPath is not; the anchor belongs to a log. "
        + "Set Audit:LogPath too, or remove Audit:AnchorPath.");
if (!string.IsNullOrWhiteSpace(auditLogPath) && !realtimeEnabled)
    throw new InvalidOperationException(
        "GridSim.Web: configuration setting Audit:LogPath is set, but this host runs no resident estimator "
        + (replayOnly ? "(GridSim:ReplayOnly=true)" : "(Realtime:Enabled is not true)")
        + ", so nothing would be recorded in it. Enable the resident estimator, or remove Audit:LogPath.");

if (realtimeEnabled)
{
    string caseName = builder.Configuration["Realtime:Case"] ?? "case9";
    string windowsDir = builder.Configuration["Realtime:WindowsDir"] ?? "";
    // D-DOC-153: load the estimator's case HERE, once, and turn a refusal into a start-up message that names the
    // setting. Before, the load ran inside the feed's DI factory when the hosted service started, so a case the licence
    // does not cover surfaced as a raw LicenceException (and an unknown name as a KeyNotFoundException) from deep in
    // host start-up, with nothing saying which setting was wrong. Still fail loud: the host does not start.
    GridSim.Core.Model.GridModel realtimeModel = LoadRealtimeCase(catalog, caseName);
    // GR-242: the estimator reads its windows from this folder. A blank or misspelt setting used to give a run of 0
    // windows that "succeeded" (/api/realtime and /health reported from it, and the audit left the input out); it
    // stops start-up instead, naming the setting. (Checked after the case, so a bad case is still named first.)
    if (string.IsNullOrWhiteSpace(windowsDir) || !Directory.Exists(windowsDir))
        throw new InvalidOperationException(
            "GridSim.Web: configuration setting Realtime:WindowsDir " +
            (string.IsNullOrWhiteSpace(windowsDir) ? "is not set" : $"names '{windowsDir}', which is not a folder") +
            "; the resident estimator reads its measurement windows from it. Point it at the windows folder, or set Realtime:Enabled=false.");
    // D-DOC-060: open (and verify, against its anchor) the audit log NOW, before the host serves anything, so a tampered,
    // truncated, locked or misplaced log stops start-up with a message naming the setting.
    GridSim.Core.Diagnostics.AuditRunRecorder? realtimeAudit = string.IsNullOrWhiteSpace(auditLogPath)
        ? null
        : StartRealtimeAudit(auditLogPath, auditAnchorPath, caseName, windowsDir, builder.Configuration);
    builder.Services.AddSingleton<IRealtimeWindowFeed>(_ => new ReplayDirectoryWindowFeed(realtimeModel, windowsDir));
    // Registered as a singleton too, so GET /api/realtime can read its output (R100).
    builder.Services.AddSingleton(sp => new RealtimeEstimatorHostedService(
        sp.GetRequiredService<IRealtimeWindowFeed>(), options: null, audit: realtimeAudit));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeEstimatorHostedService>());
}

// D-DOC-225: /api/topology caches a replay run's topology instead of reloading the whole run on every call.
builder.Services.AddSingleton<ReplayTopologyCache>();

WebApplication app = builder.Build();

// D-DOC-076: build the session manager now, so a malformed GridSim:MaxSessions stops start-up (naming the setting)
// rather than failing the first visitor's request.
_ = app.Services.GetRequiredService<SessionManager>();

// GR-720 (October 2026): the exception handler is the first middleware, so an exception thrown in any later one (the
// security.txt answer, the replay-only gate, the forwarded-header handling) gets the generic /Error page rather than
// the bare server default. It used to be registered after those three. ASP.NET's own guidance puts error handling
// ahead of the forwarded-headers middleware. Development still shows the developer exception page (the default
// `dotnet run` profile); run production under the Production environment.
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

// --- security.txt (RFC 9116); served first so auth/setup gates cannot intercept it ---
// N086: the file is a deployment setting (Site:SecurityTxt:Contact / Expires / PreferredLanguages / Comment). With no
// contact configured the path answers 404 here, explicitly, so no later middleware or page route can claim it.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/.well-known/security.txt")
    {
        if (site.SecurityTxt is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(ctx.Response, site.SecurityTxt);
        return;
    }
    await next();
});

// D-DOC-016 (SECURITY): on a replay-only instance the dashboard route serves ONLY the raw public-data replay. Before,
// replay-only redirected just "/" and left /dashboard/<any case> open, so GET /dashboard/case9 started a LIVE solver
// session (with the AUDIT LOG tab) on a public host. This gate answers 404 for every other /dashboard/<id> before any
// page or session code runs (the same answer the unmapped /api and /ws/sim give). It is the first of two locks:
// SessionManager.Create refuses the same ids too, which also covers a navigation inside an already-open circuit that
// never makes an HTTP request.
if (replayOnly)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/dashboard", StringComparison.OrdinalIgnoreCase, out PathString rest))
        {
            // The route's one segment, as the router will see it (PathString is already URL-decoded once).
            string id = (rest.Value ?? "").TrimStart('/');
            // N346: against the replay cases this instance was configured for (GridSim:ReplayCases, default gb-full).
            if (!ctx.RequestServices.GetRequiredService<SessionManager>().IsAllowedHere(id))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(ctx.Response,
                    "Not available: this is a replay-only instance (GridSim:ReplayOnly=true), which offers only the raw public-data replay.");
                return;
            }
        }
        await next();
    });
}

// Behind nginx (loopback) terminating TLS - honour X-Forwarded-Proto/For so the request is seen as
// https (the SSO cookie itself is always Secure since GR-733). ADDITIVE: harmless for direct/local requests too.
// N226 (October 2026): only from the proxies GridSim:TrustedProxies names (default loopback; Docker Compose sets
// "loopback,private" because the host proxy reaches the container from the bridge gateway). Before N226 both known
// lists were cleared, which believes the headers from ANY caller. A malformed value stops start-up naming the setting.
var trustedProxies = GridSim.Web.Site.TrustedProxies.FromConfiguration(app.Configuration);
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
trustedProxies.ApplyTo(fwd);
Console.Error.WriteLine($"GridSim.Web: forwarded headers trusted from {trustedProxies.Description}");
app.UseForwardedHeaders(fwd);

app.UseStaticFiles();

// Recognise the shared estate login cookie (decrypt-only; no forced auth). Placed after static files
// and before antiforgery/endpoints so User is populated for every component that reads it.
app.UseAuthentication();
app.UseAuthorization();

// Serve the git-ignored geography snapshot (DNO/GSP/asset GeoJSON) under /geo when present, so the
// network map's admin-boundary overlays light up. Optional & graceful: if the folder is absent the
// map-geo.js loaders just get 404s and return [] (overlays unavailable), exactly like the WPF app.
string geoDir = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "gda-out", "geo"));
if (Directory.Exists(geoDir))
{
    // GR-708 (October 2026): only GeoJSON is served. ServeUnknownFileTypes used to publish EVERY file of the folder (a
    // stray CSV, a log, an editor backup) as application/geo+json to anonymous visitors. The content-type map now holds
    // just .geojson and .json, and any other extension is a 404, so dropping a file into the folder no longer publishes it.
    // The folder is resolved once, from the content root, and named on stderr so an operator sees what is served.
    var geoTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    geoTypes.Mappings.Clear();
    geoTypes.Mappings[".geojson"] = "application/geo+json";
    geoTypes.Mappings[".json"] = "application/json";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(geoDir),
        RequestPath = "/geo",
        ContentTypeProvider = geoTypes,
        ServeUnknownFileTypes = false,
    });
    Console.Error.WriteLine($"GridSim.Web: /geo serves the .geojson and .json files of {geoDir}");
}

app.UseAntiforgery();

// The solve REST API (incl. POST /api/solve) and the raw sim WebSocket are external-consumer surfaces that drive
// the in-process solver - not offered on a replay-only instance. The Blazor UI drives its own SimSession, so the
// dashboard is unaffected. (Blazor's own SignalR circuit does not need app.UseWebSockets().)
if (!replayOnly)
{
    app.UseWebSockets();
    app.MapGroup("/api").MapGridSimApi();
    // Streaming endpoint for external consumers (the Blazor UI drives its own SimSession in-process).
    app.Map("/ws/sim", (HttpContext ctx, SessionManager manager) => SimWebSocket.HandleAsync(ctx, manager));
}

// CAF A4.b: a running instance states its build identity, SBOM hash and recorded control set. Anonymous, read-only,
// always mapped (even replay-only), so a reviewer can compare a production instance with the published release. Only a
// release build can read compliant; the controls not recorded Achieved are listed.
app.MapGet("/compliance", () =>
{
    GridSim.Core.Diagnostics.SupplyChain.ComplianceAttestation a =
        GridSim.Core.Diagnostics.SupplyChain.ComplianceAttestation.Current;
    return Results.Json(new
    {
        version = a.Version,
        commit = a.Commit,
        buildHash = a.BuildHash,
        sbomSha256 = a.SbomSha256,
        manifestHash = a.ManifestHash,
        releaseBuild = a.IsReleaseBuild,
        inScopeControls = a.InScopeControlCount,
        achievedControls = a.AchievedControlCount,
        compliant = a.IsCompliant,
        notAchieved = GridSim.Core.Diagnostics.SupplyChain.ComplianceManifest.InScope
            .Where(c => c.Status != GridSim.Core.Diagnostics.SupplyChain.ComplianceStatus.Achieved)
            .Select(c => new { id = c.Id, principle = c.Principle, status = c.Status.ToString() })
            .ToArray(),
    });
});

// CAF B5: a liveness and health probe. Always mapped. When the resident estimator is enabled, its own run counters are
// folded against the default service-level objective; a breach answers 503 so an orchestrator can act on it.
app.MapGet("/health", (IServiceProvider sp) =>
{
    var svc = sp.GetService(typeof(GridSim.Web.Realtime.RealtimeEstimatorHostedService))
        as GridSim.Web.Realtime.RealtimeEstimatorHostedService;
    // No resident estimator configured: a plain liveness answer.
    if (svc is null)
        return Results.Ok(new { status = "ok", version = GridSim.Core.BuildInfo.Version });
    // GR-709 (October 2026): the estimator is configured but has not produced its first summary yet. This used to
    // answer "ok", which read as "checked and fine" although nothing had been checked. It now says "starting" (still
    // 200, so a liveness probe does not restart a host that is merely warming up) until the first summary arrives.
    GridSim.Core.Simulation.RealtimeRunSummary? summary = svc.LastSummary;
    if (summary is null)
        return Results.Ok(new { status = "starting", version = GridSim.Core.BuildInfo.Version });
    GridSim.Core.Simulation.Health.HealthReport report = new GridSim.Core.Simulation.Health.HealthProbe().Check(summary);
    return Results.Json(new
    {
        status = report.Healthy ? "healthy" : "degraded",
        version = GridSim.Core.BuildInfo.Version,
        convergedFraction = report.ConvergedFraction,
        rejectedFreshFraction = report.RejectedFreshFraction,
        breaches = report.BreachedObjectives,
    }, statusCode: report.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

WebSite.Data.Sso.EstateSso.MapEstateSso(app);

app.Run();

// D-DOC-153: the resident estimator's case, loaded once at start-up. A case the licence does not cover, or a name the
// catalog does not know, stops start-up with one message naming Realtime:Case and the way out.
// D-DOC-060: open the resident estimator's audit log (action web-realtime, provenance DerivedFromRecorded: estimates
// from recorded windows) and add its inputs: the case (its id, or its files' hash) and the window directory (a hash of
// every file's name and bytes). Any refusal stops start-up, naming the setting.
// CAF B3 secrets: Secrets:Directory (Secrets__Directory) names a folder of one-file-per-secret (Docker / Compose secrets
// at /run/secrets, a Kubernetes secret volume, systemd $CREDENTIALS_DIRECTORY). Secrets are looked up there first and in
// the environment second, so a key-ring certificate password need never be in configuration or the environment. A
// directory that does not exist stops start-up naming the setting.
static GridSim.Core.Security.ISecretStore WebSecretStore(IConfiguration config)
{
    string? dir = config["Secrets:Directory"];
    if (string.IsNullOrWhiteSpace(dir)) return GridSim.Core.Security.EnvironmentSecretStore.Instance;
    try
    {
        return new GridSim.Core.Security.CompositeSecretStore(
            new GridSim.Core.Security.DirectorySecretStore(dir), GridSim.Core.Security.EnvironmentSecretStore.Instance);
    }
    catch (DirectoryNotFoundException ex)
    {
        throw new InvalidOperationException($"GridSim.Web: configuration setting Secrets:Directory: {ex.Message}", ex);
    }
}

static GridSim.Core.Diagnostics.AuditRunRecorder StartRealtimeAudit(string logPath, string? anchorPath, string caseName, string windowsDir,
    IConfiguration config)
{
    try
    {
        var sinks = new List<GridSim.Core.Diagnostics.IAuditSink>();
        var channels = new List<GridSim.Core.Diagnostics.IDeliveryChannel>();
        if (config["Audit:ForwardPath"] is { Length: > 0 } forwardPath)
        {
            var forward = new GridSim.Core.Diagnostics.JsonLinesAuditSink(forwardPath);
            sinks.Add(forward);
            channels.Add(forward);
        }
        if (config["Audit:IncidentLogPath"] is { Length: > 0 } incidentPath)
        {
            var incidents = new GridSim.Core.Diagnostics.Incident.JsonLinesIncidentSink(incidentPath);
            sinks.AddRange(GridSim.Core.Diagnostics.Incident.IncidentWiring.AuditSinksFor(incidents));
            channels.Add(incidents);
        }
        var options = new GridSim.Core.Diagnostics.AuditRecorderOptions
        {
            Sinks = sinks,
            DeliveryChannels = channels,
            AnchorSigningKey = config["Audit:AnchorSigningKeyPath"] is { Length: > 0 } signPath
                ? GridSim.Core.Diagnostics.AuditRecorderOptions.LoadKey(signPath, requirePrivate: true) : null,
            AnchorVerificationKey = config["Audit:AnchorVerificationKeyPath"] is { Length: > 0 } verifyPath
                ? GridSim.Core.Diagnostics.AuditRecorderOptions.LoadKey(verifyPath, requirePrivate: false) : null,
        };
        GridSim.Core.Diagnostics.AuditRunRecorder audit = GridSim.Core.Diagnostics.AuditRunRecorder.Start(
            logPath, GridSim.Core.Diagnostics.AuditEvent.DefaultActor(), "web-realtime",
            GridSim.Core.Diagnostics.ProvenanceTag.DerivedFromRecorded, string.IsNullOrWhiteSpace(anchorPath) ? null : anchorPath, options);
        if (audit.AnchorWasMissing)
            Console.Error.WriteLine($"GridSim.Web: warning: audit log '{audit.FilePath}' has entries but no anchor file '{audit.AnchorPath}'; "
                + "tail truncation before this run cannot be detected. The anchor is written from this run on.");
        audit.AddInput(GridSim.Core.Diagnostics.AuditRunRecorder.InputForArgument("case", caseName));
        if (Directory.Exists(windowsDir))
            audit.AddInput(GridSim.Core.Diagnostics.AuditRunRecorder.InputForArgument("windows-dir", windowsDir));
        return audit;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {
        throw new InvalidOperationException(
            $"GridSim.Web: configuration setting Audit:LogPath ('{logPath}') could not be opened as the audit log: {ex.Message}", ex);
    }
}

static GridSim.Core.Model.GridModel LoadRealtimeCase(CaseCatalog catalog, string caseName)
{
    try
    {
        return catalog.LoadModel(caseName);
    }
    catch (LicenceException ex)
    {
        throw new InvalidOperationException(
            $"GridSim.Web: configuration setting Realtime:Case names '{caseName}', which this licence does not cover: {ex.Message} "
            + "Choose a case the licence covers, install a licence (Licence:Key), or set Realtime:Enabled=false.", ex);
    }
    catch (KeyNotFoundException ex)
    {
        throw new InvalidOperationException(
            $"GridSim.Web: configuration setting Realtime:Case names '{caseName}', which is not a case this host knows. "
            + "Use a built-in name such as case9, a bundled GB case, or a model folder next to the app; or set Realtime:Enabled=false.", ex);
    }
}
