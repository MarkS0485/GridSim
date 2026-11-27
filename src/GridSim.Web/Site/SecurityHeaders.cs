using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// Bug #179 (2026-11-01): the response headers that tell a browser how to treat GridSim's pages. Before, the app set
/// none: the Blazor UI could be framed by any site (click-jacking), a browser could sniff a JSON or text answer into an
/// executable type, and HTTP Strict Transport Security relied wholly on the reverse proxy.
///
/// <para>Every response now carries</para>
/// <list type="bullet">
/// <item><c>Content-Security-Policy</c>: <see cref="Policy"/> (2.1.140), which lets the page load scripts, styles,
/// images, fonts and connections from this app only, followed by <c>frame-ancestors &lt;value&gt;</c>. Styles also allow
/// <c>'unsafe-inline'</c>, because the Razor components set inline <c>style</c> attributes; scripts do not (the pages
/// have no inline script or inline event handler). The telemetry guard checks the <c>connect-src</c> directive at
/// start-up (<see cref="ConnectSrc"/>);</item>
/// <item><c>X-Frame-Options</c>, the older equivalent, <c>DENY</c> for <c>'none'</c> and <c>SAMEORIGIN</c> for
/// <c>'self'</c> (left out for any other value, which it cannot express);</item>
/// <item><c>X-Content-Type-Options: nosniff</c> and <c>Referrer-Policy: strict-origin-when-cross-origin</c>.</item>
/// </list>
/// <para>HSTS is added by ASP.NET Core's own middleware (Program.cs) outside Development, on HTTPS requests only (a
/// request the trusted proxy forwarded as https counts), and never for the loopback names.</para>
///
/// <para><b>The settings (opt-outs).</b> <c>GridSim:FrameAncestors</c> (default <c>'none'</c>): the CSP
/// frame-ancestors source list, for example <c>'self' https://intranet.example.com</c> to allow a named portal to embed
/// the dashboard, or <c>*</c> to allow any site (not recommended). <c>GridSim:Hsts</c> (default <c>true</c>):
/// <c>false</c> leaves HSTS to the reverse proxy. A frame-ancestors value with a semicolon, comma, quote mark in the wrong
/// place or a line break (which would inject another policy directive), or an HSTS value that is not true or false,
/// stops start-up with a message naming the setting.</para>
/// </summary>
public sealed class SecurityHeaders
{
    // CA1870: the characters that would end the frame-ancestors directive or start a new header line, cached once.
    private static readonly System.Buffers.SearchValues<char> ForbiddenInFrameAncestors =
        System.Buffers.SearchValues.Create(";,\r\n\0");

    /// <summary>The frame-ancestors setting key (<c>GridSim__FrameAncestors</c> in the environment).</summary>
    public const string FrameAncestorsKey = "GridSim:FrameAncestors";

    /// <summary>The HSTS switch key (<c>GridSim__Hsts</c> in the environment).</summary>
    public const string HstsKey = "GridSim:Hsts";

    /// <summary>The connect-src directive: the page may connect (fetch, WebSocket) to this app only.</summary>
    public const string ConnectSrc = "connect-src 'self'";

    /// <summary>
    /// Every directive of the Content-Security-Policy except frame-ancestors (which follows the setting): nothing may be
    /// loaded from, or sent to, another origin. <c>'unsafe-inline'</c> in style-src is for the Razor components' inline
    /// style attributes; <c>data:</c> in img-src is for images the page builds in place.
    /// </summary>
    public const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
        + ConnectSrc + "; base-uri 'self'; form-action 'self'";

    /// <summary>The default frame-ancestors source list: no site may frame GridSim.</summary>
    public const string DefaultFrameAncestors = "'none'";

    /// <summary>The CSP frame-ancestors source list in force.</summary>
    public string FrameAncestors { get; }

    /// <summary>The X-Frame-Options value, or null when the frame-ancestors list has no X-Frame-Options equivalent.</summary>
    public string? XFrameOptions { get; }

    /// <summary>True when the app adds HSTS itself (outside Development).</summary>
    public bool Hsts { get; }

    private SecurityHeaders(string frameAncestors, bool hsts)
    {
        FrameAncestors = frameAncestors;
        XFrameOptions = frameAncestors switch
        {
            "'none'" => "DENY",
            "'self'" => "SAMEORIGIN",
            _ => null,
        };
        Hsts = hsts;
    }

