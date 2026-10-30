using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// DC Power Transfer Distribution Factors (PTDF) and Line Outage Distribution Factors (LODF): the
/// closed-form linear sensitivities of branch flows to nodal injections (PTDF) and to line outages
/// (LODF). Where <see cref="ContingencyAnalysis"/> screens N-1 by physically removing a branch and
/// re-solving, these give the same information from ONE factorisation of the DC nodal susceptance -
/// the workhorses of operations and markets ("which line does each generator load", instant N-1
/// screening, the basis for redispatch).
///
/// DC linearisation (the standard convention): only the series reactance is used - R, line charging,
/// taps and phase shifts are ignored, and voltage magnitudes are 1 pu. Branch susceptance b_l = 1/X_l;
/// the reduced nodal susceptance B' (slack row/col removed) is factored once, then PTDF column k
/// (injection at bus k, withdrawal at the slack) is one solve B' x = e_k. LODF for an outage of line k
/// is one transfer solve, LODF[l,k] = Phi[l,k] / (1 - Phi[k,k]); when 1 - Phi[k,k] -> 0 the outage
/// islands the system (line k is a bridge), reported in <see cref="RadialOutages"/> rather than divided.
/// PTDF and LODF are dimensionless (MW per MW); <see cref="DcFlows"/> returns MW.
///
/// <para><b>Islands (N034, October 2026).</b> A network made of several electrically separate parts (DC islands:
/// connected components over the branches the DC model uses) has no single reference: B' with one slack removed
/// is singular, and this class used to throw "DC susceptance is singular" (so gb-full, which carries a 3-bus island
/// with its own slack, could not run the security DC-OPF or the CLI <c>dcopf</c> verb). Each island now gets its
/// own reference bus, chosen the way the AC power flow's island pre-pass (<see cref="PowerFlow.IslandPartition"/>)
/// chooses one: the island's slack bus if it has one (the first in model order), otherwise the bus of its largest
/// generator by |Pg| (the first in model order among ties). One difference: here any generator on the island counts,
/// even one at Pg = 0, while the AC pre-pass blacks out an island whose generators all have Pg = 0. The island
/// holding the overall slack keeps exactly the reference this class always used. Every reference row and column
/// is removed from B', which is then block diagonal and factored once. A PTDF column means "inject at the bus,
/// withdraw at ITS island's reference"; flows in other islands are zero. An island with no generator
/// at all still gets a reference (its first bus, so B' stays
/// non-singular) and is reported through <see cref="Islands"/> with <see cref="DcIsland.HasGeneration"/> false, so
/// a caller can tell that nothing can supply it. With one island (every connected case) nothing changes: the same
/// reference, the same reduced matrix built in the same order, the same factorisation, bit for bit.</para>
/// </summary>
public sealed class DistributionFactors
{
    /// <summary>Islanding test of a branch outage (R155): when <c>|1 - Phi_kk|</c> (one minus the branch's
    /// self-transfer sensitivity) is below this, the branch is a bridge, its outage islands the network and its
    /// LODF column is undefined (NaN). Absolute and dimensionless; shared by <see cref="OutageIslands"/> and the
    /// LODF column so the two can never disagree.</summary>
    public const double IslandingSensitivityTolerance = 1e-9;

    private readonly SparseLuFactorization _bPrime; // factored reduced susceptance
    private readonly int[] _redOf;   // full bus index -> reduced index (every island reference -> -1)
    private readonly int _n;         // bus count
    private readonly int _nr;        // reduced size: the bus count minus one reference per island
    private readonly int[] _from;    // per branch: from bus index (or -1 for out-of-service)
    private readonly int[] _to;
    private readonly double[] _b;    // per branch: susceptance 1/X (0 for out-of-service)
    // Bus id -> model index of its FIRST occurrence (R005). Replaces a linear Array.IndexOf per lookup, which made
    // DcFlows O(injections x buses); first-occurrence keeps IndexOf's answer exactly, even for a duplicated id.
    private readonly Dictionary<int, int> _firstIndexOfBus;

