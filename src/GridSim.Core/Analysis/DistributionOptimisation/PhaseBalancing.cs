using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Distribution.Meshed;

namespace GridSim.Core.Analysis.DistributionOptimisation;

/// <summary>A single-phase load that may be moved, and the phases it may go to (null: every balancing phase at its node).</summary>
public sealed record MovableLoad(string Name, IReadOnlyList<string>? AllowedPhases = null);

/// <summary>The settings of <see cref="PhaseBalancing.Run"/>.</summary>
public sealed record PhaseBalanceOptions
{
    /// <summary>The loads that may move, in order; null for every single-phase load of the case (model order).</summary>
    public IReadOnlyList<MovableLoad>? Movable { get; init; }

    /// <summary>The phase wires balanced, in order (default a, b, c). The order is the order of the assignment vector.</summary>
    public IReadOnlyList<string> Phases { get; init; } = ["a", "b", "c"];

    /// <summary>The feeder-head branch whose neutral current is reported; null for the one branch at a source node.</summary>
    public string? HeadBranch { get; init; }

    /// <summary>The neutral wire of the head branch (default n).</summary>
    public string NeutralWire { get; init; } = "n";

    /// <summary>The settings of the two verification solves (before and after).</summary>
    public MeshedOptions Solver { get; init; } = new();
}

/// <summary>One movable load: where it was, where it goes and the real power it draws at nominal voltage (W).</summary>
public sealed record PhaseLoadMove(string Load, string FromPhase, string ToPhase, double PowerW);

/// <summary>The result of <see cref="PhaseBalancing.Run"/>.</summary>
/// <param name="Moves">Every movable load, in order, with its chosen phase.</param>
/// <param name="Phases">The balancing phases in order.</param>
/// <param name="PhasePowerBeforeW">The proxy phase powers before (W), in phase order.</param>
/// <param name="PhasePowerAfterW">The proxy phase powers after (W), in phase order.</param>
/// <param name="ProxyBeforeW">The proxy objective before: the largest deviation of a phase power from their mean (W).</param>
/// <param name="ProxyAfterW">The proxy objective after (W).</param>
/// <param name="Method">"exhaustive" (at most <see cref="PhaseBalancing.MaxExhaustiveAssignments"/> assignments) or "greedy".</param>
/// <param name="ProxyEvaluations">Assignments whose proxy was evaluated.</param>
/// <param name="HeadBranch">The head branch.</param>
/// <param name="HeadNode">The head branch's node away from the source, where the unbalance factor is measured.</param>
/// <param name="StatusBefore">The verification solve before.</param>
/// <param name="StatusAfter">The verification solve after.</param>
/// <param name="NeutralBeforeA">|I| in the head branch's neutral before (A); NaN when that solve did not converge.</param>
/// <param name="NeutralAfterA">|I| in the head branch's neutral after (A).</param>
/// <param name="VufBefore">The voltage unbalance factor |V2| / |V1| at the head node before (NaN when not three phases).</param>
/// <param name="VufAfter">The voltage unbalance factor at the head node after.</param>
/// <param name="Model">The case with the chosen assignment.</param>
/// <param name="Notes">Recorded decisions.</param>
public sealed record PhaseBalanceResult(
    IReadOnlyList<PhaseLoadMove> Moves, IReadOnlyList<string> Phases, IReadOnlyList<double> PhasePowerBeforeW,
    IReadOnlyList<double> PhasePowerAfterW, double ProxyBeforeW, double ProxyAfterW, string Method, int ProxyEvaluations,
    string HeadBranch, string HeadNode, MeshedStatus StatusBefore, MeshedStatus StatusAfter, double NeutralBeforeA,
    double NeutralAfterA, double VufBefore, double VufAfter, PhaseFrameModel Model, IReadOnlyList<string> Notes);

