using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis;

/// <summary>
/// One DC island's balance in a <see cref="DcPowerFlow"/> solve.
/// </summary>
/// <param name="ReferenceBusId">The island's reference (angle 0) bus, chosen by <see cref="DistributionFactors"/>.</param>
/// <param name="BusCount">Buses in the island.</param>
/// <param name="LoadMw">The island's demand (sum of bus Pd).</param>
/// <param name="ScheduledGenerationMw">The island's scheduled generation: the sum of in-service Pg, the reference bus's
/// units included.</param>
/// <param name="ReferenceInjectionMw">The net injection the reference bus must make so the island balances (the DC model
/// is lossless): minus the sum of every other bus's net injection. Compare with the reference bus's own scheduled net
/// injection to see how much it picks up.</param>
/// <param name="HasGeneration">False when no generator sits on the island: its load cannot really be supplied.</param>
public sealed record DcIslandBalance(
    int ReferenceBusId, int BusCount, double LoadMw, double ScheduledGenerationMw, double ReferenceInjectionMw, bool HasGeneration);

/// <summary>The result of a <see cref="DcPowerFlow"/> solve.</summary>
/// <param name="BusIds">Bus ids in model order.</param>
/// <param name="VaDeg">Bus voltage angles in degrees, aligned with <see cref="BusIds"/> (every island reference at 0).</param>
/// <param name="PInjMw">Net injections in MW, aligned with <see cref="BusIds"/>: scheduled generation minus demand at
/// every bus except each island reference, whose entry is the balancing <see cref="DcIslandBalance.ReferenceInjectionMw"/>.</param>
/// <param name="FlowMw">Branch flows in MW, from end to to end, index-aligned with <c>model.Branches</c>; zero for a
/// branch the DC model leaves out (out of service or zero reactance).</param>
/// <param name="LoadingPercent">|flow| / RateA x 100 per branch; 0 for an unrated branch.</param>
/// <param name="Islands">One balance per DC island, in model order of each island's first bus.</param>
public sealed record DcPowerFlowResult(
    IReadOnlyList<int> BusIds, double[] VaDeg, double[] PInjMw, double[] FlowMw, double[] LoadingPercent,
    IReadOnlyList<DcIslandBalance> Islands);

/// <summary>
/// The DC (linearised) power flow (F18, October 2026): bus angles and branch flows from the DC nodal susceptance, the
/// standard textbook linearisation. It REUSES <see cref="DistributionFactors"/> (the same B' build, the same
/// factorisation, the same per-island references and the same flow products as its <c>DcFlows</c>, which the DC-OPF
/// and the PTDF / LODF verbs already use), so there is one DC model in GridSim, not two.
///
/// Convention (that of <see cref="DistributionFactors"/>): only series reactance enters; resistance, line charging,
/// shunts, tap ratios and phase shifts are ignored; voltage magnitudes are 1 pu; there are no losses, so each island's
/// reference bus picks up exactly the island's imbalance. Injections are the in-service generators' Pg minus the bus
/// demand Pd. Nothing here is iterative, so there is no convergence question; a network whose DC model cannot be
/// formed (an in-service zero-reactance branch that is the only link between two islands, or a singular B') throws
/// <see cref="InvalidOperationException"/> from <see cref="DistributionFactors.Build"/>.
/// </summary>
public static class DcPowerFlow
{
    /// <summary>Solve the DC power flow of <paramref name="model"/>.</summary>
    public static DcPowerFlowResult Solve(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DistributionFactors df = DistributionFactors.Build(model);
        Dictionary<int, double> netByBus = NetInjections(model);
        (double[] theta, double[] flows) = df.DcSolve(netByBus, model.BaseMva);
        return Assemble(model, df, netByBus, theta, flows);
    }