    /// <summary>Bus ids in model order (the column index of PTDF).</summary>
    public IReadOnlyList<int> BusIds { get; }
    /// <summary>The slack bus id (PTDF's reference; its column is identically zero). With several islands this is
    /// the reference of the island holding the overall slack; every island's reference is in <see cref="Islands"/>.</summary>
    public int SlackBusId { get; }

    /// <summary>The DC islands, in order of each island's first bus in the model (N034). A connected network has
    /// exactly one, whose reference is <see cref="SlackBusId"/>.</summary>
    public IReadOnlyList<DcIsland> Islands { get; }

    private DistributionFactors(SparseLuFactorization bPrime, int[] redOf, int n, int nr,
        int[] from, int[] to, double[] b, IReadOnlyList<int> busIds, int slackId, IReadOnlyList<DcIsland> islands)
    {
        _bPrime = bPrime; _redOf = redOf; _n = n; _nr = nr;
        _from = from; _to = to; _b = b;
        BusIds = busIds; SlackBusId = slackId; Islands = islands;
        _firstIndexOfBus = new Dictionary<int, int>(busIds.Count);
        for (int i = 0; i < busIds.Count; i++) _firstIndexOfBus.TryAdd(busIds[i], i);
    }

    /// <summary>Model index of bus <paramref name="busId"/> (its first occurrence), or -1 when absent - the value
    /// <c>Array.IndexOf(BusIds, busId)</c> gave before R005, in O(1).</summary>
    private int IndexOfBus(int busId) => _firstIndexOfBus.TryGetValue(busId, out int i) ? i : -1;

    /// <summary>Build the factors for <paramref name="model"/> (slack = the model's slack bus unless
    /// <paramref name="slackBusId"/> overrides). A network of several DC islands gets one reference per island (see
    /// the class summary and <see cref="FindIslands"/>). Throws when an in-service zero-reactance branch is the only
    /// link between two islands (the reactance-only model cannot represent it), when one sits inside a mesh (GR-127:
    /// leaving it out would change the topology), or when B' is singular even with one reference per island.</summary>
    public static DistributionFactors Build(GridModel model, int? slackBusId = null)
    {
        Prepared p = Prepare(model, slackBusId);
        SparseLuFactorization fact = SparseLu.Factor(p.BPrime) ?? throw p.SingularError();
        return FromPrepared(p, fact);
    }

    /// <summary><see cref="Build"/> with B' factored under the OPT-IN diagonal-preference pivot rule (see
    /// <see cref="SparseLuFactorization.Factor"/>); 0 is exactly <see cref="Build"/>. Used only by
    /// <see cref="DcPowerFlow.Solve(GridModel, double)"/>.</summary>
    internal static DistributionFactors Build(GridModel model, int? slackBusId, double diagonalPreference)
    {
        if (diagonalPreference == 0.0) return Build(model, slackBusId);
        Prepared p = Prepare(model, slackBusId);
        SparseLuFactorization fact = SparseLu.Factor(p.BPrime, diagonalPreference) ?? throw p.SingularError();
        return FromPrepared(p, fact);
    }

    /// <summary>
    /// Everything <see cref="Build"/> derives from a model before the factorisation: the reduced nodal susceptance B'
    /// (built in exactly <see cref="Build"/>'s stamp order) and the index maps. <see cref="Build"/> is
    /// <c>FromPrepared(p, SparseLu.Factor(p.BPrime))</c>, so a caller that factors many B' matrices of one structure
    /// together (the batched DC power flow, <see cref="DcPowerFlow.SolveBatch"/>) and hands each factorisation back
    /// through <see cref="FromPrepared"/> gets exactly the object <see cref="Build"/> would have made, provided its
    /// factorisation is bit-identical to <see cref="SparseLu.Factor(CsrMatrix)"/>'s.
    /// </summary>
    internal sealed record Prepared(CsrMatrix BPrime, int[] RedOf, int N, int Nr, int[] From, int[] To, double[] B,
        int[] BusIds, int SlackBusId, IReadOnlyList<DcIsland> Islands)
    {
        /// <summary>The error <see cref="Build"/> throws when B' cannot be factorised.</summary>
        internal InvalidOperationException SingularError() => new(
            $"DC susceptance is singular even with one reference bus per island ({Islands.Count} island(s)); "
            + "PTDF/LODF cannot be formed for this network.");
    }

