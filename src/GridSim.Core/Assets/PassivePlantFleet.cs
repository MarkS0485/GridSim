using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Assets;

/// <summary>
/// The steady-state-tick update for one passive plant asset, keyed by its branch.
/// <para><paramref name="BaseRateAMva"/> is the branch's ORIGINAL (un-derated) rating the thermal derate is taken
/// from. The fleet always fills it in; a caller that builds a state by hand may leave it as NaN, in which case
/// <see cref="PassivePlantFleet.ApplyTo"/> treats the model's present rating as the original (correct only for a
/// model that has not already been derated - see the R058 note on ApplyTo).</para>
/// <para><paramref name="FromBus"/>, <paramref name="ToBus"/> and <paramref name="Ordinal"/> pin the update to ONE
/// branch even when several share a name (gb-full has 58 transformer names used by 133 branches, 11 of those groups
/// joining different buses). Ordinal counts earlier branches with the same name AND ends, in model order. The
/// fleet always fills them in; a hand-built state that leaves the ends null matches every branch with that name.</para>
/// </summary>
public readonly record struct BranchPlantState(
    string BranchName, PlantStateUpdate Update, double BaseRateAMva = double.NaN,
    int? FromBus = null, int? ToBus = null, int Ordinal = 0);

/// <summary>
/// The bound passive-plant fleet (transformers, cables) - the slow-tick counterpart of
/// <see cref="AssetDynamicsFleet"/>. Advances every bound passive controller against its branch loading
/// / controlled-bus voltage and returns per-branch tap/rating/thermal updates. These override the
/// solver's instantaneous tap and static rating; they are never swing-power terms. Built from a model's
/// branches and their resolved passive classes; empty when nothing is bound (no effect - reduction gate).
/// </summary>
/// <remarks>
/// <para><b>Wiring (R058, October 2026).</b> Until October 2026 nothing outside the tests ever ran this fleet, so the
/// OLTC and thermal classes bound by <c>data/gb-full/asset-bindings.json</c> were silently ignored. It now runs per
/// tick inside <see cref="Simulation.LiveSim"/> (opt-in: <see cref="Dynamics.DynamicsConfig.EnablePassivePlant"/>)
/// and <see cref="Simulation.GdaReplayLoop"/> (opt-in: the <c>passivePlant</c> argument). Each tick the loop
/// (1) applies the updates from the previous tick to the freshly built operating case BEFORE the solve, then
/// (2) after a CONVERGED solve, advances every controller by the tick length against that solve's branch loading
/// and controlled-bus voltage (<see cref="Advance(double, double, PowerFlowResult, GridModel)"/>), keeping the
/// result for the next tick. A non-converged solve is never fed to the controllers (the R039 rule).</para>
/// <para><b>Who regulates a tap.</b> A transformer governed by a passive (delayed) OLTC controller is no longer also
/// stepped by the solver's instantaneous OLTC loop: <see cref="WithoutSupersededOltcs"/> removes its
/// <see cref="Oltc"/> record from the model handed to the solver, otherwise the instantaneous loop would undo the
/// mechanical delay inside every solve. The controlled bus comes from the <see cref="Oltc"/> record that drives this
/// branch when there is one (its <see cref="Oltc.ControlsBus"/>; the record is matched to its transformer by name AND
/// end bus with the solver's own <see cref="SteadyStateSolver.MapOltcRecordsToBranches"/>, N046, so the two always
/// agree; the first record matched to a branch wins), and is otherwise the branch's To bus (the solver's default rule and the orientation of every OLTC record in gb-full). Target,
/// deadband, step and tap limits come from the bound class, which is the authored dynamics source. A transformer
/// whose controlled bus is generator-regulated (PV or slack) holds its tap (thermal derate only): no tap can move a
/// bus an AVR holds, and trying runs the tap to its limit (see <see cref="TapsHeldAtGeneratorBus"/>). N110 (October
/// 2026): "generator-regulated" is the bus type the solve ENDED with (<see cref="PowerFlowResult.SolvedBusTypes"/>),
/// decided every tick in <see cref="Advance(double, double, PowerFlowResult, GridModel)"/>, the rule N071 gave
/// <see cref="SteadyStateSolver"/>: a PV bus whose units the reactive-limit loop pinned at a limit, or a PV-typed bus
/// with no in-service unit (solved as PQ since R283), has no AVR holding it, so its tap changer regulates it. The
/// fleet used to decide once, at build, from the model's stored types.</para>
/// <para><b>Branch identity.</b> Branch names are not unique in real data, so every controller is keyed by
/// (name, From bus, To bus, ordinal among branches sharing all three). That key survives everything the loops do to
/// a model (demand scaling, storage injection, warm start, opening a branch, dropping dead-island branches), which
/// a plain list index does not.</para>
/// </remarks>
public sealed class PassivePlantFleet
{
    // The identity of one branch (see the class remarks).
    private readonly record struct BranchKey(string Name, int From, int To, int Ordinal);

