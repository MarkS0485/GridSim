using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Models;

public static partial class RmsTransientStability
{
    /// <summary>
    /// RC5 stage 6: a full-model RMS event study with each unit's own relays in the run.
    /// </summary>
    /// <param name="Result">The run (its traces end at <see cref="EndedAtSeconds"/> when the run ended early).</param>
    /// <param name="Prepared">The fleet the run started from.</param>
    /// <param name="UnitRelays">Every evaluated stage's record, in unit and stage order.</param>
    /// <param name="NotEvaluated">The stages not evaluated, with reasons.</param>
    /// <param name="EndReason">Why the run ended before its duration (a trip of the last connected machine), or null.</param>
    /// <param name="EndedAtSeconds">The time of the run's last sample when it ended early, or null.</param>
    public sealed record UnitRelayStudy(MultiMachineSubtransient.Result Result, SubtransientInit.Prepared Prepared,
        IReadOnlyList<FullModelUnitRelayRecord> UnitRelays, IReadOnlyList<UnitRelayNotEvaluated> NotEvaluated,
        string? EndReason, double? EndedAtSeconds)
    {
        /// <summary>RC5 wp32b: every branch-bound user relay's record, in binding order (empty without branch relays).</summary>
        public IReadOnlyList<FullModelBranchRelayRecord> BranchRelays { get; init; } = Array.Empty<FullModelBranchRelayRecord>();
    }

    /// <summary>RC5 wp32b: how user relays (plug-ins and declarative relays) act, stated in every result that runs them.</summary>
    public const string UserRelayRule =
        "each user relay (a relay plug-in, PLUGIN:ID, or a declarative gridsim-model/1 relay, USER:ID) is evaluated at every step start "
        + "from that step's network solve, after the units' own relay stages; a unit relay reads its machine (|V| at the unit's bus, F0 (1 + w), "
        + "F0 dw/dt, P, Q, w and |I|) and its trip disconnects the machine from the next step as a unit relay stage does; a branch relay reads "
        + "one end of its branch (|V| at that bus, the P, Q and |I| flowing into the branch there; no frequency or speed, a branch has no "
        + "machine) and its trip opens the branch at both ends from the next step (the network rebuilt without it, as a scheduled branch "
        + "trip); a declarative relay's timers count whole steps (pickup at the first step beyond, operation at the first step k with "
        + "(k - k_pickup) h + h 1e-6 >= the delay); a trip of the last connected machine, or a branch trip that leaves a part of the network "
        + "with no path to ground, ends the run there";

    /// <summary>How unit relays act in the full-model engine, stated in every result that uses them.</summary>
    public const string FullModelUnitRelayRule =
        "each unit's own relay stages (its unitProtection settings) are evaluated at every step start from that step's network solve: "
        + "81U / 81O on the machine's electrical frequency F0 (1 + w); 81R on F0 dw/dt from the swing equation (no relay measurement window); "
        + "27 / 59 on |V| at the unit's bus (V = E'' - jX'' I), or, behind a folded unit transformer (UnitTransformerXPu above 0), on |V_t| at the "
        + "machine terminal V_t = V + j X_t I on its generator side (each record's VoltageMeasuredAt says which); 32 on the electrical power "
        + "Re(E'' conj I) (not evaluated on a unit with no rating); 40 on the apparent impedance at the machine terminal Z = V_t / I (V_t = V "
        + "without a unit transformer, so Z = V / I + j X_t with one) in pu of the unit rating against its offset mho zone (centre "
        + "-j(offset + diameter / 2), radius diameter / 2); "
        + "78 on the V_t angle jump across a network change at that instant (instantaneous); a stage picks up at the first step beyond its pickup "
        + "and operates at the first step at least its delay later (never early, resolution one step), a step not beyond resets it; an operating "
        + "stage disconnects its unit's machine from the next step (network rebuilt without it, mechanical power 0, field held, not judged) and "
        + "the run continues; a trip of the last connected machine ends the run there; at one step units in order, stages in order, one trip per "
        + "unit; critical clearing time rows do not run unit relays";

