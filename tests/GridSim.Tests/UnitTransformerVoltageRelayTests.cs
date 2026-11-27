using System.Numerics;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC5: a unit's under / over-voltage relays (27 / 59) on a unit whose step-up transformer is folded into the machine model
/// (<see cref="Generator.UnitTransformerXPu"/>, on the unit rating). Their VTs are on the generator side, so they read the
/// machine terminal V_t = V_hv + j X_t I (I the unit's current into the network), not the network bus (the HV side). The
/// expected values are formed independently: V_hv as a phasor at its solved angle, I = conj(S / V_hv), V_t built with complex
/// arithmetic and its magnitude taken with <see cref="Complex.Abs"/>.
/// The case: IEEE 9-bus, G2 (bus 2, 1.025 pu, overexcited) with X_t = 0.10 pu. |V_t| is about 1.0317 pu, so a 59 at 1.03 pu
/// trips on the terminal while the HV bus (1.025 pu) is inside it.
/// </summary>
public sealed class UnitTransformerVoltageRelayTests(ITestOutputHelper output)
{
    private const double Xt = 0.10;
    private const double OvPickup = 1.03;
    private const double Step = 0.005;
    private static readonly double Eps = Math.ScaleB(1.0, -52);
    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal)))));

    private static (string, double)[] Ov(double delay) => new (string, double)[] { ("ov1Pu", OvPickup), ("ov1DelayS", delay) };

    private static (GridModel Model, PowerFlowResult Pf) Case9(double xt)
    {
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" })
            .Select(g => g.Name == "G2" ? g with { UnitTransformerXPu = xt } : g).ToList(), generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    /// <summary>The independent terminal voltage magnitude of unit <paramref name="g"/> on the solved state.</summary>
    private static (double Vt, double Vhv) Hand(GridModel m, PowerFlowResult pf, int g, double xt)
    {
        Generator gen = m.Generators[g];
        (double p, double q) = SolvedGeneration.UnitOutput(m, pf, g);
        int bi = pf.BusIds.ToList().IndexOf(gen.Bus);
        Complex v = Complex.FromPolarCoordinates(pf.Vm[bi], pf.VaDeg[bi] * Math.PI / 180.0);
        Complex i = Complex.Conjugate(new Complex(p / gen.MBase, q / gen.MBase) / v);
        Complex vt = v + new Complex(0.0, xt) * i;
        return (Complex.Abs(vt), pf.Vm[bi]);
    }

    private static int G2(GridModel m) => m.Generators.ToList().FindIndex(x => x.Name == "G2");

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model) =>
        model.Generators.Select(gen => new SubtransientInit.MachineData(
            H: gen.H, Damping: 0.0,
            Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    // ---- passive ----

    private static RelayVerdict PassiveOv(double xt, out GridModel m, out PowerFlowResult pf)
    {
        (m, pf) = Case9(xt);
        m = m.WithUnitProtection(Set(("G2", Ov(0.5))));
        ProtectionReport rep = ProtectionAssessment.Assess(m, pf);
        return Assert.Single(rep.Verdicts, v => v.UnitStage is { Function: UnitRelayFunction.OverVoltage } && v.LocationName == "G2");
    }

    [Fact]
    public void Passive_59_reads_the_hand_machine_terminal_voltage_behind_the_unit_transformer_to_machine_epsilon()
    {
        RelayVerdict v = PassiveOv(Xt, out GridModel m, out PowerFlowResult pf);
        (double vt, double vhv) = Hand(m, pf, G2(m), Xt);
        output.WriteLine($"|V_hv| {vhv:R}, hand |V_t| {vt:R}, engine {v.Quantity:R}, diff {Math.Abs(vt - v.Quantity) / (Eps * vt):R} eps; {v.Basis}");
        // The engine forms V_t in the HV voltage's own frame, the test at the solved angle: a few roundings apart.
        Assert.True(Math.Abs(vt - v.Quantity) <= 4 * Eps * vt, $"{vt:R} vs {v.Quantity:R}");
        // The hand relation in that frame: |V_t|^2 = (V + X_t Q / (MBase V))^2 + (X_t P / (MBase V))^2.
        (double p, double q) = SolvedGeneration.UnitOutput(m, pf, G2(m));
        double mb = m.Generators[G2(m)].MBase;
        double re = vhv + Xt * q / (mb * vhv), im = Xt * p / (mb * vhv);
        Assert.True(Math.Abs(Math.Sqrt(re * re + im * im) - v.Quantity) <= 4 * Eps * vt);
        // The HV bus is inside the pickup, the terminal beyond it: 59 operates on the terminal.
        Assert.True(vhv < OvPickup && vt > OvPickup);
        Assert.True(v.Operate);
        Assert.Contains("machine terminal voltage", v.Basis, StringComparison.Ordinal);
        Assert.Contains("behind the 0.1 pu unit transformer", v.Basis, StringComparison.Ordinal);
        Assert.Contains("HV bus 1.025 pu", v.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void Passive_59_without_a_unit_transformer_reads_the_network_bus_as_before()
    {
        RelayVerdict v = PassiveOv(0.0, out _, out PowerFlowResult pf);
        Assert.False(v.Operate);
        Assert.Equal(BitConverter.DoubleToInt64Bits(pf.Vm[pf.BusIds.ToList().IndexOf(2)]), BitConverter.DoubleToInt64Bits(v.Quantity));
        Assert.StartsWith("unit G2 relay 59-1: terminal voltage 1.025 pu at ", v.Basis, StringComparison.Ordinal);
        Assert.DoesNotContain("unit transformer", v.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void Passive_27_behind_a_unit_transformer_on_a_fault_snapshot_is_not_checkable()
    {
        (GridModel m, PowerFlowResult pf) = Case9(Xt);
        m = m.WithUnitProtection(Set(("G2", new (string, double)[] { ("uv1Pu", 0.8), ("uv1DelayS", 0.0) })));
        var ctx = new ProtectionContext(m, pf, new ProtectionSettings(), null);
        RelayVerdict v = Assert.Single(UnitRelayAssessment.Evaluate(ctx, m.UnitProtection!, faultSnapshot: true));
        Assert.Equal(ProtectionConfidence.NotCheckable, v.Confidence);
        Assert.Contains("fault snapshot carries no current for the unit", v.Basis, StringComparison.Ordinal);
    }

    // ---- full-model RMS loop ----

    private static RmsTransientStability.UnitRelayStudy FullModel(double xt, double delay, double duration)
    {
        (GridModel m, PowerFlowResult pf) = Case9(xt);
        return RmsTransientStability.StudyEventsWithUnitRelays(m.WithUnitProtection(Set(("G2", Ov(delay)))), pf, GenrouData(m),
            Array.Empty<RmsEvent>(), stepSeconds: Step, durationSeconds: duration);
    }

    /// <summary>At t = 0 the state is the solved one, so the |V_t| a no-delay 59 reads at step 0 is the hand value. The engine
    /// recovers V and I from E'' and the step's network solve, so it agrees to the solve's rounding, not bit for bit.</summary>
    [Fact]
    public void Full_model_59_reads_the_hand_machine_terminal_voltage_at_the_solved_state_and_trips_where_the_HV_bus_is_inside()
    {
        (GridModel m, PowerFlowResult pf) = Case9(Xt);
        (double vt, double vhv) = Hand(m, pf, G2(m), Xt);
        RmsTransientStability.UnitRelayStudy s = FullModel(Xt, 0.0, 0.05);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        output.WriteLine($"hand {vt:R}, engine {r.OperateQuantity:R}, diff {Math.Abs(vt - r.OperateQuantity!.Value):R}; {r.VoltageMeasuredAt}");
        Assert.Empty(s.NotEvaluated);
        Assert.True(r.Tripped);
        Assert.Equal(0.0, r.OperateSeconds);
        Assert.True(Math.Abs(r.OperateQuantity!.Value - vt) < 1e-12);
        Assert.True(Math.Abs(r.OperateQuantity!.Value - vhv) > 0.005);   // not the HV bus (0.0067 pu apart)
        Assert.Contains("the machine terminal, behind the 0.1 pu unit transformer", r.VoltageMeasuredAt, StringComparison.Ordinal);
        Assert.Contains("\"VoltageMeasuredAt\"", JsonSerializer.Serialize(s.UnitRelays), StringComparison.Ordinal);

        // With a delay: the terminal stays beyond the pickup at the steady state, so 59 operates after its delay.
        FullModelUnitRelayRecord d = Assert.Single(FullModel(Xt, 0.1, 0.5).UnitRelays);
        Assert.True(d.Tripped);
        Assert.Equal(0.0, d.PickupSeconds);
        Assert.Equal(0.1, d.OperateSeconds!.Value, 9);
    }

    [Fact]
    public void Full_model_59_without_a_unit_transformer_reads_the_network_bus_and_writes_no_new_member()
    {
        RmsTransientStability.UnitRelayStudy s = FullModel(0.0, 0.0, 0.05);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.False(r.Operated);
        Assert.Null(r.VoltageMeasuredAt);
        Assert.DoesNotContain("VoltageMeasuredAt", JsonSerializer.Serialize(s.UnitRelays), StringComparison.Ordinal);
    }

    // ---- adaptive RMS loop ----

    private static RmsTransientStability.AdaptiveStudy Adaptive(double xt, double delay)
    {
        (GridModel m, PowerFlowResult pf) = Case9(xt);
        return RmsTransientStability.StudyEventsAdaptiveWithUnitRelays(m.WithUnitProtection(Set(("G2", Ov(delay)))), pf, GenrouData(m),
            Array.Empty<RmsEvent>(), new AdaptiveOptions(), durationSeconds: 0.5);
    }

    [Fact]
    public void Adaptive_59_reads_the_hand_machine_terminal_voltage_and_trips_where_the_HV_bus_is_inside()
    {
        (GridModel m, PowerFlowResult pf) = Case9(Xt);
        (double vt, _) = Hand(m, pf, G2(m), Xt);
        RmsTransientStability.AdaptiveStudy s = Adaptive(Xt, 0.0);
        AdaptiveUnitRelayRecord r = Assert.Single(s.Run.UnitRelays!);
        output.WriteLine($"hand {vt:R}, engine {r.TripQuantity:R} at {r.TripSeconds:R}; {r.VoltageMeasuredAt}");
        Assert.Empty(s.UnitRelaysNotEvaluated!);
        Assert.True(r.Tripped);
        Assert.True(Math.Abs(r.TripQuantity!.Value - vt) < 1e-12);
        Assert.Contains("the machine terminal, behind the 0.1 pu unit transformer", r.VoltageMeasuredAt, StringComparison.Ordinal);

        AdaptiveUnitRelayRecord d = Assert.Single(Adaptive(Xt, 0.1).Run.UnitRelays!);
        Assert.True(d.Tripped);
        Assert.True(Math.Abs(d.TripSeconds!.Value - 0.1) < 1e-6);
    }

    [Fact]
    public void Adaptive_59_without_a_unit_transformer_reads_the_network_bus_and_writes_no_new_member()
    {
        RmsTransientStability.AdaptiveStudy s = Adaptive(0.0, 0.0);
        AdaptiveUnitRelayRecord r = Assert.Single(s.Run.UnitRelays!);
        Assert.False(r.Tripped);
        Assert.Null(r.VoltageMeasuredAt);
        Assert.DoesNotContain("VoltageMeasuredAt", JsonSerializer.Serialize(s.Run.UnitRelays), StringComparison.Ordinal);
    }

    // ---- determinism ----

    /// <summary>The three paths twice each give the same bits, and the bits are pinned (taken on this code), so the three CPU
    /// modes (AVX-512 / AVX2 / no hardware intrinsics) must give exactly these.</summary>
    [Fact]
    public void Terminal_27_59_gives_the_pinned_bits_in_every_path_on_every_run()
    {
        static string Bits()
        {
            RelayVerdict pv = PassiveOv(Xt, out _, out _);
            FullModelUnitRelayRecord fm = Assert.Single(FullModel(Xt, 0.1, 0.5).UnitRelays);
            RmsTransientStability.AdaptiveStudy ad = Adaptive(Xt, 0.1);
            AdaptiveUnitRelayRecord ar = Assert.Single(ad.Run.UnitRelays!);
            string run = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { ad.Run.TimeSeconds, ad.Run.DeltaRad, ad.Run.SpeedPu, ad.Run.UnitRelays })));
            return $"{BitConverter.DoubleToInt64Bits(pv.Quantity):X16} {BitConverter.DoubleToInt64Bits(fm.OperateQuantity!.Value):X16} "
                + $"{BitConverter.DoubleToInt64Bits(fm.OperateSeconds!.Value):X16} {BitConverter.DoubleToInt64Bits(ar.TripQuantity!.Value):X16} "
                + $"{BitConverter.DoubleToInt64Bits(ar.TripSeconds!.Value):X16} {run}";
        }
        string a = Bits();
        output.WriteLine(a);
        Assert.Equal(a, Bits());
        Assert.Equal(Pinned, a);
    }

    // 2.1.103: the last field (SHA-256 of the adaptive trajectory) re-captured; it was
    // 9510C54D6F480220744194275D47FF98D82CEEB595A89DE9F66D277613A73F93. The error test now gives rotor angles the fixed
    // scale atol + rtol x 1 rad in place of atol + rtol |delta|. The case9 angles here are below 1 rad, so their scale is
    // a little wider (at most rtol (1 - |delta|) per step, rtol = 1e-6) and the adaptive step sequence, hence its samples,
    // follows the new test: the hash of those samples must change. The five pinned scalars do not depend on that
    // sequence and are unchanged bit for bit: the passive and full-model quantities do not use the adaptive path, the
    // adaptive trip quantity is read at the trip, and the trip time is the event located to 1e-6 s around 0.1 s.
    private const string Pinned =
        "3FF081E344B077D5 3FF081E344B077CE 3FB999999999999A 3FF081E344B077CE 3FB9999DB22D0E56 88029D156E0C19D686782C692817F9CD7FBCAC817633B31A3DAB97EA93DE73F7";
}
