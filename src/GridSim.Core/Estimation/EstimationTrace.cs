namespace GridSim.Core.Estimation;

/// <summary>One Gauss-Newton iteration's summary in the estimation trace.</summary>
public sealed record EstimationIteration(
    int Pass, int Iteration, double MaxStateUpdate, double StepScale);

/// <summary>
/// Opt-in per-iteration diagnostic capture for a <see cref="WlsStateEstimator"/> run - the
/// estimator-side sibling of the power-flow <see cref="Diagnostics.SolveTrace"/>. Records each
/// iteration's ||delta-x||inf and the trust-region scale actually applied, per bad-data pass, so a
/// convergence path can be audited after the fact (carried as an additive optional block in
/// <c>gridsim-estimate-export/1</c> and re-checked by <c>se_residual_check.py</c>: monotone
/// approach, caps binding only far from the optimum, pass boundaries at removals).
/// </summary>
public sealed class EstimationTrace
{
    private readonly List<EstimationIteration> _iterations = new();

    public IReadOnlyList<EstimationIteration> Iterations => _iterations;

    internal void Record(int pass, int iteration, double maxUpdate, double stepScale)
        => _iterations.Add(new EstimationIteration(pass, iteration, maxUpdate, stepScale));
}
