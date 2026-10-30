using System;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Round-two fixes on the three-phase feeder path (October 2026 review): GR-580 (a line code's series r and x must be
/// symmetric to rounding) and GR-108 (a branch written with its From end downstream reports its current toward its To
/// bus, as documented, not the parent-to-child current).
/// </summary>
public class GrRound2DistributionTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    // The shipped IEEE 4-node feeder (gy-gy step-down, unbalanced) as a mutable JSON tree.
    private static JsonNode Ieee4()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "data", "dist3ph", "ieee4-gyGy-stepdown-unbalanced.json")))!;

    [Fact]
    public void Gr580_the_shipped_line_code_asymmetric_in_the_last_bit_is_still_accepted()
    {
        // r[0][1] and r[1][0] differ by about 2e-16 relative in the shipped file: well inside the 1e-9 rounding allowance.
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(Ieee4().ToJsonString(), "ieee4.json");
        Assert.Equal(4, f.Model.Buses.Count);
    }

    [Theory]
    [InlineData("r")]
    [InlineData("x")]
    public void Gr580_a_line_code_with_a_mistyped_mutual_term_is_refused_naming_the_entry(string member)
    {
        JsonNode root = Ieee4();
        JsonNode m = root["lineCodes"]![0]![member]!;
        // A transposed or mistyped mutual term: [0][2] doubled, so it no longer matches [2][0].
        m[0]![2] = 2.0 * m[0]![2]!.GetValue<double>();
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(root.ToJsonString(), "ieee4.json"));
        Assert.StartsWith($"ieee4.json: $.lineCodes[0].{member}[0][2]:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must be symmetric", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr108_a_line_written_from_its_downstream_end_reports_its_current_toward_its_To_bus()
    {
        JsonNode forward = Ieee4();
        JsonNode reversed = Ieee4();
        // line3-4 written the other way round: From is bus 4 (downstream), To is bus 3 (upstream).
        reversed["lines"]![1]!["from"] = 4;
        reversed["lines"]![1]!["to"] = 3;

        MultiPhaseFeeder ff = MultiPhaseJsonLoader.Parse(forward.ToJsonString(), "fwd.json");
        MultiPhaseFeeder fr = MultiPhaseJsonLoader.Parse(reversed.ToJsonString(), "rev.json");
        MultiPhaseResult a = new ThreePhaseSweep().Solve(ff.Model);
        MultiPhaseResult b = new ThreePhaseSweep().Solve(fr.Model);
        Assert.True(a.Converged && b.Converged, $"status {a.Status} / {b.Status}");

        // The branch is the same impedance either way round, so the solved voltages are the same bits.
        for (int i = 0; i < a.V.Length; i++)
            for (int p = 0; p < 3; p++)
                Assert.Equal(a.V[i][p], b.V[i][p]);

        // Locate line3-4 in each model by its end buses (the loader keeps lines before transformers, but do not rely on it).
        int ka = BranchIndex(ff.Model, 3, 4), kb = BranchIndex(fr.Model, 4, 3);
        for (int p = 0; p < 3; p++)
        {
            // Forward: toward bus 4 (its To bus). Reversed: toward bus 3 (its To bus), the exact negation.
            Assert.True(a.BranchCurrents![ka][p].Magnitude > 0.0, "the loaded line carries current");
            Assert.Equal(-a.BranchCurrents[ka][p], b.BranchCurrents![kb][p]);
        }
    }

    private static int BranchIndex(MultiPhaseModel m, int from, int to)
    {
        for (int k = 0; k < m.Branches.Count; k++)
            if (m.Branches[k].From == from && m.Branches[k].To == to) return k;
        throw new InvalidOperationException($"no branch {from}-{to}");
    }
}
