using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Protection;
using Xunit;
using Xunit.Abstractions;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC5 stage 3: each unit's own relays in the adaptive RMS loop, opt-in. With the option off every adaptive number and
/// byte is the incumbent's (pinned by a digest taken on the kernel before any stage 3 source existed); with it on, a stage
/// that never picks up changes nothing, and an operating stage trips its unit at its delay and the run continues.
/// </summary>
public sealed class UnitRelayInLoopTests(ITestOutputHelper output)
{
    /// <summary>SHA-256 of the JSON of the adaptive evidence below. First captured on the per-asset-protection branch
    /// before any change to the adaptive kernel (RC5 stage 3 baseline), 1925790440B4AAB6F7BCF492CA8832A06600CBD19F83AA42F01CC8EA2EB85902.
    /// <para>
    /// Re-captured in 2.1.103, when the error test gave rotor angles the fixed scale atol + rtol x 1 rad in place of
    /// atol + rtol |delta|. Why the old value cannot stand: (1) the evidence JSON carries the declared scheme text
    /// (AdaptiveStepController.Scheme), which now states the angle scale, so the hashed bytes differ by construction and no
    /// correct 2.1.103 kernel can give the old SHA-256; (2) the step sequence follows the error test, so the samples move.
    /// Size of (2): each accepted step's local error is within its component scales under either test, and here
    /// rtol = 1e-8, atol = 1e-10, so an angle scale changes by at most rtol |1 - |delta|| per step; on case14 with
    /// rtol 1e-8 the measured rotor-angle movement over 120 s is at most 7.4e-7 degrees (26.5 of 53 bits over the run). What this test
    /// guards is unchanged: with unit relays off the adaptive evidence is that of the kernel without them, and the next
    /// test checks that byte for byte against a run with quiet unit relays on.
    /// </para></summary>
    private const string IncumbentAdaptiveDigest = "E5437F09553090273D016DBC07580A188C9867E1C6E5172436AFC306E53F2243";

    private static readonly DynamicsAdaptiveRequest Adaptive = new(
        new AdaptiveOptions(RelativeTolerance: 1e-8, AbsoluteTolerance: 1e-10, MaxStepSeconds: 0.02, EventTimeToleranceSeconds: 1e-7), CctToleranceSeconds: 1e-3);

