using GridSim.Web.Site;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N086 / N087: the deployment identity of the web app (analytics, share-card metadata, security.txt, footer links,
/// data-protection application name) is configuration with neutral defaults, checked loudly at start-up. These tests
/// drive <see cref="SiteSettings.FromConfiguration"/> directly; WebHostTests checks the same through the real host.
/// </summary>
public sealed class SiteSettingsTests
{
    // A fixed "now" so the expiry checks never depend on the day the suite runs.
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static SiteSettings Read(params (string Key, string Value)[] values)
    {
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        return SiteSettings.FromConfiguration(c, Now);
    }

    private static string Refused(params (string Key, string Value)[] values)
        => Assert.Throws<InvalidOperationException>(() => Read(values)).Message;

    [Fact]
    public void Nothing_configured_is_fully_neutral()
    {
        SiteSettings s = Read();
        Assert.Equal("", s.GoogleTagId);
        Assert.Null(s.GoogleTagBootstrap);
        Assert.Equal("", s.AnalyticsScriptUrl);
        Assert.Equal("", s.AnalyticsCollectorUrl);
        Assert.False(s.HasAnalytics);
        Assert.Equal("", s.ShareImageUrl);
        Assert.Null(s.OrganisationJsonLd);
        Assert.Empty(s.FooterLinks);
        Assert.Null(s.SecurityTxt);
    }

    [Fact]
    public void Neutral_application_name_is_the_product_name()
    {
        // N087: before, an unset Auth:ApplicationName fell back to one estate's name in code.
        Assert.Equal("GridSim", SiteSettings.DefaultApplicationName);
    }

    [Fact]
    public void Configured_values_are_carried_through()
    {
        SiteSettings s = Read(
            ("Site:GoogleTagId", "G-ABC123DEF4"),
            ("Site:AnalyticsScriptUrl", "https://stats.example.com/a.js"),
            ("Site:AnalyticsCollectorUrl", "https://stats.example.com"),
            ("Site:ShareImageUrl", "https://www.example.com/og.png"),
            ("Site:OrganisationName", "Example Org"),
            ("Site:OrganisationUrl", "https://www.example.com"),
            ("Site:FooterLinks:0:Text", "Source"),
            ("Site:FooterLinks:0:Url", "https://code.example.com/gridsim"),
            ("Site:FooterLinks:1:Text", "News"),
            ("Site:FooterLinks:1:Url", "https://news.example.com/"));
        Assert.True(s.HasAnalytics);
        Assert.Contains("gtag('config', 'G-ABC123DEF4');", s.GoogleTagBootstrap, StringComparison.Ordinal);
        Assert.Equal("https://stats.example.com/a.js", s.AnalyticsScriptUrl);
        Assert.Equal("https://stats.example.com", s.AnalyticsCollectorUrl);
        Assert.Equal("GridSim", s.AnalyticsSiteName);
        Assert.Equal("https://www.example.com/og.png", s.ShareImageUrl);
        Assert.Equal("{\"@context\":\"https://schema.org\",\"@type\":\"Organization\",\"name\":\"Example Org\",\"url\":\"https://www.example.com\"}",
            s.OrganisationJsonLd);
        Assert.Equal(new[] { new SiteSettings.FooterLink("Source", "https://code.example.com/gridsim"), new SiteSettings.FooterLink("News", "https://news.example.com/") },
            s.FooterLinks);
    }

    [Fact]
    public void Footer_links_keep_numeric_order_past_nine()
    {
        var values = new List<(string, string)>();
        for (int i = 0; i < 11; i++)
        {
            values.Add(($"Site:FooterLinks:{i}:Text", $"L{i}"));
            values.Add(($"Site:FooterLinks:{i}:Url", $"https://example.com/{i}"));
        }
        SiteSettings s = Read(values.ToArray());
        Assert.Equal(Enumerable.Range(0, 11).Select(i => $"L{i}"), s.FooterLinks.Select(l => l.Text));
    }

    [Fact]
    public void Organisation_json_cannot_close_its_script_element()
    {
        SiteSettings s = Read(("Site:OrganisationName", "</script><script>alert(1)</script>"));
        Assert.DoesNotContain("<", s.OrganisationJsonLd, StringComparison.Ordinal);
        Assert.DoesNotContain(">", s.OrganisationJsonLd, StringComparison.Ordinal);
    }

    [Fact]
    public void Security_txt_is_built_from_contact_and_expiry()
    {
        SiteSettings s = Read(
            ("Site:SecurityTxt:Contact", "mailto:security@example.com"),
            ("Site:SecurityTxt:Expires", "2027-08-29T23:59:59Z"),
            ("Site:SecurityTxt:Comment", "Example security team"));
        Assert.Equal(
            "# Example security team\n\nContact: mailto:security@example.com\nExpires: 2027-08-29T23:59:59.000Z\nPreferred-Languages: en\n",
            s.SecurityTxt);

        SiteSettings plain = Read(
            ("Site:SecurityTxt:Contact", "https://www.example.com/security"),
            ("Site:SecurityTxt:Expires", "2027-01-31T23:59:59Z"),
            ("Site:SecurityTxt:PreferredLanguages", "en, cy"));
        Assert.Equal(
            "Contact: https://www.example.com/security\nExpires: 2027-01-31T23:59:59.000Z\nPreferred-Languages: en, cy\n",
            plain.SecurityTxt);
    }

    [Fact]
    public void Malformed_values_stop_start_up_naming_the_setting()
    {
        Assert.Contains("Site:GoogleTagId", Refused(("Site:GoogleTagId", "G-1'); alert(1); ('")), StringComparison.Ordinal);
        Assert.Contains("Site:GoogleTagId", Refused(("Site:GoogleTagId", "not-a-tag")), StringComparison.Ordinal);
        Assert.Contains("Site:AnalyticsScriptUrl", Refused(("Site:AnalyticsScriptUrl", "/js/a.js")), StringComparison.Ordinal);
        Assert.Contains("Site:AnalyticsScriptUrl", Refused(("Site:AnalyticsScriptUrl", "javascript:alert(1)")), StringComparison.Ordinal);
        Assert.Contains("Site:AnalyticsCollectorUrl", Refused(("Site:AnalyticsCollectorUrl", "https://stats.example.com")), StringComparison.Ordinal);
        Assert.Contains("Site:ShareImageUrl", Refused(("Site:ShareImageUrl", "images/og.png")), StringComparison.Ordinal);
        Assert.Contains("Site:OrganisationName", Refused(("Site:OrganisationUrl", "https://www.example.com")), StringComparison.Ordinal);
        Assert.Contains("Site:FooterLinks:0", Refused(("Site:FooterLinks:0:Text", "Source")), StringComparison.Ordinal);
        Assert.Contains("Site:FooterLinks:0:Url", Refused(("Site:FooterLinks:0:Text", "Source"), ("Site:FooterLinks:0:Url", "code.example.com")), StringComparison.Ordinal);
    }

    [Fact]
    public void Security_txt_needs_a_valid_contact_and_a_future_expiry()
    {
        Assert.Contains("Site:SecurityTxt:Expires",
            Refused(("Site:SecurityTxt:Contact", "mailto:security@example.com")), StringComparison.Ordinal);
        Assert.Contains("Site:SecurityTxt:Contact",
            Refused(("Site:SecurityTxt:Contact", "security@example.com"), ("Site:SecurityTxt:Expires", "2027-01-01T00:00:00Z")), StringComparison.Ordinal);
        Assert.Contains("Site:SecurityTxt:Contact",
            Refused(("Site:SecurityTxt:Expires", "2027-01-01T00:00:00Z")), StringComparison.Ordinal);
        Assert.Contains("not a date",
            Refused(("Site:SecurityTxt:Contact", "mailto:s@example.com"), ("Site:SecurityTxt:Expires", "next year")), StringComparison.Ordinal);
        // Exactly "now" is already stale (the check is expiry <= now).
        Assert.Contains("already in the past",
            Refused(("Site:SecurityTxt:Contact", "mailto:s@example.com"), ("Site:SecurityTxt:Expires", "2026-10-04T12:00:00Z")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_break_in_any_value_is_refused()
    {
        Assert.Contains("control character",
            Refused(("Site:SecurityTxt:Contact", "mailto:s@example.com\nContact: mailto:evil@example.com"), ("Site:SecurityTxt:Expires", "2027-01-01T00:00:00Z")),
            StringComparison.Ordinal);
        Assert.Contains("Site:SecurityTxt:Comment",
            Refused(("Site:SecurityTxt:Contact", "mailto:s@example.com"), ("Site:SecurityTxt:Expires", "2027-01-01T00:00:00Z"), ("Site:SecurityTxt:Comment", "a\tb")),
            StringComparison.Ordinal);
    }
}
