using GridSim.Web.Site;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GridSim.Tests;

/// <summary>2.1.074 RC-4-Fixes: the opt-in script-src policy. Off (the default) adds nothing. That a browser renders the pages under it is unverified.</summary>
public sealed class ScriptCspPolicyTests
{
    [Fact]
    public void Off_is_the_default_and_adds_no_header()
    {
        var headers = new HeaderDictionary { ["Content-Security-Policy"] = "frame-ancestors 'none'" };
        ScriptCspPolicy.Parse(null, null).Apply(headers);
        ScriptCspPolicy.Parse("off", "https://a.example").Apply(headers);
        Assert.Equal("frame-ancestors 'none'", headers["Content-Security-Policy"].ToString());
        Assert.False(headers.ContainsKey("Content-Security-Policy-Report-Only"));
    }

    [Fact]
    public void Report_only_blocks_nothing_and_leaves_the_enforced_header_alone()
    {
        var headers = new HeaderDictionary { ["Content-Security-Policy"] = "frame-ancestors 'none'" };
        ScriptCspPolicy.Parse("report-only", null).Apply(headers);
        Assert.Equal("script-src 'self'", headers["Content-Security-Policy-Report-Only"].ToString());
        Assert.Equal("frame-ancestors 'none'", headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void Enforce_appends_the_directive_once_with_the_extra_origins()
    {
        var headers = new HeaderDictionary { ["Content-Security-Policy"] = "frame-ancestors 'none'" };
        ScriptCspPolicy p = ScriptCspPolicy.Parse("Enforce", "https://stats.example.com");
        p.Apply(headers);
        p.Apply(headers);
        Assert.Equal("frame-ancestors 'none'; script-src 'self' https://stats.example.com", headers["Content-Security-Policy"].ToString());
        var bare = new HeaderDictionary();
        p.Apply(bare);
        Assert.Equal("script-src 'self' https://stats.example.com", bare["Content-Security-Policy"].ToString());
    }

    [Theory]
    [InlineData("maybe", null)]
    [InlineData("enforce", "'unsafe-inline'")]
    [InlineData("enforce", "https://a.example; img-src *")]
    [InlineData("enforce", "stats.example.com")]
    public void A_malformed_setting_stops_start_up_naming_it(string mode, string? sources)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptCspPolicy.Parse(mode, sources));
        Assert.Contains("GridSim:ScriptCsp", ex.Message, StringComparison.Ordinal);
    }
}
