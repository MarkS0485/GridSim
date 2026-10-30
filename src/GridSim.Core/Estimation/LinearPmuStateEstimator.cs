using System.Globalization;
using System.Numerics;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>A synchronized bus voltage-phasor measurement (per-unit, rectangular), from a PMU.</summary>
/// <param name="BusId">The measured bus.</param>
/// <param name="Value">The voltage phasor (per-unit).</param>
/// <param name="Sigma">Measurement sigma (per-unit) on each rectangular component.</param>
/// <param name="CovOffDiag">Covariance between the real and imaginary error components (0 = independent).</param>
/// <param name="SigmaIm">Sigma of the imaginary component when it differs from <paramref name="Sigma"/> (null: the same
/// sigma on both components, the original circular error). A polar PMU reading (magnitude and angle sigmas) maps to
/// unequal rectangular variances (<see cref="PmuMeasurementSetAdapter"/>, 1.95.245).</param>
public sealed record PmuVoltageMeasurement(int BusId, Complex Value, double Sigma, double CovOffDiag = 0.0, double? SigmaIm = null);

/// <summary>A synchronized branch current-phasor measurement (per-unit, rectangular), from a PMU.</summary>
/// <param name="FromBusId">Branch from-bus.</param>
/// <param name="ToBusId">Branch to-bus.</param>
/// <param name="Circuit">Circuit name to disambiguate parallels (null if unique).</param>
/// <param name="Value">The current phasor into the from end (per-unit).</param>
/// <param name="Sigma">Measurement sigma (per-unit) on each rectangular component.</param>
/// <param name="CovOffDiag">Covariance between the real and imaginary error components (0 = independent).</param>
/// <param name="SigmaIm">Sigma of the imaginary component when it differs from <paramref name="Sigma"/> (null: the same
/// sigma on both components).</param>
public sealed record PmuCurrentMeasurement(int FromBusId, int ToBusId, string? Circuit, Complex Value, double Sigma, double CovOffDiag = 0.0, double? SigmaIm = null);

/// <summary>The single-shot linear PMU estimate: rectangular voltages resolved in one non-iterative solve.</summary>
/// <param name="Converged">True when the normal-equation gain was non-singular.</param>
/// <param name="BusIds">Bus ids aligned with <see cref="Vm"/>/<see cref="VaDeg"/>.</param>
/// <param name="Vm">Per-bus voltage magnitude (per-unit).</param>
/// <param name="VaDeg">Per-bus voltage angle (degrees).</param>
/// <param name="ObjectiveJ">Weighted residual sum sum (r/sigma)^2 - ~0 when the phasors are consistent.</param>
public sealed record LinearPmuResult(bool Converged, IReadOnlyList<int> BusIds, double[] Vm, double[] VaDeg, double ObjectiveJ);

/// <summary>
/// A <b>linear</b> PMU state estimator: with the state in rectangular voltages x = [Vre; Vim], both the
/// synchronized voltage phasors (h = x) and the branch current phasors (I = Yff V_from + Yft V_to) are
/// <b>linear</b> in x, so the whole estimate is a single non-iterative weighted-least-squares solve
/// x = (H^T W H)^-1 H^T W z - unlike the iterative nonlinear WLS. It is the fast inner estimator of the
/// hybrid multi-rate scheme (run at PMU rate). A static, measurement-only filter (no review-gate entry);
/// inputs arrive guard-checked from the PMU mapper.
///
/// <para><b>Experimental</b> (R062, October 2026): one non-iterative solve with no bad-data test. Production caller
/// (1.95.245): the CLI verb <c>pmu-estimate</c> runs it over a gridsim-measurements/1 set
/// (for example one <c>pmu-ingest --measurements-dir</c> wrote), through <see cref="PmuMeasurementSetAdapter"/>, which
/// pairs the mapper's polar VmPu / VaDeg / IMagPu / IAngDeg rows into these rectangular records with their exact
/// first-order error covariance (<c>SigmaIm</c> and <c>CovOffDiag</c>). Until then it was Core API and tests only
/// (R062; D-DOC-219 corrected the earlier note). Still outstanding: a live or replay surface at PMU rate (the hybrid
/// multi-rate scheduler is test-only), and to-end current phasors.</para>
///
/// <para><b>Inputs are checked (D-DOC-109, October 2026).</b> Every measurement is checked before any row is built and
/// every problem is named in one <see cref="InvalidDataException"/>: a voltage phasor at a bus the model does not have,
/// a sigma that is not a finite number above 0, a covariance that is not finite or does not give a positive-definite
/// 2x2 error matrix, and a non-finite phasor value. A current on a branch that is not an in-service model branch (or
/// that matches parallel circuits without a circuit name) is refused by name, as before.</para>
/// </summary>
public static class LinearPmuStateEstimator
{
    /// <summary>Solve the rectangular state from voltage and current phasors in one linear step.</summary>
    public static LinearPmuResult Estimate(
        GridModel model,
        IReadOnlyList<PmuVoltageMeasurement> voltages,
        IReadOnlyList<PmuCurrentMeasurement> currents)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(voltages);
        ArgumentNullException.ThrowIfNull(currents);
        int n = model.BusCount;
        int cols = 2 * n; // [Vre_0..n-1, Vim_0..n-1]
        var idToIndex = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idToIndex[model.Buses[i].Id] = i;