    internal static DynamicsCaseRequest[] BaselineCases() => new[]
    {
        Fault4("f4", 0.15),
        new DynamicsCaseRequest("b", DynamicsCaseKind.Events, new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.2, Bus: 9),
            new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.27, Bus: 9),
        }),
        new DynamicsCaseRequest("c", DynamicsCaseKind.Events, new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.LoadStep, 0.3, Bus: 5, PMw: 30.0, QMvar: 5.0),
        }),
    };

    internal static string Digest(DynamicsAdaptiveEvidence e) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e))));

    /// <summary>case14 with each generator named G&lt;bus&gt; (the RAW loader leaves machines unnamed; unit protection binds
    /// to units by name, so the test names them; one machine per bus in case14).</summary>
    private static GridSim.Core.IO.Psse.PsseParseResult NamedRaw()
    {
        GridSim.Core.IO.Psse.PsseParseResult raw = Raw(Psse("case14.raw"));
        var gens = raw.Model.AllGenerators.Select(g => g with { Name = $"G{g.Bus}" }).ToList();
        return raw with { Model = raw.Model.CopyWith(generators: gens, generatorsAreWholeFleet: true) };
    }

    // The baseline digest run keeps the RAW case exactly as loaded; every unit relay run (on and off) uses the named copy.
    private static DynamicsAdaptiveEvidence Run(UnitProtectionSet? units, DynamicsCaseRequest[]? cases = null, int parallelism = 1, bool named = true) =>
        DynamicsStudyRunner.Run((named
                ? Request(NamedRaw(), Text(Study("case14-study.dyr")), cases ?? BaselineCases(), duration: 2.0, parallelism: parallelism)
                : Case14(cases ?? BaselineCases(), duration: 2.0, parallelism: parallelism))
            with { Adaptive = Adaptive with { UnitProtection = units } }).Adaptive!;

    /// <summary>The names unit protection binds to (G&lt;bus&gt;, in generator order).</summary>
    private static IReadOnlyList<string> Units() => NamedRaw().Model.Generators.Select(g => g.Name!).ToList();

    private static readonly AssetClassProvenance Test = new(ProvenanceTag.Synthetic, "test setting");

    private static UnitProtectionSet Set(params (string Unit, (string Key, double Value)[] Settings)[] units) =>
        new(Array.Empty<UnitProtectionClass>(), units.Select(u => new UnitProtectionAssignment(u.Unit, null,
            new UnitProtectionLayer(u.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                u.Settings.ToDictionary(x => x.Key, _ => Test, StringComparer.Ordinal)))));

    [Fact]
    public void With_unit_relays_off_the_adaptive_evidence_is_the_incumbents_byte_for_byte()
    {
        string d = Digest(Run(null, named: false));
        output.WriteLine("adaptive digest: " + d);
        Assert.Equal(IncumbentAdaptiveDigest, d);
    }

    [Fact]
    public void With_unit_relays_on_but_no_stage_picking_up_every_number_of_the_run_is_unchanged()
    {
        IReadOnlyList<string> names = Units();
        output.WriteLine("case14 units: " + string.Join(", ", names));
        double f0 = NamedRaw().Model.FrequencyHz;
        UnitProtectionSet quiet = Set(names.Select(n => (n, new[] { ("uf1Hz", f0 - 10.0), ("uf1DelayS", 1.0), ("of1Hz", f0 + 10.0), ("of1DelayS", 1.0), ("relayNominalHz", f0) })).ToArray());
        DynamicsAdaptiveEvidence off = Run(null), on = Run(quiet);
        Assert.Null(off.UnitRelayRule);
        Assert.NotNull(on.UnitRelayRule);
        Assert.Empty(on.UnitRelaysNotEvaluated!);   // every quiet stage is evaluated (and never picks up)
        for (int i = 0; i < off.Rows.Count; i++)
        {
            DynamicsAdaptiveRow a = off.Rows[i], b = on.Rows[i];
            Assert.Null(a.UnitRelays);
            Assert.All(b.UnitRelays!, r => Assert.False(r.Tripped));
            Assert.Equal(JsonSerializer.Serialize(a.TimeSeconds), JsonSerializer.Serialize(b.TimeSeconds));
            Assert.Equal(JsonSerializer.Serialize(a.Channels), JsonSerializer.Serialize(b.Channels));
            Assert.Equal(JsonSerializer.Serialize(a.Events), JsonSerializer.Serialize(b.Events));
            Assert.Equal((a.AcceptedSteps, a.RejectedSteps, a.Evaluations), (b.AcceptedSteps, b.RejectedSteps, b.Evaluations));
        }
    }

    [Fact]
    public void An_underfrequency_stage_trips_its_unit_its_delay_after_pickup_and_the_run_continues()
    {
        string unit = Units()[1];
        double f0 = NamedRaw().Model.FrequencyHz;   // the settings are written for the run's own nominal
        UnitProtectionSet set = Set((unit, new[] { ("uf1Hz", f0 - 0.01), ("uf1DelayS", 0.2), ("relayNominalHz", f0) }));
        DynamicsAdaptiveEvidence ev = Run(set, new[] { BaselineCases()[2] });
        Assert.Empty(ev.UnitRelaysNotEvaluated!);
        DynamicsAdaptiveRow row = ev.Rows[0];
        AdaptiveUnitRelayRecord r = Assert.Single(row.UnitRelays!);
        output.WriteLine(JsonSerializer.Serialize(row.Events));
        Assert.True(r.Tripped);
        Assert.Equal(unit, r.Unit);
        Assert.Equal("81U-1", r.Device);
        double delay = r.TripSeconds!.Value - r.PickupSeconds!.Value;
        Assert.InRange(delay, 0.2 - 1e-6, 0.2 + 1e-6);
        Assert.True(r.TripQuantity < f0 - 0.01);
        Assert.Contains(row.Events!, e => e.Source == "unit-relay" && e.Kind == "trip");
        Assert.Equal(2.0, row.TimeSeconds![^1]);   // the run continued to its end
        Assert.Contains("(sourced, unit, Synthetic)", r.Settings, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rocof_stage_with_an_instantaneous_setting_trips_at_the_load_step()
    {
        string unit = Units()[1];
        var legacy = new UnitProtectionSet(Array.Empty<UnitProtectionClass>(), new[]
        {
            new UnitProtectionAssignment(unit, null, new UnitProtectionLayer(new Dictionary<string, double> { ["rocofHzPerS"] = 0.001, ["rocofDelayS"] = 0.0 },
                new Dictionary<string, AssetClassProvenance> { ["rocofHzPerS"] = Test, ["rocofDelayS"] = Test })),
        });
        DynamicsAdaptiveRow row = Run(legacy, new[] { BaselineCases()[2] }).Rows[0];
        AdaptiveUnitRelayRecord r = Assert.Single(row.UnitRelays!);
        Assert.True(r.Tripped);
        Assert.Equal(0.3, r.TripSeconds!.Value);   // the load step instant: RoCoF steps there, and the stage has no delay
    }

    [Fact]
    public void A_vector_shift_stage_trips_on_the_angle_jump_of_a_fault()
    {
        string unit = Units()[1];
        UnitProtectionSet set = Set((unit, new[] { ("vectorShiftDeg", 0.5), ("vectorShiftDelayS", 0.0) }));
        DynamicsAdaptiveRow row = Run(set, new[] { BaselineCases()[1] }).Rows[0];
        AdaptiveUnitRelayRecord r = Assert.Single(row.UnitRelays!);
        Assert.True(r.Tripped);
        Assert.Equal(0.2, r.TripSeconds!.Value);
        Assert.True(r.TripQuantity > 0.5);
    }

    [Fact]
    public void Loss_of_excitation_is_listed_as_not_evaluated_with_the_reason()
    {
        string unit = Units()[1];
        UnitProtectionSet set = Set((unit, new[] { ("loe1OffsetPu", 0.1), ("loe1DiameterPu", 1.0), ("loe1DelayS", 0.5) }));
        DynamicsAdaptiveEvidence e = Run(set, new[] { BaselineCases()[2] });
        UnitRelayNotEvaluated n = Assert.Single(e.UnitRelaysNotEvaluated!);
        Assert.Equal((unit, "40-1"), (n.Unit, n.Device));
        Assert.Contains("needs a field model", n.Reason, StringComparison.Ordinal);
        Assert.Empty(e.Rows[0].UnitRelays!);
    }

    [Fact]
    public void Unit_relay_runs_are_bit_identical_whatever_the_row_parallelism()
    {
        IReadOnlyList<string> names = Units();
        double f0 = NamedRaw().Model.FrequencyHz;
        UnitProtectionSet set = Set(names.Skip(1).Select(n => (n, new[] { ("uf1Hz", f0 - 0.01), ("uf1DelayS", 0.2), ("rocofHzPerS", 0.05), ("rocofDelayS", 0.1), ("relayNominalHz", f0) })).ToArray());
        string one = JsonSerializer.Serialize(Run(set, parallelism: 1));
        Assert.Equal(one, JsonSerializer.Serialize(Run(set, parallelism: 1)));
        Assert.Equal(one, JsonSerializer.Serialize(Run(set, parallelism: 3)));
    }

    [Fact]
    public void A_unit_name_the_case_does_not_have_is_an_input_error_before_any_work()
    {
        UnitProtectionSet set = Set(("No such unit", new[] { ("uf1Hz", 49.0), ("uf1DelayS", 0.2) }));
        var ex = Assert.Throws<InvalidDataException>(() => Run(set));
        Assert.StartsWith("adaptive.unitProtection: ", ex.Message, StringComparison.Ordinal);
    }
}
