using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Scripting;

/// <summary>The whole-horizon engines. Each is a pure function of its settings and its events; a step re-runs it from t = 0.</summary>
public enum ReplayKind { Rms, Frequency, Emt, Protection }

/// <summary>What an event does to an engine: a step in infeed (MW; negative = a loss), or a fault applied to or cleared from a bus.</summary>
public abstract record ReplayEffect;

public sealed record InfeedEffect(double DeltaMw, int Bus) : ReplayEffect;

/// <summary>A fault applied (On) or cleared at a bus. WP2: <see cref="Type"/> set makes an applied fault a sequence fault of
/// that type through <see cref="FaultRPu"/> + j<see cref="FaultXPu"/> (pu, system base), which only the RMS SUBTRANSIENT and
/// GOVERNED engines take; null is the bolted three-phase fault.</summary>
public sealed record FaultEffect(int BusId, bool On, GridSim.Core.Protection.FaultType? Type = null, double FaultRPu = 0.0, double FaultXPu = 0.0) : ReplayEffect;

/// <summary>One event of a whole-horizon engine: its script time, its order among events of the same time, the statement text.</summary>
public sealed record ReplayEvent(long TimeUs, int Order, string Text, int ScheduledAtLine, ReplayEffect Effect);

/// <summary>The settings of SELECT ENGINE RMS | FREQUENCY | EMT | PROTECTION. A zero means "not given".</summary>
public sealed record ReplaySettings(
    ReplayKind Kind, long StepUs,
    decimal InertiaS = 0m, decimal DampingPu = 0m, decimal SlipDeg = 360m,
    decimal DroopPu = 0.05m, decimal ResponsivePu = 0.2m, decimal GovernorS = 8m,
    string? SourceBus = null, string Model = "", string? DynamicsPath = null, string? SchemasDir = null, string? ProbeBus = null)
{
    public static long DefaultStepUs(ReplayKind k) => k switch
    {
        ReplayKind.Rms => 5_000,
        ReplayKind.Emt => 100,
        _ => 20_000,
    };

    /// <summary>Every effective setting, in a fixed order, for the run record.</summary>
    public string Describe()
    {
        string step = string.Create(CultureInfo.InvariantCulture, $"step {StepUs} us");
        string C(decimal d) => Quantity.Canonical(d);
        return Kind switch
        {
            ReplayKind.Rms when Model != "CLASSICAL" => $"model {Model.ToLowerInvariant()}; {step}; dynamic data \"{DynamicsPath}\"; slip limit {C(SlipDeg)} deg; six-state subtransient machines on the sparse network" + (Model == "GOVERNED" ? ", with the governors and exciters of the file" : ", controllers of the file not used"),
            ReplayKind.Emt when Model == "THREE_PHASE" => $"model three-phase; {step}; source bus {(SourceBus is null ? "first generator" : "\"" + SourceBus + "\"")}; probe bus {(ProbeBus is null ? "first bus" : "\"" + ProbeBus + "\"")}; abc nodes, positive-sequence branches (zero-sequence mutual where the case gives R0 and X0), loads as constant impedance, a three-phase fault at every bus",
            ReplayKind.Rms => $"model classical; {step}; inertia {(InertiaS == 0m ? "from the case" : C(InertiaS) + " s")}; damping {C(DampingPu)} pu; slip limit {C(SlipDeg)} deg; model classical multi-machine (constant E', one fault window)",
            ReplayKind.Emt => $"model single-phase; {step}; source bus {(SourceBus is null ? "first generator" : "\"" + SourceBus + "\"")}; single-phase, trapezoidal with CDA, loads as constant impedance, a fault switch at every bus",
            ReplayKind.Frequency => $"model lumped; {step}; droop {C(DroopPu)} pu; responsive fraction {C(ResponsivePu)} pu; governor time constant {C(GovernorS)} s",
            _ => $"model protection-frequency; {step}; droop {C(DroopPu)} pu; responsive fraction {C(ResponsivePu)} pu; governor time constant {C(GovernorS)} s",
        };
    }
}

/// <summary>The outcome of a step of a whole-horizon engine.</summary>
public sealed record ReplayStep(long FromUs, long ToUs, int Ticks, ImmutableArray<KeyValuePair<string, string>> Final, string SeriesSha256, string Line);

