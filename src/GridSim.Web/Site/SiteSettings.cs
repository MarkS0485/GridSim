using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// N086 / N087: everything that identifies ONE deployment of the web app (its analytics, its share-card metadata,
/// its security contact, its footer links and its data-protection application name) comes from configuration, never
/// from committed code. Before N086 the page head loaded a Google tag and a first-party analytics script from one
/// specific estate host, carried that estate's og:image and Organization metadata, served a security.txt naming one
/// person, linked one person's social profiles in the footer, and used that estate's data-protection application name
/// when none was configured - in every deployment, including a fresh clone run on a laptop.
///
/// <para>Defaults are neutral: with no <c>Site</c> section at all the page loads no third-party script, carries no
/// host-specific metadata, shows no personal links, answers 404 for <c>/.well-known/security.txt</c>, and protects its
/// cookies under the application name <see cref="DefaultApplicationName"/>. A site sets its own values through
/// configuration, typically environment variables such as <c>Site__GoogleTagId</c> (see docs/developer/deploy/README.md).</para>
///
/// <para>Fail loud: every value is checked once at start-up. A malformed value (a relative URL, a tag id that is not
/// a tag id, a security contact without an expiry, an expiry already in the past, a control character anywhere) stops
/// the app with a message naming the setting, rather than rendering a broken or misleading page.</para>
/// </summary>
public sealed class SiteSettings
{
    /// <summary>The configuration section the site values live under (<c>Site:...</c>, or <c>Site__...</c> in the environment).</summary>
    public const string Section = "Site";

    /// <summary>
    /// N087: the data-protection application name used when <c>Auth:ApplicationName</c> is unset. It is neutral (the
    /// product name). A deployment that must decrypt another application's cookies (an estate single sign-on) sets
    /// <c>Auth:ApplicationName</c> to that estate's name explicitly; it no longer inherits one from code.
    /// </summary>
    public const string DefaultApplicationName = "GridSim";

    /// <summary>The site name passed to the first-party analytics script when <c>Site:AnalyticsSiteName</c> is unset.</summary>
    public const string DefaultAnalyticsSiteName = "GridSim";

    /// <summary>The <c>Preferred-Languages</c> line of security.txt when <c>Site:SecurityTxt:PreferredLanguages</c> is unset.</summary>
    public const string DefaultSecurityPreferredLanguages = "en";

    /// <summary>Google tag id (for example <c>G-XXXXXXXXXX</c>); empty = no Google tag is loaded.</summary>
    public string GoogleTagId { get; private init; } = "";

    /// <summary>Absolute URL of a first-party analytics script; empty = none is loaded.</summary>
    public string AnalyticsScriptUrl { get; private init; } = "";

    /// <summary>Absolute URL the analytics script reports to (its <c>data-collector</c> attribute); empty = attribute omitted.</summary>
    public string AnalyticsCollectorUrl { get; private init; } = "";

    /// <summary>The <c>data-site</c> attribute given to the analytics script.</summary>
    public string AnalyticsSiteName { get; private init; } = DefaultAnalyticsSiteName;

    /// <summary>Absolute URL of the share-card image (og:image, twitter:image); empty = those two tags are omitted.</summary>
    public string ShareImageUrl { get; private init; } = "";

    /// <summary>Organisation name for the schema.org Organization block; empty = the block is omitted.</summary>
    public string OrganisationName { get; private init; } = "";

    /// <summary>Absolute URL of the organisation (schema.org <c>url</c>); optional, needs <see cref="OrganisationName"/>.</summary>
    public string OrganisationUrl { get; private init; } = "";

    /// <summary>Absolute URL of the organisation logo (schema.org <c>logo</c>); optional, needs <see cref="OrganisationName"/>.</summary>
    public string OrganisationLogoUrl { get; private init; } = "";

    /// <summary>The footer links (text and absolute URL), in order; empty = no links are shown.</summary>
    public IReadOnlyList<FooterLink> FooterLinks { get; private init; } = Array.Empty<FooterLink>();

    /// <summary>
    /// The complete body of <c>/.well-known/security.txt</c> (RFC 9116), or null when no security contact is
    /// configured, in which case the path answers 404.
    /// </summary>
    public string? SecurityTxt { get; private init; }

    /// <summary>True when a Google tag or a first-party analytics script is configured.</summary>
    public bool HasAnalytics => GoogleTagId.Length > 0 || AnalyticsScriptUrl.Length > 0;

