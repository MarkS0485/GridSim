using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), contingency findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    // ---------------------------------------------------------------------------------------------------------------
    // BH69: contingency violation texts print numbers in the invariant culture.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH69_contingency_violation_texts_print_a_decimal_point_under_a_comma_decimal_culture()
    {
        // IEEE case 9 with every load raised by 60 %: the base case solves with buses below the operational band, so
        // the result carries an "undervoltage 0.xxx p.u." text.
        GridModel c9 = IeeeCases.Case9();
        GridModel heavy = c9.CopyWith(buses: c9.Buses.Select(b => b with { Pd = b.Pd * 1.6, Qd = b.Qd * 1.6 }).ToList());
        ContingencyResult r = BHUnderGermanCulture(() => ContingencyAnalysis.ScreenBaseCase(heavy));
        Assert.True(r.Converged);
        string under = Assert.Single(r.Violations, v => v.StartsWith("undervoltage", StringComparison.Ordinal));
        Assert.Matches(@"^undervoltage 0\.\d{3} p\.u\.$", under);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH70: a NaN branch rating is refused by the contingency screen instead of reading as unrated.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH70_a_nan_branch_rating_is_refused_by_name_instead_of_reading_as_unrated()
    {
        GridModel c9 = IeeeCases.Case9();
        var branches = c9.Branches.Select((b, i) => i == 3 ? b with { RateAMva = double.NaN } : b).ToList();
        GridModel bad = c9.CopyWith(branches: branches);
        var ex = Assert.Throws<InvalidDataException>(() => ContingencyAnalysis.ScreenBaseCase(bad));
        Assert.Contains("a rating must be 0 (unrated) or a number above 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NaN", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH71: an N-k screen that could form no combination says why instead of returning a silent empty report.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH71_an_nk_screen_deeper_than_the_branch_count_says_why_it_is_empty()
    {
        GridModel c9 = IeeeCases.Case9();
        NkScreenReport r = ContingencyAnalysis.ScreenNMinusKReport(c9, c9.Branches.Count + 1);
        Assert.Empty(r.Results);
        Assert.NotNull(r.Note);
        Assert.Contains("nothing was screened", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void BH71_too_few_solvable_single_outages_give_a_note_and_enough_give_none()
    {
        Assert.Null(NkScreenReport.TooFewCandidates(candidates: 2, excluded: 5, k: 2));
        string? note = NkScreenReport.TooFewCandidates(candidates: 1, excluded: 8, k: 2);
        Assert.Equal("only 1 single outage(s) solve (8 do not), fewer than the depth k = 2, so no 2-outage combination could be formed; nothing was screened.", note);
    }
}