    // One bound passive asset: its controller plus the branch facts the loop needs every tick.
    // ControlledBus is -1 for a line (no tap, no controlled bus). N110: for a transformer it is always the regulated bus;
    // whether the tap holds (the bus is generator-regulated) is decided per tick from the solved bus types.
    private readonly record struct BoundPlant(IPassiveAssetDynamics Plant, BranchKey Key, int ControlledBus, double BaseRateAMva);

    private readonly BoundPlant[] _plant;
    private readonly IPassiveAssetDynamics[] _controllers;

    // The branch keys of the transformers this fleet regulates, whose solver Oltc records it supersedes (see
    // WithoutSupersededOltcs). GR-137: keyed by the full branch key, not the name, so an unbound namesake keeps its record.
    private readonly HashSet<BranchKey> _tapControlled;

    // The model's nominal frequency, handed to each passive controller as its local frequency (R196: was a
    // literal 50.0). No passive controller (transformer OLTC, line thermal) reads LocalHz today, so this is
    // informational and moves no number; it is carried so a future frequency-aware passive model sees the truth.
    private readonly double _nominalHz;

    private PassivePlantFleet(BoundPlant[] plant, double nominalHz)
    {
        _plant = plant;
        _controllers = plant.Select(p => p.Plant).ToArray();
        _tapControlled = new HashSet<BranchKey>(plant.Where(p => p.Plant is TransformerDynamics).Select(p => p.Key));
        _nominalHz = nominalHz;
    }

    public static PassivePlantFleet Empty { get; } =
        new(Array.Empty<BoundPlant>(), global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz);
    public bool IsEmpty => _plant.Length == 0;
    public int Count => _plant.Length;
    public IReadOnlyList<IPassiveAssetDynamics> Plant => _controllers;

    /// <summary>The synthetic technology label a branch matches binding rules by: "transformer" or "line".</summary>
    public static string BranchType(Branch b) => b.IsTransformer ? "transformer" : "line";

    /// <summary>
    /// The key of every branch of <paramref name="branches"/>, in order: (name, From, To, ordinal), where the ordinal
    /// counts earlier branches with the same name and ends. Unnamed branches get a null key (never bound).
    /// </summary>
    private static BranchKey?[] KeysOf(IReadOnlyList<Branch> branches)
    {
        var keys = new BranchKey?[branches.Count];
        var seen = new Dictionary<(string, int, int), int>();
        for (int i = 0; i < branches.Count; i++)
        {
            Branch b = branches[i];
            if (string.IsNullOrEmpty(b.Name)) continue;
            var id = (b.Name, b.From, b.To);
            int ord = seen.GetValueOrDefault(id, 0);
            seen[id] = ord + 1;
            keys[i] = new BranchKey(b.Name, b.From, b.To, ord);
        }
        return keys;
    }

