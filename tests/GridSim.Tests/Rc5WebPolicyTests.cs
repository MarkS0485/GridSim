using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using GridSim.Web.Site;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 package TEL, 2.1.140 (B32): the web host's Google tag and analytics script settings are removed, every answer
/// carries a same-origin Content-Security-Policy, and the telemetry guard checks that policy and refuses a configured
/// third-party script origin at start-up.
/// </summary>
public sealed class Rc5WebPolicyTests
{
    private const string FullPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), relative)).Replace("\r\n", "\n");

    // The same policy function the host passes to the guard (Program.cs).
    private static TelemetryWebPolicy HostPolicy(IConfiguration c) => new(
        SecurityHeaders.Policy.Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith("connect-src", StringComparison.Ordinal)),
        SecurityHeaders.ConfiguredScriptOrigins(c));

    [Fact]
    public void Every_answer_carries_the_same_origin_policy_with_the_configured_framing()
    {
        Assert.Equal(FullPolicy, SecurityHeaders.Parse(null, null).ContentSecurityPolicy);
        Assert.Equal(FullPolicy.Replace("frame-ancestors 'none'", "frame-ancestors 'self' https://portal.example.com", StringComparison.Ordinal),
            SecurityHeaders.Parse("'self' https://portal.example.com", null).ContentSecurityPolicy);
        Assert.Equal("connect-src 'self'", SecurityHeaders.ConnectSrc);

        // Applied over a frame-ancestors-only policy (what Blazor writes on its pages), the full policy replaces it.
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary { ["Content-Security-Policy"] = "frame-ancestors 'none'" };
        SecurityHeaders.Parse(null, null).Apply(headers);
        Assert.Equal(FullPolicy, headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void The_guard_accepts_the_neutral_host_and_its_own_policy()
    {
        Assert.Null(TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(Config())));
        Assert.Null(TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(Config(("Site:ShareImageUrl", "https://www.example.com/og.png")))));
    }

    [Theory]
    [InlineData("Site:GoogleTagId", "G-TEST123456")]
    [InlineData("Site:AnalyticsScriptUrl", "https://stats.example.com/a.js")]
    [InlineData("Site:AnalyticsCollectorUrl", "https://stats.example.com")]
    [InlineData("Site:AnalyticsSiteName", "GridSim")]
    [InlineData("GridSim:ScriptCspSources", "https://cdn.example.com")]
    public void A_configured_third_party_script_origin_exits(string key, string value)
    {
        Assert.Equal($"a third-party script origin is configured ({key}={value})",
            TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(Config((key, value)))));
    }

    [Fact]
    public void The_web_check_fails_closed()
    {
        Assert.Equal("the telemetry guard itself failed (InvalidOperationException: no config)",
            TelemetryGuard.EvaluateWebPolicy(() => throw new InvalidOperationException("no config")));
        Assert.StartsWith("the telemetry guard itself failed (ArgumentNullException:", TelemetryGuard.EvaluateWebPolicy(null!) ?? "", StringComparison.Ordinal);
        Assert.StartsWith("the telemetry guard itself failed (ArgumentNullException:", TelemetryGuard.EvaluateWebPolicy(() => null!) ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GoogleTagId", "G-ABC123DEF4")]
    [InlineData("AnalyticsScriptUrl", "https://stats.example.com/a.js")]
    [InlineData("AnalyticsCollectorUrl", "https://stats.example.com")]
    [InlineData("AnalyticsSiteName", "GridSim")]
    public void Site_settings_refuse_each_removed_setting_by_name(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SiteSettings.FromConfiguration(Config(("Site:" + key, value)), new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal($"GridSim.Web: configuration setting Site:{key} was removed in 2.1.140: the web app loads no script from another origin. Remove the setting.", ex.Message);
    }

    [Fact]
    public void The_host_runs_the_web_check_straight_after_reading_its_configuration()
    {
        string src = Read("src/GridSim.Web/Program.cs");
        int builder = src.IndexOf("WebApplicationBuilder builder = WebApplication.CreateBuilder(args);", StringComparison.Ordinal);
        int check = src.IndexOf("TelemetryGuard.EnforceWebPolicy(\"GridSim.Web\"", StringComparison.Ordinal);
        Assert.True(builder > 0 && check > builder, "EnforceWebPolicy is not after CreateBuilder");
        // Nothing but comments between the two.
        string between = src[(src.IndexOf('\n', builder) + 1)..src.LastIndexOf('\n', check)];
        Assert.All(between.Split('\n'), l => Assert.True(l.Trim().Length == 0 || l.TrimStart().StartsWith("//", StringComparison.Ordinal), l));
        Assert.Contains("SecurityHeaders.ConfiguredScriptOrigins(builder.Configuration)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pages_have_no_analytics_no_inline_script_and_no_inline_event_handler()
    {
        string web = Path.Combine(CliProcess.Root(), "src", "GridSim.Web");
        foreach (string f in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            string razor = File.ReadAllText(f);
            string name = Path.GetFileName(f);
            foreach (string gone in new[] { "gtag", "googletagmanager", "AnalyticsScriptUrl", "tsgbAnalytics", "pageView", "GoogleTag" })
                Assert.DoesNotContain(gone, razor, StringComparison.OrdinalIgnoreCase);
            // A script element either loads a file from this app or is a JSON-LD data block (not executed).
            foreach (Match m in Regex.Matches(razor, @"<script\b([^>]*)>"))
            {
                string attrs = m.Groups[1].Value;
                Assert.True(Regex.IsMatch(attrs, @"\bsrc=""(_framework|js)/[^""]+\.js""") || attrs.Contains("application/ld+json", StringComparison.Ordinal),
                    $"{name} has an inline or external script: {m.Value}");
            }
            // An HTML inline event handler (onclick=, onload=, ...) would need 'unsafe-inline' in script-src; Blazor's
            // @onclick handlers are not inline attributes.
            Assert.DoesNotMatch(new Regex(@"(?<![@\w-])on[a-z]+\s*=\s*[""']"), razor);
            Assert.DoesNotContain("javascript:", razor, StringComparison.OrdinalIgnoreCase);
        }
        foreach (string js in Directory.EnumerateFiles(Path.Combine(web, "wwwroot", "js"), "*.js"))
        {
            string code = File.ReadAllText(js);
            Assert.DoesNotMatch(new Regex(@"\beval\s*\(|new\s+Function\s*\("), code);
        }
    }

    [Fact]
    public void The_deployment_files_no_longer_map_the_removed_settings()
    {
        string compose = Read("compose.yml");
        foreach (string gone in new[] { "GRIDSIM_GOOGLE_TAG_ID", "GRIDSIM_ANALYTICS_SCRIPT_URL", "GRIDSIM_ANALYTICS_COLLECTOR", "Site__GoogleTagId", "Site__Analytics" })
            Assert.DoesNotContain(gone, compose, StringComparison.Ordinal);
    }
}
