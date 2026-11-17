using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC3 P6.1 / P6.2 / P6.3: the dynamics study traced from RAW identity to DYR binding, initial phasor, model state, event
/// schedule and trajectory on the IEEE 14-bus fixture (tests/GridSim.Tests/fixtures/psse/case14.raw + case14.dyr), and on
/// the parallel-unit variant (fixtures/dynamics-study/case14-parallel.raw: the bus 2 unit split into two 50 MVA units).
/// Every simulation is the incumbent RMS engine; the oracles are the engine called directly, exact identities and
/// analytical equivalences.
/// </summary>
public class DynamicsStudyTests
{
    /// <summary>The CLI's own binding of a DYR file (Gated.PsseDynamics): the strict reader, then the loader.</summary>
    private static MachineDynamicsSet IncumbentBinding(PsseParseResult raw, string dyr)
    {
        PsseDyrResult r = PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        return DynamicsModelCoverage.BindDocument(r.Json, "case14.dyr", Schemas, raw.Model);
    }

    private static (PowerFlowResult Pf, List<SubtransientInit.MachineData> Data, List<RmsExciter?>? Exciters) Incumbent(PsseParseResult raw, string dyr)
    {
        MachineDynamicsSet set = IncumbentBinding(raw, dyr);
        GridModel m = raw.Model;
        PowerFlowResult pf = SolverSelector.CreateNewton(m).Solve(m, new PowerFlowOptions { Tolerance = 1e-8, MaxIterations = 100 });
        var data = Enumerable.Range(0, m.Generators.Count).Select(k => set.For(k)!.Data).ToList();
        List<RmsExciter?>? ex = null;
        if (set.Entries.Any(e => e.Exciter is not null))
        {
            ex = Enumerable.Repeat<RmsExciter?>(null, m.Generators.Count).ToList();
            foreach (MachineDynamicsEntry e in set.Entries) if (e.Exciter is not null) ex[e.GeneratorIndex] = e.Exciter.Create();
        }
        return (pf, data, ex);
    }

