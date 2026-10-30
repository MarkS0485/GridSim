using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

public static partial class RmsTransientStability
{
    /// <summary>
    /// An RMS event study (<see cref="StudyEvents"/>) together with the voltage angle of the buses it measures and the
    /// fleet facts a centre-of-inertia frequency needs (inertia placement study, October 2026).
    /// </summary>
    /// <param name="Result">The run, exactly as <see cref="StudyEvents"/> returns it for the same inputs.</param>
    /// <param name="Buses">The measured buses' voltage angles, one sample per step.</param>
    /// <param name="H">The system-base inertia of each integrated machine, in trace order.</param>
    /// <param name="MachineBuses">Each integrated machine's terminal bus id, in trace order.</param>
    /// <param name="NominalHz">The nominal frequency the run was integrated at.</param>
    /// <param name="RealMachines">How many leading traces are real machines: the island reference sources the RMS
    /// preparation adds after them (stiff synthetic sources, H = <see cref="SubtransientInit.IslandReferenceH"/>, for an
    /// island with no machine of its own) are not, and must be left out of any centre of inertia.</param>
    public sealed record MeasuredEventStudy(
        MultiMachineSubtransient.Result Result, RmsBusTrace Buses, IReadOnlyList<double> H,
        IReadOnlyList<int> MachineBuses, double NominalHz, int RealMachines);

    /// <summary>
    /// <see cref="StudyEvents"/> with the voltage of every bus in <paramref name="measuredBusIds"/> recorded at each step.
    /// The bus voltages are a second solve of the same factorised network the step was committed against, with the same
    /// machine EMFs and converter currents (<see cref="SparseRmsNetwork.SolveBusVoltagesInto"/>); it is read after the
    /// step's own solve and feeds nothing back, so every machine and converter number of the run is the same bits as
    /// <see cref="StudyEvents"/> gives (pinned by a test). A bus id that is not in the model is refused.
    /// </summary>
    public static MeasuredEventStudy StudyEventsMeasured(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, IReadOnlyList<int> measuredBusIds,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(measuredBusIds);
        if (measuredBusIds.Count == 0)
            throw new ArgumentException("Name at least one bus to measure.", nameof(measuredBusIds));
        var known = new HashSet<int>();
        foreach (Bus b in model.Buses) known.Add(b.Id);
        var seen = new HashSet<int>();
        foreach (int id in measuredBusIds)
        {
            if (!known.Contains(id)) throw new ArgumentException($"Measured bus {id} is not in the model.", nameof(measuredBusIds));
            if (!seen.Add(id)) throw new ArgumentException($"Measured bus {id} is named twice.", nameof(measuredBusIds));
        }

        int m = measuredBusIds.Count;
        var angles = new List<double>[m];
        for (int i = 0; i < m; i++) angles[i] = new List<double>();
        var times = new List<double>();
        SubtransientInit.Prepared? prepared = null;

        Action<double, IRmsNetwork, Complex[], Complex[]> Observer(SubtransientInit.Prepared p)
        {
            prepared = p;
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
                for (int i = 0; i < m; i++) angles[i].Add(BusFrequencyMeter.PhaseOf(v[index[i]]));
            };
        }

        MultiMachineSubtransient.Result run = StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds,
            slipLimitRad, exciters, Observer);
        SubtransientInit.Prepared pp = prepared
            ?? throw new InvalidOperationException("The RMS event study did not prepare a fleet.");

        var series = new double[m][];
        for (int i = 0; i < m; i++)
        {
            series[i] = angles[i].ToArray();
            BusFrequencyMeter.Unwrap(series[i]);
        }
        return new MeasuredEventStudy(run, new RmsBusTrace(measuredBusIds, times, series), pp.H, pp.GeneratorBuses,
            ResolveNominalHz(model, f0), pp.Machines.Count - (pp.IslandReferences?.Count ?? 0));
    }
}
