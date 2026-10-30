namespace WebSite.Data.Sso;

/// <summary>
/// Cross-domain SSO configuration (bind of the <c>Sso</c> section). The estate's shared-cookie
/// SSO works within <c>.login.example.com</c> but a browser will never send that
/// cookie to a different registered domain (app.example.org, other.example.net, ...). This adds a
/// signed-ticket login handoff so each app can establish its OWN session cookie on its OWN
/// domain from the one central login - reusing the shared DataProtection key ring every app
/// already mounts (so tickets one app mints, another can verify).
/// </summary>
public sealed class EstateSsoOptions
{
    public const string Section = "Sso";

    /// <summary>Master switch. Off => endpoints 404 and the cookie redirect is unchanged.</summary>
    public bool Enabled { get; set; }

    /// <summary>This app is the login AUTHORITY (mints tickets at /sso/bridge). Exactly one app
    /// in the estate sets this (its login site). All others are clients.</summary>
    public bool IsAuthority { get; set; }

    /// <summary>The authority's public origin, e.g. <c>https://login.example.com</c>.
    /// Clients redirect unauthenticated users here to obtain a ticket.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Allowed client origins the authority may mint tickets for (the audience allowlist),
    /// e.g. <c>https://app.example.org</c>. A ticket is bound to exactly one of these and is
    /// rejected anywhere else. Only consulted on the authority.</summary>
    public List<string> AllowedClients { get; set; } = new();

    /// <summary>Ticket lifetime. Short by design - it only has to survive one browser redirect.</summary>
    public int TicketSeconds { get; set; } = 120;

    /// <summary>
    /// D-DOC-171: this CLIENT's own public origin as visitors see it, e.g. <c>https://app.example.org</c> (scheme, host
    /// and port only). Empty (the default) means "the origin of the request", which is right when the app is reached
    /// directly or behind a proxy that forwards <c>X-Forwarded-Proto</c> and <c>Host</c>. Set it when the proxy does not:
    /// the landing URL sent to the authority and the audience a ticket must carry then both use this value, so a TLS
    /// proxy that talks plain http to the app no longer makes every sign-in fail on an http-vs-https audience mismatch.
    /// Checked at start-up (<see cref="EstateSso.ValidatePublicOrigin"/>).
    /// </summary>
    public string PublicOrigin { get; set; } = "";
}