    private static void SameBits(IReadOnlyList<double> a, IReadOnlyList<double> b, string what)
    {
        Assert.Equal(a.Count, b.Count);
        for (int k = 0; k < a.Count; k++)
            Assert.True(BitConverter.DoubleToInt64Bits(a[k]) == BitConverter.DoubleToInt64Bits(b[k]), $"{what}[{k}]: {a[k]:R} vs {b[k]:R}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // P6.1: one supported case, RAW identity to trajectory.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Case14_is_traced_from_raw_identity_to_dyr_binding_initial_state_events_and_trajectory()
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        string dyr = Text(Study("case14-study.dyr"));
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(raw, dyr, new[] { Fault4("fault-4") }));

        // Coverage: all 11 records supported, all five units bound, by native identity.
        Assert.Equal("COMPLETE", r.Coverage.Status);
        Assert.True(r.Coverage.LikeForLike);
        Assert.Equal(11, r.Coverage.Records.Count);
        Assert.All(r.Coverage.Records, x => Assert.Equal(DyrRecordStatus.Supported, x.Status));
        Assert.Equal(new[] { 1, 2, 3, 6, 8 }, r.Coverage.Units.Select(u => u.Bus));
        Assert.All(r.Coverage.Units, u => Assert.Equal("BOUND", u.Status));
        DyrRecordCoverage gensal = r.Coverage.Records.Single(x => x.Model == "GENSAL");
        Assert.Equal((8, 2, "1", "machine", 1), (gensal.Line, gensal.Bus, gensal.Id, gensal.Role, gensal.GeneratorIndex));
        Assert.Equal("exact with derivations", gensal.Semantics);
        Assert.Contains(gensal.Derived, d => d.StartsWith("xqpPu: GENSAL has no q-axis transient circuit", StringComparison.Ordinal));
        Assert.Equal("exact", r.Coverage.Records.Single(x => x.Model == "TGOV1").Semantics);
        Assert.Contains("rotorAngle 2/'1'", gensal.AffectedTraces);
        DynamicsUnitCoverage u2 = r.Coverage.Units.Single(u => u.Bus == 2);
        Assert.Equal(("GENSAL (line 8)", "IEEET1", "PSS1A", "HYGOV"), (u2.Machine, u2.Exciter, u2.Stabiliser, u2.Governor));

        // Initial phasors: exactly the RMS preparation of the solved case with the incumbent binding.
        (PowerFlowResult pf, List<SubtransientInit.MachineData> data, List<RmsExciter?>? ex) = Incumbent(raw, dyr);
        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(raw.Model, pf, data);
        Assert.Equal(5, r.Initialisation!.Machines.Count);
        foreach (DynamicsInitialMachine m in r.Initialisation.Machines)
        {
            Assert.Equal(raw.Model.Generators[m.GeneratorIndex].Bus, m.Bus);
            Assert.Equal((p.Delta0[m.Slot], p.Eqpp0[m.Slot], p.Edpp0[m.Slot], p.Pm[m.Slot], p.Efd[m.Slot]), (m.DeltaRad, m.EqppPu, m.EdppPu, m.PmPu, m.EfdPu));
        }
        Assert.True(r.Initialisation.PowerFlow.Converged);

        // Event schedule: resolved by identity onto engine events, on the step grid.
        DynamicsCaseResult row = Assert.Single(r.Cases);
        Assert.Equal(DynamicsDisposition.Stable, row.Disposition);
        Assert.Equal(new[] { "bolted fault at bus 4 at 0.1 s", "fault clearance at bus 4 at 0.15 s", "branch 6 trip at 0.15 s" }, row.Events!.Select(e => e.EngineEvent));
        Assert.All(row.Events!, e => Assert.True(e.LandsOnRequestedTime));
        Assert.Equal(new[] { 20, 30, 30 }, row.Events!.Select(e => e.AppliedStep));
        Assert.Equal(new[] { 0, 1, 1 }, row.Events!.Select(e => e.SimultaneousGroup));
        Assert.Equal(4, raw.Model.Branches[6].From);
        Assert.Equal(5, raw.Model.Branches[6].To);

        // Trajectory: the very bits of the engine called directly with the incumbent binding.
        var events = new List<RmsEvent> { new RmsBusFault(0.1, 4), new RmsFaultClear(0.15, 4), new RmsBranchTrip(0.15, 6) };
        MultiMachineSubtransient.Result direct = RmsTransientStability.StudyEvents(raw.Model, pf, data, events, null, 0.005, 3.0, 2.0 * Math.PI, ex);
        Assert.Equal(direct.StayedInSynchronism, row.Run!.StayedInSynchronism);
        SameBits(direct.Machines[1].SpeedPu, row.Run.Channels.Single(c => c.Label == "speed 2/'1'").Values, "speed 2/'1'");
        var angle = direct.Machines[1].DeltaRad.Zip(direct.Machines[0].DeltaRad, (a, b) => (a - b) * 180.0 / Math.PI).ToList();
        SameBits(angle, row.Run.Channels.Single(c => c.Label == "rotorAngle 2/'1'").Values, "rotorAngle 2/'1'");
        Assert.Equal(direct.Machines[0].TimeSeconds.Count, row.Run.Samples);

        // The post-clear network is verified separately: 4-5 open, still one island, and it solves.
        Assert.Equal(1, row.PostEvent!.Islands);
        Assert.Equal("Converged", row.PostEvent.PowerFlowStatus);
        Assert.Equal(DynamicsDisposition.Stable, r.Status);
    }

