using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// LF1 (October 2026): low-hanging findings from review-progress/2026-10/NEW-FINDINGS.md, one partial file per N-id.
/// Every test here is new.
/// </summary>
public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N270: protection rows with an equal operating time are ordered by row index, not by a rounding-dependent margin.
    // =================================================================================================================

    private static RelayVerdict N270V(string device, int id, double margin, double? t) =>
        new(device, device, ProtectionLocation.Branch, id, $"L{id}", true, 0, "-", 1.0, margin, t, device, ProtectionConfidence.Generic);

    [Fact]
    public void N270_equal_operating_times_keep_the_row_order_whatever_the_margins()
    {
        // Three 21-Z2 rows at the same 0.30 s with margins in INCREASING order: the old rule (equal times by margin,
        // largest first) would have reversed them; the row index keeps them as produced.
        var rep = new ProtectionReport(new[]
        {
            N270V("21-Z2", 0, 0.1, 0.3),
            N270V("21-Z2", 1, 0.2, 0.3),
            N270V("21-Z2", 2, 0.3, 0.3),
        }, new ProtectionSettings(), false);
        Assert.Equal(new[] { 0, 1, 2 }, rep.Operating.Select(v => v.LocationId).ToArray());
    }

    [Fact]
    public void N270_margins_that_differ_only_in_the_last_bit_do_not_reorder_the_rows()
    {
        // Two rows whose margins are the same number in exact arithmetic but differ in the last bit, presented in both
        // last-bit orders: the operating order is the row order in both, so it cannot depend on rounding.
        double m = 0.2;
        double mUp = Math.BitIncrement(m);
        var a = new ProtectionReport(new[] { N270V("21-Z2", 7, m, 0.3), N270V("21-Z2", 8, mUp, 0.3) }, new ProtectionSettings(), false);
        var b = new ProtectionReport(new[] { N270V("21-Z2", 7, mUp, 0.3), N270V("21-Z2", 8, m, 0.3) }, new ProtectionSettings(), false);
        Assert.Equal(new[] { 7, 8 }, a.Operating.Select(v => v.LocationId).ToArray());
        Assert.Equal(new[] { 7, 8 }, b.Operating.Select(v => v.LocationId).ToArray());
    }

    [Fact]
    public void N270_different_times_still_order_fastest_first_and_untimed_rows_by_relative_margin()
    {
        // The parts of the D-DOC-158 order N270 does not touch: time ascending first (a later row with a shorter time
        // still comes first), then untimed rows by the margin relative to the pickup.
        var rep = new ProtectionReport(new[]
        {
            N270V("51", 0, 9.0, 0.8),
            N270V("27", 1, 0.1, null),
            N270V("50", 2, 1.0, 0.0),
            N270V("27", 3, 0.5, null),
            N270V("21-Z1", 4, 0.01, 0.0),
        }, new ProtectionSettings(), false);
        Assert.Equal(new[] { 2, 4, 0, 3, 1 }, rep.Operating.Select(v => v.LocationId).ToArray());
    }

    [Fact]
    public void N270_case14_bus4_fault_lists_the_tied_zone_rows_in_branch_order()
    {
        // The case that exposed the defect: a bolted fault at case14 bus 4. The five 21-Z2 rows (0.30 s) have the same
        // margin in exact arithmetic; the three 21-Z3 rows (0.80 s) have different margins but the same time. Both
        // groups now list in branch order (the case's branch table: 1-5 is row 1, 2-4 row 3, 2-5 row 4, 3-4 row 5, 4-5
        // row 6, 4-7 row 7, 4-9 row 8, 5-6 row 9).
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(pre.Converged);
        (_, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, 4);
        string[] z2 = rep.Operating.Where(v => v.Device == "21-Z2").Select(v => v.LocationName).ToArray();
        string[] z3 = rep.Operating.Where(v => v.Device == "21-Z3").Select(v => v.LocationName).ToArray();
        Assert.Equal(new[] { "branch 2-4", "branch 3-4", "branch 4-5", "branch 4-7", "branch 4-9" }, z2);
        Assert.Equal(new[] { "branch 1-5", "branch 2-5", "branch 5-6" }, z3);
        // The 87 of the faulted bus stays first and is the primary clearance.
        Assert.Equal("87", rep.Operating[0].Device);
        Assert.Equal("87", rep.PrimaryClearance!.Device);
    }
}