/// <summary>
/// Phase balancing of a three-phase (phase-frame) feeder by moving single-phase loads between phases.
/// <para>A single-phase load is a load of one leg from a balancing phase wire to a wire that is not one (the neutral) or
/// to earth. Moving it changes only the phase wire of its leg.</para>
/// <para>Proxy objective: the largest deviation of a phase's real power from the mean of the phases. A phase's power is
/// the nominal real power of every leg from it to a non-phase wire or earth, plus half of every leg between it and another
/// balancing phase (fixed loads included; other legs are left out). The load scale factors of the solver are not applied.
/// No power flow is needed for the proxy.</para>
/// <para>Method: when the number of assignments (the product over the movable loads of their allowed phase counts) is at
/// most <see cref="MaxExhaustiveAssignments"/> (3^12 = 531441), every assignment in lexicographic order of the assignment
/// vector (phase indices in <see cref="PhaseBalanceOptions.Phases"/> order); an assignment replaces the incumbent only
/// when its proxy is lower by more than 1e-12 of the total load power, so the lexicographically smallest optimal
/// assignment wins. Above that, greedy: the movable loads by the size of their real power, largest first (ties in load
/// order), each to its allowed phase of least power so far, or, for a load that generates (negative real power), of most
/// power so far (ties to the first phase). Then one
/// <see cref="Newton3Ph"/> solve before and one after; the neutral current and the unbalance factor reported come from
/// those solves, not from the proxy.</para>
/// </summary>
public static class PhaseBalancing
{
    /// <summary>The most assignments searched exhaustively (3^12: twelve loads free on three phases).</summary>
    public const int MaxExhaustiveAssignments = 531441;

    /// <summary>Run phase balancing on <paramref name="model"/> (which is not changed). Throws <see cref="ArgumentException"/>
    /// for a load that cannot move, an unknown phase, wire or branch, or a head branch that cannot be found.</summary>
    public static PhaseBalanceResult Run(PhaseFrameModel model, PhaseBalanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        PhaseBalanceOptions opt = options ?? new PhaseBalanceOptions();
        IReadOnlyList<string> phases = opt.Phases;
        if (phases.Count < 2) throw new ArgumentException("Phase balancing: at least two phases are needed.", nameof(options));
        if (phases.Distinct(StringComparer.Ordinal).Count() != phases.Count) throw new ArgumentException("Phase balancing: a phase is listed twice.", nameof(options));
        int np = phases.Count;
        int PhaseOf(string? w) { for (int p = 0; p < np; p++) if (phases[p] == w) return p; return -1; }

        var nodeWires = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (FrameNode n in model.Nodes) nodeWires[n.Name] = n.Wires;
        var loadIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < model.Loads.Count; i++)
            if (!loadIndex.TryAdd(model.Loads[i].Name, i)) throw new ArgumentException($"Phase balancing: two loads are named '{model.Loads[i].Name}'.", nameof(model));

        bool SinglePhase(FrameLoad l) => l.Legs.Count == 1 && l.S.Count == 1
            && (l.Connection is FrameConnection.Wye or FrameConnection.WyeEarthed or FrameConnection.LineLine)
            && PhaseOf(l.Legs[0].From) >= 0 && PhaseOf(l.Legs[0].To) < 0;

        // The movable loads and their allowed phase indices (ascending).
        IReadOnlyList<MovableLoad> movable = opt.Movable
            ?? model.Loads.Where(SinglePhase).Select(l => new MovableLoad(l.Name)).ToArray();
        if (movable.Count == 0) throw new ArgumentException("Phase balancing: there is no single-phase load to move.", nameof(options));
        int nm = movable.Count;
        var mIdx = new int[nm];
        var allowed = new int[nm][];
        var isMovable = new bool[model.Loads.Count];
        for (int j = 0; j < nm; j++)
        {
            MovableLoad mv = movable[j];
            if (!loadIndex.TryGetValue(mv.Name, out int li)) throw new ArgumentException($"Phase balancing: there is no load '{mv.Name}'.", nameof(options));
            if (isMovable[li]) throw new ArgumentException($"Phase balancing: load '{mv.Name}' is listed twice.", nameof(options));
            FrameLoad l = model.Loads[li];
            if (!SinglePhase(l))
                throw new ArgumentException($"Phase balancing: load '{mv.Name}' is not a single-phase load (one leg from a phase wire to the neutral or earth), so it cannot be moved.", nameof(options));
            IReadOnlyList<string> wires = nodeWires[l.Node];
            List<int> ok;
            if (mv.AllowedPhases is { } given)
            {
                ok = new List<int>();
                foreach (string w in given)
                {
                    int p = PhaseOf(w);
                    if (p < 0) throw new ArgumentException($"Phase balancing: load '{mv.Name}': '{w}' is not one of the balancing phases.", nameof(options));
                    if (!wires.Contains(w)) throw new ArgumentException($"Phase balancing: load '{mv.Name}': node '{l.Node}' has no wire '{w}'.", nameof(options));
                    if (ok.Contains(p)) throw new ArgumentException($"Phase balancing: load '{mv.Name}': phase '{w}' is allowed twice.", nameof(options));
                    ok.Add(p);
                }
                ok.Sort();
            }
            else
            {
                ok = new List<int>();
                for (int p = 0; p < np; p++) if (wires.Contains(phases[p])) ok.Add(p);
            }
            if (ok.Count == 0) throw new ArgumentException($"Phase balancing: load '{mv.Name}' has no allowed phase.", nameof(options));
            isMovable[li] = true;
            mIdx[j] = li;
            allowed[j] = ok.ToArray();
        }

