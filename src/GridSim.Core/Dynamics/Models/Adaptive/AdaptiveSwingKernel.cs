using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Models.Adaptive;

/// <summary>
/// RC4 R4-14: the plant topology of an adaptive run, owned by the caller. The engine asks it for the network in force and
/// tells it when a scheduled event time is reached or a relay trips a branch.
/// </summary>
public interface IAdaptiveTopology
{
    /// <summary>The network at t = 0.</summary>
    IRmsNetwork Initial { get; }

    /// <summary>The distinct scheduled event times, ascending (exact, never quantised).</summary>
    IReadOnlyList<double> ScheduledTimes { get; }

    /// <summary>Apply every scheduled event at <c>ScheduledTimes[index]</c> in rank order (time, then input order) and return
    /// the network that then stands; <paramref name="description"/> lists them.</summary>
    IRmsNetwork ApplyScheduled(int index, out string description);

    /// <summary>A relay opens branch <paramref name="branchIndex"/>: return the network that then stands (with every scheduled
    /// event so far still in force). Opening a branch that is already open changes nothing.</summary>
    IRmsNetwork ApplyRelayTrip(int branchIndex);

    /// <summary>Machines currently disconnected (carry no current, Pm = 0); re-read after every apply.</summary>
    IReadOnlyList<bool> MachineOut { get; }
}

/// <summary>
/// RC5 stage 3: a topology that can disconnect a machine when its unit's own relay operates. Required only when unit
/// relays are in the loop (<see cref="AdaptiveSwingProblem.UnitRelays"/>); the existing topologies need not implement it.
/// </summary>
public interface IAdaptiveUnitTripTopology
{
    /// <summary>Disconnect machine <paramref name="slot"/> (its unit's own relay operated) and return the network that then
    /// stands, or null when that would leave no connected machine (the run then stops, saying so). A machine that is
    /// already out changes nothing.</summary>
    IRmsNetwork? ApplyUnitTrip(int slot);

    /// <summary>True once the scheduled events last applied (<see cref="IAdaptiveTopology.ApplyScheduled"/>) have, together
    /// with the unit relay trips so far, disconnected every connected machine: the run then stops at that instant, saying
    /// so, and the network that call returned is not used.</summary>
    bool NoConnectedMachine => false;
}

/// <summary>A topology given as a fixed list of pre-built networks (no relay tripping beyond the supplied function).</summary>
public sealed class StageListTopology : IAdaptiveTopology
{
    private readonly List<(double At, IRmsNetwork Network, string Description)> _stages;
    private readonly Func<int, IRmsNetwork>? _trip;
    private readonly bool[] _out;

    /// <summary>Create the topology; <paramref name="stages"/> must be in ascending time order.</summary>
    public StageListTopology(IRmsNetwork initial, IEnumerable<(double At, IRmsNetwork Network, string Description)> stages,
        int machines, Func<int, IRmsNetwork>? relayTrip = null)
    {
        Initial = initial ?? throw new ArgumentNullException(nameof(initial));
        _stages = stages.ToList();
        _trip = relayTrip;
        _out = new bool[machines];
        for (int i = 1; i < _stages.Count; i++)
            if (!(_stages[i].At > _stages[i - 1].At)) throw new ArgumentException("Stage times must be strictly ascending.", nameof(stages));
    }

    /// <inheritdoc/>
    public IRmsNetwork Initial { get; }

    /// <inheritdoc/>
    public IReadOnlyList<double> ScheduledTimes => _stages.Select(s => s.At).ToList();

    /// <inheritdoc/>
    public IRmsNetwork ApplyScheduled(int index, out string description)
    {
        description = _stages[index].Description;
        return _stages[index].Network;
    }

    /// <inheritdoc/>
    public IRmsNetwork ApplyRelayTrip(int branchIndex) =>
        _trip?.Invoke(branchIndex) ?? throw new InvalidOperationException("This topology has no relay trip handler.");

    /// <inheritdoc/>
    public IReadOnlyList<bool> MachineOut => _out;
}

/// <summary>One event as the adaptive run applied it. <see cref="Source"/> is <c>scheduled</c> or <c>relay</c>.</summary>
/// <param name="Source">scheduled (an input event) or relay (a trip, or a relay changing class).</param>
/// <param name="Kind">The event kind (for a relay: <c>trip</c>, <c>pickup</c>, <c>dropout</c>).</param>
/// <param name="Description">What happened.</param>
/// <param name="AppliedSeconds">The time it was applied at: exactly the requested time for a scheduled event; the high end of
/// the located bracket for a relay event.</param>
/// <param name="BracketLowSeconds">The low end of the located bracket (equals <paramref name="AppliedSeconds"/> for a scheduled event).</param>
/// <param name="Rank">Order in which events were applied (0 first); simultaneous events are ranked scheduled before relay,
/// scheduled events in input order, relay events by device number, pickup before trip.</param>
public sealed record AdaptiveAppliedEvent(string Source, string Kind, string Description, double AppliedSeconds,
    double BracketLowSeconds, int Rank);

