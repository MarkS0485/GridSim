using System.Security.Cryptography;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Compliance.GridCode;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>REVIEW-FIX-3 (2.1.087): the AC-SCOPF at ratings it cannot meet, and the grid-code requirements hash on every OS.</summary>
public sealed class Rc5ReviewFix3Tests
{
    /// <summary>Technical chapter 29 says the AC-SCOPF fails loud when the ratings cannot be met. Case9 with every RateA scaled
    /// by 0.7 is AC-infeasible: after the outage of branch 5 (7-8), bus 7 is fed by branch 4 (6-7) alone, so the power entering
    /// branch 4 at bus 7 is exactly bus 7's load, |100 + j35| = 105.948 MVA, above 0.7 x 150 = 105 MVA whatever the dispatch.
    /// The result must not be an optimum.</summary>
    [Fact]
    public void AcScopf_on_case9_at_0_7_ratings_is_not_optimal()
    {
        GridModel src = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        GridModel m = src.CopyWith(branches: src.Branches.Select(b => b with { RateAMva = b.RateAMva * 0.7 }).ToArray());
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions());
        Assert.False(r.Optimal, r.Message);
        Assert.NotEqual(AcScopfStatus.Optimal, r.Status);
    }

    /// <summary>The requirements hash in the grid-code result is the sha256 of the embedded file's bytes. The files are stored
    /// with no line-ending conversion (.gitattributes `-text`, as models/ and the bundled-data files are), so the bytes, and
    /// the hash, are the same on a Windows and a Linux checkout. Proven here by: no CR in either file, the hash pinned, and a
    /// CRLF variant hashing differently (so a converted checkout would be caught by the pin).</summary>
    [Theory]
    [InlineData("gb-ecc-issue6-rev33", "594dc8ff26abf776fcd7e601235ee0e969453424017eb3edb6331f5725e90083")]
    [InlineData("eu-rfg-2016-631", "e41e739fe378c4510aa5082671ef8bc775c19866a974081ea1d285bb4eedf2fb")]
    public void The_requirements_bytes_are_fixed_and_their_hash_is_the_same_on_every_os(string id, string sha256)
    {
        byte[] bytes = GridCodeRequirements.Bytes(id);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        byte[] crlf = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.NotEqual(sha256, Convert.ToHexStringLower(SHA256.HashData(crlf)));
    }

    /// <summary>.gitattributes marks the requirement files `-text` (found by walking up from the test binaries; a source tree
    /// is not shipped with a packaged test run, in which case the byte pin above is the evidence).</summary>
    [Fact]
    public void Gitattributes_stores_the_requirement_files_without_line_ending_conversion()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, ".gitattributes"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return;
        string[] lines = File.ReadAllLines(Path.Combine(dir, ".gitattributes"));
        Assert.Contains(lines, l => l.Trim() == "src/GridSim.Core/Compliance/GridCode/Requirements/*.json -text");
    }
}
