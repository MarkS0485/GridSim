using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Dynamics.Models;

public static partial class RmsTransientStability
{
    /// <summary>R4-14: an adaptive RMS event study: the variable-step run, the prepared fleet it started from and the devices
    /// the loop did not evaluate.</summary>
    public sealed record AdaptiveStudy(AdaptiveSwingResult Run, SubtransientInit.Prepared Prepared,
        IReadOnlyList<RelayNotEvaluated> RelaysNotEvaluated, string ModelDeclaration);

    /// <summary>What the adaptive path represents, stated in every result.</summary>
    public const string AdaptiveModelDeclaration =
        "electromechanical swing of the prepared machines: subtransient EMF magnitudes frozen at their initial values, constant mechanical "
        + "power equal to the initial electrical power, network re-solved at every stage evaluation; exciters, governors, flux decay and "
        + "converter plant are not represented (the fixed-step path remains the full-model answer)";

    /// <summary>
    /// R4-14: the variable-step companion of <see cref="StudyEvents"/>. The same events, the same prepared fleet and the same
    /// sparse network stages, integrated by the adaptive swing kernel (<see cref="AdaptiveSwingKernel"/>): events land on
    /// their requested times exactly (no quantisation to a step), and relays of a placed device set
    /// (<paramref name="devices"/>) act in the loop, a trip opening the protected branch at its located time.
    /// <para>
    /// The fixed-step <see cref="StudyEvents"/> and everything it calls are not touched: this is a separate entry point, so no
    /// existing number moves. Refused, as in <see cref="StudyEvents"/>: an empty list or a null entry, a time that is not finite
    /// or not after t = 0 or after the end, a bus or branch or machine that does not exist, a branch tripped twice, a
    /// clearance with no fault, a load step of nothing. Converter plant in the case is refused (not integrated by this path).
    /// </para>
    /// </summary>
    public static AdaptiveStudy StudyEventsAdaptive(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, AdaptiveOptions options, BoundDeviceSet? devices = null,
        double? f0 = null, double durationSeconds = StudyDefaultDurationSeconds, double slipLimitRad = DefaultSlipLimitRad)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!(durationSeconds > 0.0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "The duration must be positive and finite.");
        if (!(slipLimitRad > 0.0)) throw new ArgumentOutOfRangeException(nameof(slipLimitRad), slipLimitRad, "The slip limit must be positive.");
        double nominalHz = ResolveNominalHz(model, f0);
        result.RequireConverged("adaptive RMS event study");