    /// <summary>
    /// <see cref="Solve(GridModel)"/> with B' factored under the OPT-IN diagonal-preference pivot rule: a column's
    /// diagonal row is its pivot whenever its magnitude is at least <paramref name="diagonalPreference"/> times the largest
    /// candidate's (threshold partial pivoting, as KLU and UMFPACK offer). 0 is exactly <see cref="Solve(GridModel)"/>.
    /// A positive value changes the last bits of the answer (the two agree to rounding); it exists so that a batch of
    /// reactance-varied models can share the multi-case lanes (<see cref="SolveBatch"/>), which plain partial pivoting
    /// prevents on real networks (it breaks near-ties between equal-magnitude candidates by rounding, so the pivot order
    /// differs from model to model).
    /// </summary>
    public static DcPowerFlowResult Solve(GridModel model, double diagonalPreference)
    {
        ArgumentNullException.ThrowIfNull(model);
        RequirePreference(diagonalPreference);
        DistributionFactors df = DistributionFactors.Build(model, null, diagonalPreference);
        Dictionary<int, double> netByBus = NetInjections(model);
        (double[] theta, double[] flows) = df.DcSolve(netByBus, model.BaseMva);
        return Assemble(model, df, netByBus, theta, flows);
    }

    private static void RequirePreference(double diagonalPreference)
    {
        if (!(diagonalPreference >= 0.0 && diagonalPreference <= 1.0))
            throw new ArgumentOutOfRangeException(nameof(diagonalPreference), diagonalPreference, "the diagonal preference must lie in [0, 1]");
    }

