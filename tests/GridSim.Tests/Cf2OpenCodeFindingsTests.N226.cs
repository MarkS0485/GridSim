using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N226 (CF2, October 2026): forwarded headers are believed only from the proxies <c>GridSim:TrustedProxies</c> names
/// (default loopback), not from any caller; a malformed value stops start-up naming the setting. The middleware tests
/// run the real ASP.NET forwarded-headers middleware with the options the app builds, from a given remote address.
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    private static string[] N226Nets(TrustedProxies t) => t.Networks.Select(n => n.ToString()).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loopback")]
    [InlineData(" LoopBack ")]
    public void N226_unset_or_loopback_trusts_exactly_the_loopback_networks(string? value)
    {
        TrustedProxies t = TrustedProxies.Parse(value);
        Assert.False(t.TrustAll);
        Assert.Equal(new[] { "127.0.0.0/8", "::ffff:127.0.0.0/104", "::1/128" }, N226Nets(t));
        Assert.Equal("loopback (127.0.0.0/8, ::1/128)", t.Description);
        Assert.Equal("loopback", TrustedProxies.DefaultValue);
        Assert.Equal("GridSim:TrustedProxies", TrustedProxies.ConfigurationKey);
    }

    [Fact]
    public void N226_private_addresses_and_networks_expand_in_order_without_duplicates()
    {
        TrustedProxies t = TrustedProxies.Parse("loopback, private, 203.0.113.7, 2001:db8::/32, 127.0.0.0/8");
        Assert.False(t.TrustAll);
        Assert.Equal(new[]
        {
            "127.0.0.0/8", "::ffff:127.0.0.0/104", "::1/128",
            "10.0.0.0/8", "::ffff:10.0.0.0/104", "172.16.0.0/12", "::ffff:172.16.0.0/108",
            "192.168.0.0/16", "::ffff:192.168.0.0/112", "fc00::/7",
            "203.0.113.7/32", "::ffff:203.0.113.7/128", "2001:db8::/32",
        }, N226Nets(t));
    }

    [Fact]
    public void N226_any_alone_trusts_every_caller()
    {
        TrustedProxies t = TrustedProxies.Parse("ANY");
        Assert.True(t.TrustAll);
        Assert.Empty(t.Networks);
        var o = new ForwardedHeadersOptions();
        t.ApplyTo(o);
        Assert.Empty(o.KnownProxies);
        Assert.Empty(o.KnownIPNetworks);
    }

    [Theory]
    [InlineData("any,loopback")]          // any must stand alone
    [InlineData("loopback,,private")]     // empty entry
    [InlineData("loopback,")]             // trailing comma
    [InlineData("localhost")]             // a host name is not an address
    [InlineData("10")]                    // IPAddress shorthand, refused
    [InlineData("1.2.3")]
    [InlineData("300.1.1.1")]
    [InlineData("10.1.2.3/8")]            // bits set past the prefix
    [InlineData("10.0.0.0/33")]           // prefix out of range
    [InlineData("10.0.0.0/")]
    [InlineData("fe80::1%3")]             // zone index
    public void N226_a_malformed_value_is_refused_naming_the_setting(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse(value));
        Assert.Contains("GridSim:TrustedProxies", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GridSim__TrustedProxies", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N226_from_configuration_reads_the_key()
    {
        IConfiguration c = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("GridSim:TrustedProxies", "198.51.100.1") })
            .Build();
        Assert.Equal(new[] { "198.51.100.1/32", "::ffff:198.51.100.1/128" }, N226Nets(TrustedProxies.FromConfiguration(c)));
        Assert.Equal("loopback (127.0.0.0/8, ::1/128)", TrustedProxies.FromConfiguration(new ConfigurationBuilder().Build()).Description);
    }

    /// <summary>Run the real forwarded-headers middleware, configured as the app configures it, on one request from
    /// <paramref name="remote"/> carrying X-Forwarded-Proto: https and X-Forwarded-For: 192.0.2.44. Returns the scheme
    /// and remote address the app then sees.</summary>
    private static async Task<(string Scheme, string Remote)> N226Through(string setting, string remote)
    {
        var o = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        TrustedProxies.Parse(setting).ApplyTo(o);
        var mw = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(o));
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";
        ctx.Request.Headers["X-Forwarded-For"] = "192.0.2.44";
        await mw.Invoke(ctx);
        return (ctx.Request.Scheme, ctx.Connection.RemoteIpAddress!.ToString());
    }

    [Theory]
    [InlineData("loopback", "127.0.0.1", true)]
    [InlineData("loopback", "::1", true)]
    [InlineData("loopback", "::ffff:127.0.0.1", true)]     // IPv4 caller on a dual-stack listener
    [InlineData("loopback", "203.0.113.9", false)]         // a stranger cannot claim https any more
    [InlineData("loopback", "::ffff:203.0.113.9", false)]
    [InlineData("loopback,private", "::ffff:172.18.0.1", true)]   // a container bridge gateway
    [InlineData("loopback,private", "203.0.113.9", false)]
    [InlineData("203.0.113.9", "203.0.113.9", true)]
    [InlineData("any", "203.0.113.9", true)]               // the pre-N226 behaviour, now explicit
    public async Task N226_the_middleware_believes_the_headers_only_from_a_trusted_proxy(string setting, string remote, bool believed)
    {
        (string scheme, string seen) = await N226Through(setting, remote);
        Assert.Equal(believed ? "https" : "http", scheme);
        if (believed) Assert.Equal("192.0.2.44", seen);
        else Assert.Equal(IPAddress.Parse(remote).ToString(), seen);
    }

    [Fact]
    public void N226_a_malformed_setting_stops_the_web_host_at_start_up()
    {
        string keys = Path.Combine(Path.GetTempPath(), "gridsim-n226-keys-" + Guid.NewGuid().ToString("N"));
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", keys);
            b.UseSetting("GridSim:TrustedProxies", "any,loopback");
        });
        var startup = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("GridSim:TrustedProxies", startup.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void N226_compose_trusts_loopback_and_private_and_the_web_host_no_longer_clears_the_lists_by_hand()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string compose = File.ReadAllText(Path.Combine(dir!.FullName, "compose.yml"));
        Assert.Contains("GridSim__TrustedProxies: \"${GRIDSIM_TRUSTED_PROXIES:-loopback,private}\"", compose, StringComparison.Ordinal);
        string program = File.ReadAllText(Path.Combine(dir.FullName, "src", "GridSim.Web", "Program.cs"));
        Assert.Contains("trustedProxies.ApplyTo(fwd);", program, StringComparison.Ordinal);
        Assert.DoesNotContain("fwd.KnownIPNetworks.Clear();", program, StringComparison.Ordinal);
        Assert.Contains("GridSim:TrustedProxies", File.ReadAllText(Path.Combine(dir.FullName, "docs", "developer", "deploy", "README.md")), StringComparison.Ordinal);
    }
}
