using GridSim.Core.Abstractions;
using GridSim.Core.Estimation;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// One controlled area of <see cref="AreaInterchangePowerFlow"/>: its number, the bus whose units take the area's
/// adjustment (PSS/E ISW), the net export it should have (PSS/E PDES, MW, positive leaving the area), the band either
/// side of it that counts as met (PSS/E PTOL, MW) and the buses in the area.
/// </summary>
public sealed record AreaInterchangeSpec(int Area, int SwingBus, double DesiredExportMw, double ToleranceMw, IReadOnlyList<int> Buses);

/// <summary>An area whose interchange is reported but not controlled (for example the area holding the system slack).</summary>
public sealed record AreaMembership(int Area, IReadOnlyList<int> Buses);

/// <summary>How an area ended in <see cref="AreaInterchangePowerFlow"/>.</summary>
public enum AreaInterchangeStatus
{
    /// <summary>The export is within the tolerance of the desired value.</summary>
    Ok,
    /// <summary>Every unit at the swing bus reached Pmin or Pmax, so the area was not adjusted further.</summary>
    AtLimit,
    /// <summary>The export is outside the tolerance when the loop stopped (pass cap, or the engine did not converge).</summary>
    NotSettled,
    /// <summary>Not controlled: the interchange is reported only.</summary>
    Reported,
}

/// <summary>One area's outcome: export (MW, positive leaving the area), the desired value and tolerance (null for a
/// reported area), the error export minus desired, the MW added to its swing units, and the status.</summary>
public sealed record AreaInterchangeOutcome(int Area, double ExportMw, double? DesiredExportMw, double? ToleranceMw,
    double? ErrorMw, double AdjustmentMw, AreaInterchangeStatus Status)
{
    /// <summary>The status as printed: OK, AT LIMIT, NOT SETTLED or REPORTED.</summary>
    public string StatusText => Text(Status);

    /// <summary>The printed form of <paramref name="s"/>.</summary>
    public static string Text(AreaInterchangeStatus s) => s switch
    {
        AreaInterchangeStatus.Ok => "OK",
        AreaInterchangeStatus.AtLimit => "AT LIMIT",
        AreaInterchangeStatus.NotSettled => "NOT SETTLED",
        _ => "REPORTED",
    };
}

/// <summary>
/// Area interchange control (tie lines only), as an outer loop around an unchanged engine. Each controlled area holds
/// its net export at the desired value by moving the real power of the units at its swing bus.
///
/// <para><b>Export.</b> E_a is the sum over the tie branches of area a (in-service branches with exactly one end in
/// a) of s_k P_k, where P_k is the active power flowing into the branch at its metered end and s_k is +1 when that end
/// is in a and -1 when it is not. So a tie metered outside a counts the power the other area sends into it, and the
/// tie's losses are borne by the area at the far end from the meter. This is the PSS/E convention (branch MET field;
/// the from end by default).</para>
///
/// <para><b>Loop.</b> Solve; for each controlled area in ascending area number whose |E_a - PDES_a| is above PTOL_a,
/// add PDES_a - E_a to the area's adjustment K_a and share K_a over the swing bus's units in proportion to
/// (Pmax - Pmin), in fleet order (equally when a range is zero or unbounded), within [Pmin, Pmax]; re-solve. Stop when
/// no area needs a change. An area whose units are all at a limit with part of K_a unplaced is AT LIMIT and is not
/// adjusted again. The pass cap is <see cref="DefaultMaxPasses"/>.</para>
///
/// <para>The area holding a slack bus cannot be controlled: the slack absorbs the remainder. Name it in
/// <c>reported</c> to have its interchange printed. The returned result is the last engine solve, unchanged: the loop
/// changes only unit set points.</para>
/// </summary>
public sealed class AreaInterchangePowerFlow : IPowerFlowSolver
{
    /// <summary>Default cap on outer passes.</summary>
    public const int DefaultMaxPasses = 20;

    private readonly IPowerFlowSolver _engine;
    private readonly AreaInterchangeSpec[] _areas;          // ascending area number
    private readonly AreaMembership[] _reported;            // ascending area number
    private readonly IReadOnlyDictionary<int, BranchEnd> _metered;
    private readonly int _maxPasses;