    /// <summary>
    /// The DC power flow of many models at once: a Monte Carlo over branch reactances or injections, a scenario set.
    /// Entry i of the result is BIT-IDENTICAL to <see cref="Solve(GridModel)"/>(<paramref name="models"/>[i]), or with a
    /// positive <paramref name="diagonalPreference"/> to <see cref="Solve(GridModel, double)"/> under the same rule.
    ///
    /// <para>How. Each model's reduced susceptance B' is built exactly as <see cref="Solve"/> builds it. Models whose B'
    /// has the same sparsity structure form a family (a Monte Carlo over reactances on one topology is one family);
    /// each family is factored and solved <see cref="MultiCaseSparseLu.Lanes"/> models at a time, one model per SIMD
    /// lane (<see cref="SparseLuFamily"/>), and every lane holds exactly the bits the single-model factorisation and
    /// solve give (see <see cref="MultiCaseSparseLu"/>). The batches are independent and run in parallel, each writing
    /// only its own models' results, so neither the thread count nor the finishing order can change a number.</para>
    ///
    /// <para>Sharing. A lane shares the batch's factorisation plan only when its pivot order is the template's. Under
    /// plain partial pivoting (the default) a reactance-varied family on a real network almost never keeps the pivot
    /// order (gb-full: 1 lane in 32, even at a 1e-9 relative spread), so such a family is factored one model per lane
    /// alone: still bit-identical, with the speed coming from the parallel batches. Whether a family shares is decided
    /// from its first batch (if fewer than two of its lanes share, the rest of the family skips the lanes), which changes
    /// only the time taken, never a bit. With the opt-in <paramref name="diagonalPreference"/> the pivot order is stable
    /// and every lane shares (gb-full: 32 of 32 at a 10% spread, 32 factorisations in about a third of the time).</para>
    ///
    /// <para>Failures are as loud as <see cref="Solve"/>'s: a model the DC model cannot represent, or whose B' is
    /// singular, throws <see cref="InvalidOperationException"/> naming its position in the batch (the lowest such
    /// position when several fail), with <see cref="Solve"/>'s own exception as the inner exception.</para>
    /// </summary>
    /// <param name="models">The models (none null).</param>
    /// <param name="maxDegreeOfParallelism">Most batches in flight at once; -1 (the default) lets the runtime choose, 1
    /// runs every batch on the calling thread (results are the same bits either way).</param>
    /// <param name="diagonalPreference">The pivot rule: 0 (the default) is plain partial pivoting, the rule of
    /// <see cref="Solve(GridModel)"/>; a positive value is the opt-in rule of <see cref="Solve(GridModel, double)"/>.</param>
    public static IReadOnlyList<DcPowerFlowResult> SolveBatch(IReadOnlyList<GridModel> models, int maxDegreeOfParallelism = -1,
        double diagonalPreference = 0.0)
    {
        ArgumentNullException.ThrowIfNull(models);
        RequirePreference(diagonalPreference);
        if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), maxDegreeOfParallelism, "use -1 (runtime default) or a positive count");
        int count = models.Count;
        for (int i = 0; i < count; i++)
            if (models[i] is null) throw new ArgumentException($"DC power flow batch: model {i} is null.", nameof(models));
        var results = new DcPowerFlowResult[count];
        if (count == 0) return results;
        var errors = new Exception?[count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };

        // Stage 1: each model's B' and index maps (the first half of DistributionFactors.Build), in parallel; a model
        // the DC model cannot represent records its error.
        var prepared = new DistributionFactors.Prepared?[count];
        Parallel.For(0, count, options, i =>
        {
            try { prepared[i] = DistributionFactors.Prepare(models[i]); }
            catch (Exception ex) { errors[i] = ex; }
        });

        // Stage 2: families by B' structure, in model order (the first unassigned model's B' is the template), cut into
        // batches of up to Lanes models.
        var familyOf = new int[count];
        Array.Fill(familyOf, -1);
        int familyCount = 0;
        var batches = new List<(SparseLuFamily Family, int[] Members, bool First)>();
        for (int i = 0; i < count; i++)
        {
            if (prepared[i] is null || familyOf[i] >= 0) continue;
            var family = new SparseLuFamily(prepared[i]!.BPrime, diagonalPreference);
            int f = familyCount++;
            var members = new List<int>();
            for (int j = i; j < count; j++)
                if (prepared[j] is not null && familyOf[j] < 0 && family.IsMember(prepared[j]!.BPrime)) { familyOf[j] = f; members.Add(j); }
            for (int k = 0; k < members.Count; k += MultiCaseSparseLu.Lanes)
                batches.Add((family, members.GetRange(k, Math.Min(MultiCaseSparseLu.Lanes, members.Count - k)).ToArray(), k == 0));
        }

        // Stage 3: factor and solve each batch, then finish each model exactly as Solve does. Each family's first batch
        // runs first and decides whether the family's later batches use the lanes (see the summary); the decision depends
        // only on the inputs, and either way every lane is the single solve's bits.
        var familyShares = new Dictionary<SparseLuFamily, bool>(ReferenceEqualityComparer.Instance);
        int[] firsts = Enumerable.Range(0, batches.Count).Where(b => batches[b].First).ToArray();
        int[] rest = Enumerable.Range(0, batches.Count).Where(b => !batches[b].First).ToArray();
        var shares = new bool[batches.Count];
        Parallel.For(0, firsts.Length, options, f => shares[firsts[f]] = RunBatch(firsts[f], useLanes: true));
        foreach (int b in firsts) familyShares[batches[b].Family] = shares[b];
        Parallel.For(0, rest.Length, options, r => RunBatch(rest[r], familyShares[batches[rest[r]].Family]));

        for (int i = 0; i < count; i++)
            if (errors[i] is Exception e)
                throw new InvalidOperationException($"DC power flow batch: model {i} failed: {e.Message}", e);
        return results;

        // One batch: returns whether at least two of its lanes shared the plan.
        bool RunBatch(int bi, bool useLanes)
        {
            (SparseLuFamily family, int[] members, _) = batches[bi];
            int lanes = members.Length;
            MultiCaseSparseLu.Batch batch;
            try
            {
                CsrMatrix[] mats = members.Select(i => prepared[i]!.BPrime).ToArray();
                batch = useLanes ? family.Factor(mats) : family.FactorAlone(mats);
            }
            catch (Exception ex) { foreach (int i in members) errors[i] = ex; return false; }
            var df = new DistributionFactors?[lanes];
            var net = new Dictionary<int, double>?[lanes];
            var rhs = new double[lanes][];
            var x = new double[lanes][];
            for (int l = 0; l < lanes; l++)
            {
                int i = members[l];
                DistributionFactors.Prepared p = prepared[i]!;
                x[l] = new double[p.Nr];
                SparseLuFactorization? lane = batch.Lane(l);
                if (lane is null) { errors[i] = p.SingularError(); rhs[l] = new double[p.Nr]; continue; }
                df[l] = DistributionFactors.FromPrepared(p, lane);
                net[l] = NetInjections(models[i]);
                rhs[l] = df[l]!.DcRhs(net[l]!, models[i].BaseMva);
            }
            batch.SolveInto(rhs, x);
            for (int l = 0; l < lanes; l++)
            {
                if (df[l] is null) continue;
                int i = members[l];
                try
                {
                    (double[] theta, double[] flows) = df[l]!.DcSolveFromReduced(x[l], models[i].BaseMva);
                    results[i] = Assemble(models[i], df[l]!, net[l]!, theta, flows);
                }
                catch (Exception ex) { errors[i] = ex; }
            }
            return batch.SharedLanes.Count(s => s) >= 2;
        }
    }

    // Net scheduled injection per bus (MW): in-service generation minus demand, accumulated in model order.
    private static Dictionary<int, double> NetInjections(GridModel model)
    {
        var netByBus = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses) netByBus[b.Id] = netByBus.GetValueOrDefault(b.Id) - b.Pd;
        foreach (Generator g in model.Generators)
            if (netByBus.ContainsKey(g.Bus)) netByBus[g.Bus] += g.Pg;
        return netByBus;
    }

    // The result from the factors, the injections and the solved angles and flows (the tail of Solve, shared with
    // SolveBatch).
    private static DcPowerFlowResult Assemble(
        GridModel model, DistributionFactors df, Dictionary<int, double> netByBus, double[] theta, double[] flows)
    {

        int n = df.BusIds.Count;
        var firstIndex = new Dictionary<int, int>(n);   // bus id -> model index (first occurrence, as the factors key it)
        for (int i = 0; i < n; i++) firstIndex.TryAdd(df.BusIds[i], i);
        var vaDeg = new double[n];
        var pInj = new double[n];
        for (int i = 0; i < n; i++)
        {
            vaDeg[i] = theta[i] * 180.0 / Math.PI;
            pInj[i] = netByBus.GetValueOrDefault(df.BusIds[i]);
        }

        // Island balances: the reference injection is minus the sum of the island's other net injections.
        var genByBus = new Dictionary<int, double>();
        foreach (Generator g in model.Generators) genByBus[g.Bus] = genByBus.GetValueOrDefault(g.Bus) + g.Pg;
        var islands = new List<DcIslandBalance>(df.Islands.Count);
        foreach (DcIsland isl in df.Islands)
        {
            double others = 0.0, gen = 0.0;
            foreach (int id in isl.BusIds)
            {
                gen += genByBus.GetValueOrDefault(id);
                if (id != isl.ReferenceBusId) others += netByBus.GetValueOrDefault(id);
            }
            double refInj = -others;
            int refIdx = firstIndex.TryGetValue(isl.ReferenceBusId, out int ri) ? ri : -1;
            if (refIdx >= 0) pInj[refIdx] = refInj;
            islands.Add(new DcIslandBalance(isl.ReferenceBusId, isl.BusIds.Count, isl.LoadMw, gen, refInj, isl.HasGeneration));
        }

        var loading = new double[flows.Length];
        for (int l = 0; l < flows.Length; l++)
        {
            double rate = model.Branches[l].RateAMva;
            loading[l] = rate > 0 ? Math.Abs(flows[l]) / rate * 100.0 : 0.0;
        }

        return new DcPowerFlowResult(df.BusIds, vaDeg, pInj, flows, loading, islands);
    }
}
