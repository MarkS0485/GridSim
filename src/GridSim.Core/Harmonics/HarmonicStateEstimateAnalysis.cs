using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>One measurement of a harmonic state estimate, with its residual.</summary>
/// <param name="Bus">The measured bus.</param>
/// <param name="Measured">The measured voltage phasor (pu).</param>
/// <param name="Estimated">The voltage the estimate gives at that bus (pu).</param>
/// <param name="Residual">Measured minus estimated (pu).</param>
/// <param name="Sigma">The measurement's standard deviation, per real and imaginary part (pu).</param>
/// <param name="ResidualSigma">The standard deviation of each part of the residual, <c>sqrt(Omega_ii)</c> (pu); NaN for a
/// critical measurement (the estimate fits it exactly, Omega_ii is 0 to rounding).</param>
/// <param name="NormalisedResidual"><c>|Residual| / sqrt(Omega_ii)</c>; NaN for a critical measurement. Omega_ii is the
/// variance of EACH part of the complex residual, so with Gaussian errors the square of this follows chi-square with 2
/// degrees of freedom (the value itself is Rayleigh: mean sqrt(pi/2) = 1.25, P(rN &gt; c) = exp(-c^2 / 2), 1.1 % above
/// 3), not a standard normal as for a real measurement.</param>
public sealed record HarmonicMeasurementResidual(int Bus, Complex Measured, Complex Estimated, Complex Residual, double Sigma,
    double ResidualSigma, double NormalisedResidual);

/// <summary>A harmonic state estimate at one order with its bad-data statistics.</summary>
/// <param name="Order">The harmonic order.</param>
/// <param name="Observable">False when the candidate sources cannot be told apart from these measurements (the estimator
/// refuses them); every other field is then empty or NaN.</param>
/// <param name="SourceBuses">The candidate source buses.</param>
/// <param name="Injection">The estimated injection at each candidate (pu), from <see cref="HarmonicStateEstimator"/>.</param>
/// <param name="BusIds">Every bus of the case, in case order.</param>
/// <param name="Voltage">The estimated voltage at every bus: <c>V = Z(:, sources) x</c> (pu).</param>
/// <param name="Residuals">One row per measurement, in input order.</param>
/// <param name="ChiSquare">The weighted residual sum <c>J = sum |r_i|^2 / sigma_i^2</c> (the square of the estimator's
/// residual norm).</param>
/// <param name="DegreesOfFreedom"><c>2 (m - s)</c>: m complex measurements, s complex unknowns, two real parts each.</param>
/// <param name="Threshold">The chi-square quantile at <see cref="HarmonicStateEstimateAnalysis.Probability"/> for those
/// degrees of freedom; NaN when there is no redundancy.</param>
/// <param name="BadData">True when <c>J</c> exceeds the threshold.</param>
/// <param name="LargestNormalisedResidualBus">The measured bus with the largest normalised residual, or null.</param>
public sealed record HarmonicStateEstimateReport(
    int Order,
    bool Observable,
    IReadOnlyList<int> SourceBuses,
    Complex[] Injection,
    IReadOnlyList<int> BusIds,
    Complex[] Voltage,
    IReadOnlyList<HarmonicMeasurementResidual> Residuals,
    double ChiSquare,
    int DegreesOfFreedom,
    double Threshold,
    bool BadData,
    int? LargestNormalisedResidualBus);

