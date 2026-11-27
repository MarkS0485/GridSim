using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Model;
using GridSim.Core.Protection;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// RC5 stage 6: one unit relay stage's behaviour over a full-model (fixed-step) RMS run.
/// </summary>
/// <param name="Unit">The unit (generator name, or a RAW machine's key &lt;bus&gt;:&lt;id&gt;).</param>
/// <param name="Device">The stage (for example 40-1, 81U-1, 32).</param>
/// <param name="Operated">Whether the stage operated (its definite time was reached while beyond pickup).</param>
/// <param name="Tripped">Whether its operation disconnected the unit (false when the run ended instead, the unit being the
/// last connected machine, or when another stage of the unit operated first).</param>
/// <param name="PickupSeconds">The step time it last picked up (before its operation, or still picked up at the end); null if never.</param>
/// <param name="OperateSeconds">The step time it operated at; null if it did not.</param>
/// <param name="DisconnectedSeconds">The step time from which the unit was out (one step after the operation); null if not tripped.</param>
/// <param name="OperateQuantity">The quantity it read when it operated (Hz, Hz/s, pu, MW, deg, or for 40 the impedance
/// magnitude in pu of the unit rating).</param>
/// <param name="ImpedanceRPu">40 only: the apparent resistance at the terminals when it operated (pu of the unit rating).</param>
/// <param name="ImpedanceXPu">40 only: the apparent reactance at the terminals when it operated (pu of the unit rating).</param>
/// <param name="Settings">The settings phrase: each value with its grade (placeholder or sourced), layer and provenance tag.</param>
public sealed record FullModelUnitRelayRecord(string Unit, string Device, bool Operated, bool Tripped, double? PickupSeconds,
    double? OperateSeconds, double? DisconnectedSeconds, double? OperateQuantity, double? ImpedanceRPu, double? ImpedanceXPu,
    string Settings)
{
    /// <summary>27 / 59 of a unit with a folded unit transformer: the voltage the stage read (the machine terminal behind the
    /// transformer, not the network bus). Null (and not written) when the stage read the unit's network bus.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VoltageMeasuredAt { get; init; }
}

/// <summary>
/// RC5 wp32b: one branch-bound user relay's behaviour over a full-model (fixed-step) RMS run (a relay plug-in or a declarative
/// relay bound with <see cref="Plugins.UserRelayBinding.ForBranch(int, int, Plugins.PluginModel, string)"/>).
/// </summary>
/// <param name="Branch">The branch's name (the binding's label, or "branch &lt;index&gt;").</param>
/// <param name="BranchIndex">The branch's index in <c>model.Branches</c>.</param>
/// <param name="MeasuredAtBus">The bus at the end the relay reads.</param>
/// <param name="Device">The relay: <c>PLUGIN:ID</c> or <c>USER:ID</c>.</param>
/// <param name="Operated">Whether the relay gave a trip.</param>
/// <param name="Tripped">Whether its trip opened the branch (false when the run ended at that step instead).</param>
/// <param name="OperateSeconds">The step time it tripped at; null if it did not.</param>
/// <param name="DisconnectedSeconds">The step time from which the branch was out (one step after the trip); null if not tripped.</param>
/// <param name="ReasonCode">The reason code the relay gave with its trip; null if it did not trip.</param>
/// <param name="Settings">The relay's provenance (a plug-in's carries OUTSIDE THE VERIFIED CONTRACT).</param>
public sealed record FullModelBranchRelayRecord(string Branch, int BranchIndex, int MeasuredAtBus, string Device, bool Operated, bool Tripped,
    double? OperateSeconds, double? DisconnectedSeconds, double? ReasonCode, string Settings);

/// <summary>
/// RC5 wp32b: what the relay host needs to run branch-bound user relays: the network's bus index, the scheduled stage network
/// rebuilt with relay-tripped machines AND branches out, and whether a branch is out by a scheduled event in a stage.
/// </summary>
internal sealed record BranchRelaySupport(IReadOnlyDictionary<int, int> BusIndex, Func<int, bool[], bool[], IRmsNetwork> NetworkFor,
    Func<int, int, bool> ScheduledOut);

