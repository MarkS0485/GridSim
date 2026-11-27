using System.Globalization;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC4 R4-14: the opt-in variable-step companion of the dynamics study. When present on a request the study additionally runs
/// every row through the adaptive swing kernel and reports the result BESIDE the fixed-step rows, which are unchanged and
/// still decide the study status. Absent (null), every number and every byte of the result is the incumbent's.
/// </summary>
/// <param name="Options">The step controller's tolerances and limits and the event location tolerance.</param>
/// <param name="CctToleranceSeconds">The width the adaptive critical clearing time bracket is bisected to (s).</param>
/// <param name="Devices">A placed device set (R4-07) whose overcurrent relays and fuses act in the loop, or null.</param>
/// <param name="Curves">The curve library the devices' curve ids resolve against (null: the built-in families only).</param>
public sealed record DynamicsAdaptiveRequest(AdaptiveOptions Options, double CctToleranceSeconds = 1e-4,
    DeviceSet? Devices = null, CurveLibrary? Curves = null)
{
    /// <summary>
    /// RC5 stage 3 (opt-in): the units' own relays to put in the loop of every event row, or null (every existing request:
    /// no unit relay is evaluated and the evidence is byte-identical). The units are named as the RAW case's generators
    /// are (the model's generator names); an unknown or shared name is an input error before any work. Critical clearing
    /// time rows do not run unit relays (the bracket is a property of the network and the fault, stated in the rule).
    /// </summary>
    public GridSim.Core.Protection.UnitProtectionSet? UnitProtection { get; init; }
}

/// <summary>One scheduled or relay event beside what the fixed-step engine would have done with it.</summary>
/// <param name="Event">What happened.</param>
/// <param name="Source">scheduled or relay.</param>
/// <param name="AdaptiveSeconds">The time the adaptive run applied it at.</param>
/// <param name="FixedStepAppliedSeconds">The time the fixed-step engine applies an event at that time (the first step boundary at or after it).</param>
/// <param name="QuantisationSeconds">Fixed-step applied minus adaptive: the step quantisation the adaptive run removes.</param>
public sealed record DynamicsAdaptiveEventComparison(string Event, string Source, double AdaptiveSeconds, double FixedStepAppliedSeconds,
    double QuantisationSeconds);

/// <summary>One batch row of the adaptive companion run (same index and name as the fixed-step row).</summary>
public sealed record DynamicsAdaptiveRow(int Index, string Name, DynamicsCaseKind Kind, string Disposition, string? Reason,
    int AcceptedSteps, int RejectedSteps, long Evaluations, double MinStepSeconds, double MaxStepSeconds, int EventBisections,
    bool StayedInSynchronism, double MaxAngleSpreadDeg, IReadOnlyList<AdaptiveAppliedEvent>? Events,
    IReadOnlyList<DynamicsAdaptiveEventComparison>? Quantisation, IReadOnlyList<AdaptiveRelayRecord>? Relays,
    IReadOnlyList<double>? TimeSeconds, IReadOnlyList<DynamicsChannelTrace>? Channels, AdaptiveCct.Result? Cct)
{
    /// <summary>RC5 stage 3: each unit relay stage's behaviour in this row, or null when unit relays were not asked for.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AdaptiveUnitRelayRecord>? UnitRelays { get; init; }

    /// <summary>RC5 stage 3: why the row's run stopped early (a unit trip would have left no machine), or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }
}

/// <summary>The adaptive companion run: declarations first, then the rows.</summary>
public sealed record DynamicsAdaptiveEvidence(string Integration, string Scheme, string Model, AdaptiveOptions Options,
    double CctToleranceSeconds, string EventRule, string RelayRule, string StatusRule, IReadOnlyList<RelayNotEvaluated> RelaysNotEvaluated,
    IReadOnlyList<DynamicsAdaptiveRow> Rows)
{
    /// <summary>RC5 stage 3: how unit relays act in the loop, or null when they were not asked for.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UnitRelayRule { get; init; }

    /// <summary>RC5 stage 3: the unit relay stages not evaluated, with reasons, or null when unit relays were not asked for.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnitRelayNotEvaluated>? UnitRelaysNotEvaluated { get; init; }
}

