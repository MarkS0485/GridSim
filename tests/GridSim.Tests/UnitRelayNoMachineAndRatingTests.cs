using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
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
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// Unit relays (2.1.074 fixes): the adaptive RMS loop stops, saying why, when a trip would leave no connected machine at
/// t = 0 or at a scheduled machine trip after unit relay trips; and a reverse-power (32) stage on a unit with no rating
/// (MBase 0) is listed as not evaluated in both RMS loops, as the passive check already lists it not checkable.
/// </summary>
public sealed class UnitRelayNoMachineAndRatingTests(ITestOutputHelper output)
{
    private static readonly DynamicsAdaptiveRequest Adaptive = new(
        new AdaptiveOptions(RelativeTolerance: 1e-8, AbsoluteTolerance: 1e-10, MaxStepSeconds: 0.02, EventTimeToleranceSeconds: 1e-7), CctToleranceSeconds: 1e-3);

    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal)))));

    /// <summary>case14 with each generator named G&lt;bus&gt; (one machine per bus), optionally with one unit's MBase changed.</summary>
    private static GridSim.Core.IO.Psse.PsseParseResult NamedRaw(string? noRatingUnit = null)
    {
        GridSim.Core.IO.Psse.PsseParseResult raw = Raw(Psse("case14.raw"));
        var gens = raw.Model.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" })
            .Select(g => g.Name == noRatingUnit ? g with { MBase = 0.0 } : g).ToList();
        return raw with { Model = raw.Model.CopyWith(generators: gens, generatorsAreWholeFleet: true) };
    }

    private static DynamicsAdaptiveEvidence Run(UnitProtectionSet units, DynamicsCaseRequest[] cases, double duration = 1.0,
        string? noRatingUnit = null) =>
        DynamicsStudyRunner.Run(Request(NamedRaw(noRatingUnit), Text(Study("case14-study.dyr")), cases, duration: duration)
            with { Adaptive = Adaptive with { UnitProtection = units } }).Adaptive!;

    // Instantaneous 59 just above 1 pu and 27 just below: a unit whose voltage is anything but 1 pu (to 1e-6) operates at once,
    // whichever way the earlier trips at the same instant moved it.
    private static readonly (string, double)[] Ov = { ("ov1Pu", 1.000001), ("ov1DelayS", 0.0), ("uv1Pu", 0.999999), ("uv1DelayS", 0.0) };

    private static DynamicsCaseRequest LoadStep() =>
        new("c", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.LoadStep, 0.3, Bus: 5, PMw: 30.0, QMvar: 5.0) });

    private void Dump(DynamicsAdaptiveRow row)
    {
        output.WriteLine(JsonSerializer.Serialize(row.Events));
        output.WriteLine("stop: " + row.StopReason);
    }

    /// <summary>M3: every unit beyond an instantaneous 59 or 27 at t = 0. The first four trip at t = 0; the fifth would
    /// leave no machine, so the run stops at t = 0 with the reason, and no state beyond t = 0 is reported.</summary>
    [Fact]
    public void A_t0_unit_trip_of_the_last_machine_stops_the_run_at_t0()
    {
        IReadOnlyList<string> names = NamedRaw().Model.Generators.Select(g => g.Name!).ToList();
        DynamicsAdaptiveRow row = Run(Set(names.Select(n => (n, Ov)).ToArray()), new[] { LoadStep() }).Rows[0];
        Dump(row);
        Assert.NotNull(row.StopReason);
        Assert.Contains("would leave no connected machine", row.StopReason, StringComparison.Ordinal);
        Assert.Contains("operated at 0 s", row.StopReason, StringComparison.Ordinal);
        Assert.Equal(new[] { 0.0 }, row.TimeSeconds!.Distinct().ToArray());   // nothing integrated beyond t = 0
        Assert.Equal(names.Count - 1, row.UnitRelays!.Count(r => r.Tripped));
        Assert.DoesNotContain(row.Events!, e => e.Source == "scheduled");     // the 0.3 s load step is never reached
    }

    /// <summary>M2: unit relays trip all but G1 at t = 0, then a scheduled machine trip of G1 at 0.1 s would leave no
    /// machine. The run stops there with the reason (scheduled and unit relay trips together) instead of integrating on
    /// with every machine disconnected.</summary>
    [Fact]
    public void A_scheduled_machine_trip_after_unit_relay_trips_that_leaves_no_machine_stops_the_run()
    {
        IReadOnlyList<string> names = NamedRaw().Model.Generators.Select(g => g.Name!).ToList();
        string last = names[0];
        var trip = new DynamicsCaseRequest("trip", DynamicsCaseKind.Events,
            new[] { new DynamicsEventRequest(DynamicsEventKind.MachineTrip, 0.1, Machine: M(int.Parse(last[1..], System.Globalization.CultureInfo.InvariantCulture))) });
        DynamicsAdaptiveRow row = Run(Set(names.Where(n => n != last).Select(n => (n, Ov)).ToArray()), new[] { trip }).Rows[0];
        Dump(row);
        Assert.Equal(names.Count - 1, row.UnitRelays!.Count(r => r.Tripped));
        Assert.NotNull(row.StopReason);
        Assert.Contains("would leave no connected machine", row.StopReason, StringComparison.Ordinal);
        Assert.Contains("scheduled and unit relay trips together", row.StopReason, StringComparison.Ordinal);
        Assert.Equal(0.1, row.TimeSeconds![^1]);
        Assert.Contains(row.Events!, e => e.Kind == "stop" && e.Source == "scheduled");
    }

    /// <summary>L1, adaptive loop: a 32 stage on a unit with MBase 0 has no pickup in MW, so it is listed not evaluated with
    /// the reason (as the passive check lists it not checkable), and the unit's other stages are still evaluated.</summary>
    [Fact]
    public void Adaptive_reverse_power_on_a_unit_with_no_rating_is_listed_not_evaluated()
    {
        const string unit = "G2";
        UnitProtectionSet set = Set((unit, new[] { ("reversePowerPu", 0.05), ("reversePowerDelayS", 0.0), ("ov1Pu", 1.5), ("ov1DelayS", 1.0) }));
        DynamicsAdaptiveEvidence e = Run(set, new[] { LoadStep() }, noRatingUnit: unit);
        UnitRelayNotEvaluated n = Assert.Single(e.UnitRelaysNotEvaluated!);
        Assert.Equal((unit, "32"), (n.Unit, n.Device));
        Assert.Contains("no rating", n.Reason, StringComparison.Ordinal);
        AdaptiveUnitRelayRecord r = Assert.Single(e.Rows[0].UnitRelays!);
        Assert.Equal("59-1", r.Device);
    }

    // ---- full-model engine ----

    private static IReadOnlyList<SubtransientInit.MachineData> GenrouData(GridModel model) =>
        model.Generators.Select(gen => new SubtransientInit.MachineData(
            H: gen.H, Damping: 0.0,
            Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
            Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05)).ToList();

    /// <summary>L1, full-model engine: the same rule.</summary>
    [Fact]
    public void Full_model_reverse_power_on_a_unit_with_no_rating_is_listed_not_evaluated()
    {
        GridModel m = IeeeCases.Case9();
        m = m.CopyWith(generators: m.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" })
            .Select(g => g.Name == "G3" ? g with { MBase = 0.0 } : g).ToList(), generatorsAreWholeFleet: true);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        (string, double)[] rp = { ("reversePowerPu", 0.05), ("reversePowerDelayS", 0.0) };
        RmsTransientStability.UnitRelayStudy s = RmsTransientStability.StudyEventsWithUnitRelays(
            m.WithUnitProtection(Set(("G2", rp), ("G3", rp))), pf, GenrouData(m), Array.Empty<RmsEvent>(), stepSeconds: 0.005, durationSeconds: 0.1);
        foreach (var x in s.NotEvaluated) output.WriteLine($"not evaluated: {x.Unit} {x.Device}: {x.Reason}");
        UnitRelayNotEvaluated n = Assert.Single(s.NotEvaluated);
        Assert.Equal(("G3", "32"), (n.Unit, n.Device));
        Assert.Contains("no rating", n.Reason, StringComparison.Ordinal);
        FullModelUnitRelayRecord r = Assert.Single(s.UnitRelays);
        Assert.Equal("G2", r.Unit);
    }
}
