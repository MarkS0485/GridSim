using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS6 analysis group: R136 (one voltage-band definition), R224 (zero boundary limit),
/// R227 (DC-OPF negative Pmin), R228 (restoration energises stored-open sequenced branches and regulates taps),
/// R229 (watch-list voltage deviation from the regulated setpoint) and R230 (watch-list thermal loading on the
/// heavier end).
/// </summary>
public class AnalysisWs6Tests
{
    // ---- R136 ----------------------------------------------------------------------------------------------

    [Fact]
    public void Voltage_bands_are_defined_once_and_consistently()
    {
        Assert.Equal(0.94, VoltageLimits.OperationalLowPu);
        Assert.Equal(1.06, VoltageLimits.OperationalHighPu);
        Assert.Equal(0.90, VoltageLimits.StatutoryLowPu);
        Assert.Equal(1.10, VoltageLimits.StatutoryHighPu);
        Assert.Equal(VoltageLimits.NominalPu - VoltageLimits.StatutoryBandPu, VoltageLimits.StatutoryLowPu);
        Assert.Equal(VoltageLimits.NominalPu + VoltageLimits.StatutoryBandPu, VoltageLimits.StatutoryHighPu);
        Assert.True(VoltageLimits.NominalPu - VoltageLimits.WatchBandPu > VoltageLimits.OperationalLowPu,
            "the watch band is an early warning inside the operational band");
        Assert.True(VoltageLimits.StatutoryLowPu < VoltageLimits.OperationalLowPu && VoltageLimits.OperationalHighPu < VoltageLimits.StatutoryHighPu);
        // The SCADA console's alarm band is the operational band.
        Assert.Equal(VoltageLimits.OperationalLowPu, ScadaThresholds.VoltageBandLowPu);
        Assert.Equal(VoltageLimits.OperationalHighPu, ScadaThresholds.VoltageBandHighPu);
        // The watch-list defaults come from the shared table.
        var w = new WatchListOptions();
        Assert.Equal(VoltageLimits.WatchBandPu, w.VoltageWatchBandPu);
        Assert.Equal(VoltageLimits.StatutoryBandPu, w.VoltageLimitBandPu);
    }

    [Fact]
    public void Contingency_stress_score_uses_the_operational_band()
    {
        var low = new ContingencyResult("low", true, 0, 0, 0.93, 1.0, Array.Empty<string>());
        Assert.Equal(Math.Max(0.0, VoltageLimits.OperationalLowPu - 0.93) * 1000.0, ContingencyAnalysis.StressScore(low));
        var inBand = new ContingencyResult("ok", true, 0, 0, 0.95, 1.05, Array.Empty<string>());
        Assert.Equal(0.0, ContingencyAnalysis.StressScore(inBand));
    }

    // ---- R224 ----------------------------------------------------------------------------------------------

    private static (GridModel Model, PowerFlowResult Result) Case9WithBoundary(double limitMw)
    {
        GridModel m = Core.IO.IeeeCases.Case9();
        var named = m.Branches.Select((b, i) => b with { Name = $"B{i}" }).ToList();
        GridModel withB = m.CopyWith(branches: named,
            boundaries: new[] { new Boundary("cut", limitMw, new[] { new BoundaryBranch("B0") }) });
        return (withB, new NewtonRaphsonPowerFlow().Solve(withB));
    }

    [Fact]
    public void A_boundary_with_no_limit_set_is_never_overloaded()
    {
        (GridModel m, PowerFlowResult r) = Case9WithBoundary(0.0);
        BoundaryFlow bf = BoundaryAnalysis.Evaluate(m, r).Single();
        Assert.True(Math.Abs(bf.FlowMw) > 1.0, "the cut carries real flow");
        Assert.False(bf.Overloaded);
        Assert.Equal(0.0, bf.LoadingPercent);
    }

