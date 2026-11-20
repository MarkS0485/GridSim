using System.Text;
using GridSim.Core.Analysis.StudyCases;
using Xunit;

namespace GridSim.Tests;

/// <summary>RC4 R4-01 W3: case diff and result diff. Oracles are the edits themselves (a known one-field change, a known
/// permutation) and, for the ULP distance, counting representable doubles with <see cref="Math.BitIncrement"/>.</summary>
public sealed class R401DiffTests
{
    private static byte[] B(string text) => new UTF8Encoding(false).GetBytes(text);

    private static Dictionary<string, byte[]> Case(string lines, string buses = "[ { \"id\": 1, \"baseKv\": 230 }, { \"id\": 2, \"baseKv\": 230 } ]") => new()
    {
        ["buses.json"] = B(buses),
        ["transmission.json"] = B(lines),
    };

    private const string L1 = "{ \"name\": \"L1\", \"from\": 1, \"to\": 2, \"rPu\": 0.01, \"xPu\": 0.1, \"ratingMva\": 100 }";
    private const string L2 = "{ \"name\": \"L2\", \"from\": 1, \"to\": 2, \"rPu\": 0.01, \"xPu\": 0.2, \"ratingMva\": 100 }";
    private const string Lines = "[ " + L1 + ", " + L2 + " ]";

    [Fact]
    public void A_case_against_itself_is_empty_and_bit_equal()
    {
        CaseDiffResult r = CaseDiff.Compare(Case(Lines), Case(Lines));
        Assert.True(r.Empty);
        Assert.True(r.BytesIdentical);
    }

    [Fact]
    public void One_changed_field_reports_exactly_one_entry_with_old_new_and_unit()
    {
        string changed = Lines.Replace("\"xPu\": 0.2", "\"xPu\": 0.25");
        CaseDiffResult r = CaseDiff.Compare(Case(Lines), Case(changed));
        CaseDiffEntry e = Assert.Single(r.Entries);
        Assert.Equal((CaseDiffKind.Changed, "transmission.json", "transmission.json[name=L2].xPu", "0.2", "0.25", "pu"),
            (e.Kind, e.File, e.Path, e.Old, e.New, e.Unit));
        Assert.False(r.Empty);
        Assert.False(r.BytesIdentical);
    }

    [Fact]
    public void A_rating_change_carries_its_unit_from_the_schema_title()
    {
        CaseDiffEntry e = Assert.Single(CaseDiff.Compare(Case(Lines), Case(Lines.Replace("\"ratingMva\": 100 }, ", "\"ratingMva\": 120 }, "))).Entries);
        Assert.Equal(("transmission.json[name=L1].ratingMva", "100", "120"), (e.Path, e.Old, e.New));
        Assert.False(string.IsNullOrEmpty(e.Unit));
    }

    [Fact]
    public void Permuted_record_order_is_reordered_not_changed()
    {
        CaseDiffResult r = CaseDiff.Compare(Case(Lines), Case("[ " + L2 + ", " + L1 + " ]"));
        CaseDiffEntry e = Assert.Single(r.Entries);
        Assert.Equal((CaseDiffKind.Reordered, "transmission.json", "name=L1,name=L2", "name=L2,name=L1"), (e.Kind, e.Path, e.Old, e.New));
    }

    [Fact]
    public void Parallel_circuits_without_names_stay_distinct_by_endpoint_and_occurrence()
    {
        const string anonymous = "[ { \"from\": 1, \"to\": 2, \"xPu\": 0.1 }, { \"from\": 1, \"to\": 2, \"xPu\": 0.2 } ]";
        CaseDiffEntry e = Assert.Single(CaseDiff.Compare(Case(anonymous), Case(anonymous.Replace("0.2", "0.3"))).Entries);
        Assert.Equal(("transmission.json[1-2#2].xPu", "0.2", "0.3"), (e.Path, e.Old, e.New));
    }

