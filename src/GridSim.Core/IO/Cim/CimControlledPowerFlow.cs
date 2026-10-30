using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO.Cim;

/// <summary>N295 / N296 (CIM3, 2026-10): the knobs of <see cref="CimControlledPowerFlow"/>.</summary>
/// <param name="TolerancePu">How close (pu) every regulated voltage must be to its control law for the controls to count
/// as settled: |V - target| for a remote control, |V + slope x Q - target| for a susceptance control. Default 1e-8 pu
/// (4 microvolts per kV, below the 1e-6 kV the CGMES SV profiles print).</param>
/// <param name="MaxSolves">The cap on engine solves of one control loop (each probe and each step is one solve); with
/// switched shunts, the cap applies to each switching pass. Default 200.</param>
/// <param name="Shunts">N296: how voltage-regulating shunts are solved (<see cref="CimShuntControlMode"/>); by default as
/// their data asks.</param>
public sealed record CimControlOptions(
    double TolerancePu = 1e-8, int MaxSolves = 200, CimShuntControlMode Shunts = CimShuntControlMode.AsWritten);

/// <summary>N296 (CIM3, 2026-10): how <see cref="CimControlledPowerFlow"/> solves the voltage-regulating shunts
/// (<see cref="CimBridgeResult.ShuntRegulations"/>).</summary>
public enum CimShuntControlMode
{
    /// <summary>As the data asks: discrete (whole sections, <see cref="SwitchedShuntPowerFlow"/>) when its
    /// RegulatingControl.discrete is true or absent, continuous when it is false; the other form when the asked one cannot
    /// be built (said in <see cref="CimControlledPowerFlow.ShuntChoices"/>).</summary>
    AsWritten,
    /// <summary>Every regulating shunt switched in whole sections (held at its sections when it has no discrete form).</summary>
    Discrete,
    /// <summary>Every regulating shunt as a continuous susceptance between 0 and maximumSections sections, holding its target
    /// exactly (what a tool that regulates shunts continuously, or treats them as synchronous condensers, solves).</summary>
    Continuous,
    /// <summary>Every shunt held at its sections, as a plain solve does.</summary>
    Held,
}

/// <summary>
/// N306 (LF3, October 2026): what a settled <see cref="CimControlledPowerFlow"/> solve set the regulated devices to, kept
/// against the result object it returned so <see cref="CimBridgeResult.SwitchFlows"/> can balance a switch with the
/// regulated values instead of the plain model's fixed ones.
/// </summary>
/// <param name="Bridge">The bridged model the solver regulates (a result is only read against its own bridge).</param>
/// <param name="SusceptanceMvarByMrid">Each susceptance control's final susceptance, MVAr at 1.0 pu of its bus, by device
/// mRID: a StaticVarCompensator's whole susceptance (its SSH q is taken out of the bus), a continuous shunt's susceptance
/// RELATIVE to the sections the bridged model holds.</param>
/// <param name="SwitchedMvarByMrid">Each shunt switched in whole sections: its final steps x one section, MVAr at 1.0 pu,
/// relative to the sections the bridged model holds, by mRID.</param>
/// <param name="RegulatingMachineBusIds">The GridModel buses the remote controls made voltage-controlled.</param>
internal sealed record CimControlledSolveRecord(
    CimBridgeResult Bridge, IReadOnlyDictionary<string, double> SusceptanceMvarByMrid,
    IReadOnlyDictionary<string, double> SwitchedMvarByMrid, IReadOnlySet<int> RegulatingMachineBusIds);

/// <summary>N295: where one control ended after <see cref="CimControlledPowerFlow.Solve"/>.</summary>
/// <param name="Name">The regulated bus and its devices, in plain words.</param>
/// <param name="Kind">"remote voltage" or "susceptance".</param>
/// <param name="Value">The control variable: the common per-unit voltage set on the machines' buses (remote), or the
/// susceptance in MVAr at 1.0 pu (relative to the bridged model for a shunt).</param>
/// <param name="ErrorPu">The remaining control-law error (pu) at the final state.</param>
/// <param name="AtLimit">True when the device ended at a limit (a machine at a reactive limit, a susceptance at a
/// rating), so the regulated voltage is not at its target.</param>
public sealed record CimControlState(string Name, string Kind, double Value, double ErrorPu, bool AtLimit);