        for (int i = 0; i < events.Count; i++)
        {
            RmsEvent? ev = events[i];
            if (ev is null) throw new ArgumentException($"events[{i}] is null.", nameof(events));
            if (!double.IsFinite(ev.AtSeconds) || !(ev.AtSeconds > 0.0))
                throw new ArgumentOutOfRangeException(nameof(events), ev.AtSeconds,
                    $"events[{i}] ({ev.Describe()}): the time must be finite and after t = 0.");
            if (ev.AtSeconds > durationSeconds)
                throw new ArgumentOutOfRangeException(nameof(events), ev.AtSeconds,
                    $"events[{i}] ({ev.Describe()}) is after the end of the run ({durationSeconds} s); it would never happen.");
        }

        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, result, data);
        SubtransientInit.SparseNetworkBasis basis = p.Basis
            ?? throw new InvalidOperationException("The adaptive RMS study needs the sparse network basis.");
        if (p.ConverterUnits.Count > 0)
            throw new ArgumentException("The case has converter plant, which the adaptive path does not integrate; use the fixed-step study.", nameof(data));
        int[] slotOfGenerator = MachineSlots(model, data, p);
        int connectedMachines = p.Machines.Count - (p.IslandReferences?.Count ?? 0);

        var topology = new AdaptiveEventTopology(model, basis, events, slotOfGenerator, connectedMachines, p.Machines.Count, p.Network!);
        int n = p.Machines.Count;

        // The mechanical power is the initial electrical power of the swing model, so the undisturbed run is an equilibrium by construction.
        var emf0 = new Complex[n];
        SubtransientNetworkInterface.EmfPhasorsInto(p.Eqpp0, p.Edpp0, p.Delta0, emf0);
        var im0 = new Complex[n];
        p.Network!.SolveInto(emf0, Array.Empty<Complex>(), im0, Array.Empty<Complex>());
        var pm = new double[n];
        for (int i = 0; i < n; i++) pm[i] = emf0[i].Real * im0[i].Real + emf0[i].Imaginary * im0[i].Imaginary;

        RelayInLoop? relays = null;
        IRelayCurrentSource? source = null;
        if (devices is not null)
        {
            relays = new RelayInLoop(devices, options.EventTimeToleranceSeconds);
            if (relays.Count > 0) source = new BranchCurrentSource(model, basis, relays, topology);
        }

        AdaptiveSwingResult run = AdaptiveSwingKernel.Run(new AdaptiveSwingProblem
        {
            H = p.H, D = p.Damping, Pm = pm, Eqpp = p.Eqpp0, Edpp = p.Edpp0, Delta0 = p.Delta0,
            F0 = nominalHz, DurationSeconds = durationSeconds, SlipLimitRad = slipLimitRad,
            SynchronismMachines = connectedMachines, Topology = topology,
            Relays = relays is { Count: > 0 } ? relays : null, CurrentSource = source, Options = options,
        });
        return new AdaptiveStudy(run, p, relays?.NotEvaluated ?? Array.Empty<RelayNotEvaluated>(), AdaptiveModelDeclaration);
    }

    /// <summary>R4-14: the adaptive critical clearing time of a bolted fault cleared by tripping branches: the fault is applied at
    /// <paramref name="onsetSeconds"/> and cleared (with the trips) after a trial clearing time that is NOT rounded to a step.
    /// The bracket is bisected to <paramref name="toleranceSeconds"/>; a trial is stable when every connected machine stays
    /// within the slip limit of the centre of inertia for the post-clearing window.</summary>
    public static AdaptiveCct.Result StudyCctAdaptive(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        int faultBus, IReadOnlyList<int> clearingTripBranches, double onsetSeconds, double maxClearingSeconds, double toleranceSeconds,
        AdaptiveOptions options, double? f0 = null, double postClearWindowSeconds = PostClearWindowSeconds, double slipLimitRad = DefaultSlipLimitRad)
    {
        ArgumentNullException.ThrowIfNull(clearingTripBranches);
        bool Stable(double clearing)
        {
            var ev = new List<RmsEvent> { new RmsBusFault(onsetSeconds, faultBus) };
            double at = onsetSeconds + clearing;
            ev.Add(new RmsFaultClear(at, faultBus));
            foreach (int b in clearingTripBranches) ev.Add(new RmsBranchTrip(at, b));
            return StudyEventsAdaptive(model, result, data, ev, options, null, f0, at + postClearWindowSeconds, slipLimitRad).Run.StayedInSynchronism;
        }
        return AdaptiveCct.Search(Stable, maxClearingSeconds, toleranceSeconds);
    }

    /// <summary>The cumulative network state of the scheduled events, with the relay-tripped branches kept beside it.</summary>
    private sealed class AdaptiveEventTopology : IAdaptiveTopology
    {
        private readonly GridModel _model;
        private readonly SubtransientInit.SparseNetworkBasis _basis;
        private readonly int[] _slotOfGenerator;
        private readonly int _connected;
        private readonly List<(RmsEvent Event, int Index)> _ordered;
        private readonly List<int> _groupEnds = new();      // index (exclusive) into _ordered where each time group ends
        private readonly List<double> _times = new();
        private readonly List<IRmsNetwork> _prebuilt = new();
        private readonly HashSet<int> _busIds;

        private List<int> _scheduledTripped = new();
        private readonly List<int> _relayTripped = new();
        private List<int> _faulted = new();
        private List<int> _loadBuses = new();
        private Dictionary<int, (double P, double Q)> _loadDelta = new();
        private bool[] _machineOut;

        public AdaptiveEventTopology(GridModel model, SubtransientInit.SparseNetworkBasis basis, IReadOnlyList<RmsEvent> events,
            int[] slotOfGenerator, int connectedMachines, int machineCount, IRmsNetwork initial)
        {
            _model = model; _basis = basis; _slotOfGenerator = slotOfGenerator; _connected = connectedMachines;
            Initial = initial;
            _busIds = new HashSet<int>(model.Buses.Select(b => b.Id));
            _machineOut = new bool[machineCount];
            _ordered = events.Select((ev, index) => (Event: ev, Index: index)).OrderBy(x => x.Event.AtSeconds).ToList();
            for (int k = 0; k < _ordered.Count; k++)
            {
                bool last = k + 1 == _ordered.Count || _ordered[k + 1].Event.AtSeconds != _ordered[k].Event.AtSeconds;
                if (last) { _groupEnds.Add(k + 1); _times.Add(_ordered[k].Event.AtSeconds); }
            }
            // Dry run: apply every group in order (validating every event, naming it) and build every stage once, so a stage that
            // cannot be solved fails before any integration; the networks are kept for runs where no relay has yet tripped.
            for (int g = 0; g < _times.Count; g++) { ApplyGroup(g); _prebuilt.Add(Build()); }
            Reset();
        }

        public IRmsNetwork Initial { get; }

        public IReadOnlyList<double> ScheduledTimes => _times;

        public IReadOnlyList<bool> MachineOut => _machineOut;

        private void Reset()
        {
            _scheduledTripped = new List<int>(); _relayTripped.Clear(); _faulted = new List<int>(); _loadBuses = new List<int>();
            _loadDelta = new Dictionary<int, (double P, double Q)>(); _machineOut = new bool[_machineOut.Length];
        }

        /// <summary>Whether a branch is open now (out in the model, tripped by a scheduled event or tripped by a relay).</summary>
        public bool IsBranchOut(int branchIndex) =>
            !_model.Branches[branchIndex].InService || _scheduledTripped.Contains(branchIndex) || _relayTripped.Contains(branchIndex);

        public IRmsNetwork ApplyScheduled(int index, out string description)
        {
            description = ApplyGroup(index);
            return _relayTripped.Count == 0 ? _prebuilt[index] : Build();
        }

        public IRmsNetwork ApplyRelayTrip(int branchIndex)
        {
            if (!_relayTripped.Contains(branchIndex) && !_scheduledTripped.Contains(branchIndex)) _relayTripped.Add(branchIndex);
            return Build();
        }

        private IRmsNetwork Build()
        {
            List<int> tripped = _scheduledTripped.Union(_relayTripped).OrderBy(i => i).ToList();
            return BuildEventStage(_model, _basis, tripped, _faulted, _loadBuses, _loadDelta, _machineOut);
        }

        private string ApplyGroup(int g)
        {
            int from = g == 0 ? 0 : _groupEnds[g - 1];
            var parts = new List<string>();
            for (int k = from; k < _groupEnds[g]; k++)
            {
                (RmsEvent ev, int i) = _ordered[k];
                string what = $"events[{i}] ({ev.Describe()})";
                parts.Add(ev.Describe());
                switch (ev)
                {
                    case RmsBusFault f:
                        Bus(f.BusId, what);
                        if (_faulted.Contains(f.BusId)) throw new ArgumentException($"{what}: bus {f.BusId} is already faulted at that time.");
                        _faulted.Add(f.BusId);
                        break;
                    case RmsFaultClear c:
                        Bus(c.BusId, what);
                        if (!_faulted.Remove(c.BusId)) throw new ArgumentException($"{what}: there is no fault in place at bus {c.BusId} to clear at that time.");
                        break;
                    case RmsBranchTrip b:
                        Branch(b.BranchIndex, what);
                        if (!_model.Branches[b.BranchIndex].InService) throw new ArgumentException($"{what}: the branch is already out of service in the model.");
                        if (_scheduledTripped.Contains(b.BranchIndex)) throw new ArgumentException($"{what}: the branch is already tripped by an earlier event.");
                        _scheduledTripped.Add(b.BranchIndex);
                        _relayTripped.Remove(b.BranchIndex);   // an input trip of a branch a relay opened first takes over the record
                        break;
                    case RmsBranchReclose rc:
                        Branch(rc.BranchIndex, what);
                        if (!_scheduledTripped.Remove(rc.BranchIndex)) throw new ArgumentException($"{what}: the branch is not tripped at that time (a reclose must follow its trip).");
                        break;
                    case RmsLoadStep l:
                        Bus(l.BusId, what);
                        if (!double.IsFinite(l.DeltaPMw) || !double.IsFinite(l.DeltaQMvar)) throw new ArgumentException($"{what}: the load step must be finite.");
                        if (l.DeltaPMw == 0.0 && l.DeltaQMvar == 0.0) throw new ArgumentException($"{what}: a load step of zero in both P and Q changes nothing.");
                        if (!_loadDelta.TryGetValue(l.BusId, out (double P, double Q) acc)) { _loadBuses.Add(l.BusId); acc = (0.0, 0.0); }
                        _loadDelta[l.BusId] = (acc.P + l.DeltaPMw, acc.Q + l.DeltaQMvar);
                        break;
                    case RmsGeneratorTrip gt:
                        if (gt.GeneratorIndex < 0 || gt.GeneratorIndex >= _model.Generators.Count)
                            throw new ArgumentException($"{what}: the model has {_model.Generators.Count} in-service generators.");
                        int slot = _slotOfGenerator[gt.GeneratorIndex];
                        if (slot < 0) throw new ArgumentException($"{what}: the generator is not integrated as a synchronous machine, so it cannot be tripped as one.");
                        if (_machineOut[slot]) throw new ArgumentException($"{what}: the generator is already tripped by an earlier event.");
                        _machineOut[slot] = true;
                        if (_machineOut.Count(x => x) >= _connected) throw new ArgumentException($"{what}: it would trip the last connected machine.");
                        break;
                    default:
                        throw new ArgumentException($"{what}: unknown event kind {ev.GetType().Name}.");
                }
            }
            return string.Join("; ", parts);
        }

        private void Bus(int id, string what)
        {
            if (!_busIds.Contains(id)) throw new ArgumentException($"{what}: bus {id} is not in the model.");
        }

        private void Branch(int index, string what)
        {
            if (index < 0 || index >= _model.Branches.Count)
                throw new ArgumentException($"{what}: the model has {_model.Branches.Count} branches (0 to {_model.Branches.Count - 1}).");
        }
    }

    /// <summary>Relay primary currents from the network's bus voltages: the near-end current of each protected branch from its
    /// two-port stamp (tap and phase shift included), converted from per unit to amperes at the near bus's base voltage. A branch
    /// that is open carries no current.</summary>
    private sealed class BranchCurrentSource : IRelayCurrentSource
    {
        private readonly RelayInLoop _relays;
        private readonly AdaptiveEventTopology _topology;
        private readonly int[] _branch, _near, _far;
        private readonly bool[] _atFrom;
        private readonly BranchStamp[] _stamp;
        private readonly double[] _ampsPerPu;
        private readonly Complex[] _v;
        private readonly Complex[] _noConv = [];

        public BranchCurrentSource(GridModel model, SubtransientInit.SparseNetworkBasis basis, RelayInLoop relays, AdaptiveEventTopology topology)
        {
            _relays = relays; _topology = topology;
            int nd = relays.Count;
            _branch = new int[nd]; _near = new int[nd]; _far = new int[nd]; _atFrom = new bool[nd];
            _stamp = new BranchStamp[nd]; _ampsPerPu = new double[nd];
            _v = new Complex[basis.BusCount];
            for (int d = 0; d < nd; d++)
            {
                DeviceLocation l = relays.Device(d).Location;
                Branch br = model.Branches[l.BranchIndex];
                _branch[d] = l.BranchIndex;
                _atFrom[d] = l.AtFromEnd;
                _near[d] = basis.BusIndex[l.NearBus];
                _far[d] = basis.BusIndex[l.FarBus];
                _stamp[d] = AdmittanceStamp.Branch(br.R, br.X, br.B, br.TapRatio, br.PhaseShiftDeg);
                double kv = model.Buses.First(b => b.Id == l.NearBus).BaseKv;
                _ampsPerPu[d] = model.BaseMva * 1000.0 / (Math.Sqrt(3.0) * kv);
            }
        }

        public void PrimaryCurrentsA(IRmsNetwork network, Complex[] emf, double[] amps)
        {
            if (network is not SparseRmsNetwork sparse)
                throw new InvalidOperationException("Relay currents need the sparse full network (bus voltages).");
            sparse.SolveBusVoltagesInto(emf, _noConv, _v);
            for (int d = 0; d < _branch.Length; d++)
            {
                if (_topology.IsBranchOut(_branch[d])) { amps[d] = 0.0; continue; }
                BranchStamp s = _stamp[d];
                Complex vn = _v[_near[d]], vf = _v[_far[d]];
                Complex i = _atFrom[d] ? s.Yff * vn + s.Yft * vf : s.Ytt * vn + s.Ytf * vf;
                amps[d] = i.Magnitude * _ampsPerPu[d];
            }
        }
    }
}