/// <summary>
/// RC5 WP27: the bad-data statistics of a harmonic state estimate. The estimate itself is
/// <see cref="HarmonicStateEstimator.Estimate"/>, unchanged; this rebuilds the same measurement matrix
/// <c>H = Z(h)[measured, sources]</c> the same way (one factorisation of Y(h), unit right-hand sides, in the same order) and
/// adds:
/// <list type="bullet">
/// <item>the estimated voltage at every bus, <c>V = Z(:, sources) x</c>;</item>
/// <item>the residual of each measurement and its normalised residual <c>|r_i| / sqrt(Omega_ii)</c>, with
/// <c>Omega = R - H G^-1 H^H</c>, <c>G = H^H W H</c>, <c>W = R^-1</c>, <c>R = diag(sigma_i^2)</c> (the residual covariance of
/// each real part);</item>
/// <item>the chi-square test: <c>J = sum w_i |r_i|^2</c> against the 99 % quantile with <c>2 (m - s)</c> degrees of freedom.</item>
/// </list>
/// The convention: <c>sigma</c> is the standard deviation of each of the real and imaginary parts, so with Gaussian errors
/// <c>J</c> follows chi-square with <c>2 (m - s)</c> degrees of freedom. Those degrees of freedom are always even, so the
/// quantile is exact: <see cref="ChiSquare.QuantileEven"/> inverts <c>F(x) = 1 - e^(-x/2) sum_(j&lt;k) (x/2)^j / j!</c>
/// (k = dof / 2). The normalised residual <c>|r_i| / sqrt(Omega_ii)</c> is kept on the per-part variance: its square
/// follows chi-square with 2 degrees of freedom under Gaussian errors (it is not a standard normal), and only its largest
/// is used, to point at the suspect measurement, which no common scale changes. With exactly one bad measurement
/// and otherwise exact data, the largest normalised residual is at the bad measurement (Cauchy-Schwarz on Omega), unless
/// two measurements have parallel columns of Omega.
/// </summary>
public static class HarmonicStateEstimateAnalysis
{
    /// <summary>The probability of the chi-square threshold.</summary>
    public const double Probability = 0.99;

    /// <summary>Estimate the injections at one order and add the statistics.</summary>
    /// <exception cref="ArgumentException">Fewer measurements than candidates, a bus not in the case, or a bad sigma (the
    /// estimator's own refusals).</exception>
    /// <exception cref="SingularHarmonicNetworkException">Y(h) is singular.</exception>
    public static HarmonicStateEstimateReport Analyse(
        GridModel model,
        int order,
        IReadOnlyList<HarmonicVoltageMeasurement> measurements,
        IReadOnlyList<int> candidateSourceBuses,
        HarmonicModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(candidateSourceBuses);
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        int n = model.BusCount;
        var busIds = new int[n];
        var index = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) { busIds[i] = model.Buses[i].Id; index[busIds[i]] = i; }

        // The estimator's own refusals (an unknown bus, a bad sigma, fewer measurements than candidates) come first. When it
        // fails with an InvalidOperationException, observability is decided below from the Gram matrix this method forms
        // the same way (a typed check, not the message): singular means not observable; anything else is rethrown.
        HarmonicStateEstimate? est = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failed = null;
        try
        {
            est = HarmonicStateEstimator.Estimate(model, order, measurements, candidateSourceBuses, opt, new RealExpandedComplexSolver());
        }
        catch (InvalidOperationException ex) when (ex is not SingularHarmonicNetworkException)
        {
            failed = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }

        // The same H the estimator built: one factorisation, the unit right-hand sides in candidate order.
        ComplexCsr y = FrequencyDependentYBus.Build(model, order, opt);
        IComplexLinearFactorization fac = new RealExpandedComplexSolver().Factor(y)
            ?? throw new SingularHarmonicNetworkException("harmonic state estimation", order);
        int s = candidateSourceBuses.Count, m = measurements.Count;
        var zcol = new Complex[s][];
        for (int j = 0; j < s; j++)
        {
            var e = new Complex[n];
            e[index[candidateSourceBuses[j]]] = Complex.One;
            zcol[j] = fac.Solve(e) ?? throw new SingularHarmonicNetworkException("harmonic state estimation", order);
        }