/// <summary>One relay's behaviour over the run.</summary>
/// <param name="DeviceId">The device ID.</param>
/// <param name="BranchIndex">The branch it protects (index in the model).</param>
/// <param name="Tripped">Whether it tripped.</param>
/// <param name="PickupSeconds">The time it last picked up before its trip (or at the end, if it was picked up and did not
/// trip); null if it never picked up.</param>
/// <param name="TripSeconds">The trip time (applied time of the located event), or null.</param>
/// <param name="TripBracketLowSeconds">The low end of the trip bracket.</param>
/// <param name="TripCurrentA">The primary current (A) at the trip.</param>
/// <param name="MinActiveCurrentA">Smallest primary current sampled while picked up (accepted steps and events).</param>
/// <param name="MaxActiveCurrentA">Largest primary current sampled while picked up.</param>
public sealed record AdaptiveRelayRecord(string DeviceId, int BranchIndex, bool Tripped, double? PickupSeconds, double? TripSeconds,
    double? TripBracketLowSeconds, double? TripCurrentA, double? MinActiveCurrentA, double? MaxActiveCurrentA);

/// <summary>The variable-step swing run: declarations, counts, events and the trajectory.</summary>
public sealed record AdaptiveSwingResult(
    string Integration, string Scheme, AdaptiveOptions Options, int AcceptedSteps, int RejectedSteps, long Evaluations,
    double MinStepSeconds, double MaxStepSeconds, int EventBisections,
    IReadOnlyList<double> TimeSeconds, IReadOnlyList<IReadOnlyList<double>> DeltaRad, IReadOnlyList<IReadOnlyList<double>> SpeedPu,
    bool StayedInSynchronism, IReadOnlyList<AdaptiveAppliedEvent> Events, IReadOnlyList<AdaptiveRelayRecord> Relays)
{
    /// <summary>The fixed label of this integration path in every result.</summary>
    public const string AdaptiveLabel = "adaptive";

    /// <summary>RC5 stage 3: each unit relay stage's behaviour, or null when unit relays were not in the loop (every
    /// run without them; left out of the JSON then, so such a result's bytes are unchanged).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AdaptiveUnitRelayRecord>? UnitRelays { get; init; }

    /// <summary>RC5 stage 3: why the run stopped before its end (a unit trip would have left no connected machine), or
    /// null when it ran to the end.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }
}

/// <summary>The inputs of <see cref="AdaptiveSwingKernel.Run"/>. Per-machine arrays are in machine order.</summary>
public sealed class AdaptiveSwingProblem
{
    /// <summary>Inertia constants (s on the system base).</summary>
    public required double[] H { get; init; }
    /// <summary>Damping coefficients (pu torque per pu speed deviation).</summary>
    public required double[] D { get; init; }
    /// <summary>Mechanical powers (pu on the system base).</summary>
    public required double[] Pm { get; init; }
    /// <summary>Quadrature EMF magnitudes (frozen).</summary>
    public required double[] Eqpp { get; init; }
    /// <summary>Direct-axis EMF magnitudes (frozen).</summary>
    public required double[] Edpp { get; init; }
    /// <summary>Initial rotor angles (rad).</summary>
    public required double[] Delta0 { get; init; }
    /// <summary>Nominal frequency (Hz).</summary>
    public required double F0 { get; init; }
    /// <summary>Run length (s).</summary>
    public required double DurationSeconds { get; init; }
    /// <summary>Synchronism criterion: any connected machine further than this from the inertia-weighted centre-of-inertia angle.</summary>
    public double SlipLimitRad { get; init; } = 2.0 * Math.PI;
    /// <summary>How many of the leading machines are judged for synchronism (the rest are stiff island references); -1 for all.</summary>
    public int SynchronismMachines { get; init; } = -1;
    /// <summary>The plant topology.</summary>
    public required IAdaptiveTopology Topology { get; init; }
    /// <summary>Relays in the loop, or null.</summary>
    public RelayInLoop? Relays { get; init; }
    /// <summary>Where the relays read current (required when <see cref="Relays"/> has devices).</summary>
    public IRelayCurrentSource? CurrentSource { get; init; }
    /// <summary>The tolerances.</summary>
    public AdaptiveOptions Options { get; init; } = new();
    /// <summary>RC5 stage 3: each unit's own relays in the loop (opt-in), or null (every existing run). With stages to
    /// evaluate, <see cref="Topology"/> must implement <see cref="IAdaptiveUnitTripTopology"/> and the network must be the
    /// sparse full network (bus voltages).</summary>
    public UnitRelaysInLoop? UnitRelays { get; init; }
}