    [Fact]
    public void The_observed_run_is_the_event_study_and_the_no_event_run_is_its_undisturbed_prefix()
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        (PowerFlowResult pf, List<SubtransientInit.MachineData> data, _) = Incumbent(raw, Text(Study("case14-study.dyr")));
        (_, _, List<RmsExciter?>? ex1) = Incumbent(raw, Text(Study("case14-study.dyr")));
        (_, _, List<RmsExciter?>? ex2) = Incumbent(raw, Text(Study("case14-study.dyr")));
        (_, _, List<RmsExciter?>? ex3) = Incumbent(raw, Text(Study("case14-study.dyr")));
        var events = new List<RmsEvent> { new RmsLoadStep(0.5, 9, 20.0, 5.0) };
        MultiMachineSubtransient.Result plain = RmsTransientStability.StudyEvents(raw.Model, pf, data, events, null, 0.005, 1.0, 2.0 * Math.PI, ex1);
        RmsTransientStability.ObservedStudy seen = RmsTransientStability.StudyEventsObserved(raw.Model, pf, data, events, new[] { 4, 9 },
            stepSeconds: 0.005, durationSeconds: 1.0, exciters: ex2);
        RmsTransientStability.ObservedStudy flat = RmsTransientStability.StudyEventsObserved(raw.Model, pf, data, Array.Empty<RmsEvent>(), new[] { 9 },
            stepSeconds: 0.005, durationSeconds: 1.0, exciters: ex3);
        for (int i = 0; i < plain.Machines.Count; i++)
        {
            SameBits(plain.Machines[i].DeltaRad, seen.Result.Machines[i].DeltaRad, $"delta {i}");
            SameBits(plain.Machines[i].SpeedPu, seen.Result.Machines[i].SpeedPu, $"speed {i}");
            SameBits(plain.Machines[i].EqppPu, seen.Result.Machines[i].EqppPu, $"eqpp {i}");
            // Up to and including the step the load step lands on (k = 100), the states have seen only the base network.
            SameBits(plain.Machines[i].DeltaRad.Take(101).ToList(), flat.Result.Machines[i].DeltaRad.Take(101).ToList(), $"flat delta {i}");
            Assert.NotEqual(plain.Machines[i].DeltaRad[^1], flat.Result.Machines[i].DeltaRad[^1]);
        }
        Assert.Equal(201, seen.Buses.TimeSeconds.Count);
        // The step's bus solve at k = 100 already carries the load step; at k = 99 it does not.
        Assert.Equal(flat.Buses.MagnitudePu[0][99], seen.Buses.MagnitudePu[1][99]);
        Assert.NotEqual(flat.Buses.MagnitudePu[0][100], seen.Buses.MagnitudePu[1][100]);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // P6.1: a parallel unit pair at one bus, so identity is never keyed by bus alone.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_units_at_one_bus_bind_by_machine_id_whatever_the_raw_order(bool swapRawOrder)
    {
        string rawText = Text(Study("case14-parallel.raw"));
        if (swapRawOrder)
        {
            const string one = "2, '1', 20, 21.2, 25, -20, 1.045, 0, 50, 0, 1, 0, 0, 1, 1, 100, 70, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1\n";
            const string two = "2, '2', 20, 21.2, 25, -20, 1.045, 0, 50, 0, 1, 0, 0, 1, 1, 100, 70, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1\n";
            Assert.Contains(one + two, rawText, StringComparison.Ordinal);
            rawText = rawText.Replace(one + two, two + one, StringComparison.Ordinal);
        }
        PsseParseResult raw = RawText(rawText, "case14-parallel");
        string dyr = Text(Study("case14-parallel-distinct.dyr"));
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(raw, dyr, new[] { Fault4("fault-4") },
            channels: Channels(M(2, "1"), M(2, "2")), dyrName: "case14-parallel-distinct.dyr"));