    [Fact]
    public void Added_and_removed_records_members_and_files_are_listed()
    {
        string plus = "[ " + L1 + ", " + L2 + ", { \"name\": \"L3\", \"from\": 2, \"to\": 1, \"xPu\": 0.3 } ]";
        CaseDiffResult added = CaseDiff.Compare(Case(Lines), Case(plus));
        Assert.Equal((CaseDiffKind.Added, "transmission.json[name=L3]"), (added.Entries[0].Kind, added.Entries[0].Path));
        Assert.Single(added.Entries);
        CaseDiffResult removed = CaseDiff.Compare(Case(plus), Case(Lines));
        Assert.Equal((CaseDiffKind.Removed, "transmission.json[name=L3]"), (removed.Entries[0].Kind, removed.Entries[0].Path));

        CaseDiffResult member = CaseDiff.Compare(Case(Lines), Case(Lines.Replace("\"ratingMva\": 100 }, ", "\"ratingMva\": 100, \"bPu\": 0.02 }, ")));
        Assert.Equal((CaseDiffKind.Added, "transmission.json[name=L1].bPu", "0.02"), (member.Entries[0].Kind, member.Entries[0].Path, member.Entries[0].New));

        Dictionary<string, byte[]> extra = Case(Lines);
        extra["demand.json"] = B("[]");
        Assert.Equal((CaseDiffKind.FileAdded, "demand.json"), (CaseDiff.Compare(Case(Lines), extra).Entries[0].Kind, CaseDiff.Compare(Case(Lines), extra).Entries[0].File));
        Assert.Equal(CaseDiffKind.FileRemoved, CaseDiff.Compare(extra, Case(Lines)).Entries[0].Kind);
    }

    [Fact]
    public void Formatting_alone_and_equal_numbers_in_other_spellings_are_not_differences()
    {
        string spaced = Lines.Replace("0.1", "0.10").Replace("100", "1e2").Replace(" ", "   ");
        CaseDiffResult r = CaseDiff.Compare(Case(Lines), Case(spaced));
        Assert.True(r.Empty);
        Assert.False(r.BytesIdentical);
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        Assert.Contains("not valid JSON", Assert.Throws<StudyCaseException>(() => CaseDiff.Compare(Case("[ {"), Case(Lines))).Message);
    }

    [Fact]
    public void Unit_is_read_for_a_v2_network_member()
    {
        var a = new Dictionary<string, byte[]> { ["network/n.json"] = B("{ \"buses\": [ { \"id\": 1, \"baseKv\": 230 } ], \"lines\": [ { \"id\": \"l1\", \"from\": 1, \"to\": 2, \"xPu\": 0.1 } ] }") };
        var b = new Dictionary<string, byte[]> { ["network/n.json"] = B("{ \"buses\": [ { \"id\": 1, \"baseKv\": 230 } ], \"lines\": [ { \"id\": \"l1\", \"from\": 1, \"to\": 2, \"xPu\": 0.2 } ] }") };
        CaseDiffEntry e = Assert.Single(CaseDiff.Compare(a, b).Entries);
        Assert.Equal(("network/n.json:lines[id=l1].xPu", "0.1", "0.2"), (e.Path, e.Old, e.New));
    }

    // ---- Result diff -----------------------------------------------------------------------------------------------

    [Fact]
    public void Ulp_distance_counts_representable_doubles()
    {
        Assert.Equal(0UL, ResultDiff.Ulps(1.0, 1.0));
        Assert.Equal(1UL, ResultDiff.Ulps(1.0, Math.BitIncrement(1.0)));
        Assert.Equal(1UL, ResultDiff.Ulps(Math.BitDecrement(2.0), 2.0));        // across a binade boundary
        double x = 0.9194904559886774;
        double y = x;
        for (int i = 0; i < 37; i++) y = Math.BitIncrement(y);
        Assert.Equal(37UL, ResultDiff.Ulps(x, y));
        Assert.Equal(37UL, ResultDiff.Ulps(y, x));
        Assert.Equal(0UL, ResultDiff.Ulps(0.0, -0.0));
        Assert.Equal(2UL, ResultDiff.Ulps(double.Epsilon, -double.Epsilon));      // -eps, 0, +eps
        Assert.Equal(1UL, ResultDiff.Ulps(0.0, double.Epsilon));
        double n = -3.5, m = n;
        for (int i = 0; i < 5; i++) m = Math.BitDecrement(m);
        Assert.Equal(5UL, ResultDiff.Ulps(n, m));
    }

