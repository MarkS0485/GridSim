using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// LF2 (October 2026): contained findings from review-progress/2026-10/NEW-FINDINGS.md, one partial file per N-id.
/// Every test here is new.
/// </summary>
public partial class Lf2ContainedTests
{
    // =================================================================================================================
    // N277: the protection cascade takes simultaneous (equal-time) branch trips in row order, not by margin.
    // =================================================================================================================

    /// <summary>An operating verdict on branch <paramref name="id"/> with the given margin and time.</summary>
    private static RelayVerdict N277V(string device, int id, double margin, double? t,
        ProtectionLocation kind = ProtectionLocation.Branch, bool operate = true) =>
        new(device, device, kind, id, $"L{id}", operate, 0, "-", 1.0, margin, t, device, ProtectionConfidence.Generic);

    private static ProtectionReport N277Report(params RelayVerdict[] rows) => new(rows, new ProtectionSettings(), false);

    /// <summary>The rule ProtectionCascade used before N277, kept here verbatim as the comparison oracle.</summary>
    private static RelayVerdict? N277OldRule(ProtectionReport rep) =>
        rep.Operating
            .Where(v => v.LocationKind == ProtectionLocation.Branch
                && (v.Device is "49" or "50" or "51" || v.Device.StartsWith("21", StringComparison.Ordinal)))
            .OrderBy(v => v.TimeToTripS ?? 0.0)
            .ThenByDescending(v => v.Margin)
            .FirstOrDefault();

    [Fact]
    public void N277_equal_times_take_the_first_row_whatever_the_margins()
    {
        // Three instantaneous 50s with margins INCREASING down the rows: the old rule took the largest margin (row 2);
        // the row index takes row 0.
        ProtectionReport rep = N277Report(
            N277V("50", 10, 0.1, 0.0),
            N277V("50", 11, 0.2, 0.0),
            N277V("50", 12, 0.3, 0.0));
        Assert.Equal(10, ProtectionCascade.NextBranchTrip(rep)!.LocationId);
        Assert.Equal(12, N277OldRule(rep)!.LocationId);   // the defect being removed, stated
    }

    [Fact]
    public void N277_margins_that_differ_only_in_the_last_bit_do_not_change_the_pick()
    {
        // The same pair in both last-bit orders: the pick is row 0 in both, so it cannot depend on rounding.
        double m = 0.2;
        double mUp = Math.BitIncrement(m);
        ProtectionReport a = N277Report(N277V("21-Z2", 7, m, 0.3), N277V("21-Z2", 8, mUp, 0.3));
        ProtectionReport b = N277Report(N277V("21-Z2", 7, mUp, 0.3), N277V("21-Z2", 8, m, 0.3));
        Assert.Equal(7, ProtectionCascade.NextBranchTrip(a)!.LocationId);
        Assert.Equal(7, ProtectionCascade.NextBranchTrip(b)!.LocationId);
    }

    [Fact]
    public void N277_a_strictly_faster_later_row_still_wins()
    {
        // Time first, exactly as before: a later row with a shorter time is taken, even by one ulp.
        double t = 0.3;
        ProtectionReport rep = N277Report(
            N277V("51", 0, 9.0, t),
            N277V("21-Z2", 1, 0.01, Math.BitDecrement(t)),
            N277V("49", 2, 5.0, 100.0));
        Assert.Equal(1, ProtectionCascade.NextBranchTrip(rep)!.LocationId);
    }

    [Fact]
    public void N277_only_operating_branch_tripping_elements_are_candidates()
    {
        // Rows that do not open a branch are skipped whatever their time: a bus 27, a branch 37 (undercurrent is not a
        // branch trip), a bus 87, and a non-operating 50. The first branch-tripping operating row is taken.
        ProtectionReport rep = N277Report(
            N277V("27", 0, 0.5, null, ProtectionLocation.Bus),
            N277V("37", 1, 0.5, null),
            N277V("87", 2, 9.0, 0.0, ProtectionLocation.Bus),
            N277V("50", 3, -0.1, null, operate: false),
            N277V("50", 4, 0.1, 0.0),
            N277V("21-Z1", 5, 0.9, 0.0));
        Assert.Equal(4, ProtectionCascade.NextBranchTrip(rep)!.LocationId);
        Assert.Null(ProtectionCascade.NextBranchTrip(N277Report(N277V("27", 0, 0.5, null, ProtectionLocation.Bus))));
        Assert.Null(ProtectionCascade.NextBranchTrip(N277Report()));
    }