        // D-DOC-109 (October 2026): every measurement is checked before any row is built, and every problem is named at
        // once. Before, nothing was checked: a voltage phasor at a bus the model does not have threw a raw
        // KeyNotFoundException from the bus lookup, and a sigma of 0, a negative or non-finite sigma, or a covariance
        // that does not make a positive-definite 2x2 error matrix went straight into the weights (1 / sigma^2 is
        // infinite at 0; a non-positive-definite covariance was silently replaced by the independent one).
        RequireUsable(idToIndex, voltages, currents);

        // Each phasor measurement contributes a real+imaginary row PAIR with a 2x2 error covariance
        // R = [[sigma^2, cov],[cov, sigma^2]]; the pair enters the normal equations weighted by R^-1, so a
        // correlated real/imaginary error is handled exactly (cov = 0 reduces to two independent rows).
        var pairs = new List<(double[] Hre, double[] Him, double Zre, double Zim, double A, double B, double D)>();

        foreach (PmuVoltageMeasurement v in voltages)
        {
            int k = idToIndex[v.BusId];
            var hRe = new double[cols]; hRe[k] = 1.0;
            var hIm = new double[cols]; hIm[n + k] = 1.0;
            (double a, double b, double d) = InverseSym2(v.Sigma * v.Sigma, ImagVariance(v.Sigma, v.SigmaIm), v.CovOffDiag);
            pairs.Add((hRe, hIm, v.Value.Real, v.Value.Imaginary, a, b, d));
        }

        foreach (PmuCurrentMeasurement c in currents)
        {
            (int f, int t, Complex yff, Complex yft) = ResolveBranch(model, idToIndex, c);
            // Ire = gff*Vre_f - bff*Vim_f + gft*Vre_t - bft*Vim_t
            var hRe = new double[cols];
            hRe[f] = yff.Real; hRe[n + f] = -yff.Imaginary; hRe[t] = yft.Real; hRe[n + t] = -yft.Imaginary;
            // Iim = bff*Vre_f + gff*Vim_f + bft*Vre_t + gft*Vim_t
            var hIm = new double[cols];
            hIm[f] = yff.Imaginary; hIm[n + f] = yff.Real; hIm[t] = yft.Imaginary; hIm[n + t] = yft.Real;
            (double a, double b, double d) = InverseSym2(c.Sigma * c.Sigma, ImagVariance(c.Sigma, c.SigmaIm), c.CovOffDiag);
            pairs.Add((hRe, hIm, c.Value.Real, c.Value.Imaginary, a, b, d));
        }

        // Normal equations G = sum H_pair^T R^-1 H_pair, g = sum H_pair^T R^-1 z_pair.
        var g = new double[cols, cols];
        var rhs = new double[cols];
        foreach ((double[] hre, double[] him, double zre, double zim, double a, double b, double d) in pairs)
        {
            for (int i = 0; i < cols; i++)
            {
                double hrei = hre[i], himi = him[i];
                if (hrei == 0.0 && himi == 0.0) continue;
                rhs[i] += hrei * (a * zre + b * zim) + himi * (b * zre + d * zim);
                for (int j = 0; j < cols; j++)
                    g[i, j] += a * hrei * hre[j] + b * (hrei * him[j] + himi * hre[j]) + d * himi * him[j];
            }
        }

