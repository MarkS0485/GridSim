namespace GridSim.Core.Estimation;

/// <summary>
/// Projection statistics (Mili-Cheniae-Rousseeuw): a masking-immune measure of how outlying each measurement
/// Jacobian row is among all rows, using a high-breakdown location/scale. Unlike the hat-matrix leverage
/// (which is computed against the possibly-contaminated fit and can itself be masked by multiple interacting
/// errors), PS looks only at the geometry of the rows in state space, so it cannot be masked.
///
/// NOT YET WIRED (R259, October 2026 review): this doc used to call PS "the leverage source for the Schweppe-Huber
/// GM estimator", but <c>EstimationMethod.SchweppeGm</c> uses the hat-matrix normalised residual from
/// <c>IWlsStep.NormalizedResiduals</c> (with the declared weights since 1.95.038), not PS. PS is a tested building
/// block kept for that wiring (the Mili weight a_i = min(1, (d / PS_i)^2) multiplying the Huber threshold); note
/// <see cref="Compute"/> is O(m^2 s) and needs a sparse projection before it can run at gb-full scale.
///
/// NOT READY FOR WIRING (N330, 1.95.247): this is the dense Gasko-Donoho form (median and MAD over every row's
/// projection). On a power-system Jacobian most rows share no column with a given direction, so most projections are 0
/// and the MAD is tiny: on case14 with the synthetic set every row's PS came out large (median 437 for P injections,
/// 10.5 for voltage magnitudes, maximum 5,960), so a_i = min(1, (d / PS_i)^2) down-weights almost every row by four
/// orders of magnitude. Wired into SchweppeGm as a trial it turned a clean case14 estimate from J = 86.33 (PASS) into
/// J = 365.50 (FAIL). Mili, Cheniae and Rousseeuw's sparse-matrix form (median and MAD over the nonzero projections
/// only) is the published remedy; it should be built and checked against a reference before PS is wired.
///
/// CF5 (N330): <see cref="ComputeSparse"/> is that sparse form (location and scale over the overlapping rows only),
/// validated against a hand-computed reference in Cf5ProjectionStatisticsTests. <see cref="Compute"/> is kept unchanged
/// (its existing tests pin it).
///
/// STILL NOT WIRED (CF5, measured 2026-10-11): the trial wiring (Mili's r_Si = r_i / (sigma_i a_i) with
/// a_i = min(1, (d_i / PS_i)^2), d_i = sqrt of the chi-square 0.975 quantile at the row's nonzero count) was re-run with
/// the sparse form on the same clean set (estimate case14 --synth --seed 3 --method schweppe-gm): hat leverage
/// J = 86.34 vs chi2(95 %) 118.75 PASS (35 iterations, max |dVa| 0.1150 deg); sparse PS J = 509.80 FAIL (115 iterations,
/// max |dVa| 0.1295 deg). The sparse form cut the PS by an order of magnitude (P injection median 437 -> 30.31) but
/// the overlap sets still mix rows of very different scale (|V| rows with entries near 1 against injection rows near
/// the bus self-susceptance, tens of pu), so every injection row is a leverage point: median a_i 0.0217 for P
/// injections (14 rows, d 4.187), 0.0669 for Q injections, 0.181 / 0.278 for P / Q flows, 1 for |V|. A robust
/// estimator must not reject clean data, so SchweppeGm keeps the hat-matrix leverage (bit for bit unchanged). The next
/// step would be the [VERIFY] above (the published estimator may scale each direction differently) and a row
/// normalisation decision; Cf5ProjectionStatisticsTests records the per-kind weights so the reason stays visible.
/// </summary>
internal static class ProjectionStatistics
{
    /// <summary>Consistency constant of the median absolute deviation for a normal distribution: 1/Phi^-1(3/4)
    /// = 1.4826, so 1.4826 x MAD estimates the standard deviation (R174).</summary>
    internal const double MadNormalConsistency = 1.4826;

    /// <summary>A projection direction (or a MAD) at or below this is degenerate and skipped.</summary>
    internal const double DegenerateScale = 1e-15;

    /// <summary>The projection statistic PS_i of each row: the max over the m directions (each row to the
    /// coordinate-wise median) of its robustly-standardized projection. A well-placed row has PS ~ a few; a
    /// leverage point has PS &gt;&gt; that.</summary>
    public static double[] Compute(IReadOnlyList<double[]> points)
    {
        int m = points.Count;
        if (m == 0) return Array.Empty<double>();
        int s = points[0].Length;

        // Coordinate-wise (L1) median of the point cloud.
        var median = new double[s];
        var col = new double[m];
        for (int c = 0; c < s; c++)
        {
            for (int j = 0; j < m; j++) col[j] = points[j][c];
            median[c] = Median(col);
        }

        var ps = new double[m];
        var t = new double[m];
        var dev = new double[m];
        for (int i = 0; i < m; i++)
        {
            // Direction u = (p_i - median), normalised.
            var u = new double[s];
            double norm = 0;
            for (int c = 0; c < s; c++) { u[c] = points[i][c] - median[c]; norm += u[c] * u[c]; }
            norm = Math.Sqrt(norm);
            if (norm <= DegenerateScale) continue;
            for (int c = 0; c < s; c++) u[c] /= norm;

            // Project every point onto u, then robustly standardise.
            for (int j = 0; j < m; j++)
            {
                double p = 0;
                for (int c = 0; c < s; c++) p += points[j][c] * u[c];
                t[j] = p;
            }
            double medT = Median((double[])t.Clone());
            for (int j = 0; j < m; j++) dev[j] = Math.Abs(t[j] - medT);
            double mad = MadNormalConsistency * Median((double[])dev.Clone());
            if (mad <= DegenerateScale) continue;

            for (int j = 0; j < m; j++)
            {
                double val = Math.Abs(t[j] - medT) / mad;
                if (val > ps[j]) ps[j] = val;
            }
        }
        return ps;
    }