/// <summary>
/// RC4 R4-14: the variable-step electromechanical swing integration with scheduled events landed exactly and relay
/// events located by bisection. It is a NEW path: the fixed-step <see cref="MultiMachineSubtransient"/> kernel and every
/// established result are untouched.
/// <para>
/// <b>Model.</b> The machines are the classical swing with the subtransient EMF magnitudes frozen (the model the RK4
/// kernel integrates for its rotor swing, with flux decay, exciters and governors not represented):
/// <c>d delta/dt = ws w</c>, <c>dw/dt = (Pm - Pe - D w) / (2 H)</c>, <c>Pe = Re(E conj(I))</c>, with <c>E</c> from
/// <see cref="SubtransientNetworkInterface.EmfPhasorsInto"/> and the currents <c>I</c> from the network in force.
/// Relay states ride in the same state vector (see <see cref="RelayInLoop"/>).
/// </para>
/// <para>
/// <b>Order of operations.</b> Initialise (relays classified at t = 0) -> loop: choose the trial step (the controller's, cut to
/// land exactly on the next scheduled time or the end) -> step with the embedded estimate -> accept or reject by
/// <c>err &lt;= 1</c> -> locate relay events inside the accepted step (earliest first; ties by device number, pickup before
/// trip) and apply them at the located time -> at a scheduled time apply the scheduled events (input order), then any relay
/// event the new network causes at that same instant -> continue.
/// </para>
/// <para>
/// <b>Determinism.</b> Every decision is a function of the state and the options; the code is single-threaded, uses
/// <see cref="DetMath"/> for transcendental functions and has no shared mutable state, so a run is bit-identical
/// whenever and on however many threads rows are run (each row owns its instances).
/// </para>
/// </summary>
public static class AdaptiveSwingKernel
{
    /// <summary>Run the problem.</summary>
    public static AdaptiveSwingResult Run(AdaptiveSwingProblem p)
    {
        ArgumentNullException.ThrowIfNull(p);
        int n = p.H.Length;
        if (n == 0) throw new ArgumentException("At least one machine is needed.");
        if (p.D.Length != n || p.Pm.Length != n || p.Eqpp.Length != n || p.Edpp.Length != n || p.Delta0.Length != n)
            throw new ArgumentException("All per-machine arrays must have the same length.");
        for (int i = 0; i < n; i++)
            if (!(p.H[i] > 0.0) || !double.IsFinite(p.H[i])) throw new ArgumentOutOfRangeException(nameof(p), $"H[{i}] must be finite and > 0.");
        if (!(p.DurationSeconds > 0.0) || !double.IsFinite(p.DurationSeconds)) throw new ArgumentOutOfRangeException(nameof(p), "The duration must be positive and finite.");
        if (!(p.F0 > 0.0) || !double.IsFinite(p.F0)) throw new ArgumentOutOfRangeException(nameof(p), "The nominal frequency must be positive and finite.");
        p.Options.Validate();
        IRmsNetwork initial = p.Topology.Initial;
        if (initial.Machines != n || initial.Converters != 0)
            throw new ArgumentException("The network must carry one EMF node per machine and no converter nodes (the adaptive path does not integrate converter plant).");
        int nd = p.Relays?.Count ?? 0;
        if (nd > 0 && p.CurrentSource is null) throw new ArgumentException("Relays in the loop need a current source.");
        // RC5 stage 3: unit relay stages (0 for every existing run: nothing below that reads nu runs then).
        int nu = p.UnitRelays?.Count ?? 0;
        if (nu > 0 && p.Topology is not IAdaptiveUnitTripTopology)
            throw new ArgumentException("Unit relays in the loop need a topology that can disconnect a machine (IAdaptiveUnitTripTopology).");
        IReadOnlyList<double> sched = p.Topology.ScheduledTimes;
        for (int i = 0; i < sched.Count; i++)
        {
            if (!(sched[i] > 0.0) || !double.IsFinite(sched[i]) || sched[i] > p.DurationSeconds)
                throw new ArgumentOutOfRangeException(nameof(p), $"Scheduled time {sched[i]} must be after t = 0 and not after the end of the run.");
            if (i > 0 && !(sched[i] > sched[i - 1])) throw new ArgumentException("Scheduled times must be strictly ascending.");
        }

        var sys = new SwingSystem(p, initial, nd, nu);
        var ctl = new AdaptiveStepController(p.Options);
        int dim = sys.Dimension;
        var y = new double[dim];
        var yNew = new double[dim];
        var yTmp = new double[dim];
        var yEvt = new double[dim];
        for (int i = 0; i < n; i++) y[i] = p.Delta0[i];

        var times = new List<double>();
        var dSeries = new List<double>[n];
        var wSeries = new List<double>[n];
        for (int i = 0; i < n; i++) { dSeries[i] = new List<double>(); wSeries[i] = new List<double>(); }
        var events = new List<AdaptiveAppliedEvent>();
        var relayState = new RelayRun[nd];
        for (int d = 0; d < nd; d++) relayState[d] = new RelayRun();
        bool inSync = true;
        int accepted = 0, rejected = 0, bisections = 0;
        double minUsed = double.PositiveInfinity, maxUsed = 0.0;
        int rank = 0;
        var unitState = new UnitRun[nu];
        for (int k = 0; k < nu; k++) unitState[k] = new UnitRun();
        string? stopReason = null;
        var pendingShift = new List<(int Stage, double ShiftDeg)>();

        void Record(double t, double[] state)
        {
            times.Add(t);
            for (int i = 0; i < n; i++) { dSeries[i].Add(state[i]); wSeries[i].Add(state[n + i]); }
            int sync = p.SynchronismMachines >= 0 ? Math.Min(p.SynchronismMachines, n) : n;
            double hs = 0.0, hd = 0.0;
            for (int i = 0; i < sync; i++) if (!sys.MachineOut[i]) { hs += p.H[i]; hd += p.H[i] * state[i]; }
            double coi = hs > 0.0 ? hd / hs : 0.0;
            for (int i = 0; i < sync; i++)
            {
                if (sys.MachineOut[i]) continue;
                if (!double.IsFinite(state[i]) || !double.IsFinite(state[n + i]) || Math.Abs(state[i] - coi) > p.SlipLimitRad) inSync = false;
            }
            if (nd > 0)
            {
                sys.Currents(state);
                for (int d = 0; d < nd; d++)
                {
                    if (sys.Tripped[d] || sys.Mode[d] == RelayClass.Inactive) continue;
                    RelayRun rr = relayState[d];
                    double a = sys.Amps[d];
                    rr.Min = Math.Min(rr.Min, a); rr.Max = Math.Max(rr.Max, a);
                }
            }
        }

        // Relay bookkeeping at an event point (state y at time t): returns true when it changed anything.
        bool ApplyRelayPoint(double t, double lowBracket, double[] state, bool bracketed)
        {
            bool any = false;
            for (int guard = 0; guard <= 4 * nd + 4 && nd > 0; guard++)
            {
                sys.Currents(state);
                int pick = -1; RelayClass newClass = RelayClass.Inactive; bool trip = false;
                for (int d = 0; d < nd && pick < 0; d++)
                {
                    if (sys.Tripped[d]) continue;
                    RelayClass c = p.Relays!.Classify(d, sys.Amps[d]);
                    if (c != sys.Mode[d]) { pick = d; newClass = c; }
                    else if (sys.Mode[d] == RelayClass.Timed && state[2 * n + d] >= 1.0) { pick = d; newClass = c; trip = true; }
                }
                if (pick < 0) break;
                any = true;
                int dd = pick;
                RelayRun rr = relayState[dd];
                double amps = sys.Amps[dd];
                string id = p.Relays!.Device(dd).Id;
                if (!trip)
                {
                    sys.Mode[dd] = newClass;
                    rr.Min = Math.Min(rr.Min, amps); rr.Max = Math.Max(rr.Max, amps);
                    if (newClass == RelayClass.Inactive)
                    {
                        state[2 * n + dd] = 0.0; rr.Pickup = null; rr.Min = double.PositiveInfinity; rr.Max = double.NegativeInfinity;
                        events.Add(new AdaptiveAppliedEvent("relay", "dropout", FormattableString.Invariant($"relay {id} drops out at {amps:R} A"), t, lowBracket, rank++));
                    }
                    else
                    {
                        rr.Pickup ??= t;
                        if (newClass == RelayClass.Timed) rr.Pickup = t;
                        events.Add(new AdaptiveAppliedEvent("relay", "pickup", FormattableString.Invariant($"relay {id} picks up at {amps:R} A ({newClass})"), t, lowBracket, rank++));
                    }
                    if (newClass != RelayClass.Instant) continue;
                }
                // Trip: an instantaneous element on pickup, or a timed device whose fraction reached 1.
                sys.Tripped[dd] = true;
                rr.Tripped = true; rr.TripSeconds = t; rr.TripLow = lowBracket; rr.TripAmps = amps;
                rr.Pickup ??= t;
                int branch = p.Relays!.Device(dd).Location.BranchIndex;
                ChangeNetwork(p.Topology.ApplyRelayTrip(branch), state);
                events.Add(new AdaptiveAppliedEvent("relay", "trip", FormattableString.Invariant($"relay {id} trips branch {branch} at {amps:R} A"), t, lowBracket, rank++));
            }
            return any;
        }

        // RC5 stage 3: every network change goes through here. Without unit relays (nu = 0) it is exactly the SetNetwork
        // call it replaces. With a 78 (vector shift) stage in the loop, the bus voltages of the same state are solved on
        // the network before and after, and a unit whose terminal voltage angle jumps past its pickup is marked to trip at
        // this instant.
        void ChangeNetwork(IRmsNetwork net, double[] state)
        {
            if (nu == 0 || !p.UnitRelays!.HasVectorShift) { sys.SetNetwork(net, p.Topology.MachineOut); return; }
            sys.BusVoltages(state);
            var before = (Complex[])sys.VBus.Clone();
            sys.SetNetwork(net, p.Topology.MachineOut);
            sys.BusVoltages(state);
            for (int k = 0; k < nu; k++)
            {
                var e = p.UnitRelays.Get(k);
                if (e.Stage.Function != GridSim.Core.Protection.UnitRelayFunction.VectorShift || sys.UnitTripped[k] || sys.UnitMachineOut(k)) continue;
                double shift = UnitRelaysInLoop.ShiftDeg(before[e.BusIndex], sys.VBus[e.BusIndex]);
                if (shift > e.Stage.Pickup.Value && !pendingShift.Any(x => x.Stage == k)) pendingShift.Add((k, shift));
            }
        }

        // RC5 stage 3: unit relay bookkeeping at an event point (as ApplyRelayPoint). Returns false when a trip would leave
        // no connected machine: the run then stops at this instant, saying so (no continuation is fabricated).
        bool ApplyUnitPoint(double t, double lowBracket, double[] state)
        {
            for (int guard = 0; guard <= 4 * nu + 4 + nu; guard++)
            {
                int pick = -1; RelayClass newClass = RelayClass.Inactive; bool trip = false; double quantity = double.NaN;
                if (pendingShift.Count > 0)
                {
                    (pick, quantity) = pendingShift[0];
                    pendingShift.RemoveAt(0);
                    if (sys.UnitTripped[pick] || sys.UnitMachineOut(pick)) continue;
                    newClass = RelayClass.Instant; trip = true;
                }
                else
                {
                    sys.MeasureUnits(state);
                    for (int k = 0; k < nu && pick < 0; k++)
                    {
                        if (sys.UnitTripped[k] || sys.UnitMachineOut(k)) continue;
                        RelayClass c = sys.UnitClass(k);
                        if (c != sys.UnitMode[k]) { pick = k; newClass = c; }
                        else if (sys.UnitMode[k] == RelayClass.Timed && state[2 * n + nd + k] >= 1.0) { pick = k; newClass = c; trip = true; }
                    }
                    if (pick < 0) break;
                    quantity = p.UnitRelays!.Quantity(pick, sys.Hz, sys.Rocof, sys.PMw, sys.VMag, sys.VTerm);
                }
                var e = p.UnitRelays!.Get(pick);
                UnitRun ur = unitState[pick];
                string what = $"unit {e.Unit} relay {e.Stage.Device}";
                string q = quantity.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                if (!trip)
                {
                    sys.UnitMode[pick] = newClass;
                    if (newClass == RelayClass.Inactive)
                    {
                        state[2 * n + nd + pick] = 0.0; ur.Pickup = null;
                        events.Add(new AdaptiveAppliedEvent("unit-relay", "dropout", $"{what} drops out at {q}", t, lowBracket, rank++));
                    }
                    else
                    {
                        ur.Pickup = t;
                        events.Add(new AdaptiveAppliedEvent("unit-relay", "pickup", $"{what} picks up at {q} ({newClass})", t, lowBracket, rank++));
                    }
                    if (newClass != RelayClass.Instant) continue;
                }
                // Trip: an instantaneous stage on pickup, a timed stage whose fraction reached 1, or a vector shift.
                IRmsNetwork? net = ((IAdaptiveUnitTripTopology)p.Topology).ApplyUnitTrip(e.Slot);
                if (net is null)
                {
                    stopReason = $"{what} operated at {t.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s, but tripping unit {e.Unit} would leave no connected machine; the run stops here";
                    events.Add(new AdaptiveAppliedEvent("unit-relay", "stop", stopReason, t, lowBracket, rank++));
                    return false;
                }
                sys.UnitTripped[pick] = true;
                ur.Tripped = true; ur.TripSeconds = t; ur.TripLow = lowBracket; ur.TripQuantity = quantity;
                ur.Pickup ??= t;
                events.Add(new AdaptiveAppliedEvent("unit-relay", "trip", $"{what} trips unit {e.Unit} at {q}", t, lowBracket, rank++));
                ChangeNetwork(net, state);
            }
            return true;
        }


        // t = 0: the relays are classified on the undisturbed state (a relay already beyond pickup picks up at 0).
        double now = 0.0;
        ApplyRelayPoint(0.0, 0.0, y, false);
        // A unit trip at t = 0 that would leave no machine stops the run there, as at every later event point.
        bool stopped = nu > 0 && !ApplyUnitPoint(0.0, 0.0, y);
        Record(0.0, y);

        double h = Math.Min(p.Options.MaxStepSeconds, Math.Max(p.Options.MinStepSeconds, p.Options.InitialStepSeconds));
        int stageIdx = 0;
        long stepGuard = 0;
        const long MaxSteps = 50_000_000;
        while (!stopped && now < p.DurationSeconds)
        {
            if (++stepGuard > MaxSteps) throw new AdaptiveStepException("The run exceeded the step cap; the system may be stiff for this tolerance.");
            double nextStage = stageIdx < sched.Count ? sched[stageIdx] : double.PositiveInfinity;
            double boundary = Math.Min(nextStage, p.DurationSeconds);
            double hTry = h;
            if (boundary - now <= 1.1 * h) hTry = boundary - now;
            double wanted = h;
            bool landsOnBoundary = hTry == boundary - now;

            double err = ctl.Step(sys, now, y, hTry, yNew);
            if (!(err <= 1.0))
            {
                if (hTry <= p.Options.MinStepSeconds)
                    throw new AdaptiveStepException(FormattableString.Invariant($"The step at t = {now:R} s failed the error test at the minimum step {p.Options.MinStepSeconds:R} s ")
                        + (double.IsFinite(err) ? FormattableString.Invariant($"(error norm {err:R}).") : "(non-finite state)."));
                rejected++;
                h = ctl.NextStep(hTry, err, hTry);
                continue;
            }
            accepted++;
            minUsed = Math.Min(minUsed, hTry); maxUsed = Math.Max(maxUsed, hTry);

            // Relay events strictly inside the step (RC5: and unit relay events, numbered after the devices).
            if (nd > 0 || nu > 0)
            {
                var candidates = new List<(int Device, int Kind, double Hi, double Lo)>();   // Kind 0 = class change, 1 = trip
                if (nd > 0)
                {
                sys.Currents(yNew);
                var endAmps = (double[])sys.Amps.Clone();
                for (int d = 0; d < nd; d++)
                {
                    if (sys.Tripped[d]) continue;
                    int dev = d;
                    RelayClass atStart = sys.Mode[dev];
                    if (p.Relays!.Classify(dev, endAmps[dev]) != atStart)
                    {
                        LocatedEvent le = EventLocator.Locate(now, now + hTry, p.Options.EventTimeToleranceSeconds, tm =>
                        {
                            ctl.Step(sys, now, y, tm - now, yTmp);
                            sys.Currents(yTmp);
                            return p.Relays!.Classify(dev, sys.Amps[dev]) != atStart;
                        });
                        bisections += le.Bisections;
                        candidates.Add((dev, 0, le.HighSeconds, le.LowSeconds));
                    }
                    if (atStart == RelayClass.Timed && yNew[2 * n + dev] >= 1.0)
                    {
                        LocatedEvent le = EventLocator.Locate(now, now + hTry, p.Options.EventTimeToleranceSeconds, tm =>
                        {
                            ctl.Step(sys, now, y, tm - now, yTmp);
                            return yTmp[2 * n + dev] >= 1.0;
                        });
                        bisections += le.Bisections;
                        candidates.Add((dev, 1, le.HighSeconds, le.LowSeconds));
                    }
                }
                }
                if (nu > 0)
                {
                    sys.MeasureUnits(yNew);
                    var endClass = new RelayClass[nu];
                    for (int k = 0; k < nu; k++) endClass[k] = sys.UnitClass(k);
                    for (int k = 0; k < nu; k++)
                    {
                        if (sys.UnitTripped[k] || sys.UnitMachineOut(k)) continue;
                        int kk = k;
                        RelayClass atStart = sys.UnitMode[kk];
                        if (endClass[kk] != atStart)
                        {
                            LocatedEvent le = EventLocator.Locate(now, now + hTry, p.Options.EventTimeToleranceSeconds, tm =>
                            {
                                ctl.Step(sys, now, y, tm - now, yTmp);
                                sys.MeasureUnits(yTmp);
                                return sys.UnitClass(kk) != atStart;
                            });
                            bisections += le.Bisections;
                            candidates.Add((nd + kk, 0, le.HighSeconds, le.LowSeconds));
                        }
                        if (atStart == RelayClass.Timed && yNew[2 * n + nd + kk] >= 1.0)
                        {
                            LocatedEvent le = EventLocator.Locate(now, now + hTry, p.Options.EventTimeToleranceSeconds, tm =>
                            {
                                ctl.Step(sys, now, y, tm - now, yTmp);
                                return yTmp[2 * n + nd + kk] >= 1.0;
                            });
                            bisections += le.Bisections;
                            candidates.Add((nd + kk, 1, le.HighSeconds, le.LowSeconds));
                        }
                    }
                }
                double endT = landsOnBoundary ? boundary : now + hTry;
                var interior = candidates.Where(c => c.Hi < endT).OrderBy(c => c.Hi).ThenBy(c => c.Device).ThenBy(c => c.Kind).ToList();
                if (interior.Count > 0)
                {
                    var first = interior[0];
                    ctl.Step(sys, now, y, first.Hi - now, yEvt);
                    Array.Copy(yEvt, y, dim);
                    now = first.Hi;
                    ApplyRelayPoint(now, first.Lo, y, true);
                    if (nu > 0 && !ApplyUnitPoint(now, first.Lo, y)) { Record(now, y); break; }
                    Record(now, y);
                    h = ctl.NextStep(hTry, err, wanted);
                    continue;
                }
            }

            now = landsOnBoundary ? boundary : now + hTry;
            Array.Copy(yNew, y, dim);
            if (stageIdx < sched.Count && now == sched[stageIdx])
            {
                IRmsNetwork net = p.Topology.ApplyScheduled(stageIdx, out string description);
                if (nu > 0 && p.Topology is IAdaptiveUnitTripTopology { NoConnectedMachine: true })
                {
                    stopReason = $"the scheduled event(s) at {now.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s ({description}) would leave no connected machine (scheduled and unit relay trips together); the run stops here";
                    events.Add(new AdaptiveAppliedEvent("scheduled", "stop", stopReason, now, now, rank++));
                    Record(now, y);
                    break;
                }
                ChangeNetwork(net, y);
                events.Add(new AdaptiveAppliedEvent("scheduled", "scheduled", description, now, now, rank++));
                stageIdx++;
            }
            ApplyRelayPoint(now, now, y, false);
            if (nu > 0 && !ApplyUnitPoint(now, now, y)) { Record(now, y); break; }
            Record(now, y);
            h = ctl.NextStep(hTry, err, wanted);
        }

        var relayRecords = new List<AdaptiveRelayRecord>();
        for (int d = 0; d < nd; d++)
        {
            RelayRun rr = relayState[d];
            bool seen = rr.Min <= rr.Max;
            relayRecords.Add(new AdaptiveRelayRecord(p.Relays!.Device(d).Id, p.Relays.Device(d).Location.BranchIndex, rr.Tripped, rr.Pickup,
                rr.Tripped ? rr.TripSeconds : null, rr.Tripped ? rr.TripLow : null, rr.Tripped ? rr.TripAmps : null,
                seen ? rr.Min : null, seen ? rr.Max : null));
        }
        var result = new AdaptiveSwingResult(AdaptiveSwingResult.AdaptiveLabel, AdaptiveStepController.Scheme, p.Options, accepted, rejected,
            ctl.Evaluations, double.IsInfinity(minUsed) ? 0.0 : minUsed, maxUsed, bisections, times,
            dSeries.Select(s => (IReadOnlyList<double>)s).ToList(), wSeries.Select(s => (IReadOnlyList<double>)s).ToList(),
            inSync, events, relayRecords);
        if (p.UnitRelays is null) return result;
        var unitRecords = new List<AdaptiveUnitRelayRecord>();
        for (int k = 0; k < nu; k++)
        {
            UnitRun ur = unitState[k];
            var e = p.UnitRelays.Get(k);
            unitRecords.Add(new AdaptiveUnitRelayRecord(e.Unit, e.Stage.Device, ur.Tripped, ur.Pickup, ur.Tripped ? ur.TripSeconds : null,
                ur.Tripped ? ur.TripLow : null, ur.Tripped ? ur.TripQuantity : null, GridSim.Core.Protection.UnitRelayAssessment.SettingsPhrase(e.Stage))
            { VoltageMeasuredAt = e.VoltageAt });
        }
        return result with { UnitRelays = unitRecords, StopReason = stopReason };
    }

