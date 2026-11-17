using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

public static partial class RmsTransientStability
{
    /// <summary>
    /// RC3 P6 (dynamics study): the bus voltages an observed RMS run recorded, one sample per integration step.
    /// </summary>
    /// <param name="BusIds">The measured buses, in request order.</param>
    /// <param name="TimeSeconds">The sample times (s), one per step, the same grid as the machine traces.</param>
    /// <param name="MagnitudePu">Per bus, the voltage magnitude (pu) at each sample.</param>
    /// <param name="AngleRad">Per bus, the voltage angle (rad, unwrapped, the engine's synchronous frame) at each sample.</param>
    public sealed record ObservedBusVoltages(IReadOnlyList<int> BusIds, IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double[]> MagnitudePu, IReadOnlyList<double[]> AngleRad);

    /// <summary>
    /// RC3 P6 (dynamics study): an RMS run with its measured bus voltages and the prepared fleet it integrated (the initial
    /// phasors, mechanical powers and field voltages, read-only).
    /// </summary>
    /// <param name="Result">The run. With events it is exactly what <see cref="StudyEvents"/> returns for the same inputs.</param>
    /// <param name="Buses">The measured bus voltages.</param>
    /// <param name="Prepared">The fleet <see cref="SubtransientInit.FromSolvedCase"/> prepared for this run.</param>
    public sealed record ObservedStudy(MultiMachineSubtransient.Result Result, ObservedBusVoltages Buses,
        SubtransientInit.Prepared Prepared);

    /// <summary>
    /// RC3 P6 (dynamics study): an RMS run that records the voltage magnitude and angle of every bus in
    /// <paramref name="measuredBusIds"/> at each step and hands back the prepared fleet. Additive orchestration only:
    /// <list type="bullet">
    /// <item>With one or more events it runs the body of <see cref="StudyEvents"/> with a read-only step observer (the
    /// same mechanism as <see cref="StudyEventsMeasured"/>), so every machine and converter number is the same bits as
    /// <see cref="StudyEvents"/> gives.</item>
    /// <item>With no events it runs the same preparation, controller mapping and governor hook as
    /// <see cref="StudyEvents"/> on the base network alone, for the whole duration: the no-event (flat) interval an
    /// initialisation check needs. <see cref="StudyEvents"/> refuses an empty list, so this is a separate path; before the
    /// first event of an event study the two give the same bits (pinned by a test).</item>
    /// </list>
    /// The bus voltages are a second solve of the network the step was committed against, with the step's EMFs and
    /// converter currents; they feed nothing back. An empty <paramref name="measuredBusIds"/> records no bus.
    /// </summary>
    public static ObservedStudy StudyEventsObserved(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, IReadOnlyList<int> measuredBusIds,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(measuredBusIds);
        var known = new HashSet<int>();
        foreach (Bus b in model.Buses) known.Add(b.Id);
        var seen = new HashSet<int>();
        foreach (int id in measuredBusIds)
        {
            if (!known.Contains(id)) throw new ArgumentException($"Measured bus {id} is not in the model.", nameof(measuredBusIds));
            if (!seen.Add(id)) throw new ArgumentException($"Measured bus {id} is named twice.", nameof(measuredBusIds));
        }

        int m = measuredBusIds.Count;
        var mags = new List<double>[m];
        var angles = new List<double>[m];
        for (int i = 0; i < m; i++) { mags[i] = new List<double>(); angles[i] = new List<double>(); }
        var times = new List<double>();
        SubtransientInit.Prepared? prepared = null;

        Action<double, IRmsNetwork, Complex[], Complex[]> Observer(SubtransientInit.Prepared p)
        {
            prepared = p;
            if (m == 0) return (_, _, _, _) => { };
            SubtransientInit.SparseNetworkBasis basis = p.Basis
                ?? throw new InvalidOperationException("Bus measurement needs the sparse network basis.");
            var index = new int[m];
            for (int i = 0; i < m; i++) index[i] = basis.BusIndex[measuredBusIds[i]];
            var v = new Complex[basis.BusCount];
            return (t, net, e, ic) =>
            {
                if (net is not SparseRmsNetwork sparse)
                    throw new InvalidOperationException("Bus measurement needs the sparse RMS network at every stage.");
                sparse.SolveBusVoltagesInto(e, ic, v);
                times.Add(t);
                for (int i = 0; i < m; i++)
                {
                    Complex vi = v[index[i]];
                    mags[i].Add(Math.Sqrt(vi.Real * vi.Real + vi.Imaginary * vi.Imaginary));
                    angles[i].Add(DetMath.Phase(vi));
                }
            };
        }

        MultiMachineSubtransient.Result run = events.Count > 0
            ? StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds, slipLimitRad, exciters, Observer)
            : UndisturbedCore(model, result, data, f0, stepSeconds, durationSeconds, slipLimitRad, exciters, Observer);
        SubtransientInit.Prepared pp = prepared
            ?? throw new InvalidOperationException("The observed RMS study did not prepare a fleet.");

        var magSeries = new double[m][];
        var angSeries = new double[m][];
        for (int i = 0; i < m; i++)
        {
            magSeries[i] = mags[i].ToArray();
            angSeries[i] = angles[i].ToArray();
            BusFrequencyMeter.Unwrap(angSeries[i]);
        }
        return new ObservedStudy(run, new ObservedBusVoltages(measuredBusIds, times, magSeries, angSeries), pp);
    }

    /// <summary>The no-event run of <see cref="StudyEventsObserved"/>: the preparation, controller and governor mapping of
    /// <see cref="StudyEventsCore"/>, with no network stage (the base network throughout) and no machine trip.</summary>
    private static MultiMachineSubtransient.Result UndisturbedCore(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        double? f0, double stepSeconds, double durationSeconds, double slipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters,
        Func<SubtransientInit.Prepared, Action<double, IRmsNetwork, Complex[], Complex[]>> observerFor)
    {
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds))
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "The RMS step must be positive and finite.");
        if (!(durationSeconds > 0.0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds,
                "The RMS no-event run's duration must be positive and finite.");
        if (!(slipLimitRad > 0.0))
            throw new ArgumentOutOfRangeException(nameof(slipLimitRad), slipLimitRad, "The slip limit must be positive.");

        double nominalHz = ResolveNominalHz(model, f0);
        result.RequireConverged("RMS no-event run");

        SubtransientInit.Prepared p = SubtransientInit.FromSolvedCase(model, result, data);
        if (p.Basis is null)
            throw new InvalidOperationException("The RMS no-event run needs the sparse network basis; the case was "
                + "prepared on the dense test oracle, which has none.");
        IRmsFieldController?[]? controllers = MapExcitersToFleet(model, data, p, exciters);
        GovernorPlan? governors = MapGovernorsToFleet(model, data, p);
        int connectedMachines = p.Machines.Count - (p.IslandReferences?.Count ?? 0);
        Func<int, double, double>? mechanicalPower = governors is null ? null : GovernorHook(p, governors, stepSeconds);
        return MultiMachineSubtransient.Simulate(
            p.Machines, p.H, p.Damping, p.Pm, p.Efd, p.Delta0, p.Eqp0, p.Edp0, p.Eqpp0, p.Edpp0, p.Network!,
            nominalHz, stepSeconds, durationSeconds, new List<RmsNetworkStage>(), slipLimitRad, converters: p.Converters,
            synchronismMachines: connectedMachines,
            fieldControllers: controllers,
            mechanicalPower: mechanicalPower,
            machineTrips: null,
            stepObserver: observerFor(p));
    }
}