    /// <summary>
    /// N330 (CF5): the SPARSE projection statistics of Mili, Cheniae, Vichare and Rousseeuw ("Robust state estimation
    /// based on projection statistics", IEEE Trans. Power Systems 11(2), 1996). The directions are the same as
    /// <see cref="Compute"/> (each row minus the coordinate-wise median of all rows, normalised; on a sparse Jacobian
    /// the coordinate median is 0 in every column, so the direction is the row itself), but each direction's location
    /// and scale are taken ONLY over the rows whose support meets the direction's support, that is over the
    /// structurally nonzero projections. A row that shares no column with the direction projects to exactly 0 and says
    /// nothing about how far out the overlapping rows are, so letting it into the median and the MAD (the dense form)
    /// shrinks the scale to almost nothing and makes every overlapping row look extreme (N330: case14 median PS 437 for
    /// P injections). Rows outside a direction's overlap take no value from that direction.
    ///
    /// For each direction i: t_j = row_j . u_i over the overlap set S_i; location med_i = median of t over S_i
    /// (mean of the two middle values for an even count); scale MAD_i = 1.4826 x median over S_i of |t_j - med_i|;
    /// P_ij = |t_j - med_i| / MAD_i for j in S_i. PS_j = max over the directions whose overlap holds j. A direction with
    /// a degenerate norm or MAD (at or below <see cref="DegenerateScale"/>) is skipped, as in the dense form.
    ///
    /// [VERIFY] The papers are not in docs/reference. The location / scale pair (plain median and the 1.4826 MAD, rather
    /// than a low median or a small-sample corrected scale) follows the N330 finding's description "median / MAD over
    /// nonzero projections"; confirm the exact estimator against the 1996 paper before treating the PS values as the
    /// published ones. The hand-computed reference in Cf5ProjectionStatisticsTests pins this implementation's rule.
    ///
    /// Serial, in row order, plain IEEE arithmetic (sums in a fixed column order, sorts of copies): bit-identical on
    /// every tier. Cost O(m^2 s) like the dense form; callers bound the size.
    /// </summary>
    public static double[] ComputeSparse(IReadOnlyList<double[]> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        int m = points.Count;
        if (m == 0) return Array.Empty<double>();
        int s = points[0]?.Length ?? throw new ArgumentException("row 0 is null.", nameof(points));
        for (int j = 0; j < m; j++)
        {
            // Fail loud on a ragged or non-finite matrix: a NaN projection would sort unpredictably and poison the median.
            double[] row = points[j] ?? throw new ArgumentException($"row {j} is null.", nameof(points));
            if (row.Length != s)
                throw new ArgumentException($"row {j} has {row.Length} entries; row 0 has {s}. Every row must have the same length.", nameof(points));
            for (int c = 0; c < s; c++)
                if (!double.IsFinite(row[c]))
                    throw new ArgumentException($"row {j} column {c} is {row[c]}; projection statistics need finite rows.", nameof(points));
        }

        // Coordinate-wise median of all rows (zeros included), exactly as the dense form: the directions are unchanged.
        var median = new double[s];
        var col = new double[m];
        for (int c = 0; c < s; c++)
        {
            for (int j = 0; j < m; j++) col[j] = points[j][c];
            median[c] = Median(col);
        }

        var ps = new double[m];
        var u = new double[s];
        var members = new List<int>(m);
        var t = new List<double>(m);
        for (int i = 0; i < m; i++)
        {
            // Direction u = (p_i - median), normalised.
            double norm = 0;
            for (int c = 0; c < s; c++) { u[c] = points[i][c] - median[c]; norm += u[c] * u[c]; }
            norm = Math.Sqrt(norm);
            if (norm <= DegenerateScale) continue;
            for (int c = 0; c < s; c++) u[c] /= norm;

            // The overlap set: rows with at least one column where both the row and the direction are nonzero.
            // Structural, not numerical: a row whose overlapping terms happen to cancel to 0 is still a member (its
            // projection is a real value of this direction, not an absent one).
            members.Clear();
            t.Clear();
            for (int j = 0; j < m; j++)
            {
                double[] row = points[j];
                bool overlaps = false;
                double p = 0;
                for (int c = 0; c < s; c++)
                {
                    if (u[c] == 0.0 || row[c] == 0.0) continue;
                    overlaps = true;
                    p += row[c] * u[c];
                }
                if (!overlaps) continue;
                members.Add(j);
                t.Add(p);
            }
            if (members.Count == 0) continue;   // cannot happen (row i overlaps itself), but never take a median of nothing

            double[] tArr = t.ToArray();
            double medT = Median((double[])tArr.Clone());
            var dev = new double[tArr.Length];
            for (int k = 0; k < tArr.Length; k++) dev[k] = Math.Abs(tArr[k] - medT);
            double mad = MadNormalConsistency * Median((double[])dev.Clone());
            if (mad <= DegenerateScale) continue;

            for (int k = 0; k < tArr.Length; k++)
            {
                double val = dev[k] / mad;
                int j = members[k];
                if (val > ps[j]) ps[j] = val;
            }
        }
        return ps;
    }

    /// <summary>The Schweppe leverage weight a_i = min(1, (d / PS_i)^2) with cutoff d - 1 for a normal row,
    /// small for a leverage point.</summary>
    public static double LeverageWeight(double ps, double cutoff)
        => ps <= cutoff ? 1.0 : (cutoff / ps) * (cutoff / ps);

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int n = values.Length;
        return n % 2 == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
    }
}
