using System.Text;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.Estimation;
using GridSim.Core.Events;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Replay;
using GridSim.Core.Scenarios;
using GridSim.Tests.JsonSourceGen.Legacy;
using Xunit;

namespace GridSim.Tests.JsonSourceGen;

/// <summary>
/// UI programme Phase 0.1: the byte-for-byte proof for the eight exporters whose documents used to be anonymous objects
/// serialised by reflection (solve, estimate, frequency, H=0 study, harmonic, impedance stability, RMS events, small
/// signal). Each production exporter now writes named types through a source-generated context; the old exporter is
/// kept as a frozen, test-only copy (tests/GridSim.Tests/JsonSourceGen/Legacy, the 2.0.48 source, class renamed only).
/// For every representative input below both write the document and the bytes must be identical. The inputs are
/// chosen to reach every branch of every document: optional blocks present and absent (WhenWritingNull), both shapes of
/// a polymorphic member (the solve block or an estimate block; a chi-square or a LAV estimate), NaN and infinity values
/// where the options allow named literals, null-written sentinels, out-of-service plant, ZIP loads, cost curves, tap
/// changers and boundaries.
/// </summary>
public class JsonSourceGenExportEquivalenceTests
{
    private static readonly SolveExportMeta Meta = new("phase-0.1", "cpu", "deadbeef", "2026-10-20T00:00:00Z", 12.5, "test-host");