/// <summary>
/// RC5 stage 6: each generating unit's own relays in the full-model fixed-step RMS engine (<see cref="MultiMachineSubtransient"/>,
/// six-state machines with flux decay and their field controllers), opt-in. One entry per resolved stage
/// (<see cref="UnitProtectionSet"/>, the definitions and settings the passive assessment and the adaptive loop use) of a unit
/// integrated as a synchronous machine.
/// <para>
/// <b>What each stage reads</b>, at every step start from that step's committed network solve: 81U / 81O the machine's
/// electrical frequency F0 (1 + w); 81R F0 dw/dt from the swing equation with the step's mechanical and electrical power (no
/// relay measurement window); 27 / 59 |V_t| with V_t = E'' - jX'' I, the voltage of the unit's network bus (with a folded unit
/// transformer, the machine terminal on its generator side, V_t + j X_t I, X_t on the system base); 32 the electrical
/// power Re(E'' conj I) in MW; 40 the apparent impedance at the machine terminals Z = V_t / I, converted to pu of the unit
/// rating (Z_sys MBase / BaseMva), with a folded unit transformer read on its generator side (V_t + j X_t I, X_t on the system
/// base: the network bus is the transformer's HV side), inside the offset mho zone (centre -j(offset + D/2), radius D/2, offset and diameter in pu of the
/// unit rating); 78 the jump of the V_t angle across a network change, at the instant of the change (the previous step's
/// network solved again with the same EMFs and converter currents).
/// </para>
/// <para>
/// <b>Definite time on the fixed step.</b> A stage picks up at the first step k_p at which it is beyond its pickup and stays
/// so; it operates at the first step k with (k - k_p) h + h 1e-6 &gt;= delay (never early; the operate time resolution is one
/// step); a step not beyond resets it (the G59 / G99 reset note). A delay of 0 s operates on the pickup step. 78 is
/// instantaneous; a 78 with a delay above the stage tolerance is listed not evaluated.
/// </para>
/// <para>
/// <b>Trip.</b> An operating stage disconnects its unit from the next step on (one step after the operation): the network
/// in force is rebuilt without the machine, its mechanical power is zero, its field held and it is no longer judged, exactly
/// as a scheduled generator trip. A unit trips once (unit order, then stage order, at one step). If the trip would leave no
/// connected machine, the run ends at that step with the reason stated.
/// </para>
/// </summary>
internal sealed class FullModelUnitRelays
{
    // XtSys: a folded unit transformer's reactance on the system base (UnitTransformerXPu x BaseMva / MBase), 40 and 27 / 59; 0
    // when the unit has none (the terminal is then the network bus and the arithmetic is the stage 6 one, untouched).
    // VoltageAt: 27 / 59 behind a unit transformer, the record's statement of the voltage read; null otherwise.
    private sealed record Entry(string Unit, int Slot, UnitRelayStage Stage, double MBase, double ThresholdMw, double XtSys = 0.0,
        string? VoltageAt = null);

    private readonly List<Entry> _entries = new();

    // RC5 WP32: the contract 2 relay plug-ins (UserRelayBinding), each a fresh instance for this run, evaluated after the
    // built-in stages at every step. Empty without user relays, and then nothing below reads it.
    private sealed record UserEntry(string Unit, int Slot, Plugins.UserRelayBinding Binding, Plugins.IUserRelay Relay);

    private readonly List<UserEntry> _user = new();

    // RC5 wp32b: branch-bound user relays (none without them, and then nothing below reads these). A branch relay reads the end
    // at MeasuredIndex: I = YSelf V_measured + YOther V_other (the branch's pi-model stamp), S = V conj(I).
    private sealed record BranchEntry(Plugins.UserRelayBinding Binding, Plugins.IUserRelay Relay, int Branch, string Label,
        int MeasuredIndex, int OtherIndex, Complex YSelf, Complex YOther);

