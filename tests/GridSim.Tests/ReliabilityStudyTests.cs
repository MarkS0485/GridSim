using System.Text.Json;
using GridSim.Core.Analysis.Reliability;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8 reliability package (<see cref="ReliabilityStudy"/>) against hand-derived closed forms on a small radial feeder.
/// <para>
/// Feeder: source - S1 - (A) - S2 - (B) - S3 - (C), and a fused lateral L1 from the end of S1 to (D). Every main section
/// has a disconnector at its upstream end; the feeder breaker clears main-line faults and the L1 fuse clears L1's.
/// With switching time s, the classical failure-modes-and-effects result (Billinton and Allan, radial systems with
/// disconnects and lateral fuses) is, per load point (lambda in failures/yr, r and s in h):
/// A: lambda = l1 + l2 + l3, U = l1 r1 + (l2 + l3) s;
/// B: lambda = l1 + l2 + l3, U = l1 r1 + l2 r2 + l3 s;
/// C: lambda = l1 + l2 + l3, U = l1 r1 + l2 r2 + l3 r3;
/// D: lambda = l1 + l2 + l3 + lL, U = l1 r1 + (l2 + l3) s + lL rL.
/// With l1 = 0.2, r1 = 4, l2 = 0.1, r2 = 3, l3 = 0.3, r3 = 2, lL = 0.5, rL = 1, s = 0.5 and customers 100, 50, 80, 20,
/// average loads 0.5, 0.3, 0.4, 0.1 MW: U = 1.0, 1.25, 1.7, 1.5 h/yr; SAIFI = 160 / 250 = 0.64 interruptions/(customer.yr);
/// SAIDI = 328.5 / 250 = 1.314 h/(customer.yr); CAIDI = 2.053125 h/interruption; ENS = 1.705 MWh/yr;
/// AENS = 0.00682 MWh/(customer.yr).
/// </para>
/// </summary>
public sealed class ReliabilityStudyTests
{
    private const double L1 = 0.2, R1 = 4, L2 = 0.1, R2 = 3, L3 = 0.3, R3 = 2, LL = 0.5, RL = 1, S = 0.5;
    private static readonly ReliabilityAssumptions Assume = new(ReliabilityStudy.IndependentFirstOrder, ReliabilityStudy.NoAlternateSupply, 8760);

    private static ReliabilityComponent[] Components(bool switches = true, bool lateralFused = true, bool lateralSwitch = false) =>
    [
        new("S1", null, false, switches, L1, R1),
        new("S2", "S1", false, switches, L2, R2),
        new("S3", "S2", false, switches, L3, R3),
        new("L1", "S1", lateralFused, lateralSwitch, LL, RL),
    ];

    private static readonly ReliabilityLoadPoint[] Points =
    [
        new("A", "S1", 100, 0.5),
        new("B", "S2", 50, 0.3),
        new("C", "S3", 80, 0.4),
        new("D", "L1", 20, 0.1),
    ];

    private static double Index(ReliabilityStudyResult r, string name) => r.SystemIndices.Single(i => i.Name == name).Value!.Value;
    private static LoadPointReliability Point(ReliabilityStudyResult r, string id) => r.LoadPoints.Single(p => p.Id == id);