/// <summary>
/// N295 / N296 (CIM3, 2026-10): the power flow of a bridged CIM network WITH the CIM voltage controls GridSim's PV / PQ
/// bus cannot express, as an outer loop around an unchanged engine (the way GridSim's tap-changer loop and switched-shunt
/// loop work, and the way PSS/E and PowerFactory solve remote control and SVC droop in outer loops):
/// <list type="bullet">
/// <item><b>Remote voltage control</b> (<see cref="CimBridgeResult.RemoteVoltageControls"/>): the machines' buses are
/// made PV and their set-points are chosen so the REMOTE bus is at its target. The machines' reactive limits are
/// enforced by the engine as usual: when they bind (the engine ends their bus PQ), the remote bus cannot be held and the
/// control is reported at its limit (PSS/E's behaviour). N307 (LF3, October 2026; Ruling R1): a SHARED control (machines
/// at several buses) has one set-point per machine bus; the first free one carries the voltage law and each other free
/// one the sharing law Q_k / key_k = Q_lead / key_lead (keys: the reactive ranges, <see cref="CimRemoteVoltageControl.ReactiveShareKeys"/>),
/// so the reactive output is shared in proportion to the machines' reactive ranges (powsybl; the PSS/E RMPCT default). A
/// machine bus the engine pins at a reactive limit leaves the sharing and the others carry on. A control with one machine
/// bus is solved exactly as before (one set-point, the voltage law), bit for bit.</item>
/// <item><b>Susceptance controls</b> (<see cref="CimBridgeResult.SusceptanceControls"/>: StaticVarCompensators, and shunt
/// compensators under continuous control, N296): a susceptance B between its limits at its bus, with
/// V_regulated + slope x B x V_bus^2 / baseMVA = target. At a limit it stays a fixed susceptance (exact: its output
/// follows V^2, unlike a PV bus pinned at a fixed Q).</item>
/// </list>
/// The control variables are found by a damped Newton iteration whose sensitivities are taken by finite differences of
/// whole engine solves (one probe per variable), then updated by Broyden's rank-one rule; a variable at a limit that wants
/// to go further is held there. Every solve is an ordinary solve of the engine on a copy of the model, so the dense and
/// sparse engines give the same bits, and the iteration is fully deterministic (fixed order, no randomness). It ends
/// when every free control is within <see cref="CimControlOptions.TolerancePu"/>. Fail loud: when it cannot (the cap
/// is reached, the sensitivities are singular, the engine does not converge, or a machine is held at a limit on the
/// wrong side), the result is returned with <see cref="PowerFlowResult.Converged"/> false and
/// <see cref="LastNote"/> says why; such a state must never be reported as regulated.
/// <para>A plain engine solve of <see cref="CimBridgeResult.Model"/> holds remote-regulating machines and SVCs at their
/// SSH q; this solver is the opt-in that regulates them. With no control at all it is one engine solve of the model as
/// given (the same result object).</para>
/// </summary>
public sealed class CimControlledPowerFlow : IPowerFlowSolver
{
    private readonly IPowerFlowSolver _engine;
    private readonly IReadOnlyList<CimRemoteVoltageControl> _remote;
    private readonly IReadOnlyList<CimSusceptanceControl> _susceptances;
    private readonly CimControlOptions _options;

    // Finite-difference probe sizes: 1e-4 pu of voltage set-point; for a susceptance, 1e-3 of its range (at least
    // 1e-3 MVAr). Large against the engine's solution noise (about 1e-10 pu), small against the curvature.
    private const double VoltageProbePu = 1e-4;
    private const double SusceptanceProbeFraction = 1e-3;
    private const double MinSusceptanceProbeMvar = 1e-3;

    // The largest change of a voltage set-point in one Newton step (pu): keeps an early step from a poor sensitivity
    // inside the region where the engine converges.
    private const double MaxVoltageStepPu = 0.1;

    /// <summary>
    /// N307 (LF3, October 2026): how close the reactive sharing of a shared remote control must be for it to count as
    /// settled, as a per-unit reactive power on the system base: |Q_k / key_k - Q_lead / key_lead| x (sum of keys) /
    /// baseMVA. 1e-6 pu is 0.1 kVAr on a 100 MVA base. Looser than the voltage tolerance on purpose: a reactive output is
    /// the product of the solved state and the network, so its solution noise is that of the engine's mismatch (about
    /// 1e-8 pu), times the bus's short-circuit level; holding it to 1e-8 pu would chase the engine's own rounding.
    /// </summary>
    public const double ShareTolerancePu = 1e-6;

    // N307: the sharing keys of each remote control, validated (one finite positive key per machine bus; equal keys when
    // the control carries none).
    private readonly IReadOnlyList<double[]> _shareKeys;

    // N296: the regulating shunts solved in whole sections, and their names, in the bridge's order.
    private readonly List<SwitchedShunt> _switched = new();
    // N306 (LF3): the mRID of each shunt in _switched (same order), so a solve can say what each one ended at.
    private readonly List<string> _switchedMrid = new();

    // N306 (LF3, October 2026): the bridge this solver regulates, and what the last regulated solve set its devices to
    // (null after a solve that did not settle). Every result Solve returns is registered against that record, so
    // CimBridgeResult.SwitchFlows can tell a controlled result from a plain one by the result object itself.
    private readonly CimBridgeResult _bridge;
    private CimControlledSolveRecord? _lastRecord;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PowerFlowResult, CimControlledSolveRecord> Solves = new();

