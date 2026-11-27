using System.Globalization;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.Reconfiguration;

/// <summary>The settings of <see cref="FeederReconfiguration.Run"/>.</summary>
public sealed record ReconfigOptions
{
    /// <summary>The switchable branches (indices into <c>model.Branches</c>), or null for every branch that is not a
    /// transformer.</summary>
    public IReadOnlyList<int>? Switchable { get; init; }

    /// <summary>Reject a configuration with a branch above its RateA (at either end).</summary>
    public bool RespectRatings { get; init; }

    /// <summary>Stop when the best swap saves less than this (MW; default 1e-6).</summary>
    public double MinGainMw { get; init; } = 1e-6;

    /// <summary>The most swaps to make (default 100); reaching it is reported.</summary>
    public int MaxSwaps { get; init; } = 100;
}

/// <summary>How <see cref="FeederReconfiguration.Run"/> ended.</summary>
public enum ReconfigStatus
{
    /// <summary>No swap saves at least the minimum gain: a local optimum of branch exchange.</summary>
    LocalOptimum,
    /// <summary>The swap cap was reached.</summary>
    SwapLimit,
}

/// <summary>One swap: branch <see cref="Closed"/> closed, branch <see cref="Opened"/> opened, and the losses after it.</summary>
public sealed record ReconfigSwap(int Closed, int Opened, double LossMw);

/// <summary>The result of <see cref="FeederReconfiguration.Run"/>: the swaps in order, the final open branches (index
/// order), the base and final losses, the final minimum voltage and how it ended.</summary>
public sealed record ReconfigResult(
    IReadOnlyList<ReconfigSwap> Swaps, IReadOnlyList<int> OpenBranches, double BaseLossMw, double LossMw, double VminPu,
    ReconfigStatus Status, string Message);

/// <summary>
/// Distribution feeder reconfiguration for loss reduction by branch exchange, best improvement (Civanlar, Grainger, Yin
/// and Lee, IEEE Trans. PWRD 3(3), 1988; Baran and Wu, IEEE Trans. PWRD 4(2), 1989).
/// <para>Start: the case's in-service set, which must be radial and connected. One pass: for each open switchable
/// branch c in index order, and each in-service switchable branch o on the tree path between c's ends in index order,
/// the swap (close c, open o) is solved with <see cref="BackwardForwardSweep"/> (100 iterations, reactive limits on, as
/// the <c>pf</c> radial route) and rejected if the sweep does not converge, a bus is below its VminPu (else
/// <see cref="VoltageLimits.OperationalLowPu"/>) or, with <see cref="ReconfigOptions.RespectRatings"/>, a branch is above
/// its RateA. The swap of lowest loss is taken; a later candidate replaces the incumbent only when lower by more than
/// 1e-9 MW, so ties go to the lower c, then the lower o. Stop when the best gain is below the minimum or at the swap cap.
/// Branch exchange is a local search: the global optimum is not guaranteed.</para>
/// </summary>
public static class FeederReconfiguration
{
    /// <summary>A candidate replaces the incumbent only when its loss is lower by more than this (MW).</summary>
    public const double TieMw = 1e-9;

    /// <summary>Run the branch exchange on <paramref name="model"/>. Throws <see cref="InvalidDataException"/> for a start
    /// that is not radial and connected, and <see cref="ArgumentException"/> for a bad switchable list or option.</summary>
    public static ReconfigResult Run(GridModel model, ReconfigOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ReconfigOptions opt = options ?? new ReconfigOptions();
        if (!(opt.MinGainMw >= 0) || !double.IsFinite(opt.MinGainMw))
            throw new ArgumentOutOfRangeException(nameof(options), opt.MinGainMw, "Reconfiguration: the minimum gain must be a finite number of 0 MW or more.");
        if (opt.MaxSwaps < 0) throw new ArgumentOutOfRangeException(nameof(options), opt.MaxSwaps, "Reconfiguration: the swap cap must be 0 or more.");

        int nb = model.BusCount, nbr = model.Branches.Count;
        var index = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) index[model.Buses[i].Id] = i;
        int root = -1;
        for (int i = 0; i < nb; i++) if (model.Buses[i].Type == BusType.Slack) { root = i; break; }
        if (root < 0) throw new InvalidDataException("Reconfiguration: the feeder has no slack (source) bus.");