    private sealed class UnitRun
    {
        public double? Pickup;
        public bool Tripped;
        public double TripSeconds, TripLow, TripQuantity;
    }

    private sealed class RelayRun
    {
        public double Min = double.PositiveInfinity, Max = double.NegativeInfinity;
        public double? Pickup;
        public bool Tripped;
        public double TripSeconds, TripLow, TripAmps;
    }

    /// <summary>The state-space system: [delta (n), speed deviation (n), relay fractions (nd), unit relay fractions (nu)].</summary>
    private sealed class SwingSystem : IAdaptiveOde
    {
        private readonly AdaptiveSwingProblem _p;
        private readonly int _n, _nd, _nu;
        private readonly double _ws;
        private readonly double[] _delta;
        private readonly Complex[] _e, _im, _noConv = [], _noVc = [];
        private IRmsNetwork _net;
        private bool[] _out;

        public readonly RelayClass[] Mode;
        public readonly bool[] Tripped;
        public readonly double[] Amps;

        // RC5 stage 3: unit relay state and the measurement buffers (empty when nu = 0).
        public readonly RelayClass[] UnitMode;
        public readonly bool[] UnitTripped;
        public readonly double[] Hz, Rocof, PMw, VMag;
        public readonly double[] VTerm;   // per unit stage: the machine terminal |V_t| of a 27 / 59 behind a unit transformer
        public readonly Complex[] VBus;
        private readonly Complex[] _imU;

