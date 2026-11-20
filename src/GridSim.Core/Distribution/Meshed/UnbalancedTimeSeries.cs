using System.Numerics;

namespace GridSim.Core.Distribution.Meshed;

/// <summary>One step of an unbalanced time series: a label, a global load factor and optional per-load factors.</summary>
public sealed record MeshedStep(string Label, double GlobalScale = 1.0, IReadOnlyDictionary<string, double>? LoadScale = null);

/// <summary>The solved steps of a series, in order.</summary>
public sealed record MeshedSeriesResult(IReadOnlyList<MeshedStep> Steps, IReadOnlyList<MeshedResult> Results, bool WarmStart);

/// <summary>
/// RC4 R4-15: a quasi-static time series of meshed unbalanced load flows. By default every step is independent (it starts from
/// the no-load solution), so any step's result is reproducible alone and bit-identical to a single solve with the same load
/// factors. Only when the request says <c>warmStart</c> does a step start from the previous step's solution (when that step
/// converged); the converged answer then agrees within the tolerance but is not claimed bit-identical to the independent one.
/// </summary>
public static class UnbalancedTimeSeries
{
    /// <summary>Solve every step in order.</summary>
    public static MeshedSeriesResult Run(PhaseFrameModel model, IReadOnlyList<MeshedStep> steps, bool warmStart = false, MeshedOptions? options = null)
    {
        MeshedOptions baseOpt = options ?? new MeshedOptions();
        var results = new List<MeshedResult>(steps.Count);
        MeshedResult? prev = null;
        foreach (MeshedStep s in steps)
        {
            MeshedOptions o = baseOpt with
            {
                GlobalLoadScale = baseOpt.GlobalLoadScale * s.GlobalScale,
                LoadScale = s.LoadScale ?? baseOpt.LoadScale,
                InitialState = warmStart && prev is { Status: MeshedStatus.Converged } ? prev.State : baseOpt.InitialState,
            };
            MeshedResult r = Newton3Ph.Solve(model, o);
            results.Add(r);
            prev = r;
        }
        return new MeshedSeriesResult(steps, results, warmStart);
    }
}