    private readonly List<BranchEntry> _branchUser = new();
    private readonly BranchRelaySupport? _branchSupport;
    private readonly bool[]? _branchOut;
    private readonly double?[] _bOperate = Array.Empty<double?>();
    private readonly double?[] _bDisconnected = Array.Empty<double?>();
    private readonly double?[] _bReason = Array.Empty<double?>();
    private readonly bool[] _bTripped = Array.Empty<bool>();
    private Complex[]? _busV;
    private int _stage = -1;
    private readonly double?[] _uOperate = Array.Empty<double?>();
    private readonly double?[] _uDisconnected = Array.Empty<double?>();
    private readonly double?[] _uReason = Array.Empty<double?>();
    private readonly bool[] _uTripped = Array.Empty<bool>();
    private readonly List<UnitRelayNotEvaluated> _skipped = new();
    private readonly int[] _pickupStep;
    private readonly double?[] _lastPickup;
    private readonly double?[] _operate;
    private readonly double?[] _disconnected;
    private readonly double?[] _quantity;
    private readonly double?[] _zr, _zx;
    private readonly bool[] _tripped;
    private readonly double _step, _eps, _f0, _baseMva;
    private readonly double[] _xpp;
    private readonly bool[] _out;           // machines a unit relay has disconnected (by slot)
    private readonly bool[] _slotDone;      // the unit's machine is out (by its own relay or a scheduled trip)
    private readonly Func<int, bool[], IRmsNetwork> _networkFor;
    private readonly Dictionary<(int Stage, string Mask), IRmsNetwork> _cache = new();
    private IRmsNetwork? _previous;
    private Complex[]? _imPrev, _vcPrev;
    private readonly bool _vectorShift;