        public SwingSystem(AdaptiveSwingProblem p, IRmsNetwork initial, int nd, int nu = 0)
        {
            _p = p; _n = p.H.Length; _nd = nd; _ws = 2.0 * Math.PI * p.F0;
            _delta = new double[_n]; _e = new Complex[_n]; _im = new Complex[_n];
            _net = initial; _out = p.Topology.MachineOut.ToArray();
            Mode = new RelayClass[nd]; Tripped = new bool[nd]; Amps = new double[nd];
            Dimension = 2 * _n + nd;
            _nu = nu;
            UnitMode = new RelayClass[nu]; UnitTripped = new bool[nu];
            Hz = new double[nu > 0 ? _n : 0]; Rocof = new double[nu > 0 ? _n : 0]; PMw = new double[nu > 0 ? _n : 0];
            int buses = nu > 0 ? p.UnitRelays!.BusCount : 0;
            VMag = new double[buses]; VBus = new Complex[buses];
            _imU = new Complex[nu > 0 ? _n : 0];
            VTerm = new double[nu > 0 && p.UnitRelays!.HasTerminalVoltage ? nu : 0];
            Dimension += nu;
        }

        public IRmsNetwork Network => _net;

        /// <summary>RC5: whether unit stage k's machine is disconnected.</summary>
        public bool UnitMachineOut(int k) => _out[_p.UnitRelays!.Get(k).Slot];

