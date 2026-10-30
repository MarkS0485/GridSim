using GridSim.Core.Estimation.Dse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Estimation;

/// <summary>A measurement identified as bad by the hypothesis test, with its estimated error and test statistic.</summary>
/// <param name="Measurement">The offending measurement.</param>
/// <param name="EstimatedError">The jointly-estimated gross error, in the estimator's INTERNAL units of the measurement's
/// residual, not the measurement's own engineering units (N164; the old text said "engineering units ... pu"):
/// per-unit on the system base for the power kinds (<see cref="MeasurementKind.PInjMw"/>,
/// <see cref="MeasurementKind.QInjMvar"/>, <see cref="MeasurementKind.PFlowMw"/>, <see cref="MeasurementKind.QFlowMvar"/>:
/// multiply by <see cref="GridModel.BaseMva"/> for MW / MVAr), per-unit for <see cref="MeasurementKind.VmPu"/> and
/// <see cref="MeasurementKind.IMagPu"/>, and RADIANS for the angle kinds (<see cref="MeasurementKind.VaDeg"/>,
/// <see cref="MeasurementKind.IAngDeg"/>: multiply by 180 / pi for degrees). <c>HtiBadDataEstimation</c>'s
/// correct-in-place path converts it to measurement units exactly this way before subtracting it from the value.</param>
/// <param name="TestStatistic">|error| / sqrt(error variance); above the threshold means bad.</param>
public sealed record HtiIdentified(Measurement Measurement, double EstimatedError, double TestStatistic);

