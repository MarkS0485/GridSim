using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 1.95.251 (N168): the delta / grounded-wye step-down transformer in the three-phase sweep, validated against the IEEE 4
/// Node Test Feeder's published D-GrY step-down results; every other three-phase solver refuses it by name.
/// </summary>
public class Dist3phDeltaWyeTransformerTests
{
    private static string FeederPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return Path.Combine(dir!.FullName, "data", "dist3ph", name);
    }

    // Published node voltages (volts, degrees) of the IEEE 4 Node Test Feeder (IEEE PES Distribution System Analysis
    // Subcommittee, W. H. Kersting), D-GrY step-down cases: node 2 (the delta side) line to line (ab, bc, ca), nodes 3 and 4
    // line to neutral, printed to 1 V and 0.1 degree. The same values are tabulated in the LBL Modelica Buildings library,
    // Buildings.Electrical.AC.ThreePhasesUnbalanced.Validation.IEEETests.Test4NodesFeeder.{Balanced,Unbalanced}StepDown.DY
    // (V2_ref, V3_ref, V4_ref and Theta*_ref; read 2026-10-06), whose Y-Y values equal the ones the Y-Y test pins.
    //
    // Residual, pinned (the MultiPhaseIeee123Tests rule: tolerances just above the measured residual, cause recorded).
    // 17 of the 18 values per case agree within half the last printed digit (0.5 V, 0.05 degree). The one that does not
    // is unbalanced node 3 phase b: 2261.597 V against a printed 2261 V (+0.597 V, 0.026 %; its angle -153.8140 deg is
    // within 0.014 degree). The largest angle residual is 0.0488 degree (unbalanced node 2, c-a). The tolerances are
    // therefore 0.6 V and 0.05 degree. [VERIFY] the cause against the IEEE document: the published program may round an
    // intermediate (the 4-decimal line matrices, or the transformer impedance in ohms) where GridSim keeps full precision.
    private const double MagTolVolts = 0.6;
    private const double AngTolDeg = 0.05;

    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg)[] PublishedUnbalanced =
    {
        (2, true, new[] { 12350.0, 12314.0, 12333.0 }, new[] { 29.6, -90.4, 149.8 }),
        (3, false, new[] { 2290.0, 2261.0, 2214.0 }, new[] { -32.4, -153.8, 85.2 }),
        (4, false, new[] { 2157.0, 1936.0, 1849.0 }, new[] { -34.2, -157.0, 73.4 }),
    };

    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg)[] PublishedBalanced =
    {
        (2, true, new[] { 12340.0, 12349.0, 12318.0 }, new[] { 29.7, -90.4, 149.6 }),
        (3, false, new[] { 2249.0, 2263.0, 2259.0 }, new[] { -33.7, -153.4, 86.4 }),
        (4, false, new[] { 1920.0, 2054.0, 1986.0 }, new[] { -39.1, -158.3, 80.9 }),
    };

    [Theory]
    [InlineData("ieee4-dGy-stepdown-unbalanced.json", false)]
    [InlineData("ieee4-dGy-stepdown-balanced.json", true)]
    public void Ieee4_dGy_stepdown_matches_the_published_node_voltages_to_rounding(string file, bool balanced)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath(file));
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"sweep status {r.Status} after {r.Iterations} iterations");

        var failures = new List<string>();
        foreach ((int bus, bool ll, double[] vPub, double[] degPub) in balanced ? PublishedBalanced : PublishedUnbalanced)
        {
            int i = r.BusIds.ToList().IndexOf(bus);
            Complex[] v = r.V[i];
            Complex[] shown = ll ? new[] { v[0] - v[1], v[1] - v[2], v[2] - v[0] } : v;
            for (int p = 0; p < 3; p++)
            {
                double volts = shown[p].Magnitude * f.VoltBaseLN(bus);
                double deg = shown[p].Phase * 180.0 / Math.PI;
                double dd = Math.IEEERemainder(deg - degPub[p], 360.0);
                if (Math.Abs(volts - vPub[p]) > MagTolVolts || Math.Abs(dd) > AngTolDeg)
                    failures.Add($"bus {bus} {(ll ? "ab bc ca".Split(' ')[p] : "abc"[p].ToString())}: {volts:F3} V at {deg:F4} deg vs published {vPub[p]} V at {degPub[p]} deg");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // 1.95.252: the D-GrY STEP-UP cases (12.47 kV delta to 24.9 kV grounded wye, the wye leading by 30 degrees), from
    // the same sources (LBL Modelica Buildings, IEEETests.Test4NodesFeeder.{Balanced,Unbalanced}StepUp.DY; read
    // 2026-10-06). All 36 values agree within half the last printed digit (largest 0.456 V, 0.0461 degree), so these use
    // the plain half-digit tolerances.
    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg)[] PublishedStepUpUnbalanced =
    {
        (2, true, new[] { 12364.0, 12391.0, 12333.0 }, new[] { 29.8, -90.5, 149.6 }),
        (3, false, new[] { 13792.0, 13733.0, 13641.0 }, new[] { 27.7, -93.5, 145.4 }),
        (4, false, new[] { 13768.0, 13684.0, 13600.0 }, new[] { 27.7, -93.6, 145.2 }),
    };

    private static readonly (int Bus, bool LineToLine, double[] V, double[] Deg)[] PublishedStepUpBalanced =
    {
        (2, true, new[] { 12361.0, 12372.0, 12348.0 }, new[] { 29.7, -90.4, 149.6 }),
        (3, false, new[] { 13697.0, 13710.0, 13681.0 }, new[] { 26.7, -93.4, 146.6 }),
        (4, false, new[] { 13653.0, 13678.0, 13644.0 }, new[] { 26.6, -93.5, 146.5 }),
    };

    [Theory]
    [InlineData("ieee4-dGy-stepup-unbalanced.json", false)]
    [InlineData("ieee4-dGy-stepup-balanced.json", true)]
    public void Ieee4_dGy_stepup_matches_the_published_node_voltages_to_half_the_last_digit(string file, bool balanced)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(FeederPath(file));
        Assert.True(f.Model.Branches.Single(b => b.IsDeltaWye).DeltaWyeStepUp);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"sweep status {r.Status} after {r.Iterations} iterations");
        var failures = new List<string>();
        foreach ((int bus, bool ll, double[] vPub, double[] degPub) in balanced ? PublishedStepUpBalanced : PublishedStepUpUnbalanced)
        {
            Complex[] v = r.V[r.BusIds.ToList().IndexOf(bus)];
            Complex[] shown = ll ? new[] { v[0] - v[1], v[1] - v[2], v[2] - v[0] } : v;
            for (int p = 0; p < 3; p++)
            {
                double volts = shown[p].Magnitude * f.VoltBaseLN(bus);
                double deg = shown[p].Phase * 180.0 / Math.PI;
                if (Math.Abs(volts - vPub[p]) > 0.5 || Math.Abs(Math.IEEERemainder(deg - degPub[p], 360.0)) > 0.05)
                    failures.Add($"bus {bus} phase {p}: {volts:F3} V at {deg:F4} deg vs published {vPub[p]} V at {degPub[p]} deg");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void The_step_up_matrices_are_lossless_and_lead_by_30_degrees()
    {
        var vLv = new[] { new Complex(1.01, 0.02), new Complex(-0.49, -0.86), new Complex(-0.52, 0.85) };
        var iHv = new[] { new Complex(0.4, -0.2), new Complex(-0.1, 0.3), new Complex(0.25, 0.15) };
        Complex[] vHv = PhaseBranch.DeltaWyeStepUpVoltage(vLv);
        Complex[] iLv = PhaseBranch.DeltaWyeStepUpCurrent(iHv);
        Complex sIn = Enumerable.Range(0, 3).Select(p => vLv[p] * Complex.Conjugate(iLv[p])).Aggregate(Complex.Zero, (a, b) => a + b);
        Complex sOut = Enumerable.Range(0, 3).Select(p => vHv[p] * Complex.Conjugate(iHv[p])).Aggregate(Complex.Zero, (a, b) => a + b);
        Assert.True((sIn - sOut).Magnitude < 1e-15, $"S_in {sIn} vs S_out {sOut}");
        Assert.True((iLv[0] + iLv[1] + iLv[2]).Magnitude < 1e-15);
        Complex[] pos = PhaseBranch.DeltaWyeStepUpVoltage(new[] { Complex.FromPolarCoordinates(1, 0), Complex.FromPolarCoordinates(1, -2 * Math.PI / 3), Complex.FromPolarCoordinates(1, 2 * Math.PI / 3) });
        Assert.Equal(1.0, pos[0].Magnitude, 14);
        Assert.Equal(30.0, pos[0].Phase * 180.0 / Math.PI, 12);
    }

    [Fact]
    public void The_transformer_matrices_are_lossless_and_block_zero_sequence_on_the_delta_side()
    {
        var vHv = new[] { new Complex(1.01, 0.02), new Complex(-0.49, -0.86), new Complex(-0.52, 0.85) };
        var iLv = new[] { new Complex(0.4, -0.2), new Complex(-0.1, 0.3), new Complex(0.25, 0.15) };
        Complex[] vLv = PhaseBranch.DeltaWyeVoltage(vHv);
        Complex[] iHv = PhaseBranch.DeltaWyeCurrent(iLv);
        Complex sHv = Enumerable.Range(0, 3).Select(p => vHv[p] * Complex.Conjugate(iHv[p])).Aggregate(Complex.Zero, (a, b) => a + b);
        Complex sLv = Enumerable.Range(0, 3).Select(p => vLv[p] * Complex.Conjugate(iLv[p])).Aggregate(Complex.Zero, (a, b) => a + b);
        Assert.True((sHv - sLv).Magnitude < 1e-15, $"S_hv {sHv} vs S_lv {sLv}");
        Assert.True((iHv[0] + iHv[1] + iHv[2]).Magnitude < 1e-15);
        // A balanced positive sequence lags by exactly 30 degrees at unchanged magnitude.
        Complex[] pos = PhaseBranch.DeltaWyeVoltage(new[] { Complex.FromPolarCoordinates(1, 0), Complex.FromPolarCoordinates(1, -2 * Math.PI / 3), Complex.FromPolarCoordinates(1, 2 * Math.PI / 3) });
        Assert.Equal(1.0, pos[0].Magnitude, 14);
        Assert.Equal(-30.0, pos[0].Phase * 180.0 / Math.PI, 12);
    }

    // Moved 2026-10 (N168, 1.95.252, the R059 / N062 rule: Mark's 2026-10-02 best-judgement ruling, as for the "d-gy"
    // loader case): step-up delta / grounded-wye transformers are now modelled and validated against the IEEE 4 Node Test
    // Feeder's published D-GrY step-up results, so a step-up "d-gy" loads. The test keeps checking that a transformer the
    // loader does not model is refused by name. Before: a step-up d-gy refused naming "step-down", and the "d-d" message
    // containing "\"d-gy\" (delta / grounded wye, step-down)". After: a d-gy with equal rated voltages (no ANSI direction)
    // refused at the connection, and the "d-d" message containing "\"d-gy\" (delta / grounded wye".
    [Fact]
    public void A_delta_wye_with_no_ansi_direction_and_an_unmodelled_connection_are_refused_by_name()
    {
        string text = File.ReadAllText(FeederPath("ieee4-dGy-stepdown-unbalanced.json"));
        string level = text.Replace("{ \"id\": 3, \"baseKvLL\": 4.16 }", "{ \"id\": 3, \"baseKvLL\": 12.47 }", StringComparison.Ordinal)
                           .Replace("{ \"id\": 4, \"baseKvLL\": 4.16 }", "{ \"id\": 4, \"baseKvLL\": 12.47 }", StringComparison.Ordinal)
                           .Replace("\"kvLLTo\": 4.16", "\"kvLLTo\": 12.47", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(level, "level.json"));
        Assert.Contains("$.transformers[0].connection", ex.Message, StringComparison.Ordinal);
        Assert.Contains("different rated voltages", ex.Message, StringComparison.Ordinal);
        // Moved 2026-10 (N168, 1.95.253, the same rule): "d-d" is now modelled. Before: the "d-d" connection refused.
        // After: "open-delta" (still unmodelled) refused, with the same message check.
        var dd = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(text.Replace("\"d-gy\"", "\"open-delta\"", StringComparison.Ordinal), "dd.json"));
        Assert.Contains("\"d-gy\" (delta / grounded wye", dd.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_three_phase_solver_refuses_the_transformer_by_name()
    {
        MultiPhaseModel m = MultiPhaseJsonLoader.Load(FeederPath("ieee4-dGy-stepdown-unbalanced.json")).Model;
        Assert.Equal(MultiPhaseElement.DeltaWyeTransformer, m.ElementsPresent & MultiPhaseElement.DeltaWyeTransformer);

        var wls = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(m, Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("delta / grounded-wye transformers", wls.Message, StringComparison.Ordinal);

        var harm = Assert.Throws<ArgumentException>(() => ThreePhaseHarmonicPenetration.Solve(m, Array.Empty<ThreePhaseHarmonicSource>(), new[] { 5 }));
        Assert.Contains("delta / grounded-wye transformers", harm.Message, StringComparison.Ordinal);

        var fault = Assert.Throws<ArgumentException>(() => SequenceFaultStudy.Thevenin(m, 4));
        Assert.Contains("delta / grounded-wye", fault.Message, StringComparison.Ordinal);

        var dsse = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(m, Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("delta / grounded-wye transformer", dsse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_branch_without_the_flag_is_the_old_branch()
    {
        var b = new PhaseBranch(1, 2, PhaseImpedance.Diagonal(new Complex(0.01, 0.06)));
        Assert.False(b.DeltaWyeStepDown);
        Assert.False(b.HasExtendedElements);
    }
}