        /// <summary>RC5: the class of unit stage k at the last <see cref="MeasureUnits"/>.</summary>
        public RelayClass UnitClass(int k) =>
            !_p.UnitRelays!.Beyond(k, Hz, Rocof, PMw, VMag, VTerm) ? RelayClass.Inactive : _p.UnitRelays.Instant(k) ? RelayClass.Instant : RelayClass.Timed;

        /// <summary>RC5: the unit quantities of state <paramref name="y"/> on the network in force: per machine the electrical
        /// frequency F0 (1 + w), RoCoF F0 dw/dt (the swing equation) and the electrical power in MW; per bus the voltage.</summary>
        public void MeasureUnits(double[] y)
        {
            Array.Copy(y, 0, _delta, 0, _n);
            SubtransientNetworkInterface.EmfPhasorsInto(_p.Eqpp, _p.Edpp, _delta, _e);
            _net.SolveInto(_e, _noConv, _imU, _noVc);
            double f0 = _p.F0, baseMva = _p.UnitRelays!.BaseMva;
            for (int i = 0; i < _n; i++)
            {
                double w = y[_n + i];
                double pe = _out[i] ? 0.0 : _e[i].Real * _imU[i].Real + _e[i].Imaginary * _imU[i].Imaginary;
                double dw = _out[i] ? (0.0 - _p.D[i] * w) / (2.0 * _p.H[i]) : (_p.Pm[i] - pe - _p.D[i] * w) / (2.0 * _p.H[i]);
                Hz[i] = f0 * (1.0 + w);
                Rocof[i] = f0 * dw;
                PMw[i] = pe * baseMva;
            }
            if (VBus.Length > 0) BusVoltages(y);
            // RC5: 27 / 59 behind a folded unit transformer read the machine terminal, V_hv + j X_t I, of the same state.
            if (VTerm.Length > 0) _p.UnitRelays.TerminalVoltages(VBus, _imU, VTerm);
        }

