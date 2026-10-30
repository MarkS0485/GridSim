using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Distributed;

/// <summary>Where the distributed model's inter-domain synchronising stiffness came from (R025, October 2026 review,
/// stream B1).</summary>
public enum CouplingSource
{
    /// <summary>Derived from the network: the Kron-reduced synchronising susceptance between the domains' buses.</summary>
    Kron,
    /// <summary>The uniform target-mode stiffness (tuned to <see cref="DistributedConfig.TargetInterAreaHz"/>, not derived
    /// from the network), because the model carries no branch reactance (a bare test model).</summary>
    UniformNoBranchReactance,
    /// <summary>The uniform target-mode stiffness, because the network reduction failed (a singular part of the network
    /// that still connects machines).</summary>
    UniformReductionFailed,
    /// <summary>The uniform target-mode stiffness, because the reduced network couples the domains by a negligible total
    /// (the domains' machines are not connected to each other through the network).</summary>
    UniformNegligibleCoupling,
    /// <summary>One swing-mass domain or none: there is nothing to couple.</summary>
    NotNeeded,
}

/// <summary>
/// R025 (October 2026 review, stream B1): how the inter-domain coupling was obtained, so a report can say whether the
/// per-domain nadirs and RoCoFs rest on the network or on the uniform heuristic. Before R025 the fallback was silent.
/// </summary>
/// <param name="Source">Where the stiffness came from.</param>
/// <param name="Reason">Plain-English reason (the reduction's own message when it failed); empty for <see cref="CouplingSource.Kron"/>.</param>
/// <param name="DroppedBusCount">Buses left out of the reduction because no machine bus is connected to them (an
/// isolated bus, or a separate island with no machine of a swing domain): they cannot couple anything, and eliminating
/// an isolated one used to fail the whole reduction.</param>
/// <param name="DroppedNegativePairs">Cross-domain bus pairs whose reduced synchronising coefficient was zero or negative
/// (an anti-synchronising artefact of the flat-voltage reduction), left out of K as before, now counted.</param>
public sealed record CouplingReport(CouplingSource Source, string Reason, int DroppedBusCount, int DroppedNegativePairs)
{
    /// <summary>True when the stiffness is the uniform heuristic rather than derived from the network.</summary>
    public bool IsHeuristic => Source is CouplingSource.UniformNoBranchReactance or CouplingSource.UniformReductionFailed
        or CouplingSource.UniformNegligibleCoupling;
}

/// <summary>
/// Derives the inter-domain synchronising-stiffness matrix K (MW/rad) from the network. Kron-reduces
/// the bus admittance matrix onto the generator buses (<see cref="KronReduction"/>), then reads the
/// reduced synchronising susceptance (Im Y x base MVA at flat 1 p.u. voltage) across every cross-domain
/// bus pair. This replaces the M1 MVP target-mode heuristic with physically-derived coupling: a stiffer
/// line (smaller reactance) between two domains gives a larger K.
///
/// Falls back to the M1 uniform target-mode stiffness when the model carries no branch reactance (bare
/// test models) or the reduction yields negligible coupling. The matrix is symmetric, so the tie power it
/// drives is antisymmetric and never moves the centre of inertia (the T1 invariant is preserved).
/// </summary>
public static class DomainCoupling
{
    // ---- Named thresholds (October 2026 review, R159, H hygiene stream). Values unchanged. ----

    /// <summary>A branch whose |X| (pu) is at or below this counts as having no reactance; a model with no branch above
    /// it (a bare test model) falls back to the uniform target-mode stiffness.</summary>
    public const double MinBranchReactancePu = 1e-12;

    /// <summary>Total synchronising stiffness (MW/rad, summed over every ordered domain pair) at or below which the
    /// Kron-reduced coupling counts as negligible and the uniform target-mode stiffness is used instead.</summary>
    public const double MinTotalCouplingMwPerRad = 1e-9;

    /// <summary>Synchronising stiffness (MW/rad) between each pair of swing-mass domains, in <paramref name="masses"/> order.</summary>
    public static double[,] SynchronisingMatrix(
        GridModel model, IReadOnlyList<SpeedDomain> masses, DistributedConfig dist)
        => Synchronising(model, masses, dist).K;