/// <summary>What the engine stands at after its last run (for REPORT SIM).</summary>
public sealed record ReplaySummary(ImmutableArray<string> Lines, double? FrequencyHz = null);

/// <summary>
/// A whole-horizon engine stepped by deterministic re-run. It is immutable: adding an event or advancing gives a new engine,
/// so the session's rollback keeps the old one. Advancing to a new time re-runs the engine from t = 0 with every event so far,
/// checks that the part already shown is reproduced to the bit (a digest of the earlier part of the new run must equal the
/// digest taken when it was first shown), and reports only the new interval. Events land on step boundaries: an event at
/// script time T acts from the step that begins at T, so nothing at or before T changes when it is added.
/// </summary>
public sealed partial class ReplayEngine
{
    private readonly GridModel _model;
    private readonly PowerFlowResult? _solved;
    private readonly CaseInfo _case;
    private readonly string? _prefixDigest;

    private readonly Dyn? _dyn;

    /// <summary>The machine dynamic data an RMS SUBTRANSIENT or GOVERNED engine runs on.</summary>
    private sealed record Dyn(MachineDynamicsSet Set, string FullPath, string Sha256, IReadOnlyList<SubtransientInit.MachineData> Data, int[] MachineGenerators);

    /// <summary>The dynamic data file the engine was built on (full path), or null.</summary>
    public string? DynamicsFile => _dyn?.FullPath;
    public string? DynamicsSha256 => _dyn?.Sha256;

    private ReplayEngine(ReplaySettings settings, GridModel model, PowerFlowResult? solved, CaseInfo ci,
        ImmutableList<ReplayEvent> events, long reachedUs, string? prefixDigest, ReplaySummary? last, Dyn? dyn = null)
    {
        _dyn = dyn;
        Settings = settings;
        _model = model;
        _solved = solved;
        _case = ci;
        Events = events;
        ReachedUs = reachedUs;
        _prefixDigest = prefixDigest;
        Last = last;
    }

    public ReplaySettings Settings { get; }
    public ReplayKind Kind => Settings.Kind;
    public long StepUs => Settings.StepUs;
    public ImmutableList<ReplayEvent> Events { get; }
    public long ReachedUs { get; }
    public ReplaySummary? Last { get; }
    public string KindName => Kind.ToString().ToUpperInvariant();

    /// <summary>Build the engine for the plant as it stands. RMS needs a fresh solved power flow; PROTECTION needs a protection bundle.</summary>
    public static ReplayEngine Create(ReplaySettings settings, Plant plant, CaseInfo ci, PowerFlowResult? solved, string baseDirectory)
    {
        GridModel m = plant.Model;
        switch (settings.Kind)
        {
            case ReplayKind.Rms:
                if (solved is null) throw new ScriptException(ErrorPhase.Execution, default, "RMS starts from a solved power flow; run SOLVE POWERFLOW first");
                break;
            case ReplayKind.Protection:
                if (m.Protections is null)
                    throw new ScriptException(ErrorPhase.Execution, default, $"case \"{m.Name}\" carries no protection bundle (protections.json), so there is nothing for the PROTECTION FREQUENCY engine to evaluate");
                int network = AssetDynamicsFleet.Build(m, m.FrequencyHz).NetworkStageAssetCount;
                if (network > 0)
                    throw new ScriptException(ErrorPhase.Execution, default,
                        $"{network} device(s) of case \"{m.Name}\" carry voltage or loading stages, which PROTECTION FREQUENCY would have to skip; it never skips a stage. Use SELECT ENGINE LIVE, which evaluates them against the network");
                break;
            case ReplayKind.Emt:
                if (settings.SourceBus is { } sb && !ci.BusIndex.ContainsKey(sb))
                    throw new ScriptException(ErrorPhase.Execution, default, $"the EMT source bus \"{sb}\" is not a bus of the case");
                break;
        }
        Dyn? dyn = settings.Kind == ReplayKind.Rms && settings.Model != "CLASSICAL" ? LoadDynamics(settings, m, ci, baseDirectory) : null;
        return new ReplayEngine(settings, m, solved, ci, [], 0, null, null, dyn);
    }

