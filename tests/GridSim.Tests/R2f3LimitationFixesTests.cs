using System;
using System.Collections.Generic;
using GridSim.Core.Analysis;
using GridSim.Core.Assets;
using GridSim.Core.Model;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Round-two fixes of documented limitations (October 2026): storage sizing and energy accounting (GR-136, GR-627,
/// GR-626), contingency netting of worsened base-case violations (GR-125) and the DC model's zero-reactance branch
/// inside a mesh (GR-127).
/// </summary>
public class R2f3LimitationFixesTests
{
    // ---- GR-136: PackModel.Sized checks the energy it achieves ----
    [Fact]
    public void A_pack_far_smaller_than_one_string_is_refused()
    {
        // 10 kWh at 1000 V NMC: one string holds tens of kWh, so the built pack would be several times the request.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PackModel.Sized(BatteryChemistryLibrary.Nmc, 10e3, 1000, 0.5));
        Assert.Contains("whole strings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pack_within_the_sizing_tolerance_is_built_as_before()
    {
        PackModel pack = PackModel.Sized(BatteryChemistryLibrary.Nmc, 1e6, 1000, 0.5);
        Assert.InRange(pack.NominalEnergyWh, 1e6 * (1 - PackModel.MaxSizingRelativeError), 1e6 * (1 + PackModel.MaxSizingRelativeError));
        // A smaller DC voltage (a shorter string) makes the same small request buildable.
        PackModel small = PackModel.Sized(BatteryChemistryLibrary.Nmc, 10e3, 48, 0.5);
        Assert.InRange(small.NominalEnergyWh, 5e3, 15e3);
    }

    [Theory]
    [InlineData(0.0, 1000.0)]
    [InlineData(double.NaN, 1000.0)]
    [InlineData(1e6, 0.0)]
    [InlineData(1e6, double.PositiveInfinity)]
    public void Pack_sizing_refuses_an_energy_or_voltage_that_is_not_a_positive_number(double energyWh, double dcVoltage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PackModel.Sized(BatteryChemistryLibrary.Nmc, energyWh, dcVoltage, 0.5));
    }

    // ---- GR-627: the SoC bucket delivers only the energy it holds on the crossing step ----
    [Fact]
    public void The_soc_bucket_crossing_its_floor_delivers_only_the_energy_left()
    {
        // 1 MWh, floor 0.1, at 0.2: 0.1 MWh is left above the floor. A 1 MW draw for one hour asks for 1 MWh.
        var bucket = new SocBucket(capacityMwh: 1.0, socMin: 0.1, socMax: 0.9, socInit: 0.2);
        double delivered = bucket.Draw(1.0, 3600.0);
        Assert.Equal(0.1, bucket.SocFraction, 12);
        Assert.Equal(0.1, delivered, 12);   // 0.1 MWh over 1 h; it used to return the full 1 MW
        Assert.Equal(0.0, bucket.Headroom().UpMw);
    }

    [Fact]
    public void The_soc_bucket_crossing_its_ceiling_absorbs_only_the_room_left()
    {
        var bucket = new SocBucket(capacityMwh: 2.0, socMin: 0.0, socMax: 0.9, socInit: 0.8);
        double delivered = bucket.Draw(-1.0, 3600.0);   // charge 1 MWh into 0.2 MWh of room
        Assert.Equal(0.9, bucket.SocFraction, 12);
        Assert.Equal(-0.2, delivered, 12);
    }

    [Fact]
    public void A_soc_bucket_step_inside_the_window_is_unchanged()
    {
        var bucket = new SocBucket(capacityMwh: 2.0, socMin: 0.1, socMax: 0.9, socInit: 0.5);
        Assert.Equal(0.7, bucket.Draw(0.7, 600.0));   // exactly the request, as before
        Assert.Equal(0.5 - 0.7 * 600.0 / 3600.0 / 2.0, bucket.SocFraction);
    }

    // ---- GR-626: an end-energy request outside the energy window is refused ----
    [Theory]
    [InlineData(120.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void The_storage_lp_refuses_an_end_energy_outside_the_window(double endMwh)
    {
        var env = new StorageEnvelope(50, 50, 10, 100, 50, 0.95, 0.95);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StorageLpScheduler.Solve(env, new[] { 10.0, 80.0, 20.0 }, 1.0, endEnergyMwh: endMwh));
    }

    [Fact]
    public void The_storage_lp_accepts_an_end_energy_inside_the_window()
    {
        var env = new StorageEnvelope(50, 50, 10, 100, 50, 0.95, 0.95);
        StorageLpScheduler.Plan plan = StorageLpScheduler.Solve(env, new[] { 10.0, 80.0, 20.0 }, 1.0, endEnergyMwh: 60.0);
        Assert.True(plan.EnergyMwh[^1] >= 60.0 - 1e-6);
    }

    // ---- GR-125: netting reports (and on request counts) a worsened base-case violation ----
    private static ContingencyResult State(string label, params ContingencyViolation[] details) =>
        new(label, true, 0, 0, 1.0, 1.0, new List<string>()) { Details = details };

    [Fact]
    public void A_deepened_base_overload_is_reported_and_optionally_counted()
    {
        ContingencyResult baseCase = State("base", new ContingencyViolation(ContingencyViolationKind.BranchOverload, 3, 105.0, "L3"));
        ContingencyResult deepened = State("out A", new ContingencyViolation(ContingencyViolationKind.BranchOverload, 3, 140.0, "L3"));
        ContingencyResult same = State("out B", new ContingencyViolation(ContingencyViolationKind.BranchOverload, 3, 105.5, "L3"));

        // Default netting: the D03 verdicts do not move, but the deepened overload is named.
        ContingencyResult d = ContingencyAnalysis.NetAgainstBase(deepened, baseCase);
        Assert.True(d.Secure);
        Assert.Equal(new[] { "branch L3 140%" }, d.WorsenedPreExistingViolations);
        Assert.Equal(new[] { "branch L3 140%" }, d.PreExistingViolations);

        // Within the margin: not worsened.
        ContingencyResult s = ContingencyAnalysis.NetAgainstBase(same, baseCase);
        Assert.Empty(s.WorsenedPreExistingViolations);

        // Counted: the deepened overload is the outage's own violation.
        ContingencyResult c = ContingencyAnalysis.NetAgainstBase(deepened, baseCase, countWorsened: true);
        Assert.False(c.Secure);
        Assert.Equal(new[] { "branch L3 140%" }, c.Violations);
        Assert.Empty(c.PreExistingViolations);
        Assert.True(ContingencyAnalysis.NetAgainstBase(same, baseCase, countWorsened: true).Secure);
    }

    [Fact]
    public void A_deeper_base_undervoltage_counts_as_worsened()
    {
        ContingencyResult baseCase = State("base", new ContingencyViolation(ContingencyViolationKind.Undervoltage, 7, 0.930, "bus 7"));
        ContingencyResult deeper = State("out", new ContingencyViolation(ContingencyViolationKind.Undervoltage, 7, 0.900, "bus 7"));
        Assert.Equal(new[] { "undervoltage 0.900 p.u." }, ContingencyAnalysis.NetAgainstBase(deeper, baseCase).WorsenedPreExistingViolations);
    }

    // ---- GR-127: a zero-reactance branch inside a mesh is refused by the DC model ----
    [Fact]
    public void The_dc_model_refuses_a_zero_reactance_branch_inside_a_mesh()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10), new Bus(3, BusType.Pq, Pd: 10) },
            Generators = new[] { new Generator(1, Pg: 20) },
            // A triangle; the 2-3 side is a resistive coupler with X = 0 (it closes a mesh, it is not the only link).
            Branches = new[] { new Branch(1, 2, R: 0.01, X: 0.1), new Branch(1, 3, R: 0.01, X: 0.1), new Branch(2, 3, R: 0.001, X: 0.0, Name: "coupler") },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => DistributionFactors.Build(m));
        Assert.Contains("'coupler'", ex.Message, StringComparison.Ordinal);
    }
}