    /// <summary>
    /// Build from the model's unit protection. <paramref name="slotOfGenerator"/> maps <c>model.Generators</c> to machine slots
    /// (-1 when the unit is not integrated as a machine); <paramref name="machines"/> are the prepared machines (system base);
    /// <paramref name="networkFor"/> gives the network of scheduled stage s (-1: the base) with the machines of the mask out.
    /// </summary>
    internal FullModelUnitRelays(GridModel model, UnitProtectionSet? units, IReadOnlyList<int> slotOfGenerator,
        IReadOnlyList<MultiMachineSubtransient.Machine> machines, double nominalHz, double stepSeconds,
        Func<int, bool[], IRmsNetwork> networkFor, IReadOnlyList<Plugins.UserRelayBinding>? userRelays = null,
        BranchRelaySupport? branchSupport = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        // RC5 WP32: the unit protection may be absent when contract 2 relay plug-ins are given (and only then).
        if (units is null && userRelays is not { Count: > 0 }) throw new ArgumentNullException(nameof(units));
        _networkFor = networkFor ?? throw new ArgumentNullException(nameof(networkFor));
        _step = stepSeconds;
        _eps = stepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        _f0 = nominalHz;
        _baseMva = model.BaseMva;
        int n = machines.Count;
        _xpp = new double[n];
        for (int i = 0; i < n; i++) _xpp[i] = machines[i].Xdpp;
        _out = new bool[n];
        _slotDone = new bool[n];

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < model.Generators.Count; g++) if (model.Generators[g].Name is { } nm) byName.TryAdd(nm, g);
        foreach (ResolvedUnitProtection u in units?.Resolved ?? Array.Empty<ResolvedUnitProtection>())
        {
            int g = byName.GetValueOrDefault(u.Unit, -1);
            foreach (UnitRelayStage st in u.Stages)
            {
                string? why = null;
                Generator? gen = g >= 0 ? model.Generators[g] : null;
                int slot = g >= 0 ? slotOfGenerator[g] : -1;
                if (gen is null) why = "the unit is out of service";
                else if (slot < 0) why = "the unit is not integrated as a synchronous machine in this run (converter plant, a held unit or a power-flow device)";
                else if (st.Function is UnitRelayFunction.UnderFrequency or UnitRelayFunction.OverFrequency
                         && BitConverter.DoubleToInt64Bits(u.SettingsNominalHz) != BitConverter.DoubleToInt64Bits(nominalHz))
                    why = $"its pickup is written for a {F(u.SettingsNominalHz)} Hz system and this run is {F(nominalHz)} Hz";
                else if (st.Function == UnitRelayFunction.VectorShift && st.Delay.Value > _eps)
                    why = $"a vector shift is a step at a network change; a {F(st.Delay.Value)} s delay on it is not modelled";
                else if (st.Function == UnitRelayFunction.ReversePower && (!(gen.MBase > 0.0) || !double.IsFinite(gen.MBase)))
                    why = $"the unit has no rating (MBase {gen.MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so a reverse-power pickup in pu of it has no value";
                else if (st.Function == UnitRelayFunction.LossOfExcitation)
                {
                    MultiMachineSubtransient.Machine mc = machines[slot];
                    if (!(gen.MBase > 0.0) || !double.IsFinite(gen.MBase))
                        why = $"the unit has no rating (MBase {gen.MBase.ToString("R", CultureInfo.InvariantCulture)} MVA), so the mho zone (pu of the rating) has no impedance base";
                    else if (!double.IsFinite(gen.UnitTransformerXPu))
                        why = $"the unit transformer reactance is not finite ({gen.UnitTransformerXPu.ToString("R", CultureInfo.InvariantCulture)} pu), so the machine terminal cannot be recovered";
                    else if (!(mc.Xd > mc.Xdp))
                        why = "the machine has no field dynamics (Xd not above X'd: the constant-EMF reduction), so a field failure cannot be represented";
                }
                else if (st.Function is UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage
                         && gen.UnitTransformerXPu > 0.0 && !double.IsFinite(gen.UnitTransformerXPu))
                    why = $"the unit transformer reactance is not finite ({gen.UnitTransformerXPu.ToString("R", CultureInfo.InvariantCulture)} pu), so the machine terminal voltage cannot be recovered";
                if (why is not null) { _skipped.Add(new UnitRelayNotEvaluated(u.Unit, st.Device, why)); continue; }
                // 40 and 27 / 59 behind a folded unit transformer: the relay sees the machine terminal, the generator side of the
                // transformer (its VTs are there).
                bool terminal = st.Function is UnitRelayFunction.LossOfExcitation or UnitRelayFunction.UnderVoltage or UnitRelayFunction.OverVoltage
                    && gen!.UnitTransformerXPu > 0.0;
                double xtSys = terminal ? UnitTerminal.SystemBaseXt(gen!, _baseMva) : 0.0;
                string? voltageAt = terminal && st.Function != UnitRelayFunction.LossOfExcitation
                    ? UnitTerminal.TerminalPhrase(gen!.UnitTransformerXPu, gen.Bus) : null;
                _entries.Add(new Entry(u.Unit, slot, st, gen!.MBase, st.Pickup.Value * gen.MBase, xtSys, voltageAt));
                if (st.Function == UnitRelayFunction.VectorShift) _vectorShift = true;
            }
        }
        if (userRelays is { Count: > 0 })
        {
            // RC5 WP32: a relay plug-in on a unit the case does not name is refused; on a unit that is not integrated as a
            // synchronous machine it is listed not evaluated, as a built-in stage is.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Generator gg in model.AllGenerators) if (gg.Name is { } nn) names.Add(nn);
            foreach (Plugins.UserRelayBinding b in userRelays)
            {
                ArgumentNullException.ThrowIfNull(b, nameof(userRelays));
                if (b.BranchIndex is int bi)
                {
                    // RC5 wp32b: a branch relay. The branch must be in the model and in service, and the measuring bus one of its ends.
                    string who = $"relay {b.Device} on branch {bi.ToString(CultureInfo.InvariantCulture)}";
                    if (branchSupport is null)
                        throw new ArgumentException($"{who}: this run has no network to read a branch from.", nameof(userRelays));
                    if (bi >= model.Branches.Count)
                        throw new ArgumentException($"{who}: the model has {model.Branches.Count.ToString(CultureInfo.InvariantCulture)} branches.", nameof(userRelays));
                    Branch br = model.Branches[bi];
                    if (!br.InService)
                        throw new ArgumentException($"{who}: the branch is out of service in the model.", nameof(userRelays));
                    if (b.MeasuredAtBus != br.From && b.MeasuredAtBus != br.To)
                        throw new ArgumentException($"{who}: bus {b.MeasuredAtBus.ToString(CultureInfo.InvariantCulture)} is not an end of the branch ({br.From.ToString(CultureInfo.InvariantCulture)} - {br.To.ToString(CultureInfo.InvariantCulture)}).", nameof(userRelays));
                    PowerFlow.BranchStamp st = PowerFlow.AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                    bool atFrom = b.MeasuredAtBus == br.From;
                    int fi = branchSupport.BusIndex[br.From], ti = branchSupport.BusIndex[br.To];
                    Plugins.IUserRelay branchRelay = b.NewInstance();
                    branchRelay.Initialise();
                    _branchUser.Add(new BranchEntry(b, branchRelay, bi, b.BranchLabel ?? "branch " + bi.ToString(CultureInfo.InvariantCulture),
                        atFrom ? fi : ti, atFrom ? ti : fi, atFrom ? st.Yff : st.Ytt, atFrom ? st.Yft : st.Ytf));
                    continue;
                }
                string unitName = b.Unit!;
                if (!names.Contains(unitName))
                    throw new ArgumentException($"relay {b.Device}: the case has no unit named '{unitName}'.", nameof(userRelays));
                int g = byName.GetValueOrDefault(unitName, -1);
                int slot = g >= 0 ? slotOfGenerator[g] : -1;
                if (g < 0) { _skipped.Add(new UnitRelayNotEvaluated(unitName, b.Device, "the unit is out of service")); continue; }
                if (slot < 0)
                {
                    _skipped.Add(new UnitRelayNotEvaluated(unitName, b.Device, "the unit is not integrated as a synchronous machine in this run (converter plant, a held unit or a power-flow device)"));
                    continue;
                }
                Plugins.IUserRelay relay = b.NewInstance();
                relay.Initialise();
                _user.Add(new UserEntry(unitName, slot, b, relay));
            }
            int cu = _user.Count;
            _uOperate = new double?[cu];
            _uDisconnected = new double?[cu];
            _uReason = new double?[cu];
            _uTripped = new bool[cu];
            int cb = _branchUser.Count;
            if (cb > 0)
            {
                _branchSupport = branchSupport;
                _branchOut = new bool[model.Branches.Count];
                _bOperate = new double?[cb];
                _bDisconnected = new double?[cb];
                _bReason = new double?[cb];
                _bTripped = new bool[cb];
            }
        }
        int c = _entries.Count;
        _pickupStep = new int[c];
        Array.Fill(_pickupStep, -1);
        _lastPickup = new double?[c];
        _operate = new double?[c];
        _disconnected = new double?[c];
        _quantity = new double?[c];
        _zr = new double?[c];
        _zx = new double?[c];
        _tripped = new bool[c];
    }

    /// <summary>Number of evaluated stages (RC5 WP32: with the evaluated relay plug-ins; RC5 wp32b: and branch relays).</summary>
    internal int Count => _entries.Count + _user.Count + _branchUser.Count;

    /// <summary>The stages listed as not evaluated, with reasons.</summary>
    internal IReadOnlyList<UnitRelayNotEvaluated> NotEvaluated => _skipped;

    /// <summary>Why the run ended before its duration (the last connected machine), or null.</summary>
    internal string? EndReason { get; private set; }

    /// <summary>The time of the last sample when the run ended early, or null.</summary>
    internal double? EndedAtSeconds { get; private set; }

    /// <summary>The per-stage records, in unit and stage order.</summary>
    internal IReadOnlyList<FullModelUnitRelayRecord> Records()
    {
        var list = new List<FullModelUnitRelayRecord>(_entries.Count);
        for (int j = 0; j < _entries.Count; j++)
        {
            Entry e = _entries[j];
            list.Add(new FullModelUnitRelayRecord(e.Unit, e.Stage.Device, _operate[j] is not null, _tripped[j], _lastPickup[j],
                _operate[j], _disconnected[j], _quantity[j], _zr[j], _zx[j], UnitRelayAssessment.SettingsPhrase(e.Stage))
            { VoltageMeasuredAt = e.VoltageAt });
        }
        // RC5 WP32: the relay plug-ins, after the built-in stages: no pickup time (a plug-in keeps its own timers), the reason
        // code it gave in place of the operate quantity, and its provenance (with the OUTSIDE THE VERIFIED CONTRACT label) as the
        // settings phrase.
        for (int j = 0; j < _user.Count; j++)
        {
            UserEntry u = _user[j];
            list.Add(new FullModelUnitRelayRecord(u.Unit, u.Binding.Device, _uOperate[j] is not null, _uTripped[j], null,
                _uOperate[j], _uDisconnected[j], _uReason[j], null, null, u.Binding.ProvenanceNote));
        }
        return list;
    }

    /// <summary>RC5 wp32b: the branch relays' records, in binding order (empty without branch relays).</summary>
    internal IReadOnlyList<FullModelBranchRelayRecord> BranchRecords()
    {
        var list = new List<FullModelBranchRelayRecord>(_branchUser.Count);
        for (int j = 0; j < _branchUser.Count; j++)
        {
            BranchEntry b = _branchUser[j];
            list.Add(new FullModelBranchRelayRecord(b.Label, b.Branch, b.Binding.MeasuredAtBus, b.Binding.Device, _bOperate[j] is not null,
                _bTripped[j], _bOperate[j], _bDisconnected[j], _bReason[j], b.Binding.ProvenanceNote));
        }
        return list;
    }

    /// <summary>The network in force at step time <c>t</c>: the scheduled one, or (once a unit relay has disconnected a
    /// machine) the scheduled stage <paramref name="stageIndex"/> rebuilt without every relay-tripped machine.</summary>
    internal IRmsNetwork NetworkAt(int stageIndex, IRmsNetwork scheduled)
    {
        _stage = stageIndex;
        if (_branchOut is not null && Array.IndexOf(_branchOut, true) >= 0) return BranchNetwork(stageIndex);
        bool any = false;
        for (int i = 0; i < _out.Length; i++) any |= _out[i];
        if (!any) return scheduled;
        var key = (stageIndex, MaskKey(_out));
        if (_cache.TryGetValue(key, out IRmsNetwork? net)) return net;
        net = _networkFor(stageIndex, (bool[])_out.Clone());
        _cache[key] = net;
        return net;
    }

    /// <summary>RC5 wp32b: the scheduled stage rebuilt with every relay-tripped machine and branch out (cached per masks).</summary>
    private IRmsNetwork BranchNetwork(int stageIndex)
    {
        var key = (stageIndex, MaskKey(_out) + "|" + MaskKey(_branchOut!));
        if (_cache.TryGetValue(key, out IRmsNetwork? net)) return net;
        try { net = _branchSupport!.NetworkFor(stageIndex, (bool[])_out.Clone(), (bool[])_branchOut!.Clone()); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("a branch opened by a user relay leaves a network that cannot be solved in a later network stage: "
                + ex.Message, ex);
        }
        _cache[key] = net;
        return net;
    }

    private static string MaskKey(bool[] mask)
    {
        var c = new char[mask.Length];
        for (int i = 0; i < mask.Length; i++) c[i] = mask[i] ? '1' : '0';
        return new string(c);
    }

    /// <summary>True when at least one machine of the synchronism set is connected at <paramref name="t"/>.</summary>
    internal bool AnyConnected(double t, double[] tripAt, int syncCount)
    {
        for (int i = 0; i < syncCount; i++) if (!(t + _eps >= tripAt[i])) return true;
        return false;
    }

    /// <summary>Record that no machine is left at step time <paramref name="t"/>, so the run ended at its previous sample
    /// <paramref name="lastSampleSeconds"/>.</summary>
    internal void EndNoMachine(double t, double lastSampleSeconds)
    {
        EndReason ??= $"no machine of the synchronism set is connected at {F(t)} s (scheduled and unit relay trips together), so the run ends at the step before";
        EndedAtSeconds ??= lastSampleSeconds;
    }

    /// <summary>
    /// Evaluate every stage at step <paramref name="k"/> (time <paramref name="t"/>) from the step's committed solve and apply
    /// the trips (from step k + 1 on, through <paramref name="tripAt"/>). Returns false when the run must end here (a trip of
    /// the last connected machine).
    /// </summary>
    internal bool Evaluate(int k, double t, IRmsNetwork net, Complex[] e, Complex[] im, Complex[] ic, double[] omega,
        double[] pm, double[] damping, double[] inertia, double[] tripAt, int syncCount)
    {
        // Machines already out (a scheduled trip or an earlier relay trip): their stages stop.
        for (int i = 0; i < _slotDone.Length; i++) if (t + _eps >= tripAt[i]) _slotDone[i] = true;

        // 78: the V_t angle jump across a network change at this instant (the previous network, the same EMFs and currents).
        Complex[]? vtBefore = null;
        if (_vectorShift && _previous is not null && !ReferenceEquals(_previous, net))
        {
            _imPrev ??= new Complex[e.Length];
            _vcPrev ??= new Complex[ic.Length];
            _previous.SolveInto(e, ic, _imPrev, _vcPrev);
            vtBefore = new Complex[e.Length];
            for (int i = 0; i < e.Length; i++) vtBefore[i] = Vt(e[i], _imPrev[i], _xpp[i]);
        }
        _previous = net;

        double tNext = (k + 1) * _step;
        var trippedNow = new HashSet<int>();
        for (int j = 0; j < _entries.Count; j++)
        {
            Entry en = _entries[j];
            int s = en.Slot;
            if (_slotDone[s] || trippedNow.Contains(s)) continue;
            Complex vt = Vt(e[s], im[s], _xpp[s]);
            double q = double.NaN, zr = double.NaN, zx = double.NaN;
            bool beyond;
            double p = en.Stage.Pickup.Value;
            switch (en.Stage.Function)
            {
                case UnitRelayFunction.UnderFrequency: q = _f0 * (1.0 + omega[s]); beyond = q < p; break;
                case UnitRelayFunction.OverFrequency: q = _f0 * (1.0 + omega[s]); beyond = q > p; break;
                case UnitRelayFunction.Rocof:
                {
                    double pe = e[s].Real * im[s].Real + e[s].Imaginary * im[s].Imaginary;
                    q = _f0 * ((pm[s] - pe - damping[s] * omega[s]) / (2.0 * inertia[s]));
                    beyond = Math.Abs(q) > p;
                    break;
                }
                case UnitRelayFunction.UnderVoltage: q = en.XtSys > 0.0 ? UnitTerminal.Magnitude(vt, im[s], en.XtSys) : Mag(vt); beyond = q < p; break;
                case UnitRelayFunction.OverVoltage: q = en.XtSys > 0.0 ? UnitTerminal.Magnitude(vt, im[s], en.XtSys) : Mag(vt); beyond = q > p; break;
                case UnitRelayFunction.ReversePower:
                    q = (e[s].Real * im[s].Real + e[s].Imaginary * im[s].Imaginary) * _baseMva;
                    beyond = q < -en.ThresholdMw;
                    break;
                case UnitRelayFunction.LossOfExcitation:
                {
                    double i2 = im[s].Real * im[s].Real + im[s].Imaginary * im[s].Imaginary;
                    if (!(i2 > 0.0)) { beyond = false; break; }   // no current: the impedance is unbounded, outside every zone
                    // A folded unit transformer: V_t above is its HV bus (E'' - j(X''m + Xt) I); the machine terminal is
                    // V_term = V_hv + j Xt I = E'' - j X''m I (I leaves the machine, generator convention, system base).
                    if (en.XtSys > 0.0) vt = UnitTerminal.Voltage(vt, im[s], en.XtSys);
                    // Z = V conj(I) / |I|^2 on the system base, then on the unit rating (x MBase / BaseMva).
                    double scale = en.MBase / _baseMva / i2;
                    zr = (vt.Real * im[s].Real + vt.Imaginary * im[s].Imaginary) * scale;
                    zx = (vt.Imaginary * im[s].Real - vt.Real * im[s].Imaginary) * scale;
                    double radius = 0.5 * en.Stage.Diameter!.Value;
                    double cy = -(p + radius);
                    double dx = zr, dy = zx - cy;
                    beyond = dx * dx + dy * dy < radius * radius;
                    q = Math.Sqrt(zr * zr + zx * zx);
                    break;
                }
                case UnitRelayFunction.VectorShift:
                    if (vtBefore is null) { beyond = false; break; }
                    q = UnitRelaysInLoop.ShiftDeg(vtBefore[s], vt);
                    beyond = q > p;
                    break;
                default: beyond = false; break;
            }
            if (!beyond)
            {
                _pickupStep[j] = -1;   // dropout resets the timer
                continue;
            }
            if (_pickupStep[j] < 0) { _pickupStep[j] = k; _lastPickup[j] = t; }
            double elapsed = (k - _pickupStep[j]) * _step;
            if (!(elapsed + _eps >= en.Stage.Delay.Value)) continue;

            // Operates at this step.
            _operate[j] = t;
            _quantity[j] = q;
            if (en.Stage.Function == UnitRelayFunction.LossOfExcitation) { _zr[j] = zr; _zx[j] = zx; }
            // Would this leave no connected machine of the synchronism set from the next step?
            bool left = false;
            for (int i = 0; i < syncCount; i++)
                if (i != s && !trippedNow.Contains(i) && !(tNext + _eps >= tripAt[i])) { left = true; break; }
            if (!left && s < syncCount)
            {
                EndReason = $"unit {en.Unit} relay {en.Stage.Device} operated at {F(t)} s; its machine is the last one connected, so the run ends there (no continuation is fabricated)";
                EndedAtSeconds = t;
                return false;
            }
            trippedNow.Add(s);
            _tripped[j] = true;
            _disconnected[j] = tNext;
        }
        // RC5 WP32: the relay plug-ins, from the same solve, after the built-in stages (none without them).
        for (int j = 0; j < _user.Count; j++)
        {
            UserEntry u = _user[j];
            int s = u.Slot;
            if (_slotDone[s] || trippedNow.Contains(s)) continue;
            Complex vt = Vt(e[s], im[s], _xpp[s]);
            double pe = e[s].Real * im[s].Real + e[s].Imaginary * im[s].Imaginary;
            double qt = vt.Imaginary * im[s].Real - vt.Real * im[s].Imaginary;
            double rocof = _f0 * ((pm[s] - pe - damping[s] * omega[s]) / (2.0 * inertia[s]));
            Plugins.RelayDecision d = u.Relay.Evaluate(new Plugins.RelayInputs(k, t, _step, Mag(vt), _f0 * (1.0 + omega[s]), rocof, pe, qt, omega[s], Mag(im[s])));
            if (!d.Trip) continue;
            _uOperate[j] = t;
            _uReason[j] = d.ReasonCode;
            bool left = false;
            for (int i = 0; i < syncCount; i++)
                if (i != s && !trippedNow.Contains(i) && !(tNext + _eps >= tripAt[i])) { left = true; break; }
            if (!left && s < syncCount)
            {
                EndReason = $"unit {u.Unit} relay {u.Binding.Device} operated at {F(t)} s; its machine is the last one connected, so the run ends there (no continuation is fabricated)";
                EndedAtSeconds = t;
                return false;
            }
            trippedNow.Add(s);
            _uTripped[j] = true;
            _uDisconnected[j] = tNext;
        }
        foreach (int s in trippedNow)
        {
            _out[s] = true;
            if (tNext < tripAt[s]) tripAt[s] = tNext;
        }
        return _branchUser.Count == 0 || EvaluateBranches(k, t, net, e, ic, tNext);
    }

    /// <summary>
    /// RC5 wp32b: the branch relays at step <paramref name="k"/>, from the same solve (every bus voltage of it, with the same
    /// right-hand side and factorisation: <see cref="SparseRmsNetwork.SolveBusVoltagesInto"/>), after the unit relays. A branch
    /// out by a scheduled event or an earlier relay trip is not evaluated. A trip opens the branch from the next step on; when the
    /// network without it cannot be solved (a part left with no path to ground), the run ends at this step with the reason.
    /// </summary>
    private bool EvaluateBranches(int k, double t, IRmsNetwork net, Complex[] e, Complex[] ic, double tNext)
    {
        SparseRmsNetwork sparse = net as SparseRmsNetwork
            ?? throw new InvalidOperationException("a branch relay reads the bus voltages of the sparse network, which this run does not use.");
        _busV ??= new Complex[sparse.Buses];
        sparse.SolveBusVoltagesInto(e, ic, _busV);
        var trippedNow = new List<int>();
        for (int j = 0; j < _branchUser.Count; j++)
        {
            BranchEntry b = _branchUser[j];
            if (_branchOut![b.Branch] || _branchSupport!.ScheduledOut(_stage, b.Branch)) continue;
            Complex v = _busV[b.MeasuredIndex];
            Complex i = b.YSelf * v + b.YOther * _busV[b.OtherIndex];
            // S = V conj(I): the power flowing into the branch at the measuring end.
            double p = v.Real * i.Real + v.Imaginary * i.Imaginary;
            double q = v.Imaginary * i.Real - v.Real * i.Imaginary;
            Plugins.RelayDecision d = b.Relay.Evaluate(new Plugins.RelayInputs(k, t, _step, Mag(v), double.NaN, double.NaN, p, q, double.NaN, Mag(i)));
            if (!d.Trip) continue;
            _bOperate[j] = t;
            _bReason[j] = d.ReasonCode;
            _branchOut[b.Branch] = true;
            trippedNow.Add(j);
        }
        if (trippedNow.Count == 0) return true;
        // The network the next step uses (unless a scheduled stage starts then) is built now, so that a trip that leaves a part of
        // the network with no path to ground ends the run here, with the reason, rather than failing a step later.
        try { _ = BranchNetwork(_stage); }
        catch (InvalidOperationException ex)
        {
            EndReason = $"a branch relay operated at {F(t)} s and the network without the branch cannot be solved ({ex.Message}); the run ends there (no continuation is fabricated)";
            EndedAtSeconds = t;
            return false;
        }
        foreach (int j in trippedNow)
        {
            _bTripped[j] = true;
            _bDisconnected[j] = tNext;
        }
        return true;
    }

    private static Complex Vt(Complex e, Complex i, double xpp) => new(e.Real + xpp * i.Imaginary, e.Imaginary - xpp * i.Real);

    private static double Mag(Complex v) => Math.Sqrt(v.Real * v.Real + v.Imaginary * v.Imaginary);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
