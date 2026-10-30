using GridSim.Core.Model;

namespace GridSim.Core.Assets;

/// <summary>
/// The authored-asset bundle attached to a <see cref="GridModel"/>: the class library plus the bulk
/// binding rules. Present only when a case ships <c>asset-classes.json</c>; null otherwise (so every
/// existing case is unchanged and the reduction gate holds by construction).
/// </summary>
public sealed record AssetDynamicsModel(AssetClassLibrary Library, AssetBindingSet Bindings);

/// <summary>The aggregate result of stepping the active-asset fleet once.</summary>
/// <remarks>R074 (October 2026 review, H hygiene stream): the per-domain power split (PowerByDomain) is gone with the dead
/// per-domain sampling path; it was null on every reachable step.</remarks>
/// <param name="ProtectionInjectionMw">R044 (October 2026, stream B2): the part of <paramref name="NetActivePowerMw"/> that
/// placed protection devices hold (an LFDD block shed, an injection step), summed separately, left to right from +0.0. It
/// is a standing change the live loop's secondary restoration redispatches against, like a trip; the net sum itself is
/// added exactly as before.</param>
public readonly record struct AssetFleetStep(
    double NetActivePowerMw,
    double TrippedDeficitMw,
    double TrippedInertiaMwS,
    double ProtectionInjectionMw = 0.0);

/// <summary>Per-class rollup for the dashboard (raw numbers; formatting lives in the UI).</summary>
public readonly record struct AssetClassRollup(
    string ClassName, string Mode, int Count, double OnlineMw, int TrippedCount, double SocPercent);

/// <summary>
/// The bound active-asset fleet - the new <c>pAssetFleet</c> term. Advances every bound inverter
/// against its locally-sampled frequency and sums the swing power, tracking cumulative Loss-of-Mains
/// trips (lost dispatched <c>Pg</c> becomes deficit, mirroring <see cref="Dynamics.RocofScreening"/>).
/// Built from a model's generators + resolved bindings; empty when nothing is bound, in which case it
/// contributes exactly zero (the reduction gate).
/// </summary>
public sealed class AssetDynamicsFleet
{
    private readonly IActiveAssetDynamics[] _active;
    private readonly double[] _pgMw;             // dispatched output per active asset (for trip accounting); R043: follows SetDispatch
    // R043: the index in GridModel.Generators of the generator each active asset is bound to (-1 for a placed protection
    // device), and the generator count the fleet was built against, so a dispatch update is matched unit for unit.
    private readonly int[] _generatorIndex;
    private readonly int _generatorCount;
    // N297 (B4, October 2026): the total demand (MW, sum of bus Pd) of the model the fleet was built from: the demand the
    // placed LFDD blocks were set against. SetDispatch scales them by the operating point's demand over this.
    private readonly double _baseDemandMw;
    private double _trippedDeficitMw;
    private double _trippedInertiaMwS;
    // D-DOC-161: the same cumulative infeed loss, attributed by who tripped. Accumulated beside _trippedDeficitMw and never
    // read by the swing, so no trajectory number depends on them.
    private double _inverterTrippedMw;
    private double _protectionTrippedMw;

    private AssetDynamicsFleet(IActiveAssetDynamics[] active, double[] pgMw, int[] generatorIndex, int generatorCount,
        double baseDemandMw = 0.0)
    {
        _active = active;
        _pgMw = pgMw;
        _generatorIndex = generatorIndex;
        _generatorCount = generatorCount;
        _baseDemandMw = baseDemandMw;
        int network = 0;
        foreach (IActiveAssetDynamics a in active) if (a.UsesNetworkState) network++;
        NetworkStageAssetCount = network;
    }

    /// <summary>
    /// D-DOC-038: how many assets carry a voltage or loading trip stage. On a path that feeds no network state those
    /// stages are not evaluated (the context carries <see cref="AssetStepContext.NoNetworkVoltagePu"/>), and the caller
    /// reports this count so the reader knows they did not run.
    /// </summary>
    public int NetworkStageAssetCount { get; }