    /// <summary>N306: the record of the <see cref="CimControlledPowerFlow"/> solve that returned <paramref name="result"/>
    /// (by object identity), or null when it is not one (a plain engine solve).</summary>
    internal static CimControlledSolveRecord? RecordOf(PowerFlowResult result) =>
        Solves.TryGetValue(result, out CimControlledSolveRecord? rec) ? rec : null;

    // N306: remember what this solve's regulated devices ended at, against the result object Solve returns.
    private void Register(PowerFlowResult result, IReadOnlyList<int>? steps)
    {
        if (_lastRecord is null) return;
        var switched = new Dictionary<string, double>(StringComparer.Ordinal);
        if (steps is not null)
            for (int i = 0; i < _switched.Count && i < steps.Count; i++)
                switched[_switchedMrid[i]] = steps[i] * _switched[i].StepMvar;
        Solves.AddOrUpdate(result, _lastRecord with { SwitchedMvarByMrid = switched });
    }

    /// <summary>Wrap <paramref name="engine"/> with the controls of <paramref name="bridge"/>.</summary>
    public CimControlledPowerFlow(IPowerFlowSolver engine, CimBridgeResult bridge, CimControlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(bridge);
        _engine = engine;
        _bridge = bridge;
        _remote = bridge.RemoteVoltageControls;
        _options = options ?? new CimControlOptions();
        if (!(_options.TolerancePu > 0) || !double.IsFinite(_options.TolerancePu))
            throw new ArgumentOutOfRangeException(nameof(options), _options.TolerancePu, "the control tolerance must be a finite number above 0 pu");
        if (_options.MaxSolves < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxSolves, "at least one solve is needed");

        // N307: each remote control's sharing keys, one per machine bus. Fail loud on keys that cannot share (a count that
        // does not match the buses, or a key that is not a finite number above 0); none given = equal shares.
        var shareKeys = new List<double[]>(_remote.Count);
        foreach (CimRemoteVoltageControl rc in _remote)
        {
            int m = rc.GeneratorBusIds.Count;
            if (m < 1)
                throw new ArgumentException($"CIM remote voltage control of bus {rc.RegulatedBusId} has no machine bus.", nameof(bridge));
            if (rc.ReactiveShareKeys is null) { shareKeys.Add(Enumerable.Repeat(1.0, m).ToArray()); continue; }
            if (rc.ReactiveShareKeys.Count != m)
                throw new ArgumentException(
                    $"CIM remote voltage control of bus {rc.RegulatedBusId}: {rc.ReactiveShareKeys.Count} sharing key(s) for {m} machine bus(es).",
                    nameof(bridge));
            foreach (double key in rc.ReactiveShareKeys)
                if (!double.IsFinite(key) || !(key > 0.0))
                    throw new ArgumentException(
                        $"CIM remote voltage control of bus {rc.RegulatedBusId}: a sharing key of {key} is not a finite number above 0.",
                        nameof(bridge));
            shareKeys.Add(rc.ReactiveShareKeys.ToArray());
        }
        _shareKeys = shareKeys;

        // N296: each regulating shunt in the form the options ask for (the other form when that one cannot be built,
        // under AsWritten only; held otherwise), said one line per shunt.
        var susceptances = new List<CimSusceptanceControl>(bridge.SusceptanceControls);
        var choices = new List<string>();
        foreach (CimShuntRegulation s in bridge.ShuntRegulations)
        {
            bool wantDiscrete = _options.Shunts switch
            {
                CimShuntControlMode.Discrete => true,
                CimShuntControlMode.Continuous => false,
                CimShuntControlMode.AsWritten => s.DataSaysDiscrete,
                _ => false,
            };
            if (_options.Shunts == CimShuntControlMode.Held)
            {
                choices.Add($"{s.Name}: held at its sections (CimShuntControlMode.Held).");
            }
            else if (wantDiscrete && s.Discrete is not null)
            {
                _switched.Add(s.Discrete);
                _switchedMrid.Add(s.MRID);
                choices.Add($"{s.Name}: switched in whole sections.");
            }
            else if (!wantDiscrete && s.Continuous is not null)
            {
                susceptances.Add(s.Continuous);
                choices.Add($"{s.Name}: a continuous susceptance.");
            }
            else if (_options.Shunts == CimShuntControlMode.AsWritten && s.Continuous is not null)
            {
                susceptances.Add(s.Continuous);
                choices.Add($"{s.Name}: a continuous susceptance, because it has no discrete form ({s.Basis}).");
            }
            else
            {
                choices.Add($"{s.Name}: held at its sections, because it has no {(wantDiscrete ? "discrete" : "continuous")} form ({s.Basis}).");
            }
        }
        _susceptances = susceptances;
        ShuntChoices = choices;
    }

    /// <summary>N296: how each regulating shunt of the bridge is solved, one line each, in the bridge's order.</summary>
    public IReadOnlyList<string> ShuntChoices { get; }

    /// <summary>N296: the section steps each switched shunt ended at in the last <see cref="Solve"/>, RELATIVE to the
    /// sections the bridged model holds, in the order of the shunts solved in whole sections; empty when there are none.</summary>
    public IReadOnlyList<int> LastShuntSteps { get; private set; } = Array.Empty<int>();