/// <summary>RC4 R4-14: runs the rows of a <see cref="DynamicsAdaptiveRequest"/>.</summary>
internal static class DynamicsAdaptiveRunner
{
    public static DynamicsAdaptiveEvidence Run(GridModel model, PowerFlowResult pf, IReadOnlyList<Dynamics.Models.SubtransientInit.MachineData> data,
        DynamicsStudyRequest request, IReadOnlyList<RmsEvent>?[] events, IReadOnlyList<(int Branch, string Label)>?[] trips,
        IReadOnlyList<(DynamicsChannelRequest Channel, int? Slot, string Label)> channels, int[] slotOfGenerator, int refSlot,
        double nominalHz, BoundDeviceSet? devices, CancellationToken cancellation)
    {
        DynamicsAdaptiveRequest a = request.Adaptive!;
        int n = request.Cases.Count;
        var rows = new DynamicsAdaptiveRow?[n];
        // RC5 stage 3: the model with the units' own relays (named check done by the runner before any work).
        if (a.UnitProtection is { } up) model = model.WithUnitProtection(up);
        // Each row's list of unit relay stages not evaluated, by row index. The rows share one model, so today every row
        // reports the same list; taking the first non-empty one in ROW order (below, after the loop) rather than the first
        // row to finish keeps the evidence independent of thread timing should a reason ever depend on a row's events.
        var skippedByRow = new IReadOnlyList<UnitRelayNotEvaluated>?[n];
        void RunRow(int i)
        {
            DynamicsCaseRequest c = request.Cases[i];
            if (cancellation.IsCancellationRequested)
            {
                rows[i] = Failed(i, c, DynamicsDisposition.NotAttempted, "the run was cancelled before this row started");
                return;
            }
            IReadOnlyList<UnitRelayNotEvaluated>? sk = null;
            rows[i] = c.Kind == DynamicsCaseKind.Events
                ? EventRow(i, c, model, pf, data, request, events[i]!, channels, slotOfGenerator, refSlot, nominalHz, devices, out sk)
                : CctRow(i, c, model, pf, data, request, trips[i]!);
            skippedByRow[i] = sk;
        }
        if (request.MaxParallelism <= 1)
            for (int i = 0; i < n; i++) RunRow(i);
        else
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = request.MaxParallelism }, RunRow);
        IReadOnlyList<UnitRelayNotEvaluated> unitSkippedFirst = Array.Empty<UnitRelayNotEvaluated>();
        foreach (IReadOnlyList<UnitRelayNotEvaluated>? rowSkipped in skippedByRow)
            if (rowSkipped is { Count: > 0 }) { unitSkippedFirst = rowSkipped; break; }

        // RC5 stage 3: the rule and the stages not evaluated, only when unit relays were asked for.
        string? unitRule = null;
        IReadOnlyList<UnitRelayNotEvaluated>? unitSkipped = null;
        if (a.UnitProtection is not null)
        {
            unitRule = "each unit's own relay stages (its unitProtection settings) are evaluated at every state: 81U / 81O on the machine's electrical frequency F0 (1 + w), 81R on F0 dw/dt from the swing equation (no relay measurement window), 27 / 59 on the voltage magnitude at the unit's bus, or, behind a folded unit transformer (UnitTransformerXPu above 0), at the machine terminal V_hv + j X_t I on its generator side (each record's VoltageMeasuredAt says which), 32 on the machine's electrical power (not evaluated on a unit with no rating), 78 on the terminal voltage angle jump across a network change (instantaneous); a definite-time stage uses its delay while beyond pickup (dr/dt = 1/delay) and resets to 0 on dropout, a delay within the event tolerance trips on pickup; an operating stage disconnects its unit's machine and the run continues; a unit or scheduled trip that would leave no connected machine (t = 0 included) stops the run at that instant with the reason; at one instant scheduled events, then branch relays, then unit relays in unit and stage order; 40 is not evaluated (no field model in the swing); critical clearing time rows do not run unit relays";
            unitSkipped = unitSkippedFirst;
        }

        string hStep = request.StepSeconds.ToString("R", CultureInfo.InvariantCulture);
        return new DynamicsAdaptiveEvidence(
            Integration: AdaptiveSwingResult.AdaptiveLabel,
            Scheme: AdaptiveStepController.Scheme,
            Model: RmsTransientStability.AdaptiveModelDeclaration,
            Options: a.Options,
            CctToleranceSeconds: a.CctToleranceSeconds,
            EventRule: "scheduled events are applied at exactly their requested times (the step is cut to land on them; no quantisation); simultaneous scheduled events in input order; relay events (pickup, dropout, trip) are located by bisection to the event tolerance and applied at the high end of the bracket; at one instant scheduled events come before relay events, relay events by device number with pickup before trip",
            RelayRule: "each device carries the fraction r of its operate time used; dr/dt = 1/t_op(I) while picked up (t_op from the placed device's curve at the simulated primary current), reset to 0 on dropout; a device whose operate time is within the event tolerance trips on pickup; a trip opens the protected branch (branch-open event); first shot only, no reclosing in the loop",
            StatusRule: $"the fixed-step rows (step {hStep} s) are unchanged and decide the study status; the adaptive rows are additional evidence from a reduced model and never replace them",
            RelaysNotEvaluated: devices is null ? Array.Empty<RelayNotEvaluated>() : new RelayInLoop(devices, a.Options.EventTimeToleranceSeconds).NotEvaluated,
            Rows: rows.Select(r => r!).ToList())
        {
            UnitRelayRule = unitRule,
            UnitRelaysNotEvaluated = unitSkipped,
        };
    }

    private static DynamicsAdaptiveRow Failed(int index, DynamicsCaseRequest c, string disposition, string reason) =>
        new(index, c.Name, c.Kind, disposition, reason, 0, 0, 0, 0.0, 0.0, 0, false, 0.0, null, null, null, null, null, null);

    private static DynamicsAdaptiveRow EventRow(int index, DynamicsCaseRequest c, GridModel model, PowerFlowResult pf,
        IReadOnlyList<Dynamics.Models.SubtransientInit.MachineData> data, DynamicsStudyRequest request, IReadOnlyList<RmsEvent> events,
        IReadOnlyList<(DynamicsChannelRequest Channel, int? Slot, string Label)> channels, int[] slotOfGenerator, int refSlot,
        double nominalHz, BoundDeviceSet? devices, out IReadOnlyList<UnitRelayNotEvaluated>? unitSkipped)
    {
        unitSkipped = null;
        RmsTransientStability.AdaptiveStudy study;
        try
        {
            study = request.Adaptive!.UnitProtection is null
                ? RmsTransientStability.StudyEventsAdaptive(model, pf, data, events, request.Adaptive!.Options, devices, null,
                    request.DurationSeconds, request.Criteria.SlipLimitRad)
                : RmsTransientStability.StudyEventsAdaptiveWithUnitRelays(model, pf, data, events, request.Adaptive.Options, devices, null,
                    request.DurationSeconds, request.Criteria.SlipLimitRad);
            unitSkipped = study.UnitRelaysNotEvaluated;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failed(index, c, DynamicsDisposition.Failed, "the adaptive engine refused the row: " + ex.Message);
        }
        AdaptiveSwingResult run = study.Run;
        var tripped = new HashSet<int>(events.OfType<RmsGeneratorTrip>().Select(g => slotOfGenerator[g.GeneratorIndex]));
        // RC5 stage 3: a unit its own relay disconnected leaves the spread too (none when unit relays are off).
        if (run.UnitRelays is { } urs)
        {
            var slotByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int g = 0; g < model.Generators.Count; g++)
                if (model.Generators[g].Name is { } gn && slotOfGenerator[g] >= 0) slotByName.TryAdd(gn, slotOfGenerator[g]);
            foreach (AdaptiveUnitRelayRecord ur in urs)
                if (ur.Tripped && slotByName.TryGetValue(ur.Unit, out int us)) tripped.Add(us);
        }
        int real = study.Prepared.Machines.Count - (study.Prepared.IslandReferences?.Count ?? 0);
        double spread = 0.0;
        for (int k = 0; k < run.TimeSeconds.Count; k++)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int s = 0; s < real; s++)
            {
                if (tripped.Contains(s)) continue;
                double d = run.DeltaRad[s][k];
                if (!double.IsFinite(d)) { lo = double.NegativeInfinity; hi = double.PositiveInfinity; break; }
                lo = Math.Min(lo, d); hi = Math.Max(hi, d);
            }
            spread = Math.Max(spread, (hi - lo) * 180.0 / Math.PI);
        }

        var list = new List<DynamicsChannelTrace>();
        foreach ((DynamicsChannelRequest ch, int? slot, string label) in channels)
        {
            switch (ch.Kind)
            {
                case DynamicsChannelKind.RotorAngle:
                {
                    var v = new double[run.TimeSeconds.Count];
                    for (int k = 0; k < v.Length; k++) v[k] = (run.DeltaRad[slot!.Value][k] - run.DeltaRad[refSlot][k]) * 180.0 / Math.PI;
                    list.Add(new DynamicsChannelTrace(label, "rotorAngle", "rotor angle relative to the reference machine", "deg", v));
                    break;
                }
                case DynamicsChannelKind.Speed:
                    list.Add(new DynamicsChannelTrace(label, "speed", "rotor speed deviation", "pu", run.SpeedPu[slot!.Value].ToArray()));
                    break;
                case DynamicsChannelKind.Frequency:
                {
                    var v = new double[run.TimeSeconds.Count];
                    for (int k = 0; k < v.Length; k++) v[k] = nominalHz * (1.0 + run.SpeedPu[slot!.Value][k]);
                    list.Add(new DynamicsChannelTrace(label, "frequency", "rotor electrical frequency", "Hz", v));
                    break;
                }
                default:
                    break;   // bus voltages are not recorded by the swing kernel (stated in the model declaration)
            }
        }

        double h = request.StepSeconds;
        var comparison = new List<DynamicsAdaptiveEventComparison>();
        foreach (AdaptiveAppliedEvent e in run.Events)
        {
            if (e.Kind is "pickup" or "dropout") continue;
            double fixedApplied = DynamicsStudyRunner.AppliedStep(e.AppliedSeconds, h) * h;
            comparison.Add(new DynamicsAdaptiveEventComparison(e.Description, e.Source, e.AppliedSeconds, fixedApplied, fixedApplied - e.AppliedSeconds));
        }

        string disposition; string? reason = null;
        if (!run.StayedInSynchronism)
        { disposition = DynamicsDisposition.Unstable; reason = $"loss of synchronism: a machine left the {request.Criteria.SlipLimitRad.ToString("R", CultureInfo.InvariantCulture)} rad centre-of-inertia band"; }
        else if (!(spread <= request.Criteria.MaxAngleSpreadDeg))
        { disposition = DynamicsDisposition.Unstable; reason = $"rotor angle spread {spread.ToString("R", CultureInfo.InvariantCulture)} deg exceeds {request.Criteria.MaxAngleSpreadDeg.ToString("R", CultureInfo.InvariantCulture)} deg"; }
        else disposition = DynamicsDisposition.Stable;
        return new DynamicsAdaptiveRow(index, c.Name, c.Kind, disposition, reason, run.AcceptedSteps, run.RejectedSteps, run.Evaluations,
            run.MinStepSeconds, run.MaxStepSeconds, run.EventBisections, run.StayedInSynchronism, spread, run.Events, comparison, run.Relays,
            run.TimeSeconds, list, null)
        {
            UnitRelays = run.UnitRelays,
            StopReason = run.StopReason,
        };
    }

    private static DynamicsAdaptiveRow CctRow(int index, DynamicsCaseRequest c, GridModel model, PowerFlowResult pf,
        IReadOnlyList<Dynamics.Models.SubtransientInit.MachineData> data, DynamicsStudyRequest request, IReadOnlyList<(int Branch, string Label)> trips)
    {
        DynamicsCctRequest q = c.Cct!;
        DynamicsAdaptiveRequest a = request.Adaptive!;
        try
        {
            AdaptiveCct.Result r = RmsTransientStability.StudyCctAdaptive(model, pf, data, q.FaultBus, trips.Select(t => t.Branch).ToList(), q.OnsetSeconds,
                q.MaxClearingSeconds, a.CctToleranceSeconds, a.Options, null, RmsTransientStability.PostClearWindowSeconds, request.Criteria.SlipLimitRad);
            return new DynamicsAdaptiveRow(index, c.Name, c.Kind, r.Disposition, null, 0, 0, 0, 0.0, 0.0, 0, true, 0.0, null, null, null, null, null, r);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failed(index, c, DynamicsDisposition.Failed, "the adaptive engine refused the row: " + ex.Message);
        }
    }
}
