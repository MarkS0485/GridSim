using System.Net;
using IPNetwork = System.Net.IPNetwork;   // not the obsolete Microsoft.AspNetCore.HttpOverrides.IPNetwork
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// N226 (October 2026): which reverse proxies the app believes when they send <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c>. Before N226 the app cleared ASP.NET's known-proxy lists, which makes the forwarded-headers
/// middleware believe those headers from ANY caller: harmless only while the app is reachable from the proxy alone.
/// A published port, a LAN binding or a container network shared with other services would let any client claim to be
/// HTTPS (satisfying the sign-on cookie's Secure policy) or claim any client address.
///
/// <para><b>The setting.</b> <c>GridSim:TrustedProxies</c> (environment variable <c>GridSim__TrustedProxies</c>): a
/// comma-separated list of entries, each one of</para>
/// <list type="bullet">
/// <item><c>loopback</c>: 127.0.0.0/8 and ::1/128, a proxy on the same machine (nginx in front of a systemd service);</item>
/// <item><c>private</c>: the RFC 1918 IPv4 networks 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 and the IPv6 unique local
/// network fc00::/7, where a container runtime's bridge gateway lives (Docker Compose: the host proxy reaches the
/// container from the bridge gateway, not from loopback);</item>
/// <item>an IP address (one proxy), or a network in CIDR form (<c>203.0.113.0/24</c>, <c>2001:db8::/32</c>);</item>
/// <item><c>any</c>, ALONE: believe every caller (the behaviour before N226; only where nothing but the proxy can reach
/// the app).</item>
/// </list>
/// <para>Unset or empty means <c>loopback</c>: the documented deployment, behind a proxy on loopback. Entries are trimmed
/// and keywords are case-insensitive.</para>
///
/// <para><b>Fail loud.</b> An entry that is not a keyword, an address or a well-formed CIDR network (including one whose
/// address has bits set past its prefix, such as <c>10.1.2.3/8</c>), an empty entry between commas, or <c>any</c>
/// combined with other entries stops start-up with a message naming the setting. Nothing is guessed.</para>
///
/// <para><b>IPv4 on a dual-stack listener.</b> Kestrel bound to <c>http://+:port</c> sees an IPv4 caller as an
/// IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>). Every IPv4 network is therefore also trusted in its mapped form
/// (<c>::ffff:0:0/96</c> plus the prefix), so the trust does not depend on how the middleware compares families.</para>
/// </summary>
public sealed class TrustedProxies
{
    /// <summary>The configuration key (<c>GridSim__TrustedProxies</c> in the environment).</summary>
    public const string ConfigurationKey = "GridSim:TrustedProxies";

    /// <summary>The value used when the setting is unset or empty.</summary>
    public const string DefaultValue = "loopback";

    /// <summary>True only for the explicit <c>any</c>: the known-proxy lists are left empty, which the middleware reads as
    /// "believe every caller".</summary>
    public bool TrustAll { get; }

    /// <summary>The trusted networks, in the order the entries were given (keywords expanded, IPv4 entries followed by
    /// their IPv4-mapped IPv6 form), without duplicates. Empty when <see cref="TrustAll"/>.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>A one-line description for the start-up log, e.g. <c>loopback (127.0.0.0/8, ::1/128)</c>.</summary>
    public string Description { get; }

    private TrustedProxies(bool trustAll, IReadOnlyList<IPNetwork> networks, string description)
    {
        TrustAll = trustAll;
        Networks = networks;
        Description = description;
    }

    // The keyword networks, written once. Each IPv4 network is followed by its mapped form when expanded (see Add).
    private static readonly string[] LoopbackNetworks = { "127.0.0.0/8", "::1/128" };
    private static readonly string[] PrivateNetworks = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7" };

