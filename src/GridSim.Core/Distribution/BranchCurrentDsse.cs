using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>A branch-current DSSE result: per-bus phase voltages plus the estimated per-branch phase currents.</summary>
/// <param name="Converged">True when the radial forward sweep completed.</param>
/// <param name="BusIds">Bus ids aligned with <see cref="V"/>.</param>
/// <param name="V">Per-bus complex phase voltages (per-unit).</param>
/// <param name="BranchCurrents">Estimated current (into the from-end) per branch, keyed by (from, to).</param>
public sealed record BranchCurrentEstimate(
    bool Converged, IReadOnlyList<int> BusIds, Complex[][] V, IReadOnlyDictionary<(int From, int To), Complex[]> BranchCurrents)
{
    /// <summary>
    /// D-DOC-114 (October 2026): every (tree branch, phase) the estimate could not determine, as
    /// <c>"from-to phase"</c> (for example <c>"1-2 b"</c>), in the order of the forward sweep and phase a, b, c: a phase
    /// the branch carries with no current phasor on it (in either orientation). A branch with no phasor at all lists
    /// each of its phases. When this list is not empty <see cref="BranchCurrentEstimate.Converged"/> is false. Before,
    /// an unmeasured phase of a partly measured branch took zero current silently and the estimate still read
    /// Converged, with every voltage downstream on that phase wrong.
    /// </summary>
    public IReadOnlyList<string> UnobservableBranchPhases { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A branch-current distribution state estimator (Baran-Kelley formulation): the state is the per-phase branch
/// currents rather than the node voltages. On a radial feeder that is simpler, faster and better-conditioned
/// than the node-voltage formulation. It WLS-combines the redundant current-phasor measurements per branch,
/// then recovers the node voltages by a single forward sweep down the radial tree (V_to = V_from - Z * I_from).
/// It cross-validates the node-voltage <see cref="ThreePhaseWlsEstimator"/> to machine precision. A static,
/// measurement-only filter (no review-gate entry).
/// <para>N167 (October 2026): every N062 element is modelled. A measured current is the current into the branch at its
/// From bus; across a regulator and a charged line the forward sweep undoes the regulator ratio and the From-end half of
/// the charging before the series drop, and a lateral drops only on its phases (the others are exactly zero, as in the
/// sweep). Validated on the IEEE 123 Node Test Feeder (N167ExtendedElementSolversTests).</para>
/// </summary>
public static class BranchCurrentDsse
{
    /// <summary>Estimate the branch currents from the current-phasor measurements and forward-sweep the voltages.</summary>
    public static BranchCurrentEstimate Estimate(MultiPhaseModel model, IReadOnlyList<ThreePhaseCurrent> currents)
    {
        // N167 (October 2026): every N062 element is modelled (it used to refuse them all). Loads and capacitors do not
        // enter the formulation (the measured branch currents already carry what they draw); a closed switch has zero
        // drop; a single- or two-phase branch drops only on its phases and leaves the others exactly zero (as the sweep
        // does); a regulator and line charging are undone from the measured From-end current in the forward sweep below
        // (ExtendedStep). So no element kind is refused; a measurement on a phase its branch does not carry is.
        // N264 (October 2026): every measurement sigma must be usable (finite, above 0, finite positive weight 1 / sigma^2),
        // refused by name before anything else. A zero sigma used to give an infinite weight (NaN currents and voltages),
        // an infinite one a weight of 0 (the measurement silently ignored); see ThreePhaseSigmaChecks.
        ThreePhaseSigmaChecks.Require("the branch-current estimator", null, currents);
        // 1.95.251 (N168): the forward sweep below drops voltage across series impedances (and undoes regulators and
        // charging); it has no delta / grounded-wye transformer step, so such a feeder is refused by name.
        foreach (PhaseBranch db in model.Branches)
            if (db.IsTransformerElement)
                throw new ArgumentException(
                    $"the branch-current estimator cannot model branch {db.From}-{db.To}, a {db.TransformerName} transformer (its phase " +
                    "shift and zero-sequence break are not in the forward sweep of measured currents). Use the three-phase sweep.");
        int n = model.Buses.Count;
        var branchByKey = new Dictionary<(int, int), PhaseBranch>();
        foreach (PhaseBranch br in model.Branches) branchByKey.TryAdd((br.From, br.To), br);

        // D-DOC-165 (October 2026): a current named against the reversed endpoints (To, From) was never matched (the
        // lookup is by the exact (From, To) key), so the branch read as unmeasured and the CLI blamed a missing phasor,
        // while the node-voltage estimator has accepted the same file since R236. Do what R236 does: for a pure series
        // branch the current into its To end is exactly minus the current into its From end, so re-key the row onto the
        // model orientation and negate it (negation is exact, so a model-oriented file gives the same bits as before).
        // Across a regulator or line charging that identity does not hold (the ratio and the shunt current sit between
        // the two ends), so such a row is refused by name rather than converted wrongly. A row naming no branch in either
        // orientation used to be dropped without a word; it is now an input error, as in the node-voltage estimator.
        var oriented = new List<ThreePhaseCurrent>(currents.Count);
        foreach (ThreePhaseCurrent c in currents)
        {
            if (branchByKey.ContainsKey((c.FromBusId, c.ToBusId))) { oriented.Add(c); continue; }
            if (branchByKey.TryGetValue((c.ToBusId, c.FromBusId), out PhaseBranch? rb))
            {
                if (rb.RegulatorRatio is not null || rb.ShuntY is not null)
                    throw new ArgumentException(
                        $"the branch-current estimator: the current measurement {c.FromBusId}->{c.ToBusId} (phase {"abc"[c.Phase]}) is named " +
                        $"against branch {rb.From}-{rb.To}, which has a {(rb.RegulatorRatio is not null ? "regulator" : "line-charging shunt")}; " +
                        "the current into its To end is not minus the current into its From end, so give the measurement at the From end " +
                        $"(from {rb.From}, to {rb.To}).", nameof(currents));
                oriented.Add(c with { FromBusId = c.ToBusId, ToBusId = c.FromBusId, Value = -c.Value });
                continue;
            }
            throw new ArgumentException(
                $"the branch-current estimator: the current measurement on branch {c.FromBusId}->{c.ToBusId} (phase {"abc"[c.Phase]}) " +
                "matches no branch of the model in either orientation.", nameof(currents));
        }
        currents = oriented;

        foreach (ThreePhaseCurrent c in currents)
            if (branchByKey.TryGetValue((c.FromBusId, c.ToBusId), out PhaseBranch? mb) && !PhaseIndex.Has(mb.Phases, c.Phase))
                throw new ArgumentException(
                    $"the branch-current estimator: a current measurement on phase {"abc"[c.Phase]} of branch {c.FromBusId}-{c.ToBusId}, " +
                    $"which carries phase(s) {PhaseIndex.Name(mb.Phases)} only.", nameof(currents));
        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idToIndex[model.Buses[i].Id] = i;
        int source = -1;
        Complex[] sourceV = MultiPhaseModel.BalancedNominal();
        for (int i = 0; i < n; i++) if (model.Buses[i].IsSource) { source = i; sourceV = model.Buses[i].SourceV ?? sourceV; }

        // WLS-combine current measurements per (branch, phase): weighted mean, weight 1/sigma^2.
        var sum = new Dictionary<(int, int), Complex[]>();
        var wsum = new Dictionary<(int, int), double[]>();
        foreach (ThreePhaseCurrent c in currents)
        {
            var key = (c.FromBusId, c.ToBusId);
            if (!sum.TryGetValue(key, out Complex[]? sArr))
            {
                sArr = new Complex[3];
                sum[key] = sArr;
                wsum[key] = new double[3];
            }
            double w = 1.0 / (c.Sigma * c.Sigma);
            sArr[c.Phase] += w * c.Value;
            wsum[key][c.Phase] += w;
        }
        var branchCurrent = new Dictionary<(int From, int To), Complex[]>();
        foreach (var kv in sum)
        {
            var i = new Complex[3];
            for (int p = 0; p < 3; p++) i[p] = wsum[kv.Key][p] > 0 ? kv.Value[p] / wsum[kv.Key][p] : Complex.Zero;
            branchCurrent[kv.Key] = i;
        }

        // Radial tree from the source.
        var parentBus = new int[n];
        var parentBranch = new int[n];
        for (int i = 0; i < n; i++) { parentBus[i] = -1; parentBranch[i] = -1; }
        var adjacency = new List<(int Other, int Branch)>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new List<(int, int)>();
        // R233: the branch-current formulation is valid only on a single radial tree. A loop-closing or
        // parallel branch is never anyone's parent branch, so its current measurement was silently ignored and
        // the forward sweep followed one arbitrary path while reporting Converged. Mirror ThreePhaseSweep's
        // check: every branch must join two known buses and there must be exactly n - 1 of them (with every bus
        // reached below, that is a spanning tree). A non-radial model is reported Converged = false.
        bool radial = model.Branches.Count == n - 1;
        for (int b = 0; b < model.Branches.Count; b++)
        {
            PhaseBranch br = model.Branches[b];
            if (idToIndex.TryGetValue(br.From, out int f) && idToIndex.TryGetValue(br.To, out int t))
            {
                adjacency[f].Add((t, b));
                adjacency[t].Add((f, b));
            }
            else radial = false;   // a branch to an unknown bus is not part of any tree from the source
        }
        var order = new List<int>(n);
        var visited = new bool[n];
        var queue = new Queue<int>();
        queue.Enqueue(source); visited[source] = true;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            order.Add(u);
            foreach ((int other, int branch) in adjacency[u])
                if (!visited[other]) { visited[other] = true; parentBus[other] = u; parentBranch[other] = branch; queue.Enqueue(other); }
        }

        // Forward sweep: V_to = V_from - Z * I_from (orientation-aware).
        var v = new Complex[n][];
        for (int i = 0; i < n; i++) v[i] = (Complex[])sourceV.Clone();
        bool converged = radial;
        // Any bus the BFS never reached is disconnected from the source and unobservable; keeping the
        // cloned source voltage there is not a converged estimate. (The node-voltage estimator flags
        // the same case with a singular gain matrix and Converged=false.)
        for (int i = 0; i < n; i++) if (!visited[i]) converged = false;
        // N167: the phases of each bus, as the sweep defines them (the source has all three, every other bus the phases
        // of the branch feeding it); a branch may not carry a phase its upstream bus lacks.
        var busPhases = new Phase[n];
        var unobservable = new List<string>();   // D-DOC-114
        if (source >= 0) busPhases[source] = Phase.Abc;
        foreach (int bus in order)
        {
            if (bus == source) continue;
            PhaseBranch br = model.Branches[parentBranch[bus]];
            if ((br.Phases & ~busPhases[parentBus[bus]]) != 0)
                throw new ArgumentException(
                    $"the branch-current estimator: branch {br.From}-{br.To} carries phase(s) {PhaseIndex.Name(br.Phases)} but bus " +
                    $"{model.Buses[parentBus[bus]].Id}, which feeds it, has only {PhaseIndex.Name(busPhases[parentBus[bus]])}.");
            busPhases[bus] = br.Phases;
            var key = (br.From, br.To);
            // A tree branch with no current phasor is unobservable: substituting zero current silently copies the
            // parent voltage downstream, so flag the estimate rather than report Converged with a fabricated zero.
            // (A phasor keyed with reversed endpoints is re-keyed above since D-DOC-165.)
            // D-DOC-114 (October 2026): the same holds PER PHASE. A branch measured on some of its phases only used to pass
            // this check (the dictionary had an entry), and its unmeasured phases took the zero of the weighted mean, so
            // the estimate read Converged with those phases' voltages wrong all the way downstream. Every phase the branch
            // carries must have a phasor with positive weight; each one that has not is named in UnobservableBranchPhases
            // and the estimate is not converged. The arithmetic of the sweep is unchanged (the zero still stands in, so a
            // caller who looks at the voltages sees what it always saw), only the verdict and the list are new.
            wsum.TryGetValue(key, out double[]? wKey);
            for (int p = 0; p < 3; p++)
                if (PhaseIndex.Has(br.Phases, p) && !(wKey is not null && wKey[p] > 0))
                {
                    unobservable.Add($"{br.From}-{br.To} {"abc"[p]}");
                    converged = false;
                }
            if (!branchCurrent.TryGetValue(key, out Complex[]? ic)) { ic = new Complex[3]; converged = false; }
            Complex[] iBr = ic;
            int parent = parentBus[bus];
            // If the branch's from-end is the parent, the from-current drops parent->child; if the from-end is
            // the child, the same current raises the child voltage relative to the parent.
            bool fromIsParent = idToIndex[br.From] == parent;
            if (br.HasExtendedElements)
            {
                // N167: a lateral, a regulator or line charging; the v1 three-phase line below is untouched.
                converged &= ExtendedStep(br, fromIsParent, v[parent], iBr, v[bus]);
                continue;
            }
            Complex[] drop = br.Z.Multiply(iBr);
            for (int p = 0; p < 3; p++)
            {
                Complex nv = fromIsParent ? v[parent][p] - drop[p] : v[parent][p] + drop[p];
                converged &= double.IsFinite(nv.Real) && double.IsFinite(nv.Imaginary);
                v[bus][p] = nv;
            }
        }

        var busIds = new int[n];
        for (int i = 0; i < n; i++) busIds[i] = model.Buses[i].Id;
        return new BranchCurrentEstimate(converged, busIds, v, branchCurrent) { UnobservableBranchPhases = unobservable };
    }

    /// <summary>
    /// N167: one forward-sweep step across a branch with N062 elements, the inverse of the sweep's element (see
    /// <see cref="PhaseBranch"/> and <see cref="ThreePhaseSweep"/>). The measurement is the current into the branch at its
    /// From bus, i_from. With the From bus upstream (the parent):
    /// <code>
    ///   v_out    = a .* v_parent                 (a = the regulator ratio per phase; 1 without a regulator)
    ///   i_out    = i_from ./ a                   (the ideal regulator draws a times its output current)
    ///   i_series = i_out - (Y_sh / 2) v_out      (the From-end half of the pi shunt, when there is one)
    ///   v_child  = v_out - Z i_series            (on the branch's phases; the others are exactly zero)
    /// </code>
    /// A plain lateral (no regulator, no shunt) may also face the source (From downstream): then v_child = v_parent +
    /// Z i_from, as for a three-phase line. A regulator or a shunt facing the source is refused, naming the branch: the
    /// sweep refuses such a regulator too, and the shunt would need the unknown downstream voltage. Writes v_child into
    /// <paramref name="vChild"/>; returns false when a value is not finite.
    /// </summary>
    private static bool ExtendedStep(PhaseBranch br, bool fromIsParent, Complex[] vParent, Complex[] iFrom, Complex[] vChild)
    {
        if (!fromIsParent && (br.RegulatorRatio is not null || br.ShuntY is not null))
            throw new ArgumentException(
                $"the branch-current estimator: branch {br.From}-{br.To} has a {(br.RegulatorRatio is not null ? "regulator" : "line-charging shunt")} " +
                "and faces the source (its From bus is downstream); orient it from the source.");

        Complex[] vOut = vParent;
        Complex[] iSeries = iFrom;
        if (br.RegulatorRatio is { } a)
        {
            vOut = new[] { vParent[0] * a[0], vParent[1] * a[1], vParent[2] * a[2] };
            iSeries = new[] { iFrom[0] / a[0], iFrom[1] / a[1], iFrom[2] / a[2] };
        }
        if (br.ShuntY is { } y)
        {
            var ish = new Complex[3];
            for (int p = 0; p < 3; p++)
            {
                Complex s = Complex.Zero;
                for (int q = 0; q < 3; q++) s += y[p, q] * 0.5 * vOut[q];
                ish[p] = s;
            }
            iSeries = new[] { iSeries[0] - ish[0], iSeries[1] - ish[1], iSeries[2] - ish[2] };
        }
        Complex[] drop = br.Z.Multiply(iSeries);
        bool finite = true;
        for (int p = 0; p < 3; p++)
        {
            if (!PhaseIndex.Has(br.Phases, p)) { vChild[p] = Complex.Zero; continue; }
            Complex nv = fromIsParent ? vOut[p] - drop[p] : vOut[p] + drop[p];
            finite &= double.IsFinite(nv.Real) && double.IsFinite(nv.Imaginary);
            vChild[p] = nv;
        }
        return finite;
    }
}