    /// <summary>Engine solves taken by the last <see cref="Solve"/> (over every switching pass).</summary>
    public int LastSolves { get; private set; }

    /// <summary>Whether every control ended within tolerance or legitimately at a limit in the last <see cref="Solve"/>.</summary>
    public bool ControlsSettled { get; private set; }

    /// <summary>Why the last <see cref="Solve"/> did not settle (null when it did).</summary>
    public string? LastNote { get; private set; }

    /// <summary>Where each control ended in the last <see cref="Solve"/>: the remote controls, then the susceptance
    /// controls, in the bridge's order. N307: a shared remote control has one state per machine bus (its set-point, and the
    /// voltage error on the first free bus or the sharing error, per-unit on the system base, on the others).</summary>
    public IReadOnlyList<CimControlState> LastStates { get; private set; } = Array.Empty<CimControlState>();

    /// <summary>One control variable.</summary>
    private sealed class Var
    {
        public required bool Remote { get; init; }
        public required int Index { get; init; }          // into _remote or _susceptances
        public int Bus { get; init; } = -1;               // N307: a remote variable's machine bus (its set-point)
        public int Member { get; init; }                  // N307: its position in the control's GeneratorBusIds
        public required double Min { get; init; }
        public required double Max { get; init; }
        public required double Probe { get; init; }
        public double X;
        public bool Pinned;                               // a remote control whose machines the engine held at a limit
    }

    /// <summary>Solve <paramref name="model"/> (the bridged model, or a copy of it with the same buses) with the controls.
    /// N296: when shunts are switched in whole sections, <see cref="SwitchedShuntPowerFlow"/> (unchanged) is the outer
    /// loop and the control loop of the other controls is its engine, so every switching pass is a fully regulated
    /// solve; a pass whose controls do not settle stops the switching with a non-converged result.</summary>
    public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        LastSolves = 0;
        LastNote = null;
        ControlsSettled = false;
        LastStates = Array.Empty<CimControlState>();
        LastShuntSteps = Array.Empty<int>();
        _lastRecord = null;
        if (_switched.Count == 0)
        {
            PowerFlowResult direct = SolveControls(model, options);
            Register(direct, null);   // N306: SwitchFlows can tell this result is a controlled one
            return direct;
        }

