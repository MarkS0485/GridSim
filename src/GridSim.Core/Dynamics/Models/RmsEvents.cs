using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// R061 rest (CF4, October 2026; Ruling R1): one scheduled disturbance of an RMS event study
/// (<see cref="RmsTransientStability.StudyEvents"/>). Every event takes effect at the first integration step whose time
/// has reached <see cref="AtSeconds"/> (the network-stage rule of the kernel, with its millionth-of-a-step tolerance) and
/// stays in effect for the rest of the run unless a later event undoes it (only a bus fault can be undone, by a
/// <see cref="RmsFaultClear"/>). The kinds are the ones PSS/E dynamic simulation and PowerFactory RMS studies offer as
/// their basic switching events: a bolted bus fault and its clearance, a branch trip, a load step and a generator trip.
/// </summary>
/// <param name="AtSeconds">When the event happens (s from the start of the run): finite, after t = 0 (the run starts
/// from the solved, undisturbed state) and no later than the run's end.</param>
public abstract record RmsEvent(double AtSeconds)
{
    /// <summary>A short description for messages ("branch 3 trip at 0.1 s").</summary>
    public abstract string Describe();

    /// <summary>A number for a message, invariant culture, round-trip form.</summary>
    private protected static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A bolted three-phase fault to ground at bus <paramref name="BusId"/> (the same near short circuit the
/// single-fault study uses, <see cref="SubtransientInit.BoltedFaultShuntPu"/>), in place until a
/// <see cref="RmsFaultClear"/> on the same bus.</summary>
public sealed record RmsBusFault(double AtSeconds, int BusId) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"bolted fault at bus {BusId} at {Num(AtSeconds)} s";
}

/// <summary>The clearance of the bolted fault at bus <paramref name="BusId"/> (the fault's shunt is removed; the
/// network is otherwise as it then stands). Clearing by tripping the faulted branch is a clearance plus an
/// <see cref="RmsBranchTrip"/> at the same time.</summary>
public sealed record RmsFaultClear(double AtSeconds, int BusId) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"fault clearance at bus {BusId} at {Num(AtSeconds)} s";
}

/// <summary>Branch <paramref name="BranchIndex"/> (its index in <c>model.Branches</c>) opens at both ends and stays
/// out: its series admittance, line charging and tap leave the network.</summary>
public sealed record RmsBranchTrip(double AtSeconds, int BranchIndex) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"branch {BranchIndex} trip at {Num(AtSeconds)} s";
}

/// <summary>
/// A load step at bus <paramref name="BusId"/>: <paramref name="DeltaPMw"/> + j<paramref name="DeltaQMvar"/> more (or,
/// negative, less) load. The RMS network carries load as constant admittance converted at the solved voltage
/// (<see cref="SubtransientInit"/>), so the step is the admittance <c>conj(dS / Sbase) / |V0|^2</c> with V0 the bus's
/// solved voltage: it draws exactly dS at the pre-event voltage and scales with |V|^2 after, as the base loads do (the
/// PSS/E and PowerFactory constant-impedance load convention). Steps on one bus add.
/// </summary>
public sealed record RmsLoadStep(double AtSeconds, int BusId, double DeltaPMw, double DeltaQMvar) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"load step of {Num(DeltaPMw)} MW + j{Num(DeltaQMvar)} Mvar at bus {BusId} at {Num(AtSeconds)} s";
}

/// <summary>N351 (CF5): branch <paramref name="BranchIndex"/> (its index in <c>model.Branches</c>), tripped by an earlier
/// <see cref="RmsBranchTrip"/>, returns to service (auto-reclose). A reclose of a branch that is not tripped at that time
/// is refused. After the last reclose the network is the base network's own cells again.</summary>
public sealed record RmsBranchReclose(double AtSeconds, int BranchIndex) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"branch {BranchIndex} reclose at {Num(AtSeconds)} s";
}

/// <summary>Generator <paramref name="GeneratorIndex"/> (its index in <c>model.Generators</c>) is disconnected: its
/// Norton admittance leaves the network, it carries no current, its mechanical power is zero, its field is held and it
/// is no longer judged for synchronism (see <see cref="RmsMachineTrip"/>). Only a unit integrated as a synchronous
/// machine can be tripped (converter plant and held units are not machines of the RMS fleet).</summary>
public sealed record RmsGeneratorTrip(double AtSeconds, int GeneratorIndex) : RmsEvent(AtSeconds)
{
    /// <inheritdoc/>
    public override string Describe() => $"generator {GeneratorIndex} trip at {Num(AtSeconds)} s";
}

