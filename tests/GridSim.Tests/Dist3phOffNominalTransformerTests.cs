using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 1.95.254 (N168): off-nominal transformer ratios in the three-phase feeders. A transformer whose rated kV differ from its
/// bus kV bases loads with the per-unit ideal ratio t = (base_from / rated_from)(rated_to / base_to) and its nameplate
/// impedance moved to the To bus base by (rated_to / base_to)^2. Proved by per-unit invariance: the same physical feeder,
/// described with the LV buses on the transformer's rated kV (rated ratio) and on another base (off-nominal), gives the
/// same volts and degrees at every node within 1e-9 relative (the sweep converges to 1e-10 pu), for every connection kind.
/// </summary>
public class Dist3phOffNominalTransformerTests
{
    private static string Text(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return File.ReadAllText(Path.Combine(dir!.FullName, "data", "dist3ph", name));
    }

    // The feeder with its LV buses on lvBaseKv and the transformer rated 12.47 / lvRatedKv.
    private static MultiPhaseFeeder Variant(string file, string lvKvText, double lvBaseKv, double lvRatedKv) =>
        MultiPhaseJsonLoader.Parse(Text(file)
            .Replace($"{{ \"id\": 3, \"baseKvLL\": {lvKvText} }}", $"{{ \"id\": 3, \"baseKvLL\": {lvBaseKv.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} }}", StringComparison.Ordinal)
            .Replace($"{{ \"id\": 4, \"baseKvLL\": {lvKvText} }}", $"{{ \"id\": 4, \"baseKvLL\": {lvBaseKv.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} }}", StringComparison.Ordinal)
            .Replace($"\"kvLLTo\": {lvKvText}", $"\"kvLLTo\": {lvRatedKv.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.Ordinal), file);

    [Theory]
    [InlineData("ieee4-gyGy-stepdown-unbalanced.json", "4.16")]
    [InlineData("ieee4-dGy-stepdown-unbalanced.json", "4.16")]
    [InlineData("ieee4-dGy-stepup-unbalanced.json", "24.9")]
    [InlineData("ieee4-yD-stepdown-unbalanced.json", "4.16")]
    [InlineData("ieee4-dD-stepdown-unbalanced.json", "4.16")]
    public void An_off_nominal_description_gives_the_same_physical_volts_as_the_rated_one(string file, string lvKvText)
    {
        double lv = double.Parse(lvKvText, System.Globalization.CultureInfo.InvariantCulture);
        double rated = lv * 1.025;                                    // a +2.5 % tap on the LV winding
        MultiPhaseFeeder onRated = Variant(file, lvKvText, rated, rated);   // LV buses on the rated kV: rated ratio
        MultiPhaseFeeder offNominal = Variant(file, lvKvText, lv, rated);   // LV buses on the old base: off-nominal
        Assert.DoesNotContain(onRated.Model.Branches, b => b.TransformerRatio != 1.0 || b.RegulatorRatio is not null);
        MultiPhaseResult a = new ThreePhaseSweep().Solve(onRated.Model);
        MultiPhaseResult b = new ThreePhaseSweep().Solve(offNominal.Model);
        Assert.True(a.Converged && b.Converged);
        for (int i = 0; i < a.BusIds.Count; i++)
        {
            int bus = a.BusIds[i];
            for (int p = 0; p < 3; p++)
            {
                Complex va = a.V[i][p] * onRated.VoltBaseLN(bus), vb = b.V[i][p] * offNominal.VoltBaseLN(bus);
                Assert.True((va - vb).Magnitude <= 1e-9 * va.Magnitude, $"bus {bus} phase {p}: {va} V vs {vb} V");
            }
        }
    }

    [Fact]
    public void At_no_load_an_off_nominal_gy_gy_scales_the_voltage_by_its_ratio_exactly()
    {
        string text = Text("ieee4-gyGy-stepdown-unbalanced.json").Replace("\"kvLLTo\": 4.16", "\"kvLLTo\": 4.264", StringComparison.Ordinal);
        int at = text.IndexOf("\"kw\": [", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\"kw\": \\[[^\\]]*\\], \"kvar\": \\[[^\\]]*\\]", "\"kw\": [0, 0, 0], \"kvar\": [0, 0, 0]");
        Assert.True(at > 0);
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(text, "noload.json");
        PhaseBranch t = f.Model.Branches.Single(x => x.RegulatorRatio is not null);
        Assert.Equal(4.264 / 4.16, t.RegulatorRatio![0], 15);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged);
        int i2 = r.BusIds.ToList().IndexOf(2), i3 = r.BusIds.ToList().IndexOf(3);
        for (int p = 0; p < 3; p++)
            Assert.True((r.V[i3][p] - r.V[i2][p] * t.RegulatorRatio[p]).Magnitude < 1e-14);
    }

    [Fact]
    public void A_rated_transformer_is_the_old_branch_exactly()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(Text("ieee4-dGy-stepdown-unbalanced.json"), "rated.json");
        PhaseBranch t = f.Model.Branches.Single(b => b.IsTransformerElement);
        Assert.Equal(1.0, t.TransformerRatio);
        Assert.Null(t.RegulatorRatio);
    }
}