    [Fact]
    public void Load_point_and_system_indices_match_the_closed_forms_with_units()
    {
        ReliabilityStudyResult r = ReliabilityStudy.Run(new ReliabilityRequest(Components(), Points, Assume, S));
        Assert.Equal("COMPLETE", r.Status);
        Assert.Empty(r.Missing);

        double[] lambda = [L1 + L2 + L3, L1 + L2 + L3, L1 + L2 + L3, L1 + L2 + L3 + LL];
        double[] u = [L1 * R1 + (L2 + L3) * S, L1 * R1 + L2 * R2 + L3 * S, L1 * R1 + L2 * R2 + L3 * R3, L1 * R1 + (L2 + L3) * S + LL * RL];
        double[] hand = [1.0, 1.25, 1.7, 1.5];
        int[] n = [100, 50, 80, 20];
        double[] load = [0.5, 0.3, 0.4, 0.1];
        for (int j = 0; j < 4; j++)
        {
            LoadPointReliability p = r.LoadPoints[j];
            Assert.Equal(lambda[j], p.InterruptionsPerYear!.Value, 12);
            Assert.Equal(u[j], p.OutageHoursPerYear!.Value, 12);
            Assert.Equal(hand[j], p.OutageHoursPerYear!.Value, 12);
            Assert.Equal(u[j] / lambda[j], p.HoursPerInterruption!.Value, 12);
            Assert.Equal(load[j] * u[j], p.EnergyNotSuppliedMwhPerYear!.Value, 12);
        }
        Assert.Equal(0.64, Index(r, "SAIFI"), 12);
        Assert.Equal(1.314, Index(r, "SAIDI"), 12);
        Assert.Equal(1.314 / 0.64, Index(r, "CAIDI"), 12);
        Assert.Equal(2.053125, Index(r, "CAIDI"), 12);
        Assert.Equal(1.705, Index(r, "ENS"), 12);
        Assert.Equal(1.705 / 250, Index(r, "AENS"), 12);
        Assert.Equal(1 - 1.314 / 8760, Index(r, "ASAI"), 12);
        Assert.Equal(250, r.TotalCustomers);
        Assert.Equal(Enumerable.Range(0, 4).Sum(j => lambda[j] * n[j]) / 250, Index(r, "SAIFI"), 12);

        Assert.Equal("interruptions/(customer.yr)", r.SystemIndices.Single(i => i.Name == "SAIFI").Units);
        Assert.Equal("h/(customer.yr)", r.SystemIndices.Single(i => i.Name == "SAIDI").Units);
        Assert.Equal("h/interruption", r.SystemIndices.Single(i => i.Name == "CAIDI").Units);
        Assert.Equal("MWh/yr", r.SystemIndices.Single(i => i.Name == "ENS").Units);
        Assert.Equal(L1 * R1 > L3 * R3 ? L1 * R1 : L3 * R3, r.MaxLambdaRepairProductPerYear!.Value, 12);
        Assert.NotEmpty(r.Scope);
    }

    [Fact]
    public void Failure_mode_table_records_clearing_device_and_restoration_kind()
    {
        ReliabilityStudyResult r = ReliabilityStudy.Run(new ReliabilityRequest(Components(), Points, Assume, S));
        LoadPointReliability a = Point(r, "A");
        Assert.Equal(["S1", "S2", "S3"], a.Exposures.Select(e => e.Component).ToArray());   // L1's fuse spares A
        Assert.Equal(["Repair", "Switching", "Switching"], a.Exposures.Select(e => e.Restoration).ToArray());
        Assert.All(a.Exposures, e => Assert.Equal("FeederBreaker", e.ClearedBy));
        ReliabilityExposure lateral = Point(r, "D").Exposures.Single(e => e.Component == "L1");
        Assert.Equal("L1", lateral.ClearedBy);
        Assert.Equal("Repair", lateral.Restoration);
        Assert.Equal(LL * RL, lateral.UnavailabilityHoursPerYear!.Value, 12);
    }

    [Fact]
    public void Without_disconnectors_every_load_point_waits_for_every_repair()
    {
        ReliabilityStudyResult r = ReliabilityStudy.Run(new ReliabilityRequest(Components(switches: false), Points, Assume, S));
        double main = L1 * R1 + L2 * R2 + L3 * R3;
        Assert.Equal(main, Point(r, "A").OutageHoursPerYear!.Value, 12);
        Assert.Equal(main, Point(r, "B").OutageHoursPerYear!.Value, 12);
        Assert.Equal(main, Point(r, "C").OutageHoursPerYear!.Value, 12);
        Assert.Equal(main + LL * RL, Point(r, "D").OutageHoursPerYear!.Value, 12);
        Assert.Equal((main * 230 + (main + LL * RL) * 20) / 250, Index(r, "SAIDI"), 12);
    }