        // Fixed phase powers (every leg of every load that is not movable) and the movable powers.
        var fixedW = new double[np];
        double totalW = 0;
        for (int i = 0; i < model.Loads.Count; i++)
        {
            if (isMovable[i]) continue;
            FrameLoad l = model.Loads[i];
            for (int g = 0; g < l.Legs.Count && g < l.S.Count; g++)
            {
                int a = PhaseOf(l.Legs[g].From), b = PhaseOf(l.Legs[g].To);
                double pw = l.S[g].Real;
                if (a >= 0 && b < 0) fixedW[a] += pw;
                else if (a < 0 && b >= 0) fixedW[b] += pw;
                else if (a >= 0 && b >= 0) { fixedW[a] += 0.5 * pw; fixedW[b] += 0.5 * pw; }
                else continue;
                totalW += Math.Abs(pw);
            }
        }
        var loadW = new double[nm];
        var startPhase = new int[nm];
        for (int j = 0; j < nm; j++)
        {
            FrameLoad l = model.Loads[mIdx[j]];
            loadW[j] = l.S[0].Real;
            startPhase[j] = PhaseOf(l.Legs[0].From);
            totalW += Math.Abs(loadW[j]);
        }
        double tie = 1e-12 * totalW;

        double[] Sums(int[] asg)
        {
            var s = (double[])fixedW.Clone();
            for (int j = 0; j < nm; j++) s[asg[j]] += loadW[j];
            return s;
        }
        double Proxy(double[] s)
        {
            double mean = 0;
            for (int p = 0; p < np; p++) mean += s[p];
            mean /= np;
            double d = 0;
            for (int p = 0; p < np; p++) d = Math.Max(d, Math.Abs(s[p] - mean));
            return d;
        }

        double[] before = Sums(startPhase);
        int[] chosen;
        int evals = 0;
        string method;
        long assignments = 1;
        for (int j = 0; j < nm && assignments <= MaxExhaustiveAssignments; j++) assignments *= allowed[j].Length;
        if (assignments <= MaxExhaustiveAssignments)
        {
            method = "exhaustive";
            var digit = new int[nm];
            var asg = new int[nm];
            chosen = new int[nm];
            double best = double.PositiveInfinity;
            while (true)
            {
                for (int j = 0; j < nm; j++) asg[j] = allowed[j][digit[j]];
                double v = Proxy(Sums(asg));
                evals++;
                if (v < best - tie) { best = v; Array.Copy(asg, chosen, nm); }
                int k = nm - 1;
                while (k >= 0 && ++digit[k] == allowed[k].Length) { digit[k] = 0; k--; }
                if (k < 0) break;
            }
        }
        else
        {
            method = "greedy";
            chosen = new int[nm];
            var s = (double[])fixedW.Clone();
            int[] order = Enumerable.Range(0, nm).OrderByDescending(j => Math.Abs(loadW[j])).ToArray();
            foreach (int j in order)
            {
                // A load goes to the lightest phase; a generating load (negative power) to the heaviest, which it relieves.
                int bestP = allowed[j][0];
                bool generating = loadW[j] < 0;
                foreach (int p in allowed[j])
                    if (generating ? s[p] > s[bestP] : s[p] < s[bestP]) bestP = p;
                chosen[j] = bestP;
                s[bestP] += loadW[j];
                evals++;
            }
        }
        double[] after = Sums(chosen);

