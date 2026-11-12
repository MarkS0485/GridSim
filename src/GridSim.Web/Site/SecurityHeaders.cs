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
/// <item><c>Content-Security-Policy: frame-ancestors &lt;value&gt;</c> (only the framing directive: a full script
/// policy would break the configurable analytics and share-card settings, so it is left to the deployment's proxy);</item>
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
        // A page that set its own Content-Security-Policy keeps it; the framing rule is then that page's own concern.
        if (!headers.ContainsKey("Content-Security-Policy"))
            headers["Content-Security-Policy"] = "frame-ancestors " + FrameAncestors;
        // X-Frame-Options always follows the configured rule: ASP.NET Core's antiforgery writes SAMEORIGIN on every form
        // page, which would contradict 'none' (DENY) and block a portal named in the setting.
        if (XFrameOptions is not null) headers["X-Frame-Options"] = XFrameOptions;
        else headers.Remove("X-Frame-Options");
        headers["X-Content-Type-Options"] = "nosniff";
        if (!headers.ContainsKey("Referrer-Policy"))
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    /// <summary>A one-line description for the start-up log.</summary>
    public string Description =>
        $"frame-ancestors {FrameAncestors}" + (XFrameOptions is null ? "" : $", X-Frame-Options {XFrameOptions}")
        + ", nosniff, Referrer-Policy strict-origin-when-cross-origin, HSTS " + (Hsts ? "on (HTTPS, outside Development)" : "OFF (left to the reverse proxy)");
}
