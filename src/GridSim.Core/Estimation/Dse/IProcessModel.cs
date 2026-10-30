namespace GridSim.Core.Estimation.Dse;

/// <summary>
/// The predict-step process model of a recursive (Kalman-family) state estimator: how the state is expected
/// to evolve from one measurement window to the next, and how much trust to place in that expectation.
///
/// <para>This is the seam where a <c>Dynamics/</c> DAE model could be fused into estimation - the boundary
/// case <c>UnderstandingRestrictions.md</c> gates (invariant #5). The predict step integrates the MODEL a
/// step ahead; the filter's emitted estimate is still the FILTERED PRESENT (a filter, not a predictor). The
/// default <see cref="RandomWalkProcessModel"/> is the classical persistence model (state expected to hold,
/// with process noise); an electromechanical process model is a later, reviewed increment.</para>
/// </summary>
public interface IProcessModel
{
    /// <summary>A short identifier for provenance/logging.</summary>
    string Name { get; }

    /// <summary>
    /// Predict the prior state <paramref name="x"/> (packed estimator state, modified in place to the
    /// one-step-ahead prediction x^-), and fill the state-transition Jacobian <paramref name="f"/> (s x s,
    /// df/dx) and the process-noise covariance <paramref name="q"/> (s x s) for the step of length
    /// <paramref name="dtSeconds"/>. For persistence, f is the identity and x is unchanged.
    /// </summary>
    void Predict(double[] x, double dtSeconds, double[,] f, double[,] q);
}