public static partial class RmsTransientStability
{
    /// <summary>
    /// R061 rest (CF4, October 2026; Ruling R1): an RMS study driven by a list of scheduled events (bolted bus faults and
    /// their clearances, branch trips, load steps, generator trips), on the same machines, standard control library and
    /// sparse network as <see cref="Study"/>: optional per-machine exciters (<paramref name="exciters"/>, SEXS, IEEET1, EXAC1, ESAC1A or ESST4B
    /// with an optional PSS1A, rules as in <see cref="Study"/>) and the governors in the machine data
    /// (<see cref="SubtransientInit.MachineData.Governor"/>). Returns the per-machine traces and the synchronism verdict
    /// over the machines still connected.
    /// <para>
    /// <b>How the events become network stages.</b> The events are taken in time order (input order for equal times).
    /// At each distinct event time a network is built from the base network's own stamps
    /// (<see cref="SubtransientInit.Prepared.Basis"/>): the Y-bus of the branches still in service (rebuilt by
    /// <see cref="YBus.BuildSparse"/> once any branch is out, otherwise the base cells as they are), the base load,
    /// held-unit and converter stamps, the Norton stamp of every machine still connected, the accumulated load-step
    /// admittances and a bolted-fault shunt on every bus faulted at that time. The networks are all built (and factored)
    /// before the run starts, so a stage that cannot be solved (for example a branch trip that leaves a bus with no
    /// path to ground) fails before any integration. A generator trip is also handed to the kernel
    /// (<see cref="RmsMachineTrip"/>), which zeroes that machine's mechanical power and leaves it out of the synchronism
    /// verdict.
    /// </para>
    /// <para>
    /// <b>Fail loud.</b> Refused, naming the event: an empty list or a null entry; a time that is not finite, not after
    /// t = 0 or after the run's end; a bus that is not in the model; a branch index out of range, a branch already out of
    /// service, or tripped twice; a fault on a bus already faulted, or a clearance with no fault in place at that bus; a
    /// load step that is not finite or is zero in both parts; a generator index out of range, a unit that is not an
    /// integrated synchronous machine, a machine tripped twice, or a trip of every machine. A step or duration that is
    /// not positive and finite is refused too.
    /// </para>
    /// <para>
    /// The existing <see cref="Study"/> (one bolted fault, cleared) is unchanged; this is a separate entry point, so no
    /// existing number moves.
    /// </para>
    /// </summary>
    public static MultiMachineSubtransient.Result StudyEvents(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
        => StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds, slipLimitRad, exciters, null);

