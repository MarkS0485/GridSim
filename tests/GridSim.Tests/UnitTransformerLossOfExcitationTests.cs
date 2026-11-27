using System.Numerics;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5: loss of excitation (40) on a unit whose step-up transformer is folded into the machine model
/// (<see cref="Generator.UnitTransformerXPu"/>). The network bus is then the transformer's HV side; the relay sees the machine
/// terminal on its generator side. With I the unit's current into the network (generator convention) and X_t on the unit
/// rating, V_t = V_hv + j X_t I, so Z_t = V_t / I = Z_hv + j X_t (pu of the unit rating). The passive steady-state assessment
/// and the full-model RMS loop both read Z_t; the expected values here are formed independently with complex phasor
/// arithmetic (V_t built, then divided by I).
/// </summary>
public sealed class UnitTransformerLossOfExcitationTests(ITestOutputHelper output)
{
    private const double Step = 0.005;
    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal)))));

    /// <summary>The independent terminal impedance: V_t = V + j X_t I with I = conj(S / V), S = (P + jQ) / MBase, Z = V_t / I.</summary>
    private static Complex TerminalImpedance(double vm, double vaRad, double pMw, double qMvar, double mBase, double xt)
    {
        Complex v = Complex.FromPolarCoordinates(vm, vaRad);
        Complex i = Complex.Conjugate(new Complex(pMw / mBase, qMvar / mBase) / v);
        Complex vt = v + new Complex(0.0, xt) * i;
        return vt / i;
    }

    // ---- passive (steady state) ----

    [Fact]
    public void Passive_loss_of_excitation_reads_the_machine_terminal_behind_the_unit_transformer()
    {
        const double xt = 0.12;
        string dir = Path.Combine(RepoRoot(), "tests", "GridSim.Tests", "fixtures", "unit-protection");
        GridModel m0 = V2CaseLoader.Load(Path.Combine(dir, "three-unit.case.json"), SchemasDir);
        GridModel m = m0.CopyWith(generators: m0.AllGenerators.Select(g => g.Name == "CCGT B" ? g with { UnitTransformerXPu = xt } : g).ToList(),
            generatorsAreWholeFleet: true);
        Assert.NotNull(m.UnitProtection);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
        Assert.True(r.Converged);
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        int g = m.Generators.ToList().FindIndex(x => x.Name == "CCGT B");
        Generator gen = m.Generators[g];
        (double p, double q) = SolvedGeneration.UnitOutput(m, r, g);
        int bi = r.BusIds.ToList().IndexOf(gen.Bus);
        Complex z = TerminalImpedance(r.Vm[bi], r.VaDeg[bi] * Math.PI / 180.0, p, q, gen.MBase, xt);
        List<RelayVerdict> loe = rep.Verdicts.Where(x => x.UnitStage is not null && x.LocationName == "CCGT B" && x.Device.StartsWith("40-", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(loe);
        foreach (RelayVerdict v in loe)
        {
            string device = v.Device;
            UnitRelayStage st = v.UnitStage!;
            double radius = 0.5 * st.Diameter!.Value;
            double d = (z - new Complex(0.0, -(st.Pickup.Value + radius))).Magnitude;
            output.WriteLine($"{device}: Z_t = {z.Real:R} {z.Imaginary:R}j, d = {d:R}, engine {v.Quantity:R}, diff {Math.Abs(d - v.Quantity) / (Math.ScaleB(1.0, -52) * d):R} eps");
            // Machine epsilon: the engine forms k(P + jQ) + jX_t, the test V_t / I; a few roundings apart.
            Assert.True(Math.Abs(d - v.Quantity) <= 8 * Math.ScaleB(1.0, -52) * d, $"{device}: {d:R} vs {v.Quantity:R}");
            Assert.Equal(d < radius, v.Operate);
            Assert.Contains("at the machine terminal, behind the 0.12 pu unit transformer", v.Basis, StringComparison.Ordinal);
        }
        // Z_t is Z at the HV bus moved up by X_t (the hand relation).
        double k = r.Vm[bi] * r.Vm[bi] * gen.MBase / (p * p + q * q);
        Assert.True(Math.Abs(z.Imaginary - (k * q + xt)) <= 8 * Math.ScaleB(1.0, -52) * Math.Max(1.0, Math.Abs(z.Imaginary)));
        Assert.True(Math.Abs(z.Real - k * p) <= 8 * Math.ScaleB(1.0, -52) * Math.Abs(z.Real));
    }

    // ---- full-model RMS loop ----

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model) =>
        model.Generators.Select(gen => new SubtransientInit.MachineData(
            H: gen.H, Damping: 0.0,
            Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    private static (GridModel Model, PowerFlowResult Pf) Case9(Func<Generator, Generator> g2)
    {
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" }).Select(g => g.Name == "G2" ? g2(g) : g).ToList(),
            generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    private sealed class FieldFailure(RmsExciter inner, double failAt, double step) : IRmsFieldController
    {
        private int _k;
        public void Initialize(double fieldVoltagePu) { _k = 0; inner.Initialize(fieldVoltagePu); }
        public double EquilibriumErrorPu(double fieldVoltagePu) => inner.EquilibriumErrorPu(fieldVoltagePu);
        public double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds)
        {
            double t = _k++ * step;
            double e = inner.Step(voltageErrorPu, speedDeviationPu, dtSeconds);
            return t + 1e-9 >= failAt ? 0.0 : e;
        }
    }

    private static List<RmsExciter?> Exciters(GridModel m, string? failUnit, double failAt) =>
        m.Generators.Select(g => (RmsExciter?)(g.Name == failUnit
            ? RmsExciter.FromUser(new FieldFailure(RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0), failAt, Step), "SEXS with field failure")
            : RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0))).ToList();

    private static (string, double)[] Loe() => new (string, double)[]
    {
        ("loe1OffsetPu", 0.15), ("loe1DiameterPu", 1.0), ("loe1DelayS", 0.1),
        ("loe2OffsetPu", 0.15), ("loe2DiameterPu", 1.0), ("loe2DelayS", 0.5),
    };

    private void Dump(RmsTransientStability.UnitRelayStudy s)
    {
        foreach (FullModelUnitRelayRecord r in s.UnitRelays)
            output.WriteLine($"{r.Unit} {r.Device} op={r.Operated} trip={r.Tripped} pick={r.PickupSeconds:R} at={r.OperateSeconds:R} out={r.DisconnectedSeconds:R} q={r.OperateQuantity:R} z=({r.ImpedanceRPu:R}, {r.ImpedanceXPu:R})");
        foreach (var n in s.NotEvaluated) output.WriteLine($"not evaluated: {n.Unit} {n.Device}: {n.Reason}");
        if (s.EndReason is not null) output.WriteLine("end: " + s.EndReason);
    }

    /// <summary>The steady state at t = 0 is the solved one, so the impedance 40 reads at step 0 is the hand value from the
    /// solved P, Q and V at the HV bus and X_t. An underexcited unit (low voltage setpoint) with a wide zone of no delay
    /// operates at step 0 and records it. The engine recovers V and I from E'' and the step's network solve, so it agrees with
    /// the hand value to the solve's rounding (well inside 1e-12 pu), not bit for bit.</summary>
    [Fact]
    public void Full_model_loss_of_excitation_reads_the_hand_terminal_impedance_behind_the_unit_transformer_at_the_solved_state()
    {
        const double xt = 0.08;
        (GridModel m, PowerFlowResult pf) = Case9(g => g with { UnitTransformerXPu = xt, Vg = 0.95 });
        int g2 = m.Generators.ToList().FindIndex(x => x.Name == "G2");
        Generator gen = m.Generators[g2];
        (double p, double q) = SolvedGeneration.UnitOutput(m, pf, g2);
        int bi = pf.BusIds.ToList().IndexOf(gen.Bus);
        Complex z = TerminalImpedance(pf.Vm[bi], pf.VaDeg[bi] * Math.PI / 180.0, p, q, gen.MBase, xt);
        output.WriteLine($"G2 P {p:R} MW Q {q:R} Mvar V {pf.Vm[bi]:R} MBase {gen.MBase:R}: Z_t = {z.Real:R} {z.Imaginary:R}j");
        Assert.True(z.Imaginary < 0.0, "the case must leave G2 underexcited at its terminal for the zone to contain it");
        // Offset 0, diameter 100: contains every point below the R axis with |Z|^2 < -100 X.
        Assert.True(z.Real * z.Real + z.Imaginary * z.Imaginary < -100.0 * z.Imaginary);
        var zone = new (string, double)[] { ("loe1OffsetPu", 0.0), ("loe1DiameterPu", 100.0), ("loe1DelayS", 0.0) };
        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUnitRelays(m.WithUnitProtection(Set(("G2", zone))), pf,
            GenrouData(m), Array.Empty<RmsEvent>(), stepSeconds: Step, durationSeconds: 0.05);
        Dump(s);
        Assert.Empty(s.NotEvaluated);
        FullModelUnitRelayRecord rec = s.UnitRelays.Single();
        Assert.True(rec.Tripped);
        Assert.Equal(0.0, rec.OperateSeconds);
        Assert.Equal(Step, rec.DisconnectedSeconds);
        output.WriteLine($"diff R {Math.Abs(rec.ImpedanceRPu!.Value - z.Real):R}, X {Math.Abs(rec.ImpedanceXPu!.Value - z.Imaginary):R}");
        Assert.True(Math.Abs(rec.ImpedanceRPu!.Value - z.Real) < 1e-12);
        Assert.True(Math.Abs(rec.ImpedanceXPu!.Value - z.Imaginary) < 1e-12);
        // And not the HV-bus reading: X_t lies between them.
        Assert.True(Math.Abs(rec.ImpedanceXPu!.Value - (z.Imaginary - xt)) > 0.5 * xt);
    }

    private static RmsTransientStability.UnitRelayStudy FieldFailureRun(double xt)
    {
        (GridModel m, PowerFlowResult pf) = Case9(g => g with { UnitTransformerXPu = xt });
        return RmsTransientStability.StudyEventsWithUnitRelays(m.WithUnitProtection(Set(("G2", Loe()))), pf, GenrouData(m),
            Array.Empty<RmsEvent>(), stepSeconds: Step, durationSeconds: 8.0, exciters: Exciters(m, "G2", 0.5));
    }

    [Fact]
    public void A_field_failure_behind_a_unit_transformer_trips_loss_of_excitation_after_its_delay()
    {
        const double failAt = 0.5;
        RmsTransientStability.UnitRelayStudy s = FieldFailureRun(0.10);
        Dump(s);
        Assert.Empty(s.NotEvaluated);
        Assert.Null(s.EndReason);
        FullModelUnitRelayRecord trip = s.UnitRelays.Single(r => r.Tripped);
        Assert.StartsWith("40-", trip.Device, StringComparison.Ordinal);
        Assert.True(trip.PickupSeconds > failAt, "the impedance enters the zone only after the field is lost");
        double delay = trip.Device == "40-1" ? 0.1 : 0.5;
        Assert.True(trip.OperateSeconds - trip.PickupSeconds >= delay - 1e-9);
        Assert.True(trip.OperateSeconds - trip.PickupSeconds < delay + Step + 1e-9);
        Assert.Equal(trip.OperateSeconds!.Value + Step, trip.DisconnectedSeconds!.Value, 12);
        Assert.True(trip.ImpedanceXPu < -0.15);
        Assert.True(trip.ImpedanceRPu * trip.ImpedanceRPu + Math.Pow(trip.ImpedanceXPu!.Value + 0.65, 2) < 0.25);
        Assert.True(s.Result.StayedInSynchronism);
    }

    /// <summary>Determinism: the same run twice gives the same bits, and the record and run digest are pinned (taken on this
    /// code), so the three CPU modes (AVX-512 / AVX2 / no hardware intrinsics) must give exactly these bits.</summary>
    [Fact]
    public void The_field_failure_behind_a_unit_transformer_gives_the_pinned_bits_on_every_run()
    {
        RmsTransientStability.UnitRelayStudy a = FieldFailureRun(0.10);
        RmsTransientStability.UnitRelayStudy b = FieldFailureRun(0.10);
        string da = FullModelKernelDigestTests.Digest(a.Result, Array.Empty<RmsEvent>());
        Assert.Equal(da, FullModelKernelDigestTests.Digest(b.Result, Array.Empty<RmsEvent>()));
        string ra = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(a.UnitRelays)));
        Assert.Equal(ra, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(b.UnitRelays))));
        FullModelUnitRelayRecord t = a.UnitRelays.Single(r => r.Tripped);
        string bits = $"{t.Device} {BitConverter.DoubleToInt64Bits(t.OperateSeconds!.Value):X16} {BitConverter.DoubleToInt64Bits(t.ImpedanceRPu!.Value):X16} {BitConverter.DoubleToInt64Bits(t.ImpedanceXPu!.Value):X16}";
        output.WriteLine($"run {da}\nrecords {ra}\ntrip {bits}");
        Assert.Equal(PinnedRun, da);
        Assert.Equal(PinnedRecords, ra);
        Assert.Equal(PinnedTrip, bits);
    }

    private const string PinnedRun = "913D8E8A2297223972CF6C668E3FDE7009376299A339389666E7A5093C1BA89D";
    private const string PinnedRecords = "2C6914360AC98A44B94CF06DF7F905AD79DA0BB95497396086FE896AEFB76F3C";
    private const string PinnedTrip = "40-1 4015B851EB851EB8 BFA647243944ECC5 BFE3F6134F8767E8";
}
