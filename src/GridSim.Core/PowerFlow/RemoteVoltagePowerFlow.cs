using GridSim.Core.Abstractions;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>A generator bus that holds the voltage of another bus (PSS/E IREG): the units at
/// <paramref name="GeneratorBus"/> move their voltage set point so that <paramref name="RegulatedBus"/> sits at
/// <paramref name="TargetVpu"/>.</summary>
public sealed record RemoteVoltageControl(int GeneratorBus, int RegulatedBus, double TargetVpu);

/// <summary>How a remote voltage control ended in <see cref="RemoteVoltagePowerFlow"/>.</summary>
public enum RemoteVoltageStatus
{
    /// <summary>The regulated bus is within the tolerance of its target.</summary>
    Ok,
    /// <summary>The controlling bus's units reached a reactive limit in the direction the target asks for; the set
    /// point is frozen and the regulated bus is off target.</summary>
    AtQLimit,
    /// <summary>The set point reached its range [<see cref="RemoteVoltagePowerFlow.MinSetPointPu"/>,
    /// <see cref="RemoteVoltagePowerFlow.MaxSetPointPu"/>] and is frozen there.</summary>
    AtSetPointLimit,
    /// <summary>Off target when the loop stopped (pass cap, or the engine did not converge).</summary>
    NotSettled,
}

/// <summary>One control's outcome: the regulated bus's voltage, the controlling bus's final set point and the status.</summary>
public sealed record RemoteVoltageOutcome(int GeneratorBus, int RegulatedBus, double TargetVpu, double RegulatedVpu,
    double SetPointPu, RemoteVoltageStatus Status)
{
    /// <summary>The status as printed: OK, AT Q LIMIT, AT SET-POINT LIMIT or NOT SETTLED.</summary>
    public string StatusText => Status switch
    {
        RemoteVoltageStatus.Ok => "OK",
        RemoteVoltageStatus.AtQLimit => "AT Q LIMIT",
        RemoteVoltageStatus.AtSetPointLimit => "AT SET-POINT LIMIT",
        _ => "NOT SETTLED",
    };
}

/// <summary>
/// Remote voltage regulation, as an outer loop around an unchanged engine. The controlling bus stays a PV bus in the
/// Newton solve; its voltage set point is the outer variable. After each solve every control (in the order given)
/// compares its regulated bus with the target, e = target - V_reg, and moves its set point by a secant step on its own
/// (set point, V_reg) history: v_{k+1} = v_k + e_k (v_k - v_{k-1}) / (V_reg,k - V_reg,k-1), the first step with slope
/// 1. The set point stays in [<see cref="MinSetPointPu"/>, <see cref="MaxSetPointPu"/>]. The loop stops when every
/// control is within <see cref="VoltageTolerancePu"/> or frozen.
///
/// <para>A controlling bus that the reactive-limit loop turned PQ, at the limit in the direction the target asks for,
/// is AT Q LIMIT and its set point is frozen. A frozen control is looked at again after every solve, because another
/// control's step can bring its unit off the limit: it is released (and starts its secant afresh) as soon as its bus is
/// back on voltage control or the target asks to move away from the limit. PSS/E solves the remote voltage inside the Newton step instead; when no
/// limit binds both methods meet the same equations, so they reach the same state.</para>
///
/// <para>Refused by name: two controlling buses for one regulated bus (PSS/E RMPCT sharing is not modelled); a
/// regulated bus that is PV or slack; a regulated bus outside the controlling bus's island. The returned result is the
/// last engine solve, unchanged.</para>
/// </summary>
public sealed class RemoteVoltagePowerFlow : IPowerFlowSolver
{
    /// <summary>Default cap on outer passes.</summary>
    public const int DefaultMaxPasses = 30;

    /// <summary>The loop stops when every regulated bus is this close to its target (pu): the Newton engines' own
    /// mismatch tolerance.</summary>
    public const double VoltageTolerancePu = 1e-8;

    /// <summary>The lowest set point the loop gives a controlling bus (pu).</summary>
    public const double MinSetPointPu = 0.8;

    /// <summary>The highest set point the loop gives a controlling bus (pu).</summary>
    public const double MaxSetPointPu = 1.2;

    private readonly IPowerFlowSolver _engine;
    private readonly RemoteVoltageControl[] _controls;
    private readonly int _maxPasses;

