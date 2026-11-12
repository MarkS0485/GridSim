using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// Bug #170 (2026-11-01): which <c>Host</c> names this instance answers. Before, no host filtering was configured at
/// all, and the <c>/ws/sim</c> cross-site guard accepted any <c>Origin</c> equal to the request's own <c>Host</c>. A
/// DNS-rebinding page (attacker.example first resolving to the attacker, then to the GridSim machine) sends
/// <c>Host: attacker.example</c> and <c>Origin: http://attacker.example</c>, which matched, so on a non-replay-only
/// instance it could drive sessions, read frames and call <c>/api/solve</c> and <c>/api/topology</c> from a visitor's
/// browser. With an allow-list of names, a request whose <c>Host</c> is not one of them is refused before any page,
/// API or socket code runs, so a rebound name never reaches the app.
///
/// <para><b>The setting.</b> The standard ASP.NET Core key <c>AllowedHosts</c> (environment variable
/// <c>AllowedHosts</c>): names separated by semicolons. Each entry is one of</para>
/// <list type="bullet">
/// <item>a host name or IP address, matched exactly and case-insensitively, port ignored (<c>gridsim.example.com</c>,
/// <c>192.0.2.10</c>, <c>[::1]</c> with its brackets);</item>
/// <item><c>*.example.com</c>: any sub-domain of example.com (not example.com itself);</item>
/// <item><c>*</c>, ALONE: answer every name. This is the opt-out; it brings back the exposure above, and start-up says
/// so on standard error. The <c>/ws/sim</c> origin check then still refuses a page whose origin is not loopback or one
/// of <c>GridSim:AllowedOrigins</c> (see <see cref="Api.SimWebSocket.OriginAllowed"/>).</item>
/// </list>
/// <para><b>Default: secure.</b> Unset or empty means <see cref="DefaultValue"/>, the loopback names only, which is what
/// a local <c>dotnet run</c>, the container's own health check and a reverse proxy that sends <c>Host: localhost</c>
/// need. A deployment reached under a public name (nginx forwarding <c>Host $host</c>, as the example configuration
/// does) MUST list that name, for example <c>AllowedHosts=gridsim.example.com</c>. Until it does, every request under
/// that name gets a 400 whose text names the setting, so the change cannot go unnoticed.</para>
///
/// <para><b>Fail loud.</b> An empty entry between semicolons, an entry with a scheme, path, port or white space inside,
/// a bare <c>*.</c>, or <c>*</c> combined with other entries stops start-up with a message naming the setting.</para>
/// </summary>
public sealed class HostAllowList
{
    /// <summary>The configuration key: the standard ASP.NET Core host-filtering setting.</summary>
    public const string ConfigurationKey = "AllowedHosts";

    /// <summary>The value used when the setting is unset or empty: the loopback names.</summary>
    public const string DefaultValue = "localhost;127.0.0.1;[::1]";

    private readonly string[] _exact;
    private readonly string[] _suffixes;   // ".example.com" for each "*.example.com"

    /// <summary>True only for the explicit opt-out <c>*</c>: every Host is answered.</summary>
    public bool AllowsAny { get; }

    /// <summary>True when the value came from <see cref="DefaultValue"/> (the setting was not given).</summary>
    public bool IsDefault { get; }

    /// <summary>A one-line description for the start-up log.</summary>
    public string Description { get; }

    /// <summary>
    /// B2 (November 2026): true when every name this instance answers is a loopback name (localhost, 127.x.x.x, [::1])
    /// and there is no wildcard: the default list, or an explicit list of loopback names only. False for <c>*</c> and
    /// for any list that names a public or private host. <see cref="AccessControl"/> reads it to choose the default
    /// role of an anonymous caller: the documented single-user loopback use keeps working, and an instance reached
    /// under any other name is fail-safe.
    /// </summary>
    public bool LoopbackOnly => !AllowsAny && _suffixes.Length == 0 && _exact.Length > 0 && _exact.All(IsLoopbackName);

    private HostAllowList(bool any, string[] exact, string[] suffixes, bool isDefault, string description)
    {
        AllowsAny = any;
        _exact = exact;
        _suffixes = suffixes;
        IsDefault = isDefault;
        Description = description;
    }

