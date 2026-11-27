using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// An opt-in <c>script-src</c> Content-Security-Policy (2.1.074 RC-4-Fixes), read once at start-up from
/// <c>GridSim:ScriptCsp</c>: <c>off</c> (the default: the headers are exactly what they were), <c>report-only</c> (a
/// <c>Content-Security-Policy-Report-Only</c> header, which blocks nothing and lets a deployer see in the browser console
/// what <c>script-src 'self'</c> would stop) or <c>enforce</c> (the directive is added to the Content-Security-Policy
/// header). Since 2.1.140 every answer's Content-Security-Policy already carries <c>script-src 'self'</c>
/// (<see cref="SecurityHeaders.Policy"/>), so <c>enforce</c> leaves it as it is. <c>GridSim:ScriptCspSources</c> would add
/// origins after <c>'self'</c>; the telemetry guard refuses to start a host that sets any, so it must stay empty.
/// The policy has no nonce, so an inline script is blocked in <c>enforce</c>: the Blazor pages have not been exercised in a
/// browser under it, and a deployer should run <c>report-only</c> first. A page cannot set its own Content-Security-Policy:
/// <see cref="SecurityHeaders.Apply"/> replaces it when the response starts, before this policy is applied.
/// </summary>
public sealed class ScriptCspPolicy
{
    /// <summary>Configuration key of the mode (<c>GridSim__ScriptCsp</c>).</summary>
    public const string ModeKey = "GridSim:ScriptCsp";
    /// <summary>Configuration key of the extra sources (<c>GridSim__ScriptCspSources</c>).</summary>
    public const string SourcesKey = "GridSim:ScriptCspSources";

    private static readonly char[] Forbidden = { ';', ',', '"', '\r', '\n', '\t' };

    /// <summary>The mode.</summary>
    public enum PolicyMode
    {
        /// <summary>No script-src header (the default).</summary>
        Off = 0,
        /// <summary>Content-Security-Policy-Report-Only only.</summary>
        ReportOnly = 1,
        /// <summary>script-src added to Content-Security-Policy.</summary>
        Enforce = 2,
    }

    private ScriptCspPolicy(PolicyMode mode, string directive)
    {
        Mode = mode;
        Directive = directive;
    }

    /// <summary>The mode in force.</summary>
    public PolicyMode Mode { get; }

    /// <summary>The directive text, <c>script-src 'self' ...</c>.</summary>
    public string Directive { get; }

    /// <summary>Read and check the settings.</summary>
    /// <exception cref="InvalidOperationException">A value is malformed; the message names the setting.</exception>
    public static ScriptCspPolicy FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[ModeKey], configuration[SourcesKey]);
    }

    /// <summary>Check a mode and a source list (null or blank: off, no extra sources).</summary>
    public static ScriptCspPolicy Parse(string? mode, string? sources)
    {
        PolicyMode m = (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "off" => PolicyMode.Off,
            "report-only" => PolicyMode.ReportOnly,
            "enforce" => PolicyMode.Enforce,
            _ => throw new InvalidOperationException($"The setting {ModeKey} ('{mode}') is not valid: write off, report-only or enforce."),
        };
        string extra = string.IsNullOrWhiteSpace(sources) ? "" : sources.Trim();
        if (extra.AsSpan().IndexOfAny(Forbidden) >= 0)
            throw new InvalidOperationException($"The setting {SourcesKey} ('{extra}') is not valid: a space-separated list of origins, with no semicolon, comma, quote mark or line break.");
        foreach (string token in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (token.Contains('\'') || !(token.StartsWith("https://", StringComparison.Ordinal) || token.StartsWith("http://", StringComparison.Ordinal)))
                throw new InvalidOperationException($"The setting {SourcesKey} ('{extra}') is not valid: '{token}' is not an http:// or https:// origin.");
        string directive = "script-src 'self'" + (extra.Length == 0 ? "" : " " + extra);
        return new ScriptCspPolicy(m, directive);
    }

    /// <summary>Add the header for the mode (called with the other security headers, after them).</summary>
    public void Apply(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        switch (Mode)
        {
            case PolicyMode.ReportOnly:
                headers["Content-Security-Policy-Report-Only"] = Directive;
                break;
            case PolicyMode.Enforce:
                if (!headers.TryGetValue("Content-Security-Policy", out var existing) || existing.Count == 0)
                    headers["Content-Security-Policy"] = Directive;
                else if (!existing.ToString().Contains("script-src", StringComparison.Ordinal))
                    headers["Content-Security-Policy"] = existing.ToString() + "; " + Directive;
                break;
        }
    }

    /// <summary>One line for the start-up log.</summary>
    public string Description => Mode switch
    {
        PolicyMode.Off => "no separate script policy (the Content-Security-Policy carries script-src 'self')",
        PolicyMode.ReportOnly => $"{Directive} (report-only; nothing blocked)",
        _ => $"{Directive} (enforced; no nonce, so inline scripts are blocked)",
    };
}
