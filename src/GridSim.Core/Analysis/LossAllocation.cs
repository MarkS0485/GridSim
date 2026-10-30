using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis;

/// <summary>One branch's contribution to system losses.</summary>
public sealed record BranchLoss(
    string Name, int From, int To,
    double PLossMw, double QLossMvar,
    double LoadingPercent,
    bool IsTransformer);

/// <summary>Losses aggregated over a named zone (typically a TO boundary region).</summary>
public sealed record ZoneLoss(string Zone, double PLossMw, double QLossMvar, int BranchCount);

/// <summary>
/// The full loss decomposition of a solved state: every branch's individual loss,
/// a per-zone aggregation aligned with the model's <see cref="Boundary"/> definitions,
/// and a reconciliation residual against <see cref="PowerFlowResult.TotalLossMw"/>.
/// </summary>
public sealed class LossAllocationResult
{
    public required IReadOnlyList<BranchLoss> BranchLosses { get; init; }
    public required IReadOnlyList<ZoneLoss> ZoneLosses { get; init; }
    public required double TotalPLossMw { get; init; }
    public required double TotalQLossMvar { get; init; }

    /// <summary>Real power drawn by the bus shunt conductances at the solved voltages, sum of <c>Gs_i * Vm_i^2</c> (MW;
    /// <see cref="Bus.Gs"/> is MW at 1 pu). N053, October 2026: it is part of <see cref="PowerFlowResult.TotalLossMw"/>
    /// (the sum of net bus injections) but flows in no branch, so before N053 it was left in the reconciliation
    /// residual: 1.21 MW on case300, whose 17 conductive bus shunts total 1.30 MW at 1 pu. Zero on a model without
    /// conductive shunts.</summary>
    public double ShuntPMw { get; init; }

    /// <summary>What <see cref="PowerFlowResult.TotalLossMw"/> leaves unexplained after the branch losses AND the bus
    /// shunt conductance: <c>TotalLossMw - TotalPLossMw - ShuntPMw</c>. Rounding only (of order 1e-12 MW) on a
    /// converged solve.</summary>
    public required double ReconciliationResidualMw { get; init; }
}

/// <summary>
/// Decomposes a solved power-flow result into per-branch and per-zone (TO boundary)
/// loss contributions. The per-branch losses come directly from the pi-model line-flow
/// computation (I²R on each branch); the zone aggregation groups branches by which
/// boundary they belong to, with an "Other" bucket for branches outside any boundary.
/// </summary>
public static class LossAllocation
{
    /// <summary>
    /// Allocate the solved losses. D-DOC-087 (October 2026): <paramref name="result"/> must have converged
    /// (<see cref="ConvergenceContract.RequireConverged"/>): the losses of a non-converged iterate are not the network's
    /// losses, and they used to be allocated and reported like a solved state's.
    /// </summary>
    public static LossAllocationResult Allocate(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        result.RequireConverged("Loss allocation");   // D-DOC-087: the convergence contract
        // Per-branch losses (already computed by the solver).
        var branchLosses = new List<BranchLoss>(result.LineFlows.Count);
        double sumP = 0, sumQ = 0;
        foreach (LineFlow lf in result.LineFlows)
        {
            branchLosses.Add(new BranchLoss(
                lf.Name, lf.From, lf.To,
                lf.PLossMw, lf.QLossMvar,
                lf.LoadingPercent,
                model.Branches.Count > branchLosses.Count && model.Branches[branchLosses.Count].IsTransformer));
            sumP += lf.PLossMw;
            sumQ += lf.QLossMvar;
        }

        // Build branch-name -> boundary-name lookup. N138: every boundary branch name must name exactly one branch
        // (a duplicate used to take the first namesake here, the last in BoundaryAnalysis; an unknown name was skipped).
        Boundary.RequireResolvedBranchNames(model);
        var branchToZone = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Boundary b in model.Boundaries)
            foreach (BoundaryBranch bb in b.Branches)
                branchToZone.TryAdd(bb.Branch, b.Name);

        // Aggregate per zone.
        var zoneP = new Dictionary<string, double>(StringComparer.Ordinal);
        var zoneQ = new Dictionary<string, double>(StringComparer.Ordinal);
        var zoneCount = new Dictionary<string, int>(StringComparer.Ordinal);
        double otherP = 0, otherQ = 0;
        int otherCount = 0;

        foreach (BranchLoss bl in branchLosses)
        {
            if (!string.IsNullOrEmpty(bl.Name) && branchToZone.TryGetValue(bl.Name, out string? zone))
            {
                zoneP[zone] = zoneP.GetValueOrDefault(zone) + bl.PLossMw;
                zoneQ[zone] = zoneQ.GetValueOrDefault(zone) + bl.QLossMvar;
                zoneCount[zone] = zoneCount.GetValueOrDefault(zone) + 1;
            }
            else
            {
                otherP += bl.PLossMw;
                otherQ += bl.QLossMvar;
                otherCount++;
            }
        }

        var zoneLosses = new List<ZoneLoss>(zoneP.Count + 1);
        foreach (string z in zoneP.Keys)
            zoneLosses.Add(new ZoneLoss(z, zoneP[z], zoneQ[z], zoneCount[z]));
        GridSim.Core.Numerics.FrozenIntroSort.Sort(zoneLosses, (a, b) => b.PLossMw.CompareTo(a.PLossMw));   // GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)
        if (otherCount > 0)
            zoneLosses.Add(new ZoneLoss("Other", otherP, otherQ, otherCount));

        // N053: the bus shunt conductances draw real power that is in TotalLossMw (the sum of net bus injections, i.e.
        // everything the network absorbs) but in no branch's loss. Account for it explicitly, so the residual is the
        // rounding it claims to be. Buses are matched to the result by id (first occurrence, as the solvers key them);
        // the sum runs in result order, so it is deterministic. A result whose buses do not match the model is a
        // programming error and fails loud.
        double shuntP = 0.0;
        if (model.Buses.Any(b => b.Gs != 0))
        {
            var busById = new Dictionary<int, Bus>(model.Buses.Count);
            foreach (Bus b in model.Buses) busById.TryAdd(b.Id, b);
            if (result.Vm.Length != result.BusIds.Count)
                throw new ArgumentException($"the result has {result.BusIds.Count} bus ids but {result.Vm.Length} voltages.", nameof(result));
            for (int i = 0; i < result.BusIds.Count; i++)
            {
                if (!busById.TryGetValue(result.BusIds[i], out Bus? bus))
                    throw new ArgumentException($"result bus {result.BusIds[i]} is not in the model.", nameof(result));
                if (bus.Gs != 0) shuntP += bus.Gs * result.Vm[i] * result.Vm[i];
            }
        }

        return new LossAllocationResult
        {
            BranchLosses = branchLosses,
            ZoneLosses = zoneLosses,
            TotalPLossMw = sumP,
            TotalQLossMvar = sumQ,
            ShuntPMw = shuntP,
            // With no conductive shunt, shuntP is exactly 0.0 and x - 0.0 == x: the residual is bit-for-bit what it was.
            ReconciliationResidualMw = result.TotalLossMw - sumP - shuntP,
        };
    }
}