    /// <summary>The factors from a <see cref="Prepare"/> result and the factorisation of its B'.</summary>
    internal static DistributionFactors FromPrepared(Prepared p, SparseLuFactorization fact)
        => new(fact, p.RedOf, p.N, p.Nr, p.From, p.To, p.B, p.BusIds, p.SlackBusId, p.Islands);

    /// <summary>The model-to-matrix half of <see cref="Build"/> (see <see cref="Prepared"/>); throws as
    /// <see cref="Build"/> does for a network the DC model cannot represent.</summary>
    internal static Prepared Prepare(GridModel model, int? slackBusId = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        int n = model.Buses.Count;
        var index = new Dictionary<int, int>(n);
        var busIds = new int[n];
        for (int i = 0; i < n; i++) { index[model.Buses[i].Id] = i; busIds[i] = model.Buses[i].Id; }

        // The islands and their references. The island holding the overall slack (the slackBusId override, else the
        // first Slack bus, else bus 0: the rule this class always used) keeps that slack as its reference, and it is
        // first in referenceIndices.
        (IReadOnlyList<DcIsland> islands, int[] referenceIndices) = IslandsCore(model, index, slackBusId);

        // Full -> reduced index (drop every island's reference). With one island this is exactly the old
        // "drop the slack" numbering.
        var isReference = new bool[n];
        foreach (int r in referenceIndices) isReference[r] = true;
        var redOf = new int[n];
        for (int i = 0, r = 0; i < n; i++) redOf[i] = isReference[i] ? -1 : r++;
        int nr = n - referenceIndices.Length;

        int m = model.Branches.Count;
        var from = new int[m];
        var to = new int[m];
        var b = new double[m];
        var builder = new CsrMatrix.Builder(nr);
        for (int l = 0; l < m; l++)
        {
            Branch br = model.Branches[l];
            if (!br.InService || !index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t))
            { from[l] = -1; to[l] = -1; b[l] = 0.0; continue; }
            // GR-127 (October 2026): an in-service zero-reactance branch is refused here too. IslandsCore already refuses
            // one that is the only link between two islands; one inside a mesh used to be left out of B' silently (its
            // buses not merged), so DC flows, PTDF, LODF and the corrective redispatch were computed on a network
            // without that connection. The reactance-only model cannot represent it (its susceptance is infinite):
            // give it a small reactance (MATPOWER's 1e-4 pu) or merge its two buses.
            if (br.X == 0.0)
                throw new InvalidOperationException(
                    $"DC model: in-service branch {l} ('{br.Name ?? $"{br.From}-{br.To}"}', {br.From}->{br.To}) has zero reactance inside a meshed part of "
                    + "the network; the reactance-only DC model cannot represent it (infinite susceptance) and leaving it out would compute "
                    + "PTDF/LODF on a different topology. Give it a small reactance (1e-4 pu) or merge its two buses.");
            double bl = 1.0 / br.X;
            from[l] = f; to[l] = t; b[l] = bl;

            // Real nodal-susceptance stamp B += A^T diag(b) A, with every reference row/col omitted. With one island
            // that is the slack's alone, exactly as before: the same stamps, in the same order, with the same values.
            bool fFree = redOf[f] >= 0, tFree = redOf[t] >= 0;
            if (fFree) builder.Add(redOf[f], redOf[f], bl);
            if (tFree) builder.Add(redOf[t], redOf[t], bl);
            if (fFree && tFree)
            {
                builder.Add(redOf[f], redOf[t], -bl);
                builder.Add(redOf[t], redOf[f], -bl);
            }
        }