        var switching = new SwitchedShuntPowerFlow(new ControlLoop(this), _switched);
        PowerFlowResult r = switching.Solve(model, options);
        LastShuntSteps = switching.LastSteps;
        // N306: the last pass's regulated solve set _lastRecord; the steps are the ones that pass solved with.
        Register(r, switching.LastSteps);
        if (!r.Converged)
        {
            // The control loop already said why (and returned a non-converged state); keep its note, else the switching's.
            LastNote ??= "switched shunts: " + (switching.LastNote ?? "the engine did not converge");
            ControlsSettled = false;
            return r;
        }
        if (!switching.ShuntsSettled)
        {
            LastNote = "switched shunts: " + (switching.LastNote ?? "they did not settle");
            ControlsSettled = false;
            return NotConverged(r);
        }
        return r;
    }

    /// <summary>The control loop as an engine, for <see cref="SwitchedShuntPowerFlow"/> to wrap (N296).</summary>
    private sealed class ControlLoop : IPowerFlowSolver
    {
        private readonly CimControlledPowerFlow _owner;
        public ControlLoop(CimControlledPowerFlow owner) => _owner = owner;
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null) => _owner.SolveControls(model, options);
    }

    /// <summary>One regulated solve of <paramref name="model"/>: the remote and susceptance controls (N295 / N296).</summary>
    private PowerFlowResult SolveControls(GridModel model, PowerFlowOptions? options)
    {
        LastNote = null;
        ControlsSettled = false;
        LastStates = Array.Empty<CimControlState>();
        _lastRecord = null;
        int startSolves = LastSolves;

        var position = new Dictionary<int, int>(model.Buses.Count);
        for (int i = 0; i < model.Buses.Count; i++) position.TryAdd(model.Buses[i].Id, i);
        int Pos(int busId, string what) => position.TryGetValue(busId, out int p) ? p
            : throw new ArgumentException($"CIM control {what} names bus {busId}, which is not in the model; solve the model the bridge built.");

        if (_remote.Count == 0 && _susceptances.Count == 0)
        {
            // Nothing to regulate: the engine's own solve, untouched.
            LastSolves++;
            PowerFlowResult plain = _engine.Solve(model, options);
            ControlsSettled = plain.Converged;
            if (!plain.Converged) LastNote = "the engine did not converge";
            else _lastRecord = NewRecord(new Dictionary<string, double>(StringComparer.Ordinal));
            return plain;
        }

        // ---- the base model the controls act on ------------------------------------------------------------
        // Remote control: the machines' buses become PV (their set-point is the control variable). Susceptance control:
        // the SSH q the plain model holds an SVC at is taken back out of its bus's Qd (the susceptance replaces it).
        var buses = model.Buses.ToArray();
        foreach (CimRemoteVoltageControl rc in _remote)
            foreach (int gb in rc.GeneratorBusIds)
            {
                int p = Pos(gb, $"of bus {rc.RegulatedBusId}");
                if (buses[p].Type == BusType.Pq) buses[p] = buses[p] with { Type = BusType.Pv };
            }
        foreach (CimSusceptanceControl sc in _susceptances)
        {
            int p = Pos(sc.BusId, sc.Name);
            Pos(sc.RegulatedBusId, sc.Name);
            if (sc.HeldQdMvar != 0.0) buses[p] = buses[p] with { Qd = buses[p].Qd - sc.HeldQdMvar };
        }
        foreach (CimRemoteVoltageControl rc in _remote) Pos(rc.RegulatedBusId, $"of bus {rc.RegulatedBusId}");

        // ---- the control variables ---------------------------------------------------------------------------
        // N307: one set-point per machine bus of a remote control (one variable for a control with one machine bus, as
        // before), all starting at the remote target, in the control's bus order.
        var vars = new List<Var>();
        for (int k = 0; k < _remote.Count; k++)
            for (int i = 0; i < _remote[k].GeneratorBusIds.Count; i++)
                vars.Add(new Var { Remote = true, Index = k, Member = i, Bus = _remote[k].GeneratorBusIds[i],
                                   Min = double.NegativeInfinity, Max = double.PositiveInfinity,
                                   Probe = VoltageProbePu, X = _remote[k].TargetPu });
        for (int k = 0; k < _susceptances.Count; k++)
        {
            CimSusceptanceControl sc = _susceptances[k];
            double probe = Math.Max(MinSusceptanceProbeMvar, SusceptanceProbeFraction * (sc.BMaxMvar - sc.BMinMvar));
            vars.Add(new Var { Remote = false, Index = k, Min = sc.BMinMvar, Max = sc.BMaxMvar, Probe = probe,
                               X = Math.Clamp(sc.InitialBMvar, sc.BMinMvar, sc.BMaxMvar) });
        }
        int n = vars.Count;

        // One engine solve at the control values x: the result, each control's error (null when the engine failed), which
        // remote variables the engine pinned at a reactive limit, and (N307) which variables carry a sharing law rather
        // than a voltage law. ownPins: whether the pins found in THIS solve decide which member of a shared control carries
        // the voltage law (true for a point the loop may move to; false for a sensitivity probe, whose errors must keep
        // the meaning they have at the point being differentiated).
        (PowerFlowResult R, double[]? G, bool[] Pinned, bool[] Share) Eval(double[] x, bool ownPins)
        {
            var b = (Bus[])buses.Clone();
            for (int j = 0; j < n; j++)
            {
                if (vars[j].Remote) continue;
                int p = position[_susceptances[vars[j].Index].BusId];
                b[p] = b[p] with { Bs = b[p].Bs + x[j] };
            }
            // N307: each remote variable is the set-point of its own machine bus (with one machine bus per control this is
            // the control's one set-point on its one bus, exactly as before).
            var setPoint = new Dictionary<int, double>();
            for (int j = 0; j < n; j++)
                if (vars[j].Remote) setPoint[vars[j].Bus] = x[j];
            var gens = new List<Generator>(model.Generators.Count);
            foreach (Generator g in model.Generators)
                gens.Add(setPoint.TryGetValue(g.Bus, out double u) ? g with { Vg = u } : g);
            GridModel m = model.CopyWith(buses: b, generators: gens);
            LastSolves++;
            PowerFlowResult r = _engine.Solve(m, options);
            var pinned = new bool[n];
            var share = new bool[n];
            if (!r.Converged) return (r, null, pinned, share);
            var e = new double[n];
            // The engine ended a machine bus PQ: its machines are at a reactive limit and no longer follow their set-point.
            for (int j = 0; j < n; j++)
                if (vars[j].Remote && r.SolvedBusTypes is { } solved && solved[position[vars[j].Bus]] == BusType.Pq)
                    pinned[j] = true;
            for (int j = 0; j < n; j++)
            {
                if (vars[j].Remote)
                {
                    if (vars[j].Member != 0) continue;   // a control's members are handled together, from its first
                    CimRemoteVoltageControl rc = _remote[vars[j].Index];
                    int count = rc.GeneratorBusIds.Count;
                    double vErr = r.Vm[position[rc.RegulatedBusId]] - rc.TargetPu;
                    // The member that carries the voltage law: the first one neither held at a limit nor pinned now (the
                    // first member when all are held, so a held control still reports its voltage error, as before).
                    bool Held(int jj) => vars[jj].Pinned || (ownPins && pinned[jj]);
                    int lead = j;
                    for (int i = 0; i < count; i++) if (!Held(j + i)) { lead = j + i; break; }
                    e[lead] = vErr;
                    if (count == 1) continue;
                    // N307: every other member carries the sharing law against the lead, as a per-unit reactive power on
                    // the system base: (Q_k / key_k - Q_lead / key_lead) x (sum of keys) / baseMVA. Q is the machine
                    // bus's reactive generation: its net injection plus its demand (the shunts are in the network).
                    double[] keys = _shareKeys[vars[j].Index];
                    double keySum = 0.0;
                    foreach (double key in keys) keySum += key;
                    double QOf(int jj) { int p = position[vars[jj].Bus]; return r.QInjMvar[p] + b[p].Qd; }
                    double leadPerKey = QOf(lead) / keys[vars[lead].Member];
                    for (int i = 0; i < count; i++)
                    {
                        int jj = j + i;
                        if (jj == lead) continue;
                        e[jj] = (QOf(jj) / keys[vars[jj].Member] - leadPerKey) * keySum / model.BaseMva;
                        share[jj] = true;
                    }
                }
                else
                {
                    CimSusceptanceControl sc = _susceptances[vars[j].Index];
                    double vBus = r.Vm[position[sc.BusId]];
                    double qPu = x[j] * vBus * vBus / model.BaseMva;   // the capacitive output, pu
                    e[j] = r.Vm[position[sc.RegulatedBusId]] + sc.SlopePu * qPu - sc.TargetPu;
                }
            }
            return (r, e, pinned, share);
        }

        double[] x = vars.Select(v => v.X).ToArray();
        (PowerFlowResult res, double[]? err, bool[] pin, bool[] role) = Eval(x, ownPins: true);
        if (err is null) return Fail(res, x, null, vars, "the engine did not converge at the starting point");
        double[] g = err;
        // N307: the tolerance of a variable's law: the voltage tolerance, or ShareTolerancePu for a sharing law.
        double Tol(int j, bool[] shareRole) => shareRole[j] ? ShareTolerancePu : _options.TolerancePu;

        double[,]? jac = null;
        int[] jacIdx = Array.Empty<int>();
        bool recompute = true;
        while (true)
        {
            // Which variables are free: a remote control whose machines are at a limit is held (for good); a susceptance at
            // a limit that wants to go further is held there for this step.
            for (int j = 0; j < n; j++) if (pin[j]) vars[j].Pinned = true;
            var free = new List<int>();
            for (int j = 0; j < n; j++)
            {
                if (vars[j].Pinned) continue;
                if (!vars[j].Remote)
                {
                    // g < 0: the voltage is low, more capacitive susceptance is wanted.
                    if (x[j] >= vars[j].Max && g[j] < 0) continue;
                    if (x[j] <= vars[j].Min && g[j] > 0) continue;
                }
                free.Add(j);
            }
            if (free.All(j => Math.Abs(g[j]) <= Tol(j, role)))
                return Finish(res, x, g, vars, model, role, buses);
            if (LastSolves - startSolves >= _options.MaxSolves)
                return Fail(res, x, g, vars, $"the controls did not settle within {_options.MaxSolves} engine solves");

            // Sensitivities: by finite differences when there are none yet, when the free set changed, or when the last
            // step made poor progress; otherwise Broyden's update of the last ones.
            if (jac is null || recompute || !jacIdx.SequenceEqual(free))
            {
                jacIdx = free.ToArray();
                jac = new double[jacIdx.Length, jacIdx.Length];
                for (int c = 0; c < jacIdx.Length; c++)
                {
                    int j = jacIdx[c];
                    double h = x[j] + vars[j].Probe <= vars[j].Max ? vars[j].Probe : -vars[j].Probe;
                    double[] xp = (double[])x.Clone();
                    xp[j] += h;
                    var (_, gp, _, _) = Eval(xp, ownPins: false);
                    if (gp is null) return Fail(res, x, g, vars, "the engine did not converge at a sensitivity probe");
                    for (int rIdx = 0; rIdx < jacIdx.Length; rIdx++)
                        jac[rIdx, c] = (gp[jacIdx[rIdx]] - g[jacIdx[rIdx]]) / h;
                }
            }

            // The Newton step on the free variables.
            int m = jacIdx.Length;
            var rhs = new double[m];
            for (int rIdx = 0; rIdx < m; rIdx++) rhs[rIdx] = 0.0 - g[jacIdx[rIdx]];
            double[]? step = SolveDense(jac, rhs);
            if (step is null)
                return Fail(res, x, g, vars, "the controls' sensitivity matrix is singular (two controls act on one voltage, or a control has no effect)");
            double[] xNew = (double[])x.Clone();
            for (int c = 0; c < m; c++)
            {
                int j = jacIdx[c];
                double d = step[c];
                if (vars[j].Remote) d = Math.Clamp(d, -MaxVoltageStepPu, MaxVoltageStepPu);
                xNew[j] = Math.Clamp(x[j] + d, vars[j].Min, vars[j].Max);
            }

            (PowerFlowResult resNew, double[]? gNew, bool[] pinNew, bool[] roleNew) = Eval(xNew, ownPins: true);
            if (gNew is null) return Fail(resNew, xNew, null, vars, "the engine did not converge after a control step");

            // Broyden's rank-one update on the free block: J += (dg - J dx) dx^T / (dx^T dx).
            double dxx = 0.0;
            var dxv = new double[m];
            for (int c = 0; c < m; c++) { dxv[c] = xNew[jacIdx[c]] - x[jacIdx[c]]; dxx += dxv[c] * dxv[c]; }
            if (dxx > 0.0)
            {
                for (int rIdx = 0; rIdx < m; rIdx++)
                {
                    double jdx = 0.0;
                    for (int c = 0; c < m; c++) jdx += jac[rIdx, c] * dxv[c];
                    double resid = (gNew[jacIdx[rIdx]] - g[jacIdx[rIdx]]) - jdx;
                    for (int c = 0; c < m; c++) jac[rIdx, c] += resid * dxv[c] / dxx;
                }
            }
            recompute = Norm(gNew, jacIdx) > 0.5 * Norm(g, jacIdx);
            x = xNew;
            g = gNew;
            res = resNew;
            pin = pinNew;
            role = roleNew;
        }
    }

    private static double Norm(double[] g, int[] idx)
    {
        double s = 0.0;
        foreach (int j in idx) s = Math.Max(s, Math.Abs(g[j]));
        return s;
    }

    /// <summary>Gaussian elimination with partial pivoting (row order fixed, so the result is deterministic); null when a
    /// pivot is negligible against the matrix's scale (singular).</summary>
    private static double[]? SolveDense(double[,] a0, double[] b0)
    {
        int m = b0.Length;
        var a = (double[,])a0.Clone();
        var b = (double[])b0.Clone();
        double scale = 0.0;
        foreach (double v in a) scale = Math.Max(scale, Math.Abs(v));
        if (!(scale > 0.0) || !double.IsFinite(scale)) return null;
        for (int k = 0; k < m; k++)
        {
            int piv = k;
            for (int i = k + 1; i < m; i++) if (Math.Abs(a[i, k]) > Math.Abs(a[piv, k])) piv = i;
            if (Math.Abs(a[piv, k]) <= 1e-12 * scale) return null;
            if (piv != k)
            {
                for (int c = 0; c < m; c++) (a[k, c], a[piv, c]) = (a[piv, c], a[k, c]);
                (b[k], b[piv]) = (b[piv], b[k]);
            }
            for (int i = k + 1; i < m; i++)
            {
                double f = a[i, k] / a[k, k];
                if (f == 0.0) continue;
                for (int c = k; c < m; c++) a[i, c] -= f * a[k, c];
                b[i] -= f * b[k];
            }
        }
        var xs = new double[m];
        for (int i = m - 1; i >= 0; i--)
        {
            double s = b[i];
            for (int c = i + 1; c < m; c++) s -= a[i, c] * xs[c];
            xs[i] = s / a[i, i];
        }
        foreach (double v in xs) if (!double.IsFinite(v)) return null;
        return xs;
    }

    /// <summary>The settled end: every free control within tolerance. A remote control held at a machine limit must be on
    /// the side the limit explains (at the upper limit the regulated voltage is at or below its target, at the lower
    /// limit at or above); otherwise the state is not consistent and is failed.</summary>
    private PowerFlowResult Finish(PowerFlowResult r, double[] x, double[] g, List<Var> vars, GridModel model, bool[] shareRole, IReadOnlyList<Bus> solvedBuses)
    {
        var states = States(x, g, vars);
        for (int j = 0; j < vars.Count; j++)
        {
            if (!vars[j].Pinned) continue;
            CimRemoteVoltageControl rc = _remote[vars[j].Index];
            // N307: the control's voltage error is carried by its voltage-law member (for a control with one machine bus,
            // the variable itself, as before).
            int vj = j;
            for (int jj = 0; jj < vars.Count; jj++)
                if (vars[jj].Remote && vars[jj].Index == vars[j].Index && !shareRole[jj]) { vj = jj; break; }
            double vErr = g[vj];
            // Is the shortfall in the direction of the limit? Upper limit: the voltage is low (g <= 0). Lower: high.
            bool atUpper = MachinesAtUpperLimit(vars[j].Bus, r, model, solvedBuses);
            string where = rc.GeneratorBusIds.Count > 1 ? $" at bus {vars[j].Bus}" : "";
            if ((atUpper && vErr > _options.TolerancePu) || (!atUpper && vErr < 0.0 - _options.TolerancePu))
                return Fail(r, x, g, vars,
                    $"the machines regulating bus {rc.RegulatedBusId} ({string.Join(", ", rc.Machines)}){where} are at their " +
                    $"{(atUpper ? "upper" : "lower")} reactive limit while the regulated voltage is {(atUpper ? "above" : "below")} its target");
        }
        LastStates = states;
        ControlsSettled = true;
        // N306: what each susceptance control ended at (MVAr at 1.0 pu of its bus, relative to the bridged model for a
        // shunt), for CimBridgeResult.SwitchFlows.
        var susceptance = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int j = 0; j < vars.Count; j++)
            if (!vars[j].Remote) susceptance[_susceptances[vars[j].Index].MRID] = x[j];
        _lastRecord = NewRecord(susceptance);
        return r;
    }

    /// <summary>N306: the record of a settled regulated solve: the bridge, the susceptances, and the machine buses the
    /// remote controls made voltage-controlled (their machines' reactive output is then a solved bus total).</summary>
    private CimControlledSolveRecord NewRecord(Dictionary<string, double> susceptance)
    {
        var machineBuses = new SortedSet<int>();
        foreach (CimRemoteVoltageControl rc in _remote)
            foreach (int gb in rc.GeneratorBusIds) machineBuses.Add(gb);
        return new CimControlledSolveRecord(_bridge, susceptance,
            new Dictionary<string, double>(StringComparer.Ordinal), machineBuses);
    }

    /// <summary>Whether the machines at a pinned remote-control machine bus are at their upper (rather than lower) reactive
    /// limit: the bus's solved reactive generation (net injection + demand) against the sum of its machines' Qmax. N307:
    /// judged per machine bus (with one machine bus per control, the same sums in the same order as before).</summary>
    /// Bug hunt 2026-10-29, finding 52: the demand added back is the SOLVED model's (<paramref name="solvedBuses"/>), the
    /// copy each SVC's held q was taken out of. The original model's Qd used to be added, so at a bus hosting both a
    /// regulating SVC and remote-controlling machines the machines' reactive output was off by that held q and the
    /// upper/lower verdict could be wrong. At every other bus the two Qd are the same number, so nothing else moves.
    private static bool MachinesAtUpperLimit(int gb, PowerFlowResult r, GridModel model, IReadOnlyList<Bus> solvedBuses)
    {
        double q = 0.0, qMax = 0.0;
        var index = new Dictionary<int, int>();
        for (int i = 0; i < r.BusIds.Count; i++) index.TryAdd(r.BusIds[i], i);
        Bus bus = solvedBuses.First(b => b.Id == gb);
        q += r.QInjMvar[index[gb]] + bus.Qd;
        foreach (Generator gen in model.Generators) if (gen.Bus == gb) qMax += gen.Qmax;
        return q >= qMax - NewtonShellTolerance;
    }

    // NewtonShell.QLimitTolMva is internal to the power-flow namespace's assembly; the same 1e-3 MVAr is used here.
    private const double NewtonShellTolerance = 1e-3;

    private List<CimControlState> States(double[] x, double[]? g, List<Var> vars)
    {
        var states = new List<CimControlState>(vars.Count);
        for (int j = 0; j < vars.Count; j++)
        {
            Var v = vars[j];
            double err = g is null ? double.NaN : g[j];
            if (v.Remote)
            {
                CimRemoteVoltageControl rc = _remote[v.Index];
                // N307: one state per machine bus; a control with one machine bus keeps its one state and its old name.
                string at = rc.GeneratorBusIds.Count > 1 ? $" (machine bus {v.Bus})" : "";
                states.Add(new CimControlState($"bus {rc.RegulatedBusId} by {string.Join(", ", rc.Machines)}{at}", "remote voltage",
                    x[j], err, v.Pinned));
            }
            else
            {
                CimSusceptanceControl sc = _susceptances[v.Index];
                bool atLimit = x[j] >= v.Max || x[j] <= v.Min;
                states.Add(new CimControlState($"{sc.Class} {sc.Name}", "susceptance", x[j], err, atLimit));
            }
        }
        return states;
    }

    /// <summary>Fail loud: the state is returned as NOT converged (so no caller can report it as regulated), with the
    /// reason in <see cref="LastNote"/>.</summary>
    private PowerFlowResult Fail(PowerFlowResult r, double[] x, double[]? g, List<Var> vars, string why)
    {
        LastNote = why;
        ControlsSettled = false;
        LastStates = States(x, g, vars);
        return NotConverged(r);
    }

    /// <summary>A copy of a converged engine result marked NOT converged (status MaxIterations): the state is kept for
    /// inspection but can never be reported as regulated. A result already not converged is returned as it is.</summary>
    private static PowerFlowResult NotConverged(PowerFlowResult r)
    {
        if (!r.Converged) return r;
        return new PowerFlowResult
        {
            Converged = false,
            Status = PowerFlowStatus.MaxIterations,
            Iterations = r.Iterations,
            MaxMismatch = r.MaxMismatch,
            BusIds = r.BusIds,
            Vm = r.Vm,
            VaDeg = r.VaDeg,
            PInjMw = r.PInjMw,
            QInjMvar = r.QInjMvar,
            TotalLossMw = r.TotalLossMw,
            TotalLossMvar = r.TotalLossMvar,
            LineFlows = r.LineFlows,
            SolvedBusTypes = r.SolvedBusTypes,
            SweepVoltageStepPu = r.SweepVoltageStepPu,
        };
    }
}
