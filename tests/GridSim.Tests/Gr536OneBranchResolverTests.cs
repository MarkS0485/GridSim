using System.Numerics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-536: the WLS family and the linear PMU estimator resolved a branch measurement by rules of their own. Both now use
/// MeasurementSet.TryResolveBranch: exact name among the same-endpoint candidates first, then a plain decimal "#k"
/// ordinal (NumberStyles.None: no sign, no spaces), a "#k" that does not fit is refused (never re-read as a name), and
/// two candidates with one name are refused. The PMU estimator passes inServiceOnly (an open branch carries no current);
/// the WLS family resolves out-of-service branches, as its measurement model stamps them with zero admittance.
/// </summary>
public class Gr536OneBranchResolverTests
{
    // case9 plus three twins of branch 0: "P" and "P" (one name twice) and "Q" (out of service).
    private static (GridModel Model, Branch B0, int P1, int P2, int Q) Twins()
    {
        GridModel c9 = IeeeCases.Case9();
        Branch b0 = c9.Branches[0];
        int n = c9.Branches.Count;
        var list = c9.Branches.ToList();
        list.Add(b0 with { Name = "P" });
        list.Add(b0 with { Name = "P" });
        list.Add(b0 with { Name = "Q", InService = false });
        return (c9.CopyWith(branches: list), b0, n, n + 1, n + 2);
    }

    private static (bool Ok, int Index, string? Error) Resolve(GridModel m, Branch b, string? circuit, bool inServiceOnly)
    {
        bool ok = MeasurementSet.TryResolveBranch(m, b.From, b.To, circuit, inServiceOnly, out int k, out string? error);
        return (ok, k, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr536_an_exact_name_shared_by_two_candidates_is_refused(bool inServiceOnly)
    {
        (GridModel m, Branch b0, _, _, _) = Twins();
        (bool ok, _, string? error) = Resolve(m, b0, "P", inServiceOnly);
        Assert.False(ok);
        Assert.Contains("are named 'P'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr536_an_out_of_service_branch_resolves_for_wls_and_is_refused_for_the_pmu_estimator()
    {
        (GridModel m, Branch b0, _, _, int q) = Twins();
        Assert.Equal((true, q, (string?)null), Resolve(m, b0, "Q", inServiceOnly: false));
        Assert.Equal((true, q, (string?)null), Resolve(m, b0, $"#{q}", inServiceOnly: false));
        Assert.False(Resolve(m, b0, "Q", inServiceOnly: true).Ok);
        (bool ok, _, string? error) = Resolve(m, b0, $"#{q}", inServiceOnly: true);
        Assert.False(ok);
        Assert.Contains("out of service", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr536_a_plain_ordinal_resolves_and_a_signed_spaced_or_mismatched_one_is_refused(bool inServiceOnly)
    {
        (GridModel m, Branch b0, int p1, _, _) = Twins();
        Assert.Equal((true, 0, (string?)null), Resolve(m, b0, "#0", inServiceOnly));
        Assert.Equal((true, p1, (string?)null), Resolve(m, b0, $"#{p1}", inServiceOnly));
        foreach (string bad in new[] { "#+0", "#-0", "# 0", "#0 ", "#1", "#999", "#" })
            Assert.False(Resolve(m, b0, bad, inServiceOnly).Ok, $"'{bad}' must be refused");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr536_an_exact_name_is_tried_before_the_ordinal(bool inServiceOnly)
    {
        GridModel c9 = IeeeCases.Case9();
        Branch b0 = c9.Branches[0];
        var list = c9.Branches.ToList();
        list.Add(b0 with { Name = "#0" });   // a branch literally named "#0", next to branch 0 itself
        GridModel m = c9.CopyWith(branches: list);
        Assert.Equal((true, list.Count - 1, (string?)null), Resolve(m, b0, "#0", inServiceOnly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gr536_no_circuit_on_parallel_candidates_is_refused(bool inServiceOnly)
    {
        (GridModel m, Branch b0, _, _, _) = Twins();
        (bool ok, _, string? error) = Resolve(m, b0, null, inServiceOnly);
        Assert.False(ok);
        Assert.Contains("parallel", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr536_the_linear_pmu_estimator_refuses_a_shared_name_it_used_to_take_as_the_first_branch()
    {
        (GridModel m, Branch b0, _, _, _) = Twins();
        var voltages = m.Buses.Select(b => new PmuVoltageMeasurement(b.Id, Complex.One, 0.001)).ToList();
        var currents = new[] { new PmuCurrentMeasurement(b0.From, b0.To, "P", Complex.Zero, 0.01) };
        var ex = Assert.Throws<InvalidDataException>(() => LinearPmuStateEstimator.Estimate(m, voltages, currents));
        Assert.Contains("are named 'P'", ex.Message, StringComparison.Ordinal);
    }
}
