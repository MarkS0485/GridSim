using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.PyPsa;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S2 / P05 (October 2026): the numbers of the GB reference-model cross-check report
/// (review-progress/2026-10/gb-reference-cross-check.md), pinned so the report can never drift from what GridSim
/// computes. Each model is solved the way the report says; every value is asserted exactly.
/// </summary>
public class GbReferenceCrossCheckTests
{
    private static string Data => S10TestSupport.DataDir;
    private static readonly PowerFlowOptions PfOptions = new() { MaxIterations = 100, EnforceQLimits = true };

    private static GridModel Gb29() => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Path.Combine(Data, GbReferenceModels.Gb29RelativePath)));

    /// <summary>Generation as solved: each bus's net injection plus its demand, summed (includes the slack's balancing output).</summary>
    private static double SolvedGeneration(GridModel m, PowerFlowResult r)
    {
        double g = 0;
        for (int i = 0; i < m.BusCount; i++) g += r.PInjMw[i] + m.Buses[i].Pd;
        return g;
    }

    /// <summary>
    /// Boundary flows of a model whose bus i is PyPSA-GB buses.csv row i but whose branches carry no names
    /// (gb-reduced-29): the signed sum of the from-end MW of every branch whose ends lie on different sides of the cut,
    /// +1 when the from end is north, the side sets being the audit file boundary-sides.json. The same rule
    /// BoundaryAnalysis applies to the named cuts of pypsa-gb-reduced.
    /// </summary>
    private static double[] SideCutFlows(GridModel m, PowerFlowResult r)
    {
        string[] names = File.ReadAllLines(Path.Combine(Data, GbReferenceModels.PypsaReducedRelativeDir, "buses.csv")).Skip(1)
            .Where(l => l.Length > 0).Select(l => l.Split(',')[0]).ToArray();
        using JsonDocument sides = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "matpower", "gb", "pypsa-gb", "boundary-sides.json")));
        var flows = new List<double>();
        foreach (JsonProperty p in sides.RootElement.EnumerateObject())
        {
            var north = p.Value.GetProperty("reducedNorth").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
            double f = 0;
            foreach (LineFlow lf in r.LineFlows)
            {
                bool fn = north.Contains(names[lf.From - 1]), tn = north.Contains(names[lf.To - 1]);
                if (fn != tn) f += (fn ? 1 : -1) * lf.PFromMw;
            }
            flows.Add(f);
        }
        return flows.ToArray();
    }

    [Fact]
    public void Gb_reduced_29_as_shipped_totals_and_boundary_flows()
    {
        GridModel m = Gb29();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, PfOptions);
        Assert.True(r.Converged);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: generation 58231.686081460124 -> 58231.68608146015; side-cut
        // flows 2039.86113179131, 5747.547961074001, 6490.237991534751, 12464.200294955155 -> 2039.86113179131, 5747.54796107399,
        // 6490.237991534737, 12464.200294955164.
        Assert.Equal(58231.68608146015, SolvedGeneration(m, r));
        Assert.Equal(new[] { 2039.86113179131, 5747.54796107399, 6490.237991534737, 12464.200294955164 }, SideCutFlows(m, r));
    }

    [Fact]
    public void Gb_reduced_29_balanced_by_pmax_distributed_slack_totals_and_boundary_flows()
    {
        // The report's balanced operating point: the file's 18,686.28 MW surplus (plus losses) shared by every unit in
        // proportion to its Pmax (pf --distributed-slack pmax, F22), not sunk at the slack. Dense and sparse agree.
        GridModel m = Gb29();
        var dense = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Pmax);
        var sparse = new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Pmax);
        PowerFlowResult r = dense.Solve(m, PfOptions);
        S10TestSupport.AssertBitIdentical("gb-reduced-29 dslack dense vs sparse", r, sparse.Solve(m, PfOptions));
        Assert.True(r.Converged);
        Assert.Equal(7, dense.LastPasses);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: imbalance -18316.54732822068 -> -18316.547328220688; loss
        // 369.73260441774084 -> 369.7326044177099; generation 56695.592604417754 -> 56695.59260441771; max loading 66.36695254816127
        // -> 66.36695254816104; side-cut flows 1140.7169505839834, 3003.932414253936, 2988.1789189807632, 4745.373223631851 ->
        // 1140.7169505839834, 3003.9324142538417, 2988.1789189806705, 4745.373223631759 (passes unchanged).
        Assert.Equal(-18316.547328220688, dense.LastImbalanceMw);
        Assert.Equal(369.7326044177099, r.TotalLossMw);
        Assert.Equal(56695.59260441771, SolvedGeneration(m, r));
        Assert.Equal(66.36695254816104, r.LineFlows.Max(f => f.LoadingPercent));
        Assert.Equal(new[] { 1140.7169505839834, 3003.9324142538417, 2988.1789189806705, 4745.373223631759 }, SideCutFlows(m, r));
    }

    [Fact]
    public void Pypsa_gb_reduced_balanced_by_pmax_distributed_slack_totals_and_boundary_flows()
    {
        GridModel m = PypsaGbReduced.Load(Path.Combine(Data, GbReferenceModels.PypsaReducedRelativeDir), Gb29()).Model;
        var dense = new DistributedSlackPowerFlow(new NewtonRaphsonPowerFlow(), ParticipationRule.Pmax);
        PowerFlowResult r = dense.Solve(m, PfOptions);
        S10TestSupport.AssertBitIdentical("pypsa-gb-reduced dslack dense vs sparse", r,
            new DistributedSlackPowerFlow(new SparseNewtonRaphsonPowerFlow(), ParticipationRule.Pmax).Solve(m, PfOptions));
        Assert.True(r.Converged);
        Assert.Equal(8, dense.LastPasses);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: loss 341.41531248968545 -> 341.4153124897141; generation
        // 56667.275312489684 -> 56667.27531248971; max loading 90.20653128976625 -> 90.20653128976627; boundary flows
        // 1147.338345604764, 3013.3707364550805, 3009.5283892811663, 4785.358216398061 -> 1147.3383456047648, 3013.3707364550364,
        // 3009.5283892811212, 4785.358216398061; as-shipped generation 58392.05792558953 -> 58392.05792558954.
        Assert.Equal(341.4153124897141, r.TotalLossMw);
        Assert.Equal(56667.27531248971, SolvedGeneration(m, r));
        Assert.Equal(90.20653128976627, r.LineFlows.Max(f => f.LoadingPercent));
        Assert.Equal(new[] { 1147.3383456047648, 3013.3707364550364, 3009.5283892811212, 4785.358216398061 },
            BoundaryAnalysis.Evaluate(m, r).Select(f => f.FlowMw));
        // As shipped (the slack sinks the surplus), the same solve the P02 pin covers: generation as solved.
        PowerFlowResult shipped = new NewtonRaphsonPowerFlow().Solve(m, PfOptions);
        Assert.Equal(58392.05792558954, SolvedGeneration(m, shipped));
    }

    [Fact]
    public void Gb_network_2224_totals()
    {
        GridModel m = MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Path.Combine(Data, GbReferenceModels.Gb2224RelativePath)));
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m, PfOptions);
        Assert.True(r.Converged);
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: generation 61302.46991104913 -> 61302.46991104905; loss
        // 1224.9099110491472 -> 1224.9099110490408.
        Assert.Equal(61302.46991104905, SolvedGeneration(m, r));
        Assert.Equal(1224.9099110490408, r.TotalLossMw);
    }

    [Fact]
    public void Gb_full_and_the_uk_case_totals_and_boundary_flows_under_the_study_solver()
    {
        // gb-full and uk carry OLTCs, so the report solves every model with the contingency family's study solver (the
        // OLTC loop over the shared Newton engine, 100 iterations, reactive limits on); for the MATPOWER models, with no
        // OLTCs, that is the pf solve to the bit (pinned in the P01 / P02 tests).
        GridModel full = JsonGridLoader.LoadDirectory(Path.Combine(Data, "gb-full"));
        PowerFlowResult rf = ContingencyAnalysis.CreateStudySolver(full).Solve(full, ContingencyAnalysis.StudyOptions);
        Assert.True(rf.Converged);
        Assert.Equal(53284.997572214575, full.Buses.Sum(b => b.Pd));
        // Moved 2026-10 (D1, Mark's determinism rule 2026-10-04: identical bits on every CPU tier and OS): deterministic
        // fdlibm sin / cos in the Newton solve. Before -> after: generation 53808.126425048205 -> 53808.12642504887; loss
        // 523.1288528336736 -> 523.1288528342689; boundary flows 1670.7787461364737, 3779.626723418185, 3340.5799117624283,
        // 8535.77075152331, -2534.8000886880695 -> 1670.778746136451, 3779.6267234181432, 3340.5799117624165, 8535.77075152322,
        // -2534.800088688238 (demand and the uk case unchanged).
        Assert.Equal(53808.12642504887, SolvedGeneration(full, rf));
        Assert.Equal(523.1288528342689, rf.TotalLossMw);
        IReadOnlyList<BoundaryFlow> bf = BoundaryAnalysis.Evaluate(full, rf);
        Assert.Equal(new[] { "B4 SHETL-SPT (SSE-SP)", "B6 Scotland-England (SCOTEX)", "B7 Upper North England (HARSPNBLY)", "B8 North to Midlands (FLOWSTH)", "SEIMP South East import (SEIMPPR2)" },
            bf.Select(f => f.Name));
        Assert.Equal(new[] { 1670.778746136451, 3779.6267234181432, 3340.5799117624165, 8535.77075152322, -2534.800088688238 }, bf.Select(f => f.FlowMw));

        GridModel uk = JsonGridLoader.LoadDirectory(Path.Combine(Data, "uk"));
        PowerFlowResult ru = ContingencyAnalysis.CreateStudySolver(uk).Solve(uk, ContingencyAnalysis.StudyOptions);
        Assert.True(ru.Converged);
        Assert.Equal(39555.56867258789, SolvedGeneration(uk, ru));
        Assert.Equal(55.56867258789316, ru.TotalLossMw);
        Assert.Equal(new[] { 1549.9351574107684, 2321.819157158176, 1247.0267937336664, 3050.5350893032605 },
            BoundaryAnalysis.Evaluate(uk, ru).Select(f => f.FlowMw));
    }
}