    private const string Doc = "{ \"status\": \"SECURE\", \"v\": [ 0.9194904559886774, 1.0, 2 ], \"n\": { \"p\": 80.0, \"q\": 30 }, \"tag\": \"a\" }";

    [Fact]
    public void Two_identical_results_are_bit_equal()
    {
        ResultDiffResult r = ResultDiff.Compare(B(Doc), B(Doc));
        Assert.True(r.Equal);
        Assert.True(r.BytesIdentical);
        Assert.Null(r.First);
    }

    [Fact]
    public void A_perturbed_run_reports_the_first_differing_quantity_with_its_ulp_distance()
    {
        double x = 0.9194904559886774;
        double y = Math.BitIncrement(Math.BitIncrement(x));
        string perturbed = Doc.Replace("0.9194904559886774", y.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("\"q\": 30", "\"q\": 31");
        ResultDiffResult r = ResultDiff.Compare(B(Doc), B(perturbed));
        Assert.Equal(2, r.Differences);
        ResultDiffEntry first = r.First!;
        Assert.Equal(("$.v[0]", ResultDiffKind.Changed, 2UL), (first.Path, first.Kind, first.UlpDistance!.Value));
        Assert.Equal(y - x, first.AbsoluteDifference!.Value);
        Assert.Equal("$.n.q", r.Entries[1].Path);
        Assert.Equal(1.0, r.Entries[1].AbsoluteDifference);
    }

    [Fact]
    public void Structure_changes_are_classified()
    {
        ResultDiffResult r = ResultDiff.Compare(B("{ \"a\": [1, 2], \"b\": 1, \"c\": \"x\", \"d\": true }"),
            B("{ \"a\": [1, 2, 3], \"c\": \"y\", \"d\": \"true\", \"e\": null }"));
        Assert.Equal([ResultDiffKind.LengthChanged, ResultDiffKind.Removed, ResultDiffKind.Changed, ResultDiffKind.TypeChanged, ResultDiffKind.Added],
            r.Entries.Select(e => e.Kind));
        Assert.Equal(["$.a", "$.b", "$.c", "$.d", "$.e"], r.Entries.Select(e => e.Path));
    }

    [Fact]
    public void Entries_are_capped_but_every_difference_is_counted()
    {
        string a = "[" + string.Join(",", Enumerable.Range(0, 10)) + "]";
        string b = "[" + string.Join(",", Enumerable.Range(1, 10)) + "]";
        ResultDiffResult r = ResultDiff.Compare(B(a), B(b), maxEntries: 3);
        Assert.Equal((10, 3, true), (r.Differences, r.Entries.Count, r.Truncated));
        Assert.Equal(10, ResultDiff.Compare(B(a), B(b), maxEntries: 0).Entries.Count);
    }

    [Fact]
    public void Equal_numbers_in_other_spellings_are_equal_in_value_but_not_byte_identical()
    {
        ResultDiffResult r = ResultDiff.Compare(B("{ \"x\": 1 }"), B("{ \"x\": 1.0 }"));
        Assert.True(r.Equal);
        Assert.False(r.BytesIdentical);
    }

    [Fact]
    public void Malformed_result_json_is_refused()
    {
        Assert.Contains("not valid JSON", Assert.Throws<StudyCaseException>(() => ResultDiff.Compare(B("{"), B("{}"))).Message);
    }
}