        double[] x;
        try { x = DseLinAlg.MatVec(DseLinAlg.Invert(g, cols), rhs, cols); }
        catch (InvalidOperationException)
        {
            // GR-525: a singular gain (an unobserved bus) has no state. The arrays are NaN, not 0: zeros read as a blackout
            // state to a consumer that ignores Converged = false, NaN cannot be mistaken for one.
            var none = new double[n];
            Array.Fill(none, double.NaN);
            return new LinearPmuResult(false, model.Buses.Select(b => b.Id).ToArray(), none, (double[])none.Clone(), double.NaN);
        }

        var vm = new double[n];
        var vaDeg = new double[n];
        for (int i = 0; i < n; i++)
        {
            var vphasor = new Complex(x[i], x[n + i]);
            vm[i] = vphasor.Magnitude;
            vaDeg[i] = DetMath.Phase(vphasor) * 180.0 / Math.PI;
        }

        double obj = 0;
        foreach ((double[] hre, double[] him, double zre, double zim, double a, double b, double d) in pairs)
        {
            double hxRe = 0, hxIm = 0;
            for (int i = 0; i < cols; i++) { hxRe += hre[i] * x[i]; hxIm += him[i] * x[i]; }
            double rRe = zre - hxRe, rIm = zim - hxIm;
            obj += a * rRe * rRe + 2.0 * b * rRe * rIm + d * rIm * rIm; // r^T R^-1 r
        }