    /// <summary>Read <see cref="ConfigurationKey"/> and parse it (see <see cref="Parse"/>).</summary>
    /// <exception cref="InvalidOperationException">The value is malformed; the message names the setting.</exception>
    public static HostAllowList FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[ConfigurationKey]);
    }

    /// <summary>Parse an <c>AllowedHosts</c> value. Null, empty or white space means <see cref="DefaultValue"/>.</summary>
    /// <exception cref="InvalidOperationException">The value is malformed; the message names the setting.</exception>
    public static HostAllowList Parse(string? value)
    {
        bool isDefault = string.IsNullOrWhiteSpace(value);
        string text = isDefault ? DefaultValue : value!;
        string[] entries = text.Split(';', StringSplitOptions.TrimEntries);
        if (entries.Length == 1 && entries[0] == "*")
            return new HostAllowList(true, Array.Empty<string>(), Array.Empty<string>(), false,
                "* (ANY host name: the DNS-rebinding protection is OFF)");

        var exact = new List<string>();
        var suffixes = new List<string>();
        foreach (string e in entries)
        {
            if (e.Length == 0)
                throw Bad(text, "an entry is empty (two semicolons in a row, or a leading or trailing semicolon)");
            if (e == "*")
                throw Bad(text, "'*' (any host) must be the only entry");
            if (e.Contains("://", StringComparison.Ordinal) || e.Contains('/') || e.Any(char.IsWhiteSpace))
                throw Bad(text, $"'{e}' is not a host name (no scheme, path or spaces: write gridsim.example.com)");
            string host = e;
            if (host.StartsWith("*.", StringComparison.Ordinal))
            {
                string rest = host[2..];
                if (rest.Length == 0 || rest.Contains('*') || rest.Contains(':'))
                    throw Bad(text, $"'{e}' is not a valid wildcard (write *.example.com)");
                suffixes.Add("." + rest.ToLowerInvariant());
                continue;
            }
            if (host.Contains('*'))
                throw Bad(text, $"'{e}': a wildcard is allowed only as a leading '*.'");
            // A port is never part of an entry: the Host header's port is ignored when matching. IPv6 is written in
            // brackets, so a colon outside brackets can only be a port.
            bool bracketed = host.StartsWith('[') && host.EndsWith(']');
            if (!bracketed && host.Contains(':'))
                throw Bad(text, $"'{e}' carries a port or is an IPv6 address without brackets (write the name alone, or [::1])");
            exact.Add(host.ToLowerInvariant());
        }
        string description = string.Join(";", exact.Concat(suffixes.Select(s => "*" + s)))
            + (isDefault ? " (default: loopback only; set AllowedHosts to the names this instance is reached under)" : "");
        return new HostAllowList(false, exact.ToArray(), suffixes.ToArray(), isDefault, description);
    }

    private static InvalidOperationException Bad(string value, string why) =>
        new($"The setting {ConfigurationKey} ('{value}') is not valid: {why}. It is a semicolon-separated list of host "
            + "names (gridsim.example.com, *.example.com, [::1]), or '*' alone to answer any name.");

    /// <summary>
    /// True when a request with this <c>Host</c> header value is answered. The port is ignored; matching is
    /// case-insensitive. An empty Host (an HTTP/1.0 request) is refused unless <see cref="AllowsAny"/>.
    /// </summary>
    public bool IsAllowed(string? hostHeader)
    {
        if (AllowsAny) return true;
        string? host = HostWithoutPort(hostHeader);
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant();
        foreach (string e in _exact)
            if (string.Equals(e, host, StringComparison.Ordinal)) return true;
        foreach (string s in _suffixes)
            if (host.Length > s.Length && host.EndsWith(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the host name (port ignored) is a loopback name: localhost, 127.x.x.x or [::1].</summary>
    public static bool IsLoopbackName(string? hostHeader)
    {
        string? host = HostWithoutPort(hostHeader)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return false;
        if (host == "localhost" || host == "[::1]") return true;
        return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && System.Net.IPAddress.IsLoopback(ip);
    }

    // "name:port" -> "name"; "[v6]:port" -> "[v6]"; "name" -> "name". Null for null or blank.
    private static string? HostWithoutPort(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return null;
        string h = hostHeader.Trim();
        if (h.StartsWith('['))
        {
            int close = h.IndexOf(']', StringComparison.Ordinal);
            return close < 0 ? h : h[..(close + 1)];
        }
        int colon = h.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? h : h[..colon];
    }

    /// <summary>The plain-text body of the 400 answer to a refused Host. It names the setting so an operator who has
    /// just upgraded sees at once what to set.</summary>
    public static string RefusalText(string? hostHeader) =>
        $"400 Bad Request: this GridSim instance does not answer the host name '{hostHeader}'. "
        + "Its operator lists the names it is reached under in the AllowedHosts setting (environment variable "
        + "AllowedHosts, semicolon-separated, for example AllowedHosts=gridsim.example.com); the default is the loopback "
        + "names only. AllowedHosts=* answers any name but removes the DNS-rebinding protection.";
}