    private static void SameBytes(string legacy, string current, string what)
    {
        byte[] a = Encoding.UTF8.GetBytes(legacy), b = Encoding.UTF8.GetBytes(current);
        if (a.AsSpan().SequenceEqual(b)) return;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 60);
        Assert.Fail($"{what}: the bytes differ at offset {i} of {a.Length} / {b.Length}:\nreflection:       ...{legacy.Substring(from, Math.Min(120, legacy.Length - from))}...\nsource-generated: ...{current.Substring(from, Math.Min(120, current.Length - from))}...");
    }

    // ---- Solve export ------------------------------------------------------------------------------------------------

    /// <summary>
    /// case9 with every optional model member in play: a ZIP load, an unlimited (infinite) reactive limit, a cost curve
    /// on every unit plus a reactive cost, an out-of-service unit and branch, the fault and dynamics data, a tap changer
    /// and a boundary.
    /// </summary>
    private static GridModel RichCase9()
    {
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.Select((b, i) => i == 4 ? b with { Zip = new ZipLoad(0.2, 0.3, 0.5, 0.1, 0.1, 0.8), VminPu = 0.95, VmaxPu = 1.05, Name = "Bus \"5\"" } : b).ToList();
        var cost = new GeneratorCost(GeneratorCostModel.Polynomial, 1500, 0, new[] { 0.11, 5.0, 150.0 });
        var gens = m.AllGenerators.Select((g, i) => i switch
        {
            0 => g with { Qmax = double.PositiveInfinity, Cost = cost, ReactiveCost = cost, DynamicsClass = "steam", SubtransientXdPu = 0.2 },
            1 => g with { ConverterInterfaced = true, ConverterFaultCurrentPu = 1.1, GridForming = true, UnitTransformerXPu = 0.1 },
            _ => g,
        }).Append(new Generator(5, Pg: 0, MBase: 50, Name: "spare", Type: "ocgt", InService: false)).ToList();
        var branches = m.Branches.Select((e, i) => i switch
        {
            0 => e with { Name = "T1", IsTransformer = true, TapRatio = 1.025, PhaseShiftDeg = 2.5, R0 = 0.01, X0 = 0.05, FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.Delta, FromNeutralXPu = 0.01 },
            1 => e with { Name = "L2", LengthKm = 42.5, Equipment = "ohl", RateBMva = 300, RateCMva = 350, DynamicsClass = "line" },
            8 => e with { Name = "L9", InService = false },
            _ => e with { Name = $"B{i}" },
        }).ToList();
        return m.CopyWith(buses: buses, generators: gens, branches: branches,
            oltcs: new[] { new Oltc("T1", 4, 1.01) },
            boundaries: new[] { new Boundary("B-test", 150, new[] { new BoundaryBranch("L2", 1), new BoundaryBranch("B3", -1) }) });
    }

    [Fact]
    public void The_solve_export_writes_the_same_bytes()
    {
        int n = 0;
        foreach (GridModel model in new[] { IeeeCases.Case9(), IeeeCases.Case14(), IeeeCases.Case30(), IeeeCases.Case118(), RichCase9() })
        {
            var trace = new SolveTrace();
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
            SameBytes(LegacySolveExport.ToJson(model, r, trace, Meta), SolveExport.ToJson(model, r, trace, Meta), model.Name);
            SameBytes(LegacySolveExport.ToJson(model, r, trace, Meta, "slack Q at its limit", null, null, 1e-12),
                SolveExport.ToJson(model, r, trace, Meta, "slack Q at its limit", null, null, 1e-12), model.Name + " (note, tight)");
            n++;
        }
        // A non-converged solve: an iteration cap leaves a large mismatch (and the trace of every iteration).
        {
            GridModel model = IeeeCases.Case30();
            var trace = new SolveTrace();
            PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace, MaxIterations = 1 });
            SameBytes(LegacySolveExport.ToJson(model, r, trace, Meta), SolveExport.ToJson(model, r, trace, Meta), "case30 capped");
        }
        Assert.Equal(5, n);
    }

    [Fact]
    public void The_estimated_solve_export_writes_the_same_bytes()
    {
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        PowerFlowResult projected = est.ToPowerFlowResult(model);
        ExportPhysics physics = EstimateExport.PhysicsReport(model, est);
        var estimate = new SolveExportEstimate(est.Status.ToString(), est.MaxStateUpdate, "wls");
        var trace = new SolveTrace();
        SameBytes(LegacySolveExport.ToJson(model, projected, trace, Meta, null, physics, estimate),
            SolveExport.ToJson(model, projected, trace, Meta, null, physics, estimate), "estimated solve");
    }

    // ---- Estimate export ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_estimate_export_writes_the_same_bytes()
    {
        GridModel case9 = IeeeCases.Case9(), case14 = IeeeCases.Case14();
        PowerFlowResult t9 = new NewtonRaphsonPowerFlow().Solve(case9), t14 = new NewtonRaphsonPowerFlow().Solve(case14);

        // WLS with a trace (chi-square ran: true / false / null cases follow from the sets below).
        MeasurementSet s9 = SyntheticMeasurements.Sample(case9, t9) with { Note = "noted" };
        var trace = new EstimationTrace();
        EstimationResult wls = new WlsStateEstimator().Estimate(case9, s9, new EstimationOptions { Trace = trace });
        SameBytes(LegacyEstimateExport.ToJson(case9, s9, wls, Meta, trace), EstimateExport.ToJson(case9, s9, wls, Meta, trace), "case9 wls traced");

        // Bad data removed (the Q13 set: one injection pushed 30 sigma) and detection off.
        MeasurementSet exact = SyntheticMeasurements.Sample(case14, t14, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = exact.Measurements.ToList();
        int victim = ms.FindIndex(x => x.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };
        MeasurementSet bad = exact with { Measurements = ms };
        foreach (EstimationOptions opt in new[]
                 {
                     new EstimationOptions(), new EstimationOptions { DetectBadData = false },
                     new EstimationOptions { Method = EstimationMethod.Huber },
                     new EstimationOptions { Method = EstimationMethod.Lav },
                 })
        {
            EstimationResult r = new WlsStateEstimator().Estimate(case14, bad, opt);
            SameBytes(LegacyEstimateExport.ToJson(case14, bad, r, Meta), EstimateExport.ToJson(case14, bad, r, Meta),
                $"case14 bad data, {opt.Method}, detect {opt.DetectBadData}");
        }

        // A LAV estimate on noisy data (the LAV-shaped estimate block).
        MeasurementSet s14 = SyntheticMeasurements.Sample(case14, t14, new SyntheticMeasurementOptions { Seed = 3 });
        EstimationResult lav = new WlsStateEstimator().Estimate(case14, s14, new EstimationOptions { Method = EstimationMethod.Lav });
        Assert.False(lav.ChiSquareIsVerdict);
        SameBytes(LegacyEstimateExport.ToJson(case14, s14, lav, Meta), EstimateExport.ToJson(case14, s14, lav, Meta), "case14 lav");
    }

    // ---- Frequency export --------------------------------------------------------------------------------------------

    [Fact]
    public void The_frequency_export_writes_the_same_bytes()
    {
        var meta = new FrequencyExportMeta("phase-0.1", "deadbeef", "2026-10-20T00:00:00Z", 1.5, "test-host", ProvenanceTag.Synthetic);
        var events = new[] { new FrequencyEvent(2.0, 300), new FrequencyEvent(1.0, -1320) };
        var service = new SystemResponseService(Mw: 500, DeadbandHz: 0.015, FullDeliveryDeviationHz: 0.5,
            TimeConstantS: 0.5, KneeDeviationHz: 0.2, KneeFraction: 0.05);
        (double Sys, DynamicsConfig Cfg)[] runs =
        {
            (120_000, new DynamicsConfig()),
            (120_000, new DynamicsConfig { DeviceRotationalInertiaMwS = 40_000, FastResponseMw = 1140 }),
            (150_000, new DynamicsConfig { Forming = FormingMode.Vsm, VirtualInertiaMwS = 30_000, FormingDampingMwPerHz = 400 }),
            (0, new DynamicsConfig { Forming = FormingMode.Vsm, VirtualInertiaMwS = 30_000, FormingDampingMwPerHz = 400, DampingPctPerHz = 0, ResponsiveFraction = 0 }),
            (120_000, new DynamicsConfig { SystemResponseServices = [service], InitialFrequencyHz = 49.95 }),
        };
        foreach ((double sys, DynamicsConfig cfg) in runs)
        {
            FrequencyResponse resp = FrequencyDynamics.Simulate(sys, 30_000, events, cfg);
            SameBytes(LegacyFrequencyExport.ToJson(sys, 30_000, events, cfg, resp, meta),
                FrequencyExport.ToJson(sys, 30_000, events, cfg, resp, meta), $"frequency sys={sys} forming={cfg.Forming}");
        }
    }

    // ---- H=0 study export --------------------------------------------------------------------------------------------

    [Fact]
    public void The_h0_study_export_writes_the_same_bytes()
    {
        const double inertiaMwS = 155_000, demandMw = 32_000, lossMw = 1320;
        var cfg0 = new DynamicsConfig();
        FrequencyResponse truth = FrequencyDynamics.Simulate(inertiaMwS, demandMw, new[] { new FrequencyEvent(1.0, -lossMw) }, cfg0);
        var ev = new GridEvent(
            "syn", "self-consistent", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), inertiaMwS, demandMw,
            new List<PowerStep> { new(1.0, -lossMw, "loss", ProvenanceTag.Synthetic) }, new List<RecordedAction>(),
            new RecordedOutcome(truth.InitialRocofHzPerS, truth.NadirHz, truth.SettleHz, 0, null, ProvenanceTag.Synthetic),
            ProvenanceTag.Synthetic);
        AssetClass gfl = AssetTestHelpers.GflClass("gfl-test", rocofTripHzPerS: 1.0);
        AssetClass gfm = AssetTestHelpers.GfmClass("gfm-test", synInertiaMwsPerHz: 1600.0, dampingMwPerHz: 200.0);
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = new List<Generator>
            {
                new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),
                new(1, Pg: 2_000, MBase: 3_000, H: 0, Name: "w1", Type: "wind"),
                new(1, Pg: 800, MBase: 1_500, H: 0, Name: "s1", Type: "storage"),
            },
            Branches = Array.Empty<Branch>(),
            AssetDynamics = AssetTestHelpers.Bundle(new[] { gfl, gfm },
                new AssetBinding("wind*", "gfl-test"), new AssetBinding("storage", "gfm-test")),
        };
        var meta = new H0StudyMeta("syn", "deadbeef", "2026-10-20T00:00:00Z", "test-host", "gfm-test", "gfl-test");
        foreach (Corner[] corners in new[] { new[] { Corner.Best }, new[] { Corner.Best, Corner.Nominal, Corner.Worst } })
        {
            // The 0.0 scale with no grid-forming fraction is a forming-dependent cell: its RoCoF and nadir are null.
            var spec = new H0StudySpec(ev, model, new[] { 1.0, 0.5, 0.0 }, new[] { 0.0, 1.0 }, corners, "gfm-test", "gfl-test",
                new DynamicsConfig { EnableAssetDynamics = true }, ReplayTolerance.Default);
            H0StudyResult r = H0StudyEngine.Run(spec);
            Assert.Contains(r.Cells, c => c.FormingDependent);
            SameBytes(LegacyH0StudyExport.ToJson(r, meta), H0StudyExport.ToJson(r, meta), $"h0 study, {corners.Length} corner(s)");
        }
    }

    // ---- Harmonic export ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_harmonic_export_writes_the_same_bytes()
    {
        GridModel case9 = IeeeCases.Case9();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7, 11 } };
        var sources = new HarmonicSourceSet
        {
            Case = "case9",
            Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", case9.Buses[4].Id, 0.8) },
        };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(case9, sources.Sources, opt);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(case9, idx.WorstBusId, opt);
        var compliance = HarmonicCompliance.AssessIeee519(case9, pen);
        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(case9, 1, opt);
        double[] vm = Enumerable.Range(0, pen.BusIds.Count).Select(i => 1.0 + 0.001 * i).ToArray();

        // Plain; with the scan and compliance; with everything optional.
        SameBytes(LegacyHarmonicExport.ToJson(case9, sources, pen, idx, Meta), HarmonicExport.ToJson(case9, sources, pen, idx, Meta), "harmonic plain");
        SameBytes(LegacyHarmonicExport.ToJson(case9, sources, pen, idx, Meta, scan, compliance),
            HarmonicExport.ToJson(case9, sources, pen, idx, Meta, scan, compliance), "harmonic scan + compliance");
        SameBytes(LegacyHarmonicExport.ToJson(case9, sources, pen, idx, Meta, scan, compliance, opt, sweep, vm),
            HarmonicExport.ToJson(case9, sources, pen, idx, Meta, scan, compliance, opt, sweep, vm), "harmonic all optional");

        // The harmonic model block with machine sources, load shunts and the active-front-end units (N348).
        GridModel afe = case9.CopyWith(generators: case9.Generators
            .Append(new Generator(5, Pg: 10, MBase: 50, Name: "afe", Type: "solar"))
            .Append(new Generator(7, Pg: 20, MBase: 200, Name: "lcc", Type: "interconnector", ConverterFaultCurrentPu: 0.0))
            .ToList());
        HarmonicModelOptions study = HarmonicModelOptions.Study with
        {
            Orders = new[] { 5, 7, 11 }, ActiveFrontEndReactancePu = 0.15, ActiveFrontEndResistancePu = 0.004,
        };
        var afeSources = new HarmonicSourceSet { Case = "case9", Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", 5, 0.8) } };
        HarmonicPenetrationResult pen2 = HarmonicPenetration.Solve(afe, afeSources.Sources, study);
        HarmonicIndicesResult idx2 = HarmonicIndices.FromPenetration(pen2);
        foreach (HarmonicModelOptions o in new[] { study, study with { ActiveFrontEndReactancePu = null } })
            SameBytes(LegacyHarmonicExport.ToJson(afe, afeSources, pen2, idx2, Meta, modelOptions: o),
                HarmonicExport.ToJson(afe, afeSources, pen2, idx2, Meta, modelOptions: o), $"harmonic model block, afe {o.ActiveFrontEndReactancePu}");
    }

    // ---- Impedance-stability export ----------------------------------------------------------------------------------

    [Fact]
    public void The_impedance_stability_export_writes_the_same_bytes()
    {
        var op = new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, 0.2);
        var conv = GridFollowingImpedance.FromParameters(2, op, ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder, 20.0, 0.707, 0.0);
        var opt = new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 200, PointsPerDecade = 20, F0Hz = 50, Criterion = StabilityCriterion.Siso,
            UseCanonicalThevenin = true, Scr = 1.0, XOverR = 10, PRatedPu = 1.0, ScrMin = 0.2, ScrMax = 6.0, ScrSteps = 20,
        };
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, conv);
        GridModel model = GridImpedanceAssembler.CanonicalThevenin(1.0, 10, 1.0, 100);
        foreach (ImpedanceStabilityResult x in new[] { r, r.WithModalCrossCheck(0), r.WithModalCrossCheck(1), r with { Params = null } })
            SameBytes(LegacyImpedanceStabilityExport.ToJson(model, x, Meta), ImpedanceStabilityExport.ToJson(model, x, Meta),
                $"impedance (crossval {x.RhpEigenvalueCountFromModal}, params {x.Params is not null})");
    }

    // ---- RMS event-study summary -------------------------------------------------------------------------------------

    [Fact]
    public void The_rms_event_summary_writes_the_same_bytes()
    {
        MultiMachineSubtransient.MachineTrace Trace(double offset)
        {
            double[] t = Enumerable.Range(0, 5).Select(k => 0.005 * k).ToArray();
            double[] v = t.Select(x => x + offset).ToArray();
            return new MultiMachineSubtransient.MachineTrace(t, v, v, v, v, v, v);
        }
        double[] cv = { 1.0, 0.98, 0.5, 0.97, 1.0 };
        var converter = new RmsConverterTrace("gfl \"1\"", 3, cv, cv, cv, cv, cv, new[] { false, false, true, false, false });
        IReadOnlyList<RmsEvent> events = new RmsEvent[] { new RmsFaultClear(0.2, 7), new RmsBusFault(0.1, 7) };
        var runs = new[]
        {
            new MultiMachineSubtransient.Result(new[] { Trace(0.1), Trace(0.2) }, true),
            new MultiMachineSubtransient.Result(new[] { Trace(0.1), Trace(0.2) }, false),
            new MultiMachineSubtransient.Result(new[] { Trace(0.1), Trace(0.2) }, true, new[] { converter }, 3),
        };
        foreach (MultiMachineSubtransient.Result run in runs)
            foreach (int every in new[] { 1, 2 })
                SameBytes(LegacyRmsEventsExport.SummaryJsonText(run, new[] { 1, 2 }, 1, events, "WSCC-9", 60.0, 0.005, 3.2, true, "INDICATIVE", every),
                    RmsEventsExport.SummaryJsonText(run, new[] { 1, 2 }, 1, events, "WSCC-9", 60.0, 0.005, 3.2, true, "INDICATIVE", every),
                    $"rms summary (stable {run.StayedInSynchronism}, converters {run.Converters?.Count ?? 0}, every {every})");
    }

    // ---- Small-signal export -----------------------------------------------------------------------------------------

    [Fact]
    public void The_small_signal_export_writes_the_same_bytes()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCase(model, pf, new[] { 0.0608, 0.1198, 0.1813 });
        ModalResult modal = ModalAnalysis.Analyze(ssm);
        foreach (int? exit in new int?[] { null, 0, 1 })
            SameBytes(LegacySmallSignalExport.ToJson("WSCC-9", ssm, modal, exit), SmallSignalExport.ToJson("WSCC-9", ssm, modal, exit), $"small signal exit {exit}");
        ModalResult unavailable = modal with { ParticipationUnavailable = true, ParticipationUnavailableReason = "defective A (test)" };
        SameBytes(LegacySmallSignalExport.ToJson("WSCC-9", ssm, unavailable), SmallSignalExport.ToJson("WSCC-9", ssm, unavailable), "small signal, no participation");
    }
}
