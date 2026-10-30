using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 1.95.253 (N168): ungrounded-wye / delta ("y-d") and delta / delta ("d-d") transformers in the three-phase sweep, both
/// with a three-wire (delta) secondary, validated against the IEEE 4 Node Test Feeder's published Y-D and D-D results
/// (step-down and step-up, balanced and unbalanced, a delta-connected load); and the three-wire rules.
/// </summary>
public class Dist3phDeltaSecondaryTransformerTests
{
    private static string FeederPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return Path.Combine(dir!.FullName, "data", "dist3ph", name);
    }

    // Published node voltages (volts, degrees), IEEE 4 Node Test Feeder (IEEE PES Distribution System Analysis
    // Subcommittee, W. H. Kersting), as tabulated in the LBL Modelica Buildings library,
    // Buildings.Electrical.AC.ThreePhasesUnbalanced.Validation.IEEETests.Test4NodesFeeder.{Balanced,Unbalanced}Step{Down,Up}
    // .{DD,YD} (V*_ref, Theta*_ref; read 2026-10-06; its Y-Y values equal the ones MultiPhaseJsonLoaderTests pins). LL marks a
    // line-to-line reading (ab, bc, ca) where the reference probes a delta; the Y-D node 2 is line to ground (V1 = Vag, the
    // IEEE table note).
    // Moved 2026-10 (END-PASS VERIFY row 4, end pass; IEEE 4 Node Test Feeder solution tables, revised 2006-09-19, Ruling R1
    // and Mark's go on the end-pass moves 2026-10-11): comment-only correction. Before -> after: "the Y-D node 2 is line to
    // neutral" -> "line to ground (V1 = Vag, the IEEE table note)".
    // Moved 2026-10 (END-PASS VERIFY rows 1 and 3, end pass; IEEE 4 Node Test Feeder solution tables, revised 2006-09-19,
    // decoded in review-progress/2026-10/verify-sources, N066 / N336, Ruling R1 and Mark's go on the end-pass moves
    // 2026-10-11): the D-D and Y-D values below are now checked against the IEEE tables themselves, not only LBL's copy.
    // - D-D step-up balanced node 4 phase b angle: -93.6 -> -93.5 (IEEE prints 23688 / -93.5; the -93.6 is an LBL copying
    //   error in its DD model, whose DY model has -93.5). GridSim 23687.6101 V at -93.53974 deg: residual -0.3899 V,
    //   -0.03974 deg, inside the half digit, so its pinned residual row is removed (see Residuals).
    // - Y-D (the four yD files): LBL's "YD" values are the IEEE's Gr Y-D column (grounded wye), but these feeders model the
    //   ungrounded wye (Y-D column), so the four rows are re-pinned to the Y-D column. Before -> after:
    //   SD-bal node 2 V {7113, 7132, 7123} -> {7112, 7133, 7124}, angles {-0.3, -120.3, 119.6} -> {-0.3, -120.4, 119.6}
    //   (the IEEE prints "-.03" for phase a, a typo: Gr Y-D prints -0.3); node 3 angle a -3.5 -> -3.4.
    //   SD-unb node 2 V {7113, 7144, 7111} -> {7112, 7144, 7112}; node 3 V c 3875 -> 3874.
    //   SU-bal node 2 V {7128, 7145, 7137} -> {7127, 7145, 7138}, angle b -120.3 -> -120.4; node 4 V {23680, 23663, 23625}
    //   -> {23681, 23664, 23625}.
    //   SU-unb node 2 V {7121, 7147, 7150} -> {7120, 7147, 7150}, angle c 119.5 -> 119.6; node 4 V c 23496 -> 23495.
    //   Values copied from the IEEE Y-D column (not from a run log).
    private static readonly Dictionary<string, (int Bus, bool LineToLine, double[] V, double[] Deg)[]> Published = new()
    {
        ["ieee4-dD-stepdown-balanced.json"] = new[] { (2, true, new[] { 12339.0, 12349, 12321 }, new[] { 29.7, -90.4, 149.6 }), (3, true, new[] { 3911.0, 3914, 3905 }, new[] { 26.5, -93.6, 146.4 }), (4, true, new[] { 3442.0, 3497, 3384 }, new[] { 22.3, -99.4, 140.7 }) },
        ["ieee4-dD-stepdown-unbalanced.json"] = new[] { (2, true, new[] { 12341.0, 12370, 12302 }, new[] { 29.8, -90.5, 149.5 }), (3, true, new[] { 3902.0, 3972, 3871 }, new[] { 27.2, -93.9, 145.7 }), (4, true, new[] { 3431.0, 3647, 3294 }, new[] { 24.3, -100.4, 138.6 }) },
        ["ieee4-yD-stepdown-balanced.json"] = new[] { (2, false, new[] { 7112.0, 7133, 7124 }, new[] { -0.3, -120.4, 119.6 }), (3, true, new[] { 3906.0, 3915, 3909 }, new[] { -3.4, -123.6, 116.3 }), (4, true, new[] { 3437.0, 3497, 3388 }, new[] { -7.8, -129.3, 110.6 }) },
        ["ieee4-yD-stepdown-unbalanced.json"] = new[] { (2, false, new[] { 7112.0, 7144, 7112 }, new[] { -0.2, -120.4, 119.5 }), (3, true, new[] { 3896.0, 3972, 3874 }, new[] { -2.8, -123.8, 115.7 }), (4, true, new[] { 3425.0, 3646, 3298 }, new[] { -5.8, -130.3, 108.6 }) },
        ["ieee4-dD-stepup-balanced.json"] = new[] { (2, true, new[] { 12361.0, 12372, 12348 }, new[] { 29.7, -90.4, 149.6 }), (3, true, new[] { 23723.0, 23746, 23698 }, new[] { 26.7, -93.4, 146.6 }), (4, true, new[] { 23657.0, 23688, 23625 }, new[] { 26.6, -93.5, 146.5 }) },
        ["ieee4-dD-stepup-unbalanced.json"] = new[] { (2, true, new[] { 12362.0, 12392, 12334 }, new[] { 29.8, -90.4, 149.5 }), (3, true, new[] { 23675.0, 24060, 23573 }, new[] { 27.2, -93.6, 146.0 }), (4, true, new[] { 23610.0, 24015, 23492 }, new[] { 27.2, -93.7, 145.9 }) },
        ["ieee4-yD-stepup-balanced.json"] = new[] { (2, false, new[] { 7127.0, 7145, 7138 }, new[] { -0.3, -120.4, 119.6 }), (3, true, new[] { 23746.0, 23722, 23698 }, new[] { 56.7, -63.4, 176.7 }), (4, true, new[] { 23681.0, 23664, 23625 }, new[] { 56.6, -63.6, 176.5 }) },
        ["ieee4-yD-stepup-unbalanced.json"] = new[] { (2, false, new[] { 7120.0, 7147, 7150 }, new[] { -0.4, -120.3, 119.6 }), (3, true, new[] { 23703.0, 24040, 23576 }, new[] { 57.2, -63.6, 176.1 }), (4, true, new[] { 23637.0, 23995, 23495 }, new[] { 57.1, -63.8, 175.9 }) },
    };

    // Residuals, pinned (the MultiPhaseIeee123Tests rule: never hidden). 61 of the 72 published values agree within half
    // the last printed digit (0.5 V, 0.05 degree). The 11 others are listed with the measured deviation (GridSim minus
    // published) and must stay within 0.01 V and 0.001 degree of it, so a change in the element shows.
    // Moved 2026-10 (END-PASS VERIFY rows 1 and 3, end pass; IEEE 4 Node Test Feeder solution tables, revised 2006-09-19,
    // N066 / N336, Ruling R1 and Mark's go on the end-pass moves 2026-10-11). Before -> after:
    // - row ("ieee4-dD-stepup-balanced.json", 4, 1, -0.390, 0.0603) removed: against the IEEE's -93.5 the value is inside
    //   the half digit (-0.3899 V, -0.03974 deg).
    // - the 8 Y-D rows pinned against the Gr Y-D column (SD-bal (2,a) -1.361 / 0.0022 and (2,b) 0.120 / -0.0503; SD-unb
    //   (2,a) -1.860 / -0.0044; SU-bal (2,a) -1.663 / -0.0148, (4,a) 0.513 / -0.0407 and (4,b) 0.506 / 0.0272; SU-unb (2,a)
    //   -1.260 / 0.0134 and (4,c) -0.556 / 0.0358) are replaced by the 10 rows against the Y-D column below, measured on
    //   the endpass-v branch at 1.95.274 (a probe test, not a suite run log), 10 of 36
    //   Y-D values (8 of 36 against Gr Y-D before). The old "[VERIFY]" phase a pattern (1.26 to 1.86 V low) was the
    //   grounded column's; against the Y-D column phase a is out only in SD-unb (-0.8603 V) and SU-bal (-0.6635 V).
    // Why residuals of up to 1.03 V remain: the IEEE Y-D node 2 table disagrees with the IEEE's own Y-D line currents.
    // Node 2 = source - Z(1-2) I(1-2), from the published Y-D currents (335.8 / -34.7, 335.9 / -154.6, 335.9 / 85.3 A) and
    // the 12.47 kV source, is 7111.50, 7132.20, 7123.16 V; GridSim gives 7111.64, 7132.12, 7123.13 V (within 0.14 V of the
    // IEEE's own currents); the table prints 7112, 7133, 7124 (+0.50, +0.80, +0.84 V). GridSim's line 1-2 currents match
    // the Y-D column (335.77 / -34.65, 335.95 / -154.64, 335.92 / 85.33 A), not Gr Y-D (334.8, 335.4, 337.4 A, which carry
    // the grounded bank's zero sequence), and its neutral shift (mean of Vag..Vcg) matches the printed Vng in all four
    // cases (3.5664 / 169.54 vs 3.6 / 169.5; 4.2610 / 171.55 vs 4.27 / 171.6; 3.1040 / 174.88 vs 3.1 / 174.9; 2.7857 /
    // -173.86 vs 2.79 / -173.9). A 7200 V line-to-ground source (the other reading of the document) was tried and rejected
    // (74 values outside the bound against 14 with the bundled 12.47 kV source). So the scatter is the published data's
    // own; the element is the ungrounded Y-D connection and these rows pin where the table and its own currents differ.
    private static readonly (string File, int Bus, int Phase, double DVolts, double DDeg)[] Residuals =
    {
        ("ieee4-dD-stepdown-balanced.json", 4, 2, -0.236, -0.0533),
        ("ieee4-yD-stepdown-balanced.json", 2, 1, -0.8803, 0.04969),
        ("ieee4-yD-stepdown-balanced.json", 2, 2, -0.8657, -0.00099),
        ("ieee4-yD-stepdown-balanced.json", 3, 0, -0.2686, -0.14236),
        ("ieee4-yD-stepdown-unbalanced.json", 2, 0, -0.8603, -0.00445),
        ("ieee4-yD-stepdown-unbalanced.json", 2, 2, -0.8932, 0.03745),
        ("ieee4-yD-stepdown-unbalanced.json", 3, 2, 1.0337, -0.00119),
        ("ieee4-yD-stepup-balanced.json", 2, 0, -0.6635, -0.01485),
        ("ieee4-yD-stepup-balanced.json", 2, 1, -0.3605, 0.05337),
        ("ieee4-yD-stepup-balanced.json", 2, 2, -0.6217, 0.00338),
        ("ieee4-yD-stepup-unbalanced.json", 2, 2, -0.3403, -0.05299),
    };

    [Theory]
    [InlineData("ieee4-dD-stepdown-balanced.json")]
    [InlineData("ieee4-dD-stepdown-unbalanced.json")]
    [InlineData("ieee4-yD-stepdown-balanced.json")]
    [InlineData("ieee4-yD-stepdown-unbalanced.json")]
    [InlineData("ieee4-dD-stepup-balanced.json")]
    [InlineData("ieee4-dD-stepup-unbalanced.json")]
    [InlineData("ieee4-yD-stepup-balanced.json")]
    [InlineData("ieee4-yD-stepup-unbalanced.json")]
    public void Ieee4_dD_and_yD_match_the_published_node_voltages(string file)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath(file));
        Assert.True(f.Model.Branches.Single(b => b.IsTransformerElement).HasDeltaSecondary);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"sweep status {r.Status} after {r.Iterations} iterations");
        var failures = new List<string>();
        foreach ((int bus, bool ll, double[] vPub, double[] degPub) in Published[file])
        {
            Complex[] v = r.V[r.BusIds.ToList().IndexOf(bus)];
            Complex[] shown = ll ? new[] { v[0] - v[1], v[1] - v[2], v[2] - v[0] } : v;
            for (int p = 0; p < 3; p++)
            {
                double dv = shown[p].Magnitude * f.VoltBaseLN(bus) - vPub[p];
                double dd = Math.IEEERemainder(shown[p].Phase * 180.0 / Math.PI - degPub[p], 360.0);
                var pinned = Residuals.Where(x => x.File == file && x.Bus == bus && x.Phase == p).ToList();
                bool ok = pinned.Count == 1
                    ? Math.Abs(dv - pinned[0].DVolts) <= 0.01 && Math.Abs(dd - pinned[0].DDeg) <= 0.001
                    : Math.Abs(dv) <= 0.5 && Math.Abs(dd) <= 0.05;
                if (!ok) failures.Add($"bus {bus} phase {p}: dV {dv:F3} V, dAngle {dd:F4} deg{(pinned.Count == 1 ? $" (pinned {pinned[0].DVolts}, {pinned[0].DDeg})" : "")}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_delta_delta_matrix_blocks_zero_sequence_and_passes_the_rest_unchanged()
    {
        var v = new[] { new Complex(1.0, 0.1), new Complex(-0.4, -0.9), new Complex(-0.5, 0.8) };
        Complex[] o = PhaseBranch.RemoveZeroSequence(v);
        Assert.True((o[0] + o[1] + o[2]).Magnitude < 1e-15);
        Complex[] s = SymmetricalComponents.ToSequence(v), so = SymmetricalComponents.ToSequence(o);
        Assert.True((s[1] - so[1]).Magnitude < 1e-15 && (s[2] - so[2]).Magnitude < 1e-15);
    }

    private static string Text(string file) => File.ReadAllText(FeederPath(file));

    [Fact]
    public void A_wye_load_on_the_three_wire_secondary_is_refused_by_the_sweep()
    {
        MultiPhaseModel m = MultiPhaseJsonLoader.Parse(Text("ieee4-dD-stepdown-unbalanced.json")
            .Replace("\"connection\": \"delta\"", "\"connection\": \"wye\"", StringComparison.Ordinal), "wye.json").Model;
        var ex = Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(m));
        Assert.Contains("three-wire system", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no neutral", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_y_d_with_equal_rated_voltages_and_an_unmodelled_connection_are_refused_by_name()
    {
        string text = Text("ieee4-yD-stepdown-unbalanced.json");
        string level = text.Replace("\"baseKvLL\": 4.16", "\"baseKvLL\": 12.47", StringComparison.Ordinal).Replace("\"kvLLTo\": 4.16", "\"kvLLTo\": 12.47", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(level, "level.json"));
        Assert.Contains("\"y-d\" needs different rated voltages", ex.Message, StringComparison.Ordinal);
        var od = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(text.Replace("\"y-d\"", "\"open-delta\"", StringComparison.Ordinal), "od.json"));
        Assert.Contains("\"d-d\" (delta / delta)", od.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grounded_wye_transformer_fed_from_the_three_wire_secondary_is_refused_by_the_loader()
    {
        string text = Text("ieee4-dD-stepdown-unbalanced.json")
            .Replace("{ \"id\": 4, \"baseKvLL\": 4.16 }", "{ \"id\": 4, \"baseKvLL\": 4.16 },\n    { \"id\": 5, \"baseKvLL\": 4.16 }", StringComparison.Ordinal);
        int at = text.IndexOf("\"transformers\": [", StringComparison.Ordinal);
        Assert.True(at > 0);
        text = text.Insert(text.IndexOf('[', at) + 1,
            "\n    { \"id\": \"t4-5\", \"from\": 4, \"to\": 5, \"connection\": \"gy-gy\", \"kva\": 500, \"kvLLFrom\": 4.16, \"kvLLTo\": 4.16, \"rPercent\": 1.0, \"xPercent\": 4.0 },");
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(text, "gygy.json"));
        Assert.Contains("\"t4-5\" (gy-gy) is fed from a three-wire system", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_three_phase_solver_refuses_a_delta_secondary_by_name()
    {
        MultiPhaseModel m = MultiPhaseJsonLoader.Load(FeederPath("ieee4-yD-stepdown-unbalanced.json")).Model;
        var wls = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(m, Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("wye / delta and delta / delta transformers", wls.Message, StringComparison.Ordinal);
        var fault = Assert.Throws<ArgumentException>(() => SequenceFaultStudy.Thevenin(m, 4));
        Assert.Contains("wye / delta", fault.Message, StringComparison.Ordinal);
        var dsse = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(m, Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("wye / delta transformer", dsse.Message, StringComparison.Ordinal);
    }
}