    /// <summary>D-DOC-161: cumulative infeed (MW) lost to tripped inverters bound to generators (Loss-of-Mains RoCoF / vector
    /// shift, or under-voltage ride-through) since the last <see cref="Reset"/>.</summary>
    public double InverterTrippedMw => _inverterTrippedMw;

    /// <summary>D-DOC-161: cumulative infeed (MW) lost to placed protection devices operating (any trip stage) since the last
    /// <see cref="Reset"/>.</summary>
    public double ProtectionTrippedMw => _protectionTrippedMw;

    /// <summary>A fleet with nothing bound - contributes exactly zero (the reduction-gate default).</summary>
    public static AssetDynamicsFleet Empty { get; } = new(Array.Empty<IActiveAssetDynamics>(), Array.Empty<double>(), Array.Empty<int>(), 0);

    public bool IsEmpty => _active.Length == 0;
    public int Count => _active.Length;
    public IReadOnlyList<IActiveAssetDynamics> Assets => _active;

    /// <summary>
    /// Build the fleet from a model's generators + their resolved authored classes (active kinds), plus any
    /// placed protective devices (<see cref="GridModel.Protections"/>). Both are optional; absent bundles
    /// yield an empty fleet - the reduction gate.
    /// </summary>
    public static AssetDynamicsFleet Build(GridModel model, double f0)
    {
        var controllers = new List<IActiveAssetDynamics>();
        var pgs = new List<double>();
        var genIndex = new List<int>();

        if (model.AssetDynamics is { } spec)
        {
            for (int gi = 0; gi < model.Generators.Count; gi++)
            {
                Generator g = model.Generators[gi];
                string? className = spec.Bindings.Resolve(g.DynamicsClass, g.Type);
                if (className is null) continue;
                AssetClass cls = spec.Library.Get(className);   // throws on unknown - fail loud
                IActiveAssetDynamics? ctrl = AssetControllerFactory.CreateActive(cls, g.Bus, g.Pg, g.MBase, f0);
                if (ctrl is null) continue;   // passive kind bound to a generator: ignore
                controllers.Add(ctrl);
                pgs.Add(g.Pg);
                genIndex.Add(gi);
            }
        }

        if (model.Protections is { } prot)
        {
            foreach (Protection.ProtectionInstance pi in prot.Instances)
            {
                AssetClass cls = prot.Library.Get(pi.ClassName);   // throws on unknown - fail loud
                // N297 (B4, October 2026): the device's frequency settings move with the system's nominal f0 (the model's
                // nominal for every model-based caller); at 50 Hz they are used exactly as written.
                controllers.Add(new Protection.ProtectionController(cls, pi.MonitorBus, pi.CapacityMw, pi.TargetBranch, f0));
                pgs.Add(pi.CapacityMw);
                genIndex.Add(-1);   // not a generator: its capacity is its own setting, never redispatched
            }
        }

        return controllers.Count == 0
            ? new AssetDynamicsFleet(Array.Empty<IActiveAssetDynamics>(), Array.Empty<double>(), Array.Empty<int>(), model.Generators.Count)
            : new AssetDynamicsFleet(controllers.ToArray(), pgs.ToArray(), genIndex.ToArray(), model.Generators.Count,
                TotalDemandMw(model));
    }

    /// <summary>N297: the model's total demand (MW), summed over its buses in order (the sum LiveSim's demand uses).</summary>
    private static double TotalDemandMw(GridModel model)
    {
        double sum = 0.0;
        foreach (Bus b in model.Buses) sum += b.Pd;
        return sum;
    }

    /// <summary>The dispatched output (MW) each active asset would book as lost infeed on a trip now, in fleet order.</summary>
    public IReadOnlyList<double> DispatchMw => _pgMw;

