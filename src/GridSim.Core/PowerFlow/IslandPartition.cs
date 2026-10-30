using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>What the island pre-pass did to make an (possibly split) network solvable.</summary>
public sealed record IslandReport(
    int Islands,                       // connected components over in-service branches
    int EnergizedIslands,              // islands that can be solved (have a reference)
    int BlackedOutIslands,             // islands with no scheduled infeed and no unit able to produce real power -> de-energised, removed from the solve
    IReadOnlyList<int> ElectedSlackBuses,   // buses promoted to reference (island had generation but no slack)
    IReadOnlyList<int> BlackedOutBuses,     // buses dropped from the solve (dead islands)
    double LostLoadMw,                 // demand on blacked-out buses (lost supply)
    double LostGenMw)                  // generation on blacked-out buses (lost infeed)
{
    public bool AnyChange => ElectedSlackBuses.Count > 0 || BlackedOutBuses.Count > 0;

    /// <summary>GR-055: one entry per island that was given an elected reference, in <see cref="ElectedSlackBuses"/>
    /// order: what the elected bus must absorb at the island's schedule, and whether that is outside its units' range.
    /// The Newton engines never limit a slack, so an island whose imbalance is beyond the elected units' capability still
    /// solves; this is the place that says so (losses are not included: they are known only after the solve).</summary>
    public IReadOnlyList<ElectedIsland> ElectedIslands { get; init; } = Array.Empty<ElectedIsland>();

    /// <summary>Coupled devices (HVDC links, FACTS devices, static var compensators, induction machines, transformer
    /// flow controls, zero-sequence mutuals) that touched a de-energised bus and so left the solve with their island,
    /// one plain-English description each, in model order. Empty when nothing was dropped.</summary>
    public IReadOnlyList<string> DroppedDevices { get; init; } = Array.Empty<string>();

    /// <summary>GR-055: true when any elected reference must leave its units' [Pmin, Pmax] to balance its island.</summary>
    public bool AnyElectedBeyondCapability => ElectedIslands.Any(e => e.BeyondCapability);
}

/// <summary>GR-055: the balance of one island whose reference was elected by <see cref="IslandPartition"/>.</summary>
/// <param name="SlackBus">The elected reference bus.</param>
/// <param name="LoadMw">The island's nominal demand (sum of Pd).</param>
/// <param name="ScheduledGenMw">The island's scheduled generation (sum of Pg of its in-service units).</param>
/// <param name="ElectedPgMw">The scheduled output of the units on the elected bus.</param>
/// <param name="ElectedPminMw">The summed Pmin of the units on the elected bus.</param>
/// <param name="ElectedPmaxMw">The summed Pmax of the units on the elected bus.</param>
public sealed record ElectedIsland(int SlackBus, double LoadMw, double ScheduledGenMw, double ElectedPgMw,
    double ElectedPminMw, double ElectedPmaxMw)
{
    /// <summary>Load minus scheduled generation: what the elected bus absorbs on top of its schedule (before losses).</summary>
    public double ImbalanceMw => LoadMw - ScheduledGenMw;

    /// <summary>The elected units' output the island needs: their schedule plus the imbalance (before losses).</summary>
    public double RequiredPgMw => ElectedPgMw + ImbalanceMw;

    /// <summary>True when <see cref="RequiredPgMw"/> lies outside [Pmin, Pmax] of the elected units.</summary>
    public bool BeyondCapability => RequiredPgMw > ElectedPmaxMw || RequiredPgMw < ElectedPminMw;
}