    /// <summary>The engine with one more event. Refuses an event the engine cannot represent.</summary>
    public ReplayEngine WithEvent(ReplayEvent e)
    {
        if (e.Effect is FaultEffect f)
        {
            bool on = false;
            foreach (ReplayEvent x in Events)
                if (x.Effect is FaultEffect xf && xf.BusId == f.BusId)
                    on = xf.On;
            if (f.On && on) throw new ScriptException(ErrorPhase.Execution, default, "this bus is already faulted");
            if (f.On && f.Type is not null && !(Kind == ReplayKind.Rms && Settings.Model != "CLASSICAL"))
                throw new ScriptException(ErrorPhase.Execution, default, "a fault with a type or an impedance (THREE_PHASE, SLG, LL, LLG, WITH R .. X ..) needs RMS SUBTRANSIENT or RMS GOVERNED; this engine takes the bolted FAULT BUS only");
            if (!f.On && !on) throw new ScriptException(ErrorPhase.Execution, default, "no fault is in place at this bus");
            if (Kind == ReplayKind.Rms && Settings.Model != "CLASSICAL" && f.On && e.TimeUs == 0)
                throw new ScriptException(ErrorPhase.Execution, default, $"RMS {Settings.Model} starts from the solved undisturbed state: a fault cannot be applied at 0 ms");
            if (Kind == ReplayKind.Emt && Settings.Model == "THREE_PHASE" && f.On && Events.Any(x => x.Effect is FaultEffect xf && xf.BusId == f.BusId))
                throw new ScriptException(ErrorPhase.Execution, default, "EMT THREE_PHASE carries one fault window per bus per run");
            if (Kind == ReplayKind.Rms && Settings.Model == "CLASSICAL")
            {
                if (f.On && Events.Any(x => x.Effect is FaultEffect))
                    throw new ScriptException(ErrorPhase.Execution, default, "the classical RMS model carries one fault window (one FAULT and its CLEAR FAULT) per run");
            }
        }
        return new ReplayEngine(Settings, _model, _solved, _case, Events.Add(e), ReachedUs, _prefixDigest, Last, _dyn);
    }

    /// <summary>Re-run from t = 0 to <c>ReachedUs + duration</c> with every event so far; returns the engine at the new time and the new interval.</summary>
    public (ReplayEngine Next, ReplayStep Step) Advance(long durationUs)
    {
        long to = ReachedUs + durationUs;
        int fromStep = checked((int)(ReachedUs / StepUs));
        int toStep = checked((int)(to / StepUs));
        Run run = Kind switch
        {
            ReplayKind.Rms => Settings.Model == "CLASSICAL" ? RunRms(to) : RunRmsDynamic(to),
            ReplayKind.Frequency => RunFrequency(to, assets: false),
            ReplayKind.Protection => RunFrequency(to, assets: true),
            _ => Settings.Model == "THREE_PHASE" ? RunEmt3(to) : RunEmt(to),
        };
        if (run.Vectors.Length != toStep + 1)
            throw new ScriptException(ErrorPhase.Execution, default, $"the {KindName} engine returned {run.Vectors.Length} points for {toStep + 1} expected steps");
        if (_prefixDigest is not null && Digest(run.Vectors, 0, fromStep) != _prefixDigest)
            throw new ScriptException(ErrorPhase.Execution, default,
                $"the {KindName} engine's re-run did not reproduce the part of the run already shown (up to {Clock(ReachedUs)}); no new result is reported");
        string seriesSha = Digest(run.Vectors, fromStep + 1, toStep);
        string prefix = Digest(run.Vectors, 0, toStep);
        var step = new ReplayStep(ReachedUs, to, toStep - fromStep, run.Final, seriesSha, run.Line);
        var next = new ReplayEngine(Settings, _model, _solved, _case, Events, to, prefix, new ReplaySummary(run.Report, run.Hz), _dyn);
        return (next, step);
    }

    private sealed record Run(double[][] Vectors, ImmutableArray<KeyValuePair<string, string>> Final, string Line, ImmutableArray<string> Report, double? Hz = null);

