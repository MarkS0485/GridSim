using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Shared helpers for the S10 engine-gap tests (F15-F18, F24, October 2026): the bundled-case catalogue and an
/// exact, bit-for-bit comparison of two power-flow results. "Exact" means the same IEEE-754 bit pattern for every
/// number (so -0.0 and 0.0 differ, and NaN equals NaN only bit for bit), never a tolerance.
/// </summary>
internal static class S10TestSupport
{
    /// <summary>The data folder copied next to the test binaries.</summary>
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Every bundled MATPOWER file under data/matpower, as paths relative to it (forward slashes), sorted.
    /// Fails (never skips) when the folder is missing or empty.</summary>
    public static IReadOnlyList<string> BundledMatpowerFiles()
    {
        string root = Path.Combine(DataDir, "matpower");
        Assert.True(Directory.Exists(root), $"bundled MATPOWER folder missing: {root}");
        List<string> files = Directory.EnumerateFiles(root, "*.m", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.True(files.Count >= 20, $"expected the bundled MATPOWER library (25 files at 1.95.112), found {files.Count}");
        return files;
    }

    /// <summary>xUnit member data: every bundled MATPOWER file.</summary>
    public static IEnumerable<object[]> BundledMatpowerData() => BundledMatpowerFiles().Select(f => new object[] { f });

    /// <summary>xUnit member data: every bundled MATPOWER file except the large ones (CaseSizes.LargeMatpowerFiles). A
    /// theory using it carries each large file as its own RAM-gated row (LargeCaseInlineData), so it still covers every
    /// file and a machine below the cap reports those rows as skipped.</summary>
    public static IEnumerable<object[]> BundledMatpowerDataBelowGate() =>
        BundledMatpowerFiles().Where(f => !CaseSizes.LargeMatpowerFiles.ContainsKey(f)).Select(f => new object[] { f });

    /// <summary>Read a bundled MATPOWER file's text.</summary>
    public static string MatpowerText(string rel)
    {
        string path = Path.Combine(DataDir, "matpower", rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"bundled case file missing: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Parse a bundled MATPOWER file exactly as the CLI does (parse, then the raw-unit repair).</summary>
    public static MatpowerParseResult LoadMatpower(string rel)
    {
        MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(MatpowerText(rel), Path.GetFileNameWithoutExtension(rel));
        return r with { Model = MatpowerUnitNormalizer.Normalize(r.Model) };
    }

    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void Bits(string label, string what, double[] a, double[] b)
    {
        Assert.True(a.Length == b.Length, $"{label}: {what} length {a.Length} vs {b.Length}");
        for (int i = 0; i < a.Length; i++)
            Assert.True(Same(a[i], b[i]), $"{label}: {what}[{i}] {a[i]:R} vs {b[i]:R}");
    }

    /// <summary>
    /// Assert two power-flow results are bit-identical: verdict, status, iterations, mismatch, bus ids, voltages,
    /// angles, injections, losses and every line flow field.
    /// </summary>
    public static void AssertBitIdentical(string label, PowerFlowResult a, PowerFlowResult b)
    {
        Assert.True(a.Converged == b.Converged, $"{label}: converged {a.Converged} vs {b.Converged}");
        Assert.True(a.Status == b.Status, $"{label}: status {a.Status} vs {b.Status}");
        Assert.True(a.Iterations == b.Iterations, $"{label}: iterations {a.Iterations} vs {b.Iterations}");
        Assert.True(Same(a.MaxMismatch, b.MaxMismatch), $"{label}: max mismatch {a.MaxMismatch:R} vs {b.MaxMismatch:R}");
        Assert.Equal(a.BusIds, b.BusIds);
        Bits(label, "Vm", a.Vm, b.Vm);
        Bits(label, "VaDeg", a.VaDeg, b.VaDeg);
        Bits(label, "PInjMw", a.PInjMw, b.PInjMw);
        Bits(label, "QInjMvar", a.QInjMvar, b.QInjMvar);
        Assert.True(Same(a.TotalLossMw, b.TotalLossMw), $"{label}: loss MW {a.TotalLossMw:R} vs {b.TotalLossMw:R}");
        Assert.True(Same(a.TotalLossMvar, b.TotalLossMvar), $"{label}: loss MVAr {a.TotalLossMvar:R} vs {b.TotalLossMvar:R}");
        Assert.True(a.LineFlows.Count == b.LineFlows.Count, $"{label}: {a.LineFlows.Count} vs {b.LineFlows.Count} line flows");
        for (int i = 0; i < a.LineFlows.Count; i++)
        {
            LineFlow x = a.LineFlows[i], y = b.LineFlows[i];
            Assert.True(x.From == y.From && x.To == y.To, $"{label}: flow {i} ends {x.From}-{x.To} vs {y.From}-{y.To}");
            Bits(label, $"flow {i}",
                new[] { x.PFromMw, x.QFromMvar, x.PToMw, x.QToMvar, x.PLossMw, x.QLossMvar, x.RatingMva, x.LoadingPercent },
                new[] { y.PFromMw, y.QFromMvar, y.PToMw, y.QToMvar, y.PLossMw, y.QLossMvar, y.RatingMva, y.LoadingPercent });
        }
    }

    /// <summary>
    /// Solve <paramref name="a"/> and <paramref name="b"/> with BOTH Newton engines (dense and sparse) and assert each
    /// pair is bit-identical. The dense engine is skipped above <paramref name="denseBusLimit"/> buses (it stores
    /// n x n matrices; case9241pegase does not fit, as DenseSparseParityTests notes).
    /// </summary>
    public static void AssertSolvesIdentical(string label, GridModel a, GridModel b, PowerFlowOptions? opt = null, int denseBusLimit = 3000)
    {
        opt ??= new PowerFlowOptions();
        // P0 (October 2026, suite runtime): the two solves of each pair (a and b, each on its own new solver
        // instance) now run side by side on two threads instead of one after the other; the same four solves are made
        // and the same bit-for-bit comparisons follow. A solve reads only its own model and options and its own
        // solver's scratch, and every engine is deterministic regardless of thread, so the results are the ones the
        // serial calls gave. The dense pair on a 2,000-3,000-bus case (case2869pegase: tens of seconds per solve) was
        // the longest item of ConvertAndValidateTests, whose tests run one after another and so ended the suite.
        // The solvers only read the model and the options (GridModel has no lazy caches; PowerFlowOptions is init-only,
        // and none of the callers sets a Trace), so even a call with a and b the same model (gb-reduced-29, run to
        // run) reads it from two threads safely, as the parallel contingency screens read their base model.
        (PowerFlowResult sa, PowerFlowResult sb) = Both(() => new SparseNewtonRaphsonPowerFlow().Solve(a, opt), () => new SparseNewtonRaphsonPowerFlow().Solve(b, opt));
        AssertBitIdentical(label + " [sparse]", sa, sb);
        if (a.BusCount <= denseBusLimit)
        {
            // A large dense solve waits for a BigDenseSolveGate slot (memory, not numbers).
            (PowerFlowResult da, PowerFlowResult db) = Both(
                () => BigDenseSolveGate.Run(a.BusCount, () => new NewtonRaphsonPowerFlow().Solve(a, opt)),
                () => BigDenseSolveGate.Run(b.BusCount, () => new NewtonRaphsonPowerFlow().Solve(b, opt)));
            AssertBitIdentical(label + " [dense]", da, db);
        }
    }

    /// <summary>Run two independent solves at once and return both results. Parallel.Invoke returns only when both
    /// have finished and rethrows any exception (in an AggregateException), so a failing solve still fails loud.</summary>
    private static (PowerFlowResult A, PowerFlowResult B) Both(Func<PowerFlowResult> first, Func<PowerFlowResult> second)
    {
        PowerFlowResult a = null!, b = null!;
        try { Parallel.Invoke(() => a = first(), () => b = second()); }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            // One solve threw: rethrow that exception itself, with its own stack, as the serial call would have.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
        return (a, b);
    }
}
