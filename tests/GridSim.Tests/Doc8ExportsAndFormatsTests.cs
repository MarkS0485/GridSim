using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Estimation;
using GridSim.Core.Events;
using GridSim.Core.Gda;
using GridSim.Core.Interpose;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-8 (October 2026): the exports-and-formats findings of the docs session, stream 8 of
/// review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md, tested on the Core readers and writers directly (the CLI side is in
/// CliDoc8ProcessTests). Every test here is new; no existing expectation was moved.
///
/// <list type="bullet">
/// <item>D-DOC-067: the solve export carries every bus, generator (in service or not) and branch field, and SolveImport
/// reads every one back.</item>
/// <item>D-DOC-068: solve.busSpec Qd is MVAr on every engine (the sweep wrote per unit).</item>
/// <item>D-DOC-071: an estimate projected into the solve shape names its state update maxStateUpdate, not maxMismatch.</item>
/// <item>D-DOC-180: the solve export carries the solve status.</item>
/// <item>D-DOC-069: the estimate-export residual reader refuses a row without its bus / from / to / end.</item>
/// <item>D-DOC-070: a measurement set, harmonic source set or RTU point map without a "schema" member is refused.</item>
/// <item>D-DOC-175: the impedance-stability export writes the converter admittance under converterAdmittance.</item>
/// <item>D-DOC-177: a GDA time-series without "steps" is refused naming the file.</item>
/// <item>D-DOC-179: data/events/gb-events.json carries exactly the built-in events.</item>
/// </list>
/// </summary>
public sealed class Doc8ExportsAndFormatsTests
{
    private static string TempFile(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static void DeleteFolderOf(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly SolveExportMeta Meta = new("case9", "cpu", "test", "2026-10-05T00:00:00Z", 12.5, "test");

    // ---- D-DOC-067: every model field round-trips ------------------------------------------------------------------

    /// <summary>
    /// IEEE case9 with EVERY field the export used to drop set to a non-default value: a bus angle, layout coordinates
    /// and the bus's own voltage limits; a fourth generator that is out of service, and on the in-service units the
    /// fault, converter, cost and dynamics-class fields; on the branches the length, equipment, short-term and emergency
    /// ratings and dynamics class.
    /// </summary>
    private static GridModel FullyDecorated()
    {
        GridModel m = IeeeCases.Case9();
        var buses = m.Buses.ToList();
        buses[4] = buses[4] with { VaDeg = -3.25, X = 0.125, Y = 0.875, VminPu = 0.95, VmaxPu = 1.05 };
        buses[6] = buses[6] with { VminPu = 0.9 };   // one side only; the other stays null (the global band)
        var gens = m.Generators.ToList();
        gens[0] = gens[0] with
        {
            DynamicsClass = "sync-steam-2024", SubtransientXdPu = 0.21, NegativeSeqXPu = 0.23, UnitTransformerXPu = 0.12,
            Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 1500, 250, new[] { 0.11, 5.0, 150.0 }),
        };
        gens[1] = gens[1] with
        {
            ConverterInterfaced = true, ConverterFaultCurrentPu = 1.1, GridForming = true,
            ReactiveCost = new GeneratorCost(GeneratorCostModel.PiecewiseLinear, 0, 0, new[] { 0.0, 0.0, 100.0, 200.0 }),
        };
        gens[2] = gens[2] with { PowerFlowOnly = true, Type = "wind" };
        gens.Add(new Generator(3, Pg: 50, MBase: 120, H: 4.0, Name: "held unit", InService: false, Pmax: 100));
        var branches = m.Branches.ToList();
        branches[1] = branches[1] with { LengthKm = 42.5, Equipment = "ohl", RateBMva = 250, RateCMva = 300, DynamicsClass = "line-thermal" };
        branches[2] = branches[2] with { Equipment = "cable", LengthKm = 3.75 };
        return m.CopyWith(buses: buses, generators: gens, branches: branches);
    }

    private static (GridModel Model, PowerFlowResult Result, SolveTrace Trace, string Json) ExportFullyDecorated()
    {
        GridModel m = FullyDecorated();
        Assert.Single(m.OutOfServiceGenerators);
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        return (m, r, trace, SolveExport.ToJson(m, r, trace, Meta));
    }

    [Fact]
    public void D067_every_bus_generator_and_branch_field_round_trips_exactly()
    {
        (GridModel m, _, _, string json) = ExportFullyDecorated();
        GridModel back = SolveImport.Read(json).Model;

        // Buses and branches: record equality, field for field (doubles exactly: JSON numbers round-trip).
        Assert.Equal(m.Buses, back.Buses);
        Assert.Equal(m.Branches, back.Branches);

        // Generators: the WHOLE fleet in order, the out-of-service unit included and still out of service. A cost curve
        // holds a list, which record equality compares by reference, so the curves are compared field by field and the
        // rest of each generator by record equality.
        Assert.Equal(m.AllGenerators.Count, back.AllGenerators.Count);
        for (int i = 0; i < m.AllGenerators.Count; i++)
        {
            Generator a = m.AllGenerators[i], b = back.AllGenerators[i];
            Assert.Equal(a with { Cost = null, ReactiveCost = null }, b with { Cost = null, ReactiveCost = null });
            AssertSameCost(a.Cost, b.Cost);
            AssertSameCost(a.ReactiveCost, b.ReactiveCost);
        }
        Assert.Equal(m.Generators.Count, back.Generators.Count);              // the in-service list is the same size
        Assert.Equal("held unit", Assert.Single(back.OutOfServiceGenerators).Name);
    }

    private static void AssertSameCost(GeneratorCost? a, GeneratorCost? b)
    {
        if (a is null) { Assert.Null(b); return; }
        Assert.NotNull(b);
        Assert.Equal(a.Model, b!.Model);
        Assert.Equal(a.StartupCost, b.StartupCost);
        Assert.Equal(a.ShutdownCost, b.ShutdownCost);
        Assert.Equal(a.Coefficients, b.Coefficients);
    }

    [Fact]
    public void D067_the_out_of_service_unit_is_written_with_its_state_and_the_solve_is_unchanged_by_it()
    {
        (GridModel m, PowerFlowResult r, _, string json) = ExportFullyDecorated();
        JsonElement gens = JsonDocument.Parse(json).RootElement.GetProperty("model").GetProperty("generators");
        Assert.Equal(4, gens.GetArrayLength());                                // was 3: the held unit vanished
        Assert.False(gens[3].GetProperty("inService").GetBoolean());
        Assert.All(Enumerable.Range(0, 3), i => Assert.True(gens[i].GetProperty("inService").GetBoolean()));

        // The held unit is not solved: the same case built from the in-service units alone solves to the same bits.
        PowerFlowResult r2 = new NewtonRaphsonPowerFlow().Solve(new GridModel
        {
            Name = m.Name, BaseMva = m.BaseMva, Buses = m.Buses, Generators = m.Generators.ToArray(), Branches = m.Branches,
        });
        Assert.Equal(r.Vm, r2.Vm);
        Assert.Equal(r.VaDeg, r2.VaDeg);
    }

    [Fact]
    public void D067_a_re_imported_export_re_solves_to_the_bit_identical_state()
    {
        (_, PowerFlowResult r, _, string json) = ExportFullyDecorated();
        GridModel back = SolveImport.Read(json).Model;
        PowerFlowResult again = new NewtonRaphsonPowerFlow().Solve(back);
        Assert.True(again.Converged);
        Assert.Equal(r.Vm, again.Vm);
        Assert.Equal(r.VaDeg, again.VaDeg);
        Assert.Equal(r.PInjMw, again.PInjMw);
        Assert.Equal(r.QInjMvar, again.QInjMvar);
    }

    [Fact]
    public void D067_an_older_document_without_the_new_fields_still_reads_with_the_model_defaults()
    {
        // The pre-DOC-8 shape of one bus, one generator and one branch (no vaDeg / limits / inService / cost / length).
        const string json = """
        { "schema": "gridsim-solve-export/1",
          "model": { "name": "old", "baseMva": 100,
            "buses": [ { "id": 1, "type": "Slack", "vm": 1.0 }, { "id": 2, "type": "Pq", "pd": 10, "qd": 2 } ],
            "generators": [ { "bus": 1, "pg": 10, "vg": 1.0 } ],
            "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1 } ] },
          "solve": { "converged": true, "iterations": 3, "maxMismatch": 1e-10 },
          "result": { "buses": [], "lineFlows": [] } }
        """;
        SolveSnapshot s = SolveImport.Read(json);
        Bus b = s.Model.Buses[1];
        Assert.Equal(0.0, b.VaDeg);
        Assert.Null(b.VminPu);
        Assert.Null(b.VmaxPu);
        Generator g = Assert.Single(s.Model.Generators);
        Assert.True(g.InService);
        Assert.Null(g.Cost);
        Assert.Null(g.ConverterInterfaced);
        Branch e = Assert.Single(s.Model.Branches);
        Assert.Equal(0.0, e.LengthKm);
        Assert.Null(e.Equipment);
        Assert.Equal(0.0, e.RateBMva);
        Assert.Equal(1e-10, s.Result.MaxMismatch);
        Assert.Equal(PowerFlowStatus.Converged, s.Result.Status);   // no status written: the default, as before
    }

    [Fact]
    public void D067_an_unknown_cost_model_is_refused_not_dropped()
    {
        const string json = """
        { "schema": "gridsim-solve-export/1",
          "model": { "buses": [ { "id": 1, "type": "Slack" } ],
            "generators": [ { "bus": 1, "pg": 10, "name": "G1", "cost": { "model": "Quadratic", "coefficients": [1, 2] } } ],
            "branches": [] },
          "result": { "buses": [], "lineFlows": [] } }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => SolveImport.Read(json));
        Assert.Contains("cost.model 'Quadratic' is not a cost model", ex.Message, StringComparison.Ordinal);
    }

    // ---- D-DOC-180: the solve status ---------------------------------------------------------------------------------

    [Fact]
    public void D180_the_solve_block_carries_the_status_and_import_restores_it()
    {
        (_, PowerFlowResult r, _, string json) = ExportFullyDecorated();
        JsonElement solve = JsonDocument.Parse(json).RootElement.GetProperty("solve");
        Assert.Equal("Converged", solve.GetProperty("status").GetString());
        Assert.Equal(r.MaxMismatch, solve.GetProperty("maxMismatch").GetDouble());   // unchanged for a solve

        // A solve stopped at its iteration cap: converged false, status MaxIterations, and the import keeps both.
        GridModel m = IeeeCases.Case9();
        var trace = new SolveTrace();
        PowerFlowResult capped = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 1, Trace = trace });
        Assert.False(capped.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, capped.Status);
        string cappedJson = SolveExport.ToJson(m, capped, trace, Meta);
        Assert.Equal("MaxIterations", JsonDocument.Parse(cappedJson).RootElement.GetProperty("solve").GetProperty("status").GetString());
        PowerFlowResult back = SolveImport.Read(cappedJson).Result;
        Assert.False(back.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, back.Status);
    }

    [Fact]
    public void D180_a_status_that_is_not_a_power_flow_status_is_refused()
    {
        (_, _, _, string json) = ExportFullyDecorated();
        string bad = json.Replace("\"status\": \"Converged\"", "\"status\": \"Mostly\"", StringComparison.Ordinal);
        Assert.NotEqual(json, bad);
        var ex = Assert.Throws<InvalidDataException>(() => SolveImport.Read(bad));
        Assert.Contains("solve.status 'Mostly' is not a power-flow status", ex.Message, StringComparison.Ordinal);
    }

    // ---- D-DOC-071: the estimated solve export ------------------------------------------------------------------------

    [Fact]
    public void D071_an_estimate_in_the_solve_shape_names_its_state_update_and_writes_no_power_mismatch()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions());
        Assert.True(est.Converged);
        PowerFlowResult projected = est.ToPowerFlowResult(m);
        // The defect: the projection puts the state update in the mismatch slot (unchanged in memory).
        Assert.Equal(est.MaxStateUpdate, projected.MaxMismatch);

        string json = SolveExport.ToJson(m, projected, new SolveTrace(), Meta,
            estimate: new SolveExportEstimate(est.Status.ToString(), est.MaxStateUpdate, "Wls"));
        JsonElement solve = JsonDocument.Parse(json).RootElement.GetProperty("solve");
        Assert.False(solve.TryGetProperty("maxMismatch", out _));             // no longer written under that name
        Assert.Equal(est.MaxStateUpdate, solve.GetProperty("maxStateUpdate").GetDouble());   // exact
        Assert.Equal("Converged", solve.GetProperty("status").GetString());
        Assert.Equal("state-estimate (Wls)", solve.GetProperty("solver").GetString());
        Assert.True(solve.GetProperty("converged").GetBoolean());
        Assert.Equal(est.Iterations, solve.GetProperty("iterations").GetInt32());

        // The import puts it back where the in-memory projection holds it, so the round trip is exact.
        SolveSnapshot back = SolveImport.Read(json);
        Assert.Equal(projected.MaxMismatch, back.Result.MaxMismatch);
        Assert.Equal(projected.Vm, back.Result.Vm);
    }

    // ---- D-DOC-068: busSpec units -------------------------------------------------------------------------------------

    [Fact]
    public void D068_bus_spec_qd_is_mvar_on_the_sweep_as_on_newton()
    {
        // CIGRE MV is radial, so the backward/forward sweep and Newton both solve it.
        GridModel m = IeeeCases.CigreMv();
        var bfsTrace = new SolveTrace();
        var nrTrace = new SolveTrace();
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, new PowerFlowOptions { Trace = bfsTrace });
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = nrTrace });
        Assert.True(bfs.Converged);
        Assert.True(nr.Converged);
        Assert.Contains(m.Buses, b => b.Qd != 0);   // the comparison means something

        var nrQd = nrTrace.Spec.ToDictionary(row => row.Id, row => row.Qd);
        foreach (BusSpecRow row in bfsTrace.Spec)
        {
            double mvar = m.Buses.Single(b => b.Id == row.Id).Qd;
            Assert.Equal(mvar, row.Qd);                // MVAr, exactly the model's value (was Qd / BaseMva)
            Assert.Equal(nrQd[row.Id], row.Qd);         // the same number Newton records
        }
    }

    [Fact]
    public void D068_the_trace_unit_change_does_not_move_the_sweep_solution()
    {
        // Trace capture only: the same sweep with and without a trace gives the same bits.
        GridModel m = IeeeCases.CigreMv();
        PowerFlowResult plain = new BackwardForwardSweep().Solve(m);
        PowerFlowResult traced = new BackwardForwardSweep().Solve(m, new PowerFlowOptions { Trace = new SolveTrace() });
        Assert.Equal(plain.Vm, traced.Vm);
        Assert.Equal(plain.VaDeg, traced.VaDeg);
        Assert.Equal(plain.MaxMismatch, traced.MaxMismatch);
    }

    // ---- D-DOC-069: the residual reader requires the location ---------------------------------------------------------

    private static string ResidualDoc(string measurementJson) => $$"""
        { "schema": "gridsim-estimate-export/1", "estimate": { "converged": true },
          "residuals": [ { "measurement": {{measurementJson}}, "predicted": 1.0, "residual": 0.001, "normalizedResidual": 0.5, "critical": false } ] }
        """;

    [Theory]
    [InlineData("""{ "kind": "VmPu", "value": 1.0, "sigma": 0.004, "source": "scada" }""", "missing 'bus' (a VmPu measurement needs it)")]
    [InlineData("""{ "kind": "VmPu", "value": 1.0, "sigma": 0.004, "bus": null }""", "missing 'bus' (a VmPu measurement needs it)")]
    [InlineData("""{ "kind": "PInjMw", "value": 1.0, "sigma": 0.004, "bus": 2.5 }""", "'bus' is not a whole number")]
    [InlineData("""{ "kind": "PFlowMw", "value": 1.0, "sigma": 0.004, "to": 4, "end": "From" }""", "missing 'from' (a PFlowMw measurement needs it)")]
    [InlineData("""{ "kind": "QFlowMvar", "value": 1.0, "sigma": 0.004, "from": 1, "end": "To" }""", "missing 'to' (a QFlowMvar measurement needs it)")]
    [InlineData("""{ "kind": "IMagPu", "value": 1.0, "sigma": 0.004, "from": 1, "to": 4 }""", "missing 'end'")]
    [InlineData("""{ "kind": "PFlowMw", "value": 1.0, "sigma": 0.004, "from": 1, "to": 4, "end": "to" }""", "'end' is 'to', expected \"From\" or \"To\"")]
    public void D069_a_residual_row_without_its_location_is_refused(string measurement, string expected)
    {
        string path = TempFile("estimate-export.json");
        try
        {
            File.WriteAllText(path, ResidualDoc(measurement));
            var ex = Assert.Throws<InvalidDataException>(() => EstimateExportResiduals.Load(path));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
            Assert.Contains("residuals[0]", ex.Message, StringComparison.Ordinal);
        }
        finally { DeleteFolderOf(path); }
    }

    [Fact]
    public void D069_complete_rows_still_read_with_their_locations()
    {
        string path = TempFile("estimate-export.json");
        try
        {
            File.WriteAllText(path, $$"""
                { "schema": "gridsim-estimate-export/1", "estimate": { "converged": true },
                  "residuals": [
                    { "measurement": { "kind": "VmPu", "value": 1.0, "sigma": 0.004, "bus": 7, "from": null, "to": null, "source": "pmu" },
                      "predicted": 1.0, "residual": 0.0, "normalizedResidual": "NaN", "critical": true },
                    { "measurement": { "kind": "PFlowMw", "value": 12.5, "sigma": 0.5, "bus": null, "from": 4, "to": 9, "end": "To", "circuit": "c2", "source": "scada" },
                      "predicted": 12.0, "residual": 0.5, "normalizedResidual": 1.25, "critical": false } ] }
                """);
            EstimateExportResidualTable t = EstimateExportResiduals.Load(path);
            Assert.Equal(7, t.Residuals[0].Measurement.Bus);
            Measurement f = t.Residuals[1].Measurement;
            Assert.Equal((4, 9, BranchEnd.To, "c2"), (f.From, f.To, f.End, f.Circuit));
        }
        finally { DeleteFolderOf(path); }
    }

    // ---- D-DOC-070: the schema member is required ---------------------------------------------------------------------

    [Fact]
    public void D070_a_measurement_set_without_a_schema_member_is_refused()
    {
        string path = TempFile("m.json");
        try
        {
            File.WriteAllText(path, """{ "case": "case9", "baseMva": 100, "takenUtc": "2024-03-01T12:00:00Z", "measurements": [ { "kind": "VmPu", "value": 1.0, "sigma": 0.004, "bus": 1 } ] }""");
            var ex = Assert.Throws<InvalidDataException>(() => MeasurementSet.Load(path));
            Assert.Contains("has no \"schema\" member", ex.Message, StringComparison.Ordinal);
            Assert.Contains("\"schema\": \"gridsim-measurements/1\"", ex.Message, StringComparison.Ordinal);
            // The same file with the member loads (case-insensitive name, as the deserialiser binds it).
            File.WriteAllText(path, """{ "Schema": "gridsim-measurements/1", "case": "case9", "baseMva": 100, "takenUtc": "2024-03-01T12:00:00Z", "measurements": [ { "kind": "VmPu", "value": 1.0, "sigma": 0.004, "bus": 1 } ] }""");
            Assert.Single(MeasurementSet.Load(path).Measurements);
        }
        finally { DeleteFolderOf(path); }
    }

    [Fact]
    public void D070_a_harmonic_source_set_without_a_schema_member_is_refused()
    {
        string path = TempFile("h.json");
        try
        {
            File.WriteAllText(path, """{ "case": "case9", "sources": [] }""");
            var ex = Assert.Throws<InvalidDataException>(() => HarmonicSourceSet.Load(path));
            Assert.Contains("has no \"schema\" member", ex.Message, StringComparison.Ordinal);
            Assert.Contains("gridsim-harmonics/1", ex.Message, StringComparison.Ordinal);
            File.WriteAllText(path, """{ "schema": "gridsim-harmonics/1", "case": "case9", "sources": [] }""");
            Assert.Empty(HarmonicSourceSet.Load(path).Sources);
        }
        finally { DeleteFolderOf(path); }
    }

    [Fact]
    public void D070_an_rtu_point_map_without_a_schema_member_is_refused()
    {
        string path = TempFile("p.json");
        try
        {
            File.WriteAllText(path, """{ "bindings": [ { "address": "IOA:1001", "kind": "VmPu", "bus": 4 } ] }""");
            var ex = Assert.Throws<InvalidDataException>(() => RtuPointMap.Load(path));
            Assert.Contains("has no \"schema\" member", ex.Message, StringComparison.Ordinal);
            Assert.Contains("gridsim-rtu-pointmap/1", ex.Message, StringComparison.Ordinal);
            File.WriteAllText(path, """{ "schema": "gridsim-rtu-pointmap/1", "bindings": [ { "address": "IOA:1001", "kind": "VmPu", "bus": 4 } ] }""");
            Assert.Single(RtuPointMap.Load(path).Bindings);
        }
        finally { DeleteFolderOf(path); }
    }

    [Fact]
    public void D070_the_schema_check_leaves_other_shapes_to_the_reader()
    {
        // Not an object: no verdict here (the reader reports the shape); not JSON: the parser's own exception.
        SchemaMember.Require("[]", "x.json", "gridsim-measurements/1", "measurement set");
        Assert.ThrowsAny<JsonException>(() => SchemaMember.Require("not json", "x.json", "gridsim-measurements/1", "measurement set"));
    }

    // ---- D-DOC-175: the converter admittance under its own key --------------------------------------------------------

    [Fact]
    public void D175_converter_admittance_is_written_under_its_own_key_and_the_old_key_carries_the_same_values()
    {
        var conv = GridFollowingImpedance.FromParameters(2, new ConverterOperatingPoint(1.0, 0.0, 1.0, 0.2, 1.0, -0.2),
            ImpedanceModelDetail.PllOuter, ProvenanceTag.CuratedPlaceholder, 5.0, 0.707, 0.0);
        var opt = new ImpedanceStabilityOptions
        {
            FMinHz = 0.1, FMaxHz = 250.0, PointsPerDecade = 10, F0Hz = 50.0, UseCanonicalThevenin = true,
            Scr = 3.0, XOverR = 10.0, PRatedPu = 1.0, ScrMin = 1.0, ScrMax = 10.0, ScrSteps = 5,
        };
        ImpedanceStabilityResult r = ImpedanceStabilityScan.Run(opt, conv);
        JsonElement root = JsonDocument.Parse(ImpedanceStabilityExport.ToJson(IeeeCases.Case9(), r, Meta)).RootElement;
        JsonElement y = root.GetProperty("converterAdmittance");
        JsonElement legacy = root.GetProperty("converterImpedance");
        Assert.Equal(r.Frequency.Count, y.GetArrayLength());
        Assert.Equal(y.GetRawText(), legacy.GetRawText());                     // the same values, byte for byte
        for (int i = 0; i < r.Frequency.Count; i++)
        {
            Assert.Equal(r.Frequency[i].YConv.Real, y[i].GetProperty("re").GetDouble());
            Assert.Equal(r.Frequency[i].YConv.Imaginary, y[i].GetProperty("im").GetDouble());
        }
    }

    // ---- D-DOC-177: a GDA time-series without steps ---------------------------------------------------------------------

    [Fact]
    public void D177_a_series_without_steps_is_refused_naming_the_file()
    {
        string path = TempFile("series.json");
        try
        {
            File.WriteAllText(path, """{ "name": "no steps here" }""");
            var ex = Assert.Throws<InvalidDataException>(() => GdaTimeSeries.Load(path));
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains("has no \"steps\" array", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Parameter 'source'", ex.Message, StringComparison.Ordinal);

            File.WriteAllText(path, """{ "name": "a null step", "steps": [ null ] }""");
            ex = Assert.Throws<InvalidDataException>(() => GdaTimeSeries.Load(path));
            Assert.Contains("steps[0] is null", ex.Message, StringComparison.Ordinal);

            File.WriteAllText(path, """{ "name": "ok", "steps": [ { "timeUtc": "2024-03-01T12:00:00Z", "systemLoadFactor": 0.9 } ] }""");
            Assert.Single(GdaTimeSeries.Load(path).Steps);
        }
        finally { DeleteFolderOf(path); }
    }

    // ---- D-DOC-179: the bundled catalog is the built-in one -------------------------------------------------------------

    [Fact]
    public void D179_the_bundled_event_catalog_carries_exactly_the_built_in_events()
    {
        string bundled = Path.Combine(CliProcess.Root(), "data", "events", "gb-events.json");
        IReadOnlyList<GridEvent> fromFile = EventCatalog.Load(bundled);
        IReadOnlyList<GridEvent> builtin = EventCatalog.Builtin();
        Assert.Equal(builtin.Select(e => e.Id), fromFile.Select(e => e.Id));   // gb-2023-12-22 was missing
        Assert.Contains(fromFile, e => e.Id == "gb-2023-12-22");

        // Every field the same: both lists written by the one catalog writer must give the same text.
        string a = TempFile("builtin.json"), b = TempFile("file.json");
        try
        {
            EventCatalog.Save(a, builtin);
            EventCatalog.Save(b, fromFile);
            Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));
        }
        finally { DeleteFolderOf(a); DeleteFolderOf(b); }
    }
}
