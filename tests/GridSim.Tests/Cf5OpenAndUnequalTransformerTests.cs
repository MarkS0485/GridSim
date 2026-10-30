using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// CF5 (N334, N335): open wye / open delta and open delta / open delta banks, and unequal winding impedances on the
/// delta-secondary elements, in the three-phase sweep. No published reference is on disk ([VERIFY] against Kersting's
/// worked examples), so these are self-consistency proofs: the equal-winding limit is the existing element bit for bit;
/// the line-to-neutral equivalent's losses equal the winding losses; the ideal part is lossless; KCL and KVL of the delta
/// hold; an open delta at no load gives the full delta's voltages exactly; the v2 form round-trips.
/// </summary>
public class Cf5OpenAndUnequalTransformerTests
{
    private static string FeederPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return Path.Combine(dir!.FullName, "data", "dist3ph", name);
    }

    private static string Text(string file) => File.ReadAllText(FeederPath(file)).Replace("\r\n", "\n");

    private static readonly Complex[] Unequal = { new(0.010, 0.060), new(0.014, 0.075), new(0.008, 0.052) };

    // A deterministic three-wire current set (sums to zero exactly: the third is minus the sum of the first two).
    private static Complex[] ZeroSumCurrents()
    {
        var a = new Complex(0.83, -0.41);
        var b = new Complex(-0.57, -0.66);
        return new[] { a, b, -(a + b) };
    }

    private static Complex[] Mul(PhaseImpedance z, Complex[] i) => z.Multiply(i);

    private static Complex Dot(Complex[] u, Complex[] conjOf)
    {
        Complex s = Complex.Zero;
        for (int k = 0; k < 3; k++) s += u[k] * Complex.Conjugate(conjOf[k]);
        return s;
    }

    private static void Close(Complex expected, Complex actual, double rel, string what)
        => Assert.True((expected - actual).Magnitude <= rel * Math.Max(1.0, expected.Magnitude), $"{what}: expected {expected}, got {actual}");

    // ------------------------------------------------------------------ equal-winding limit, bit for bit

    [Fact]
    public void Equal_windings_give_exactly_the_existing_equal_impedance_element()
    {
        var z = new Complex(0.01 * (6.0 / 6.0), 0.06);
        foreach (PhaseImpedance zt in new[] { TransformerWindings.DeltaDeltaSeriesZ(new[] { z, z, z }), TransformerWindings.WyeDeltaSeriesZ(new[] { z, z, z }) })
        {
            PhaseImpedance d = PhaseImpedance.Diagonal(z);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(d[i, j].Real), BitConverter.DoubleToInt64Bits(zt[i, j].Real));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(d[i, j].Imaginary), BitConverter.DoubleToInt64Bits(zt[i, j].Imaginary));
                }
        }
    }

    [Theory]
    [InlineData("ieee4-dD-stepdown-unbalanced.json", "\"connection\": \"d-d\"")]
    [InlineData("ieee4-yD-stepdown-unbalanced.json", "\"connection\": \"y-d\"")]
    public void A_feeder_with_three_equal_windings_solves_bit_for_bit_like_the_bank_impedance(string file, string conn)
    {
        string text = Text(file);
        string withWindings = text.Replace("\"rPercent\": 1.0, \"xPercent\": 6.0 }",
            "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", StringComparison.Ordinal);
        Assert.NotEqual(text, withWindings);
        Assert.Contains(conn, withWindings, StringComparison.Ordinal);
        MultiPhaseResult a = new ThreePhaseSweep().Solve(MultiPhaseJsonLoader.Parse(text, "bank.json").Model);
        MultiPhaseResult b = new ThreePhaseSweep().Solve(MultiPhaseJsonLoader.Parse(withWindings, "windings.json").Model);
        Assert.True(a.Converged && b.Converged);
        Assert.Equal(a.Iterations, b.Iterations);
        for (int i = 0; i < a.V.Length; i++)
            for (int p = 0; p < 3; p++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Real), BitConverter.DoubleToInt64Bits(b.V[i][p].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.V[i][p].Imaginary), BitConverter.DoubleToInt64Bits(b.V[i][p].Imaginary));
            }
    }

    [Fact]
    public void The_general_construction_at_nearly_equal_windings_tends_to_the_equal_element()
    {
        // The equal case takes the exact shortcut; the general W M construction just off it must act on three-wire
        // currents as z times the identity (to first order in the inequality), proving the shortcut is the limit.
        var z = new Complex(0.01, 0.06);
        var eps = new Complex(1e-12, 1e-12);
        Complex[] i = ZeroSumCurrents();
        foreach (PhaseImpedance zt in new[] { TransformerWindings.DeltaDeltaSeriesZ(new[] { z, z, z + eps }), TransformerWindings.WyeDeltaSeriesZ(new[] { z, z, z + eps }) })
        {
            Complex[] drop = Mul(zt, i);
            for (int p = 0; p < 3; p++) Close(z * i[p], drop[p], 1e-10, $"phase {p}");
        }
    }

    // ------------------------------------------------------------------ the winding physics behind the equivalent

    [Fact]
    public void Delta_delta_winding_currents_close_the_delta_and_carry_the_circulating_current()
    {
        Complex[] i = ZeroSumCurrents();
        Complex[] id = TransformerWindings.DeltaDeltaWindingCurrents(Unequal, i);
        // KCL at the delta corners: I_a = I_ab - I_ca, I_b = I_bc - I_ab, I_c = I_ca - I_bc.
        Close(i[0], id[0] - id[2], 1e-14, "KCL a");
        Close(i[1], id[1] - id[0], 1e-14, "KCL b");
        Close(i[2], id[2] - id[1], 1e-14, "KCL c");
        // KVL round the delta: the winding drops sum to zero (the HV delta's EMFs do).
        Close(Complex.Zero, Unequal[0] * id[0] + Unequal[1] * id[1] + Unequal[2] * id[2], 1e-14, "KVL");
        // Unequal windings carry a circulating current (the winding currents do not sum to zero).
        Assert.True((id[0] + id[1] + id[2]).Magnitude > 1e-3);
    }

    [Fact]
    public void Wye_delta_winding_currents_have_no_circulating_current()
    {
        Complex[] i = ZeroSumCurrents();
        Complex[] id = TransformerWindings.WyeDeltaWindingCurrents(i);
        Close(i[0], id[0] - id[2], 1e-14, "KCL a");
        Close(i[1], id[1] - id[0], 1e-14, "KCL b");
        Close(Complex.Zero, id[0] + id[1] + id[2], 1e-14, "floating primary neutral");
    }

    [Fact]
    public void The_equivalent_impedances_dissipate_exactly_the_winding_losses()
    {
        // Power into the series part on the line side, sum_p conj(I_p) (Z I)_p, must equal the windings' own
        // sum_x Zw_x |I_Dx|^2 with Zw = 3 z (line-to-line windings on the line-to-neutral base). This ties the W M
        // construction to the physics of each connection.
        Complex[] i = ZeroSumCurrents();
        Complex Windings(Complex[] z, Complex[] id)
        {
            Complex s = Complex.Zero;
            for (int k = 0; k < z.Length; k++) s += 3.0 * z[k] * id[k] * Complex.Conjugate(id[k]);
            return s;
        }
        Close(Windings(Unequal, TransformerWindings.DeltaDeltaWindingCurrents(Unequal, i)),
            Dot(Mul(TransformerWindings.DeltaDeltaSeriesZ(Unequal), i), i), 1e-13, "delta / delta");
        Close(Windings(Unequal, TransformerWindings.WyeDeltaWindingCurrents(i)),
            Dot(Mul(TransformerWindings.WyeDeltaSeriesZ(Unequal), i), i), 1e-13, "wye / delta");
        Complex[] open = TransformerWindings.OpenDeltaWindingCurrents(i);
        Close(Windings(new[] { Unequal[0], Unequal[1] }, open.Take(2).ToArray()),
            Dot(Mul(TransformerWindings.OpenDeltaSeriesZ(Unequal[0], Unequal[1]), i), i), 1e-13, "open delta");
        Assert.Equal(Complex.Zero, open[2]);
    }

    [Fact]
    public void The_open_delta_drop_closes_the_line_to_line_triangle()
    {
        // drop_ab = 3 z_ab I_a, drop_bc = -3 z_bc I_c; the line-to-neutral drops' differences reproduce them.
        Complex[] i = ZeroSumCurrents();
        Complex[] d = Mul(TransformerWindings.OpenDeltaSeriesZ(Unequal[0], Unequal[1]), i);
        Close(3.0 * Unequal[0] * i[0], d[0] - d[1], 1e-14, "ab");
        Close(-3.0 * Unequal[1] * i[2], d[1] - d[2], 1e-14, "bc");
        Close(Complex.Zero, d[0] + d[1] + d[2], 1e-14, "zero sequence of the drop");
    }

    // ------------------------------------------------------------------ the ideal parts of the open banks

    [Fact]
    public void The_open_wye_open_delta_voltage_gives_one_per_unit_at_minus_thirty_degrees_and_ignores_phase_c()
    {
        Complex[] v = MultiPhaseModel.BalancedNominal();
        Complex[] o = PhaseBranch.OpenWyeOpenDeltaVoltage(v);
        for (int p = 0; p < 3; p++)
        {
            Assert.InRange(o[p].Magnitude, 1.0 - 1e-14, 1.0 + 1e-14);
            Complex expected = v[p] * Complex.FromPolarCoordinates(1.0, -Math.PI / 6.0);
            Close(expected, o[p], 1e-14, $"phase {p}");
        }
        var noC = new[] { v[0], v[1], Complex.Zero };
        Complex[] o2 = PhaseBranch.OpenWyeOpenDeltaVoltage(noC);
        for (int p = 0; p < 3; p++) Assert.Equal(o[p], o2[p]);
    }

    [Fact]
    public void The_open_banks_ideal_parts_are_lossless_for_three_wire_currents()
    {
        var v = new[] { new Complex(1.01, 0.02), new Complex(-0.49, -0.87), new Complex(-0.52, 0.85) };
        Complex[] i = ZeroSumCurrents();
        // open wye / open delta: power in on the From side equals power out at the To side's open-circuit voltage.
        Close(Dot(PhaseBranch.OpenWyeOpenDeltaVoltage(v), i), Dot(v, PhaseBranch.OpenWyeOpenDeltaCurrent(i)), 1e-14, "oy-od");
        // open delta / open delta: voltages as delta / delta.
        Close(Dot(PhaseBranch.RemoveZeroSequence(v), i), Dot(v, PhaseBranch.OpenDeltaOpenDeltaCurrent(i)), 1e-14, "od-od");
        // The open delta / open delta's From-side currents are the line currents themselves (three-wire: KCL of the two units).
        Complex[] from = PhaseBranch.OpenDeltaOpenDeltaCurrent(i);
        for (int p = 0; p < 3; p++) Close(i[p], from[p], 1e-15, $"od-od phase {p}");
    }

    // ------------------------------------------------------------------ the sweep with the new elements

    private static MultiPhaseModel OpenFeeder(bool openWye, Complex[] loadS, Phase sourcePhases = Phase.Abc)
    {
        // Source 1 -- line -- 2 == open bank == 3 -- line -- 4 (delta load). Per-unit, a short three-wire line each side.
        var line = new PhaseImpedance(new Complex[,]
        {
            { new(0.004, 0.012), new(0.001, 0.004), new(0.001, 0.003) },
            { new(0.001, 0.004), new(0.004, 0.012), new(0.001, 0.004) },
            { new(0.001, 0.003), new(0.001, 0.004), new(0.004, 0.012) },
        });
        PhaseImpedance zt = TransformerWindings.OpenDeltaSeriesZ(Unequal[0], Unequal[1]);
        var buses = new List<PhaseBus> { new(1, true), new(2, false), new(3, false), new(4, false) };
        var branches = new List<PhaseBranch>
        {
            new(1, 2, line),
            openWye ? new PhaseBranch(2, 3, zt, OpenWyeOpenDelta: true) : new PhaseBranch(2, 3, zt, OpenDeltaOpenDelta: true),
            new(3, 4, line),
        };
        var loads = new List<PhaseLoad> { new(4, loadS, PhaseLoadModel.ConstantPower, PhaseLoadConnection.Delta) };
        return new MultiPhaseModel(buses, branches, loads, 1.0);
    }

    private static readonly Complex[] DeltaLoad = { new(0.20, 0.10), new(0.25, 0.12), new(0.15, 0.06) };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_open_bank_feeder_converges_and_the_bank_balances_power_exactly(bool openWye)
    {
        MultiPhaseModel m = OpenFeeder(openWye, DeltaLoad);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(m);
        Assert.True(r.Converged, $"status {r.Status} after {r.Iterations}");
        PhaseBranch bank = m.Branches[1];
        Complex[] iTo = r.BranchCurrents![1];
        Close(Complex.Zero, iTo[0] + iTo[1] + iTo[2], 1e-12, "three-wire currents below the bank");
        Complex[] vFrom = r.V[1], vTo = r.V[2];
        Complex sFrom = Dot(vFrom, bank.TransformerInput(iTo));
        Complex sTo = Dot(vTo, iTo);
        Complex loss = Dot(bank.Z.Multiply(iTo), iTo);
        Close(sFrom, sTo + loss, 1e-12, "S_from = S_to + series loss");
        // The bank output is the line-to-neutral of zero zero-sequence.
        Close(Complex.Zero, vTo[0] + vTo[1] + vTo[2], 1e-12, "zero sequence at bus 3");
        // (Bus 4 is behind a three-wire LINE whose phase matrix has unequal column sums, so its line-to-neutral values are
        // not zero-sequence free; only the bank output is.)
    }

    [Fact]
    public void An_open_wye_bank_draws_nothing_on_phase_c_and_runs_from_a_two_phase_ab_supply()
    {
        MultiPhaseModel m = OpenFeeder(true, DeltaLoad);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(m);
        Assert.True(r.Converged);
        Complex[] iFrom = m.Branches[1].TransformerInput(r.BranchCurrents![1]);
        Assert.Equal(Complex.Zero, iFrom[2]);
        // The earth return carries I_A + I_B (an open wye primary is not three-wire).
        Assert.True((iFrom[0] + iFrom[1]).Magnitude > 1e-3);
    }

    [Fact]
    public void An_open_delta_at_no_load_gives_the_full_delta_voltages_exactly()
    {
        var zero = new[] { Complex.Zero, Complex.Zero, Complex.Zero };
        MultiPhaseModel open = OpenFeeder(false, zero);
        var full = new MultiPhaseModel(open.Buses, new List<PhaseBranch>
        {
            open.Branches[0], new(2, 3, TransformerWindings.DeltaDeltaSeriesZ(Unequal), DeltaDelta: true), open.Branches[2],
        }, open.Loads, 1.0);
        MultiPhaseResult a = new ThreePhaseSweep().Solve(open);
        MultiPhaseResult b = new ThreePhaseSweep().Solve(full);
        Assert.True(a.Converged && b.Converged);
        for (int i = 0; i < a.V.Length; i++)
            for (int p = 0; p < 3; p++) Assert.Equal(b.V[i][p], a.V[i][p]);
    }

    [Fact]
    public void An_open_delta_under_balanced_load_unbalances_the_voltages_a_full_delta_keeps_balanced()
    {
        // The known open-delta behaviour: with only two windings the secondary voltages unbalance under a balanced load.
        var balanced = new[] { new Complex(0.2, 0.1), new Complex(0.2, 0.1), new Complex(0.2, 0.1) };
        var z = new Complex(0.01, 0.06);
        MultiPhaseModel open = OpenFeeder(false, balanced);
        var line = PhaseImpedance.Diagonal(new Complex(0.004, 0.012));
        MultiPhaseModel openSym = new(open.Buses, new List<PhaseBranch>
        {
            new(1, 2, line), new(2, 3, TransformerWindings.OpenDeltaSeriesZ(z, z), OpenDeltaOpenDelta: true), new(3, 4, line),
        }, open.Loads, 1.0);
        MultiPhaseModel fullSym = new(open.Buses, new List<PhaseBranch>
        {
            new(1, 2, line), new(2, 3, TransformerWindings.DeltaDeltaSeriesZ(new[] { z, z, z }), DeltaDelta: true), new(3, 4, line),
        }, open.Loads, 1.0);
        MultiPhaseResult ro = new ThreePhaseSweep().Solve(openSym);
        MultiPhaseResult rf = new ThreePhaseSweep().Solve(fullSym);
        Assert.True(ro.Converged && rf.Converged);
        Assert.True(rf.VoltageUnbalancePercent(3) < 1e-9, $"full delta unbalance {rf.VoltageUnbalancePercent(3)} %");
        Assert.True(ro.VoltageUnbalancePercent(3) > 0.1, $"open delta unbalance {ro.VoltageUnbalancePercent(3)} %");
    }

    [Fact]
    public void An_unequal_delta_delta_feeder_converges_and_balances_power()
    {
        string text = Text("ieee4-dD-stepdown-unbalanced.json").Replace("\"rPercent\": 1.0, \"xPercent\": 6.0 }",
            "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.4, \"xPercent\": 7.5 }, { \"rPercent\": 0.8, \"xPercent\": 5.2 } ] }", StringComparison.Ordinal);
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(text, "unequal.json");
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged);
        int k = f.Model.Branches.ToList().FindIndex(b => b.DeltaDelta);
        PhaseBranch bank = f.Model.Branches[k];
        Complex[] iTo = r.BranchCurrents![k];
        int from = r.BusIds.ToList().IndexOf(bank.From), to = r.BusIds.ToList().IndexOf(bank.To);
        Complex sFrom = Dot(r.V[from], bank.TransformerInput(iTo));
        Complex sTo = Dot(r.V[to], iTo);
        Close(sFrom, sTo + Dot(bank.Z.Multiply(iTo), iTo), 1e-12, "S_from = S_to + winding losses");
    }

    // ------------------------------------------------------------------ refusals and the file forms

    [Fact]
    public void An_open_wye_bank_fed_from_a_three_wire_system_or_without_phase_b_is_refused()
    {
        MultiPhaseModel m = OpenFeeder(true, DeltaLoad);
        // Put a delta / delta ahead of it: the open wye's phase-to-earth primaries then have no earth.
        var dd = new PhaseBranch(1, 2, PhaseImpedance.Diagonal(new Complex(0.01, 0.06)), DeltaDelta: true);
        var three = new MultiPhaseModel(m.Buses, new List<PhaseBranch> { dd, m.Branches[1], m.Branches[2] }, m.Loads, 1.0);
        var ex = Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(three));
        Assert.Contains("three-wire", ex.Message, StringComparison.Ordinal);
        // A single-phase (a only) supply cannot feed it.
        var aOnly = new PhaseImpedance(new Complex[,] { { new(0.004, 0.012), Complex.Zero, Complex.Zero }, { Complex.Zero, Complex.Zero, Complex.Zero }, { Complex.Zero, Complex.Zero, Complex.Zero } });
        var lateral = new MultiPhaseModel(m.Buses, new List<PhaseBranch> { new(1, 2, aOnly, Phase.A), m.Branches[1], m.Branches[2] }, m.Loads, 1.0);
        var ex2 = Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(lateral));
        Assert.Contains("needs phases a and b", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_three_phase_solvers_refuse_an_open_bank_by_name()
    {
        MultiPhaseModel m = OpenFeeder(false, DeltaLoad);
        Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(m, Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(m, Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("open wye / open delta", MultiPhaseElements.Describe(m.ElementsPresent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("oy-od")]
    [InlineData("od-od")]
    public void The_v1_loader_reads_the_open_banks_and_the_v2_form_round_trips_them(string conn)
    {
        // Corrected 2026-10-11 (end pass, first run): the IEEE 4-node unbalanced load (1275 + 1800 + 2375 kW, 6.06 MVA on
        // the 6 MVA bank) has NO power-flow solution through an open bank of these two units: an independent Newton
        // solve of the same per-unit network (Python, continuation in the load scale) finds the nose of the PV curve at
        // 94.5 % of that load for od-od and 76.8 % for oy-od, so the sweep's MaxIterations was the correct fail-loud
        // answer, not a defect (an open bank of two units is the classic 57.7 % derating). The load is halved (every kW
        // and kvar exactly / 2), where both banks solve (an independent sweep replica converges in 13 / 17 sweeps,
        // lowest line-to-line voltage 0.84 / 0.80 pu); the test is about the loader and the v2 round trip.
        // Before -> after: the fixture's loads as published -> "kw": [637.5, 900, 1187.5],
        // "kvar": [395.0870157319777, 435.8898943540673, 390.3123748999001] (half of each).
        const string fullLoad = "\"kw\": [1275, 1800, 2375], \"kvar\": [790.1740314639554, 871.7797887081346, 780.6247497998002]";
        const string halfLoad = "\"kw\": [637.5, 900, 1187.5], \"kvar\": [395.0870157319777, 435.8898943540673, 390.3123748999001]";
        Assert.Contains(fullLoad, Text("ieee4-dD-stepdown-unbalanced.json"), StringComparison.Ordinal);
        string text = Text("ieee4-dD-stepdown-unbalanced.json").Replace("\"connection\": \"d-d\"", $"\"connection\": \"{conn}\"", StringComparison.Ordinal)
            .Replace("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.4, \"xPercent\": 7.5 } ] }", StringComparison.Ordinal)
            .Replace(fullLoad, halfLoad, StringComparison.Ordinal);
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Parse(text, "open.json");
        PhaseBranch bank = f.Model.Branches.Single(b => b.IsTransformerElement);
        Assert.True(conn == "oy-od" ? bank.OpenWyeOpenDelta : bank.OpenDeltaOpenDelta);
        Assert.True(bank.HasDeltaSecondary && bank.IsOpenTransformer);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(f.Model);
        Assert.True(r.Converged, $"status {r.Status}");
        // Corrected 2026-10-11 (end pass, first run): since N339 the v2 writer refuses a v1 feeder (which carries no
        // provenance) unless the caller names one ("GridSim does not invent a tag"); the round trip therefore passes the
        // same provenance the N339 tests give these IEEE PES feeders.
        // Before -> after: MultiPhaseV2Writer.ToJson(f) -> MultiPhaseV2Writer.ToJson(f, new AssetClassProvenance(
        // ProvenanceTag.Recorded, "IEEE PES test feeder data, as the v1 file's description cites.")).
        string v2 = MultiPhaseV2Writer.ToJson(f, new GridSim.Core.Assets.AssetClassProvenance(
            GridSim.Core.Diagnostics.ProvenanceTag.Recorded, "IEEE PES test feeder data, as the v1 file's description cites."));
        MultiPhaseFeeder back = MultiPhaseJsonLoader.ParseV2(v2, "open.v2.json");
        Assert.Null(MultiPhaseV2Writer.FirstDifference(f, back));
    }

    [Theory]
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"rPercent\": 1.0, \"xPercent\": 6.0, \"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", "both")]
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", "needs exactly 3 windings")]
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 0, \"xPercent\": 0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", "zero impedance")]
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": -1, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", "must not be negative")]
    [InlineData("\"rPercent\": 1.0, \"xPercent\": 6.0 }", "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0, \"kva\": 1 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", "kva")]
    public void Malformed_windings_are_refused_by_name(string from, string to, string expect)
    {
        string text = Text("ieee4-dD-stepdown-unbalanced.json").Replace(from, to, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(text, "bad.json"));
        Assert.Contains(expect, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windings_on_a_connection_that_has_no_delta_secondary_are_refused()
    {
        string text = Text("ieee4-dGy-stepdown-unbalanced.json");
        string bad = text.Replace("\"rPercent\": 1.0, \"xPercent\": 6.0 }",
            "\"windings\": [ { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 }, { \"rPercent\": 1.0, \"xPercent\": 6.0 } ] }", StringComparison.Ordinal);
        Assert.NotEqual(text, bad);
        var ex = Assert.Throws<InvalidDataException>(() => MultiPhaseJsonLoader.Parse(bad, "bad.json"));
        Assert.Contains("per-winding impedances are read for", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_winding_helpers_refuse_bad_values()
    {
        Assert.Throws<ArgumentException>(() => TransformerWindings.DeltaDeltaSeriesZ(new[] { Unequal[0], Unequal[1] }));
        Assert.Throws<ArgumentException>(() => TransformerWindings.DeltaDeltaSeriesZ(new[] { Unequal[0], Complex.Zero, Unequal[2] }));
        Assert.Throws<ArgumentException>(() => TransformerWindings.WyeDeltaSeriesZ(new[] { Unequal[0], new Complex(-0.1, 0.05), Unequal[2] }));
        Assert.Throws<ArgumentException>(() => TransformerWindings.OpenDeltaSeriesZ(new Complex(double.NaN, 0), Unequal[1]));
        Assert.Throws<ArgumentNullException>(() => TransformerWindings.DeltaDeltaSeriesZ(null!));
    }
}