    /// <summary>
    /// RC5 stage 6: <see cref="StudyEvents"/> with each unit's own relays in the full-model fixed-step engine (opt-in): the
    /// stages of <see cref="GridModel.UnitProtection"/> (81U / 81O, 81R, 27 / 59, 32, 40 and 78; see
    /// <see cref="FullModelUnitRelayRule"/>) are evaluated at every step, and an operating stage disconnects its unit while the
    /// run continues. Unlike <see cref="StudyEvents"/> the event list may be empty (the relays may be the only events, for
    /// example after a field failure in a controller). Refused when the model carries no unit protection. Every other input and
    /// rule is <see cref="StudyEvents"/>'s, and that method is unchanged.
    /// </summary>
    public static UnitRelayStudy StudyEventsWithUnitRelays(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.UnitProtection is null)
            throw new ArgumentException("The model carries no unit protection (GridModel.UnitProtection), so there are no unit relays to run; use StudyEvents.", nameof(model));
        FullModelUnitRelays? host = null;
        SubtransientInit.Prepared? prepared = null;
        MultiMachineSubtransient.Result run = StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds, slipLimitRad,
            exciters, null, (h, p) => { host = h; prepared = p; });
        return new UnitRelayStudy(run, prepared!, host!.Records(), host.NotEvaluated, host.EndReason, host.EndedAtSeconds);
    }

    /// <summary>
    /// RC5 WP32: <see cref="StudyEventsWithUnitRelays"/> with contract 2 relay plug-ins (<see cref="Plugins.UserRelayBinding"/>; RC5
    /// wp32b: or declarative relays, and either bound to a branch instead of a unit, see <see cref="UserRelayRule"/>)
    /// acting as well, with or without <see cref="GridModel.UnitProtection"/>. Each plug-in gets a fresh instance for the run and
    /// is evaluated at every step start after the built-in stages, from the same network solve; a trip disconnects its unit
    /// from the next step on by the built-in trip path. The records list the built-in stages first, then the plug-ins (device
    /// <c>PLUGIN:ID</c>, the reason code in <see cref="FullModelUnitRelayRecord.OperateQuantity"/>, the plug-in provenance with
    /// OUTSIDE THE VERIFIED CONTRACT as the settings). Every other input and rule is <see cref="StudyEventsWithUnitRelays"/>'s.
    /// </summary>
    public static UnitRelayStudy StudyEventsWithUserRelays(
        GridModel model, PowerFlowResult result, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsEvent> events, IReadOnlyList<Plugins.UserRelayBinding> userRelays,
        double? f0 = null, double stepSeconds = StudyDefaultStepSeconds,
        double durationSeconds = StudyDefaultDurationSeconds,
        double slipLimitRad = DefaultSlipLimitRad,
        IReadOnlyList<RmsExciter?>? exciters = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(userRelays);
        if (userRelays.Count == 0)
            throw new ArgumentException("No relay plug-ins were given; use StudyEventsWithUnitRelays or StudyEvents.", nameof(userRelays));
        for (int i = 0; i < userRelays.Count; i++)
            if (userRelays[i] is null) throw new ArgumentException($"userRelays[{i}] is null.", nameof(userRelays));
        FullModelUnitRelays? host = null;
        SubtransientInit.Prepared? prepared = null;
        MultiMachineSubtransient.Result run = StudyEventsCore(model, result, data, events, f0, stepSeconds, durationSeconds, slipLimitRad,
            exciters, null, (h, p) => { host = h; prepared = p; }, userRelays: userRelays);
        return new UnitRelayStudy(run, prepared!, host!.Records(), host.NotEvaluated, host.EndReason, host.EndedAtSeconds)
        {
            BranchRelays = host.BranchRecords(),
        };
    }
}