/// <summary>
/// Makes a network with out-of-service branches solvable. A branch opening can split the grid into
/// islands; each island needs exactly one reference (slack) bus or its power flow is singular. This
/// pre-pass detects the connected components over the IN-SERVICE branches and, per island: keeps it if it
/// already has a slack; ELECTS its largest generator bus as the reference if it has generation but no
/// slack; and BLACKS IT OUT (drops its buses/branches from the solve, booking the lost load/gen) if it has
/// no generation. With every branch in service this is a single component containing the original slack -
/// a pure no-op, so a fully-connected model is unchanged (bit-identical solve).
/// </summary>
public static class IslandPartition
{
    public static (GridModel Model, IslandReport Report) Prepare(GridModel model)
    {
        int n = model.Buses.Count;
        var idOf = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idOf[model.Buses[i].Id] = i;

        // Union-find over bus indices, joined by in-service branches.
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
        foreach (Branch br in model.Branches)
            if (br.InService && idOf.TryGetValue(br.From, out int fi) && idOf.TryGetValue(br.To, out int ti))
                Union(fi, ti);

        // Group bus indices by component root.
        var components = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!components.TryGetValue(r, out List<int>? list)) components[r] = list = new List<int>();
            list.Add(i);
        }

        // Single component with a slack (the common case, incl. everything in service): no change.
        if (components.Count == 1 && model.Buses.Any(b => b.Type == BusType.Slack))
            return (model, Empty(1));

        // Generation per bus index.
        var genOnBus = new Dictionary<int, List<Generator>>();
        foreach (Generator g in model.Generators)
            if (idOf.TryGetValue(g.Bus, out int bi))
            {
                if (!genOnBus.TryGetValue(bi, out List<Generator>? gl)) genOnBus[bi] = gl = new List<Generator>();
                gl.Add(g);
            }

        var elected = new List<int>();
        var electedIslands = new List<ElectedIsland>();
        var deadBusIdx = new HashSet<int>();
        double lostLoad = 0, lostGen = 0;
        int energized = 0, blackedOut = 0;

        foreach (List<int> comp in components.Values)
        {
            bool hasSlack = comp.Any(i => model.Buses[i].Type == BusType.Slack);
            bool hasGen = comp.Any(i => genOnBus.ContainsKey(i) && genOnBus[i].Sum(g => Math.Abs(g.Pg)) > 0);
            // Bug hunt 2026-10-29, finding 8 (EE decision): an island whose in-service units are all scheduled at 0 MW
            // but which can produce real power (Pmax > 0) is NOT dead: those units are synchronised and pick the
            // island's load up through their governors. It used to be blacked out (its load booked as lost, its units
            // dropped from the solved model). A unit with no real-power capability (a synchronous condenser, Pmax <= 0)
            // still cannot hold an island's frequency, so an island with only such units stays blacked out. Any island
            // with a non-zero schedule is decided exactly as before.
            bool canGenerate = !hasGen && comp.Any(i => genOnBus.ContainsKey(i) && genOnBus[i].Any(g => g.Pmax > 0));

            if (hasSlack) { energized++; continue; }

            if (hasGen || canGenerate)
            {
                // Elect the bus carrying the largest single generator as this island's reference; in an island
                // scheduled at 0 MW (finding 8), the bus with the largest real-power capability.
                int best = hasGen
                    ? comp.Where(genOnBus.ContainsKey).OrderByDescending(i => genOnBus[i].Max(g => Math.Abs(g.Pg))).First()
                    : comp.Where(genOnBus.ContainsKey).OrderByDescending(i => genOnBus[i].Max(g => g.Pmax)).First();
                elected.Add(model.Buses[best].Id);
                // GR-055: record the island's balance at its schedule and the elected units' range, in model order.
                double load = 0, sched = 0;
                foreach (int i in comp)
                {
                    load += model.Buses[i].Pd;
                    if (genOnBus.TryGetValue(i, out List<Generator>? units)) foreach (Generator g in units) sched += g.Pg;
                }
                double ePg = 0, ePmin = 0, ePmax = 0;
                foreach (Generator g in genOnBus[best]) { ePg += g.Pg; ePmin += g.Pmin; ePmax += g.Pmax; }
                electedIslands.Add(new ElectedIsland(model.Buses[best].Id, load, sched, ePg, ePmin, ePmax));
                energized++;
            }
            else
            {
                // Dead island: no source to hold voltage/frequency -> de-energised.
                blackedOut++;
                foreach (int i in comp)
                {
                    deadBusIdx.Add(i);
                    lostLoad += model.Buses[i].Pd;
                    if (genOnBus.TryGetValue(i, out List<Generator>? gl)) lostGen += gl.Sum(g => g.Pg);
                }
            }
        }

        if (elected.Count == 0 && deadBusIdx.Count == 0)
            return (model, new IslandReport(components.Count, energized, blackedOut,
                Array.Empty<int>(), Array.Empty<int>(), 0, 0));

        // Rebuild the model: promote elected slack buses, drop dead-island buses + their branches + generators.
        var electedIds = new HashSet<int>(elected);
        var deadIds = new HashSet<int>(deadBusIdx.Select(i => model.Buses[i].Id));

        var buses = new List<Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses)
        {
            if (deadIds.Contains(b.Id)) continue;
            buses.Add(electedIds.Contains(b.Id) ? b with { Type = BusType.Slack } : b);
        }
        // N094: the whole fleet minus the units on dead buses (the setter holds the out-of-service ones back from
        // Generators, F16), so an out-of-service unit on a live bus is kept for a writer and none is left dangling on a
        // dropped bus. Generators is the same in-service units, in the same order, as before.
        var gens = model.AllGenerators.Where(g => !deadIds.Contains(g.Bus)).ToList();
        var branches = model.Branches.Where(br => !deadIds.Contains(br.From) && !deadIds.Contains(br.To)).ToList();

        // GR-054: records that name a removed branch or bus. An OLTC whose transformer left with the dead island (every
        // branch of that name was removed) or whose controlled bus is dead has nothing to regulate and is dropped; a
        // record that never matched a branch is kept as it was (Validate reports it). A boundary branch whose name now
        // matches no branch was de-energised (no flow), so it leaves the boundary; it used to stay and make every
        // boundary evaluation of the adjusted model throw "matches no branch". A boundary left with no branch is dropped.
        // Old branch index -> new branch index (or -1 when the branch left with a dead island), for the records that
        // address branches by position (the zero-sequence mutuals).
        var branchIndexMap = new int[model.Branches.Count];
        for (int bi = 0, next = 0; bi < model.Branches.Count; bi++)
        {
            Branch br = model.Branches[bi];
            branchIndexMap[bi] = !deadIds.Contains(br.From) && !deadIds.Contains(br.To) ? next++ : -1;
        }
        var namesBefore = new HashSet<string>(model.Branches.Where(br => br.Name is not null).Select(br => br.Name!), StringComparer.Ordinal);
        var namesAfter = new HashSet<string>(branches.Where(br => br.Name is not null).Select(br => br.Name!), StringComparer.Ordinal);
        bool Removed(string name) => namesBefore.Contains(name) && !namesAfter.Contains(name);
        var oltcs = model.Oltcs.Where(o => !deadIds.Contains(o.ControlsBus) && !Removed(o.Transformer)).ToList();
        var boundaries = new List<Boundary>(model.Boundaries.Count);
        foreach (Boundary bd in model.Boundaries)
        {
            var kept = bd.Branches.Where(bb => !Removed(bb.Branch)).ToList();
            if (kept.Count == bd.Branches.Count) boundaries.Add(bd);
            else if (kept.Count > 0) boundaries.Add(bd with { Branches = kept });
        }

        // Coupled devices (HVDC links, FACTS devices, static var compensators, induction machines, transformer flow
        // controls, zero-sequence mutuals). This copy used to be built by hand with only the plain-AC parts listed, so
        // every one of these collections came back EMPTY whenever an island needed an elected reference or a blackout:
        // the Newton engines then solved a plain AC network (every converter, SVC, FACTS source and induction load
        // gone) and reported Converged. Now every device is carried, except one that touches a de-energised bus: a
        // converter, compensator or machine whose AC bus is dead cannot operate (an HVDC link with one end dead is
        // blocked, so neither end injects), so it leaves the solve with its island. Each one dropped is NAMED in the
        // report (DroppedDevices) so the loss is never silent.
        var droppedDevices = new List<string>();
        bool Dead(int bus) => deadIds.Contains(bus);
        List<T> Keep<T>(IReadOnlyList<T> items, Func<T, bool> touchesDead, Func<T, string> describe)
        {
            var kept = new List<T>(items.Count);
            foreach (T item in items)
            {
                if (touchesDead(item)) droppedDevices.Add(describe(item));
                else kept.Add(item);
            }
            return kept;
        }
        var lcc = Keep(model.LccLinks, l => Dead(l.Rectifier.Bus) || Dead(l.Inverter.Bus), l => $"LCC DC link '{l.Name}'");
        var vsc = Keep(model.VscLinks, l => Dead(l.First.Bus) || Dead(l.Second.Bus), l => $"VSC DC link '{l.Name}'");
        var mtdc = Keep(model.MtdcLinks, l => l.Converters.Any(c => Dead(c.Bus)) || l.DcBuses.Any(d => d.AcBus != 0 && Dead(d.AcBus)),
            l => $"multi-terminal DC link '{l.Name}'");
        var facts = Keep(model.FactsDevices, f => Dead(f.SendingBus) || (f.TerminalBus != 0 && Dead(f.TerminalBus)),
            f => $"FACTS device '{f.Name}'");
        var svcs = Keep(model.StaticVarCompensators, s => Dead(s.Bus), s => $"static var compensator '{s.Name}'");
        var induction = Keep(model.InductionMachines, m => Dead(m.Bus), m => $"induction machine '{m.Name}'");
        var flowControls = Keep(model.TransformerFlowControls, c => Dead(c.FromBus) || Removed(c.Transformer),
            c => $"transformer flow control on '{c.Transformer}'");
        // Zero-sequence mutuals address branches by position, so a kept one is re-pointed at its branches' new indices.
        var mutuals = new List<ZeroSequenceMutual>(model.ZeroSequenceMutuals.Count);
        foreach (ZeroSequenceMutual zm in model.ZeroSequenceMutuals)
        {
            int a = zm.BranchA >= 0 && zm.BranchA < branchIndexMap.Length ? branchIndexMap[zm.BranchA] : zm.BranchA;
            int b = zm.BranchB >= 0 && zm.BranchB < branchIndexMap.Length ? branchIndexMap[zm.BranchB] : zm.BranchB;
            if (a == -1 || b == -1)
                droppedDevices.Add($"zero-sequence mutual between branches {zm.BranchA} and {zm.BranchB}");
            else
                mutuals.Add(zm with { BranchA = a, BranchB = b });
        }

        // R270: copy through GridModel.CopyWith so every other model property is carried (the defect class this copy
        // was a late member of). The fleet passed is the WHOLE fleet minus the units on dead buses (N094 above).
        GridModel adjusted = model.CopyWith(
            buses: buses,
            generators: gens,
            branches: branches,
            oltcs: oltcs,
            boundaries: boundaries,
            lccLinks: lcc,
            vscLinks: vsc,
            mtdcLinks: mtdc,
            factsDevices: facts,
            staticVarCompensators: svcs,
            inductionMachines: induction,
            transformerFlowControls: flowControls,
            zeroSequenceMutuals: mutuals,
            // Units on dropped buses leave the fleet, so file positions shift: count them with any the loader skipped.
            droppedGeneratorRows: model.DroppedGeneratorRows + (model.AllGenerators.Count - gens.Count),
            generatorsAreWholeFleet: true);
        return (adjusted, new IslandReport(components.Count, energized, blackedOut,
            elected, deadIds.ToList(), lostLoad, lostGen) { ElectedIslands = electedIslands, DroppedDevices = droppedDevices });
    }

    private static IslandReport Empty(int islands) =>
        new(islands, islands, 0, Array.Empty<int>(), Array.Empty<int>(), 0, 0);
}