    /// <summary>The body of <see cref="StudyEvents"/> (moved here unchanged, orchestration only, so every number is the
    /// same bits), with an optional step observer for <see cref="StudyEventsMeasured"/>: given the prepared fleet, it
    /// returns the read-only observer handed to the kernel (the step observer of <see cref="MultiMachineSubtransient"/>).
    /// Null: no observer, exactly <see cref="StudyEvents"/>.</summary>
    private static MultiMachineSubtransient.Result StudyEventsCore(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, double? f0, double stepSeconds, double durationSeconds, double slipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters,
        Func<SubtransientInit.Prepared, Action<double, IRmsNetwork, Complex[], Complex[]>>? observerFor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "The RMS step must be positive and finite.");
        if (!(durationSeconds > 0.0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds,
                "The RMS event study's duration must be positive and finite.");
        if (!(slipLimitRad > 0.0))
            throw new ArgumentOutOfRangeException(nameof(slipLimitRad), slipLimitRad, "The slip limit must be positive.");
        if (events.Count == 0)
            throw new ArgumentException("The RMS event study needs at least one event (use Study for a single "
                + "fault-and-clear, or give the disturbances to schedule).", nameof(events));

        double nominalHz = ResolveNominalHz(model, f0);
        result.RequireConverged("RMS event study");   // the convergence contract (October 2026, WS4)

        // The event times first (before any preparation work): finite, strictly after t = 0 by more than the stage
        // tolerance (so the t = 0 state is the solved one and automatic AVR references are measured undisturbed), and
        // inside the run.
        double stageEps = stepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps;
        for (int i = 0; i < events.Count; i++)
        {
            RmsEvent? ev = events[i];
            if (ev is null) throw new ArgumentException($"events[{i}] is null.", nameof(events));
            if (!double.IsFinite(ev.AtSeconds) || !(ev.AtSeconds > stageEps))
                throw new ArgumentOutOfRangeException(nameof(events), ev.AtSeconds,
                    $"events[{i}] ({ev.Describe()}): the time must be finite and after t = 0 (the run starts from the "
                    + "solved, undisturbed state).");
            if (ev.AtSeconds > durationSeconds + stageEps)
                throw new ArgumentOutOfRangeException(nameof(events), ev.AtSeconds,
                    $"events[{i}] ({ev.Describe()}) is after the end of the run ({durationSeconds} s); it would never happen.");
        }

        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, result, data);   // N271: sparse
        SubtransientInit.SparseNetworkBasis basis = p.Basis
            ?? throw new InvalidOperationException("The RMS event study needs the sparse network basis; the case was "
                + "prepared on the dense test oracle, which has none.");
        IRmsFieldController?[]? controllers = MapExcitersToFleet(model, data, p, exciters);
        GovernorPlan? governors = MapGovernorsToFleet(model, data, p);   // R061: null when no machine has a governor
        int[] slotOfGenerator = MachineSlots(model, data, p);
        int connectedMachines = p.Machines.Count - (p.IslandReferences?.Count ?? 0);

        // Walk the events in time order (OrderBy is stable: equal times keep their input order), keeping the
        // cumulative state, and build one network stage per distinct time.
        var ordered = events.Select((ev, index) => (Event: ev, Index: index)).OrderBy(x => x.Event.AtSeconds).ToList();
        var trippedBranches = new List<int>();
        var faultedBuses = new List<int>();
        var loadBuses = new List<int>();                          // buses with a load step, in first-step order
        var loadDelta = new Dictionary<int, (double P, double Q)>();
        var machineOut = new bool[p.Machines.Count];
        int machinesTripped = 0;
        var trips = new List<RmsMachineTrip>();
        var stages = new List<RmsNetworkStage>();
        var busIds = new HashSet<int>(model.Buses.Select(b => b.Id));

