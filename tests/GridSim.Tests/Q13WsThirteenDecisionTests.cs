using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Q13 (October 2026): the WS13 inconsistencies Mark asked to be decided on evidence. These tests pin the four
/// behaviour changes the decisions made (all fail-loud or reporting changes; no solver number moves):
/// <list type="bullet">
/// <item>R179: a JSON bus without baseKv is refused by name (it used to become 400 kV silently).</item>
/// <item>R193: <c>ProtectionContext</c> refuses a line flow that matches no branch of the model (it used to borrow
///   another branch's impedance, or fabricate a 0.0001 pu branch).</item>
/// <item>R177: the bridge / solve-export model block carries the nominal frequency, and an invalid one fails loud.</item>
/// <item>R175: a robust estimate whose IRLS outer loop stopped on its cap says so (<c>IrlsCapReached</c>).</item>
/// </list>
/// </summary>
public sealed class Q13WsThirteenDecisionTests
{
    // ------------------------------------------------------------------------------ R179 baseKv required

    private static string WriteCase(string busesJson)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-q13-basekv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "q13", "baseMva": 100 }""");
        File.WriteAllText(Path.Combine(dir, "buses.json"), busesJson);
        File.WriteAllText(Path.Combine(dir, "generation.json"), """
            [ { "name": "G", "bus": 1, "capacityMva": 500, "outputMw": 50, "role": "slack" } ]
            """);
        File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "name": "D", "bus": 2, "pMw": 50 } ]""");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), """
            [ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1 } ]
            """);
        return dir;
    }

    [Fact]
    public void A_bus_without_baseKv_is_refused_naming_every_such_bus()
    {
        string dir = WriteCase("""[ { "id": 1, "name": "North" }, { "id": 2 }, { "id": 3, "baseKv": 132 } ]""");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("buses.json", ex.Message, StringComparison.Ordinal);
            Assert.Contains("baseKv", ex.Message, StringComparison.Ordinal);
            Assert.Contains("2 bus(es)", ex.Message, StringComparison.Ordinal);
            // Exactly the two offenders, in file order, and not bus 3 (which has its baseKv).
            Assert.EndsWith("): 1 ('North'), 2.", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_bus_with_baseKv_loads_with_exactly_that_value()
    {
        string dir = WriteCase("""[ { "id": 1, "baseKv": 400 }, { "id": 2, "baseKv": 132.0 } ]""");
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Equal(400.0, m.Buses.Single(b => b.Id == 1).BaseKv);
            Assert.Equal(132.0, m.Buses.Single(b => b.Id == 2).BaseKv);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------------------ R193 BranchFor fails loud

    [Fact]
    public void A_flow_that_matches_no_branch_is_refused_rather_than_given_another_branchs_impedance()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult result = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(result.Converged);

        // The same result against a model that has lost its first branch: that flow now matches nothing.
        var lost = new GridModel
        {
            BaseMva = model.BaseMva, Buses = model.Buses, Generators = model.Generators,
            Branches = model.Branches.Skip(1).ToList(), Name = model.Name,
        };
        LineFlow orphan = result.LineFlows[0];
        var ex = Assert.Throws<InvalidOperationException>(() => new ProtectionContext(lost, result, new ProtectionSettings(), null));
        Assert.Contains($"{orphan.From} -> {orphan.To}", ex.Message, StringComparison.Ordinal);

        // A model with no branches at all is refused the same way (it used to get a fabricated 0.0001 pu branch).
        var none = new GridModel
        {
            BaseMva = model.BaseMva, Buses = model.Buses, Generators = model.Generators,
            Branches = Array.Empty<Branch>(), Name = model.Name,
        };
        Assert.Throws<InvalidOperationException>(() => new ProtectionContext(none, result, new ProtectionSettings(), null));
    }

    [Fact]
    public void A_matching_model_still_gives_each_flow_its_own_branch_impedance_exactly()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult result = new NewtonRaphsonPowerFlow().Solve(model);
        var ctx = new ProtectionContext(model, result, new ProtectionSettings(), null);
        for (int l = 0; l < result.LineFlows.Count; l++)
        {
            Branch b = model.Branches[l];
            Assert.Equal(Math.Sqrt(b.R * b.R + b.X * b.X), ctx.ZLineMagPu[l]);
        }
    }

    // ------------------------------------------------------------------------------ R177 frequency in the model block

    private static SolveExportMeta Meta() => new("case9", "cpu", "c", "2026-10-02T00:00:00Z", 1.0, "test");

    [Fact]
    public void A_solve_export_carries_the_model_frequency_and_a_re_import_keeps_it()
    {
        GridModel c = IeeeCases.Case9();
        var m60 = new GridModel
        {
            BaseMva = c.BaseMva, Buses = c.Buses, Generators = c.Generators, Branches = c.Branches, Name = c.Name,
            FrequencyHz = 60.0,
        };
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m60, new PowerFlowOptions { Trace = trace });
        string json = SolveExport.ToJson(m60, r, trace, Meta());
        Assert.Contains("\"frequencyHz\":60", json.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Equal(60.0, SolveImport.Read(json).Model.FrequencyHz);
    }

    [Fact]
    public void A_model_block_without_frequency_reads_as_50_Hz_exactly_as_before()
    {
        var block = new BridgeProtocol.ModelBlock { Name = "old", Buses = new() { new BridgeProtocol.BusDto { Id = 1, Type = "Slack" } } };
        Assert.Null(block.FrequencyHz);
        Assert.Equal(50.0, BridgeProtocol.BuildModel(block).FrequencyHz);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_invalid_model_block_frequency_fails_loud(double hz)
    {
        var block = new BridgeProtocol.ModelBlock
        {
            Name = "bad", FrequencyHz = hz, Buses = new() { new BridgeProtocol.BusDto { Id = 1, Type = "Slack" } },
        };
        var ex = Assert.Throws<InvalidDataException>(() => BridgeProtocol.BuildModel(block));
        Assert.Contains("frequencyHz", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ R175 IRLS cap flag

    [Fact]
    public void Plain_wls_never_reports_an_irls_cap()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult wls = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));
        Assert.Equal(EstimationStatus.Converged, wls.Status);
        Assert.False(wls.IrlsCapReached);
    }

    [Fact]
    public void Huber_on_an_exact_set_settles_at_once_and_reports_no_cap()
    {
        // Every standardised residual is far inside k, so every weight factor is 1 on the first re-weighting and the
        // weights settle without touching the cap.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult huber = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth),
            new EstimationOptions { Method = EstimationMethod.Huber });
        Assert.Equal(EstimationStatus.Converged, huber.Status);
        Assert.False(huber.IrlsCapReached);
    }

    [Fact]
    public void Lav_with_a_gross_outlier_runs_to_the_cap_and_says_so_without_changing_its_status()
    {
        // LAV's 1/|r| re-weighting converges only linearly, so with one 30-sigma outlier on case14 the weights are
        // still moving after IrlsMaxOuterIterations passes. The status stays Converged (a flag only, R175), and
        // the state is the same as before the flag existed (the LAV pin in SparseEstimatorTests still holds).
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { NoiseScale = 0 });
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(m => m.Kind == MeasurementKind.PInjMw);
        ms[victim] = ms[victim] with { Value = ms[victim].Value + 30 * ms[victim].Sigma };
        // Moved 2026-10 (END-PASS CF6 row 1, end pass; N356, EstimationMethod.Lav is now the exact LP form and the old IRLS
        // form is EstimationMethod.LavIrls, the same code bit for bit, Ruling R1 and Mark's go on the end-pass moves
        // 2026-10-11): this test pins the IRLS cap behaviour, which still exists on LavIrls, so it is pointed there and its
        // old expectations are kept exactly (Lav itself has no IRLS loop: observed IrlsCapReached false, 1.95.273-275 runs).
        // Before -> after: Method = EstimationMethod.Lav -> EstimationMethod.LavIrls; Converged and IrlsCapReached true
        // unchanged (the same set and method give exactly these in Cf6LavExactTests.Lav_irls_keeps_the_old_behaviour_on_the_q13_set_cap_reached_status_converged,
        // green in those runs).
        EstimationResult lav = new WlsStateEstimator().Estimate(model, set with { Measurements = ms },
            new EstimationOptions { Method = EstimationMethod.LavIrls });
        Assert.Equal(EstimationStatus.Converged, lav.Status);
        Assert.True(lav.IrlsCapReached);
    }
}