    /// <summary>
    /// Wrap <paramref name="engine"/>. <paramref name="meteredEnds"/> gives the metered end of a branch by its index in
    /// <c>model.Branches</c> (the from end when absent). Each spec is checked here: a finite desired export, a finite
    /// tolerance above 0, at least one bus, the swing bus among them, no area twice and no bus in two areas.
    /// </summary>
    public AreaInterchangePowerFlow(IPowerFlowSolver engine, IReadOnlyList<AreaInterchangeSpec> areas,
        IReadOnlyDictionary<int, BranchEnd>? meteredEnds = null, IReadOnlyList<AreaMembership>? reported = null,
        int maxPasses = DefaultMaxPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(areas);
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass is needed");
        reported ??= Array.Empty<AreaMembership>();
        var seenArea = new HashSet<int>();
        var areaOfBus = new Dictionary<int, int>();
        foreach (AreaInterchangeSpec s in areas)
        {
            ArgumentNullException.ThrowIfNull(s);
            string who = $"area interchange: area {s.Area}";
            if (!seenArea.Add(s.Area)) throw new ArgumentException($"{who} is given twice.");
            if (!double.IsFinite(s.DesiredExportMw)) throw new ArgumentException($"{who}: the desired export must be a finite number of MW.");
            if (!(s.ToleranceMw > 0.0) || !double.IsFinite(s.ToleranceMw)) throw new ArgumentException($"{who}: the tolerance must be a finite number of MW above 0.");
            if (s.Buses is null || s.Buses.Count == 0) throw new ArgumentException($"{who} has no buses.");
            if (!s.Buses.Contains(s.SwingBus)) throw new ArgumentException($"{who}: the swing bus {s.SwingBus} is not one of the area's buses.");
            AddBuses(areaOfBus, s.Area, s.Buses);
        }
        foreach (AreaMembership m in reported)
        {
            ArgumentNullException.ThrowIfNull(m);
            if (!seenArea.Add(m.Area)) throw new ArgumentException($"area interchange: area {m.Area} is given twice.");
            if (m.Buses is null || m.Buses.Count == 0) throw new ArgumentException($"area interchange: area {m.Area} has no buses.");
            AddBuses(areaOfBus, m.Area, m.Buses);
        }
        _engine = engine;
        _areas = areas.OrderBy(a => a.Area).ToArray();
        _reported = reported.OrderBy(a => a.Area).ToArray();
        _metered = meteredEnds ?? new Dictionary<int, BranchEnd>();
        _maxPasses = maxPasses;
    }

    private static void AddBuses(Dictionary<int, int> areaOfBus, int area, IReadOnlyList<int> buses)
    {
        foreach (int b in buses)
            if (areaOfBus.TryGetValue(b, out int other))
                throw new ArgumentException(other == area
                    ? $"area interchange: area {area} lists bus {b} twice."
                    : $"area interchange: bus {b} is in area {other} and area {area}; a bus belongs to one area.");
            else areaOfBus[b] = area;
    }

    /// <summary>Outer passes (engine solves) taken by the last <see cref="Solve"/>.</summary>
    public int LastPasses { get; private set; }

    /// <summary>Every area of the last <see cref="Solve"/>, controlled and reported, in ascending area number.</summary>
    public IReadOnlyList<AreaInterchangeOutcome> LastAreas { get; private set; } = Array.Empty<AreaInterchangeOutcome>();

    /// <summary>Each in-service unit's real output (MW) in the last solve, aligned to <c>model.Generators</c>.</summary>
    public IReadOnlyList<double> LastDispatchMw { get; private set; } = Array.Empty<double>();

    /// <summary>True when every controlled area ended OK or AT LIMIT.</summary>
    public bool Settled { get; private set; }

    /// <summary>Why the last <see cref="Solve"/> did not settle, or which areas are at a limit (null when neither).</summary>
    public string? LastNote { get; private set; }