        CsrMatrix bp = builder.Build();
        return new Prepared(bp, redOf, n, nr, from, to, b, busIds, busIds[referenceIndices[0]], islands);
    }

    /// <summary>
    /// The DC islands of <paramref name="model"/> (N034): the connected components over the branches the DC model
    /// uses (in service, non-zero reactance, both end buses present), each with its reference bus, in order of the
    /// island's first bus in the model. A connected network gives one island whose reference is the slack this
    /// class has always used (<paramref name="slackBusId"/> if given and present, else the first Slack bus, else the
    /// first bus). The class summary says how the other islands choose theirs. Throws
    /// <see cref="InvalidOperationException"/> when an in-service zero-reactance branch joins two islands: the AC
    /// model counts that branch as a connection and the reactance-only DC model cannot (its susceptance is
    /// infinite), so splitting the network there would silently invent an island.
    /// </summary>
    public static IReadOnlyList<DcIsland> FindIslands(GridModel model, int? slackBusId = null)
    {
        int n = model.Buses.Count;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;
        return IslandsCore(model, index, slackBusId).Islands;
    }

    /// <summary>The islands (see <see cref="FindIslands"/>, in model order) and each island's reference bus index,
    /// with the reference of the island holding the overall slack FIRST in the index array (it becomes
    /// <see cref="SlackBusId"/>).</summary>
    private static (IReadOnlyList<DcIsland> Islands, int[] ReferenceIndices) IslandsCore(
        GridModel model, Dictionary<int, int> index, int? slackBusId)
    {
        int n = model.Buses.Count;
        if (n == 0) throw new InvalidOperationException("DC distribution factors: the model has no buses.");

        // The overall slack, by the rule this class has always used (unchanged).
        int slack = -1;
        if (slackBusId is int sid && index.TryGetValue(sid, out int si)) slack = si;
        else for (int i = 0; i < n; i++) if (model.Buses[i].Type == BusType.Slack) { slack = i; break; }
        if (slack < 0) slack = 0; // no explicit slack: reference the first bus

        // Union-find over bus indices, joined by exactly the branches the DC model stamps.
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || br.X == 0.0 || !index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t)) continue;
            int rf = Find(f), rt = Find(t);
            if (rf != rt) parent[rf] = rt;
        }

        // Fail loud on an in-service zero-reactance branch that is the only DC link between two parts.
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || br.X != 0.0 || !index.TryGetValue(br.From, out int f) || !index.TryGetValue(br.To, out int t)) continue;
            if (Find(f) != Find(t))
                throw new InvalidOperationException(
                    $"DC model: in-service branch '{br.Name}' ({br.From}-{br.To}) has zero series reactance and is the only link "
                    + "between two parts of the network; the reactance-only DC model cannot represent it (its susceptance is infinite).");
        }

        // Island number per bus, numbered in order of each island's first bus in the model.
        var islandOfRoot = new Dictionary<int, int>();
        var islandOf = new int[n];
        var members = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!islandOfRoot.TryGetValue(r, out int k)) { k = members.Count; islandOfRoot[r] = k; members.Add(new List<int>()); }
            islandOf[i] = k;
            members[k].Add(i);
        }

        // Generators: whether each island has any, and the largest |Pg| on each bus (for the reference election).
        var largestPgOnBus = new Dictionary<int, double>();
        var hasGen = new bool[members.Count];
        foreach (Generator gen in model.Generators)
        {
            if (!index.TryGetValue(gen.Bus, out int gi)) continue;
            hasGen[islandOf[gi]] = true;
            double a = Math.Abs(gen.Pg);
            if (!largestPgOnBus.TryGetValue(gi, out double cur) || a > cur) largestPgOnBus[gi] = a;
        }

        var islands = new DcIsland[members.Count];
        var refs = new int[members.Count];
        int primary = islandOf[slack];
        for (int k = 0; k < members.Count; k++)
        {
            List<int> comp = members[k];
            int reference;
            if (k == primary) reference = slack;
            else
            {
                // The AC island rule (IslandPartition): the island's own slack if it has one (first in model order),
                // otherwise the bus carrying its largest generator by |Pg| (first in model order among ties),
                // otherwise (no generator: a dead island) its first bus, only so that B' stays non-singular.
                reference = -1;
                foreach (int i in comp) if (model.Buses[i].Type == BusType.Slack) { reference = i; break; }
                if (reference < 0)
                {
                    double best = -1.0;
                    foreach (int i in comp)
                        if (largestPgOnBus.TryGetValue(i, out double a) && a > best) { best = a; reference = i; }
                }
                if (reference < 0) reference = comp[0];
            }
            refs[k] = reference;
            double loadMw = 0.0;
            var ids = new int[comp.Count];
            for (int j = 0; j < comp.Count; j++) { ids[j] = model.Buses[comp[j]].Id; loadMw += model.Buses[comp[j]].Pd; }
            islands[k] = new DcIsland(model.Buses[reference].Id, ids, hasGen[k], loadMw);
        }

        // The primary island's reference first, then the others in island order.
        var ordered = new int[refs.Length];
        ordered[0] = refs[primary];
        for (int k = 0, o = 1; k < refs.Length; k++) if (k != primary) ordered[o++] = refs[k];
        return (islands, ordered);
    }

    /// <summary>True when outaging <paramref name="branch"/> disconnects the system (a bridge/radial line,
    /// for which LODF is undefined): the self-transfer sensitivity reaches 1.</summary>
    public bool OutageIslands(int branch)
        => _from[branch] >= 0 && Math.Abs(1.0 - SelfSensitivity(branch)) < IslandingSensitivityTolerance;

    /// <summary>Solve B' x = rhs_reduced and scatter into a full angle vector (every island reference at angle 0).</summary>
    private double[] SolveTheta(double[] reducedRhs) => ThetaFromReduced(_bPrime.Solve(reducedRhs));

    /// <summary>Scatter a reduced solution of B' x = rhs into a full angle vector (every island reference at 0).</summary>
    private double[] ThetaFromReduced(double[] x)
    {
        var theta = new double[_n];
        for (int i = 0; i < _n; i++) theta[i] = _redOf[i] < 0 ? 0.0 : x[_redOf[i]];
        return theta;
    }

    /// <summary>PTDF column for bus <paramref name="busId"/>: each branch's flow sensitivity (MW/MW) to
    /// +1 injected at the bus, withdrawn at the reference of the bus's island (the slack, for a connected
    /// network). Zero for a reference bus itself, and zero on every branch of another island.</summary>
    public double[] PtdfColumn(int busId)
    {
        int k = IndexOfBus(busId);
        // GR-493: a bus that is not in the model has no PTDF; it used to come back as a silent all-zero column (the
        // answer for a reference bus), so a typo read as "no sensitivity".
        if (k < 0)
            throw new ArgumentException($"PTDF: bus {busId} is not in the model.", nameof(busId));
        var col = new double[_from.Length];
        if (_redOf[k] < 0) return col;
        var rhs = new double[_nr];
        rhs[_redOf[k]] = 1.0;
        double[] theta = SolveTheta(rhs);
        for (int l = 0; l < _from.Length; l++)
            if (_from[l] >= 0) col[l] = _b[l] * (theta[_from[l]] - theta[_to[l]]);
        return col;
    }

    /// <summary>Self-sensitivity Phi[k,k] of branch k to its own from->to transfer (used by the LODF guard).</summary>
    private double SelfSensitivity(int branchK)
    {
        double[] theta = TransferTheta(branchK);
        return _b[branchK] * (theta[_from[branchK]] - theta[_to[branchK]]);
    }

    /// <summary>Angles from a unit transfer along branch k (inject +1 at its from bus, -1 at its to bus).</summary>
    private double[] TransferTheta(int branchK)
    {
        int f = _from[branchK], t = _to[branchK];
        var rhs = new double[_nr];
        if (_redOf[f] >= 0) rhs[_redOf[f]] += 1.0;
        if (_redOf[t] >= 0) rhs[_redOf[t]] -= 1.0;
        return SolveTheta(rhs);
    }

    /// <summary>LODF column for an outage of branch <paramref name="outageBranch"/>: each monitored
    /// branch's flow change per unit of the outaged branch's pre-outage flow. Diagonal is -1; an
    /// islanding (bridge) outage returns an all-NaN column and is listed in <see cref="RadialOutages"/>.</summary>
    public double[] LodfColumn(int outageBranch)
    {
        int m = _from.Length;
        var col = new double[m];
        if (_from[outageBranch] < 0) return col; // out of service
        double[] theta = TransferTheta(outageBranch);
        double phiKK = _b[outageBranch] * (theta[_from[outageBranch]] - theta[_to[outageBranch]]);
        double denom = 1.0 - phiKK;
        if (Math.Abs(denom) < IslandingSensitivityTolerance)
        {
            for (int l = 0; l < m; l++) col[l] = double.NaN;
            return col;
        }
        for (int l = 0; l < m; l++)
        {
            if (_from[l] < 0) continue;
            double phi = _b[l] * (theta[_from[l]] - theta[_to[l]]);
            col[l] = phi / denom;
        }
        col[outageBranch] = -1.0;
        return col;
    }

    /// <summary>DC branch flows (MW) for the given net nodal injections (bus id -> MW; each island's reference
    /// absorbs that island's imbalance: the slack, for a connected network). Index-aligned to <c>model.Branches</c>;
    /// zero for out-of-service branches.</summary>
    public double[] DcFlows(IReadOnlyDictionary<int, double> injectionMw, double baseMva)
    {
        double[] theta = SolveTheta(InjectionRhs(injectionMw, baseMva));
        return FlowsFromTheta(theta, baseMva);
    }

    /// <summary>
    /// The DC solve of <see cref="DcFlows"/> giving BOTH the bus angles and the branch flows from one solve (F18, the
    /// <c>dcpf</c> verb). <c>Theta</c> is in radians, index-aligned to <see cref="BusIds"/>, every island reference at
    /// 0; <c>FlowsMw</c> is exactly what <see cref="DcFlows"/> returns for the same injections (the same right-hand
    /// side, the same solve, the same flow products, so the same bits).
    /// </summary>
    public (double[] Theta, double[] FlowsMw) DcSolve(IReadOnlyDictionary<int, double> injectionMw, double baseMva)
    {
        double[] theta = SolveTheta(InjectionRhs(injectionMw, baseMva));
        return (theta, FlowsFromTheta(theta, baseMva));
    }

    /// <summary>The right-hand side <see cref="DcSolve"/> solves B' against, for the batched DC power flow, which solves
    /// it in the multi-case lanes and hands the reduced solution back to <see cref="DcSolveFromReduced"/>.</summary>
    internal double[] DcRhs(IReadOnlyDictionary<int, double> injectionMw, double baseMva) => InjectionRhs(injectionMw, baseMva);

    /// <summary><see cref="DcSolve"/> from the reduced solution x of B' x = <see cref="DcRhs"/> (the same scatter and
    /// flow products, so the same bits when x is the same bits as <c>B'.Solve(rhs)</c>).</summary>
    internal (double[] Theta, double[] FlowsMw) DcSolveFromReduced(double[] x, double baseMva)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != _nr) throw new ArgumentException($"DC solve: the reduced solution has {x.Length} entries; B' has {_nr}.", nameof(x));
        double[] theta = ThetaFromReduced(x);
        return (theta, FlowsFromTheta(theta, baseMva));
    }

    // The reduced right-hand side for net injections in MW (each island reference's own entry is dropped: it absorbs
    // its island's imbalance). Shared by DcFlows and DcSolve so the two cannot drift apart.
    private double[] InjectionRhs(IReadOnlyDictionary<int, double> injectionMw, double baseMva)
    {
        var rhs = new double[_nr];
        foreach ((int busId, double mw) in injectionMw)
        {
            int i = IndexOfBus(busId);
            if (i < 0 || _redOf[i] < 0) continue;
            rhs[_redOf[i]] += mw / baseMva;
        }
        return rhs;
    }

    // Branch flows (MW) from a full angle vector: b_l * (theta_from - theta_to) * base, zero for a branch the DC model
    // leaves out (out of service, zero reactance, or an end bus missing).
    private double[] FlowsFromTheta(double[] theta, double baseMva)
    {
        var f = new double[_from.Length];
        for (int l = 0; l < _from.Length; l++)
            if (_from[l] >= 0) f[l] = _b[l] * (theta[_from[l]] - theta[_to[l]]) * baseMva;
        return f;
    }

    /// <summary>
    /// The PTDF-transpose product (F19, nodal prices): for every bus k, <c>sum_l w_l PTDF[l, k]</c>, the weighted sum of
    /// the branch sensitivities to an injection at k, for the branch weights <paramref name="branchWeights"/> (branch
    /// index into <c>model.Branches</c> -> weight). Returned index-aligned to <see cref="BusIds"/>; every island
    /// reference gets 0 (its PTDF column is zero).
    ///
    /// <para>How: <c>PTDF[l, k] = b_l (e_from(l) - e_to(l))^T B'^-1 e_k</c>, so the sum over l is
    /// <c>e_k^T B'^-T r</c> with <c>r = sum_l w_l b_l (e_from(l) - e_to(l))</c>. B' (the reduced nodal susceptance
    /// matrix, A^T diag(b) A) is symmetric, so ONE solve <c>B' z = r</c> gives the whole vector: z_k is the answer at
    /// bus k. This equals summing w_l times each <see cref="PtdfColumn"/> entry in exact arithmetic; in floating point
    /// the two agree to rounding, not bit for bit (different operation order). Used only by the new price outputs, so
    /// no existing number depends on it.</para>
    /// </summary>
    public double[] PtdfTransposeProduct(IReadOnlyDictionary<int, double> branchWeights)
    {
        ArgumentNullException.ThrowIfNull(branchWeights);
        var rhs = new double[_nr];
        foreach ((int l, double w) in branchWeights)
        {
            if (l < 0 || l >= _from.Length)
                throw new ArgumentOutOfRangeException(nameof(branchWeights), $"branch index {l} is outside 0..{_from.Length - 1}.");
            if (_from[l] < 0 || w == 0.0) continue;   // a branch the DC model leaves out carries no sensitivity
            double v = w * _b[l];
            int f = _redOf[_from[l]], t = _redOf[_to[l]];
            if (f >= 0) rhs[f] += v;
            if (t >= 0) rhs[t] -= v;
        }
        return SolveTheta(rhs);
    }

    /// <summary>Full PTDF matrix [branches x buses] - convenient for small cases/tests; a column per bus.
    /// Memory-heavy at scale (use <see cref="PtdfColumn"/> for one column on a large network).</summary>
    public double[,] Ptdf()
    {
        int m = _from.Length;
        var p = new double[m, _n];
        for (int k = 0; k < _n; k++)
        {
            if (_redOf[k] < 0) continue;   // a reference bus: its column is identically zero
            var rhs = new double[_nr];
            rhs[_redOf[k]] = 1.0;
            double[] theta = SolveTheta(rhs);
            for (int l = 0; l < m; l++) if (_from[l] >= 0) p[l, k] = _b[l] * (theta[_from[l]] - theta[_to[l]]);
        }
        return p;
    }
}

/// <summary>
/// One DC island of a network (N034): a connected component over the branches the DC model uses.
/// </summary>
/// <param name="ReferenceBusId">The island's reference (angle 0) bus: it absorbs the island's injection imbalance in
/// every PTDF column and flow solve.</param>
/// <param name="BusIds">The island's bus ids, in model order.</param>
/// <param name="HasGeneration">True when at least one generator sits on the island. False marks a dead island: no
/// source can supply its load, and its reference exists only to keep the DC matrix non-singular.</param>
/// <param name="LoadMw">The island's total demand (sum of bus Pd, MW).</param>
public sealed record DcIsland(int ReferenceBusId, IReadOnlyList<int> BusIds, bool HasGeneration, double LoadMw);