    private static string Clock(long us) => (us / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static string F6(double d) => d.ToString("F6", CultureInfo.InvariantCulture);

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Digest(double[][] vectors, int from, int to)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int k = from; k <= to; k++)
            foreach (double d in vectors[k])
                h.AppendData(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(d)));
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static double Seconds(long us) => us / 1_000_000.0;

    // ---- FREQUENCY and PROTECTION -------------------------------------------------------------------------------------

    private Run RunFrequency(long to, bool assets)
    {
        double dt = Seconds(StepUs);
        double demand = Math.Max(1.0, _model.Buses.Sum(b => b.Pd));
        double inertia = _model.SystemInertiaGvaS * 1000.0;
        if (inertia < LiveSolveSettings.MinInertiaMwS)
            inertia = LiveSolveSettings.FallbackInertiaH * _model.Generators.Sum(g => g.MBase > 0 ? g.MBase : Math.Abs(g.Pg));
        if (inertia < LiveSolveSettings.MinInertiaMwS) inertia = LiveSolveSettings.FallbackInertiaH * demand;

        var events = Events.OrderBy(e => e.TimeUs).ThenBy(e => e.Order)
            .Select(e => (ReplayEvent: e, Effect: (InfeedEffect)e.Effect))
            .Select(x => new FrequencyEvent(Seconds(x.ReplayEvent.TimeUs), x.Effect.DeltaMw, x.Effect.Bus))
            .ToList();
        var cfg = new DynamicsConfig
        {
            NominalHz = _model.FrequencyHz,
            DroopPu = (double)Settings.DroopPu,
            ResponsiveFraction = (double)Settings.ResponsivePu,
            GovernorTimeConstantS = (double)Settings.GovernorS,
            StepSeconds = dt,
            DurationSeconds = Seconds(to),
            EnableAssetDynamics = assets,
        };
        int steps = checked((int)(to / StepUs));
        FrequencyResponse resp;
        AssetFrequencyResult? ar = null;
        if (assets)
        {
            double sys = _model.SystemInertiaGvaS * 1000.0;
            cfg = cfg with { DeviceRotationalInertiaMwS = Math.Max(0.0, inertia - sys) };
            ar = AssetFrequencyDynamics.Simulate(_model, demand, events, cfg);
            resp = ar.Response;
        }
        else resp = FrequencyDynamics.Simulate(inertia, demand, events, cfg);

        var vectors = new double[steps + 1][];
        for (int k = 0; k <= steps; k++) vectors[k] = [resp.Points[k].Hz];
        double hz = resp.Points[steps].Hz;
        double rocof = steps > 0 ? resp.Points[steps - 1].RocofHzPerS : 0.0;
        double nadir = double.MaxValue, nadirT = 0, peak = double.MinValue;
        for (int k = 0; k <= steps; k++)
        {
            if (resp.Points[k].Hz < nadir) { nadir = resp.Points[k].Hz; nadirT = k * dt; }
            if (resp.Points[k].Hz > peak) peak = resp.Points[k].Hz;
        }
        var final = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        final.Add(new("frequencyHz", R(hz)));
        final.Add(new("rocofHzPerS", R(rocof)));
        final.Add(new("nadirHz", R(nadir)));
        final.Add(new("nadirAtS", R(nadirT)));
        string line = $"frequency {F6(hz)} Hz, rocof {F6(rocof)} Hz/s, nadir {F6(nadir)} Hz at {F6(nadirT)} s";
        var report = ImmutableArray.CreateBuilder<string>();
        report.Add($"t {Clock(to)}, frequency {F6(hz)} Hz, rocof {F6(rocof)} Hz/s (rate over the last step)");
        report.Add($"since t = 0: nadir {F6(nadir)} Hz at {F6(nadirT)} s, peak {F6(peak)} Hz; inertia {inertia.ToString("F1", CultureInfo.InvariantCulture)} MW.s, demand {demand.ToString("F1", CultureInfo.InvariantCulture)} MW");
        if (ar is not null)
        {
            final.Add(new("trippedUnits", ar.TrippedCount.ToString(CultureInfo.InvariantCulture)));
            final.Add(new("trippedMw", R(ar.TotalTrippedMw)));
            final.Add(new("protectionOperations", ar.ProtectionTrippedCount.ToString(CultureInfo.InvariantCulture)));
            line += $"; {ar.TrippedCount} unit(s) tripped, {ar.TotalTrippedMw.ToString("F2", CultureInfo.InvariantCulture)} MW lost";
            report.Add($"protection: {ar.ProtectionTrippedCount} device(s) operated ({ar.ProtectionTrippedMw.ToString("F2", CultureInfo.InvariantCulture)} MW), inverters {ar.InverterTrippedCount} tripped ({ar.InverterTrippedMw.ToString("F2", CultureInfo.InvariantCulture)} MW); total {ar.TrippedCount} unit(s), {ar.TotalTrippedMw.ToString("F2", CultureInfo.InvariantCulture)} MW lost");
        }
        report.Add($"events so far: {Events.Count}");
        return new Run(vectors, final.ToImmutable(), line, report.ToImmutable(), hz);
    }

    // ---- RMS (classical multi-machine) ---------------------------------------------------------------------------------

    private Run RunRms(long to)
    {
        double dt = Seconds(StepUs);
        GridModel model = _model;
        if (Settings.InertiaS > 0m)
        {
            var fleet = model.AllGenerators.ToArray();
            for (int i = 0; i < fleet.Length; i++)
                if (fleet[i].InService && !fleet[i].PowerFlowOnly && !fleet[i].IsConverterInterfaced && !(fleet[i].H > 0.0))
                    fleet[i] = fleet[i] with { H = (double)Settings.InertiaS };
            model = model.CopyWith(generators: fleet, generatorsAreWholeFleet: true);
        }
        List<ReplayEvent> faults = Events.Where(e => e.Effect is FaultEffect).OrderBy(e => e.TimeUs).ThenBy(e => e.Order).ToList();
        int? faultBus = faults.Count > 0 ? ((FaultEffect)faults[0].Effect).BusId : null;
        ClassicalInit.Prepared p;
        try
        {
            p = ClassicalInit.FromSolvedCase(model, _solved!, null, faultBus, holdNonMachines: true);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptException(ErrorPhase.Execution, default,
                ex.Message + " (give the machines an inertia constant with SELECT ENGINE RMS WITH INERTIA 5 S, or use a case that carries one)");
        }
        ClassicalMultiMachine.Fault? fault = null;
        if (faults.Count > 0)
        {
            // An event acts from the step that begins at its time: the edge sits a few millionths of a step after the
            // time, so the sample at the time itself (the end of the step before) still sees the old network.
            double edge = dt * 4e-6;
            double on = Seconds(faults[0].TimeUs) + edge;
            double clear = faults.Count > 1 ? Seconds(faults[1].TimeUs) + edge : double.PositiveInfinity;
            fault = new ClassicalMultiMachine.Fault(on, clear, p.YReducedFaulted!);
        }
        int n = p.H.Length;
        var d = new double[n];
        for (int i = 0; i < n; i++) d[i] = (double)Settings.DampingPu;
        ClassicalMultiMachine.Result res = ClassicalMultiMachine.Simulate(
            p.H, d, p.Pm, p.Eq, p.Delta0, p.YReduced, model.FrequencyHz, dt, Seconds(to), fault, (double)Settings.SlipDeg * Math.PI / 180.0);

        int steps = checked((int)(to / StepUs));
        var vectors = new double[steps + 1][];
        for (int k = 0; k <= steps; k++)
        {
            var v = new double[2 * n];
            for (int i = 0; i < n; i++)
            {
                v[i] = res.Machines[i].DeltaRad[k];
                v[n + i] = res.Machines[i].SpeedPu[k];
            }
            vectors[k] = v;
        }
        string[] names = InServiceGeneratorNames();
        double hSum = 0, hDelta = 0;
        for (int i = 0; i < n; i++) { hSum += p.H[i]; hDelta += p.H[i] * vectors[steps][i]; }
        double coi = hDelta / hSum;
        var final = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var report = ImmutableArray.CreateBuilder<string>();
        report.Add($"t {Clock(to)}, {n} machine(s), rotor angle against the inertia-weighted centre (deg) and speed deviation (pu)");
        double maxSpread = 0;
        for (int i = 0; i < n; i++)
        {
            string name = names[p.MachineIndex[i]];
            double ang = (vectors[steps][i] - coi) * 180.0 / Math.PI;
            double spd = vectors[steps][n + i];
            maxSpread = Math.Max(maxSpread, Math.Abs(ang));
            final.Add(new($"angleDeg:{name}", R(ang)));
            final.Add(new($"speedPu:{name}", R(spd)));
            report.Add($"  \"{name}\" angle {ang.ToString("F4", CultureInfo.InvariantCulture)} deg, speed {spd.ToString("E4", CultureInfo.InvariantCulture)} pu");
        }
        string sync = res.StayedInSynchronism ? "in synchronism" : "LOSS OF SYNCHRONISM";
        final.Add(new("synchronism", res.StayedInSynchronism ? "ok" : "lost"));
        report.Add($"since t = 0: {sync}; fault {(faults.Count == 0 ? "none" : $"at bus {((FaultEffect)faults[0].Effect).BusId} from {Clock(faults[0].TimeUs)}" + (faults.Count > 1 ? $" to {Clock(faults[1].TimeUs)}" : ", not yet cleared"))}; events so far: {Events.Count}");
        string line = $"largest angle from centre {maxSpread.ToString("F4", CultureInfo.InvariantCulture)} deg, {sync}";
        return new Run(vectors, final.ToImmutable(), line, report.ToImmutable());
    }

    private string[] InServiceGeneratorNames()
    {
        var list = new List<string>();
        for (int i = 0; i < _model.AllGenerators.Count; i++)
            if (_model.AllGenerators[i].InService) list.Add(_case.GeneratorNames[i]);
        return list.ToArray();
    }

    // ---- EMT ---------------------------------------------------------------------------------------------------------

    private const int FaultSwitchBase = 9_000_000;

    private Run RunEmt(long to)
    {
        double dt = Seconds(StepUs);
        GridModel m = _model;
        int srcBus = Settings.SourceBus is { } sb ? m.Buses[_case.BusIndex[sb]].Id
            : m.Generators.Count > 0 ? m.Generators[0].Bus : m.Buses[0].Id;
        EmtNetwork net0 = EmtNetwork.FromGridModel(m,
            new EmtBuildOptions([new EmtSourceSpec(srcBus, Rs: 0, AmplitudePu: 1.0, FrequencyHz: m.FrequencyHz)], IncludeLoads: true));
        // A fault switch at every bus, open, so the network is the same whatever faults the script applies later.
        var elements = new List<EmtElement>(net0.Elements);
        for (int i = 0; i < m.Buses.Count; i++)
            elements.Add(new SwitchElement(FaultSwitchBase + i, net0.BusToNode[m.Buses[i].Id], EmtNetwork.Ground, InitiallyClosed: false));
        var net = new EmtNetwork(net0.NodeCount, elements, net0.BusToNode, net0.SeriesBranchIds);
        var busIndexById = new Dictionary<int, int>();
        for (int i = 0; i < m.Buses.Count; i++) busIndexById[m.Buses[i].Id] = i;

        // An EMT event is applied while computing the step that ends at its time, so the event for the step that begins at T is at T + dt.
        var events = new List<EmtEvent>();
        foreach (ReplayEvent e in Events.OrderBy(x => x.TimeUs).ThenBy(x => x.Order))
        {
            var f = (FaultEffect)e.Effect;
            events.Add(new SwitchEvent(Seconds(e.TimeUs) + dt, FaultSwitchBase + busIndexById[f.BusId], Close: f.On));
        }
        var probes = m.Buses.Select(b => new EmtProbe("v:" + b.Id.ToString(CultureInfo.InvariantCulture), EmtProbeKind.NodeVoltage, net.BusToNode[b.Id])).ToList();
        var engine = new EmtEngine(net, dt);
        EmtResult res = engine.Simulate(new EmtCase(net, dt, Seconds(to), probes, events));

        int steps = checked((int)(to / StepUs));
        var vectors = new double[steps + 1][];
        for (int k = 0; k <= steps; k++) vectors[k] = res.Points[k].Probe;
        var final = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        var report = ImmutableArray.CreateBuilder<string>();
        report.Add($"t {Clock(to)}, single-phase EMT at {Clock(StepUs)} steps, source at bus {srcBus}; instantaneous bus voltage (pu) and the largest magnitude since t = 0");
        double worst = 0;
        for (int b = 0; b < m.Buses.Count; b++)
        {
            double peak = 0;
            for (int k = 0; k <= steps; k++) peak = Math.Max(peak, Math.Abs(vectors[k][b]));
            worst = Math.Max(worst, peak);
            string name = _case.BusNames[b];
            final.Add(new($"v:{name}", R(vectors[steps][b])));
            report.Add($"  bus \"{name}\" v {vectors[steps][b].ToString("F6", CultureInfo.InvariantCulture)} pu, peak {peak.ToString("F6", CultureInfo.InvariantCulture)} pu");
        }
        report.Add($"events so far: {Events.Count}; refactorisations {res.Stats.Refactorizations}");
        return new Run(vectors, final.ToImmutable(), $"largest bus voltage magnitude since t = 0: {worst.ToString("F6", CultureInfo.InvariantCulture)} pu", report.ToImmutable());
    }
}