    /// <summary>
    /// The synchronising stiffness matrix (as <see cref="SynchronisingMatrix"/>) together with where it came from.
    /// R025 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): (1) buses that no machine
    /// bus is connected to are dropped before the reduction (as the power-flow island partition does), so one isolated
    /// bus anywhere in a large model (common after topology edits) no longer aborts the reduction and silently swaps the
    /// physical coupling for the heuristic; (2) when the heuristic is still used, the report says so and why; (3) the
    /// cross-domain pairs dropped as anti-synchronising are counted. Bit-identity: a dropped bus shares no admittance with
    /// any kept bus, so eliminating it never touched a kept entry; the kept entries are eliminated in the same order with
    /// the same operations, so every reduced value is the one the full reduction gave whenever that succeeded.
    /// </summary>
    public static (double[,] K, CouplingReport Report) Synchronising(
        GridModel model, IReadOnlyList<SpeedDomain> masses, DistributedConfig dist)
    {
        // GR-543: every number in a CouplingReport reason is formatted invariantly, so DivergenceReport.Mechanism (which
        // copies it) is the same text on every machine locale.
        int n = masses.Count;
        var k = new double[n, n];
        if (n <= 1) return (k, new CouplingReport(CouplingSource.NotNeeded, "one swing-mass domain or none: nothing to couple", 0, 0));

        bool hasReactance = model.Branches.Any(b => Math.Abs(b.X) > MinBranchReactancePu);
        if (!hasReactance)
            return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformNoBranchReactance,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the model has no branch reactance, so the uniform {dist.TargetInterAreaHz:0.###} Hz target-mode stiffness is used"), 0, 0));

        // Map each generator bus id -> the swing-mass domain that holds the most inertia there.
        (Dictionary<int, int> busDomain, List<int> keepBusIds) = BusDomains(model, masses);

        // R105 / N290 (stream P, October 2026): the sparse admittance cells (bit for bit the dense build's) and the sparse
        // reduction (bit for bit the dense one); the dense n x n array is no longer built (see TryReduce).
        SparseYBus ybus = YBus.BuildSparse(model);
        Complex[,]? reduced = TryReduce(ybus, keepBusIds, out string? failure, out int dropped);
        if (reduced is null)
            return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformReductionFailed,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the network reduction onto the machine buses failed ({failure}), so the uniform {dist.TargetInterAreaHz:0.###} Hz ")
                + "target-mode stiffness is used", dropped, 0));

        int negative = 0;
        int m = keepBusIds.Count;
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
            {
                int da = busDomain[keepBusIds[a]];
                int db = busDomain[keepBusIds[b]];
                if (da == db) continue;
                double kij = model.BaseMva * reduced[a, b].Imaginary;   // synchronising coefficient (MW/rad), flat 1 p.u.
                if (kij <= 0) { negative++; continue; }                  // ignore anti-synchronising artefacts (counted, R025)
                k[da, db] += kij;
                k[db, da] += kij;
            }

        double total = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) total += k[i, j];
        if (total > MinTotalCouplingMwPerRad)
            return (k, new CouplingReport(CouplingSource.Kron, string.Empty, dropped, negative));
        return (Uniform(masses, model.FrequencyHz, dist), new CouplingReport(CouplingSource.UniformNegligibleCoupling,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the reduced network couples the speed domains by a negligible total ({total:E3} MW/rad), so the uniform ")
            + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{dist.TargetInterAreaHz:0.###} Hz target-mode stiffness is used"), dropped, negative));
    }

    /// <summary>
    /// R060 (October 2026 review, stream B1; Ruling R1: Mark delegated EE calls 2026-10-04): how a power step at
    /// <paramref name="busId"/> is shared among the swing-mass domains at the instant it happens, as fractions that add to
    /// one, in <paramref name="masses"/> order, with a plain-English statement of the rule that placed it.
    ///
    /// <para>The physics (Anderson and Fouad, Power System Control and Stability, 2nd ed., 2003, ch. 3 "System Response to
    /// Small Disturbances" (chapter title confirmed from the Library of Congress contents, N292), the distribution of a
    /// power impact [VERIFY section number]; Kundur, Power System Stability and Control, 1994, section 11.1): at the
    /// instant of a sudden power impact at a bus the rotor angles cannot move, so the impact is picked up by the machines
    /// in proportion to their synchronising power coefficients with respect to the point of impact, P_sik, and only later
    /// redistributed by inertia and the governors. This model works on the same flat-voltage, network-only reduction as
    /// <see cref="SynchronisingMatrix"/>: the generator buses are the domains' internal nodes, P_sik is the base MVA times
    /// the imaginary part of the Kron-reduced transfer admittance between the impact bus and each generator bus, and a
    /// domain's share is the sum over its buses. Consequences, each the limit of that rule: a step at a bus that hosts a
    /// domain's machines lands wholly on that domain (the bus IS the domain's node; when machines of several domains
    /// share the bus, the one holding the most inertia there owns it, the rule SynchronisingMatrix uses); a step at a
    /// load or transmission bus is shared by electrical proximity.</para>
    ///
    /// <para>Fails loud: a bus the model does not have (<see cref="ArgumentException"/>), and a bus that cannot be placed
    /// physically, because the model has no branch reactance, the network reduction fails, or the bus has no positive
    /// synchronising coupling to any machine (<see cref="InvalidOperationException"/>, naming the bus and the reason).</para>
    /// </summary>
    public static (double[] Shares, string Rule) ImpactShares(GridModel model, IReadOnlyList<SpeedDomain> masses, int busId)
    {
        int n = masses.Count;
        if (n == 0) throw new ArgumentException("There is no swing-mass domain to place a power step on.", nameof(masses));
        if (!model.Buses.Any(b => b.Id == busId))
            throw new ArgumentException($"The frequency event is placed at bus {busId}, which the model '{model.Name}' does not have.", nameof(busId));

        (Dictionary<int, int> busDomain, List<int> genBusIds) = BusDomains(model, masses);
        var shares = new double[n];
        if (busDomain.TryGetValue(busId, out int home))
        {
            shares[home] = 1.0;
            return (shares, $"bus {busId} hosts the machines of speed domain {masses[home].Id}, which takes the whole step");
        }

        if (!model.Branches.Any(b => Math.Abs(b.X) > MinBranchReactancePu))
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: it hosts no synchronous machine and the model has no branch "
                + "reactance to share the step by electrical proximity.");

        var keep = new List<int>(genBusIds) { busId };
        // N290 (stream P, October 2026): the sparse cells and the sparse reduction, as in Synchronising (see TryReduce).
        SparseYBus ybus = YBus.BuildSparse(model);
        Complex[,]? reduced = TryReduce(ybus, keep, out string? failure, out _);
        if (reduced is null)
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: the network reduction onto the machine buses failed ({failure}).");

        int e = keep.Count - 1;   // the impact bus is the last kept node
        var coupling = new double[n];
        for (int a = 0; a < genBusIds.Count; a++)
        {
            double pSik = model.BaseMva * reduced[e, a].Imaginary;   // synchronising coefficient to the impact bus (MW/rad)
            if (pSik > 0) coupling[busDomain[genBusIds[a]]] += pSik;
        }
        double total = 0;
        for (int d = 0; d < n; d++) total += coupling[d];
        if (!(total > MinTotalCouplingMwPerRad))
            throw new InvalidOperationException(
                $"Cannot place the frequency event at bus {busId}: it has no synchronising coupling to any synchronous machine "
                + "(it is not connected to the machines through the network).");
        for (int d = 0; d < n; d++) shares[d] = coupling[d] / total;
        return (shares, $"bus {busId} hosts no synchronous machine; the step is shared by synchronising coupling to the impact bus "
            + "(Anderson and Fouad, distribution of a power impact)");
    }

    /// <summary>
    /// Each generator bus that hosts a swing-mass member, mapped to the domain holding the most inertia (H x MBase) there,
    /// plus the bus ids in first-seen order (domain order, then member order). The list is the kept-bus order of the Kron
    /// reduction; it used to be read from the dictionary's Keys, which enumerate in insertion order when nothing is
    /// removed, so the order and every reduced value are the same, now pinned by construction instead of by a
    /// Dictionary implementation detail.
    /// </summary>
    private static (Dictionary<int, int> BusDomain, List<int> BusIds) BusDomains(GridModel model, IReadOnlyList<SpeedDomain> masses)
    {
        var busDomain = new Dictionary<int, int>();
        var busInertia = new Dictionary<int, double>();
        var order = new List<int>();
        for (int d = 0; d < masses.Count; d++)
            foreach (int gi in masses[d].MemberGenIndices)
            {
                Generator g = model.Generators[gi];
                double e = g.H * g.MBase;
                if (!busInertia.TryGetValue(g.Bus, out double cur) || e > cur)
                {
                    if (!busDomain.ContainsKey(g.Bus)) order.Add(g.Bus);
                    busInertia[g.Bus] = Math.Max(cur, e);
                    busDomain[g.Bus] = d;
                }
            }
        return (busDomain, order);
    }

    /// <summary>
    /// The Kron reduction of the network onto <paramref name="keepBusIds"/> (in that order), or null with the reason when it
    /// cannot be formed. R025: only the buses connected (through non-zero admittance) to at least one kept bus take part;
    /// the rest (<paramref name="droppedBusCount"/>) are left out, because they share no admittance with any kept bus and
    /// so cannot change a reduced value, while an isolated one (a zero self-admittance) used to abort the whole reduction
    /// on its near-singular pivot. The retained buses keep their relative order, so the elimination order and every
    /// reduced value are those of the full reduction whenever that succeeded.
    ///
    /// <para>R105 / N290 (stream P, October 2026): the same computation on the admittance's nonzero cells instead of the
    /// dense n x n array. The distributed model ran this on every Simulate call (R105), and a distributed trip at a bus with
    /// no machine ran it twice (Synchronising and ImpactShares, N290): on gb-full (3,539 buses) each was a 200 MB dense
    /// array, a dense breadth-first search (n^2 per bus visited) and a dense elimination. Bit-identity, step by step:</para>
    /// <list type="number">
    /// <item>The cells are <see cref="YBus.BuildSparse"/>'s, bit for bit the dense array's entries (same stamps, same
    ///   order; a position without a cell is the dense array's (+0, +0)).</item>
    /// <item>The search: the dense code joined i and j (i != j) when y[i,j] or y[j,i] is not zero (a signed zero counts as
    ///   zero). Here the same edges come from the off-diagonal cells whose value is not zero (a position without a cell is
    ///   zero), so the same buses are reached; only the reached set is used, not the visiting order.</item>
    /// <item>The sub-network: the reached buses in ascending original index, and the cells between two reached buses
    ///   renumbered, which is exactly the dense sub-matrix (every other position of it is (+0, +0) in both); with nothing
    ///   dropped, the cells themselves, as the dense code passed the very matrix.</item>
    /// <item>The reduction: <see cref="SparseKronReduction.Reduce"/>, bit for bit <see cref="KronReduction.Reduce"/> on
    ///   that matrix, with the same exception and message when it fails (the proof is on that class).</item>
    /// </list>
    /// </summary>
    private static Complex[,]? TryReduce(SparseYBus ybus, IReadOnlyList<int> keepBusIds, out string? failure, out int droppedBusCount)
    {
        int nAll = ybus.N;
        IReadOnlyDictionary<(int Row, int Col), Complex> cells = ybus.Cells;

        // The admittance graph: an edge for every off-diagonal cell that is not zero (both directions).
        var adjacent = new List<int>[nAll];
        for (int i = 0; i < nAll; i++) adjacent[i] = new List<int>();
        foreach (KeyValuePair<(int Row, int Col), Complex> cell in cells)
        {
            (int r, int c) = cell.Key;
            if (r == c || cell.Value == Complex.Zero) continue;
            adjacent[r].Add(c);
            adjacent[c].Add(r);
        }

        // Breadth-first search over the admittance graph from every kept bus.
        var reached = new bool[nAll];
        var queue = new Queue<int>();
        foreach (int id in keepBusIds)
        {
            int i = ybus.IndexOf(id);
            if (!reached[i]) { reached[i] = true; queue.Enqueue(i); }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (int j in adjacent[i])
                if (!reached[j]) { reached[j] = true; queue.Enqueue(j); }
        }

        // The sub-network of reached buses, in ascending original index (the order the reduction eliminates in).
        var subOf = new int[nAll];
        int kept = 0;
        for (int i = 0; i < nAll; i++) subOf[i] = reached[i] ? kept++ : -1;
        droppedBusCount = nAll - kept;

        IReadOnlyDictionary<(int Row, int Col), Complex> subCells;
        if (droppedBusCount == 0) subCells = cells;   // nothing to drop: the very matrix the reduction always used
        else
        {
            var sub = new Dictionary<(int Row, int Col), Complex>(cells.Count);
            foreach (KeyValuePair<(int Row, int Col), Complex> cell in cells)
            {
                int a = subOf[cell.Key.Row], b = subOf[cell.Key.Col];
                if (a >= 0 && b >= 0) sub[(a, b)] = cell.Value;
            }
            subCells = sub;
        }
        List<int> keepIdx = keepBusIds.Select(id => subOf[ybus.IndexOf(id)]).ToList();
        try
        {
            failure = null;
            return SparseKronReduction.Reduce(kept, subCells, keepIdx);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>The M1 uniform stiffness targeting cfg.TargetInterAreaHz (fallback for branchless models).</summary>
    private static double[,] Uniform(IReadOnlyList<SpeedDomain> masses, double f0, DistributedConfig dist)
    {
        int n = masses.Count;
        var k = new double[n, n];
        double[] eMass = masses.Select(mm => mm.AggregateInertiaMwS).ToArray();
        double sumInvE = eMass.Sum(e => 1.0 / e);
        double omega = 2.0 * Math.PI * dist.TargetInterAreaHz;
        double kUniform = (omega * omega) / (Math.PI * f0 * sumInvE);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) if (i != j) k[i, j] = kUniform;
        return k;
    }
}