    /// <summary>Solve <paramref name="model"/> with the area interchanges controlled.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        LastNote = null;
        Settled = false;
        LastAreas = Array.Empty<AreaInterchangeOutcome>();
        int g = model.Generators.Count;
        var busById = new Dictionary<int, Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) busById.TryAdd(b.Id, b);

        // Checks against the model, each naming the area.
        foreach (AreaMembership m in _reported)
            foreach (int b in m.Buses)
                if (!busById.ContainsKey(b)) throw new ArgumentException($"area interchange: area {m.Area} lists bus {b}, which is not in the model.");
        int[][] units = new int[_areas.Length][];
        double[][] alpha = new double[_areas.Length][];
        for (int a = 0; a < _areas.Length; a++)
        {
            AreaInterchangeSpec s = _areas[a];
            foreach (int b in s.Buses)
            {
                if (!busById.TryGetValue(b, out Bus? bus)) throw new ArgumentException($"area interchange: area {s.Area} lists bus {b}, which is not in the model.");
                if (bus.Type == BusType.Slack)
                    throw new ArgumentException($"area interchange: area {s.Area} holds the slack bus {b}, so it cannot be controlled: the system slack absorbs the remainder; PSS/E needs ISW to be the system swing bus there. Report it instead.");
            }
            units[a] = Enumerable.Range(0, g).Where(i => model.Generators[i].Bus == s.SwingBus && !model.Generators[i].PowerFlowOnly).ToArray();
            if (units[a].Length == 0)
                throw new ArgumentException($"area interchange: area {s.Area}: the swing bus {s.SwingBus} has no in-service unit to adjust.");
            foreach (int i in units[a])
            {
                Generator u = model.Generators[i];
                if (u.Pmin > u.Pmax)
                    throw new ArgumentException(FormattableString.Invariant(
                        $"area interchange: area {s.Area}: unit {i} at bus {s.SwingBus} has Pmin {u.Pmin} MW above Pmax {u.Pmax} MW."));
            }
            alpha[a] = Shares(model, units[a]);
        }
        string? dcNote = CrossAreaDcNote(model);
        foreach (int k in _metered.Keys.OrderBy(k => k))
            if (k < 0 || k >= model.Branches.Count)
                throw new ArgumentException($"area interchange: a metered end is given for branch {k}, which is not in the model ({model.Branches.Count} branches).");

        var pg = new double[g];
        for (int i = 0; i < g; i++) pg[i] = model.Generators[i].Pg;
        var k0 = new double[_areas.Length];      // the adjustment placed so far, per area
        var frozen = new bool[_areas.Length];
        PowerFlowResult? r = null;
        double[] exports = new double[_areas.Length];
        bool changedAny = false;
        for (int pass = 1; pass <= _maxPasses; pass++)
        {
            LastPasses = pass;
            GridModel solveModel = model;
            if (changedAny)
            {
                var gens = new Generator[g];
                for (int i = 0; i < g; i++) gens[i] = model.Generators[i] with { Pg = pg[i] };
                solveModel = model.CopyWith(generators: gens);
            }
            r = _engine.Solve(solveModel, options);
            LastDispatchMw = (double[])pg.Clone();
            if (!r.Converged)
            {
                LastNote = Join($"the engine did not converge on pass {pass}", dcNote);
                LastAreas = Outcomes(model, r, k0, frozen, unsettledAll: true);
                return r;
            }
            if (r.LineFlows.Count != model.Branches.Count)
                throw new InvalidOperationException("area interchange: the engine's result does not give one flow per branch.");

            bool changed = false;
            for (int a = 0; a < _areas.Length; a++)
            {
                AreaInterchangeSpec s = _areas[a];
                exports[a] = Export(model, r, s.Buses);
                if (frozen[a] || Math.Abs(exports[a] - s.DesiredExportMw) <= s.ToleranceMw) continue;
                double want = k0[a] + (s.DesiredExportMw - exports[a]);
                int[] idx = units[a];
                double[] p0 = idx.Select(i => model.Generators[i].Pg).ToArray();
                double[] lo = idx.Select(i => model.Generators[i].Pmin).ToArray();
                double[] hi = idx.Select(i => model.Generators[i].Pmax).ToArray();
                (double[] p, double unplaced) = DistributedSlackPowerFlow.Allocate(p0, alpha[a], lo, hi, want);
                if (unplaced != 0.0) { frozen[a] = true; k0[a] = want - unplaced; }
                else k0[a] = want;
                for (int j = 0; j < idx.Length; j++)
                    if (pg[idx[j]] != p[j]) { pg[idx[j]] = p[j]; changed = true; }
            }
            if (!changed)
            {
                LastAreas = Outcomes(model, r, k0, frozen, unsettledAll: false);
                Settled = LastAreas.All(o => o.Status != AreaInterchangeStatus.NotSettled);
                int[] atLimit = LastAreas.Where(o => o.Status == AreaInterchangeStatus.AtLimit).Select(o => o.Area).ToArray();
                if (atLimit.Length > 0) LastNote = "every unit at the swing bus is at a limit in area(s) " + string.Join(", ", atLimit);
                LastNote = Join(LastNote, dcNote);
                return r;
            }
            changedAny = true;
        }
        LastAreas = Outcomes(model, r!, k0, frozen, unsettledAll: false);
        Settled = false;
        LastNote = Join($"the area interchange did not settle within {_maxPasses} passes", dcNote);
        return r!;
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + "; " + b;

    /// <summary>NUM-25: a DC link or FACTS device whose ends lie in different areas (or one end in a controlled area and
    /// one outside every area) carries a transfer that the export, summed over AC branches only, books as load in one area
    /// and generation in the other, not as interchange. The study is not changed; the note names each such element.</summary>
    private string? CrossAreaDcNote(GridModel model)
    {
        var areaOf = new Dictionary<int, int>();
        foreach (AreaInterchangeSpec s in _areas) foreach (int b in s.Buses) areaOf.TryAdd(b, s.Area);
        foreach (AreaMembership m in _reported) foreach (int b in m.Buses) areaOf.TryAdd(b, m.Area);
        if (areaOf.Count == 0) return null;
        int AreaOf(int bus) => areaOf.TryGetValue(bus, out int a) ? a : int.MinValue;
        var names = new List<string>();
        void Check(string kind, string name, IEnumerable<int> buses)
        {
            int[] a = buses.Select(AreaOf).Distinct().ToArray();
            if (a.Length > 1) names.Add($"{kind} '{name}'");
        }
        foreach (LccDcLink l in model.LccLinks) Check("LCC link", l.Name, [l.Rectifier.Bus, l.Inverter.Bus]);
        foreach (VscDcLink l in model.VscLinks) Check("VSC link", l.Name, [l.First.Bus, l.Second.Bus]);
        foreach (MtdcLink l in model.MtdcLinks) Check("multi-terminal DC link", l.Name, l.Converters.Select(c => c.Bus));
        foreach (FactsDevice f in model.FactsDevices) if (f.TerminalBus != 0) Check("FACTS device", f.Name, [f.SendingBus, f.TerminalBus]);
        return names.Count == 0 ? null
            : "the export counts AC branches only, so the transfer of " + string.Join(", ", names)
              + " across an area boundary is booked as load in one area and generation in the other, not as interchange";
    }

    /// <summary>The share of each unit: (Pmax - Pmin) when every range is finite and their sum is above 0, else equal.</summary>
    private static double[] Shares(GridModel model, int[] idx)
    {
        var a = new double[idx.Length];
        double sum = 0.0;
        bool finite = true;
        for (int j = 0; j < idx.Length; j++)
        {
            Generator u = model.Generators[idx[j]];
            a[j] = u.Pmax - u.Pmin;
            if (!double.IsFinite(a[j])) finite = false;
            else sum += a[j];
        }
        if (!finite || !(sum > 0.0))
            for (int j = 0; j < idx.Length; j++) a[j] = 1.0;
        return a;
    }

    /// <summary>
    /// The net export (MW) of the area made of <paramref name="buses"/>: over the in-service branches with exactly one
    /// end in it, in branch order, +P at the metered end when that end is in the area, -P when it is not.
    /// </summary>
    public double Export(GridModel model, PowerFlowResult r, IReadOnlyList<int> buses) => Export(model, r, buses, _metered);

    /// <summary>The net export with explicit metered ends (see the class remarks).</summary>
    public static double Export(GridModel model, PowerFlowResult r, IReadOnlyList<int> buses, IReadOnlyDictionary<int, BranchEnd>? meteredEnds)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(buses);
        var inArea = new HashSet<int>(buses);
        double e = 0.0;
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!br.InService) continue;
            bool f = inArea.Contains(br.From), t = inArea.Contains(br.To);
            if (f == t) continue;
            BranchEnd end = meteredEnds is not null && meteredEnds.TryGetValue(k, out BranchEnd me) ? me : BranchEnd.From;
            LineFlow lf = r.LineFlows[k];
            double p = end == BranchEnd.From ? lf.PFromMw : lf.PToMw;
            bool meteredInside = end == BranchEnd.From ? f : t;
            e += meteredInside ? p : -p;
        }
        return e;
    }

    private AreaInterchangeOutcome[] Outcomes(GridModel model, PowerFlowResult r, double[] k0, bool[] frozen, bool unsettledAll)
    {
        bool flows = r.LineFlows.Count == model.Branches.Count;
        var list = new List<AreaInterchangeOutcome>(_areas.Length + _reported.Length);
        for (int a = 0; a < _areas.Length; a++)
        {
            AreaInterchangeSpec s = _areas[a];
            double e = flows ? Export(model, r, s.Buses) : double.NaN;
            double err = e - s.DesiredExportMw;
            AreaInterchangeStatus st = frozen[a] ? AreaInterchangeStatus.AtLimit
                : !unsettledAll && Math.Abs(err) <= s.ToleranceMw ? AreaInterchangeStatus.Ok
                : AreaInterchangeStatus.NotSettled;
            list.Add(new AreaInterchangeOutcome(s.Area, e, s.DesiredExportMw, s.ToleranceMw, err, k0[a], st));
        }
        foreach (AreaMembership m in _reported)
            list.Add(new AreaInterchangeOutcome(m.Area, flows ? Export(model, r, m.Buses) : double.NaN, null, null, null, 0.0, AreaInterchangeStatus.Reported));
        return list.OrderBy(o => o.Area).ToArray();
    }
}