    /// <summary>Read both settings and check them.</summary>
    /// <exception cref="InvalidOperationException">A value is malformed; the message names the setting.</exception>
    public static SecurityHeaders FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[FrameAncestorsKey], configuration[HstsKey]);
    }

    /// <summary>Check a frame-ancestors value and an HSTS value (null or blank means the default).</summary>
    /// <exception cref="InvalidOperationException">A value is malformed; the message names the setting.</exception>
    public static SecurityHeaders Parse(string? frameAncestors, string? hsts)
    {
        string fa = string.IsNullOrWhiteSpace(frameAncestors) ? DefaultFrameAncestors : frameAncestors.Trim();
        // One directive's source list only: anything that could end the directive or start a header line is refused.
        if (fa.AsSpan().IndexOfAny(ForbiddenInFrameAncestors) >= 0)
            throw new InvalidOperationException(
                $"The setting {FrameAncestorsKey} ('{fa}') is not valid: it is one CSP frame-ancestors source list (for "
                + "example 'none', 'self', or 'self' https://portal.example.com), with no semicolon, comma or line break.");
        foreach (string token in fa.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool keyword = token is "'none'" or "'self'";
            bool quoted = token.Contains('\'');
            if (quoted && !keyword)
                throw new InvalidOperationException(
                    $"The setting {FrameAncestorsKey} ('{fa}') is not valid: '{token}' is not a frame-ancestors keyword (only 'none' and 'self' are quoted).");
            if (token == "'none'" && fa != "'none'")
                throw new InvalidOperationException(
                    $"The setting {FrameAncestorsKey} ('{fa}') is not valid: 'none' must be the only source.");
        }

        bool on = true;
        if (!string.IsNullOrWhiteSpace(hsts) && !bool.TryParse(hsts.Trim(), out on))
            throw new InvalidOperationException($"The setting {HstsKey} ('{hsts}') is not valid: write true or false.");
        return new SecurityHeaders(fa, on);
    }

    /// <summary>Add the headers to a response (called from a response-starting callback, so every answer, including
    /// errors and the static files, carries them). An existing value set further down the pipeline is kept.</summary>
    public void Apply(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        // 2.1.140: always the full policy. Blazor writes a frame-ancestors-only policy on its pages; it is replaced by this
        // one, which carries the same frame-ancestors value.
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        // X-Frame-Options always follows the configured rule: ASP.NET Core's antiforgery writes SAMEORIGIN on every form
        // page, which would contradict 'none' (DENY) and block a portal named in the setting.
        if (XFrameOptions is not null) headers["X-Frame-Options"] = XFrameOptions;
        else headers.Remove("X-Frame-Options");
        headers["X-Content-Type-Options"] = "nosniff";
        if (!headers.ContainsKey("Referrer-Policy"))
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    /// <summary>The Content-Security-Policy value every answer carries.</summary>
    public string ContentSecurityPolicy => Policy + "; frame-ancestors " + FrameAncestors;

    /// <summary>
    /// The Content-Security-Policy an answer carries, composed exactly as the host composes it when a response starts
    /// (Program.cs): these headers are applied, then the script policy. The telemetry guard reads its connect-src from
    /// this, not from <see cref="Policy"/>, so a change to how the header is built is checked too.
    /// </summary>
    public static string ServedPolicy(SecurityHeaders headers, ScriptCspPolicy scriptPolicy)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(scriptPolicy);
        var composed = new HeaderDictionary();
        headers.Apply(composed);
        scriptPolicy.Apply(composed);
        return composed["Content-Security-Policy"].ToString();
    }

    /// <summary>
    /// <see cref="ServedPolicy(SecurityHeaders, ScriptCspPolicy)"/> for the security headers and script policy read from
    /// <paramref name="configuration"/> (<see cref="FromConfiguration"/>, <see cref="ScriptCspPolicy.FromConfiguration"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is malformed; the message names it.</exception>
    public static string ServedPolicy(IConfiguration configuration) =>
        ServedPolicy(FromConfiguration(configuration), ScriptCspPolicy.FromConfiguration(configuration));

    /// <summary>
    /// The connect-src directive of a Content-Security-Policy value (directive names matched ignoring case), or null when
    /// it has none. When it has more than one, all of them are returned joined by "; ", which the telemetry guard refuses.
    /// </summary>
    public static string? ConnectSrcOf(string? policy)
    {
        if (policy is null) return null;
        string[] found = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(d => d.Split(' ', 2)[0].Equals("connect-src", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return found.Length == 0 ? null : string.Join("; ", found);
    }

    /// <summary>
    /// The script origins configured anywhere in the settings, each named with its setting, for the telemetry guard
    /// (<c>TelemetryGuard.CheckWebPolicy</c>), which refuses to start when there is one: the removed analytics settings
    /// (<c>Site:GoogleTagId</c>, <c>Site:AnalyticsScriptUrl</c>, <c>Site:AnalyticsCollectorUrl</c>,
    /// <c>Site:AnalyticsSiteName</c>) and every origin in <c>GridSim:ScriptCspSources</c>. Reads raw values only, so it
    /// cannot fail on a malformed one.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredScriptOrigins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var origins = new List<string>();
        foreach (string key in SiteSettings.RemovedSettings)
        {
            string? v = configuration[SiteSettings.Section + ":" + key];
            if (!string.IsNullOrWhiteSpace(v)) origins.Add($"{SiteSettings.Section}:{key}={v.Trim()}");
        }
        string? sources = configuration[ScriptCspPolicy.SourcesKey];
        if (!string.IsNullOrWhiteSpace(sources))
            foreach (string token in sources.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                origins.Add($"{ScriptCspPolicy.SourcesKey}={token}");
        return origins;
    }

    /// <summary>A one-line description for the start-up log.</summary>
    public string Description =>
        $"Content-Security-Policy {ContentSecurityPolicy}" + (XFrameOptions is null ? "" : $", X-Frame-Options {XFrameOptions}")
        + ", nosniff, Referrer-Policy strict-origin-when-cross-origin, HSTS " + (Hsts ? "on (HTTPS, outside Development)" : "OFF (left to the reverse proxy)");
}
