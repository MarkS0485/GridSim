namespace GridSim.Core.Physics;

/// <summary>
/// How a physical law scored against a solved/estimated/dynamic state.
/// <list type="bullet">
/// <item><see cref="Holds"/> - the law is satisfied within tolerance.</item>
/// <item><see cref="Violated"/> - the state breaks the law (a physics-impossible result). For a
/// hard-gate law this fails estimator acceptance.</item>
/// <item><see cref="Constitutive"/> - the law is embodied in the model's constitutive data rather
/// than checked as a field equation (Faraday via transformer ratios, Ampere via series reactance);
/// reported with a best-effort consistency residual, never blocking.</item>
/// <item><see cref="NotModeled"/> - outside the positive-sequence phasor model entirely (Gauss).</item>
/// <item><see cref="NotApplicable"/> - the law needs an input this state does not carry (e.g.
/// synchronism on a steady-state snapshot).</item>
/// </list>
/// </summary>
public enum LawStatus { Holds, Violated, Constitutive, NotModeled, NotApplicable }

/// <summary>
/// One physical law's verdict against a state: the residual by which it holds or is violated, in its
/// own physical unit, against the tolerance applied. <see cref="Statement"/> is the law in words;
/// <see cref="Detail"/> carries the specific numbers (e.g. the two sides of a balance).
/// </summary>
public sealed record LawResult(
    int Number,
    string Name,
    string Statement,
    LawStatus Status,
    double Residual,
    string Unit,
    double Tolerance,
    string Detail);

/// <summary>
/// The full physical-law audit of a state - one <see cref="LawResult"/> per law - plus the acceptance
/// verdict. This is the feasibility oracle's output: the definition of whether a state lies in the
/// physically-admissible region.
/// </summary>
public sealed record PhysicsLawReport(
    IReadOnlyList<LawResult> Laws,
    double BaseMva,
    bool Converged)
{
    /// <summary>No law is <see cref="LawStatus.Violated"/> (constitutive/structural/NA are non-blocking).</summary>
    public bool AllHold => Laws.All(l => l.Status is not LawStatus.Violated);

    /// <summary>The violated laws, worst-residual first.</summary>
    public IEnumerable<LawResult> Violations =>
        Laws.Where(l => l.Status is LawStatus.Violated).OrderByDescending(l => l.Residual);

    /// <summary>The single worst violation, or null when none.</summary>
    public LawResult? Worst => Violations.FirstOrDefault();

    /// <summary>
    /// Acceptance gate: every law whose number is in <paramref name="hardLaws"/> holds. A hard law is
    /// one whose violation is physically impossible (energy/Ohm/Joule/KCL/complex-power), so a breach
    /// means the state is not a real network state and the estimator must reject it.
    /// </summary>
    public bool Gate(IReadOnlyCollection<int> hardLaws) =>
        Laws.Where(l => hardLaws.Contains(l.Number))
            .All(l => l.Status is not LawStatus.Violated);
}

/// <summary>
/// The dynamics input to the synchronism law (law 9), filled by the caller from whatever transient
/// artefact it holds - <c>ClassicalMultiMachine.Result</c> (rotor-angle spread) and/or a domain
/// frequency trace. Keeps the audit engine decoupled from any one dynamics result type.
/// </summary>
/// <param name="MaxAngleSpreadDeg">Largest |delta_i - delta_COI| over the run (deg).</param>
/// <param name="MaxFreqExcursionHz">Largest |f - f_nominal| over the run (Hz).</param>
/// <param name="PoleSlipLimitDeg">Pole-slip angle beyond which synchronism is lost (default 180).</param>
/// <param name="FreqBandHz">Frequency excursion beyond which synchronism is at risk (default 0.8).</param>
/// <param name="FormingBoundHz">Optional grid-forming analytic droop envelope |dP|/Dv (Hz). When &gt; 0 it
/// contributes an ADDITIONAL max-of-margins term (MaxFreqExcursionHz / FormingBoundHz) - the formed
/// frequency must stay inside its droop envelope. Default 0 leaves law-9 scoring byte-identical for every
/// existing / non-forming sample.</param>
/// <param name="AngleOnly">N246 (October 2026): score the rotor-angle criterion alone, the out-of-step test of a fault
/// study (PSS/E and PowerFactory declare loss of synchronism when a machine's angle from the centre of inertia passes
/// 180 degrees; they apply no frequency band to a fault run). The frequency excursion is then reported, not scored.
/// Default false: every existing sample scores exactly as before (angle and frequency margins, byte-identical).</param>
public sealed record SynchronismSample(
    double MaxAngleSpreadDeg,
    double MaxFreqExcursionHz,
    double PoleSlipLimitDeg = 180.0,
    double FreqBandHz = 0.8,
    double FormingBoundHz = 0.0,
    bool AngleOnly = false);
