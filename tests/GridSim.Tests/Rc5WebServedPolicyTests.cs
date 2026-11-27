using GridSim.Core.Diagnostics;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The telemetry guard's web check reads the Content-Security-Policy the host actually serves, composed from its
/// configuration the way the response-starting callback composes it (the security headers, then the script policy),
/// not a constant. So a change to how the header is built, or a policy that loosens connect-src, makes the host exit.
/// </summary>
public sealed class Rc5WebServedPolicyTests
{
    private const string FullPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // Exactly what Program.cs passes to TelemetryGuard.EnforceWebPolicy.
    private static TelemetryWebPolicy HostPolicy(IConfiguration c) => new(
        SecurityHeaders.ConnectSrcOf(SecurityHeaders.ServedPolicy(c)),
        SecurityHeaders.ConfiguredScriptOrigins(c));

    [Fact]
    public void The_host_passes_the_composed_policy_not_the_constant()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Web", "Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("GridSim.Web.Site.SecurityHeaders.ConnectSrcOf(GridSim.Web.Site.SecurityHeaders.ServedPolicy(builder.Configuration))", src, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityHeaders.Policy.Split", src, StringComparison.Ordinal);
        // The response-starting callback composes the header in the same order ServedPolicy does.
        int apply = src.IndexOf("securityHeaders.Apply(ctx.Response.Headers);", StringComparison.Ordinal);
        int script = src.IndexOf("scriptCsp.Apply(ctx.Response.Headers);", StringComparison.Ordinal);
        Assert.True(apply > 0 && script > apply, "the host no longer applies the security headers and then the script policy");
    }

    [Fact]
    public void The_default_served_policy_is_the_full_same_origin_policy_and_passes()
    {
        Assert.Equal(FullPolicy, SecurityHeaders.ServedPolicy(Config()));
        Assert.Equal("connect-src 'self'", SecurityHeaders.ConnectSrcOf(SecurityHeaders.ServedPolicy(Config())));
        Assert.Null(TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(Config())));
    }

    [Theory]
    [InlineData("report-only")]
    [InlineData("enforce")]
    [InlineData("off")]
    public void Every_script_policy_mode_keeps_connect_src_self_and_passes(string mode)
    {
        IConfiguration c = Config(("GridSim:ScriptCsp", mode), ("GridSim:FrameAncestors", "'self' https://portal.example.com"));
        Assert.Equal(FullPolicy.Replace("frame-ancestors 'none'", "frame-ancestors 'self' https://portal.example.com", StringComparison.Ordinal),
            SecurityHeaders.ServedPolicy(c));
        Assert.Null(TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(c)));
    }

    [Theory]
    [InlineData("default-src 'self'; connect-src 'self' https://collector.example.com; frame-ancestors 'none'", "connect-src 'self' https://collector.example.com")]
    [InlineData("default-src 'self'; connect-src *", "connect-src *")]
    [InlineData("default-src 'self'; connect-src https:", "connect-src https:")]
    [InlineData("default-src 'self'; connect-src 'self'; connect-src *", "connect-src 'self'; connect-src *")]
    [InlineData("default-src 'self'; script-src 'self'", "")]
    [InlineData("", "")]
    public void A_loosened_served_policy_exits(string served, string reported)
    {
        string? reason = TelemetryGuard.EvaluateWebPolicy(() => new TelemetryWebPolicy(SecurityHeaders.ConnectSrcOf(served), []));
        Assert.Equal($"the Content-Security-Policy connect-src is '{reported}', not connect-src 'self'", reason);
    }

    [Fact]
    public void The_connect_src_directive_is_found_by_name_whatever_its_place_and_case()
    {
        Assert.Equal("connect-src 'self'", SecurityHeaders.ConnectSrcOf("connect-src 'self'"));
        Assert.Equal("connect-src 'self'", SecurityHeaders.ConnectSrcOf(" img-src 'self' ;connect-src 'self';"));
        Assert.Equal("CONNECT-SRC 'self'", SecurityHeaders.ConnectSrcOf("default-src 'self'; CONNECT-SRC 'self'"));
        Assert.Null(SecurityHeaders.ConnectSrcOf("default-src 'self'; connect-srcx 'self'"));
        Assert.Null(SecurityHeaders.ConnectSrcOf(null));
        // Upper case is not the exact directive the guard asks for, so it refuses (fail closed).
        Assert.NotNull(TelemetryGuard.CheckWebPolicy(SecurityHeaders.ConnectSrcOf("CONNECT-SRC 'self'"), []));
    }

    [Theory]
    [InlineData("GridSim:FrameAncestors", "'none'; connect-src *")]
    [InlineData("GridSim:ScriptCsp", "sometimes")]
    [InlineData("GridSim:Hsts", "maybe")]
    public void A_setting_that_stops_the_policy_being_composed_exits_naming_it(string key, string value)
    {
        string? reason = TelemetryGuard.EvaluateWebPolicy(() => HostPolicy(Config((key, value))));
        Assert.NotNull(reason);
        Assert.StartsWith("the telemetry guard itself failed (InvalidOperationException: The setting " + key + " ", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("enforce", "'self' https://portal.example.com")]
    [InlineData("report-only", null)]
    public async Task The_composed_policy_is_the_header_the_running_host_sends(string? mode, string? frameAncestors)
    {
        var settings = new List<(string Key, string Value)>();
        if (mode is not null) settings.Add(("GridSim:ScriptCsp", mode));
        if (frameAncestors is not null) settings.Add(("GridSim:FrameAncestors", frameAncestors));
        string expected = SecurityHeaders.ServedPolicy(Config([.. settings]));

        string keys = TestTemp.Under("gridsim-served-policy-keys-" + Guid.NewGuid().ToString("N"));
        await using WebApplicationFactory<Program> f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            foreach ((string k, string v) in settings) b.UseSetting(k, v);
        });
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        foreach (string path in new[] { "/", "/css/app.css", "/no-such-page" })
        {
            using HttpResponseMessage r = await c.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(expected, string.Join(",", r.Headers.GetValues("Content-Security-Policy")));
        }
    }
}
