namespace GridSim.Core.Numerics;

/// <summary>
/// A minimal, deterministic Nelder-Mead downhill-simplex minimiser for smooth low-dimensional objectives
/// (no derivatives, no external dependency). GridSim's other "simplex" is the linear-programming
/// <see cref="BoundedSimplex"/>; this is the unrelated nonlinear-optimisation simplex, used where a handful
/// of physical constants are fitted to a measured curve (e.g. the frequency nadir/recovery constants).
///
/// <para>Deterministic by construction: the initial simplex is the seed plus a fixed per-coordinate step,
/// no randomness, so a fit is reproducible. Bounds are enforced by the caller inside the objective (return
/// a large finite penalty outside the feasible box) - the classic Nelder-Mead has no native bound handling
/// and reflecting through a hard wall is less robust than penalising.</para>
/// </summary>
public static class NelderMead
{
    public sealed record Options
    {
        /// <summary>Fractional (or absolute, for near-zero coords) initial step per coordinate.</summary>
        public double InitialStep { get; init; } = 0.1;
        public int MaxIterations { get; init; } = 2000;
        /// <summary>Converged when the simplex spread (max vertex-to-best distance) and the function
        /// spread both fall below their tolerances.</summary>
        public double XTolerance { get; init; } = 1e-8;
        public double FTolerance { get; init; } = 1e-12;
    }

    public sealed record Result(double[] X, double Value, int Iterations, bool Converged);

    /// <summary>Minimise <paramref name="f"/> starting from <paramref name="seed"/>.</summary>
    public static Result Minimize(Func<double[], double> f, double[] seed, Options? options = null)
    {
        Options opt = options ?? new Options();
        int n = seed.Length;
        if (n == 0) return new Result(System.Array.Empty<double>(), f(seed), 0, true);

        // Standard coefficients: reflect, expand, contract, shrink.
        const double alpha = 1.0, gamma = 2.0, rho = 0.5, sigma = 0.5;

        // Build the initial simplex (n+1 vertices): seed, plus seed perturbed along each axis.
        var simplex = new double[n + 1][];
        var fval = new double[n + 1];
        simplex[0] = (double[])seed.Clone();
        for (int i = 0; i < n; i++)
        {
            var v = (double[])seed.Clone();
            double step = opt.InitialStep * (System.Math.Abs(seed[i]) > 1e-12 ? System.Math.Abs(seed[i]) : 1.0);
            v[i] += step;
            simplex[i + 1] = v;
        }
        for (int i = 0; i <= n; i++) fval[i] = f(simplex[i]);

        int iter = 0;
        for (; iter < opt.MaxIterations; iter++)
        {
            // Order vertices by ascending value (insertion sort - n is tiny).
            SortByValue(simplex, fval);

            // Convergence: both the simplex geometry and the objective have collapsed.
            double fSpread = System.Math.Abs(fval[n] - fval[0]);
            double xSpread = 0.0;
            for (int i = 1; i <= n; i++)
            {
                double d = 0;
                for (int j = 0; j < n; j++) { double e = simplex[i][j] - simplex[0][j]; d += e * e; }
                xSpread = System.Math.Max(xSpread, System.Math.Sqrt(d));
            }
            if (fSpread <= opt.FTolerance && xSpread <= opt.XTolerance)
                return new Result(simplex[0], fval[0], iter, true);

            // Centroid of all but the worst vertex.
            var centroid = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = 0; j < n; j++) s += simplex[j][i];
                centroid[i] = s / n;
            }

            // Reflection: xr = centroid + alpha*(centroid - worst) = Combine(centroid, worst, -alpha).
            double[] xr = Combine(centroid, simplex[n], -alpha);
            double fr = f(xr);
            if (fr < fval[0])
            {
                // Expansion: xe = centroid + gamma*(xr - centroid).
                double[] xe = Combine(centroid, xr, gamma);
                double fe = f(xe);
                if (fe < fr) { simplex[n] = xe; fval[n] = fe; } else { simplex[n] = xr; fval[n] = fr; }
                continue;
            }
            if (fr < fval[n - 1]) { simplex[n] = xr; fval[n] = fr; continue; } // accept reflection

            // Contraction (outside if reflection improved on worst, else inside).
            double[] xc = fr < fval[n]
                ? Combine(centroid, xr, rho)          // outside contraction toward reflected
                : Combine(centroid, simplex[n], rho); // inside contraction toward worst
            double fc = f(xc);
            if (fc < System.Math.Min(fr, fval[n])) { simplex[n] = xc; fval[n] = fc; continue; }

            // Shrink all but the best toward the best.
            for (int i = 1; i <= n; i++)
            {
                for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + sigma * (simplex[i][j] - simplex[0][j]);
                fval[i] = f(simplex[i]);
            }
        }

        SortByValue(simplex, fval);
        return new Result(simplex[0], fval[0], iter, false);
    }

    // centroid + coeff * (point - centroid)
    private static double[] Combine(double[] centroid, double[] point, double coeff)
    {
        var r = new double[centroid.Length];
        for (int i = 0; i < centroid.Length; i++) r[i] = centroid[i] + coeff * (point[i] - centroid[i]);
        return r;
    }

    private static void SortByValue(double[][] simplex, double[] fval)
    {
        for (int i = 1; i < fval.Length; i++)
        {
            double fv = fval[i]; double[] sv = simplex[i];
            int j = i - 1;
            while (j >= 0 && fval[j] > fv) { fval[j + 1] = fval[j]; simplex[j + 1] = simplex[j]; j--; }
            fval[j + 1] = fv; simplex[j + 1] = sv;
        }
    }
}