    /// <summary>Wrap <paramref name="engine"/> with <paramref name="controls"/> (each checked: a finite target in
    /// [0.8, 1.2] pu, a regulated bus other than the controlling bus, no bus controlling twice and no bus regulated
    /// twice).</summary>
    public RemoteVoltagePowerFlow(IPowerFlowSolver engine, IReadOnlyList<RemoteVoltageControl> controls, int maxPasses = DefaultMaxPasses)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(controls);
        if (maxPasses < 1) throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "at least one pass is needed");
        var gens = new HashSet<int>();
        var regs = new Dictionary<int, int>();
        foreach (RemoteVoltageControl c in controls)
        {
            ArgumentNullException.ThrowIfNull(c);
            string who = $"remote voltage control at bus {c.GeneratorBus}";
            if (!double.IsFinite(c.TargetVpu) || c.TargetVpu < MinSetPointPu || c.TargetVpu > MaxSetPointPu)
                throw new ArgumentException(FormattableString.Invariant($"{who}: the target {c.TargetVpu} pu must be in [{MinSetPointPu}, {MaxSetPointPu}] pu."));
            if (c.RegulatedBus == c.GeneratorBus) throw new ArgumentException($"{who} regulates its own bus; that is ordinary local control.");
            if (!gens.Add(c.GeneratorBus)) throw new ArgumentException($"{who} is given twice.");
            if (regs.TryGetValue(c.RegulatedBus, out int other))
                throw new ArgumentException($"remote voltage control: bus {c.RegulatedBus} is regulated by two controlling buses ({other} and {c.GeneratorBus}); sharing one regulated bus (PSS/E RMPCT) is not modelled.");
            regs[c.RegulatedBus] = c.GeneratorBus;
        }
        _engine = engine;
        _controls = controls.ToArray();
        _maxPasses = maxPasses;
    }

    /// <summary>Outer passes (engine solves) taken by the last <see cref="Solve"/>.</summary>
    public int LastPasses { get; private set; }

    /// <summary>Each control's outcome in the last <see cref="Solve"/>, in the order given.</summary>
    public IReadOnlyList<RemoteVoltageOutcome> LastControls { get; private set; } = Array.Empty<RemoteVoltageOutcome>();

    /// <summary>True when no control ended NOT SETTLED.</summary>
    public bool Settled { get; private set; }

    /// <summary>Why the last <see cref="Solve"/> did not settle, or which controls are frozen (null when neither).</summary>
    public string? LastNote { get; private set; }

    /// <summary>Solve <paramref name="model"/> with the remote voltage controls.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        LastNote = null;
        Settled = false;
        LastControls = Array.Empty<RemoteVoltageOutcome>();
        int n = _controls.Length;
        var busById = new Dictionary<int, Bus>(model.Buses.Count);
        foreach (Bus b in model.Buses) busById.TryAdd(b.Id, b);
        int[] island = Islands(model);
        var position = new Dictionary<int, int>(model.Buses.Count);
        for (int i = 0; i < model.Buses.Count; i++) position.TryAdd(model.Buses[i].Id, i);

        var vset = new double[n];
        for (int c = 0; c < n; c++)
        {
            RemoteVoltageControl rc = _controls[c];
            string who = $"remote voltage control at bus {rc.GeneratorBus}";
            if (!busById.TryGetValue(rc.GeneratorBus, out Bus? gb)) throw new ArgumentException($"{who}: the bus is not in the model.");
            if (!busById.TryGetValue(rc.RegulatedBus, out Bus? rb)) throw new ArgumentException($"{who}: the regulated bus {rc.RegulatedBus} is not in the model.");
            if (gb.Type != BusType.Pv) throw new ArgumentException($"{who}: the controlling bus is {gb.Type}, not PV.");
            bool any = false;
            foreach (Generator u in model.Generators) if (u.Bus == rc.GeneratorBus) { vset[c] = u.Vg; any = true; }   // last unit wins, as the engines read it
            if (!any) throw new ArgumentException($"{who}: the controlling bus has no in-service unit.");
            if (rb.Type != BusType.Pq)
                throw new ArgumentException($"{who}: the regulated bus {rc.RegulatedBus} is {(rb.Type == BusType.Slack ? "the slack" : "a PV bus")}, which holds its own voltage; only a PQ bus can be regulated remotely.");
            if (island[position[rc.GeneratorBus]] != island[position[rc.RegulatedBus]])
                throw new ArgumentException($"{who}: the regulated bus {rc.RegulatedBus} is not in the controlling bus's island.");
        }

        var prevSet = new double[n];
        var prevReg = new double[n];
        var hasPrev = new bool[n];
        var status = new RemoteVoltageStatus[n];
        var vreg = new double[n];
        for (int c = 0; c < n; c++) { status[c] = RemoteVoltageStatus.NotSettled; vreg[c] = double.NaN; }
        PowerFlowResult? r = null;
        bool changedAny = false;
        for (int pass = 1; pass <= _maxPasses; pass++)
        {
            LastPasses = pass;
            r = _engine.Solve(changedAny ? WithSetPoints(model, vset) : model, options);
            if (!r.Converged)
            {
                LastNote = $"the engine did not converge on pass {pass}";
                for (int c = 0; c < n; c++) status[c] = status[c] == RemoteVoltageStatus.Ok ? RemoteVoltageStatus.NotSettled : status[c];
                LastControls = Outcomes(vreg, vset, status);
                return r;
            }
            var at = new Dictionary<int, int>(r.BusIds.Count);
            for (int i = 0; i < r.BusIds.Count; i++) at.TryAdd(r.BusIds[i], i);

            bool changed = false;
            for (int c = 0; c < n; c++)
            {
                RemoteVoltageControl rc = _controls[c];
                vreg[c] = r.Vm[at[rc.RegulatedBus]];
                if (status[c] is RemoteVoltageStatus.AtSetPointLimit) continue;
                double e = rc.TargetVpu - vreg[c];
                int gi = at[rc.GeneratorBus];
                bool wasAtQLimit = status[c] == RemoteVoltageStatus.AtQLimit;
                if (r.SolvedBusTypes is { } types && types[gi] == BusType.Pq)
                {
                    // At a reactive limit: frozen when the target asks to go further past it; otherwise step back. A frozen
                    // control is re-checked here after every solve, since another control's step may have moved the limit.
                    bool atMax = AtUpperLimit(model, busById[rc.GeneratorBus], r, gi);
                    if ((atMax && e > 0) || (!atMax && e < 0)) { status[c] = RemoteVoltageStatus.AtQLimit; continue; }
                    hasPrev[c] = false;
                }
                // Released from AT Q LIMIT (back on voltage control, or asked to move away): its history predates the
                // other controls' moves, so the secant starts afresh.
                if (wasAtQLimit) hasPrev[c] = false;
                if (Math.Abs(e) <= VoltageTolerancePu) { status[c] = RemoteVoltageStatus.Ok; continue; }
                status[c] = RemoteVoltageStatus.NotSettled;
                double step = e;
                if (hasPrev[c])
                {
                    double dv = vset[c] - prevSet[c], dr = vreg[c] - prevReg[c];
                    if (dv != 0.0 && dr != 0.0 && double.IsFinite(dv / dr)) step = e * (dv / dr);
                }
                double next = Math.Clamp(vset[c] + step, MinSetPointPu, MaxSetPointPu);
                if (next == vset[c]) { status[c] = RemoteVoltageStatus.AtSetPointLimit; continue; }
                prevSet[c] = vset[c]; prevReg[c] = vreg[c]; hasPrev[c] = true;
                vset[c] = next;
                changed = true;
            }
            if (!changed)
            {
                LastControls = Outcomes(vreg, vset, status);
                Settled = status.All(s => s != RemoteVoltageStatus.NotSettled);
                var frozen = Enumerable.Range(0, n).Where(c => status[c] is RemoteVoltageStatus.AtQLimit or RemoteVoltageStatus.AtSetPointLimit)
                    .Select(c => _controls[c].GeneratorBus.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (frozen.Length > 0) LastNote = "set point frozen at a limit for the control(s) at bus " + string.Join(", ", frozen);
                return r;
            }
            changedAny = true;
        }
        LastControls = Outcomes(vreg, vset, status);
        Settled = false;
        LastNote = $"the remote voltage controls did not settle within {_maxPasses} passes";
        return r!;
    }

    private RemoteVoltageOutcome[] Outcomes(double[] vreg, double[] vset, RemoteVoltageStatus[] status)
    {
        var o = new RemoteVoltageOutcome[_controls.Length];
        for (int c = 0; c < o.Length; c++)
            o[c] = new RemoteVoltageOutcome(_controls[c].GeneratorBus, _controls[c].RegulatedBus, _controls[c].TargetVpu, vreg[c], vset[c], status[c]);
        return o;
    }

    /// <summary>The model with every in-service unit at each controlling bus set to that control's set point.</summary>
    private GridModel WithSetPoints(GridModel model, double[] vset)
    {
        var setAt = new Dictionary<int, double>(_controls.Length);
        for (int c = 0; c < _controls.Length; c++) setAt[_controls[c].GeneratorBus] = vset[c];
        var gens = new Generator[model.Generators.Count];
        for (int i = 0; i < gens.Length; i++)
        {
            Generator u = model.Generators[i];
            gens[i] = setAt.TryGetValue(u.Bus, out double v) ? u with { Vg = v } : u;
        }
        return model.CopyWith(generators: gens);
    }

    /// <summary>Whether the units at a bus the reactive-limit loop turned PQ sit at their upper (rather than lower)
    /// limit: the unit output, net injection plus the load drawn, is nearer the summed Qmax.</summary>
    private static bool AtUpperLimit(GridModel model, Bus bus, PowerFlowResult r, int gi)
    {
        double qmax = 0.0, qmin = 0.0;
        foreach (Generator u in model.Generators)
            if (u.Bus == bus.Id) { qmax += u.Qmax; qmin += u.Qmin; }
        double q = r.QInjMvar[gi] + LoadAtVoltage.Mw(bus, r.Vm[gi]).QMvar;
        return Math.Abs(q - qmax) <= Math.Abs(q - qmin);
    }

    /// <summary>An island label per model bus (buses joined by in-service branches), by position.</summary>
    private static int[] Islands(GridModel model)
    {
        int nb = model.Buses.Count;
        var pos = new Dictionary<int, int>(nb);
        for (int i = 0; i < nb; i++) pos.TryAdd(model.Buses[i].Id, i);
        var parent = new int[nb];
        for (int i = 0; i < nb; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (Branch br in model.Branches)
        {
            if (!br.InService || !pos.TryGetValue(br.From, out int a) || !pos.TryGetValue(br.To, out int b)) continue;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        var label = new int[nb];
        for (int i = 0; i < nb; i++) label[i] = Find(i);
        return label;
    }
}
