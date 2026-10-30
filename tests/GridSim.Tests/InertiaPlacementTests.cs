using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Placement;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The inertia placement study (October 2026, <see cref="InertiaPlacementStudy"/>): its building blocks and the
/// <c>inertia-placement</c> verb. The bus measurement it rests on is tested in <see cref="BusFrequencyMeterTests"/>.
/// </summary>
public sealed class InertiaPlacementTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    // ---- The study's building blocks ----

    [Fact]
    public void Great_circle_miles_match_known_distances()
    {
        // London (51.5074, -0.1278) to Edinburgh (55.9533, -3.1883): 331.6 miles great circle.
        Assert.Equal(331.6, InertiaPlacementStudy.Miles(51.5074, -0.1278, 55.9533, -3.1883), 0);
        Assert.Equal(0.0, InertiaPlacementStudy.Miles(52.0, -1.0, 52.0, -1.0));
        // One degree of latitude is 69.09 miles on the mean sphere.
        Assert.Equal(69.09, InertiaPlacementStudy.Miles(52.0, -1.0, 53.0, -1.0), 1);
    }

    [Fact]
    public void Condensers_add_exactly_the_budget_less_the_residual_and_keep_every_existing_unit_in_place()
    {
        GridModel m = Uk();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 100 });
        InertiaPlacement pl = InertiaPlacementStudy.FleetPlacement(m);
        Assert.Equal(1.0, pl.Shares.Sum(s => s.Share), 12);
        double fleet = m.Generators.Where(InertiaPlacementStudy.IsMachine).Sum(g => g.H * g.MBase) / 1000.0;
        GridModel w = InertiaPlacementStudy.WithCondensers(m, pf, pl, 2.0 * fleet, 0.25, 8.0, out double residual);
        Assert.Equal(0.25 * fleet, residual, 9);
        for (int i = 0; i < m.Generators.Count; i++)
        {
            Assert.Equal(m.Generators[i].Bus, w.Generators[i].Bus);
            Assert.Equal(m.Generators[i].Pg, w.Generators[i].Pg);
        }
        double added = w.Generators.Skip(m.Generators.Count).Sum(g => g.H * g.MBase) / 1000.0;
        Assert.Equal(2.0 * fleet - residual, added, 9);
        Assert.All(w.Generators.Skip(m.Generators.Count), g => { Assert.Equal(0.0, g.Pg); Assert.Equal(8.0, g.H); Assert.False(g.IsConverterInterfaced); });

        // The condensers inject nothing at the solved state, so the power flow is the case's to solver tolerance.
        PowerFlowResult pw = new NewtonRaphsonPowerFlow().Solve(w, new PowerFlowOptions { MaxIterations = 100 });
        Assert.True(pw.Converged);
        for (int i = 0; i < pf.Vm.Length; i++) Assert.Equal(pf.Vm[i], pw.Vm[i], 6);

        // A budget at or below the residual is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => InertiaPlacementStudy.WithCondensers(m, pf, pl, 0.2 * fleet, 0.25, 8.0, out _));
    }

    [Fact]
    public void No_placement_puts_a_condenser_on_the_slack_bus()
    {
        // Bug #35 (2.0.143): the demand placement put one on gb-full's slack (Melksham), the RMS initialisation split the
        // slack's output with it and its exciter could not start. Every placement now leaves a slack bus out, and a
        // placement naming one is refused before any work.
        GridModel m = Uk();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { MaxIterations = 100 });
        int slack = m.Buses.Single(b => b.Type == BusType.Slack).Id;
        var sites = InertiaPlacementStudy.SpineTSites.Select(s => InertiaPlacementStudy.MapSite(m, s)).ToList();
        Assert.DoesNotContain(sites, s => s.BusId == slack);
        Assert.DoesNotContain(InertiaPlacementStudy.DemandPlacement(m).Shares, s => s.BusId == slack);
        Assert.DoesNotContain(InertiaPlacementStudy.FleetPlacement(m).Shares, s => s.BusId == slack);
        var bad = new InertiaPlacement("bad", "on the slack", new[] { (slack, 1.0) });
        Assert.Throws<ArgumentException>(() => InertiaPlacementStudy.WithCondensers(m, pf, bad, 1000.0, 0.25, 8.0, out _));
    }

    [Fact]
    public void Condensers_at_unit_buses_leave_the_power_flow_as_it_was()
    {
        // Bug #37 (2.0.146): an appended condenser took the bus setpoint (the power flow takes a bus's setpoint from its
        // last unit) with the solved voltage, so at a unit bus whose solved voltage was off its setpoint the power flow
        // moved: on gb-full's fleet placement, Willesden 66 kV by 0.0010613504115799532 pu. It now keeps the last unit's
        // setpoint. What is left is the microvar each way each condenser adds to its bus's reactive limits (so that the RMS
        // initialisation gives it no share of the bus's reactive output): at a bus on its limit it moves the voltage by
        // about 2e-11 pu (Fourstones), so the check is to 1e-9 pu and 1e-9 degrees.
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-full"));
        var opts = new PowerFlowOptions { MaxIterations = 100 };
        PowerFlowResult pf = new SparseNewtonRaphsonPowerFlow().Solve(m, opts);
        Assert.True(pf.Converged);
        GridModel w = InertiaPlacementStudy.WithCondensers(m, pf, InertiaPlacementStudy.FleetPlacement(m), 240.0, 0.25, 8.0, out _);
        PowerFlowResult pw = new SparseNewtonRaphsonPowerFlow().Solve(w, opts);
        Assert.True(pw.Converged);
        for (int i = 0; i < pf.Vm.Length; i++)
        {
            Assert.True(Math.Abs(pf.Vm[i] - pw.Vm[i]) <= 1e-9, $"bus {m.Buses[i].Id}: {pf.Vm[i]:R} vs {pw.Vm[i]:R}");
            Assert.True(Math.Abs(pf.VaDeg[i] - pw.VaDeg[i]) <= 1e-9, $"bus {m.Buses[i].Id} angle: {pf.VaDeg[i]:R} vs {pw.VaDeg[i]:R}");
        }
    }

    [Fact]
    public void The_oscillation_measure_reads_a_damped_swing_through_a_faster_ripple()
    {
        // A 0.6 Hz swing with damping ratio 0.05, plus a 2.5 Hz ripple of 40 % of its amplitude (a local swing). The first
        // two positive peaks of the raw series belong to the ripple; the measure smooths it out and fits every half-cycle.
        const double dt = 0.005, f = 0.6, zeta = 0.05, fr = 2.5;
        double wn = 2.0 * Math.PI * f / Math.Sqrt(1.0 - zeta * zeta);
        var x = new double[1400];
        for (int k = 0; k < x.Length; k++)
        {
            double t = k * dt;
            x[k] = Math.Exp(-zeta * wn * t) * Math.Sin(2.0 * Math.PI * f * t) + 0.4 * Math.Exp(-0.3 * t) * Math.Sin(2.0 * Math.PI * fr * t);
        }
        (double z, double freq, int halves) = InertiaPlacementStudy.Oscillation(x, dt, 0, 80);
        Assert.True(halves >= 6, $"{halves} half-cycles");
        Assert.Equal(0.6, freq, 1);
        Assert.InRange(z, 0.035, 0.065);

        // A growing swing reads negative; a series with too few half-cycles is NaN.
        var g = new double[1400];
        for (int k = 0; k < g.Length; k++) g[k] = Math.Exp(0.2 * k * dt) * Math.Sin(2.0 * Math.PI * f * k * dt);
        Assert.True(InertiaPlacementStudy.Oscillation(g, dt, 0, 80).Zeta < 0.0);
        Assert.True(double.IsNaN(InertiaPlacementStudy.Oscillation(new double[200], dt, 0, 80).Zeta));
    }

    [Fact]
    public void Sites_map_to_the_nearest_transmission_bus()
    {
        GridModel m = Uk();
        MappedSite s = InertiaPlacementStudy.MapSite(m, new GeoSite("probe", 51.5, -0.2));
        Assert.True(s.BaseKv >= 275.0);
        foreach (Bus b in m.Buses.Where(b => b.BaseKv >= 275.0 && !(b.X == 0.0 && b.Y == 0.0) && b.Type != BusType.Slack))
            Assert.True(InertiaPlacementStudy.Miles(51.5, -0.2, InertiaPlacementStudy.LatOf(b), InertiaPlacementStudy.LonOf(b)) >= s.DistanceMiles);
    }

    [Fact]
    public void The_lumped_time_to_the_floor_falls_with_less_inertia()
    {
        GridModel m = Uk();
        var o = new InertiaPlacementOptions();
        LumpedResponseRun big = InertiaPlacementStudy.Lumped(m, 700.0, 30000.0, 1800.0, o);
        LumpedResponseRun small = InertiaPlacementStudy.Lumped(m, 240.0, 30000.0, 1800.0, o);
        Assert.True(small.TimeToFloorSeconds < big.TimeToFloorSeconds, $"{small.TimeToFloorSeconds} vs {big.TimeToFloorSeconds}");
        // With no response the swing equation gives 0.2 Hz / (f0 dP / 2E): 1.067 s at 240 GVA.s for 1.8 GW. Response and load
        // damping only slow the fall, so the lumped time is at least that.
        Assert.True(small.TimeToFloorSeconds >= 0.2 * 2.0 * 240000.0 / (50.0 * 1800.0) - 0.02);
    }

    // ---- The verb, run as the built CLI ----

    [Fact]
    public void The_verb_runs_on_uk_and_writes_one_row_per_run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-inertia-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = Path.Combine(dir, "r.json");
            CliProcess.Result r = CliProcess.RunBuilt("inertia-placement", "uk", "--placements", "fleet,spine-t", "--budgets", "200,400",
                "--losses", "scotland", "--json", json);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("INERTIA PLACEMENT STUDY", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("smallest scanned budget meeting each criterion", r.StdOut, StringComparison.Ordinal);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            System.Text.Json.JsonElement runs = doc.RootElement.GetProperty("tables").GetProperty("runs");
            Assert.Equal(4, runs.GetArrayLength());   // 2 placements x 2 budgets x 1 loss
            Assert.Equal("ieeet1", doc.RootElement.GetProperty("summary").GetProperty("avr").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("--placements", "nowhere")]
    [InlineData("--budgets", "400,200")]
    [InlineData("--losses", "dover")]
    [InlineData("--avr", "dc1a")]
    [InlineData("--residual", "0")]
    [InlineData("--top", "3")]
    public void A_malformed_or_foreign_flag_exits_2(string flag, string value)
    {
        CliProcess.Result r = CliProcess.RunBuilt("inertia-placement", "uk", flag, value);
        Assert.Equal(2, r.ExitCode);
    }
}
