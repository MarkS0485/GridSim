using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Contingency;

/// <summary>One branch the DC screen found over its limit in a state, with the rating that applied and where it came from.</summary>
public sealed record DcViolation(int Branch, string Label, double FlowMw, double RatingMva, string RatingSource, double LoadingPct);

/// <summary>One state of the DC screen. <see cref="Outage"/> is the outaged branch index, or -1 for the intact base case.
/// <see cref="Status"/> is SECURE, VIOLATIONxN or ISLANDED (an islanding outage has no LODF and no algebra is done).</summary>
public sealed record DcContingencyRow(
    int Outage, string Label, string Status, double PreOutageFlowMw, int ViolationCount,
    int WorstBranch, double WorstLoadingPct, IReadOnlyList<DcViolation> Violations)
{
    /// <summary>The state's DC branch flows (MW, model branch order), when the screen was asked to keep them; empty otherwise.
    /// The outaged branch's own entry is 0; an ISLANDED row has none.</summary>
    public IReadOnlyList<double> FlowsMw { get; init; } = Array.Empty<double>();
}

/// <summary>The DC N-1 screen: the base flows, the intact row, one row per outage in request order.</summary>
public sealed record DcContingencyResult(
    IReadOnlyList<double> BaseFlowsMw, DcContingencyRow Base, IReadOnlyList<DcContingencyRow> Rows, int UnratedBranches, int DcIslands);

/// <summary>
/// DC N-1 contingency screen (R4-03, report row 4.2). The base DC flows come from one factorisation of B'
/// (<see cref="DistributionFactors.DcFlows"/>; injections are in-service generation minus demand, each DC island's
/// reference absorbing its imbalance, as the <c>dcpf</c> verb). An outage of branch k then changes every flow by the
/// line outage distribution factor: <c>flow_after[l] = flow_before[l] + LODF[l, k] * flow_before[k]</c>, in MW. It never
/// claims AC MVA. A bridge outage (|1 - Phi_kk| below <see cref="DistributionFactors.IslandingSensitivityTolerance"/>) is
/// an ISLANDED row, never an invented LODF.
///
/// <para>Limits follow the RC3 effective-rating rule: the intact state is judged against RateA, an outage state against
/// the rating <see cref="DcScopf.PostRating"/> selects (B or C where given, else A). A branch with no RateA is not
/// monitored (counted in <see cref="DcContingencyResult.UnratedBranches"/>). A branch is over its limit when
/// <c>|flow| &gt; rating * (1 + <see cref="LimitTolerance"/>)</c>; the pre-existing violations of the intact case are
/// reported on the intact row and are NOT netted from the outage rows (absolute limits, as RC3).</para>
/// </summary>
public static class DcContingencyScreen
{
    /// <summary>Relative tolerance on a limit comparison, declared so a flow at its rating to rounding is not a violation.</summary>
    public const double LimitTolerance = 1e-9;

    /// <summary>Screen <paramref name="outages"/> (null = every in-service branch, model order); <paramref name="keepFlows"/> keeps each
    /// state's flows on its row (memory grows with outages x branches). Throws
    /// <see cref="InvalidOperationException"/> when the DC model cannot be formed.</summary>
    public static DcContingencyResult Screen(GridModel model, PostFaultRating rating = PostFaultRating.RateA, IReadOnlyList<int>? outages = null, bool keepFlows = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        int[] set = OutageModels.Resolve(model, outages);
        DistributionFactors df = DistributionFactors.Build(model);
        var injection = new Dictionary<int, double>();
        foreach (Generator g in model.Generators) injection[g.Bus] = injection.GetValueOrDefault(g.Bus) + g.Pg;
        foreach (Bus b in model.Buses) injection[b.Id] = injection.GetValueOrDefault(b.Id) - b.Pd;
        double[] flow = df.DcFlows(injection, model.BaseMva);

        int unrated = 0;
        for (int l = 0; l < model.Branches.Count; l++)
            if (model.Branches[l].InService && !(model.Branches[l].RateAMva > 0)) unrated++;

        DcContingencyRow Evaluate(int outage, string label, string status, double[] f, bool post, double preFlow)
        {
            var violations = new List<DcViolation>();
            int worst = -1;
            double worstLoading = 0.0;
            for (int l = 0; l < model.Branches.Count; l++)
            {
                Branch br = model.Branches[l];
                if (l == outage || !br.InService || !(br.RateAMva > 0)) continue;
                double limit = post ? DcScopf.PostRating(br, rating) : br.RateAMva;
                double loading = Math.Abs(f[l]) / limit * 100.0;
                if (worst < 0 || loading > worstLoading) { worst = l; worstLoading = loading; }
                if (Math.Abs(f[l]) > limit * (1.0 + LimitTolerance))
                    violations.Add(new DcViolation(l, OutageModels.Label(model, l), f[l], limit,
                        post ? GridSim.Core.Analysis.TransmissionSecurity.TransmissionSecurityRunner.RatingSource(br, rating) : "A", loading));
            }
            if (status.Length == 0) status = violations.Count == 0 ? "SECURE" : $"VIOLATIONx{violations.Count}";
            return new DcContingencyRow(outage, label, status, preFlow, violations.Count, worst, worstLoading, violations)
                { FlowsMw = keepFlows ? (double[])f.Clone() : Array.Empty<double>() };
        }

        DcContingencyRow baseRow = Evaluate(-1, "base case", "", flow, post: false, preFlow: 0.0);
        var rows = new List<DcContingencyRow>(set.Length);
        foreach (int k in set)
        {
            string label = OutageModels.Label(model, k);
            if (df.OutageIslands(k))
            {
                rows.Add(new DcContingencyRow(k, label, "ISLANDED", flow[k], 0, -1, double.NaN, Array.Empty<DcViolation>()));
                continue;
            }
            double[] lodf = df.LodfColumn(k);
            var after = new double[flow.Length];
            for (int l = 0; l < after.Length; l++) after[l] = l == k ? 0.0 : flow[l] + lodf[l] * flow[k];
            rows.Add(Evaluate(k, label, "", after, post: true, preFlow: flow[k]));
        }
        return new DcContingencyResult(flow, baseRow, rows, unrated, df.Islands.Count);
    }
}