        for (int k = 0; k < ordered.Count; k++)
        {
            (RmsEvent ev, int i) = ordered[k];
            string what = $"events[{i}] ({ev.Describe()})";
            switch (ev)
            {
                case RmsBusFault f:
                    RequireBus(busIds, f.BusId, what, nameof(events));
                    if (faultedBuses.Contains(f.BusId))
                        throw new ArgumentException($"{what}: bus {f.BusId} is already faulted at that time.", nameof(events));
                    faultedBuses.Add(f.BusId);
                    break;
                case RmsFaultClear c:
                    RequireBus(busIds, c.BusId, what, nameof(events));
                    if (!faultedBuses.Remove(c.BusId))
                        throw new ArgumentException($"{what}: there is no fault in place at bus {c.BusId} to clear at that "
                            + "time (a clearance must follow its fault).", nameof(events));
                    break;
                case RmsBranchTrip b:
                    if (b.BranchIndex < 0 || b.BranchIndex >= model.Branches.Count)
                        throw new ArgumentOutOfRangeException(nameof(events), b.BranchIndex,
                            $"{what}: the model has {model.Branches.Count} branches (0 to {model.Branches.Count - 1}).");
                    if (!model.Branches[b.BranchIndex].InService)
                        throw new ArgumentException($"{what}: the branch is already out of service in the model.", nameof(events));
                    if (trippedBranches.Contains(b.BranchIndex))
                        throw new ArgumentException($"{what}: the branch is already tripped by an earlier event.", nameof(events));
                    trippedBranches.Add(b.BranchIndex);
                    break;
                case RmsBranchReclose rc:
                    // N351 (CF5): auto-reclose. Only a branch an earlier event tripped can close again (a branch out of
                    // service in the model is not this study's to close). Stage building reads the tripped set as a set,
                    // so the order the list is left in changes nothing.
                    if (rc.BranchIndex < 0 || rc.BranchIndex >= model.Branches.Count)
                        throw new ArgumentOutOfRangeException(nameof(events), rc.BranchIndex,
                            $"{what}: the model has {model.Branches.Count} branches (0 to {model.Branches.Count - 1}).");
                    if (!trippedBranches.Remove(rc.BranchIndex))
                        throw new ArgumentException($"{what}: the branch is not tripped at that time (a reclose must follow its trip).", nameof(events));
                    break;
                case RmsLoadStep l:
                    RequireBus(busIds, l.BusId, what, nameof(events));
                    if (!double.IsFinite(l.DeltaPMw) || !double.IsFinite(l.DeltaQMvar))
                        throw new ArgumentOutOfRangeException(nameof(events), $"{what}: the load step must be finite.");
                    if (l.DeltaPMw == 0.0 && l.DeltaQMvar == 0.0)
                        throw new ArgumentException($"{what}: a load step of zero in both P and Q changes nothing.", nameof(events));
                    if (!loadDelta.TryGetValue(l.BusId, out (double P, double Q) acc)) { loadBuses.Add(l.BusId); acc = (0.0, 0.0); }
                    loadDelta[l.BusId] = (acc.P + l.DeltaPMw, acc.Q + l.DeltaQMvar);
                    break;
                case RmsGeneratorTrip g:
                    if (g.GeneratorIndex < 0 || g.GeneratorIndex >= model.Generators.Count)
                        throw new ArgumentOutOfRangeException(nameof(events), g.GeneratorIndex,
                            $"{what}: the model has {model.Generators.Count} in-service generators (0 to {model.Generators.Count - 1}).");
                    int slot = slotOfGenerator[g.GeneratorIndex];
                    if (slot < 0)
                        throw new ArgumentException($"{what}: generator {g.GeneratorIndex} at bus {model.Generators[g.GeneratorIndex].Bus} "
                            + "is not integrated as a synchronous machine (converter plant or a held unit), so it cannot be "
                            + "tripped as a machine.", nameof(events));
                    if (machineOut[slot])
                        throw new ArgumentException($"{what}: the generator is already tripped by an earlier event.", nameof(events));
                    machineOut[slot] = true;
                    machinesTripped++;
                    if (machinesTripped >= connectedMachines)
                        throw new ArgumentException($"{what}: it would trip the last connected machine; at least one "
                            + "machine must stay connected.", nameof(events));
                    trips.Add(new RmsMachineTrip(slot, g.AtSeconds));
                    break;
                default:
                    throw new ArgumentException($"{what}: unknown event kind {ev.GetType().Name}.", nameof(events));
            }

            // One stage per distinct time: build it after the last event at this time.
            bool lastAtThisTime = k + 1 == ordered.Count || ordered[k + 1].Event.AtSeconds != ev.AtSeconds;
            if (lastAtThisTime)
                stages.Add(new RmsNetworkStage(ev.AtSeconds,
                    BuildEventStage(model, basis, trippedBranches, faultedBuses, loadBuses, loadDelta, machineOut)));
        }