        var h = new Complex[m, s];
        var w = new double[m];
        for (int i = 0; i < m; i++)
        {
            int bi = index[measurements[i].Bus];
            for (int j = 0; j < s; j++) h[i, j] = zcol[j][bi];
            w[i] = 1.0 / (measurements[i].Sigma * measurements[i].Sigma);
        }
        // G = H^H W H, as the estimator forms it.
        var gram = new Complex[s, s];
        for (int a = 0; a < s; a++)
            for (int b = 0; b < s; b++)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < m; i++) acc += Complex.Conjugate(h[i, a]) * w[i] * h[i, b];
                gram[a, b] = acc;
            }
        if (failed is not null)
        {
            // The estimator's Gram matrix, bit for bit (the same H, weights and sums), so the same pivots decide.
            if (DenseComplexLuSolver.SolveDenseOrNull((Complex[,])gram.Clone(), new Complex[s], s) is null)
                return new HarmonicStateEstimateReport(order, false, candidateSourceBuses, [], busIds, [], [], double.NaN,
                    2 * (measurements.Count - candidateSourceBuses.Count), double.NaN, false, null);
            failed.Throw();
        }
        HarmonicStateEstimate estimate = est!;

        // The estimated voltage at every bus.
        var voltage = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex acc = Complex.Zero;
            for (int j = 0; j < s; j++) acc += zcol[j][i] * estimate.Injection[j];
            voltage[i] = acc;
        }

        var rows = new HarmonicMeasurementResidual[m];
        double j2 = 0.0;
        double worst = -1.0;
        int? worstBus = null;
        for (int i = 0; i < m; i++)
        {
            Complex pred = Complex.Zero;
            for (int k = 0; k < s; k++) pred += h[i, k] * estimate.Injection[k];
            Complex r = measurements[i].Voltage - pred;
            j2 += w[i] * (r.Real * r.Real + r.Imaginary * r.Imaginary);
            // K_ii = h_i G^-1 h_i^H: solve G y = h_i^H.
            var rhs = new Complex[s];
            for (int a = 0; a < s; a++) rhs[a] = Complex.Conjugate(h[i, a]);
            Complex[]? yv = DenseComplexLuSolver.SolveDenseOrNull((Complex[,])gram.Clone(), rhs, s);
            double kii = 0.0;
            if (yv is not null)
            {
                Complex acc = Complex.Zero;
                for (int a = 0; a < s; a++) acc += h[i, a] * yv[a];
                kii = acc.Real;
            }
            double sigma2 = measurements[i].Sigma * measurements[i].Sigma;
            double omega = sigma2 - kii;
            // A critical measurement: Omega_ii is 0 in exact arithmetic (the estimate passes through it), so the computed value
            // is the rounding of sigma^2 - K_ii alone. K_ii = h_i G^-1 h_i^H comes from a backward-stable LU of the s x s Gram
            // matrix and sums of m terms, so its relative error is at most about (3s + 2m) u kappa(G) (Higham, thm 9.5, first
            // order). The cutoff 1e-9 sigma^2 lies above that rounding while (3s + 2m) u kappa(G) < 1e-9, i.e. kappa(G) below
            // about 1e6 for the handful of candidates and measurements an order has; and a non-critical measurement whose
            // residual standard deviation is below sqrt(1e-9) sigma = 3.2e-5 sigma carries no usable redundancy either way.
            bool critical = yv is null || !(omega > 1e-9 * sigma2);
            double rs = critical ? double.NaN : Math.Sqrt(omega);
            double rn = critical ? double.NaN : r.Magnitude / rs;
            rows[i] = new HarmonicMeasurementResidual(measurements[i].Bus, measurements[i].Voltage, pred, r, measurements[i].Sigma, rs, rn);
            if (!critical && rn > worst) { worst = rn; worstBus = measurements[i].Bus; }
        }
        int dof = 2 * (m - s);
        double threshold = ChiSquare.QuantileEven(dof, Probability);
        bool bad = !double.IsNaN(threshold) && j2 > threshold;
        return new HarmonicStateEstimateReport(order, true, candidateSourceBuses, estimate.Injection, busIds, voltage, rows, j2, dof, threshold, bad, worstBus);
    }
}