        /// <summary>RC5: every bus voltage of state <paramref name="y"/> on the network in force into <see cref="VBus"/> /
        /// <see cref="VMag"/>.</summary>
        public void BusVoltages(double[] y)
        {
            if (_net is not SparseRmsNetwork sparse)
                throw new InvalidOperationException("Unit relays reading a bus voltage need the sparse full network.");
            Array.Copy(y, 0, _delta, 0, _n);
            SubtransientNetworkInterface.EmfPhasorsInto(_p.Eqpp, _p.Edpp, _delta, _e);
            sparse.SolveBusVoltagesInto(_e, _noConv, VBus);
            for (int b = 0; b < VBus.Length; b++) VMag[b] = VBus[b].Magnitude;
        }

        public int Dimension { get; }

        /// <summary>The rotor angles (the first n states) take the fixed angle scale of the error test.</summary>
        public int AngleStates => _n;

        public IReadOnlyList<bool> MachineOut => _out;

        public void SetNetwork(IRmsNetwork net, IReadOnlyList<bool> machineOut)
        {
            if (net.Machines != _n || net.Converters != 0) throw new InvalidOperationException("A topology change must keep one EMF node per machine and no converters.");
            _net = net;
            _out = machineOut.ToArray();
        }

        /// <summary>The relay currents of the state <paramref name="y"/> on the network in force, into <see cref="Amps"/>.</summary>
        public void Currents(double[] y)
        {
            if (_nd == 0) return;
            Array.Copy(y, 0, _delta, 0, _n);
            SubtransientNetworkInterface.EmfPhasorsInto(_p.Eqpp, _p.Edpp, _delta, _e);
            _p.CurrentSource!.PrimaryCurrentsA(_net, _e, Amps);
        }