    /// <summary>Read <see cref="ConfigurationKey"/> from configuration and parse it (see <see cref="Parse"/>).</summary>
    /// <exception cref="InvalidOperationException">The value is malformed; the message names the setting.</exception>
    public static TrustedProxies FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[ConfigurationKey]);
    }

    /// <summary>Parse a <c>GridSim:TrustedProxies</c> value. Null, empty or white space means <see cref="DefaultValue"/>.</summary>
    /// <exception cref="InvalidOperationException">The value is malformed; the message names the setting.</exception>
    public static TrustedProxies Parse(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? DefaultValue : value;
        string[] entries = text.Split(',');
        for (int i = 0; i < entries.Length; i++) entries[i] = entries[i].Trim();

        if (entries.Any(e => e.Length == 0))
            throw Bad($"'{text}' has an empty entry (two commas together, or a comma at either end).");

        if (entries.Any(e => string.Equals(e, "any", StringComparison.OrdinalIgnoreCase)))
        {
            if (entries.Length != 1)
                throw Bad($"'{text}' combines 'any' with other entries; 'any' (believe every caller) must stand alone.");
            return new TrustedProxies(true, Array.Empty<IPNetwork>(), "any caller (GridSim:TrustedProxies=any)");
        }

        var networks = new List<IPNetwork>();
        var parts = new List<string>();
        foreach (string entry in entries)
        {
            if (string.Equals(entry, "loopback", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string n in LoopbackNetworks) Add(networks, IPNetwork.Parse(n));
                parts.Add($"loopback ({string.Join(", ", LoopbackNetworks)})");
            }
            else if (string.Equals(entry, "private", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string n in PrivateNetworks) Add(networks, IPNetwork.Parse(n));
                parts.Add($"private ({string.Join(", ", PrivateNetworks)})");
            }
            else if (entry.Contains('/', StringComparison.Ordinal))
            {
                // IPNetwork.TryParse refuses a prefix out of range. It does NOT reliably refuse an address with bits set
                // past the prefix: on .NET 10 "10.1.2.3/8" parses and is quietly masked to 10.0.0.0/8 (found at the end
                // pass, first run, 2026-10-11). A typo such as 10.1.2.3/8 for 10.1.2.3/32 would then trust a whole /8,
                // so we check it ourselves: the address the user wrote must BE the network's base address, exactly.
                const string cidrHelp = "is not a network in CIDR form (address/prefix, with no address bits set past the prefix, e.g. 203.0.113.0/24).";
                if (!IPNetwork.TryParse(entry, out IPNetwork net))
                    throw Bad($"entry '{entry}' {cidrHelp}");
                string written = entry[..entry.IndexOf('/', StringComparison.Ordinal)];
                if (!IPAddress.TryParse(written, out IPAddress? writtenAddress) || !writtenAddress.Equals(net.BaseAddress))
                    throw Bad($"entry '{entry}' {cidrHelp}");
                Add(networks, net);
                parts.Add(net.ToString());
            }
            else
            {
                // A bare address: exactly one proxy. IPAddress.TryParse also accepts shorthand such as "10" or "1.2.3";
                // refuse those by requiring the canonical text to round-trip family-wise (four dotted parts for IPv4).
                if (!IPAddress.TryParse(entry, out IPAddress? address)
                    || (address.AddressFamily == AddressFamily.InterNetwork && entry.Count(c => c == '.') != 3))
                    throw Bad($"entry '{entry}' is not an IP address, a CIDR network or one of the keywords loopback, private, any.");
                if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                    throw Bad($"entry '{entry}' carries an IPv6 zone index; give the address without '%zone'.");
                int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                var net = new IPNetwork(address, bits);
                Add(networks, net);
                parts.Add(net.ToString());
            }
        }
        return new TrustedProxies(false, networks, string.Join(", ", parts));
    }

    /// <summary>Apply to the forwarded-headers options: replace both known lists with <see cref="Networks"/> (or leave
    /// them empty for <see cref="TrustAll"/>).</summary>
    public void ApplyTo(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (IPNetwork n in Networks) options.KnownIPNetworks.Add(n);
    }

    /// <summary>Add a network once, and for an IPv4 network also its IPv4-mapped IPv6 form (::ffff:0:0/96 + prefix).</summary>
    private static void Add(List<IPNetwork> list, IPNetwork net)
    {
        if (!list.Contains(net)) list.Add(net);
        if (net.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var mapped = new IPNetwork(net.BaseAddress.MapToIPv6(), net.PrefixLength + 96);
            if (!list.Contains(mapped)) list.Add(mapped);
        }
    }

    private static InvalidOperationException Bad(string why) =>
        new($"{ConfigurationKey} (environment variable GridSim__TrustedProxies): {why} Allowed: a comma-separated list of "
            + "loopback, private, IP addresses and CIDR networks, or 'any' alone. Unset means loopback.");
}