    /// <summary>
    /// Build from a model's branches and their resolved passive classes (transformer/line kinds only).
    /// Fails loud (InvalidOperationException) when a transformer (OLTC) class is bound to a branch that is not a
    /// transformer (a line has no tap to drive). A transformer whose tap lies outside its class's tap range also
    /// throws (see <see cref="TransformerDynamics"/>).
    /// </summary>
    public static PassivePlantFleet Build(GridModel model)
    {
        if (model.AssetDynamics is not { } spec) return Empty;

        // Branch index -> the bus its Oltc record regulates. Each record is matched to its transformer exactly as the
        // solver's OLTC loop matches it (N046: by name AND end bus, SteadyStateSolver.MapOltcRecordsToBranches); the
        // first record matched to a branch wins.
        int[] branchOfRecord = SteadyStateSolver.MapOltcRecordsToBranches(model);
        var controlsBusOfBranch = new Dictionary<int, int>();
        for (int k = 0; k < model.Oltcs.Count; k++)
            if (branchOfRecord[k] >= 0) controlsBusOfBranch.TryAdd(branchOfRecord[k], model.Oltcs[k].ControlsBus);

        BranchKey?[] keys = KeysOf(model.Branches);
        var plant = new List<BoundPlant>();

        // Bus id -> type, to spot an OLTC aimed at a generator-regulated bus (see below).
        var busTypeOf = new Dictionary<int, BusType>(model.Buses.Count);
        foreach (Bus bus in model.Buses) busTypeOf[bus.Id] = bus.Type;
        int tapsHeld = 0;
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            // A passive controller is keyed by its branch so ApplyTo can map its update back. An unnamed
            // branch has no stable key, so skip it.
            if (keys[i] is not { } key) continue;
            string? className = spec.Bindings.Resolve(b.DynamicsClass, BranchType(b));
            if (className is null) continue;
            AssetClass cls = spec.Library.Get(className);

            IPassiveAssetDynamics? ctrl;
            int controlledBus = -1;
            if (cls.Kind == AssetKind.Transformer)
            {
                if (!b.IsTransformer)
                    throw new InvalidOperationException(
                        $"Passive plant: OLTC class '{cls.Name}' is bound to branch '{b.Name}', which is not a transformer. " +
                        "A line has no tap to drive; bind a line class (kind 'line') or mark the branch as a transformer.");
                // The regulated bus: that of the Oltc record matched to THIS branch (by name and end bus, as the solver
                // matches it), else the To bus. Its side picks the step direction. A same-named transformer the record
                // was not matched to (different ends in gb-full) is not that record's transformer, so it regulates its
                // own To bus.
                controlledBus = controlsBusOfBranch.TryGetValue(i, out int cb) ? cb : b.To;
                bool controlsTappedEnd = controlledBus == b.From && controlledBus != b.To;
                ctrl = new TransformerDynamics(cls, key.Name, b.TapRatio, controlsTappedEnd);

                // A controlled bus the model does not have can never be observed: fail loud rather than hold forever.
                if (!busTypeOf.TryGetValue(controlledBus, out BusType controlledType))
                    throw new InvalidOperationException(
                        $"Passive plant: transformer '{key.Name}' regulates bus {controlledBus}, which is not in the model.");

                // A generator-regulated bus (PV or slack) has its voltage fixed by the machine's AVR, so no tap
                // position can move it. Regulating it anyway runs the tap one step per tick to its limit while the
                // bus stays put (seen on gb-full: 1278 of its 2620 transformers, whose To bus is PV, all climbed to
                // 1.1375 in 12 half-hour steps and dragged the rest of the network's voltages and losses with them).
                // Such a transformer keeps its tap at the branch value and contributes thermal derating only: the OLTC
                // is shown "no measurement" on a tick whose solve left the bus PV or slack, and holds. N110: that is
                // decided per tick from the solved bus types (Advance); this build-time count, by the model's stored
                // types, is what the first tick holds when the solve keeps every type.
                if (controlledType is BusType.Pv or BusType.Slack) tapsHeld++;
            }
            else
            {
                ctrl = AssetControllerFactory.CreatePassive(cls, key.Name);
            }
            if (ctrl is null) continue;   // active class bound to a branch: ignore

            plant.Add(new BoundPlant(ctrl, key, controlledBus, b.RateAMva));
        }
        return plant.Count == 0 ? Empty : new PassivePlantFleet(plant.ToArray(), model.FrequencyHz) { TapsHeldAtGeneratorBus = tapsHeld };
    }

    /// <summary>
    /// How many bound transformers regulate a bus the MODEL types as generator-regulated (PV or slack): the taps held
    /// when the solve keeps every bus type. They still derate thermally. Reported so a run can say how much of the
    /// OLTC population is actually regulating. N110: the hold itself follows the solved types; see
    /// <see cref="TapsHeldLastAdvance"/>.
    /// </summary>
    public int TapsHeldAtGeneratorBus { get; private init; }

    /// <summary>
    /// N110: how many transformers held their tap on the last <see cref="Advance(double, double, PowerFlowResult, GridModel)"/>
    /// because their controlled bus solved as PV or slack (or, for a solver that does not report solved types, is typed
    /// so in the solved model). -1 before the first such advance.
    /// </summary>
    public int TapsHeldLastAdvance { get; private set; } = -1;

    public void Reset() { foreach (BoundPlant p in _plant) p.Plant.Reset(); }

    /// <summary>
    /// The model with every <see cref="Oltc"/> record removed whose transformer this fleet regulates with a
    /// delayed passive OLTC, so the solver's instantaneous OLTC loop does not also step it. Returns the same
    /// instance when nothing is superseded (no copy, so an unaffected model stays reference-identical).
    /// </summary>
    public GridModel WithoutSupersededOltcs(GridModel model)
    {
        if (_tapControlled.Count == 0 || model.Oltcs.Count == 0) return model;
        // GR-137: a record is superseded only when the branch it drives (matched exactly as the solver's OLTC loop
        // matches it, SteadyStateSolver.MapOltcRecordsToBranches: name and end bus) is a transformer this fleet binds.
        // Before, every record with a bound transformer's NAME was removed, so an unbound transformer that shared the
        // name (real data repeats names) lost its regulation. A record that drives no branch is kept (it moves nothing).
        int[] branchOfRecord = SteadyStateSolver.MapOltcRecordsToBranches(model);
        BranchKey?[] keys = KeysOf(model.Branches);
        bool Superseded(int k) => branchOfRecord[k] >= 0 && keys[branchOfRecord[k]] is BranchKey key && _tapControlled.Contains(key);

        var kept = new List<Oltc>(model.Oltcs.Count);
        for (int k = 0; k < model.Oltcs.Count; k++)
            if (!Superseded(k)) kept.Add(model.Oltcs[k]);
        if (kept.Count == model.Oltcs.Count) return model;   // nothing superseded: the same instance, no copy
        return model.CopyWith(oltcs: kept);
    }

    /// <summary>
    /// Apply per-branch plant updates to a model: override each matched branch's <see cref="Branch.TapRatio"/>
    /// with the delayed OLTC tap, and derate its <see cref="Branch.RateAMva"/> by the thermal fraction.
    /// Returns a new immutable model (the branch list rebuilt); other fields are shared. Branches without
    /// an update are unchanged. A state with ends set matches exactly one branch (name, ends, ordinal); a state
    /// without ends matches every branch of that name.
    /// <para>R058 (October 2026): the derate is taken from the ORIGINAL rating
    /// (<see cref="BranchPlantState.BaseRateAMva"/>), so applying the same updates to this method's own output
    /// gives the same ratings again. It used to multiply the model's present rating, so re-applying compounded
    /// the derate (1000 MVA at 20 % became 800, then 640, ...). A state with no base (NaN) falls back to the
    /// model's present rating, which is only right for an un-derated model; the fleet always supplies the base.</para>
    /// </summary>
    public static GridModel ApplyTo(GridModel model, IReadOnlyList<BranchPlantState> updates)
    {
        if (updates.Count == 0) return model;
        var byKey = new Dictionary<BranchKey, BranchPlantState>();
        var byName = new Dictionary<string, BranchPlantState>(StringComparer.Ordinal);
        foreach (BranchPlantState u in updates)
        {
            if (string.IsNullOrEmpty(u.BranchName)) continue;
            if (u.FromBus is { } f && u.ToBus is { } t) byKey[new BranchKey(u.BranchName, f, t, u.Ordinal)] = u;
            else byName[u.BranchName] = u;
        }

        BranchKey?[] keys = KeysOf(model.Branches);
        var branches = new List<Branch>(model.Branches.Count);
        for (int i = 0; i < model.Branches.Count; i++)
        {
            Branch b = model.Branches[i];
            if (keys[i] is { } key
                && (byKey.TryGetValue(key, out BranchPlantState s) || byName.TryGetValue(key.Name, out s)))
            {
                double newTap = s.Update.TapRatio ?? b.TapRatio;
                double baseRate = double.IsNaN(s.BaseRateAMva) ? b.RateAMva : s.BaseRateAMva;
                double newRate = baseRate * (1.0 - Math.Clamp(s.Update.RatingDerateFraction, 0.0, 1.0));
                branches.Add(b with { TapRatio = newTap, RateAMva = newRate });
            }
            else branches.Add(b);
        }

        // CopyWith carries every other model property (protections, asset dynamics, OLTCs, boundaries) - R270.
        return model.CopyWith(branches: branches);
    }

    // The state the fleet reports for one bound asset after it advanced.
    private static BranchPlantState StateOf(BoundPlant p, PlantStateUpdate u)
        => new(p.Key.Name, u, p.BaseRateAMva, p.Key.From, p.Key.To, p.Key.Ordinal);

    /// <summary>
    /// Advance every passive asset one steady-state tick. <paramref name="loadingOf"/> and
    /// <paramref name="voltageOf"/> supply each branch's current loading fraction and controlled-bus
    /// voltage (p.u.), looked up by branch name; return the per-branch tap/rating/thermal updates to apply to the
    /// model. The live and replay loops use the solved-state overload instead, which is exact for shared names.
    /// </summary>
    public IReadOnlyList<BranchPlantState> Advance(
        double dtSeconds, double clockS, Func<string, double> loadingOf, Func<string, double> voltageOf)
    {
        var updates = new List<BranchPlantState>(_plant.Length);
        foreach (BoundPlant p in _plant)
        {
            var ctx = new AssetStepContext(
                LocalHz: _nominalHz, LocalRocofHzPerS: 0.0, LocalPhaseDeg: 0.0,
                DtSeconds: dtSeconds, ClockSeconds: clockS,
                LocalVoltagePu: voltageOf(p.Key.Name), LoadingFraction: loadingOf(p.Key.Name));
            updates.Add(StateOf(p, p.Plant.Advance(in ctx)));
        }
        return updates;
    }

    /// <summary>
    /// Advance every passive asset one tick of <paramref name="dtSeconds"/> against a solved network state:
    /// each branch's loading is its larger end apparent power over its ORIGINAL rating (heating is set by the
    /// current, not by the derated rating, so the derate must not feed back into its own driver), and each
    /// transformer sees the solved voltage of its controlled bus. A bus missing from the result gives NaN, which
    /// the OLTC treats as "no measurement" and holds; a branch missing from the solved model carries no flow.
    /// <paramref name="solved"/> must be the model the result was solved on (its branches index-align with
    /// <see cref="PowerFlowResult.LineFlows"/>). Throws when the result did not converge: a non-converged iterate is
    /// never an observation (R039).
    /// </summary>
    public IReadOnlyList<BranchPlantState> Advance(double dtSeconds, double clockS, PowerFlowResult result, GridModel solved)
    {
        if (!result.Converged)
            throw new InvalidOperationException(
                "PassivePlantFleet.Advance: the power flow did not converge, so its voltages and flows are not a network " +
                "state. Do not advance passive plant on a non-converged solve; hold the previous tick's updates instead.");
        if (result.LineFlows.Count != solved.Branches.Count)
            throw new InvalidOperationException(
                $"PassivePlantFleet.Advance: the result has {result.LineFlows.Count} line flows but the solved model has " +
                $"{solved.Branches.Count} branches. Pass the model the result was solved on (they must index-align).");

        // Apparent power at the more heavily loaded end of every keyed branch, MVA.
        BranchKey?[] keys = KeysOf(solved.Branches);
        var mvaOf = new Dictionary<BranchKey, double>();
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] is not { } key) continue;
            LineFlow f = result.LineFlows[i];
            double sFrom = Math.Sqrt(f.PFromMw * f.PFromMw + f.QFromMvar * f.QFromMvar);
            double sTo = Math.Sqrt(f.PToMw * f.PToMw + f.QToMvar * f.QToMvar);
            mvaOf[key] = Math.Max(sFrom, sTo);
        }

        var vmOf = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count && i < result.Vm.Length; i++) vmOf[result.BusIds[i]] = result.Vm[i];

        // N110 (the N071 rule): the bus type each bus ENDED the solve with. Both Newton engines report it; a solver that
        // does not falls back to the solved model's stored types (the pre-N110 rule). A bus id appearing twice takes its
        // first occurrence, as the voltage lookup above and the solver's own hold do.
        var typeOf = new Dictionary<int, BusType>(solved.Buses.Count);
        IReadOnlyList<BusType>? solvedTypes = result.SolvedBusTypes;
        if (solvedTypes is not null && solvedTypes.Count == result.BusIds.Count)
            for (int i = 0; i < solvedTypes.Count; i++) typeOf.TryAdd(result.BusIds[i], solvedTypes[i]);
        else
            foreach (Bus bus in solved.Buses) typeOf.TryAdd(bus.Id, bus.Type);

        int held = 0;
        var updates = new List<BranchPlantState>(_plant.Length);
        foreach (BoundPlant p in _plant)
        {
            // An unrated branch (rating 0) has no meaningful loading fraction, so it is treated as unloaded
            // rather than infinitely loaded.
            double mva = mvaOf.GetValueOrDefault(p.Key, 0.0);
            double loading = p.BaseRateAMva > 0 ? mva / p.BaseRateAMva : 0.0;
            // A transformer whose controlled bus solved as PV or slack holds: its voltage is the AVR's, not the tap's,
            // so the OLTC is shown "no measurement" (NaN) and neither steps nor runs its delay timer.
            bool generatorHeld = p.ControlledBus >= 0
                && typeOf.TryGetValue(p.ControlledBus, out BusType t) && t is BusType.Pv or BusType.Slack;
            if (generatorHeld) held++;
            double v = p.ControlledBus >= 0 && !generatorHeld && vmOf.TryGetValue(p.ControlledBus, out double vm) ? vm : double.NaN;
            var ctx = new AssetStepContext(
                LocalHz: _nominalHz, LocalRocofHzPerS: 0.0, LocalPhaseDeg: 0.0,
                DtSeconds: dtSeconds, ClockSeconds: clockS,
                LocalVoltagePu: v, LoadingFraction: loading);
            updates.Add(StateOf(p, p.Plant.Advance(in ctx)));
        }
        TapsHeldLastAdvance = held;
        return updates;
    }
}