        public void Derivative(double t, double[] y, double[] dydt)
        {
            Array.Copy(y, 0, _delta, 0, _n);
            SubtransientNetworkInterface.EmfPhasorsInto(_p.Eqpp, _p.Edpp, _delta, _e);
            _net.SolveInto(_e, _noConv, _im, _noVc);
            for (int i = 0; i < _n; i++)
            {
                double w = y[_n + i];
                dydt[i] = _ws * w;
                if (_out[i]) { dydt[_n + i] = (0.0 - _p.D[i] * w) / (2.0 * _p.H[i]); continue; }
                double pe = _e[i].Real * _im[i].Real + _e[i].Imaginary * _im[i].Imaginary;   // Re(E conj(I))
                dydt[_n + i] = (_p.Pm[i] - pe - _p.D[i] * w) / (2.0 * _p.H[i]);
            }
            if (_nd > 0)
            {
                _p.CurrentSource!.PrimaryCurrentsA(_net, _e, Amps);
                for (int d = 0; d < _nd; d++)
                    dydt[2 * _n + d] = !Tripped[d] && Mode[d] == RelayClass.Timed ? _p.Relays!.Rate(d, Amps[d]) : 0.0;
            }
            // RC5: a picked-up timed unit stage uses its delay at a constant rate (dropout is located as an event).
            for (int k = 0; k < _nu; k++)
                dydt[2 * _n + _nd + k] = !UnitTripped[k] && UnitMode[k] == RelayClass.Timed && !UnitMachineOut(k)
                    ? 1.0 / _p.UnitRelays!.Get(k).Stage.Delay.Value : 0.0;
        }
    }
}