    /// <summary>
    /// R043 (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): hand every generator-bound
    /// asset the output its generator is dispatched to in <paramref name="dispatched"/>, the operating point now running,
    /// so a trip books the infeed actually lost (see <see cref="IActiveAssetDynamics.SetDispatch"/>). The live loop calls
    /// this every tick with the load-scaled, re-balanced model (<c>GridScenario.AtLoad</c>). Placed protection devices are
    /// not generators and keep their capacity.
    /// <para>Fails loud (<see cref="ArgumentException"/>) when <paramref name="dispatched"/> is not an operating point of
    /// the model the fleet was built from: a different generator count, or a bound generator at another bus. Units are
    /// matched by position in <see cref="GridModel.Generators"/>, the order every scenario copy keeps.</para>
    /// <para>Bit-identity: before any trip nothing reads the dispatch (it is only the size of the loss a trip books), and
    /// a model whose dispatch equals the base (the hold-steady path, every batch study) sets every unit to the value it
    /// already had, so only a trip on the load-scaled path books a different number.</para>
    /// </summary>
    public void SetDispatch(GridModel dispatched)
    {
        ArgumentNullException.ThrowIfNull(dispatched);
        if (dispatched.Generators.Count != _generatorCount)
            throw new ArgumentException(
                $"AssetDynamicsFleet.SetDispatch: the operating point has {dispatched.Generators.Count} in-service generators but the "
                + $"fleet was built from a model with {_generatorCount}; it is not an operating point of that model.", nameof(dispatched));
        for (int i = 0; i < _active.Length; i++)
        {
            int gi = _generatorIndex[i];
            if (gi < 0) continue;   // a placed protection device
            Generator g = dispatched.Generators[gi];
            if (g.Bus != _active[i].Bus)
                throw new ArgumentException(
                    $"AssetDynamicsFleet.SetDispatch: generator {gi} of the operating point is at bus {g.Bus}, but the asset bound to it "
                    + $"('{_active[i].ClassName}') is at bus {_active[i].Bus}.", nameof(dispatched));
            _active[i].SetDispatch(g.Pg);
            _pgMw[i] = g.Pg;
        }

        // N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; source Grid Code OC6.6.2(a)-(b), LFDD
        // blocks sized in % of demand): the placed demand-shed devices disconnect their block's SHARE of the demand now
        // running, so they take the operating point's demand over the base demand. Bit-identity: an operating point with
        // the base demand (the hold-steady path; the same buses summed in the same order) gives x / x = 1.0 exactly, and a
        // batch study never calls this (scale 1.0). A base with no demand has nothing to scale against: the scale stays 1.
        if (_baseDemandMw > 0.0)
        {
            double scale = TotalDemandMw(dispatched) / _baseDemandMw;
            foreach (IActiveAssetDynamics a in _active)
                if (a is Protection.ProtectionController pc) pc.SetDemandScale(scale);
        }
    }

    /// <summary>Reset every controller and the cumulative trip state (for a fresh simulation run).</summary>
    public void Reset()
    {
        foreach (IActiveAssetDynamics a in _active) a.Reset();
        _trippedDeficitMw = 0; _trippedInertiaMwS = 0;
        _inverterTrippedMw = 0; _protectionTrippedMw = 0;
    }

    // R074 (October 2026 review, H hygiene stream): the distributed per-domain sampling path (Step(IGridFrequencySampler,
    // ...), AssignDomains, DomainOf, the PowerByDomain split, and the CoiFrequencySampler / DomainFrequencySampler types)
    // had no production caller: only StepCoi below is used. Wired as it stood it would also have handed every asset
    // "no network" voltage and loading, leaving every voltage, LVRT and overload stage inert. It is deleted until the
    // distributed inertia model drives a per-domain step with network state (the B1 / F streams). StepCoi is unchanged.