    [Fact]
    public void Unfused_lateral_interrupts_the_whole_feeder_and_its_disconnector_shortens_the_wait()
    {
        // L1 without a fuse: the breaker clears its faults, so A, B and C also see lL. Without a disconnector at L1's
        // head they wait for its repair rL; with one, for the switching time s.
        ReliabilityStudyResult noSwitch = ReliabilityStudy.Run(new ReliabilityRequest(Components(lateralFused: false), Points, Assume, S));
        ReliabilityStudyResult withSwitch = ReliabilityStudy.Run(new ReliabilityRequest(Components(lateralFused: false, lateralSwitch: true), Points, Assume, S));
        Assert.Equal(L1 + L2 + L3 + LL, Point(noSwitch, "A").InterruptionsPerYear!.Value, 12);
        Assert.Equal(L1 * R1 + (L2 + L3) * S + LL * RL, Point(noSwitch, "A").OutageHoursPerYear!.Value, 12);
        Assert.Equal(L1 * R1 + (L2 + L3) * S + LL * S, Point(withSwitch, "A").OutageHoursPerYear!.Value, 12);
        Assert.Equal(L1 * R1 + L2 * R2 + L3 * R3 + LL * S, Point(withSwitch, "C").OutageHoursPerYear!.Value, 12);
        Assert.Equal((L1 + L2 + L3 + LL) * 250, Index(noSwitch, "SAIFI") * 250, 10);
    }

    [Fact]
    public void Missing_rate_data_is_reported_and_withholds_only_what_it_affects()
    {
        ReliabilityComponent[] c = Components();
        c[2] = c[2] with { FailuresPerYear = null };
        ReliabilityStudyResult r = ReliabilityStudy.Run(new ReliabilityRequest(c, Points, Assume, S));
        Assert.Equal("INCOMPLETE", r.Status);
        MissingRateData m = Assert.Single(r.Missing);
        Assert.Equal("S3", m.Component);
        Assert.Equal("failuresPerYear", m.Field);
        Assert.Equal(["A", "B", "C", "D"], m.AffectedLoadPoints);
        Assert.All(r.LoadPoints, p => Assert.Null(p.InterruptionsPerYear));
        Assert.Null(r.SystemIndices.Single(i => i.Name == "SAIFI").Value);

        // No switching time: A, B and D need it (an isolable upstream fault); C sees only repairs and stays computed.
        ReliabilityStudyResult noS = ReliabilityStudy.Run(new ReliabilityRequest(Components(), Points, Assume));
        Assert.Equal("INCOMPLETE", noS.Status);
        MissingRateData s = Assert.Single(noS.Missing);
        Assert.Equal("switchingHours", s.Field);
        Assert.Equal(["A", "B", "D"], s.AffectedLoadPoints);
        Assert.Equal(L1 * R1 + L2 * R2 + L3 * R3, Point(noS, "C").OutageHoursPerYear!.Value, 12);
        Assert.Null(Point(noS, "A").OutageHoursPerYear);
        Assert.Equal(0.64, Index(noS, "SAIFI"), 12);                    // frequency needs no durations
        Assert.Null(noS.SystemIndices.Single(i => i.Name == "SAIDI").Value);

        ReliabilityComponent[] noRepair = Components();
        noRepair[3] = noRepair[3] with { RepairHours = null };
        MissingRateData rr = Assert.Single(ReliabilityStudy.Run(new ReliabilityRequest(noRepair, Points, Assume, S)).Missing);
        Assert.Equal(("L1", "repairHours"), (rr.Component, rr.Field));
        Assert.Equal(["D"], rr.AffectedLoadPoints);
    }

    [Fact]
    public void Assumptions_and_inputs_are_checked_before_any_index()
    {
        ReliabilityComponent[] c = Components();
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, Points, Assume with { FailureDependency = "CommonMode" }, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, Points, Assume with { AlternateSupply = "Backfeed" }, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, Points, Assume with { HoursPerYear = 0 }, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, Points, null!, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, Points, Assume, -1)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new([c[0] with { FailuresPerYear = -0.1 }], [Points[0]], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new([c[0], c[0]], [Points[0]], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new([c[0] with { Parent = "Z" }], [Points[0]], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new([c[0] with { Parent = "S2" }, c[1]], [Points[0]], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, [Points[0] with { Component = "Q" }], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, [Points[0] with { Customers = 0 }], Assume, S)));
        Assert.Throws<ArgumentException>(() => ReliabilityStudy.Run(new(c, [Points[0] with { AverageLoadMw = double.NaN }], Assume, S)));
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        var request = new ReliabilityRequest(Components(), Points, Assume, S);
        Assert.Equal(JsonSerializer.Serialize(ReliabilityStudy.Run(request)), JsonSerializer.Serialize(ReliabilityStudy.Run(request)));
    }
}