        Assert.Equal("COMPLETE", r.Coverage.Status);
        int g1 = Index(raw, 2, "1"), g2 = Index(raw, 2, "2");
        Assert.Equal(swapRawOrder ? 2 : 1, g1);
        Assert.Equal(swapRawOrder ? 1 : 2, g2);
        DynamicsUnitCoverage u1 = r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "1");
        DynamicsUnitCoverage u2 = r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "2");
        Assert.Equal((g1, "GENSAL (line 11)", "IEEET1", "HYGOV"), (u1.GeneratorIndex, u1.Machine, u1.Exciter, u1.Governor));
        Assert.Equal((g2, "GENROU (line 7)", "SEXS", "TGOV1"), (u2.GeneratorIndex, u2.Machine, u2.Exciter, u2.Governor));
        Assert.Equal(g2, r.Coverage.Records.Single(x => x.Model == "GENROU" && x.Bus == 2).GeneratorIndex);

        // The data each unit integrates is its own record's, at its own index (H on the 50 MVA machine base).
        MachineDynamicsSet set = DynamicsModelCoverage.Build(raw, dyr, "x.dyr", Schemas, DynamicsUnmodelledPolicy.Refuse).Set!;
        Assert.Equal((6.54, 1.25), (set.For(g1)!.Data.H, set.For(g1)!.Data.Xd));
        Assert.Equal((2.5, 1.70), (set.For(g2)!.Data.H, set.For(g2)!.Data.Xd));

        // The initial phasors and the traces follow the identity, not the bus or the file order.
        DynamicsInitialMachine i1 = r.Initialisation!.Machines.Single(m => m.Bus == 2 && m.Id == "1");
        DynamicsInitialMachine i2 = r.Initialisation.Machines.Single(m => m.Bus == 2 && m.Id == "2");
        Assert.Equal((g1, "IEEET1", "HYGOV"), (i1.GeneratorIndex, i1.Exciter, i1.Governor));
        Assert.Equal((g2, "SEXS", "TGOV1"), (i2.GeneratorIndex, i2.Exciter, i2.Governor));
        Assert.Equal(6.54 * 50.0 / 100.0, i1.HSystemSeconds, 12);
        Assert.Equal(2.5 * 50.0 / 100.0, i2.HSystemSeconds, 12);
        DynamicsCaseResult row = Assert.Single(r.Cases);
        Assert.NotEqual(row.Run!.Channels.Single(c => c.Label == "speed 2/'1'").Values[100], row.Run.Channels.Single(c => c.Label == "speed 2/'2'").Values[100]);
    }

    private static int Index(PsseParseResult raw, int bus, string id)
    {
        int k = raw.MachineKeys.Select((key, i) => (key, i)).Single(x => x.key.Bus == bus && x.key.Id == id).i;
        for (int j = 0; j < raw.Model.Generators.Count; j++) if (ReferenceEquals(raw.Model.Generators[j], raw.Model.AllGenerators[k])) return j;
        throw new InvalidOperationException();
    }

    [Fact]
    public void Two_identical_half_units_behave_as_the_single_unit_they_split()
    {
        // Analytical equivalence: two identical machines on one bus, each on a 50 MVA base with the same per-unit data, carry
        // equal currents and are together the 100 MVA machine (reactances and inertia scale by MBase / BaseMva). So the
        // pair's rotor angle and speed follow the single unit's, and the two units of the pair follow each other.
        DynamicsStudyResult single = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }));
        DynamicsStudyResult pair = DynamicsStudyRunner.Run(Request(Raw(Study("case14-parallel.raw")), Text(Study("case14-parallel.dyr")),
            new[] { Fault4("fault-4") }, channels: Channels(M(1), M(2, "1"), M(2, "2")), dyrName: "case14-parallel.dyr"));
        Assert.Equal("COMPLETE", pair.Coverage.Status);
        Assert.Equal(15, pair.Coverage.Records.Count);
        var s = single.Cases[0].Run!.Channels;
        var p = pair.Cases[0].Run!.Channels;
        double MaxDiff(IReadOnlyList<double> a, IReadOnlyList<double> b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
        Assert.True(MaxDiff(p.Single(c => c.Label == "rotorAngle 2/'1'").Values, p.Single(c => c.Label == "rotorAngle 2/'2'").Values) <= PairTwinDeg);
        Assert.True(MaxDiff(s.Single(c => c.Label == "rotorAngle 2/'1'").Values, p.Single(c => c.Label == "rotorAngle 2/'1'").Values) <= PairSingleDeg);
        Assert.True(MaxDiff(s.Single(c => c.Label == "speed 2/'1'").Values, p.Single(c => c.Label == "speed 2/'1'").Values) <= PairSingleSpeedPu);
    }

    // Tolerances of the split-unit equivalence. Observed difference: exactly 0 on this fixture (halving MBase is exact in
    // binary floating point), so these bound rounding only.
    private const double PairTwinDeg = 1e-9, PairSingleDeg = 1e-6, PairSingleSpeedPu = 1e-9;

    // ---------------------------------------------------------------------------------------------------------------
    // P6.2 / P6.3: initialisation, event contract, CCT and timestep.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_no_event_interval_holds_equilibrium_within_limits_derived_from_the_newton_tolerance()
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }, duration: 10.0));
        DynamicsEquilibriumEvidence e = r.Initialisation!.Equilibrium!;
        Assert.True(e.Held, string.Join("; ", e.Exceeded));
        Assert.Equal(Math.Max(1e-8, r.Initialisation.PowerFlow.MaxMismatchPu), e.ToleranceBasisPu);
        Assert.Equal(10.0 * e.ToleranceBasisPu, e.InitialAccelerationLimitPu);
        Assert.Equal(10.0 * e.ToleranceBasisPu * 10.0 / (2.0 * e.MinHSystemSeconds), e.SpeedDriftLimitPu, 15);
        Assert.Equal(2.0 * Math.PI * 60.0 * 10.0 * e.ToleranceBasisPu * 100.0 / (2.0 * e.MinHSystemSeconds), e.AngleDriftLimitRad, 12);
        Assert.True(e.InitialVoltageResidualPu <= e.InitialVoltageLimitPu);
        Assert.True(e.MaxSpeedDriftPu <= e.SpeedDriftLimitPu);
        Assert.True(e.MaxAngleDriftRad <= e.AngleDriftLimitRad);
        Assert.True(e.MaxVoltageDriftPu <= e.VoltageDriftLimitPu);
        Assert.True(e.StayedInSynchronism);
        Assert.Equal(3.0, e.MinHSystemSeconds);   // the bus 6 and 8 GENROU units, H = 3 s on 100 MVA
    }

    [Fact]
    public void Requested_and_applied_event_times_are_both_recorded_and_the_trace_shows_the_applied_one()
    {
        // 0.1023 s is not on the 5 ms grid: the engine applies it at the next step, 0.105 s (k = 21).
        var events = new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1023, Bus: 4),
            new DynamicsEventRequest(DynamicsEventKind.BranchTrip, 0.15, Branch: new DynamicsBranchId(5, 4, "1")),   // reversed orientation
            new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.15, Bus: 4),
        };
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { new DynamicsCaseRequest("off-grid", DynamicsCaseKind.Events, events) }));
        DynamicsCaseResult row = r.Cases[0];
        DynamicsAppliedEvent fault = row.Events![0];
        Assert.Equal((0.1023, 21, false), (fault.RequestedSeconds, fault.AppliedStep, fault.LandsOnRequestedTime));
        Assert.Equal(21 * 0.005, fault.AppliedSeconds);
        Assert.Equal(21 * 0.005 - 0.1023, fault.ShiftSeconds);
        // Simultaneous events keep input order: the trip (input 1) is processed before the clear (input 2), one group.
        Assert.Equal((1, 1, 2), (row.Events[1].Order, row.Events[1].SimultaneousGroup, row.Events[2].Order));
        Assert.Equal(1, row.Events[2].SimultaneousGroup);
        Assert.True(row.Events[1].LandsOnRequestedTime);
        // The bus 4 voltage collapses at the applied step, not at the requested time.
        IReadOnlyList<double> v4 = row.Run!.Channels.First(c => c.Label == "busVoltage 4" && c.Unit == "pu").Values;
        Assert.True(v4[20] > 0.9, $"v4[20] = {v4[20]}");
        Assert.True(v4[21] < 1e-3, $"v4[21] = {v4[21]}");
        Assert.True(v4[29] < 1e-3, $"v4[29] = {v4[29]}");
        Assert.True(v4[30] > 0.5, $"v4[30] = {v4[30]}");
    }

    [Fact]
    public void The_cct_bracket_keeps_both_endpoints_and_moves_under_a_refined_step_reported_on_its_own()
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[]
        {
            new DynamicsCaseRequest("cct-4", DynamicsCaseKind.CriticalClearing, Cct: new DynamicsCctRequest(4, new[] { Br(4, 5) }, 0.1, 1.0, RefinedStepSeconds: 0.001)),
        }) with { StepSeconds = 0.002 });
        DynamicsCaseResult row = r.Cases[0];
        DynamicsCctEvidence c = row.Cct!;
        Assert.Equal(DynamicsDisposition.CctBracketed, row.Disposition);
        foreach (DynamicsCctBracket b in new[] { c.Coarse, c.Refined! })
        {
            Assert.True(b.AtStepResolution);
            Assert.Equal(b.StableSteps + 1, b.UnstableSteps);
            Assert.Equal(b.StableSteps * b.StepSeconds, b.StableSeconds);
            Assert.True(b.StableEndpointVerified);
            Assert.True(b.UnstableEndpointVerified);
            Assert.False(b.NonMonotone);
        }
        Assert.Equal((0.002, 0.001), (c.Coarse.StepSeconds, c.Refined!.StepSeconds));
        Assert.Equal(c.Refined.StableSeconds - c.Coarse.StableSeconds, c.StableShiftSeconds);
        Assert.Equal(c.Refined.UnstableSeconds - c.Coarse.UnstableSeconds, c.UnstableShiftSeconds);
        Assert.Equal(DynamicsDisposition.Evaluated, r.Status);
        Assert.Equal(CctCoarseStableS, c.Coarse.StableSeconds, 12);
        Assert.Equal(CctRefinedStableS, c.Refined.StableSeconds, 12);
    }

    // The case14 bus 4 bracket (fault cleared by opening 4-5), as this engine gives it at 2 ms and 1 ms (observed values,
    // pinned so a change is seen; not a cross-tool claim).
    private const double CctCoarseStableS = 0.098, CctRefinedStableS = 0.099;

    [Fact]
    public void The_reader_fixture_case14_dyr_cannot_start_at_the_solved_point_and_the_study_says_so_before_any_row()
    {
        // psse/case14.dyr was written for the reader tests: its bus 1 TGOV1 VMAX (1.1 pu) is below the 2.32 pu the slack unit
        // carries on its 100 MVA base. Coverage is complete (every record maps), but the governor cannot be initialised, so
        // the study stops before the no-event interval (the legacy verb exits 2 for the same reason; see the CLI test).
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }, dyrText: Text(Psse("case14.dyr"))));
        Assert.Equal("COMPLETE", r.Coverage.Status);
        Assert.Equal(DynamicsDisposition.Incomplete, r.Status);
        Assert.Equal(DynamicsDisposition.NotRun, r.Cases[0].Disposition);
        Assert.True(r.Initialisation!.PowerFlow.Converged);
        Assert.Null(r.Initialisation.Equilibrium);
        Assert.Contains("TGOV1 governor cannot start at the solved operating point", r.Reasons[0], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // P6.3: an unsupported record, a mapping failure for the second machine on a shared bus.
    // ---------------------------------------------------------------------------------------------------------------

    private const string Esst1a = "     6 'ESST1A' 1  0.0 1 2 3 /\n";

    [Fact]
    public void An_unsupported_record_blocks_like_for_like_and_with_refuse_nothing_is_simulated()
    {
        string dyr = Text(Study("case14-study.dyr")) + Esst1a;
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }, dyrText: dyr));
        Assert.Equal("BLOCKED", r.Coverage.Status);
        Assert.False(r.LikeForLike);
        Assert.False(r.Coverage.Simulable);
        DyrRecordCoverage rec = r.Coverage.Records.Single(x => x.Model == "ESST1A");
        Assert.Equal((DyrRecordStatus.Skipped, 17, 6, "unknown"), (rec.Status, rec.Line, rec.Bus, rec.Role));
        Assert.Contains("ESST1A is a model the DYR reader does not map", rec.Reasons[0], StringComparison.Ordinal);
        Assert.Contains("rotorAngle 6/'1'", rec.AffectedTraces);
        Assert.Equal("APPROXIMATED", r.Coverage.Units.Single(u => u.Bus == 6).Status);
        Assert.Equal(DynamicsDisposition.Incomplete, r.Status);
        Assert.Equal(DynamicsDisposition.NotRun, r.Cases[0].Disposition);
        Assert.Null(r.Initialisation);
        Assert.Contains(r.Reasons, x => x.Contains("DYR line 17 ESST1A for machine '1' at bus 6: SKIPPED", StringComparison.Ordinal));

        // The existing strict reader path is untouched: it still refuses the file outright.
        var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(dyr, Raw(Psse("case14.raw")), "case14"));
        Assert.Contains("models this reader does not map: ESST1A x1 (first at line 17)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_leave_out_the_rows_run_but_every_disposition_is_indicative_and_there_is_no_verdict()
    {
        string dyr = Text(Study("case14-study.dyr")).Replace("     2 'PSS1A'  1   1  0  0.0  0.0", "     2 'PSS1A'  1   1  0  0.06  0.0", StringComparison.Ordinal) + Esst1a;
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4") }, dyrText: dyr,
            init: Init with { Unmodelled = DynamicsUnmodelledPolicy.LeaveOut }));
        Assert.Equal("BLOCKED", r.Coverage.Status);
        Assert.True(r.Coverage.Simulable);
        DyrRecordCoverage pss = r.Coverage.Records.Single(x => x.Model == "PSS1A");
        Assert.Equal(DyrRecordStatus.Skipped, pss.Status);
        Assert.Contains(pss.Reasons, x => x.Contains("the A1 / A2 filter", StringComparison.Ordinal));
        Assert.Equal("APPROXIMATED", r.Coverage.Units.Single(u => u.Bus == 2).Status);
        Assert.Null(r.Coverage.Units.Single(u => u.Bus == 2).Stabiliser);
        DynamicsCaseResult row = r.Cases[0];
        Assert.Equal(DynamicsDisposition.Stable, row.Disposition);
        Assert.True(row.Indicative);
        Assert.Equal(DynamicsDisposition.Incomplete, r.Status);
        Assert.False(r.LikeForLike);
        Assert.StartsWith("the model coverage is BLOCKED", r.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Gensal_saturation_left_out_is_an_approximation_not_a_supported_record()
    {
        string dyr = Text(Study("case14-study.dyr")).Replace("0.25  0.12  0.0  0.0 /", "0.25  0.12  0.1  0.3 /", StringComparison.Ordinal);
        DynamicsModelBinding b = DynamicsModelCoverage.Build(Raw(Psse("case14.raw")), dyr, "case14.dyr", Schemas, DynamicsUnmodelledPolicy.LeaveOut);
        DyrRecordCoverage gensal = b.Report.Records.Single(x => x.Model == "GENSAL");
        Assert.Equal((DyrRecordStatus.Approximated, "values left out"), (gensal.Status, gensal.Semantics));
        Assert.Contains(gensal.Reasons, x => x.StartsWith("GENSAL saturation for machine '1' at bus 2 (line 8)", StringComparison.Ordinal));
        Assert.Equal("APPROXIMATED", b.Report.Units.Single(u => u.Bus == 2).Status);
    }

    [Fact]
    public void A_record_for_a_second_machine_the_shared_bus_does_not_hold_is_a_mapping_failure_that_stops_the_study()
    {
        // case14-parallel holds units '1' and '2' at bus 2; the DYR names '3' instead of '2'.
        string dyr = Text(Study("case14-parallel.dyr")).Replace(" '2' ", " '3' ", StringComparison.Ordinal);
        foreach (DynamicsUnmodelledPolicy policy in new[] { DynamicsUnmodelledPolicy.Refuse, DynamicsUnmodelledPolicy.LeaveOut })
        {
            DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(Raw(Study("case14-parallel.raw")), dyr, new[] { Fault4("fault-4") },
                init: Init with { Unmodelled = policy }, dyrName: "case14-parallel.dyr"));
            Assert.Equal("BLOCKED", r.Coverage.Status);
            var unmatched = r.Coverage.Records.Where(x => x.Status == DyrRecordStatus.Unmatched).ToList();
            Assert.Equal(new[] { "GENSAL", "IEEET1", "PSS1A", "HYGOV" }, unmatched.Select(x => x.Model));
            Assert.All(unmatched, x => Assert.Equal((2, "3", (int?)null), (x.Bus, x.Id, x.GeneratorIndex)));
            Assert.Contains("which the RAW case does not hold", unmatched[0].Reasons[0], StringComparison.Ordinal);
            Assert.Equal("BOUND", r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "1").Status);
            Assert.Equal("UNBOUND", r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "2").Status);
            Assert.Equal(DynamicsDisposition.Incomplete, r.Status);
            Assert.Equal(DynamicsDisposition.NotRun, r.Cases[0].Disposition);
            Assert.Contains("names a machine the RAW case does not hold", r.Reasons[0], StringComparison.Ordinal);
        }
        // The incumbent reader refuses the same file (always, whatever the unmodelled option).
        Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(dyr, Raw(Study("case14-parallel.raw")), "x", new PsseDyrOptions { AllowUnmodelled = true }));
    }

    [Fact]
    public void A_second_machine_on_a_shared_bus_with_no_record_is_unbound_and_never_synthesised()
    {
        string dyr = Text(Study("case14-study.dyr"));   // gives bus 2 machine '1' only
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Request(Raw(Study("case14-parallel.raw")), dyr, new[] { Fault4("fault-4") }));
        Assert.Equal("UNBOUND", r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "2").Status);
        Assert.Equal("BOUND", r.Coverage.Units.Single(u => u.Bus == 2 && u.Id == "1").Status);
        Assert.Equal(DynamicsDisposition.NotRun, r.Cases[0].Disposition);
        Assert.Contains("an in-service unit has no supported machine model", r.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_rewritten_records_read_to_the_same_document_as_the_file()
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        string dyr = Text(Study("case14-study.dyr"));
        string strict = PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" }).Json;
        string rewritten = PsseDyrReader.Read(DynamicsModelCoverage.Rewrite(PsseDyrReader.ReadRecords(dyr)), raw, "case14",
            new PsseDyrOptions { SourceName = "case14.dyr", AllowUnmodelled = true }).Json;
        Assert.Equal(strict, rewritten);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // P6.2: batch over an explicit list, canonical order, independent state, serial vs parallel.
    // ---------------------------------------------------------------------------------------------------------------

    private static DynamicsCaseRequest[] Batch() => new[]
    {
        Fault4("fault-4-50ms", 0.15),
        new DynamicsCaseRequest("trip-unit-3", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.MachineTrip, 0.1, Machine: M(3)) }),
        new DynamicsCaseRequest("load-step-9", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.LoadStep, 0.1, Bus: 9, PMw: 30.0, QMvar: 10.0) }),
        Fault4("fault-4-300ms", 0.4),
        new DynamicsCaseRequest("trip-reclose-2-3", DynamicsCaseKind.Events, new[]
        {
            new DynamicsEventRequest(DynamicsEventKind.BranchTrip, 0.1, Branch: Br(2, 3)),
            new DynamicsEventRequest(DynamicsEventKind.BranchReclose, 0.6, Branch: Br(2, 3)),
        }),
    };

    [Fact]
    public void Serial_and_parallel_batches_give_the_same_bits_in_the_same_order()
    {
        DynamicsStudyResult serial = DynamicsStudyRunner.Run(Case14(Batch(), duration: 2.0, parallelism: 1));
        DynamicsStudyResult parallel = DynamicsStudyRunner.Run(Case14(Batch(), duration: 2.0, parallelism: 4));
        DynamicsStudyResult again = DynamicsStudyRunner.Run(Case14(Batch(), duration: 2.0, parallelism: 4));
        Assert.Equal(Batch().Select(c => c.Name), serial.Cases.Select(c => c.Name));
        foreach (DynamicsStudyResult other in new[] { parallel, again })
        {
            Assert.Equal(serial.Status, other.Status);
            Assert.Equal(serial.Cases.Select(c => (c.Index, c.Name, c.Disposition)), other.Cases.Select(c => (c.Index, c.Name, c.Disposition)));
            for (int i = 0; i < serial.Cases.Count; i++)
            {
                var a = serial.Cases[i].Run!; var b = other.Cases[i].Run!;
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.MaxAngleSpreadDeg), BitConverter.DoubleToInt64Bits(b.MaxAngleSpreadDeg));
                for (int c = 0; c < a.Channels.Count; c++) SameBits(a.Channels[c].Values, b.Channels[c].Values, $"{serial.Cases[i].Name} {a.Channels[c].Label}");
            }
        }
        // Independent state: a row run alone gives the bits it gives inside the batch.
        DynamicsStudyResult alone = DynamicsStudyRunner.Run(Case14(new[] { Batch()[3] }, duration: 2.0));
        for (int c = 0; c < alone.Cases[0].Run!.Channels.Count; c++)
            SameBits(alone.Cases[0].Run!.Channels[c].Values, serial.Cases[3].Run!.Channels[c].Values, "alone " + c);
        Assert.Equal(new DynamicsCaseCounts(5, 5, 5, 0, 0, 0), serial.Counts);
        // The machine trip leaves unit 3 out of the spread; its trace still runs.
        Assert.Equal("machine '1' at bus 3", serial.Cases[1].Events![0].Equipment);
        Assert.Equal("generator 2 trip at 0.1 s", serial.Cases[1].Events![0].EngineEvent);
    }

    [Fact]
    public void A_cancelled_batch_lists_every_unattempted_row_and_is_incomplete()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(Batch(), duration: 1.0), cts.Token);
        Assert.All(r.Cases, c => Assert.Equal(DynamicsDisposition.NotAttempted, c.Disposition));
        Assert.Equal(new DynamicsCaseCounts(5, 0, 0, 0, 0, 5), r.Counts);
        Assert.Equal(DynamicsDisposition.Incomplete, r.Status);
        Assert.Contains(r.Reasons, x => x.StartsWith("5 row(s) not attempted (cancelled): 'fault-4-50ms', 'trip-unit-3'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unstable_row_makes_the_study_unstable_and_a_failed_row_incomplete()
    {
        DynamicsStudyResult r = DynamicsStudyRunner.Run(Case14(new[] { Fault4("fault-4-held", 2.0) }, duration: 4.0));
        Assert.Equal(DynamicsDisposition.Unstable, r.Cases[0].Disposition);
        Assert.Equal(DynamicsDisposition.Unstable, r.Status);

        // Tripping both lines into bus 1 islands the slack machine's bus... the engine refuses a stage it cannot solve? Use
        // a clearance with no fault, which the engine refuses by name.
        DynamicsStudyResult f = DynamicsStudyRunner.Run(Case14(new[]
        {
            new DynamicsCaseRequest("clear-without-fault", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.FaultClear, 0.1, Bus: 4) }),
            Fault4("fault-4"),
        }));
        Assert.Equal(DynamicsDisposition.Failed, f.Cases[0].Disposition);
        Assert.Contains("there is no fault in place at bus 4", f.Cases[0].Reason, StringComparison.Ordinal);
        Assert.Equal(DynamicsDisposition.Stable, f.Cases[1].Disposition);
        Assert.Equal(DynamicsDisposition.Incomplete, f.Status);
        Assert.Equal(new DynamicsCaseCounts(2, 2, 1, 1, 0, 0), f.Counts);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Invalid input stops before any work.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("branch", "is not a branch of the RAW case")]
    [InlineData("machine", "is not a machine of the RAW case")]
    [InlineData("bus", "is not a bus of the RAW case")]
    [InlineData("duplicate", "is used twice")]
    [InlineData("step", "whole number of steps")]
    public void Unresolvable_identities_and_bad_options_are_input_errors(string what, string message)
    {
        DynamicsStudyRequest req = what switch
        {
            "branch" => Case14(new[] { new DynamicsCaseRequest("x", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.BranchTrip, 0.1, Branch: new DynamicsBranchId(4, 5, "2")) }) }),
            "machine" => Case14(new[] { new DynamicsCaseRequest("x", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.MachineTrip, 0.1, Machine: M(2, "2")) }) }),
            "bus" => Case14(new[] { new DynamicsCaseRequest("x", DynamicsCaseKind.Events, new[] { new DynamicsEventRequest(DynamicsEventKind.BusFault, 0.1, Bus: 99) }) }),
            "duplicate" => Case14(new[] { Fault4("x"), Fault4("x") }),
            _ => Case14(new[] { Fault4("x") }, duration: 1.0012),
        };
        var ex = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(req));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_kernel_landing_rule_is_mirrored_exactly()
    {
        Assert.Equal(20, DynamicsStudyRunner.AppliedStep(0.1, 0.005));
        Assert.Equal(48, DynamicsStudyRunner.AppliedStep(0.1 + 0.14, 0.005));   // onset + clearing formed in floating point
        Assert.Equal(21, DynamicsStudyRunner.AppliedStep(0.1023, 0.005));
        // Within a millionth of a step of t = 0 counts as reached at k = 0 (the engine refuses such an event time anyway).
        Assert.Equal(0, DynamicsStudyRunner.AppliedStep(1e-12, 0.005));
        Assert.Equal(1, DynamicsStudyRunner.AppliedStep(1e-6, 0.005));
    }
}