    [Fact]
    public void A_boundary_with_a_real_limit_is_still_judged()
    {
        (GridModel m, PowerFlowResult r) = Case9WithBoundary(10.0);
        BoundaryFlow bf = BoundaryAnalysis.Evaluate(m, r).Single();
        Assert.True(bf.Overloaded);
        Assert.Equal(Math.Abs(bf.FlowMw) / 10.0 * 100.0, bf.LoadingPercent);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_negative_or_non_finite_boundary_limit_fails_loud(double limit)
    {
        (GridModel m, PowerFlowResult r) = Case9WithBoundary(limit);
        var ex = Assert.Throws<InvalidDataException>(() => BoundaryAnalysis.Evaluate(m, r));
        Assert.Contains("cut", ex.Message);
    }

    // ---- R227 ----------------------------------------------------------------------------------------------

    private static GridModel StorageDispatchModel(double storagePmin, double storagePmax) => new()
    {
        Name = "dcopf-storage",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, Pd: 30), new Bus(2, BusType.Pq) },
        Generators = new[]
        {
            new Generator(1, 0, Pmin: 0, Pmax: 100, Type: "coal"),
            new Generator(2, 0, Pmin: storagePmin, Pmax: storagePmax, Type: "storage"),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    [Fact]
    public void Dc_opf_can_dispatch_storage_charging_below_zero()
    {
        // Storage priced above coal sits at its floor, which is CHARGING at 50 MW; coal then covers the 30 MW
        // demand plus the 50 MW the battery absorbs. With Pmin clamped to 0 (the old rule) the battery idled.
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(StorageDispatchModel(-50, 50),
            costPerGen: new[] { 10.0, 100.0 }, enforceLineLimits: false);
        Assert.True(r.Feasible);
        Assert.Equal(-50.0, r.Dispatch[1].Pg);
        Assert.Equal(80.0, r.Dispatch[0].Pg);
        Assert.Equal(30.0, r.TotalGenMw);
    }

    [Fact]
    public void Dc_opf_rejects_pmin_above_pmax()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DcOptimalPowerFlow.Solve(StorageDispatchModel(60, 50)));
        Assert.Contains("Pmin", ex.Message);
    }

    [Fact]
    public void Dc_opf_rejects_a_non_finite_pmin()
    {
        Assert.Throws<InvalidDataException>(() => DcOptimalPowerFlow.Solve(StorageDispatchModel(double.NegativeInfinity, 50)));
        Assert.Throws<InvalidDataException>(() => DcOptimalPowerFlow.Solve(StorageDispatchModel(double.NaN, 50)));
    }

    // ---- R228 ----------------------------------------------------------------------------------------------

    private static GridModel RestorationModel(bool bStoredOpen, IReadOnlyList<Oltc>? oltcs = null, bool transformer = false) => new()
    {
        BaseMva = 100,
        Name = "restoration-r228",
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 400),
            new Bus(2, BusType.Pv, Pd: 40, Qd: 12, BaseKv: 400),
            new Bus(3, BusType.Pq, Pd: 35, Qd: 10, BaseKv: transformer ? 132 : 400),
        },
        Generators = new[]
        {
            new Generator(1, Pg: 0, Vg: 1.00, Qmin: -500, Qmax: 500),
            new Generator(2, Pg: 40, Vg: 1.00, Qmin: -100, Qmax: 150),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.10, 0.05, Name: "A"),
            new Branch(2, 3, 0.02, 0.12, transformer ? 0 : 0.05, Name: "B", InService: !bStoredOpen, IsTransformer: transformer),
        },
        Oltcs = oltcs ?? Array.Empty<Oltc>(),
    };

    [Fact]
    public void A_sequenced_branch_stored_open_is_energised_at_its_step()
    {
        RestorationReport stored = RestorationSequencer.Evaluate(RestorationModel(bStoredOpen: true), new[] { "A", "B" });
        RestorationReport closed = RestorationSequencer.Evaluate(RestorationModel(bStoredOpen: false), new[] { "A", "B" });
        // Step 1 energises B, picking up bus 3's load: the voltage profile must change from step 0. Before the fix
        // the stored-open B was never closed and step 1 solved the same network as step 0.
        Assert.True(stored.Steps[1].MinVpu < stored.Steps[0].MinVpu, "energising B must pick up bus 3");
        // A sequenced branch's stored flag is irrelevant: both models give the same steps bit-for-bit.
        for (int s = 0; s < 2; s++)
        {
            Assert.Equal(closed.Steps[s].MinVpu, stored.Steps[s].MinVpu);
            Assert.Equal(closed.Steps[s].MaxVpu, stored.Steps[s].MaxVpu);
        }
    }

    [Fact]
    public void Restoration_rejects_an_unknown_or_repeated_branch_name()
    {
        var unknown = Assert.Throws<ArgumentException>(() => RestorationSequencer.Evaluate(RestorationModel(false), new[] { "A", "Bee" }));
        Assert.Contains("Bee", unknown.Message);
        var twice = Assert.Throws<ArgumentException>(() => RestorationSequencer.Evaluate(RestorationModel(false), new[] { "A", "A" }));
        Assert.Contains("more than once", twice.Message);
    }

    [Fact]
    public void Restoration_regulates_the_live_network_taps_like_a_plain_solve()
    {
        // B is an OLTC transformer regulating bus 3. Once everything is energised, the last step must solve
        // exactly what SteadyStateSolver solves on the whole model (OLTC included); the OLTCs used to be dropped.
        var oltc = new[] { new Oltc("B", ControlsBus: 3, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.005) };
        GridModel m = RestorationModel(bStoredOpen: false, oltcs: oltc, transformer: true);
        RestorationReport rep = RestorationSequencer.Evaluate(m, new[] { "A", "B" });

        var solver = new SteadyStateSolver();
        PowerFlowResult full = solver.Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(solver.OltcPasses > 0, "the scenario must make the OLTC step");
        Assert.Equal(full.Vm.Min(), rep.Steps[1].MinVpu);
        Assert.Equal(full.Vm.Max(), rep.Steps[1].MaxVpu);
    }

    // ---- R229 / R230 ---------------------------------------------------------------------------------------

    [Fact]
    public void Watch_list_ignores_a_plant_bus_sitting_at_its_regulated_setpoint()
    {
        // Every machine bus is regulated at 1.05 pu and holds it; only a PQ bus that sags should be listed.
        var m = new GridModel
        {
            Name = "watch-r229",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Name: "plant"),
                new Bus(2, BusType.Pq, Pd: 60, Qd: 30, Name: "load"),
            },
            Generators = new[] { new Generator(1, 0, Vg: 1.05) },
            Branches = new[] { new Branch(1, 2, 0.02, 0.2) },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(1.05, r.Vm[0]);
        WatchListReport w = WatchList.Assess(m, r, new WatchListOptions { TopN = 10 });
        Assert.DoesNotContain(w.Items, i => i.Category == WatchCategory.VoltageLimit && i.Location == "plant");
        if (Math.Abs(r.Vm[1] - 1.0) >= VoltageLimits.WatchBandPu)
            Assert.Contains(w.Items, i => i.Category == WatchCategory.VoltageLimit && i.Location == "load");
    }

    [Fact]
    public void Watch_list_flags_a_plant_bus_that_has_lost_its_setpoint()
    {
        // A crafted solved state: the plant bus regulated at 1.05 pu reads 0.99 (its unit is at a reactive
        // limit). Deviation from the setpoint is 0.06 - listed, and described against the setpoint.
        var m = new GridModel
        {
            Name = "watch-r229b",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, Name: "plant"), new Bus(2, BusType.Pq, Name: "load") },
            Generators = new[] { new Generator(1, 0, Vg: 1.05) },
            Branches = new[] { new Branch(1, 2, 0.02, 0.2) },
        };
        PowerFlowResult r = Crafted(new[] { 1, 2 }, new[] { 0.99, 1.0 }, Array.Empty<LineFlow>());
        WatchListReport w = WatchList.Assess(m, r, new WatchListOptions { TopN = 10 });
        WatchItem item = Assert.Single(w.Items, i => i.Category == WatchCategory.VoltageLimit);
        Assert.Equal("plant", item.Location);
        Assert.Contains("setpoint", item.Description);
        Assert.Equal(Math.Clamp(Math.Abs(0.99 - 1.05) / VoltageLimits.StatutoryBandPu, 0, 1), item.Proximity);
    }

    [Fact]
    public void Watch_list_thermal_loading_uses_the_heavier_end()
    {
        // From end 10 MVA, To end about 95.5 MVA on a 100 MVA rating (a cable whose receiving end is heavy): the
        // branch is at 95%, not 10%. Before the fix it was not listed at all.
        var m = new GridModel
        {
            Name = "watch-r230",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: 100, Name: "C12") },
        };
        var lf = new LineFlow(1, 2, 10.0, 0.0, -9.9, -95.0, 0.1, -95.0, "C12", 100.0, 95.5);
        PowerFlowResult r = Crafted(new[] { 1, 2 }, new[] { 1.0, 1.0 }, new[] { lf });
        WatchListReport w = WatchList.Assess(m, r, new WatchListOptions { TopN = 10 });
        WatchItem item = Assert.Single(w.Items, i => i.Category == WatchCategory.ThermalOverload);
        double expected = Math.Sqrt(9.9 * 9.9 + 95.0 * 95.0) / 100.0;
        Assert.Equal(expected, item.Proximity);
        Assert.Contains("96% of rating", item.Description);
    }

    [Fact]
    public void Watch_list_fails_loud_on_a_result_that_does_not_match_the_model()
    {
        var m = new GridModel
        {
            Name = "watch-mismatch",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        };
        PowerFlowResult r = Crafted(new[] { 1, 7 }, new[] { 1.0, 0.9 }, Array.Empty<LineFlow>());
        Assert.Throws<InvalidOperationException>(() => WatchList.Assess(m, r));
    }

    private static PowerFlowResult Crafted(int[] ids, double[] vm, IReadOnlyList<LineFlow> flows) => new()
    {
        Converged = true, Iterations = 3, MaxMismatch = 1e-10, BusIds = ids, Vm = vm, VaDeg = new double[ids.Length],
        PInjMw = new double[ids.Length], QInjMvar = new double[ids.Length], TotalLossMw = 0, TotalLossMvar = 0,
        LineFlows = flows,
    };
}
