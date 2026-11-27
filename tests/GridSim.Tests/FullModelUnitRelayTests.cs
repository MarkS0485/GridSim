using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 stage 6: each unit's own relays in the full-model fixed-step RMS engine (opt-in,
/// <see cref="RmsTransientStability.StudyEventsWithUnitRelays"/>), including a dynamic loss of excitation (40) on the
/// apparent impedance at the machine terminals. A field failure trips its unit after the zone delay and the run continues;
/// a normal fault-and-clear does not trip 40; frequency, RoCoF, voltage and vector-shift stages act on the full model; a
/// trip of the last machine ends the run with the reason; quiet relays change no bit; the same run twice is the same bits.
/// </summary>
public sealed class FullModelUnitRelayTests(ITestOutputHelper output)
{
    private const double Step = 0.005;

    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal)))));

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model)
    {
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
            data.Add(new SubtransientInit.MachineData(
                H: gen.H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05));
        return data;
    }

    /// <summary>case9 with its generators named G&lt;bus&gt; (unit protection binds by name), solved.</summary>
    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" }).ToList(), generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        return (m, pf);
    }

    /// <summary>A controller that runs a SEXS until <paramref name="failAt"/> and then holds the field voltage at 0 (the field
    /// circuit lost): the step at which the time reaches the failure returns 0 from then on.</summary>
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

    private static RmsExciter Sexs() => RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0);

    private static List<RmsExciter?> Exciters(GridModel m, string? failUnit = null, double failAt = 0.0)
    {
        var list = new List<RmsExciter?>();
        foreach (Generator g in m.Generators)
            list.Add(g.Name == failUnit ? RmsExciter.FromUser(new FieldFailure(Sexs(), failAt, Step), "SEXS with field failure") : Sexs());
        return list;
    }

    /// <summary>Loss-of-excitation zones on the unit rating: zone 1 offset X'd/2 diameter 1.0 pu, 0.1 s; zone 2 offset
    /// X'd/2 diameter Xd, 0.5 s (the classic two-zone offset mho, X'd = 0.3, Xd = 1.0 on the machine base here).</summary>
    private static (string, double)[] Loe() => new (string, double)[]
    {
        ("loe1OffsetPu", 0.15), ("loe1DiameterPu", 1.0), ("loe1DelayS", 0.1),
        ("loe2OffsetPu", 0.15), ("loe2DiameterPu", 1.0), ("loe2DelayS", 0.5),
    };

    private static RmsTransientStability.UnitRelayStudy Run(GridModel m, PowerFlowResult pf, UnitProtectionSet set,
        IReadOnlyList<RmsEvent> events, double duration, IReadOnlyList<RmsExciter?>? exciters = null) =>
        RmsTransientStability.StudyEventsWithUnitRelays(m.WithUnitProtection(set), pf, GenrouData(m), events, stepSeconds: Step,
            durationSeconds: duration, exciters: exciters);

    private static int Samples(double duration) => (int)Math.Round(duration / Step) + 1;

    private void Dump(RmsTransientStability.UnitRelayStudy s)
    {
        foreach (FullModelUnitRelayRecord r in s.UnitRelays)
            output.WriteLine($"{r.Unit} {r.Device} op={r.Operated} trip={r.Tripped} pick={r.PickupSeconds} at={r.OperateSeconds} out={r.DisconnectedSeconds} q={r.OperateQuantity} z=({r.ImpedanceRPu}, {r.ImpedanceXPu})");
        foreach (var n in s.NotEvaluated) output.WriteLine($"not evaluated: {n.Unit} {n.Device}: {n.Reason}");
        if (s.EndReason is not null) output.WriteLine("end: " + s.EndReason);
    }

    private static int GeneratorAt(GridModel m, int bus)
    {
        for (int i = 0; i < m.Generators.Count; i++) if (m.Generators[i].Bus == bus) return i;
        throw new InvalidOperationException($"no generator at bus {bus}");
    }

    private static int BranchBetween(GridModel m, int a, int b)
    {
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch br = m.Branches[i];
            if (br.InService && ((br.From == a && br.To == b) || (br.From == b && br.To == a))) return i;
        }
        throw new InvalidOperationException($"no branch {a}-{b}");
    }

    // ---- 40: loss of excitation ----

    [Fact]
    public void A_field_failure_trips_the_unit_on_loss_of_excitation_after_its_delay_and_the_run_continues()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        const double failAt = 0.5, duration = 8.0;
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, Set(("G2", Loe())), Array.Empty<RmsEvent>(), duration, Exciters(m, "G2", failAt));
        Dump(s);
        Assert.Empty(s.NotEvaluated);
        Assert.Null(s.EndReason);
        FullModelUnitRelayRecord z1 = s.UnitRelays.Single(r => r.Device == "40-1");
        FullModelUnitRelayRecord z2 = s.UnitRelays.Single(r => r.Device == "40-2");
        FullModelUnitRelayRecord trip = new[] { z1, z2 }.Single(r => r.Tripped);
        Assert.True(trip.Operated);
        Assert.True(trip.PickupSeconds > failAt, "the impedance enters the zone only after the field is lost");
        double delay = trip.Device == "40-1" ? 0.1 : 0.5;
        // Operates at the first step at least its delay after pickup (never early, one-step resolution) ...
        Assert.True(trip.OperateSeconds - trip.PickupSeconds >= delay - 1e-9);
        Assert.True(trip.OperateSeconds - trip.PickupSeconds < delay + Step + 1e-9);
        // ... and the unit is out from the next step.
        Assert.Equal(trip.OperateSeconds!.Value + Step, trip.DisconnectedSeconds!.Value, 12);
        // The apparent impedance it read is inside the zone: below the origin, in the offset mho circle.
        Assert.True(trip.ImpedanceXPu < -0.15);
        Assert.True(trip.ImpedanceRPu * trip.ImpedanceRPu + Math.Pow(trip.ImpedanceXPu!.Value + 0.65, 2) < 0.25);
        // The run continues to its end with the other machines; the tripped machine carries no current from then.
        MultiMachineSubtransient.Result r = s.Result;
        Assert.Equal(Samples(duration), r.Machines[0].TimeSeconds.Count);
        Assert.True(r.StayedInSynchronism);
    }

    [Fact]
    public void A_normal_fault_and_clear_does_not_trip_loss_of_excitation()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        UnitProtectionSet set = Set(("G1", Loe()), ("G2", Loe()), ("G3", Loe()));
        RmsEvent[] ev = { new RmsBusFault(0.1, 7), new RmsFaultClear(0.18, 7) };
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, set, ev, 4.0, Exciters(m));
        Dump(s);
        Assert.Empty(s.NotEvaluated);
        Assert.All(s.UnitRelays, r => Assert.False(r.Operated, $"{r.Unit} {r.Device} operated on a normal fault-and-clear"));
        Assert.True(s.Result.StayedInSynchronism);
    }

    [Fact]
    public void Loss_of_excitation_is_listed_not_evaluated_where_the_terminal_or_the_rating_or_the_field_is_missing()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        var gens = m.AllGenerators.Select(g => g.Name == "G2" ? g with { UnitTransformerXPu = 0.05 } : g.Name == "G3" ? g with { MBase = 0.0 } : g).ToList();
        GridModel m2 = m.CopyWith(generators: gens, generatorsAreWholeFleet: true);
        PowerFlowResult pf2 = new NewtonRaphsonPowerFlow().Solve(m2);
        var data = GenrouData(m2).ToList();
        int g1 = GeneratorAt(m2, 1);
        data[g1] = data[g1] with { Xd = 0.30, Xq = 0.30, Xqp = 0.30 };   // the constant-EMF reduction on G1
        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUnitRelays(
            m2.WithUnitProtection(Set(("G1", Loe()), ("G2", Loe()), ("G3", Loe()))), pf2, data, Array.Empty<RmsEvent>(),
            stepSeconds: Step, durationSeconds: 0.1);
        Dump(s);
        Assert.Equal(2, s.UnitRelays.Count);   // G2 behind its unit transformer: 40-1 and 40-2 evaluated at the machine terminal
        Assert.All(s.UnitRelays, r => Assert.Equal("G2", r.Unit));
        Assert.Equal(4, s.NotEvaluated.Count);
        Assert.All(s.NotEvaluated.Where(n => n.Unit == "G1"), n => Assert.Contains("no field dynamics", n.Reason, StringComparison.Ordinal));
        Assert.All(s.NotEvaluated.Where(n => n.Unit == "G2"), n => Assert.Contains("unit transformer", n.Reason, StringComparison.Ordinal));
        Assert.All(s.NotEvaluated.Where(n => n.Unit == "G3"), n => Assert.Contains("no rating", n.Reason, StringComparison.Ordinal));
    }

    // ---- frequency, RoCoF, voltage, vector shift ----

    [Fact]
    public void An_underfrequency_stage_trips_its_unit_at_its_delay_on_the_full_model()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        double f0 = m.FrequencyHz;
        UnitProtectionSet set = Set(("G3", new[] { ("uf1Hz", f0 - 0.05), ("uf1DelayS", 0.2), ("relayNominalHz", f0) }));
        RmsEvent[] ev = { new RmsLoadStep(0.2, 5, 60.0, 10.0) };
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, set, ev, 5.0);
        Dump(s);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.True(r.Tripped);
        Assert.True(r.PickupSeconds > 0.2);
        Assert.True(r.OperateSeconds - r.PickupSeconds >= 0.2 - 1e-9 && r.OperateSeconds - r.PickupSeconds < 0.2 + Step + 1e-9);
        Assert.True(r.OperateQuantity < f0 - 0.05);
        // The tripped machine's speed trace keeps going (open circuit, no mechanical power) to the end of the run.
        Assert.Equal(Samples(5.0), s.Result.Machines[GeneratorAt(m, 3)].TimeSeconds.Count);
    }

    [Fact]
    public void A_frequency_stage_written_for_another_nominal_is_not_evaluated()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        UnitProtectionSet set = Set(("G3", new[] { ("uf1Hz", 49.0), ("uf1DelayS", 0.2), ("relayNominalHz", 50.0) }));
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, set, new RmsEvent[] { new RmsLoadStep(0.2, 5, 60.0, 10.0) }, 0.5);
        Assert.Empty(s.UnitRelays);
        Assert.Contains("50 Hz system", Assert.Single(s.NotEvaluated).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rocof_stage_trips_on_a_load_step_and_an_undervoltage_stage_waits_for_its_delay()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        UnitProtectionSet set = Set(
            ("G2", new[] { ("rocofHzPerS", 0.05), ("rocofDelayS", 0.0) }),
            ("G3", new[] { ("uv1Pu", 0.8), ("uv1DelayS", 0.5), ("uv2Pu", 0.8), ("uv2DelayS", 0.05) }));
        RmsEvent[] ev = { new RmsLoadStep(0.2, 5, 60.0, 10.0), new RmsBusFault(1.0, 6), new RmsFaultClear(1.1, 6) };
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, set, ev, 2.0);
        Dump(s);
        FullModelUnitRelayRecord rocof = s.UnitRelays.Single(r => r.Device == "81R");
        Assert.True(rocof.Tripped);
        Assert.Equal(0.2, rocof.OperateSeconds!.Value, 9);   // instantaneous, at the step the load steps
        Assert.True(Math.Abs(rocof.OperateQuantity!.Value) > 0.05);
        FullModelUnitRelayRecord uv1 = s.UnitRelays.Single(r => r.Device == "27-1");
        FullModelUnitRelayRecord uv2 = s.UnitRelays.Single(r => r.Device == "27-2");
        Assert.False(uv1.Operated);   // the 100 ms fault is shorter than its 0.5 s delay
        Assert.True(uv2.Tripped);     // and longer than 50 ms
        Assert.True(uv2.OperateSeconds >= 1.05 - 1e-9 && uv2.OperateSeconds < 1.1);
    }

    [Fact]
    public void A_vector_shift_stage_trips_at_the_network_change_that_steps_the_terminal_angle()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        UnitProtectionSet set = Set(("G3", new[] { ("vectorShiftDeg", 1.0), ("vectorShiftDelayS", 0.0) }));
        RmsEvent[] ev = { new RmsBranchTrip(0.3, BranchBetween(m, 6, 7)) };
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, set, ev, 1.0);
        Dump(s);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.True(r.Tripped);
        Assert.Equal(0.3, r.OperateSeconds!.Value, 9);
        Assert.True(r.OperateQuantity > 1.0);

        // A vector shift with a delay is not modelled: listed, never evaluated.
        RmsTransientStability.UnitRelayStudy d = Run(m, pf, Set(("G3", new[] { ("vectorShiftDeg", 1.0), ("vectorShiftDelayS", 0.1) })), ev, 0.5);
        Assert.Empty(d.UnitRelays);
        Assert.Contains("delay on it is not modelled", Assert.Single(d.NotEvaluated).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trip_of_the_last_connected_machine_ends_the_run_with_the_reason()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        (string, double)[] ov = { ("ov1Pu", 1.0000001), ("ov1DelayS", 0.0) };   // every case9 unit runs above 1 pu
        RmsTransientStability.UnitRelayStudy s = Run(m, pf, Set(("G1", ov), ("G2", ov), ("G3", ov)), Array.Empty<RmsEvent>(), 1.0);
        Dump(s);
        Assert.Equal(0.0, s.EndedAtSeconds);
        Assert.Contains("last one connected", s.EndReason, StringComparison.Ordinal);
        Assert.Equal(2, s.UnitRelays.Count(r => r.Tripped));
        FullModelUnitRelayRecord last = s.UnitRelays.Single(r => r.Operated && !r.Tripped);
        Assert.Null(last.DisconnectedSeconds);
        Assert.Single(s.Result.Machines[0].TimeSeconds);
    }

    // ---- bit identity and determinism ----

    private static void AssertSameBits(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b) =>
        Assert.Equal(FullModelKernelDigestTests.Digest(a, Array.Empty<RmsEvent>()), FullModelKernelDigestTests.Digest(b, Array.Empty<RmsEvent>()));

    [Fact]
    public void Unit_relays_that_never_pick_up_change_no_bit_of_the_run()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        RmsEvent[] ev = FullModelKernelDigestTests.Events(m);
        double f0 = m.FrequencyHz;
        (string, double)[] quiet = { ("uf1Hz", f0 - 10.0), ("uf1DelayS", 1.0), ("ov1Pu", 3.0), ("ov1DelayS", 1.0), ("loe1OffsetPu", 5.0),
            ("loe1DiameterPu", 0.1), ("loe1DelayS", 1.0), ("relayNominalHz", f0) };
        MultiMachineSubtransient.Result off = RmsTransientStability.StudyEvents(m, pf, GenrouData(m), ev, stepSeconds: Step, durationSeconds: 2.0,
            exciters: Exciters(m));
        RmsTransientStability.UnitRelayStudy on = Run(m, pf, Set(("G1", quiet), ("G2", quiet)), ev, 2.0, Exciters(m));
        Assert.All(on.UnitRelays, r => Assert.False(r.Operated));
        AssertSameBits(off, on.Result);
    }

    [Fact]
    public void The_same_relay_run_twice_gives_the_same_bits_and_records()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        UnitProtectionSet set = Set(("G2", Loe()), ("G3", new[] { ("rocofHzPerS", 0.05), ("rocofDelayS", 0.0) }));
        RmsTransientStability.UnitRelayStudy a = Run(m, pf, set, new RmsEvent[] { new RmsLoadStep(2.0, 5, 30.0, 5.0) }, 6.0, Exciters(m, "G2", 0.5));
        RmsTransientStability.UnitRelayStudy b = Run(m, pf, set, new RmsEvent[] { new RmsLoadStep(2.0, 5, 30.0, 5.0) }, 6.0, Exciters(m, "G2", 0.5));
        Assert.Contains(a.UnitRelays, r => r.Tripped);
        AssertSameBits(a.Result, b.Result);
        Assert.Equal(JsonSerializer.Serialize(a.UnitRelays), JsonSerializer.Serialize(b.UnitRelays));
        output.WriteLine("relay run digest: " + FullModelKernelDigestTests.Digest(a.Result, Array.Empty<RmsEvent>()) + " " +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(a.UnitRelays))));
    }

    [Fact]
    public void Without_unit_protection_the_entry_is_refused()
    {
        (GridModel m, PowerFlowResult pf) = Case9();
        Assert.Throws<ArgumentException>(() => RmsTransientStability.StudyEventsWithUnitRelays(m, pf, GenrouData(m), Array.Empty<RmsEvent>(),
            stepSeconds: Step, durationSeconds: 0.1));
    }

    // ---- dynamics-study ----

    [Fact]
    public void Dynamics_study_with_unit_protection_adds_the_full_model_companion_and_leaves_every_row_as_it_was()
    {
        var raw = Raw(Psse("case14.raw"));
        var keyed = raw with { Model = DynamicsStudyRunner.UnitKeyed(raw) };
        UnitProtectionSet set = Set(
            ("2:1", new[] { ("uf1Hz", 59.99), ("uf1DelayS", 0.2), ("relayNominalHz", 60.0) }),
            ("1:1", Loe().Append(("vectorShiftDeg", 5.0)).Append(("vectorShiftDelayS", 0.2)).ToArray()));
        DynamicsCaseRequest[] cases = UnitRelayInLoopTests.BaselineCases();
        var adaptive = new DynamicsAdaptiveRequest(new GridSim.Core.Dynamics.Models.Adaptive.AdaptiveOptions(RelativeTolerance: 1e-8,
            AbsoluteTolerance: 1e-10, MaxStepSeconds: 0.02, EventTimeToleranceSeconds: 1e-7), CctToleranceSeconds: 1e-3);
        DynamicsStudyResult plain = DynamicsStudyRunner.Run(Case14(cases, duration: 2.0) with { Adaptive = adaptive });
        DynamicsStudyResult with1 = DynamicsStudyRunner.Run(Case14(cases, duration: 2.0) with { Adaptive = adaptive with { UnitProtection = set } });
        DynamicsStudyResult with2 = DynamicsStudyRunner.Run(Case14(cases, duration: 2.0, parallelism: 2) with { Adaptive = adaptive with { UnitProtection = set } });
        Assert.Null(plain.FullModelUnitRelays);
        Assert.Equal(JsonSerializer.Serialize(plain.Cases), JsonSerializer.Serialize(with1.Cases));
        DynamicsFullModelUnitRelayEvidence fm = with1.FullModelUnitRelays!;
        output.WriteLine(JsonSerializer.Serialize(fm.Trips));
        Assert.Equal(cases.Length, fm.Rows.Count);
        Assert.All(fm.Rows, r => Assert.Equal(DynamicsDisposition.Evaluated, r.Disposition));
        Assert.Contains(fm.NotEvaluated, n => n.Unit == "1:1" && n.Device == "78");
        Assert.Contains(fm.Trips, t => t.Unit == "2:1" && t.Device == "81U-1");
        Assert.Equal(JsonSerializer.Serialize(fm), JsonSerializer.Serialize(with2.FullModelUnitRelays));
    }
}
