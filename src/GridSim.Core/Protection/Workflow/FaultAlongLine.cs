using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Protection.Workflow;

/// <summary>A fault network for a bus fault or a fault a fraction <c>d</c> along a branch. For a fault along a branch the
/// branch is split by a new bus: the first segment keeps the original branch index (From to the new bus), the second is
/// appended (new bus to To), so every other branch keeps its index. <see cref="FromSegment"/> and <see cref="ToSegment"/> map
/// an original branch index to the faulted-model branch a device at that end now sits on.</summary>
public sealed record FaultNetwork(GridModel Model, int FaultBusId, bool Inserted, int SplitBranch, int SecondSegmentIndex)
{
    public static int FromSegment(int original) => original;
    public int ToSegment(int original) => Inserted && original == SplitBranch ? SecondSegmentIndex : original;
}

/// <summary>The currents the branches carry for one fault: kA at each end by branch index, NaN where the branch is out of
/// service or outside the fault's island.</summary>
public sealed record BranchCurrentTable(double[] FromEndKa, double[] ToEndKa)
{
    public double At(int branch, bool fromEnd) => fromEnd ? FromEndKa[branch] : ToEndKa[branch];
}

/// <summary>
/// Fault along a line (report row A11). For a position <c>d</c> in (0, 1) a bus is inserted by
/// <see cref="GridModel.CopyWith"/> and R, X, B (and R0, X0 and the length) are split in the ratio d : 1 - d; the thermal
/// rating is a property of the conductor, so both segments keep it. At d = 0 or 1 the fault is at the end bus and nothing is
/// inserted. A transformer (or any branch with a tap, phase shift or winding data) is refused: a point along a transformer
/// is not meaningful. Currents come from the existing IEC 60909 engine, read only.
/// </summary>
public static class FaultAlongLine
{
    public static FaultNetwork Build(GridModel model, int branchIndex, double fraction)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (branchIndex < 0 || branchIndex >= model.Branches.Count)
            throw new ArgumentOutOfRangeException(nameof(branchIndex), branchIndex, $"The case has branches 0..{model.Branches.Count - 1}.");
        if (!(fraction >= 0.0 && fraction <= 1.0)) throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "The fraction along the branch must be in [0, 1].");
        Branch br = model.Branches[branchIndex];
        if (fraction == 0.0) return new FaultNetwork(model, br.From, false, branchIndex, -1);
        if (fraction == 1.0) return new FaultNetwork(model, br.To, false, branchIndex, -1);
        if (!br.InService) throw new InvalidOperationException($"Branch {branchIndex} is out of service.");
        if (br.IsTransformer || br.TapRatio != 1.0 || br.PhaseShiftDeg != 0.0 || br.FromWinding is not null || br.ToWinding is not null)
            throw new InvalidOperationException($"Branch {branchIndex} is a transformer: a fault along it is not defined.");
        if (model.ZeroSequenceMutuals.Any(m => m.BranchA == branchIndex || m.BranchB == branchIndex))
            throw new InvalidOperationException($"Branch {branchIndex} has a zero-sequence mutual coupling that a split would break.");

        int newId = model.Buses.Max(b => b.Id) + 1;
        Bus fromBus = model.Buses.First(b => b.Id == br.From);
        var buses = model.Buses.ToList();
        buses.Add(new Bus(newId, BusType.Pq, BaseKv: fromBus.BaseKv, Vm: 1.0, Name: $"fault {branchIndex}@{fraction.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"));
        double d = fraction, e = 1.0 - fraction;
        string? n = br.Name;
        Branch a = br with { To = newId, R = br.R * d, X = br.X * d, B = br.B * d, R0 = br.R0 * d, X0 = br.X0 * d, LengthKm = br.LengthKm * d, Name = n is null ? null : n + "/a" };
        Branch b2 = br with { From = newId, R = br.R * e, X = br.X * e, B = br.B * e, R0 = br.R0 * e, X0 = br.X0 * e, LengthKm = br.LengthKm * e, Name = n is null ? null : n + "/b" };
        var branches = model.Branches.ToList();
        branches[branchIndex] = a;
        branches.Add(b2);
        GridModel copy = model.CopyWith(buses: buses, branches: branches);
        return new FaultNetwork(copy, newId, true, branchIndex, branches.Count - 1);
    }

    /// <summary>Per-branch end currents for a three-phase fault at <paramref name="faultBus"/>. The incumbent returns in-service
    /// branches in model order, so each model branch is matched to the next entry with the same ends and name; a branch with
    /// no entry (out of service, or outside the fault's island) is NaN. <paramref name="objective"/> Minimum scales the
    /// maximum-case currents by Ik''(min) / Ik''(max) from the public duty (exact for a network without converter infeed).</summary>
    public static BranchCurrentTable Currents(GridModel model, int faultBus, ShortCircuitObjective objective = ShortCircuitObjective.Maximum)
    {
        IReadOnlyList<Iec60909.BranchFaultCurrent> list = Iec60909.BranchFaultCurrents(model, faultBus);
        double scale = 1.0;
        if (objective == ShortCircuitObjective.Minimum)
        {
            double max = Iec60909.ThreePhase(model, faultBus, ShortCircuitObjective.Maximum).IkppKa;
            double min = Iec60909.ThreePhase(model, faultBus, ShortCircuitObjective.Minimum).IkppKa;
            scale = max > 0.0 ? min / max : 0.0;
        }
        var from = new double[model.Branches.Count];
        var to = new double[model.Branches.Count];
        Array.Fill(from, double.NaN); Array.Fill(to, double.NaN);
        int p = 0;
        for (int k = 0; k < model.Branches.Count && p < list.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService) continue;
            Iec60909.BranchFaultCurrent c = list[p];
            string name = string.IsNullOrEmpty(br.Name) ? $"branch {br.From}-{br.To}" : br.Name!;
            if (c.From != br.From || c.To != br.To || c.Name != name) continue;
            from[k] = c.FromEndKa * scale; to[k] = c.ToEndKa * scale;
            p++;
        }
        return new BranchCurrentTable(from, to);
    }
}