        // The case with the chosen phases.
        var loads = model.Loads.ToArray();
        var moves = new PhaseLoadMove[nm];
        for (int j = 0; j < nm; j++)
        {
            FrameLoad l = loads[mIdx[j]];
            string to = phases[chosen[j]];
            moves[j] = new PhaseLoadMove(l.Name, l.Legs[0].From, to, loadW[j]);
            if (to != l.Legs[0].From) loads[mIdx[j]] = l with { Legs = [new FrameLeg(to, l.Legs[0].To)] };
        }
        var moved = new PhaseFrameModel(model.Name, model.Nodes, model.Sources, model.Branches, loads, model.Earthing, model.Grids, model.Transformers);

        // The head branch and its far node.
        var sourceNodes = new HashSet<string>(model.Sources.Select(s => s.Node), StringComparer.Ordinal);
        FrameBranch head;
        if (opt.HeadBranch is { } hb)
        {
            head = model.Branches.FirstOrDefault(b => b.Name == hb)
                ?? throw new ArgumentException($"Phase balancing: there is no branch '{hb}'.", nameof(options));
        }
        else
        {
            FrameBranch[] atSource = model.Branches.Where(b => sourceNodes.Contains(b.From) || sourceNodes.Contains(b.To)).ToArray();
            if (atSource.Length != 1)
                throw new ArgumentException($"Phase balancing: {atSource.Length} branches touch a source node, so the head branch must be named.", nameof(options));
            head = atSource[0];
        }
        if (!head.Wires.Contains(opt.NeutralWire))
            throw new ArgumentException($"Phase balancing: the head branch '{head.Name}' has no wire '{opt.NeutralWire}', so its neutral current cannot be reported.", nameof(options));
        string headNode = sourceNodes.Contains(head.From) ? head.To : head.From;

        var notes = new List<string>();
        if (opt.Solver.GlobalLoadScale != 1.0 || opt.Solver.LoadScale is { Count: > 0 })
            notes.Add("the proxy uses the nominal load powers; the solver's load scale factors apply to the two verification solves only.");
        MeshedResult r0 = Newton3Ph.Solve(model, opt.Solver);
        MeshedResult r1 = Newton3Ph.Solve(moved, opt.Solver);
        (double i0, double u0) = Measure(r0, head, headNode, opt.NeutralWire, phases, nodeWires[headNode]);
        (double i1, double u1) = Measure(r1, head, headNode, opt.NeutralWire, phases, nodeWires[headNode]);
        if (np != 3) notes.Add("the voltage unbalance factor needs exactly three phases; it is not reported.");

        return new PhaseBalanceResult(moves, phases, before, after, Proxy(before), Proxy(after), method, evals, head.Name, headNode,
            r0.Status, r1.Status, i0, i1, u0, u1, moved, notes);
    }

    private static double Mag(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);

    /// <summary>The head-branch neutral current magnitude (A) and the unbalance factor |V2| / |V1| at the head node (phase
    /// voltages to the neutral when the node has it, else to earth); NaN for a solve that did not converge.</summary>
    private static (double NeutralA, double Vuf) Measure(MeshedResult r, FrameBranch head, string headNode, string neutral,
        IReadOnlyList<string> phases, IReadOnlyList<string> nodeWires)
    {
        if (r.Status != MeshedStatus.Converged) return (double.NaN, double.NaN);
        BranchCurrents bc = r.Branch(head.Name);
        int w = -1;
        for (int k = 0; k < bc.Wires.Count; k++) if (bc.Wires[k] == neutral) { w = k; break; }
        double iN = w >= 0 ? Mag(bc.IFrom[w]) : double.NaN;
        double vuf = double.NaN;
        if (phases.Count == 3 && phases.All(p => nodeWires.Contains(p)))
        {
            Complex vn = nodeWires.Contains(neutral) ? r.V(headNode, neutral) : Complex.Zero;
            Complex[] seq = SymmetricalComponents.ToSequence([r.V(headNode, phases[0]) - vn, r.V(headNode, phases[1]) - vn, r.V(headNode, phases[2]) - vn]);
            double v1 = Mag(seq[1]);
            vuf = v1 > 0 ? Mag(seq[2]) / v1 : double.NaN;
        }
        return (iN, vuf);
    }
}