        return new LinearPmuResult(true, model.Buses.Select(b => b.Id).ToArray(), vm, vaDeg, obj);
    }

    /// <summary>The imaginary component's variance: <paramref name="sigmaIm"/> squared when given, else the real
    /// component's <paramref name="sigma"/> squared (exactly the old circular error).</summary>
    private static double ImagVariance(double sigma, double? sigmaIm) => sigmaIm is double si ? si * si : sigma * sigma;

    /// <summary>Inverse of the symmetric 2x2 error covariance [[varRe, cov],[cov, varIm]], returned as its (a, b, d)
    /// entries [[a, b],[b, d]]. With varRe == varIm (no SigmaIm) every operation is the one the circular form used
    /// (det = s2 * s2 - cov^2, a = d = s2 / det), so existing estimates are bit for bit unchanged (1.95.245).</summary>
    private static (double A, double B, double D) InverseSym2(double varRe, double varIm, double cov)
    {
        double det = varRe * varIm - cov * cov;
        // Unreachable since D-DOC-109: RequireUsable refuses a covariance with det <= 0 before any row is built. Kept as
        // a last line of defence; it used to be the silent "fall back to independent" path for such a covariance.
        if (det <= 0) return (1.0 / varRe, 0.0, 1.0 / varIm);
        return (varIm / det, -cov / det, varRe / det);
    }

    // D-DOC-109: refuse, naming every problem, a measurement the linear estimate cannot use as given:
    // - a voltage phasor at a bus the model does not have (it used to be a raw KeyNotFoundException);
    // - a sigma that is not a finite number above 0 (each rectangular component is weighted by 1 / sigma^2);
    // - a real / imaginary covariance that is not finite, or whose 2x2 error matrix [[s^2, c], [c, s^2]] is not positive
    //   definite (|c| >= s^2: its determinant s^4 - c^2 is 0 or negative, so it has no inverse to weight with);
    // - a phasor value that is not finite (it would turn the whole solve into NaN).
    // A current's branch is resolved, and refused by name when it is not an in-service model branch, by ResolveBranch.
    private static void RequireUsable(
        Dictionary<int, int> idToIndex,
        IReadOnlyList<PmuVoltageMeasurement> voltages,
        IReadOnlyList<PmuCurrentMeasurement> currents)
    {
        var problems = new List<string>();
        for (int k = 0; k < voltages.Count; k++)
        {
            PmuVoltageMeasurement v = voltages[k];
            string where = $"voltage phasor {k} (bus {v.BusId})";
            if (!idToIndex.ContainsKey(v.BusId)) problems.Add($"{where}: bus {v.BusId} is not a bus of the model");
            CheckErrorModel(where, v.Value, v.Sigma, v.SigmaIm, v.CovOffDiag, problems);
        }
        for (int k = 0; k < currents.Count; k++)
        {
            PmuCurrentMeasurement c = currents[k];
            string where = $"current phasor {k} (branch {c.FromBusId}->{c.ToBusId})";
            CheckErrorModel(where, c.Value, c.Sigma, c.SigmaIm, c.CovOffDiag, problems);
        }
        if (problems.Count > 0)
            throw new InvalidDataException("The linear PMU estimate cannot use these measurements: " + string.Join("; ", problems) + ".");
    }

    // The value, sigma and covariance checks shared by voltage and current phasors (see RequireUsable).
    private static void CheckErrorModel(string where, Complex value, double sigma, double? sigmaIm, double cov, List<string> problems)
    {
        if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: the value {value.Real} + j{value.Imaginary} is not finite"));
        if (!double.IsFinite(sigma) || sigma <= 0.0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: sigma is {sigma}; it must be a finite number above 0"));
            return;   // the covariance test below needs a usable sigma
        }
        if (sigmaIm is double si && (!double.IsFinite(si) || si <= 0.0))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: the imaginary sigma is {si}; it must be a finite number above 0"));
            return;
        }
        double varIm = ImagVariance(sigma, sigmaIm);
        if (!double.IsFinite(cov))
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{where}: the real / imaginary covariance is {cov}; it must be finite"));
        else if ((sigma * sigma) * varIm - cov * cov <= 0.0)   // the determinant InverseSym2 divides by
            problems.Add(sigmaIm is null
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{where}: the real / imaginary covariance {cov} is not smaller in size than sigma^2 = {sigma * sigma}, so the 2x2 error covariance is not positive definite")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{where}: the real / imaginary covariance {cov} is not smaller in size than sigma_re * sigma_im = {sigma * sigmaIm.Value}, so the 2x2 error covariance is not positive definite"));
    }

    private static (int F, int T, Complex Yff, Complex Yft) ResolveBranch(
        GridModel model, Dictionary<int, int> idToIndex, PmuCurrentMeasurement c)
    {
        Branch? branch = null;
        bool ambiguous = false;
        // 1.95.245: the ordinal circuit form "#k" (model branch index) of gridsim-measurements/1, used for unnamed
        // parallels (MeasurementSet.TryResolveBranch); before, "#k" matched no branch name and was refused.
        if (c.Circuit is not null && c.Circuit.StartsWith('#')
            && int.TryParse(c.Circuit.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal)
            && ordinal < model.Branches.Count && model.Branches[ordinal] is { } byOrdinal
            && byOrdinal.From == c.FromBusId && byOrdinal.To == c.ToBusId && byOrdinal.InService
            && !model.Branches.Any(b => string.Equals(b.Name, c.Circuit, StringComparison.Ordinal)))
            branch = byOrdinal;
        else foreach (Branch b in model.Branches)
        {
            if (b.From != c.FromBusId || b.To != c.ToBusId) continue;
            if (!b.InService) continue;   // a switched-out branch carries no current - never model its closed admittance
            if (c.Circuit is not null && !string.Equals(b.Name, c.Circuit, StringComparison.Ordinal)) continue;
            if (branch is null) branch = b;
            else if (c.Circuit is null) ambiguous = true;   // parallel circuits both match - need a Circuit to pick one
        }
        if (branch is null)
            throw new InvalidDataException($"PMU current references branch {c.FromBusId}->{c.ToBusId} which is not an in-service model branch.");
        if (ambiguous)
            throw new InvalidDataException($"PMU current for {c.FromBusId}->{c.ToBusId} matches parallel circuits; set Circuit to disambiguate.");

        // The same pi-model stamps as YBus.Build / MeasurementModel.
        Complex ys = Complex.One / new Complex(branch.R, branch.X);
        Complex bc = new(0, branch.B / 2.0);
        double tapA = branch.TapRatio <= 0 ? 1.0 : branch.TapRatio;
        Complex tap = DetMath.FromPolar(tapA, branch.PhaseShiftDeg * Math.PI / 180.0);
        Complex yff = (ys + bc) / (tapA * tapA);
        Complex yft = -ys / Complex.Conjugate(tap);
        return (idToIndex[branch.From], idToIndex[branch.To], yff, yft);
    }
}
