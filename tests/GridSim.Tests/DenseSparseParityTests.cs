using System.Text;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Job number 1 of the October 2026 fix programme (Mark's ruling 2): the DENSE and SPARSE Newton-Raphson
/// engines must give BIT-IDENTICAL results. Not "close", not "within 1e-12": the same IEEE-754 bit patterns
/// for every voltage, angle, injection and line flow, the same iteration count, the same convergence verdict
/// and the same diagnostics. GridSim picks an engine by network size (SolverSelector), so any difference
/// between the two means the answer to a study depends on which code path happened to run.
///
/// How parity is achieved (see NewtonShell): the two engines keep their own admittance, injection and
/// Jacobian assembly (dense arrays vs CSR walks - this test is what proves those two assemblies agree to
/// the bit), but every Newton step is solved by the ONE canonical CPU sparse LU and applied by the ONE
/// shared step-update rule. So the only code that differs between the engines is assembly, and assembly is
/// exact: the same products and sums in the same order.
///
/// Coverage: every bundled case, with and without generator Q limits, and from both the case-file start and a flat
/// start. A missing data file FAILS (it never silently skips). D-DOC-211 (October 2026): the lists used to leave out
/// case9241pegase (on memory grounds), the two GB MATPOWER files GBnetwork.m and GBreducednetwork.m and the four intl
/// folders; they are now all in (GBnetwork.m, GBreducednetwork.m and the intl folders in the lists below, case9241pegase
/// in its own test, one option set, holding the whole BigDenseSolveGate so no other large dense solve runs beside its
/// several-GB dense matrices). dist3ph is not here because it has no dense / sparse engine pair: its feeders are solved
/// by the three-phase sweep and estimator only (see Doc16NumericsDeterminismTests).
/// </summary>
public class DenseSparseParityTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    // ---- Case catalogue ------------------------------------------------------------------------

    /// <summary>The built-in IEEE / benchmark systems.</summary>
    public static IEnumerable<object[]> BuiltInCases =>
        new[] { "9", "14", "30", "57", "118", "300", "24", "145" }.Select(n => new object[] { n });

    /// <summary>Every bundled MATPOWER file small enough for the dense engine (n x n storage).</summary>
    public static IEnumerable<object[]> MatpowerFiles => new[]
    {
        "lib/case9.m", "lib/case14.m", "lib/case30.m", "lib/case57.m", "lib/case118.m", "lib/case300.m",
        "lib/case24_ieee_rts.m", "lib/case145.m",
        "dist/case18.m", "dist/case22.m", "dist/case33bw.m", "dist/case69.m", "dist/case85.m",
        "dist/case533mt_hi.m",
        "benchmarks/cigre_mv.m",
        "nesta/pglib_opf_case3_lmbd.m", "nesta/pglib_opf_case5_pjm.m", "nesta/pglib_opf_case30_as.m",
        "nesta/pglib_opf_case162_ieee_dtc.m", "nesta/pglib_opf_case240_pserc.m",
        "nesta/pglib_opf_case588_sdet.m",
        "rts/pglib_opf_case73_ieee_rts.m",
        "pegase/case1354pegase.m", "pegase/case2869pegase.m",
        // D-DOC-211: the two GB MATPOWER files.
        "gb/GBreducednetwork.m", "gb/GBnetwork.m",
    }.Select(f => new object[] { f });

    /// <summary>Bundled JSON networks (GB synthetic family and the UK model; D-DOC-211: the four intl folders).
    /// continental-europe is the theory's own RAM-gated row (MachineCapacity), not in this list.</summary>
    public static IEnumerable<object[]> JsonCases =>
        new[] { "uk", "gb-synthetic", "gb-full",
                "intl/ireland-all-island", "intl/iberia", "intl/france" }.Select(d => new object[] { d });

    private static GridModel BuiltIn(string name) => name switch
    {
        "9" => IeeeCases.Case9(),
        "14" => IeeeCases.Case14(),
        "30" => IeeeCases.Case30(),
        "57" => IeeeCases.Case57(),
        "118" => IeeeCases.Case118(),
        "300" => IeeeCases.Case300(),
        "24" => IeeeCases.Case24Rts(),
        "145" => IeeeCases.Case145(),
        _ => throw new ArgumentException($"unknown case {name}", nameof(name)),
    };

    private static GridModel Matpower(string rel)
    {
        string path = Path.Combine(DataDir, "matpower", rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled case file missing: {path}");
        return MatpowerParser.ParseFile(path);
    }

    private static GridModel Json(string dir)
    {
        string path = Path.Combine(DataDir, dir);
        Assert.True(Directory.Exists(path), $"bundled case directory missing: {path}");
        return JsonGridLoader.LoadDirectory(path);
    }

    /// <summary>The four option sets every case is solved under.</summary>
    private static IEnumerable<(string Label, PowerFlowOptions Options)> OptionSets()
    {
        yield return ("Q limits on", new PowerFlowOptions { EnforceQLimits = true });
        yield return ("Q limits off", new PowerFlowOptions { EnforceQLimits = false });
        yield return ("flat start, Q limits on", new PowerFlowOptions { EnforceQLimits = true, FlatStart = true });
        yield return ("Q limits on with revert", new PowerFlowOptions { EnforceQLimits = true, RevertQLimits = true });
    }

    // ---- Tests ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BuiltInCases))]
    public void Built_in_case_is_bit_identical_dense_vs_sparse(string name)
    {
        foreach ((string label, PowerFlowOptions opt) in OptionSets())
        {
            string outcome = AssertBitIdentical($"case{name} [{label}]", BuiltIn(name), opt);
            // The IEEE built-ins are well posed: parity of two failures would hide a regression here.
            Assert.True(outcome == "converged", $"case{name} [{label}]: {outcome}");
        }
    }

    [Theory]
    [MemberData(nameof(MatpowerFiles))]
    public void Matpower_file_is_bit_identical_dense_vs_sparse(string rel)
    {
        EachOptionSetTwoAtATime(OptionSets(), (label, opt) => AssertBitIdentical($"{rel} [{label}]", Matpower(rel), opt));
    }

    /// <summary>
    /// P0 (October 2026, suite runtime): run <paramref name="check"/> once per option set, two option sets at a time,
    /// instead of one after the other. Each check loads its own copy of the case and makes its own dense and sparse
    /// engines (exactly as the serial loop did), so the solves and the bit-for-bit comparisons are the same; only
    /// their overlap in time changed. case2869pegase's four dense solves (about 100 s in a row) made this class the
    /// last one running in some suite runs. Two at a time, not four, because a dense case2869 solve holds n x n
    /// matrices of several hundred MB and the laptop the suite runs on has 8 GB. On failure the failure of the
    /// earliest option set (in the order listed) is rethrown as itself, so the report reads as the serial loop's did.
    /// </summary>
    private static void EachOptionSetTwoAtATime(IEnumerable<(string Label, PowerFlowOptions Options)> sets, Action<string, PowerFlowOptions> check)
    {
        var list = sets.ToList();
        var failures = new Exception?[list.Count];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        {
            try { check(list[i].Label, list[i].Options); }
            catch (Exception ex) { failures[i] = ex; }
        });
        Exception? first = failures.FirstOrDefault(f => f is not null);
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    [Theory]
    [MemberData(nameof(JsonCases))]
    [LargeCaseInlineData(CaseSizes.ContinentalEurope, Engine.Dense, "intl/continental-europe")]
    public void Json_case_is_bit_identical_dense_vs_sparse(string dir)
    {
        // gb-full is the largest case the dense engine can hold; one option set keeps its run time sane. D-DOC-211: the
        // same for continental-europe (5,824 buses) and france (1,976 buses), the two large intl folders.
        IEnumerable<(string, PowerFlowOptions)> sets = dir is "gb-full" or "intl/continental-europe" or "intl/france"
            ? OptionSets().Take(1) : OptionSets();
        EachOptionSetTwoAtATime(sets, (label, opt) => AssertBitIdentical($"{dir} [{label}]", Json(dir), opt));
    }

    /// <summary>
    /// D-DOC-211 (October 2026): case9241pegase, the one bundled case the lists left out on memory grounds. The dense
    /// engine holds the complex admittance and its real and imaginary parts as 9,241 x 9,241 arrays (about 2.7 GB in
    /// all), so this runs ONE option set (Q limits on), and only while it holds every BigDenseSolveGate slot, so no other
    /// large dense solve of the suite runs beside it. The comparison is the same exact, bit-for-bit one as every other
    /// case here. RAM-gated (MachineCapacity): skipped, with the reason, below 16 GB.
    /// </summary>
    [LargeCaseFact(CaseSizes.Pegase9241, Engine.Dense)]
    public void Case9241pegase_is_bit_identical_dense_vs_sparse()
    {
        const string rel = "pegase/case9241pegase.m";
        (string Label, PowerFlowOptions Options) set = OptionSets().First();
        BigDenseSolveGate.RunExclusive(() => AssertBitIdentical($"{rel} [{set.Label}]", Matpower(rel), set.Options));
    }

    [Fact]
    public void Repeated_solves_on_one_sparse_instance_stay_bit_identical()
    {
        // The sparse LU caches its RCM ordering across calls on the same instance. A cached ordering must
        // give the same bits as a fresh one, solve after solve, including after a structure change (Q-limit
        // switching rebuilds the Jacobian pattern between passes).
        var sparse = new SparseNewtonRaphsonPowerFlow();
        var dense = new NewtonRaphsonPowerFlow();
        foreach (string name in new[] { "300", "118", "300" })
        {
            GridModel m = BuiltIn(name);
            var opt = new PowerFlowOptions { EnforceQLimits = true };
            AssertResultsBitIdentical($"case{name} (reused instances)", dense.Solve(m, opt), sparse.Solve(m, opt));
        }
    }

    [Fact]
    public void A_non_finite_input_stops_both_engines_at_once_with_the_same_failure()
    {
        // R127: the dense engine used to keep iterating on a NaN mismatch for all MaxIterations (NaN < tol is
        // false, and nothing checked the step). The shared step now refuses a non-finite correction, so both
        // engines stop on the first iteration, report failure (never "converged") and agree to the bit.
        GridModel raw = BuiltIn("14");
        var m = new GridModel
        {
            Name = raw.Name, BaseMva = raw.BaseMva, FrequencyHz = raw.FrequencyHz,
            Generators = raw.Generators, Branches = raw.Branches,
            Buses = raw.Buses.Select((bus, i) => i == 3 ? bus with { Pd = double.NaN } : bus).ToList(),
        };
        var opt = new PowerFlowOptions { EnforceQLimits = false, MaxIterations = 30 };
        PowerFlowResult d = new NewtonRaphsonPowerFlow().Solve(m, opt);
        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m, opt);

        Assert.False(d.Converged);
        Assert.Equal(PowerFlowStatus.NonFinite, d.Status);
        Assert.True(d.Iterations <= 1, $"dense ran {d.Iterations} iterations on a NaN mismatch");
        AssertResultsBitIdentical("case14 with a NaN load", d, s);
    }

    // ---- Comparison ----------------------------------------------------------------------------

    /// <summary>Solve with both engines on separate copies of the inputs and require bit identity of the
    /// whole result. Either both engines throw the same exception or both return. Returns a one-word
    /// outcome so callers can additionally require convergence where the case is known to be well posed.</summary>
    private static string AssertBitIdentical(string label, GridModel model, PowerFlowOptions opt)
    {
        var dense = new NewtonRaphsonPowerFlow();
        var sparse = new SparseNewtonRaphsonPowerFlow();

        // A large dense solve waits for a BigDenseSolveGate slot (memory only; the solve itself is unchanged).
        (PowerFlowResult? d, Exception? de) = Run(() => BigDenseSolveGate.Run(model.BusCount, () => dense.Solve(model, opt)));
        (PowerFlowResult? s, Exception? se) = Run(() => sparse.Solve(model, opt));

        if (de is not null || se is not null)
        {
            Assert.True(de is not null && se is not null,
                $"{label}: only one engine threw. dense: {de?.GetType().Name} {de?.Message}; sparse: {se?.GetType().Name} {se?.Message}");
            Assert.Equal(de!.GetType(), se!.GetType());
            Assert.Equal(de.Message, se.Message);
            return "both threw " + de.GetType().Name;
        }

        AssertResultsBitIdentical(label, d!, s!);
        Assert.Equal(dense.SlackQNote, sparse.SlackQNote);
        return d!.Converged ? "converged" : $"not converged ({d.Status}, mismatch {d.MaxMismatch:E3})";
    }

    private static (PowerFlowResult?, Exception?) Run(Func<PowerFlowResult> solve)
    {
        try { return (solve(), null); }
        catch (Exception ex) { return (null, ex); }
    }

    private static void AssertResultsBitIdentical(string label, PowerFlowResult d, PowerFlowResult s)
    {
        Assert.Equal(d.Converged, s.Converged);
        Assert.Equal(d.Status, s.Status);
        Assert.True(d.Iterations == s.Iterations, $"{label}: iterations dense {d.Iterations} vs sparse {s.Iterations}");
        AssertBits(label, "MaxMismatch", d.MaxMismatch, s.MaxMismatch);
        Assert.Equal(d.BusIds, s.BusIds);
        AssertBits(label, "Vm", d.Vm, s.Vm);
        AssertBits(label, "VaDeg", d.VaDeg, s.VaDeg);
        AssertBits(label, "PInjMw", d.PInjMw, s.PInjMw);
        AssertBits(label, "QInjMvar", d.QInjMvar, s.QInjMvar);
        AssertBits(label, "TotalLossMw", d.TotalLossMw, s.TotalLossMw);
        AssertBits(label, "TotalLossMvar", d.TotalLossMvar, s.TotalLossMvar);
        // GR-889: the bus types the solve ended with (a PV bus switched to PQ at a reactive limit) were not compared.
        // Both engines run the same NewtonShell, so on bit-identical states they must agree exactly.
        Assert.Equal(d.SolvedBusTypes is null, s.SolvedBusTypes is null);
        if (d.SolvedBusTypes is not null) Assert.Equal(d.SolvedBusTypes, s.SolvedBusTypes!);

        Assert.Equal(d.LineFlows.Count, s.LineFlows.Count);
        for (int k = 0; k < d.LineFlows.Count; k++)
        {
            LineFlow a = d.LineFlows[k], b = s.LineFlows[k];
            string at = $"{label} branch {k}";
            Assert.Equal(a.From, b.From);
            Assert.Equal(a.To, b.To);
            AssertBits(at, "PFromMw", a.PFromMw, b.PFromMw);
            AssertBits(at, "QFromMvar", a.QFromMvar, b.QFromMvar);
            AssertBits(at, "PToMw", a.PToMw, b.PToMw);
            AssertBits(at, "QToMvar", a.QToMvar, b.QToMvar);
            AssertBits(at, "LoadingPercent", a.LoadingPercent, b.LoadingPercent);
            // GR-889: the per-branch losses, rating and name were not compared; they are part of the result too.
            AssertBits(at, "PLossMw", a.PLossMw, b.PLossMw);
            AssertBits(at, "QLossMvar", a.QLossMvar, b.QLossMvar);
            AssertBits(at, "RatingMva", a.RatingMva, b.RatingMva);
            Assert.Equal(a.Name, b.Name);
        }
    }

    private static void AssertBits(string label, string field, double dense, double sparse)
    {
        if (BitConverter.DoubleToInt64Bits(dense) != BitConverter.DoubleToInt64Bits(sparse))
            Assert.Fail($"{label}: {field} dense {dense:R} vs sparse {sparse:R} (diff {dense - sparse:E3})");
    }

    private static void AssertBits(string label, string field, IReadOnlyList<double> dense, IReadOnlyList<double> sparse)
    {
        Assert.Equal(dense.Count, sparse.Count);
        var sb = new StringBuilder();
        int bad = 0;
        double worst = 0;
        for (int i = 0; i < dense.Count; i++)
        {
            if (BitConverter.DoubleToInt64Bits(dense[i]) == BitConverter.DoubleToInt64Bits(sparse[i])) continue;
            bad++;
            worst = Math.Max(worst, Math.Abs(dense[i] - sparse[i]));
            if (bad <= 3) sb.Append($" [{i}] {dense[i]:R} vs {sparse[i]:R};");
        }
        if (bad > 0)
            Assert.Fail($"{label}: {field} differs at {bad}/{dense.Count} entries (worst |diff| {worst:E3}):{sb}");
    }
}
