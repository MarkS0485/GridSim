using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>One harmonic voltage measurement: the measured complex voltage at a bus at a given order, and its
/// standard deviation (for weighting).</summary>
public sealed record HarmonicVoltageMeasurement(int Bus, Complex Voltage, double Sigma = 1e-3);

/// <summary>The result of estimating the harmonic sources at one order.</summary>
/// <param name="Order">The harmonic order estimated.</param>
/// <param name="SourceBuses">The candidate source buses whose injections were estimated.</param>
/// <param name="Injection">The estimated complex injection current at each source bus (pu).</param>
/// <param name="ResidualNorm">The weighted measurement residual norm at the estimate.</param>
public sealed record HarmonicStateEstimate(
    int Order,
    IReadOnlyList<int> SourceBuses,
    Complex[] Injection,
    double ResidualNorm);

/// <summary>
/// Harmonic state estimation: recover the harmonic SOURCE injections (and hence the whole harmonic state)
/// from a handful of harmonic voltage measurements. It is the natural inverse of penetration, and the
/// on-brand extension of GridSim's estimator identity to the harmonic domain. At a single order the problem
/// is LINEAR - the measurements are <c>z = H x + noise</c> with <c>H = Z(h)[measured, source]</c> (columns of
/// the harmonic impedance matrix) and <c>x</c> the unknown injections - so it is a complex weighted
/// least-squares solve, reusing the complex-solver seam for both the impedance columns and the small normal
/// equations. It never nowcasts: harmonic measurements are dated inputs guarded upstream like any other.
///
/// <para><b>Experimental</b> (R062, October 2026): Core API and tests only; no CLI verb or production caller. Still
/// needs: an input format for dated harmonic voltage measurements (guarded like every other dated input), a CLI
/// surface (for example under the harmonics verb), and validation against an independent reference.</para>
/// </summary>
public static class HarmonicStateEstimator
{
    /// <summary>Estimate the injections at <paramref name="candidateSourceBuses"/> at one order from the
    /// harmonic voltage measurements.</summary>
    public static HarmonicStateEstimate Estimate(
        GridModel model,
        int order,
        IReadOnlyList<HarmonicVoltageMeasurement> measurements,
        IReadOnlyList<int> candidateSourceBuses,
        HarmonicModelOptions? options = null,
        IComplexLinearSolver? solver = null)
    {
        if (measurements.Count < candidateSourceBuses.Count)
            throw new ArgumentException("under-determined: need at least as many measurements as candidate sources.");

        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        solver ??= new RealExpandedComplexSolver();

        int n = model.BusCount;
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) index[model.Buses[i].Id] = i;

        ComplexCsr y = FrequencyDependentYBus.Build(model, order, opt);

        int s = candidateSourceBuses.Count;
        int m = measurements.Count;

        // Build H (m x s): column j is the harmonic impedance column of source bus j, restricted to the
        // measured buses. Z column j solves Y(h) x = e_j - the SAME matrix for every j, so factor Y(h)
        // once and back-substitute each unit-vector RHS (one factorization instead of s).
        var zcol = new Complex[s][];
        // N012: a singular Y(h) used to give an all-zero impedance column (fac null, or a null solve, replaced by
        // new Complex[n]), so the estimator silently fitted against zeros. Fail loud as every other harmonic study
        // does.
        IComplexLinearFactorization fac = solver.Factor(y)
            ?? throw new SingularHarmonicNetworkException("harmonic state estimation", order);
        for (int j = 0; j < s; j++)
        {
            var e = new Complex[n];
            e[index[candidateSourceBuses[j]]] = Complex.One;
            zcol[j] = fac.Solve(e) ?? throw new SingularHarmonicNetworkException("harmonic state estimation", order);
        }

        var h = new Complex[m, s];
        var z = new Complex[m];
        var w = new double[m];
        for (int i = 0; i < m; i++)
        {
            HarmonicVoltageMeasurement meas = measurements[i];
            // A measurement must name a bus of the model and carry a usable sigma (finite, above 0): an unknown bus
            // threw a bare KeyNotFoundException, and a zero, negative or NaN sigma gave an infinite or NaN weight.
            if (!index.TryGetValue(meas.Bus, out int bi))
                throw new ArgumentException($"Harmonic state estimation: measurement {i} names bus {meas.Bus}, which is not in the model.", nameof(measurements));
            if (!GridSim.Core.Distribution.ThreePhaseSigmaChecks.IsUsable(meas.Sigma))
                throw new ArgumentException($"Harmonic state estimation: measurement {i} at bus {meas.Bus} has sigma {meas.Sigma}, not a finite number above 0.", nameof(measurements));
            for (int j = 0; j < s; j++) h[i, j] = zcol[j][bi];
            z[i] = meas.Voltage;
            w[i] = 1.0 / (meas.Sigma * meas.Sigma);
        }

        // Complex weighted normal equations: (H^H W H) x = H^H W z, solved densely (s is small).
        var gram = new Complex[s, s];
        var rhs = new Complex[s];
        for (int a = 0; a < s; a++)
        {
            for (int b = 0; b < s; b++)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < m; i++) acc += Complex.Conjugate(h[i, a]) * w[i] * h[i, b];
                gram[a, b] = acc;
            }
            Complex r = Complex.Zero;
            for (int i = 0; i < m; i++) r += Complex.Conjugate(h[i, a]) * w[i] * z[i];
            rhs[a] = r;
        }

        // N012: the clamping solve silently zeroed an unobservable source (a Gram matrix singular to working
        // precision: two candidate sources the measurements cannot tell apart). The honest path gives the same bits
        // on an observable set and refuses an unobservable one.
        Complex[] xEst = DenseComplexLuSolver.SolveDenseOrNull(gram, rhs, s)
            ?? throw new InvalidOperationException(
                $"harmonic state estimation at order {order}: the candidate sources are not observable from these " +
                "measurements (the weighted Gram matrix is singular to working precision); add measurements or remove " +
                "candidates that the measurements cannot distinguish.");

        // Weighted residual norm at the estimate.
        double resSq = 0;
        for (int i = 0; i < m; i++)
        {
            Complex pred = Complex.Zero;
            for (int j = 0; j < s; j++) pred += h[i, j] * xEst[j];
            Complex e = z[i] - pred;
            resSq += w[i] * (e.Real * e.Real + e.Imaginary * e.Imaginary);
        }

        return new HarmonicStateEstimate(order, candidateSourceBuses, xEst, Math.Sqrt(resSq));
    }
}