    /// <summary>
    /// Allocation-free COI fast path: advance every asset against a single lumped (Hz, RoCoF, phase) sample
    /// (the only fleet step: R074 removed the unused per-domain sampler path). Used by the hot lumped integrators where
    /// this runs thousands of times per contingency.
    /// </summary>
    public AssetFleetStep StepCoi(double hz, double rocof, double phase, double dtSeconds, double clockS,
        Func<int, double>? voltageOf = null, Func<int, double>? loadingOf = null)
    {
        double net = 0.0;
        double protection = 0.0;   // R044: the protection devices' standing injection, a separate sum (net is unchanged)
        bool network = voltageOf is not null || loadingOf is not null;
        // Shared context when no network state is fed. D-DOC-038: it used to carry voltage 1.0 / loading 0.0, as if
        // measured, so a stage set above 1.0 pu (under-voltage) or below it (over-voltage) operated at once on a voltage
        // nobody had computed. It now carries NaN ("not known"), on which no voltage or loading stage operates; frequency,
        // RoCoF and phase stages are untouched, and with every sensible setting nothing operated at 1.0 / 0 either, so
        // the lumped trajectories are bit-identical.
        var shared = new AssetStepContext(hz, rocof, phase, dtSeconds, clockS,
            AssetStepContext.NoNetworkVoltagePu, AssetStepContext.NoNetworkLoadingFraction);
        for (int i = 0; i < _active.Length; i++)
        {
            IActiveAssetDynamics a = _active[i];
            AssetStepContext ctx = network
                ? new AssetStepContext(hz, rocof, phase, dtSeconds, clockS,
                    voltageOf?.Invoke(a.Bus) ?? 1.0, loadingOf?.Invoke(a.Bus) ?? 0.0)
                : shared;
            AssetActuation act = a.Update(in ctx);
            if (act.TrippedNow)
            {
                _trippedDeficitMw += act.TrippedInfeedMw; _trippedInertiaMwS += act.TrippedInertiaMwS;
                Attribute(a, act.TrippedInfeedMw);
            }
            net += act.ActivePowerMw;
            if (a.Kind == AssetKind.Protection) protection += act.ActivePowerMw;
        }
        return new AssetFleetStep(net, _trippedDeficitMw, _trippedInertiaMwS, protection);
    }

    /// <summary>D-DOC-161: add a trip's lost infeed to the inverter or protection-device share.</summary>
    private void Attribute(IActiveAssetDynamics a, double trippedInfeedMw)
    {
        if (a.Kind == AssetKind.Protection) _protectionTrippedMw += trippedInfeedMw;
        else _inverterTrippedMw += trippedInfeedMw;
    }

    /// <summary>The set of branches currently held OPEN by recloser protections in the fleet (empty if none).</summary>
    public IReadOnlySet<string> OpenBranches()
    {
        var open = new HashSet<string>();
        foreach (IActiveAssetDynamics a in _active)
            if (a is Protection.ProtectionController { OpenBranches.Count: > 0 } pc)
                foreach (string b in pc.OpenBranches) open.Add(b);
        return open;
    }

    /// <summary>Snapshot per-class rollups for the dashboard.</summary>
    public IReadOnlyList<AssetClassRollup> Rollups()
    {
        var by = new Dictionary<string, (string mode, int count, double mw, int tripped, double socSum, int socN)>();
        foreach (IActiveAssetDynamics a in _active)
        {
            string mode = a.Kind == AssetKind.GridFormingInverter ? "grid-forming" : "grid-following";
            var cur = by.GetValueOrDefault(a.ClassName, (mode: mode, count: 0, mw: 0.0, tripped: 0, socSum: 0.0, socN: 0));
            double soc = a.SocFraction;
            bool hasSoc = !double.IsNaN(soc);
            by[a.ClassName] = (mode, cur.count + 1, cur.mw + a.CurrentMw, cur.tripped + (a.Tripped ? 1 : 0),
                cur.socSum + (hasSoc ? soc : 0.0), cur.socN + (hasSoc ? 1 : 0));
        }
        return by.Select(kv => new AssetClassRollup(
            kv.Key, kv.Value.mode, kv.Value.count, kv.Value.mw, kv.Value.tripped,
            kv.Value.socN > 0 ? 100.0 * kv.Value.socSum / kv.Value.socN : 0.0)).ToList();
    }
}