    [Fact]
    public void N277_an_untimed_branch_trip_still_counts_as_instantaneous()
    {
        // Unchanged from before N277 (TimeToTripS ?? 0.0): an untimed branch-tripping element ties with an
        // instantaneous one, and the tie now goes to the earlier row.
        ProtectionReport rep = N277Report(N277V("50", 0, 0.1, 0.0), N277V("49", 1, 0.9, null));
        Assert.Equal(0, ProtectionCascade.NextBranchTrip(rep)!.LocationId);
        ProtectionReport rev = N277Report(N277V("49", 0, 0.1, null), N277V("50", 1, 0.9, 0.0));
        Assert.Equal(0, ProtectionCascade.NextBranchTrip(rev)!.LocationId);
    }

    [Fact]
    public void N277_with_distinct_times_the_pick_is_exactly_the_old_one()
    {
        // The part of the rule N277 does not touch, proved over many seeded random reports: whenever the fastest
        // branch-tripping time is held by ONE row, the new pick is the very same verdict object as the old rule's.
        var rng = new Random(277);
        string[] devices = { "49", "50", "51", "21-Z1", "21-Z2", "21-Z3", "27", "37", "32" };
        int compared = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            int n = rng.Next(1, 12);
            var rows = new RelayVerdict[n];
            for (int i = 0; i < n; i++)
            {
                string dev = devices[rng.Next(devices.Length)];
                double? t = rng.Next(5) == 0 ? null : rng.NextDouble() * 10.0;
                rows[i] = N277V(dev, i, rng.NextDouble() - 0.2, t, operate: rng.Next(4) != 0);
            }
            ProtectionReport rep = N277Report(rows);
            RelayVerdict? oldPick = N277OldRule(rep);
            RelayVerdict? newPick = ProtectionCascade.NextBranchTrip(rep);
            if (oldPick is null) { Assert.Null(newPick); continue; }
            double best = oldPick.TimeToTripS ?? 0.0;
            int holders = rep.Operating.Count(v => v.LocationKind == ProtectionLocation.Branch
                && (v.Device is "49" or "50" or "51" || v.Device.StartsWith("21", StringComparison.Ordinal))
                && (v.TimeToTripS ?? 0.0).Equals(best));
            if (holders != 1) continue;   // a genuine tie: the rule N277 changes, covered by the tests above
            Assert.Same(oldPick, newPick);
            compared++;
        }
        Assert.True(compared > 1000, $"the oracle comparison must cover most trials, covered {compared}");
    }

    [Fact]
    public void N277_case30_oc_1_cascade_still_opens_6_8_then_8_28()
    {
        // The documented cascade (manual ch. 12, docs/_captures/protection-user/pc-case30-cascade.txt):
        // `protection-check case30 --cascade --oc 1.0` opens 6-8 (the only operating 50 on the base state), then 8-28,
        // then the re-solve fails. PREDICTION, to confirm at the batch: at step 2 the candidates are instantaneous 50s;
        // 8-28 is branch row 40 of case30 and 6-28 row 41, so if both operate the row index also takes 8-28, as the
        // margin rule did. If a branch with a lower row also operates at step 2, this test fails and the capture moves.
        GridModel m = IeeeCases.Case30();
        PowerFlowResult pre = SolverSelector.CreateNewton(m).Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
        Assert.True(pre.Converged);
        var settings = new ProtectionSettings() with { InstantaneousOcMultiple = 1.0 };
        CascadeResult cas = ProtectionCascade.Simulate(m, pre, settings);
        Assert.Equal(2, cas.Steps.Count);
        Assert.Equal(("50", 6, 8), (cas.Steps[0].Device, cas.Steps[0].From, cas.Steps[0].To));
        Assert.Equal(("50", 8, 28), (cas.Steps[1].Device, cas.Steps[1].From, cas.Steps[1].To));
        Assert.False(cas.FinalConverged);
    }
}