    /// <summary>
    /// The schema.org Organization block as JSON, or null when no organisation is configured. Serialised with the
    /// default System.Text.Json encoder, which escapes &lt;, &gt; and &amp;, so the text cannot close the surrounding
    /// script element whatever the configured values contain.
    /// </summary>
    public string? OrganisationJsonLd
    {
        get
        {
            if (OrganisationName.Length == 0) return null;
            var o = new Dictionary<string, string>
            {
                ["@context"] = "https://schema.org",
                ["@type"] = "Organization",
                ["name"] = OrganisationName,
            };
            if (OrganisationUrl.Length > 0) o["url"] = OrganisationUrl;
            if (OrganisationLogoUrl.Length > 0) o["logo"] = OrganisationLogoUrl;
            return JsonSerializer.Serialize(o);
        }
    }

    /// <summary>The Google tag bootstrap script body, or null when no tag id is configured. The id is pattern-checked
    /// at start-up (letters, digits and dashes only), so it is safe to place inside a JavaScript string literal.</summary>
    public string? GoogleTagBootstrap => GoogleTagId.Length == 0
        ? null
        : "window.dataLayer = window.dataLayer || [];\n"
          + "function gtag(){dataLayer.push(arguments);}\n"
          + "gtag('js', new Date());\n"
          + "gtag('config', '" + GoogleTagId + "');";

    /// <summary>One footer link.</summary>
    public sealed record FooterLink(string Text, string Url);

    /// <summary>The neutral settings: nothing configured.</summary>
    public static SiteSettings Neutral { get; } = new();