        var switchable = new bool[nbr];
        if (opt.Switchable is { } list)
        {
            foreach (int k in list)
            {
                if (k < 0 || k >= nbr) throw new ArgumentException($"Reconfiguration: switchable branch {k} is not a branch of the case (0..{nbr - 1}).", nameof(options));
                if (switchable[k]) throw new ArgumentException($"Reconfiguration: switchable branch {k} is listed twice.", nameof(options));
                if (model.Branches[k].IsTransformer) throw new ArgumentException($"Reconfiguration: branch {k} ({Name(model, k)}) is a transformer, which is not a switch.", nameof(options));
                switchable[k] = true;
            }
        }
        else
            for (int k = 0; k < nbr; k++) switchable[k] = !model.Branches[k].IsTransformer;

        // The start must be a spanning tree of the in-service branches.
        var inService = new bool[nbr];
        for (int k = 0; k < nbr; k++) inService[k] = model.Branches[k].InService;
        CheckRadial(model, index, inService);

        var pfOptions = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true };
        (bool ok0, double loss0, double vmin0) = Evaluate(model, inService, false, pfOptions, checkVoltage: false);
        if (!ok0) throw new InvalidDataException("Reconfiguration: the starting configuration does not solve (the sweep does not converge).");
        // ANA-26: whether the start itself breaks a bus voltage limit (it is evaluated without the voltage check, every swap
        // with it), so the message can say that swaps were refused on voltage rather than call the start a local optimum.
        bool startBelowLimit = !Evaluate(model, inService, false, pfOptions, checkVoltage: true).Item1;

        var swaps = new List<ReconfigSwap>();
        double loss = loss0, vmin = vmin0;
        ReconfigStatus status = ReconfigStatus.LocalOptimum;
        while (true)
        {
            if (swaps.Count >= opt.MaxSwaps) { status = ReconfigStatus.SwapLimit; break; }
            int[] parentBranch = Tree(model, index, inService, root, out int[] parent, out int[] depth);
            int bestC = -1, bestO = -1;
            double bestLoss = double.PositiveInfinity, bestVmin = double.NaN;
            for (int c = 0; c < nbr; c++)
            {
                if (inService[c] || !switchable[c]) continue;
                Branch bc = model.Branches[c];
                List<int> path = PathBranches(index[bc.From], index[bc.To], parentBranch, parent, depth);
                path.Sort();
                foreach (int o in path)
                {
                    if (!switchable[o]) continue;
                    inService[c] = true; inService[o] = false;
                    (bool ok, double l, double vm) = Evaluate(model, inService, opt.RespectRatings, pfOptions);
                    inService[c] = false; inService[o] = true;
                    if (!ok) continue;
                    if (l < bestLoss - TieMw) { bestLoss = l; bestC = c; bestO = o; bestVmin = vm; }
                }
            }
            if (bestC < 0 || !(loss - bestLoss >= opt.MinGainMw)) break;
            inService[bestC] = true; inService[bestO] = false;
            loss = bestLoss; vmin = bestVmin;
            swaps.Add(new ReconfigSwap(bestC, bestO, bestLoss));
        }
        var open = new List<int>();
        for (int k = 0; k < nbr; k++) if (!inService[k]) open.Add(k);
        string msg = status == ReconfigStatus.SwapLimit
            ? $"stopped at the swap cap ({opt.MaxSwaps}); a further swap may still reduce the losses"
            : startBelowLimit && swaps.Count == 0
                ? $"the starting configuration has a bus below its voltage limit, and no single swap brings every bus within it (each swap is refused when any bus is below its limit), so none was made; no swap saves {opt.MinGainMw.ToString("R", CultureInfo.InvariantCulture)} MW or more within the limits"
                : $"no swap saves {opt.MinGainMw.ToString("R", CultureInfo.InvariantCulture)} MW or more (a local optimum of branch exchange)";
        return new ReconfigResult(swaps, open, loss0, loss, vmin, status, msg);
    }

    /// <summary>Solve the configuration <paramref name="inService"/> with the sweep: (accepted, losses MW, minimum Vm).</summary>
    private static (bool Ok, double LossMw, double VminPu) Evaluate(GridModel model, bool[] inService, bool ratings, PowerFlowOptions pfOptions, bool checkVoltage = true)
    {
        var brs = new Branch[model.Branches.Count];
        for (int k = 0; k < brs.Length; k++)
            brs[k] = model.Branches[k].InService == inService[k] ? model.Branches[k] : model.Branches[k] with { InService = inService[k] };
        GridModel m = model.CopyWith(branches: brs);
        PowerFlowResult r = new BackwardForwardSweep().Solve(m, pfOptions);
        if (!r.Converged) return (false, double.NaN, double.NaN);
        double vmin = double.PositiveInfinity;
        var index = new Dictionary<int, int>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) index[r.BusIds[i]] = i;
        foreach (Bus b in model.Buses)
        {
            double v = r.Vm[index[b.Id]];
            if (checkVoltage && v < (b.VminPu ?? VoltageLimits.OperationalLowPu)) return (false, double.NaN, double.NaN);
            vmin = Math.Min(vmin, v);
        }
        if (ratings)
        {
            for (int k = 0; k < brs.Length; k++)
            {
                Branch br = brs[k];
                if (!br.InService || !(br.RateAMva > 0)) continue;
                BranchStamp s = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                int f = index[br.From], t = index[br.To];
                Complex vf = DetMath.FromPolar(r.Vm[f], r.VaDeg[f] * Math.PI / 180.0), vt = DetMath.FromPolar(r.Vm[t], r.VaDeg[t] * Math.PI / 180.0);
                double sf = (vf * Complex.Conjugate(s.Yff * vf + s.Yft * vt)).Magnitude * model.BaseMva;
                double st = (vt * Complex.Conjugate(s.Ytf * vf + s.Ytt * vt)).Magnitude * model.BaseMva;
                if (sf > br.RateAMva || st > br.RateAMva) return (false, double.NaN, double.NaN);
            }
        }
        return (true, r.TotalLossMw, vmin);
    }

    /// <summary>Refuse a start that is not a connected spanning tree: name the branch that closes a loop or a bus that is
    /// not reached.</summary>
    private static void CheckRadial(GridModel model, Dictionary<int, int> index, bool[] inService)
    {
        int nb = model.BusCount;
        var uf = new int[nb];
        for (int i = 0; i < nb; i++) uf[i] = i;
        int Find(int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; }
        for (int k = 0; k < inService.Length; k++)
        {
            if (!inService[k]) continue;
            Branch br = model.Branches[k];
            int a = Find(index[br.From]), b = Find(index[br.To]);
            if (a == b)
                throw new InvalidDataException($"Reconfiguration: the starting configuration is meshed: branch {k} ({Name(model, k)}) closes a loop. Open one branch of each loop first.");
            uf[a] = b;
        }
        int r0 = Find(0);
        for (int i = 1; i < nb; i++)
            if (Find(i) != r0)
                throw new InvalidDataException($"Reconfiguration: the starting configuration is not connected: bus {model.Buses[i].Id} is not connected to bus {model.Buses[0].Id}.");
    }

    /// <summary>The tree of the in-service branches from <paramref name="root"/>: for each bus its parent branch (-1 at
    /// the root), parent bus and depth. Neighbours are taken in branch order, so it is deterministic.</summary>
    private static int[] Tree(GridModel model, Dictionary<int, int> index, bool[] inService, int root, out int[] parent, out int[] depth)
    {
        int nb = model.BusCount;
        var adj = new List<(int Bus, int Branch)>[nb];
        for (int i = 0; i < nb; i++) adj[i] = new List<(int, int)>();
        for (int k = 0; k < inService.Length; k++)
        {
            if (!inService[k]) continue;
            Branch br = model.Branches[k];
            int f = index[br.From], t = index[br.To];
            adj[f].Add((t, k)); adj[t].Add((f, k));
        }
        var pb = new int[nb];
        parent = new int[nb];
        depth = new int[nb];
        Array.Fill(pb, -1);
        Array.Fill(parent, -1);
        var seen = new bool[nb];
        var queue = new Queue<int>();
        seen[root] = true; queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach ((int q, int k) in adj[i])
                if (!seen[q]) { seen[q] = true; parent[q] = i; pb[q] = k; depth[q] = depth[i] + 1; queue.Enqueue(q); }
        }
        return pb;
    }

    /// <summary>The branches on the tree path between buses <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static List<int> PathBranches(int a, int b, int[] parentBranch, int[] parent, int[] depth)
    {
        var path = new List<int>();
        while (depth[a] > depth[b]) { path.Add(parentBranch[a]); a = parent[a]; }
        while (depth[b] > depth[a]) { path.Add(parentBranch[b]); b = parent[b]; }
        while (a != b)
        {
            path.Add(parentBranch[a]); a = parent[a];
            path.Add(parentBranch[b]); b = parent[b];
        }
        return path;
    }

    private static string Name(GridModel model, int k)
    {
        Branch br = model.Branches[k];
        return br.Name is { Length: > 0 } n ? $"{n}, {br.From}-{br.To}" : $"{br.From}-{br.To}";
    }
}
