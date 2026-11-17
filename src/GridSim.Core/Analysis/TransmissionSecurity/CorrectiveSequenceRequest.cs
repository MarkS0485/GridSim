using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.TransmissionSecurity;

/// <summary>How the corrective actions between the two outages of a sequence are obtained.</summary>
public enum CorrectiveProposal
{
    /// <summary>No action: the second outage is taken from the first post-outage state as it stands.</summary>
    None,
    /// <summary>The actions are given in the request and replayed (a prescribed action replay; it does not search).</summary>
    Prescribed,
    /// <summary>The incumbent minimum-change DC corrective redispatch
    /// (<see cref="ContingencyAnalysis.PlanCorrectiveRedispatch(GridSim.Core.Model.GridModel)"/>) is run on the first
    /// post-outage network and its non-zero changes become the proposal. It is a candidate generator only: every change
    /// is validated against the action policy and has no authority outside it.</summary>
    PlannedRedispatch,
}

/// <summary>Which signs of MW change an eligible unit may take.</summary>
public enum ActionDirection { Up, Down, Both }

/// <summary>
/// One unit's permitted corrective envelope. <paramref name="Generator"/> is the model generator index and
/// <paramref name="Bus"/> its expected bus (the stable identity used by the preventive study). A proposed change is
/// admissible only when its sign is allowed, its final setpoint lies in [<paramref name="MinFinalMw"/>,
/// <paramref name="MaxFinalMw"/>] and the unit's own [Pmin, Pmax], and its magnitude is within the tighter of
/// <paramref name="MaxDeltaMw"/> and <paramref name="RampMwPerMin"/> times the available minutes (the smaller of
/// <paramref name="WindowMinutes"/> and the study's <see cref="CorrectiveActionPolicy.AvailableMinutes"/>).
/// </summary>
public sealed record GeneratorActionEnvelope(int Generator, int Bus, ActionDirection Direction,
    double MinFinalMw, double MaxFinalMw, double MaxDeltaMw, double RampMwPerMin, double WindowMinutes);

/// <summary>One proposed MW change on a unit (model generator index and expected bus).</summary>
public sealed record GeneratorAction(int Generator, int Bus, double DeltaMw);

/// <summary>
/// One ordered N-1-1 sequence: <paramref name="First"/> is lost, actions are proposed, validated and applied, the
/// intermediate state is verified, then <paramref name="Second"/> is lost and the final state is verified.
/// <paramref name="Actions"/> is required for <see cref="CorrectiveProposal.Prescribed"/> and refused otherwise.
/// </summary>
public sealed record CorrectiveSequence(BranchOutage First, BranchOutage Second, CorrectiveProposal Proposal,
    IReadOnlyList<GeneratorAction>? Actions = null);

/// <summary>
/// A corrective N-1-1 study. The intact state uses RATE_A. The first post-outage state is checked against
/// <paramref name="FirstStageRating"/> (the temporary emergency rating the network may sit at until actions complete);
/// loading above <paramref name="IntermediateRating"/> there is reported as temporary emergency loading, not a failure.
/// The intermediate state (first outage plus actions) is checked against <paramref name="IntermediateRating"/>, and the
/// final state (both outages, actions retained, no further control) against <paramref name="FinalRating"/>. Each
/// rating falls back to RATE_A on a branch where it is not given. The voltage band applies to every state.
/// </summary>
public sealed record CorrectiveSequenceRequest(IReadOnlyList<CorrectiveSequence> Sequences, double MinPu, double MaxPu,
    PostFaultRating FirstStageRating, PostFaultRating IntermediateRating, PostFaultRating FinalRating,
    CorrectiveActionPolicy Policy);