    // A Google tag id: a short prefix (G, GT, AW, UA, DC), a dash, then letters, digits and dashes. Strict on purpose:
    // the id is written into a script, so anything outside this alphabet is refused rather than escaped.
    private static readonly Regex TagIdPattern = new("^(G|GT|AW|UA|DC)-[A-Za-z0-9-]{4,40}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Read and check the <c>Site</c> section. <paramref name="nowUtc"/> is the moment the security.txt expiry is
    /// compared with (the caller passes the start-up time; tests pass a fixed one).
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured value is malformed; the message names the setting.</exception>
    public static SiteSettings FromConfiguration(IConfiguration configuration, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(Section);

        string tag = Text(s, "GoogleTagId");
        if (tag.Length > 0 && !TagIdPattern.IsMatch(tag))
            throw Bad("GoogleTagId", $"'{tag}' is not a Google tag id (expected something like G-ABC123DEF4: a prefix, a dash, then letters, digits and dashes)");

        string script = AbsoluteUrl(s, "AnalyticsScriptUrl");
        string collector = AbsoluteUrl(s, "AnalyticsCollectorUrl");
        if (collector.Length > 0 && script.Length == 0)
            throw Bad("AnalyticsCollectorUrl", "is set but Site:AnalyticsScriptUrl is not; a collector without a script does nothing");
        string siteName = Text(s, "AnalyticsSiteName");
        if (siteName.Length == 0) siteName = DefaultAnalyticsSiteName;

        string orgName = Text(s, "OrganisationName");
        string orgUrl = AbsoluteUrl(s, "OrganisationUrl");
        string orgLogo = AbsoluteUrl(s, "OrganisationLogoUrl");
        if (orgName.Length == 0 && (orgUrl.Length > 0 || orgLogo.Length > 0))
            throw Bad("OrganisationName", "is required when Site:OrganisationUrl or Site:OrganisationLogoUrl is set");

        return new SiteSettings
        {
            GoogleTagId = tag,
            AnalyticsScriptUrl = script,
            AnalyticsCollectorUrl = collector,
            AnalyticsSiteName = siteName,
            ShareImageUrl = AbsoluteUrl(s, "ShareImageUrl"),
            OrganisationName = orgName,
            OrganisationUrl = orgUrl,
            OrganisationLogoUrl = orgLogo,
            FooterLinks = ReadFooterLinks(s),
            SecurityTxt = BuildSecurityTxt(s.GetSection("SecurityTxt"), nowUtc),
        };
    }

    /// <summary>
    /// <c>Site:FooterLinks</c> is a list (<c>Site__FooterLinks__0__Text</c>, <c>Site__FooterLinks__0__Url</c>, ...).
    /// Each entry needs both a text and an absolute URL.
    /// </summary>
    private static List<FooterLink> ReadFooterLinks(IConfigurationSection s)
    {
        var links = new List<FooterLink>();
        // GetChildren returns the list entries keyed "0", "1", ...; order them numerically so "10" follows "9".
        IEnumerable<IConfigurationSection> entries = s.GetSection("FooterLinks").GetChildren()
            .OrderBy(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : int.MaxValue)
            .ThenBy(c => c.Key, StringComparer.Ordinal);
        foreach (IConfigurationSection e in entries)
        {
            string text = Text(e, "Text");
            string url = AbsoluteUrl(e, "Url");
            if (text.Length == 0 || url.Length == 0)
                throw Bad($"FooterLinks:{e.Key}", "needs both Text and Url");
            links.Add(new FooterLink(text, url));
        }
        return links;
    }

    /// <summary>
    /// Build security.txt from <c>Site:SecurityTxt:Contact</c> (required to serve it), <c>Expires</c> (required by RFC
    /// 9116 section 2.5.5 whenever the file is served), optional <c>PreferredLanguages</c> and an optional one-line
    /// <c>Comment</c> written first as a "# " line. Null when no contact is configured.
    /// </summary>
    private static string? BuildSecurityTxt(IConfigurationSection s, DateTimeOffset nowUtc)
    {
        string contact = Text(s, "Contact");
        string expires = Text(s, "Expires");
        string comment = Text(s, "Comment");
        string languages = Text(s, "PreferredLanguages");

        if (contact.Length == 0)
        {
            // Nothing else may be set on its own: an expiry or comment without a contact would silently do nothing.
            if (expires.Length > 0 || comment.Length > 0 || languages.Length > 0)
                throw Bad("SecurityTxt:Contact", "is required when any other Site:SecurityTxt value is set");
            return null;
        }

        // RFC 9116 2.5.3: a Contact is a URI; the useful schemes are mailto:, https: and tel:.
        if (!contact.StartsWith("mailto:", StringComparison.Ordinal)
            && !contact.StartsWith("https://", StringComparison.Ordinal)
            && !contact.StartsWith("tel:", StringComparison.Ordinal))
            throw Bad("SecurityTxt:Contact", $"'{contact}' must start with mailto:, https:// or tel: (RFC 9116 section 2.5.3)");

        if (expires.Length == 0)
            throw Bad("SecurityTxt:Expires", "is required when Site:SecurityTxt:Contact is set (RFC 9116 section 2.5.5), e.g. 2027-01-31T23:59:59Z");
        if (!DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset when))
            throw Bad("SecurityTxt:Expires", $"'{expires}' is not a date and time (use ISO 8601, e.g. 2027-01-31T23:59:59Z)");
        // A stale contact is worse than none: RFC 9116 2.5.5 says an expired file should be treated as stale, so a
        // deployment must renew it rather than keep serving it.
        if (when <= nowUtc)
            throw Bad("SecurityTxt:Expires", $"{when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)} is already in the past; renew it");

        if (languages.Length == 0) languages = DefaultSecurityPreferredLanguages;

        var sb = new StringBuilder();
        if (comment.Length > 0) sb.Append("# ").Append(comment).Append("\n\n");
        sb.Append("Contact: ").Append(contact).Append('\n');
        sb.Append("Expires: ").Append(when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Preferred-Languages: ").Append(languages).Append('\n');
        return sb.ToString();
    }

    /// <summary>A trimmed text value ("" when unset), refused if it contains a control character (a line break in a
    /// security.txt field or an HTML attribute is never intended).</summary>
    private static string Text(IConfigurationSection s, string key)
    {
        string v = (s[key] ?? "").Trim();
        foreach (char ch in v)
            if (char.IsControl(ch))
                throw Bad(SettingName(s, key), "contains a control character (line break or tab); give it on one line");
        return v;
    }

    /// <summary>An absolute http(s) URL ("" when unset).</summary>
    private static string AbsoluteUrl(IConfigurationSection s, string key)
    {
        string v = Text(s, key);
        if (v.Length == 0) return v;
        if (!Uri.TryCreate(v, UriKind.Absolute, out Uri? u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw Bad(SettingName(s, key), $"'{v}' must be an absolute http:// or https:// URL");
        return v;
    }

    private static string SettingName(IConfigurationSection s, string key)
    {
        // s.Path is "Site", "Site:SecurityTxt" or "Site:FooterLinks:0"; drop the leading "Site:" for the message.
        string p = s.Path.Length > Section.Length ? s.Path[(Section.Length + 1)..] + ":" : "";
        return p + key;
    }

    private static InvalidOperationException Bad(string key, string why)
        => new($"GridSim.Web: configuration setting {Section}:{key} {why}.");
}