/// <summary>
/// Hypothesis-Testing Identification of bad data. The sequential largest-normalized-residual loop removes one
/// measurement at a time and can mis-identify when several errors <b>interact</b> (their residuals smear so the
/// largest normalized residual points at a good measurement). HTI instead forms a joint estimate of the errors
/// over a suspect set and applies a per-suspect hypothesis test: e^ = S_SS^-1 r_SS with S = I - H G^-1 H^T W,
/// then t_i = |e^_i| / sqrt(Cov_ii). A suspect with t_i above the threshold is bad; one below is exonerated even
/// if its raw normalized residual crossed the suspect threshold. Dense; runs on a converged estimate.
/// </summary>
public static class HypothesisTestingBadData
{
    /// <summary>Identify bad data in <paramref name="set"/> given the converged <paramref name="estimate"/>.</summary>
    /// <param name="hardConstraints">True when the estimate came from a method that enforces
    /// <c>virtual-zero-injection</c> rows as HARD equalities (<see cref="EstimationMethod.Constrained"/>,
    /// <see cref="EstimationMethod.Feasible"/>). Those rows are network facts (a switching node has no device), not
    /// telemetry, so they must never be identified as bad and removed. With this on they are treated exactly as the
    /// constrained backend's own LNR path treats them (<c>ConstrainedDenseWlsStep.NormalizedResiduals</c>): in the gain
    /// and the residual covariance they carry the shared relative pseudo-weight
    /// <see cref="NumericTolerances.HardConstraintWeightScale"/> x the strongest free weight, and they are excluded from
    /// the suspect set outright. Before October 2026 Identify built a plain gain at the rows' own sigma and could put
    /// a hard constraint in the suspect set, so <c>--hti --method constrained/feasible</c> could remove a constraint
    /// that LNR marks critical. False (the default) keeps plain-WLS behaviour, where such rows are soft
    /// pseudo-measurements like any other.</param>
    public static IReadOnlyList<HtiIdentified> Identify(
        GridModel model, MeasurementSet set, EstimationResult estimate,
        double suspectThreshold = 3.0, double testThreshold = 3.0, bool hardConstraints = false)
    {
        // GR-522: identification is defined against a converged estimate of THIS model (its buses in the model's
        // order). A start state or another model's estimate used to be "identified" against without a word.
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(estimate);
        if (!estimate.Converged)
            throw new ArgumentException($"HTI: the estimate did not converge ({estimate.Status}); there is no state to test against.", nameof(estimate));
        if (estimate.BusIds.Count != model.BusCount || estimate.Vm.Length != model.BusCount || estimate.VaDeg.Length != model.BusCount)
            throw new ArgumentException($"HTI: the estimate has {estimate.BusIds.Count} buses; the model has {model.BusCount}.", nameof(estimate));
        for (int i = 0; i < model.BusCount; i++)
            if (estimate.BusIds[i] != model.Buses[i].Id)
                throw new ArgumentException($"HTI: estimate bus {i} is id {estimate.BusIds[i]}, the model's is {model.Buses[i].Id}; the estimate is not of this model.", nameof(estimate));
        var mm = new MeasurementModel(model, set);
        int m = mm.MeasurementCount, s = mm.StateSize, n = model.BusCount;

        // Which rows are hard constraints (only meaningful when the method enforces them).
        var isConstraint = new bool[m];
        if (hardConstraints)
            for (int r = 0; r < m; r++)
                isConstraint[r] = string.Equals(mm.Measurements[r].Source, ConstrainedDenseWlsStep.ConstraintSource, StringComparison.Ordinal);

        var vm = new double[n];
        var va = new double[n];
        for (int i = 0; i < n; i++) { vm[i] = estimate.Vm[i]; va[i] = estimate.VaDeg[i] * Math.PI / 180.0; }

        var h = new double[m];
        mm.EvaluateH(vm, va, h);
        var jac = new double[m, s];
        mm.EvaluateJacobian(vm, va, jac);

        var w = new double[m];
        var sigma2 = new double[m];
        for (int r = 0; r < m; r++) { sigma2[r] = mm.SigmaPu[r] * mm.SigmaPu[r]; w[r] = 1.0 / sigma2[r]; }

        if (hardConstraints)
        {
            // The constrained backend's pseudo-weight: HardConstraintWeightScale x the strongest FREE weight (one
            // value for every constraint row; R142/R143). Its variance 1/w keeps the covariance algebra below
            // consistent (Omega for a constraint row is then ~0, i.e. critical, as LNR sees it).
            double maxFree = 0.0;
            bool anyFree = false;
            for (int r = 0; r < m; r++)
            {
                if (isConstraint[r]) continue;
                anyFree = true;
                if (w[r] > maxFree) maxFree = w[r];
            }
            if (!anyFree)
                throw new InvalidOperationException(
                    "HTI: every measurement is a virtual-zero-injection constraint, so there is no telemetry to test.");
            double constraintWeight = NumericTolerances.HardConstraintWeightScale * maxFree;
            for (int r = 0; r < m; r++)
                if (isConstraint[r]) { w[r] = constraintWeight; sigma2[r] = 1.0 / constraintWeight; }
        }

        // Gain G = H^T W H and its inverse (dense).
        var g = new double[s, s];
        for (int r = 0; r < m; r++)
            for (int i = 0; i < s; i++)
            {
                double hwi = jac[r, i] * w[r];
                if (hwi == 0.0) continue;
                for (int j = 0; j < s; j++) g[i, j] += hwi * jac[r, j];
            }
        double[,] gInv = DseLinAlg.Invert(g, s);

        double HGH(int a, int b) // h_a^T G^-1 h_b
        {
            double sum = 0;
            for (int i = 0; i < s; i++)
            {
                double yi = 0;
                for (int j = 0; j < s; j++) yi += gInv[i, j] * jac[b, j];
                sum += jac[a, i] * yi;
            }
            return sum;
        }

        var res = new double[m];
        for (int r = 0; r < m; r++) res[r] = mm.ZPu[r] - h[r];

        // Suspect set: normalized residual above the suspect threshold (excluding critical rows, Omega ~ 0).
        var suspects = new List<int>();
        for (int r = 0; r < m; r++)
        {
            if (isConstraint[r]) continue;   // a hard constraint is a network fact, never removable telemetry
            double omega = sigma2[r] - HGH(r, r);
            if (omega <= sigma2[r] * BadDataAnalysis.CriticalFloor) continue; // critical - undetectable (R141: one shared floor)
            if (Math.Abs(res[r]) / Math.Sqrt(omega) > suspectThreshold) suspects.Add(r);
        }
        if (suspects.Count == 0) return Array.Empty<HtiIdentified>();

        int k = suspects.Count;
        var sMat = new double[k, k];   // S_SS = I - (H G^-1 H^T W)_SS
        var omegaMat = new double[k, k]; // Omega_SS = (W^-1)_SS - (H G^-1 H^T)_SS
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double hgh = HGH(suspects[a], suspects[b]);
                sMat[a, b] = (a == b ? 1.0 : 0.0) - hgh * w[suspects[b]];
                omegaMat[a, b] = (a == b ? sigma2[suspects[a]] : 0.0) - hgh;
            }

        double[,] sInv = DseLinAlg.Invert(sMat, k);
        var rSS = new double[k];
        for (int a = 0; a < k; a++) rSS[a] = res[suspects[a]];
        double[] eHat = DseLinAlg.MatVec(sInv, rSS, k);

        // Cov = S^-1 Omega S^-T ; we need the diagonal.
        var soi = new double[k, k]; // sInv * omegaMat
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double sum = 0;
                for (int t = 0; t < k; t++) sum += sInv[a, t] * omegaMat[t, b];
                soi[a, b] = sum;
            }

        var identified = new List<HtiIdentified>();
        for (int a = 0; a < k; a++)
        {
            double covaa = 0;
            for (int b = 0; b < k; b++) covaa += soi[a, b] * sInv[a, b]; // (S^-1 Omega S^-T)_aa
            double variance = covaa > 0 ? covaa : 0;
            double t = variance > 0 ? Math.Abs(eHat[a]) / Math.Sqrt(variance) : 0;
            if (t > testThreshold)
                identified.Add(new HtiIdentified(mm.Measurements[suspects[a]], eHat[a], t));
        }
        return identified;
    }
}
