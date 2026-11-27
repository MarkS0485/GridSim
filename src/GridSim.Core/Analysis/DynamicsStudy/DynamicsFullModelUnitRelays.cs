using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC5 stage 6: one row of the full-model unit relay companion run (an events row of the study re-run on the incumbent
/// fixed-step engine with the units' own relays in it).
/// </summary>
/// <param name="Index">The row's index in the request.</param>
/// <param name="Name">The row's name.</param>
/// <param name="Disposition">EVALUATED (the run completed or ended on a last-machine trip), FAILED (the engine refused it, reason
/// given) or NOT RUN (a CCT row: unit relays are not run in a clearing time search).</param>
/// <param name="Reason">Why a row failed or was not run; null otherwise.</param>
/// <param name="StayedInSynchronism">The relay run's synchronism verdict over the machines still connected (null when not run).</param>
/// <param name="EndedAtSeconds">The relay run's last sample when it ended early, or null.</param>
/// <param name="EndReason">Why the relay run ended early, or null.</param>
/// <param name="UnitRelays">Every evaluated stage's record in this row (null when not run).</param>
public sealed record DynamicsFullModelUnitRelayRow(int Index, string Name, string Disposition, string? Reason, bool? StayedInSynchronism,
    double? EndedAtSeconds, string? EndReason, IReadOnlyList<FullModelUnitRelayRecord>? UnitRelays)
{
    /// <summary>RC5 wp32b: every branch relay's record in this row; null (and not written) without branch relays.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FullModelBranchRelayRecord>? BranchRelays { get; init; }
}

/// <summary>RC5 wp32b: one branch trip by a user relay in the full-model companion run.</summary>
public sealed record DynamicsFullModelBranchTrip(int Row, string Case, string Branch, string Device, double OperateSeconds,
    double DisconnectedSeconds, double? ReasonCode, string Settings);

/// <summary>RC5 stage 6: one unit trip of the full-model companion run.</summary>
public sealed record DynamicsFullModelUnitTrip(int Row, string Case, string Unit, string Device, double OperateSeconds,
    double DisconnectedSeconds, double? Quantity, double? ImpedanceRPu, double? ImpedanceXPu, string Settings);

/// <summary>
/// RC5 stage 6: the units' own relays (81U / 81O, 81R, 27 / 59, 32, 40, 78) in the full-model fixed-step engine, one companion
/// run per events row. The fixed-step rows of the study (<see cref="DynamicsStudyResult.Cases"/>) are unchanged and decide the
/// study status; this is additional evidence.
/// </summary>
/// <param name="Engine">The engine the companion rows ran on.</param>
/// <param name="Rule">How the relays act (<see cref="RmsTransientStability.FullModelUnitRelayRule"/>).</param>
/// <param name="StatusRule">How the companion rows relate to the study status.</param>
/// <param name="NotEvaluated">The stages not evaluated, with reasons (the same for every row).</param>
/// <param name="Trips">Every unit trip of every row, in row order.</param>
/// <param name="Rows">One row per request row, in request order.</param>
public sealed record DynamicsFullModelUnitRelayEvidence(string Engine, string Rule, string StatusRule,
    IReadOnlyList<UnitRelayNotEvaluated> NotEvaluated, IReadOnlyList<DynamicsFullModelUnitTrip> Trips,
    IReadOnlyList<DynamicsFullModelUnitRelayRow> Rows)
{
    /// <summary>RC5 wp32b: every branch trip by a user relay, in row order; null (and not written) without branch relays.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DynamicsFullModelBranchTrip>? BranchTrips { get; init; }
}
