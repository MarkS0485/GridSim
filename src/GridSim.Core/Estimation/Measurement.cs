namespace GridSim.Core.Estimation;

/// <summary>
/// What physical quantity a measurement observes. The estimator's measurement equation is
/// z = h(x) + e: each kind has a closed-form h over the state x = (Va, Vm) evaluated through
/// the same admittance stamps the power-flow solvers use.
/// </summary>
public enum MeasurementKind
{
    /// <summary>Bus voltage magnitude, per-unit.</summary>
    VmPu,
    /// <summary>Net bus real-power injection (generation - demand), MW.</summary>
    PInjMw,
    /// <summary>Net bus reactive-power injection, MVAr.</summary>
    QInjMvar,
    /// <summary>Branch real-power flow into the measured end, MW (same convention as <see cref="PowerFlow.LineFlow.PFromMw"/>).</summary>
    PFlowMw,
    /// <summary>Branch reactive-power flow into the measured end, MVAr.</summary>
    QFlowMvar,
    /// <summary>Bus voltage angle, degrees, relative to the estimator's slack reference (WS-E).
    /// A PMU/phase-angle measurement: h(x) = Va[bus] - linear in the state, so a PMU row costs the
    /// Jacobian one constant entry. A real feed (e.g. Gridradar) carries angles against ITS OWN
    /// arbitrary reference; re-reference against the slack (subtract the feed's angle at a chosen
    /// datum bus) before authoring rows - the estimator does not solve for a per-feed offset.</summary>
    VaDeg,

    /// <summary>Branch current magnitude |I| into the measured end, per-unit. A common RTU/PMU quantity:
    /// |I| = |S|/|V| = sqrt(P^2 + Q^2) / Vm at the measured end, so it reuses the exact branch flow kernels.
    /// Addresses a branch (From/To/End), like the flow kinds.</summary>
    IMagPu,

    /// <summary>Branch current phasor ANGLE into the measured end, degrees, referenced to the estimator's slack
    /// datum (the same reference as <see cref="VaDeg"/>). ang(I) = Va_end - atan2(Q, P): a PMU current-phasor
    /// angle. With <see cref="IMagPu"/> it forms a full polar current phasor. Addresses a branch.</summary>
    IAngDeg,
}

/// <summary>Which end of a branch a flow measurement meters.</summary>
public enum BranchEnd
{
    /// <summary>The <see cref="Model.Branch.From"/> terminal.</summary>
    From,
    /// <summary>The <see cref="Model.Branch.To"/> terminal.</summary>
    To,
}

/// <summary>
/// One telemetered value. Bus measurements address the bus by <b>id</b> (not solver index) and
/// branch measurements by the branch's (<see cref="From"/>, <see cref="To"/>) endpoints - plus
/// <see cref="Circuit"/> (the <see cref="Model.Branch.Name"/>) to disambiguate parallel circuits -
/// so an external crosswalk (GDA GSP/BMU mapping) can author sets without knowing solver internals.
/// Values travel in engineering units (pu for voltage, MW/MVAr for power) and are converted to
/// per-unit once, at ingestion, against the model's BaseMva.
/// </summary>
public sealed record Measurement(
    MeasurementKind Kind,
    double Value,
    double Sigma,
    int Bus = 0,
    int From = 0,
    int To = 0,
    BranchEnd End = BranchEnd.From,
    string? Circuit = null,
    string Source = "scada")
{
    /// <summary>True for the branch-flow kinds (which address a branch, not a bus).</summary>
    public bool IsBranch => Kind is MeasurementKind.PFlowMw or MeasurementKind.QFlowMvar
        or MeasurementKind.IMagPu or MeasurementKind.IAngDeg;

    /// <summary>Human-readable location for diagnostics: "bus 4" or "branch 1->2 (from)".</summary>
    public string Describe() => IsBranch
        ? $"branch {From}->{To}{(Circuit is null ? "" : $" '{Circuit}'")} ({(End == BranchEnd.From ? "from" : "to")})"
        : $"bus {Bus}";
}