        // R061 governors: fresh governors for this run, or no hook at all (the constant-Pm path).
        Func<int, double, double>? mechanicalPower = governors is null ? null : GovernorHook(p, governors, stepSeconds);
        return MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
            nominalHz, stepSeconds, durationSeconds, stages, slipLimitRad, converters: p.Converters,
            synchronismMachines: connectedMachines,
            fieldControllers: controllers,
            mechanicalPower: mechanicalPower,
            machineTrips: trips.Count > 0 ? trips : null,
            stepObserver: observerFor?.Invoke(p));
    }

    private static void RequireBus(HashSet<int> busIds, int busId, string what, string paramName)
    {
        if (!busIds.Contains(busId))
            throw new ArgumentException($"{what}: bus {busId} is not in the model.", paramName);
    }

    /// <summary>R061 rest (CF4): each generator's machine slot in the prepared fleet, or -1 for a unit that is not an
    /// integrated machine. The classification is the one <see cref="MapExcitersToFleet"/> uses, cross-checked against
    /// the prepared fleet's terminal buses, so a drift between the two fails loud.</summary>
    private static int[] MachineSlots(GridModel model, IReadOnlyList<SubtransientInit.MachineData> data, SubtransientInit.Prepared p)
    {
        var slotOf = new int[model.Generators.Count];
        int slot = 0;
        for (int k = 0; k < model.Generators.Count; k++)
        {
            Generator gen = model.Generators[k];
            bool conv = SubtransientInit.IsConverterUnit(gen, data[k]);   // WECC: a unit with a WECC chain is converter plant
            bool held = gen.PowerFlowOnly || (!conv && !(data[k].H > 0.0));
            bool isMachine = !held && !conv;
            if (!isMachine) { slotOf[k] = -1; continue; }
            if (slot >= p.Machines.Count || p.GeneratorBuses[slot] != gen.Bus)
                throw new InvalidOperationException($"Machine slot mapping out of step with the prepared fleet at generator "
                    + $"{k} (bus {gen.Bus}); the machine classification here no longer matches SubtransientInit.");
            slotOf[k] = slot++;
        }
        return slotOf;
    }

    /// <summary>R061 rest (CF4): the network of one event stage, from the base network's stamps and the cumulative
    /// state (see <see cref="StudyEvents"/>). Every list is walked in a fixed order, so the same events always give the
    /// same triplets in the same order, and so the same factorisation bits.</summary>
    private static SparseRmsNetwork BuildEventStage(
        GridModel model, SubtransientInit.SparseNetworkBasis basis, List<int> trippedBranches, List<int> faultedBuses,
        List<int> loadBuses, Dictionary<int, (double P, double Q)> loadDelta, bool[] machineOut)
    {
        var triplets = new List<(int Row, int Col, Complex V)>(basis.BranchCells.Count + basis.OtherStamps.Count + 8);

        // 1. The branch network: the base cells while every branch is in, else the Y-bus rebuilt without the tripped
        //    branches (marked out of service, which YBus skips; every other branch, tap and bus shunt as in the model).
        if (trippedBranches.Count == 0)
        {
            triplets.AddRange(basis.BranchCells);
        }
        else
        {
            var tripped = new HashSet<int>(trippedBranches);
            var branches = new List<Branch>(model.Branches.Count);
            for (int j = 0; j < model.Branches.Count; j++)
                branches.Add(tripped.Contains(j) ? model.Branches[j] with { InService = false } : model.Branches[j]);
            foreach (KeyValuePair<(int Row, int Col), Complex> cell in YBus.BuildSparse(model.CopyWith(branches: branches)).Cells)
                triplets.Add((cell.Key.Row, cell.Key.Col, cell.Value));
        }

        // 2. The base load, held-unit and converter stamps, and the Norton stamp of every machine still connected.
        var skip = new HashSet<int>();
        for (int m = 0; m < machineOut.Length && m < basis.MachineStampIndex.Count; m++)
            if (machineOut[m]) skip.Add(basis.MachineStampIndex[m]);
        for (int s = 0; s < basis.OtherStamps.Count; s++)
            if (!skip.Contains(s)) triplets.Add(basis.OtherStamps[s]);

        // 3. The load steps, one admittance per bus (the accumulated step), at the bus's solved voltage.
        foreach (int busId in loadBuses)
        {
            (double dp, double dq) = loadDelta[busId];
            if (dp == 0.0 && dq == 0.0) continue;   // steps that cancelled exactly add nothing
            int bi = basis.BusIndex[busId];
            Complex v = basis.InitialVoltageByBus.TryGetValue(busId, out Complex vv) ? vv : Complex.One;
            double v2 = Math.Max(SubtransientInit.MinVoltageSquaredPu, v.Magnitude * v.Magnitude);
            triplets.Add((bi, bi, Complex.Conjugate(new Complex(dp, dq) / model.BaseMva) / v2));
        }

        // 4. A bolted-fault shunt on every bus faulted at this time.
        foreach (int busId in faultedBuses)
        {
            int bi = basis.BusIndex[busId];
            triplets.Add((bi, bi, new Complex(SubtransientInit.BoltedFaultShuntPu, 0.0)));
        }

        // A tripped machine has no admittance in the network's machine list either, so it carries no current.
        var yGen = basis.MachineAdmittance.ToArray();
        for (int m = 0; m < machineOut.Length && m < yGen.Length; m++) if (machineOut[m]) yGen[m] = Complex.Zero;
        return new SparseRmsNetwork(basis.BusCount, triplets, basis.MachineTerminal.ToArray(), yGen,
            basis.ConverterBusIndex.ToArray());
    }
}
