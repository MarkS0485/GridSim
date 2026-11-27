using GridSim.Core.Analysis.Probabilistic;
using GridSim.Core.Model;
using GridSim.Core.Stochastic;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-09: Monte Carlo, quasi-Monte Carlo and convolution as selectable methods; probabilistic load flow; generation
/// adequacy. Every oracle is derived inside the test: explicit state enumeration for adequacy, Cramer's-rule PTDF on a
/// hand-reduced susceptance matrix with the closed form of a sum of normals, integer-parameter polynomial forms for the
/// incomplete beta, a hand Cholesky factor. No published reliability figure is used (none could be confirmed from a
/// citable source; see the R4-09 evidence).
/// </summary>
public sealed class ProbabilisticTests
{
    private static void Near(double expected, double actual, double tol, string what = "") =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what} expected {expected:R} +/- {tol:R} but was {actual:R}");

    // ================================================================================================================
    // Cholesky, normal CDF, special functions, distributions
    // ================================================================================================================

    [Fact]
    public void Cholesky_matches_a_hand_factor_and_refuses_bad_matrices()
    {
        // A = L L^T with L = [[2,0,0],[1,2,0],[1,1,2]] by hand: A = [[4,2,2],[2,5,3],[2,3,6]].
        var a = new double[,] { { 4, 2, 2 }, { 2, 5, 3 }, { 2, 3, 6 } };
        double[,] l = Cholesky.Factor(a);
        double[,] expect = { { 2, 0, 0 }, { 1, 2, 0 }, { 1, 1, 2 } };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.Equal(expect[i, j], l[i, j]);
        var y = new double[3];
        Cholesky.MultiplyLower(l, new double[] { 1, 1, 1 }, y);
        Assert.Equal(new double[] { 2, 3, 4 }, y);
        Assert.Throws<ArgumentException>(() => Cholesky.Factor(new double[,] { { 1, 2 }, { 2, 1 } }));        // indefinite
        Assert.Throws<ArgumentException>(() => Cholesky.Factor(new double[,] { { 1, 0.5 }, { 0.4, 1 } }));    // not symmetric
        Assert.Throws<ArgumentException>(() => Cholesky.Factor(new double[,] { { 1, double.NaN }, { double.NaN, 1 } }));
        Assert.Throws<ArgumentException>(() => Cholesky.Factor(new double[2, 3]));
    }

    [Fact]
    public void Gaussian_cdf_is_symmetric_continuous_at_the_branch_and_inverts_the_quantile()
    {
        Near(0.5, GaussianCdf.Cdf(0.0), 1e-16);
        Near(0.975, GaussianCdf.Cdf(1.959963984540054), 1e-14);
        Near(0.8413447460685429, GaussianCdf.Cdf(1.0), 1e-14);
        for (double z = -7; z <= 7; z += 0.25)
        {
            Near(1.0, GaussianCdf.Cdf(z) + GaussianCdf.Cdf(-z), 1e-15, "symmetry " + z);
            double p = GaussianCdf.Cdf(z);
            if (z <= 5.5 && p > 1e-300)
            {
                double back = GaussianInverse.Quantile(p);
                Assert.True(Math.Abs(back - z) <= 1e-7 * Math.Max(1.0, Math.Abs(z)), $"round trip {z}: {back:R}");
            }
        }
        Near(GaussianCdf.Cdf(3.0 - 1e-12), GaussianCdf.Cdf(3.0 + 1e-12), 1e-11);   // series meets the continued fraction
        Near(GaussianCdf.Cdf(-3.0 - 1e-12), GaussianCdf.Cdf(-3.0 + 1e-12), 1e-11);
        Assert.Throws<ArgumentOutOfRangeException>(() => GaussianCdf.Cdf(double.NaN));
    }

    [Fact]
    public void Gamma_function_matches_integers_and_one_half()
    {
        double fact = 1.0;
        for (int n = 1; n <= 15; n++)
        {
            Assert.True(Math.Abs(SpecialFunctions.Gamma(n) - fact) <= 1e-12 * fact, $"Gamma({n})");   // Gamma(n) = (n-1)!
            fact *= n;
        }
        Near(Math.Sqrt(Math.PI), SpecialFunctions.Gamma(0.5), 1e-14);
        Near(Math.Sqrt(Math.PI) / 2.0, SpecialFunctions.Gamma(1.5), 1e-14);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpecialFunctions.LogGamma(0.0));
    }

    private static double BinomialTailBeta(int a, int b, double x)
    {
        // For integer a, b: I_x(a, b) = sum_{j=a}^{a+b-1} C(a+b-1, j) x^j (1-x)^(a+b-1-j) (the binomial tail identity).
        int n = a + b - 1;
        double s = 0.0;
        for (int j = a; j <= n; j++)
        {
            double c = 1.0;
            for (int i = 1; i <= j; i++) c *= (double)(n - j + i) / i;
            s += c * Math.Pow(x, j) * Math.Pow(1 - x, n - j);
        }
        return s;
    }

    [Fact]
    public void Incomplete_beta_matches_the_binomial_tail_polynomial_and_the_arcsine_law()
    {
        foreach ((int a, int b) in new[] { (1, 1), (2, 1), (1, 2), (2, 2), (3, 2), (5, 7), (10, 3) })
            foreach (double x in new[] { 0.01, 0.2, 0.5, 0.77, 0.99 })
                Near(BinomialTailBeta(a, b, x), SpecialFunctions.RegularisedIncompleteBeta(a, b, x), 1e-12, $"I_{x}({a},{b})");
        // Beta(1/2, 1/2) is the arcsine law: F(x) = (2/pi) asin(sqrt(x)).
        foreach (double x in new[] { 0.05, 0.3, 0.5, 0.9 })
            Near(2.0 / Math.PI * Math.Asin(Math.Sqrt(x)), SpecialFunctions.RegularisedIncompleteBeta(0.5, 0.5, x), 1e-12);
    }

    [Fact]
    public void Distribution_quantile_inverts_cdf_and_moments_match_closed_forms()
    {
        UncertainDistribution[] laws =
        {
            new NormalDistribution(3, 2), new LogNormalDistribution(0.3, 0.4), new WeibullDistribution(2.5, 1.7),
            new BetaDistribution(2, 5), new BetaDistribution(0.7, 0.9, -1, 3),
        };
        foreach (UncertainDistribution d in laws)
        {
            foreach (double p in new[] { 0.001, 0.1, 0.5, 0.9, 0.999 })
                Near(p, d.Cdf(d.Quantile(p)), 1e-9, d.Kind + " p=" + p);
            // Mean and variance against the midpoint integral of the quantile function (E X = integral of Q(u) du).
            const int n = 20_000;
            double s = 0, s2 = 0;
            for (int i = 0; i < n; i++) { double q = d.Quantile((i + 0.5) / n); s += q; s2 += q * q; }
            double mean = s / n, var = s2 / n - mean * mean;
            Assert.True(Math.Abs(mean - d.Mean) <= 3e-3 * Math.Max(1.0, Math.Abs(d.Mean)), $"{d.Kind} mean {d.Mean} vs {mean}");
            Assert.True(Math.Abs(var - d.Variance) <= 5e-2 * d.Variance, $"{d.Kind} variance {d.Variance} vs {var}");
        }
        // Closed forms: Weibull shape 1 is exponential; Weibull shape 2 scale 1 has mean sqrt(pi)/2; Beta(2,1) has F = x^2.
        var exp = new WeibullDistribution(4.0, 1.0);
        Near(1.0 - Math.Exp(-0.5), exp.Cdf(2.0), 1e-14);
        Near(4.0, exp.Mean, 1e-12);
        Near(Math.Sqrt(Math.PI) / 2.0, new WeibullDistribution(1.0, 2.0).Mean, 1e-13);
        Near(0.36, new BetaDistribution(2, 1).Cdf(0.6), 1e-13);
        Near(Math.Sqrt(0.25), new BetaDistribution(2, 1).Quantile(0.25), 1e-12);
    }

    [Fact]
    public void Discrete_laws_and_refusals()
    {
        var d = new DiscreteDistribution(new[] { (5.0, 0.2), (1.0, 0.5), (3.0, 0.3), (3.0, 0.0) });
        Assert.Equal(new[] { 1.0, 3.0, 5.0 }, d.Atoms.Select(a => a.Value).ToArray());
        Assert.Equal(1.0, d.Quantile(0.5)); Assert.Equal(3.0, d.Quantile(0.51)); Assert.Equal(5.0, d.Quantile(0.99));
        Near(0.5 * 1 + 0.3 * 3 + 0.2 * 5, d.Mean, 1e-15);
        var t = new TwoStateDistribution(100, 0.9);
        Near(90.0, t.Mean, 1e-12);
        Near(900.0, t.Variance, 1e-9);   // 100^2 * 0.9 * 0.1
        Assert.Equal(2.0, new EmpiricalDistribution(new[] { 1.0, 2.0, 3.0 }).Mean);

        Assert.Throws<ArgumentException>(() => new NormalDistribution(0, 0));
        Assert.Throws<ArgumentException>(() => new NormalDistribution(double.NaN, 1));
        Assert.Throws<ArgumentException>(() => new NormalDistribution(0, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new WeibullDistribution(1, -2));
        Assert.Throws<ArgumentException>(() => new LogNormalDistribution(0, 0));
        Assert.Throws<ArgumentException>(() => new BetaDistribution(0, 1));
        Assert.Throws<ArgumentException>(() => new BetaDistribution(1, 1, 2, 2));
        Assert.Throws<ArgumentException>(() => new TwoStateDistribution(100, 1.1));
        Assert.Throws<ArgumentException>(() => new TwoStateDistribution(-1, 0.5));
        Assert.Throws<ArgumentException>(() => new DiscreteDistribution(new[] { (1.0, 0.5), (2.0, 0.4) }));
        Assert.Throws<ArgumentException>(() => new DiscreteDistribution(new[] { (1.0, -0.5), (2.0, 1.5) }));
        Assert.True(DomainRefusalMarked(() => { _ = new NormalDistribution(0, -1); }));
    }

    private static bool DomainRefusalMarked(Action a)
    {
        try { a(); }
        catch (Exception ex) { return GridSim.Core.Diagnostics.DomainRefusal.IsMarked(ex); }
        return false;
    }

    // ================================================================================================================
    // Capacity outage table and adequacy against explicit state enumeration
    // ================================================================================================================

    // The oracle: enumerate every joint state of the units, accumulate probability by available capacity, then evaluate
    // LOLP = P(cap < load) and EENS = E[(load - cap)^+] by direct summation over the states.
    private static (double Lolp, double Eens) Enumerate(IReadOnlyList<UncertainDistribution> units, double load)
    {
        double lolp = 0, eens = 0;
        var idx = new int[units.Count];
        while (true)
        {
            double p = 1, cap = 0;
            for (int u = 0; u < units.Count; u++) { p *= units[u].Atoms[idx[u]].Probability; cap += units[u].Atoms[idx[u]].Value; }
            if (cap < load) { lolp += p; eens += p * (load - cap); }
            int k = 0;
            while (k < units.Count && ++idx[k] == units[k].Atoms.Count) { idx[k] = 0; k++; }
            if (k == units.Count) break;
        }
        return (lolp, eens);
    }

    private static UncertainDistribution[] MixedUnits() => new UncertainDistribution[]
    {
        new TwoStateDistribution(10, 0.95), new TwoStateDistribution(20, 0.9), new TwoStateDistribution(20, 0.92),
        new TwoStateDistribution(50, 0.85), new TwoStateDistribution(50, 0.88), new TwoStateDistribution(100, 0.8),
        new DiscreteDistribution(new[] { (0.0, 0.05), (30.0, 0.15), (60.0, 0.8) }),   // a derated multi-state unit
    };

    [Fact]
    public void Capacity_outage_table_matches_state_enumeration_to_rounding()
    {
        UncertainDistribution[] units = MixedUnits();
        CapacityOutageTable table = CapacityOutageTable.Build(units);
        Assert.Equal(10.0, table.StepMw);   // gcd of 10, 20, 50, 100, 30, 60
        Assert.Equal(10 + 20 + 20 + 50 + 50 + 100 + 60, table.InstalledMw);
        Near(1.0, table.Levels.Sum(l => l.Probability), 1e-14);
        foreach (double load in new[] { 5.0, 10.0, 10.0001, 35.0, 120.0, 150.0, 200.0, 310.0, 311.0, 400.0 })
        {
            (double lolp, double eens) = Enumerate(units, load);
            Near(lolp, table.LossOfLoadProbability(load), 1e-14, "LOLP at " + load);
            Near(eens, table.ExpectedShortfallMw(load), 1e-12, "EENS at " + load);
        }
    }

    [Fact]
    public void Three_identical_units_give_lolp_0_028_by_hand_and_by_the_table()
    {
        // Closed form: loss of load at 150 MW needs at most one 100 MW unit up: 0.1^3 + 3 x 0.9 x 0.1^2 = 0.028.
        var units = Enumerable.Range(0, 3).Select(_ => (UncertainDistribution)new TwoStateDistribution(100, 0.9)).ToArray();
        CapacityOutageTable table = CapacityOutageTable.Build(units);
        Near(0.1 * 0.1 * 0.1 + 3 * 0.9 * 0.1 * 0.1, table.LossOfLoadProbability(150), 1e-15);
        Near(0.028, table.LossOfLoadProbability(150), 1e-15);
        // EENS by hand: 0 up (p .001) short 150; 1 up (p .027) short 50 => 0.15 + 1.35 = 1.5 MW.
        Near(0.001 * 150 + 0.027 * 50, table.ExpectedShortfallMw(150), 1e-14);
    }

    [Fact]
    public void Off_lattice_capacities_make_convolution_out_of_scope_not_approximate()
    {
        var units = new[] { AdequacyUnit.TwoState("a", 100.5, 0.9), AdequacyUnit.TwoState("b", 50, 0.9) };
        AdequacyResult r = GenerationAdequacy.Run(new AdequacyRequest(units, new[] { 120.0 }, ProbabilisticMethod.Convolution, 1, null, 1));
        Assert.Equal(ProbabilisticStatus.OutOfScope, r.Status);
        Assert.Contains("whole number of MW", r.Reason, StringComparison.Ordinal);
        // With an explicit step that divides every capacity it is exact again.
        AdequacyResult ok = GenerationAdequacy.Run(new AdequacyRequest(units, new[] { 120.0 }, ProbabilisticMethod.Convolution, 1, null, 1, StepMw: 0.5));
        Assert.Equal(ProbabilisticStatus.Complete, ok.Status);
        Near(Enumerate(units.Select(u => u.Capacity).ToArray(), 120.0).Lolp, ok.Lole, 1e-15);
        // A step that does not divide a capacity stays out of scope.
        Assert.Equal(ProbabilisticStatus.OutOfScope, GenerationAdequacy.Run(new AdequacyRequest(units, new[] { 120.0 }, ProbabilisticMethod.Convolution, 1, null, 1, StepMw: 0.4)).Status);
    }

    private static double[] DayLoad() =>
        Enumerable.Range(0, 24).Select(h => 120.0 + 70.0 * Math.Sin(Math.PI * h / 23.0) + 5.0 * (h % 3)).ToArray();

    [Fact]
    public void Adequacy_convolution_equals_summed_enumeration_over_the_hourly_load()
    {
        UncertainDistribution[] laws = MixedUnits();
        var units = laws.Select((l, i) => new AdequacyUnit("u" + i, l)).ToArray();
        double[] loads = DayLoad();
        AdequacyResult r = GenerationAdequacy.Run(new AdequacyRequest(units, loads, ProbabilisticMethod.Convolution, 1, null, 7));
        double lole = 0, eens = 0;
        foreach (double l in loads) { (double p, double e) = Enumerate(laws, l); lole += p; eens += e; }
        Assert.Equal(ProbabilisticStatus.Complete, r.Status);
        Near(lole, r.Lole, 1e-13, "LOLE"); Near(eens, r.Eens, 1e-11, "EENS");
        Near(lole / 24.0, r.Lolp, 1e-14);
        Assert.Null(r.LoleStdError);
        Assert.Contains("no sampling error", r.ErrorModel, StringComparison.Ordinal);
        Assert.False(r.Provenance!.RandomNumbersUsed);
    }

    [Fact]
    public void Adequacy_monte_carlo_converges_to_the_exact_value_with_its_stated_error()
    {
        var units = Enumerable.Range(0, 3).Select(i => AdequacyUnit.TwoState("g" + i, 100, 0.9)).ToArray();
        var load = new[] { 150.0 };
        const int n = 400_000;
        AdequacyResult r = GenerationAdequacy.Run(new AdequacyRequest(units, load, ProbabilisticMethod.MonteCarlo, n, null, 20261117));
        double se = Math.Sqrt(0.028 * 0.972 / n);
        Near(se, r.LoleStdError!.Value, 0.03 * se, "stated SE");
        Assert.True(Math.Abs(r.Lole - 0.028) <= 4 * r.LoleStdError!.Value, $"LOLP {r.Lole:R} vs 0.028, SE {r.LoleStdError}");
        // EENS exact 1.5 MWh (hand value above), variance from the two shortfall levels.
        Assert.True(Math.Abs(r.Eens - 0.15 - 1.35) <= 4 * r.EensStdError!.Value);
        Assert.Contains("Monte Carlo standard error", r.ErrorModel, StringComparison.Ordinal);
    }

    private static double RmsErrorOverSeeds(Func<ulong, double> estimate, double exact, int seeds)
    {
        double s = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++) { double e = estimate(seed) - exact; s += e * e; }
        return Math.Sqrt(s / seeds);
    }

    [Fact]
    public void Monte_carlo_error_falls_as_one_over_root_n()
    {
        var units = Enumerable.Range(0, 3).Select(i => AdequacyUnit.TwoState("g" + i, 100, 0.9)).ToArray();
        double Est(int n, ulong seed) => GenerationAdequacy.Run(new AdequacyRequest(units, new[] { 150.0 }, ProbabilisticMethod.MonteCarlo, n, null, seed)).Lole;
        double small = RmsErrorOverSeeds(s => Est(400, s), 0.028, 300);
        double large = RmsErrorOverSeeds(s => Est(6400, s), 0.028, 300);
        // 16 times the samples: 4 times smaller RMS error (each RMS carries about 4 per cent noise from 300 seeds).
        double ratio = small / large;
        Assert.InRange(ratio, 3.2, 5.0);
        Near(Math.Sqrt(0.028 * 0.972 / 400), small, 0.15 * small, "RMS error at N=400 against the binomial standard error");
    }

    [Fact]
    public void Quasi_monte_carlo_beats_monte_carlo_on_a_many_unit_adequacy_study()
    {
        // Eight units, 5 per cent forced outage rate, 12 MW each (96 MW installed), an 80 MW load: LOLP is the chance that
        // two or more are down (one down leaves 84 MW, two leave 72 MW). The exact value is the binomial tail, derived in the test.
        double exact = 0;
        for (int k = 2; k <= 8; k++) exact += Choose(8, k) * Math.Pow(0.05, k) * Math.Pow(0.95, 8 - k);
        var units = Enumerable.Range(0, 8).Select(i => AdequacyUnit.TwoState("g" + i, 12, 0.95)).ToArray();
        var load = new[] { 80.0 };
        Near(exact, GenerationAdequacy.Run(new AdequacyRequest(units, load, ProbabilisticMethod.Convolution, 1, null, 1)).Lole, 1e-15);
        double Mc(ulong s) => GenerationAdequacy.Run(new AdequacyRequest(units, load, ProbabilisticMethod.MonteCarlo, 4096, null, s)).Lole;
        double Qmc(ulong s) => GenerationAdequacy.Run(new AdequacyRequest(units, load, ProbabilisticMethod.QuasiMonteCarloShifted, 1024, 4, s)).Lole;
        double mc = RmsErrorOverSeeds(Mc, exact, 60);
        double qmc = RmsErrorOverSeeds(Qmc, exact, 60);
        Assert.True(qmc < mc, $"QMC rms {qmc:R} should be below MC rms {mc:R} at equal samples (4096)");
    }

    private static double Choose(int n, int k)
    {
        double c = 1;
        for (int i = 1; i <= k; i++) c *= (double)(n - k + i) / i;
        return c;
    }

    [Fact]
    public void Qmc_integral_of_x1_x2_is_a_quarter_and_beats_the_monte_carlo_standard_error()
    {
        // Var(x1 x2) = E[x1^2] E[x2^2] - (1/4)^2 = 1/9 - 1/16 = 7/144, so the MC standard error at N is sqrt(7/144 / N).
        const int n = 1 << 14;
        double mcSe = Math.Sqrt(7.0 / 144.0 / n);
        ExpectationEstimate q = SampledExpectation.Estimate(ProbabilisticMethod.QuasiMonteCarlo, 2, n, null, 0, "integral", u => u[0] * u[1]);
        double qmcErr = Math.Abs(q.Mean - 0.25);
        Assert.True(qmcErr < mcSe / 10.0, $"Sobol error {qmcErr:E3} vs MC standard error {mcSe:E3}");
        Assert.Null(q.StdError);
        Assert.Contains("no error bar", q.ErrorModel, StringComparison.Ordinal);
        ExpectationEstimate mc = SampledExpectation.Estimate(ProbabilisticMethod.MonteCarlo, 2, n, null, 99, "integral", u => u[0] * u[1]);
        Near(mcSe, mc.StdError!.Value, 0.03 * mcSe);
        Assert.True(Math.Abs(mc.Mean - 0.25) <= 4 * mc.StdError!.Value);
        ExpectationEstimate sh = SampledExpectation.Estimate(ProbabilisticMethod.QuasiMonteCarloShifted, 2, 1 << 10, 8, 5, "integral", u => u[0] * u[1]);
        Assert.True(Math.Abs(sh.Mean - 0.25) <= 4 * sh.StdError!.Value + 1e-4, $"shifted {sh.Mean:R} SE {sh.StdError}");
        Assert.Equal(8, sh.ReplicateMeans.Length);
    }

    [Fact]
    public void Sampled_expectation_is_bit_identical_for_any_thread_count_and_seed_sensitive()
    {
        foreach (ProbabilisticMethod m in new[] { ProbabilisticMethod.MonteCarlo, ProbabilisticMethod.QuasiMonteCarlo, ProbabilisticMethod.QuasiMonteCarloShifted })
        {
            double Run(ulong seed, int threads) => SampledExpectation.Estimate(m, 3, 5000, m == ProbabilisticMethod.QuasiMonteCarloShifted ? 3 : null,
                seed, "rep", u => Math.Sin(u[0] * 3) * u[1] + u[2] * u[2], threads).Mean;
            long a = BitConverter.DoubleToInt64Bits(Run(4, 1));
            Assert.Equal(a, BitConverter.DoubleToInt64Bits(Run(4, 4)));
            Assert.Equal(a, BitConverter.DoubleToInt64Bits(Run(4, 16)));
            Assert.Equal(a, BitConverter.DoubleToInt64Bits(Run(4, 0)));
            if (m == ProbabilisticMethod.QuasiMonteCarlo) Assert.Equal(a, BitConverter.DoubleToInt64Bits(Run(5, 1)));   // no seed in a bare point set
            else Assert.NotEqual(a, BitConverter.DoubleToInt64Bits(Run(5, 1)));
        }
    }

    [Fact]
    public void Adequacy_results_are_bit_repeatable_across_thread_counts_and_seed_sensitive()
    {
        var units = MixedUnits().Select((l, i) => new AdequacyUnit("u" + i, l)).ToArray();
        double[] loads = DayLoad();
        foreach (ProbabilisticMethod m in new[] { ProbabilisticMethod.MonteCarlo, ProbabilisticMethod.QuasiMonteCarlo, ProbabilisticMethod.QuasiMonteCarloShifted })
        {
            AdequacyResult Run(ulong seed, int threads) => GenerationAdequacy.Run(new AdequacyRequest(units, loads, m, 4096,
                m == ProbabilisticMethod.QuasiMonteCarloShifted ? 3 : null, seed, MaxThreads: threads));
            AdequacyResult one = Run(11, 1);
            foreach (int t in new[] { 4, 16 })
            {
                AdequacyResult many = Run(11, t);
                Assert.Equal(one.Provenance!.ResultDigest, many.Provenance!.ResultDigest);
                Assert.Equal(one.Provenance.SampleDigest, many.Provenance.SampleDigest);
            }
            AdequacyResult other = Run(12, 1);
            if (m == ProbabilisticMethod.QuasiMonteCarlo) Assert.Equal(one.Provenance!.SampleDigest, other.Provenance!.SampleDigest);
            else Assert.NotEqual(one.Provenance!.SampleDigest, other.Provenance!.SampleDigest);
            Assert.Equal("gridsim-rng/1", one.Provenance.GeneratorVersion);
        }
    }

    [Fact]
    public void Adequacy_refusals_missing_seed_bad_input_and_qmc_dimension()
    {
        var u3 = Enumerable.Range(0, 3).Select(i => AdequacyUnit.TwoState("g" + i, 100, 0.9)).ToArray();
        AdequacyRequest Req(ulong? seed, ProbabilisticMethod m = ProbabilisticMethod.MonteCarlo, IReadOnlyList<AdequacyUnit>? units = null, double[]? load = null) =>
            new(units ?? u3, load ?? new[] { 150.0 }, m, 100, null, seed);
        var noSeed = Assert.Throws<ArgumentException>(() => GenerationAdequacy.Run(Req(null)));
        Assert.Contains("no default seed", noSeed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(GridSim.Core.Diagnostics.DomainRefusal.IsMarked(noSeed));
        Assert.Throws<ArgumentException>(() => GenerationAdequacy.Run(Req(1, load: new[] { double.NaN })));
        Assert.Throws<ArgumentException>(() => GenerationAdequacy.Run(Req(1, load: new[] { -1.0 })));
        Assert.Throws<ArgumentException>(() => GenerationAdequacy.Run(Req(1, load: Array.Empty<double>())));
        var many = Enumerable.Range(0, 65).Select(i => AdequacyUnit.TwoState("g" + i, 10, 0.9)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => GenerationAdequacy.Run(Req(1, ProbabilisticMethod.QuasiMonteCarlo, many)));
        // Monte Carlo and convolution handle 65 units.
        Assert.Equal(ProbabilisticStatus.Complete, GenerationAdequacy.Run(Req(1, ProbabilisticMethod.MonteCarlo, many)).Status);
        Assert.Equal(ProbabilisticStatus.Complete, GenerationAdequacy.Run(Req(1, ProbabilisticMethod.Convolution, many)).Status);
        Assert.ThrowsAny<ArgumentException>(() => GenerationAdequacy.Run(new AdequacyRequest(u3, new[] { 150.0 }, ProbabilisticMethod.QuasiMonteCarloShifted, 100, 1, 1)));   // 1 replicate
        Assert.Throws<ArgumentException>(() => GenerationAdequacy.Run(new AdequacyRequest(u3, new[] { 150.0 }, ProbabilisticMethod.MonteCarlo, 100, 4, 1)));              // replicates not for MC
        Assert.Throws<ArgumentOutOfRangeException>(() => GenerationAdequacy.Run(new AdequacyRequest(u3, new[] { 150.0 }, ProbabilisticMethod.MonteCarlo, 0, null, 1)));
    }

    // ================================================================================================================
    // Probabilistic load flow
    // ================================================================================================================

    // Triangle with slack at bus 1; branches [1-2 X=.1, 2-3 X=.2, 1-3 X=.4]; ratings in MW.
    private static GridModel Triangle(double rate12 = 0, double rate23 = 0, double rate13 = 0) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 60, Qd: 10, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 40, Qd: 5, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 100) },
        Branches = new[]
        {
            new Branch(1, 2, 0.0, 0.1, RateAMva: rate12), new Branch(2, 3, 0.0, 0.2, RateAMva: rate23), new Branch(1, 3, 0.0, 0.4, RateAMva: rate13),
        },
    };

    // Hand solution of the DC model by Cramer's rule, independent of DistributionFactors: the flow (pu) on each branch for
    // +1 pu injected at bus j (2 or 3) and withdrawn at the slack, buses 2 and 3 free (angle of bus 1 is 0).
    private static double[] HandPtdf(int j)
    {
        double b12 = 1 / 0.1, b23 = 1 / 0.2, b13 = 1 / 0.4;
        double m11 = b12 + b23, m12 = -b23, m22 = b23 + b13;
        double det = m11 * m22 - m12 * m12;
        double r2 = j == 2 ? 1 : 0, r3 = j == 3 ? 1 : 0;
        double th2 = (r2 * m22 - m12 * r3) / det, th3 = (m11 * r3 - m12 * r2) / det;
        return new[] { b12 * (0 - th2), b23 * (th2 - th3), b13 * (0 - th3) };
    }

    private static UncertainInput Load(string id, int bus, UncertainDistribution d, bool scale = false) => new(id, UncertainTarget.BusLoad, bus, 0, d, scale);

    private static PlfRequest Dc(ProbabilisticMethod m, IReadOnlyList<UncertainInput> inputs, int n = 1 << 14, ulong seed = 42, int? reps = null,
        double[]? q = null, double limit = 100.0, double? step = null, IReadOnlyList<InputCorrelation>? corr = null, int threads = 0) =>
        new(inputs, corr, PlfEvaluator.Dc, m, n, reps, seed, "plf", q, limit, 0.95, 1.05, null, null, 100, step, true, threads);

    [Fact]
    public void Dc_flow_of_independent_normals_is_exactly_normal_by_convolution_and_within_error_by_sampling()
    {
        GridModel model = Triangle(rate12: 70, rate23: 30, rate13: 40);
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(60, 10)), Load("l3", 3, new NormalDistribution(40, 5)) };
        double[] t2 = HandPtdf(2), t3 = HandPtdf(3);
        double[] mu = { 60, 40 }, sg = { 10, 5 };
        // Flow(MW) = -sum_j T_j,l P_j: slack absorbs the rest, so the nominal generation is irrelevant.
        var exMean = new double[3]; var exSd = new double[3];
        for (int l = 0; l < 3; l++)
        {
            exMean[l] = -(t2[l] * mu[0] + t3[l] * mu[1]);
            exSd[l] = Math.Sqrt(t2[l] * t2[l] * sg[0] * sg[0] + t3[l] * t3[l] * sg[1] * sg[1]);
        }
        double[] qs = { 0.05, 0.5, 0.95 };

        PlfResult conv = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.Convolution, inputs, q: qs));
        Assert.Equal(ProbabilisticStatus.Complete, conv.Status);
        for (int l = 0; l < 3; l++)
        {
            BranchStatistics b = conv.Branches[l];
            Near(exMean[l], b.Mean, 1e-9, "conv mean " + l);
            Assert.True(Math.Abs(b.StdDev / exSd[l] - 1) < 1e-4, $"conv sd {b.StdDev:R} vs {exSd[l]:R}");
            for (int i = 0; i < 3; i++)
                Near(exMean[l] + GaussianInverse.Quantile(qs[i]) * exSd[l], b.QuantileValues[i], 0.03 * exSd[l], $"conv quantile {qs[i]} branch {l}");
            double lim = model.Branches[l].RateAMva;
            double exact = GaussianCdf.Cdf((-lim - exMean[l]) / exSd[l]) + 1.0 - GaussianCdf.Cdf((lim - exMean[l]) / exSd[l]);
            Near(exact, b.ExceedProbability!.Value, 2e-3, "conv exceedance " + l);
            Assert.NotNull(b.LatticeStepMw);
            Assert.Null(b.MeanStdError);
        }

        foreach (ProbabilisticMethod m in new[] { ProbabilisticMethod.MonteCarlo, ProbabilisticMethod.QuasiMonteCarloShifted })
        {
            PlfResult r = ProbabilisticLoadFlow.Run(model, Dc(m, inputs, n: m == ProbabilisticMethod.MonteCarlo ? 1 << 16 : 1 << 12, reps: m == ProbabilisticMethod.MonteCarlo ? null : 16, q: qs));
            Assert.Equal(ProbabilisticStatus.Complete, r.Status);
            Assert.Equal(0, r.NonConverged);
            for (int l = 0; l < 3; l++)
            {
                BranchStatistics b = r.Branches[l];
                Assert.True(Math.Abs(b.Mean - exMean[l]) <= 4.5 * b.MeanStdError!.Value, $"{m} mean {l}: {b.Mean:R} vs {exMean[l]:R}, SE {b.MeanStdError}");
                Assert.True(Math.Abs(b.StdDev / exSd[l] - 1) < 0.03, $"{m} sd");
                double lim = model.Branches[l].RateAMva;
                double exact = GaussianCdf.Cdf((-lim - exMean[l]) / exSd[l]) + 1.0 - GaussianCdf.Cdf((lim - exMean[l]) / exSd[l]);
                Assert.True(Math.Abs(b.ExceedProbability!.Value - exact) <= 4.5 * b.ExceedStdError!.Value + 3.0 / 65536, $"{m} exceed {l}: {b.ExceedProbability} vs {exact}, SE {b.ExceedStdError}");
            }
        }

        PlfResult qmc = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.QuasiMonteCarlo, inputs, n: 1 << 14, q: qs));
        Assert.Null(qmc.Branches[0].MeanStdError);
        Assert.Contains("no error bar", qmc.ErrorModel, StringComparison.Ordinal);
        for (int l = 0; l < 3; l++) Near(exMean[l], qmc.Branches[l].Mean, 0.03 * exSd[l], "qmc mean " + l);
    }

    [Fact]
    public void Scale_inputs_are_multipliers_of_the_case_value()
    {
        GridModel model = Triangle();
        // Bus 2 load 60 MW scaled by N(1, 0.1): the same law as N(60, 6) on the value.
        PlfResult a = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.Convolution, new[] { Load("l2", 2, new NormalDistribution(1.0, 0.1), scale: true) }));
        PlfResult b = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.Convolution, new[] { Load("l2", 2, new NormalDistribution(60, 6)) }));
        for (int l = 0; l < 3; l++)
        {
            Near(b.Branches[l].Mean, a.Branches[l].Mean, 1e-9);
            Assert.True(Math.Abs(a.Branches[l].StdDev / b.Branches[l].StdDev - 1) < 1e-3);
        }
    }

    // Radial chain 1-2-3 (slack at 1): flow 1-2 = Pd2 + Pd3, flow 2-3 = Pd3 exactly, so discrete cases are exact on a lattice.
    private static GridModel Chain() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 5, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 15) },
        Branches = new[] { new Branch(1, 2, 0.0, 0.1, RateAMva: 30), new Branch(2, 3, 0.0, 0.2, RateAMva: 20) },
    };

    [Fact]
    public void Convolution_is_exact_on_a_discrete_case_and_monte_carlo_agrees_with_it()
    {
        var d2 = new DiscreteDistribution(new[] { (10.0, 0.4), (20.0, 0.6) });
        var d3 = new DiscreteDistribution(new[] { (5.0, 0.2), (15.0, 0.5), (25.0, 0.3) });
        UncertainInput[] inputs = { Load("l2", 2, d2), Load("l3", 3, d3) };
        // Enumerate the six joint outcomes: flow12 = P2 + P3.
        var pmf = new SortedDictionary<double, double>();
        foreach ((double v2, double p2) in d2.Atoms) foreach ((double v3, double p3) in d3.Atoms) pmf[v2 + v3] = pmf.GetValueOrDefault(v2 + v3) + p2 * p3;
        double mean = pmf.Sum(kv => kv.Key * kv.Value), sd = Math.Sqrt(pmf.Sum(kv => (kv.Key - mean) * (kv.Key - mean) * kv.Value));
        double Exceed(double lim) => pmf.Where(kv => Math.Abs(kv.Key) > lim).Sum(kv => kv.Value);

        PlfResult conv = ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.Convolution, inputs, step: 5.0, q: new[] { 0.1, 0.5, 0.9 }));
        BranchStatistics b = conv.Branches[0];
        Near(mean, b.Mean, 1e-12); Near(sd, b.StdDev, 1e-12);
        Near(Exceed(30), b.ExceedProbability!.Value, 1e-13);
        Near(0.4 * 0.3 + 0.6 * 0.5 + 0.6 * 0.3, b.ExceedProbability!.Value, 1e-13);   // totals 35, 35, 45 exceed 30 MW (hand sum 0.6)
        // Quantiles of an exact discrete law: smallest atom with CDF >= p.
        double Q(double p) { double c = 0; foreach ((double v, double pr) in pmf) { c += pr; if (c >= p - 1e-15) return v; } return pmf.Keys.Last(); }
        for (int i = 0; i < 3; i++) Near(Q(new[] { 0.1, 0.5, 0.9 }[i]), b.QuantileValues[i], 1e-9);
        BranchStatistics b2 = conv.Branches[1];   // flow 2-3 = P3
        Near(0.2 * 5 + 0.5 * 15 + 0.3 * 25, b2.Mean, 1e-12);
        Near(0.3, b2.ExceedProbability!.Value, 1e-13);   // |P3| > 20 only for 25

        PlfResult mc = ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, inputs, n: 1 << 16));
        Assert.True(Math.Abs(mc.Branches[0].Mean - mean) <= 4.5 * mc.Branches[0].MeanStdError!.Value);
        Assert.True(Math.Abs(mc.Branches[0].ExceedProbability!.Value - 0.6) <= 4.5 * mc.Branches[0].ExceedStdError!.Value);
    }

    [Fact]
    public void Correlated_normal_inputs_give_the_closed_form_variance_of_a_sum()
    {
        double s2 = 10, s3 = 5, rho = 0.6;
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(60, s2)), Load("l3", 3, new NormalDistribution(40, s3)) };
        var corr = new[] { new InputCorrelation(0, 1, rho) };
        // flow 1-2 = P2 + P3 on the chain: variance s2^2 + s3^2 + 2 rho s2 s3.
        double exVar = s2 * s2 + s3 * s3 + 2 * rho * s2 * s3;
        PlfResult mc = ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, inputs, n: 1 << 17, corr: corr));
        Assert.True(Math.Abs(mc.Branches[0].StdDev / Math.Sqrt(exVar) - 1) < 0.01, $"sd {mc.Branches[0].StdDev} vs {Math.Sqrt(exVar)}");
        Near(100.0, mc.Branches[0].Mean, 4.5 * mc.Branches[0].MeanStdError!.Value);
        PlfResult uncorr = ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, inputs, n: 1 << 17));
        Assert.True(mc.Branches[0].StdDev > uncorr.Branches[0].StdDev * 1.1);   // positive correlation widens the sum
        // Non-normal marginals through the copula keep their marginal means.
        UncertainInput[] mixed = { Load("l2", 2, new LogNormalDistribution(4.0, 0.2)), Load("l3", 3, new WeibullDistribution(40, 3)) };
        PlfResult m = ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, mixed, n: 1 << 16, corr: corr));
        double exMean = mixed[0].Distribution.Mean + mixed[1].Distribution.Mean;
        Assert.True(Math.Abs(m.Branches[0].Mean - exMean) <= 4.5 * m.Branches[0].MeanStdError!.Value, $"{m.Branches[0].Mean} vs {exMean}");
        // A correlation matrix that is not positive definite is refused.
        UncertainInput[] three = { Load("a", 2, new NormalDistribution(1, 1)), Load("b", 3, new NormalDistribution(1, 1)) };
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, three, n: 16, corr: new[] { new InputCorrelation(0, 1, 1.0) })));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(Chain(), Dc(ProbabilisticMethod.MonteCarlo, three, n: 16, corr: new[] { new InputCorrelation(0, 1, 1.5) })));
    }

    [Fact]
    public void Convolution_outside_its_domain_is_out_of_scope_with_the_reason()
    {
        GridModel model = Triangle();
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(60, 10)), Load("l3", 3, new NormalDistribution(40, 5)) };
        PlfResult ac = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.Convolution, inputs) with { Evaluator = PlfEvaluator.Ac });
        Assert.Equal(ProbabilisticStatus.OutOfScope, ac.Status);
        Assert.Contains("nonlinear", ac.Reason, StringComparison.Ordinal);
        Assert.Empty(ac.Branches);
        PlfResult corr = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.Convolution, inputs, corr: new[] { new InputCorrelation(0, 1, 0.3) }));
        Assert.Equal(ProbabilisticStatus.OutOfScope, corr.Status);
        Assert.Contains("independent", corr.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plf_refusals_missing_seed_non_finite_parameter_identity_and_duplicates()
    {
        GridModel model = Triangle();
        UncertainInput[] ok = { Load("l2", 2, new NormalDistribution(60, 10)) };
        var noSeed = Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, ok) with { Seed = null }));
        Assert.Contains("no default seed", noSeed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => new NormalDistribution(60, double.NaN));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, new[] { Load("x", 99, new NormalDistribution(1, 1)) })));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo,
            new[] { Load("a", 2, new NormalDistribution(1, 1)), Load("b", 2, new NormalDistribution(1, 1)) })));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo,
            new[] { new UncertainInput("g", UncertainTarget.GeneratorPg, 1, 3, new NormalDistribution(1, 1)) })));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, ok, q: new[] { 1.5 })));
        Assert.Throws<ArgumentException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, ok, limit: double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, ok, n: 0)));
        // QMC beyond the 64-dimension table is refused; the same inputs run under Monte Carlo.
        var buses = new List<Bus> { new(1, BusType.Slack) };
        var branches = new List<Branch>();
        var many = new List<UncertainInput>();
        for (int i = 2; i <= 66; i++)
        {
            buses.Add(new Bus(i, BusType.Pq, Pd: 1));
            branches.Add(new Branch(i - 1, i, 0.0, 0.1));
            many.Add(Load("l" + i, i, new NormalDistribution(1, 0.1)));
        }
        var big = new GridModel { BaseMva = 100, Buses = buses, Generators = new[] { new Generator(1, Pg: 65) }, Branches = branches };
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbabilisticLoadFlow.Run(big, Dc(ProbabilisticMethod.QuasiMonteCarlo, many, n: 8)));
        Assert.Equal(ProbabilisticStatus.Complete, ProbabilisticLoadFlow.Run(big, Dc(ProbabilisticMethod.MonteCarlo, many, n: 8)).Status);
    }

    [Fact]
    public void Plf_results_are_bit_repeatable_across_threads_and_seed_sensitive_dc_and_ac()
    {
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(60, 10)), Load("l3", 3, new NormalDistribution(40, 5)) };
        foreach (PlfEvaluator ev in new[] { PlfEvaluator.Dc, PlfEvaluator.Ac })
            foreach (ProbabilisticMethod m in new[] { ProbabilisticMethod.MonteCarlo, ProbabilisticMethod.QuasiMonteCarlo, ProbabilisticMethod.QuasiMonteCarloShifted })
            {
                PlfResult Run(ulong seed, int threads) => ProbabilisticLoadFlow.Run(Triangle(70, 40, 40),
                    Dc(m, inputs, n: ev == PlfEvaluator.Ac ? 300 : 3000, seed: seed, reps: m == ProbabilisticMethod.QuasiMonteCarloShifted ? 3 : null, threads: threads) with { Evaluator = ev });
                PlfResult one = Run(8, 1);
                foreach (int t in new[] { 4, 16 })
                {
                    PlfResult many = Run(8, t);
                    Assert.Equal(one.Provenance!.ResultDigest, many.Provenance!.ResultDigest);
                    Assert.Equal(one.Provenance.SampleDigest, many.Provenance.SampleDigest);
                }
                PlfResult other = Run(9, 1);
                if (m == ProbabilisticMethod.QuasiMonteCarlo) Assert.Equal(one.Provenance!.SampleDigest, other.Provenance!.SampleDigest);
                else Assert.NotEqual(one.Provenance!.SampleDigest, other.Provenance!.SampleDigest);
            }
    }

    [Fact]
    public void Ac_evaluator_matches_the_solver_at_the_mean_for_a_tiny_spread_and_reports_voltages()
    {
        GridModel model = Triangle(100, 100, 100);
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(60, 0.01)), Load("l3", 3, new NormalDistribution(40, 0.01)) };
        PlfRequest req = Dc(ProbabilisticMethod.MonteCarlo, inputs, n: 64) with { Evaluator = PlfEvaluator.Ac };
        PlfResult r = ProbabilisticLoadFlow.Run(model, req);
        Assert.Equal(64, r.Converged);
        var solved = GridSim.Core.Analysis.ContingencyAnalysis.CreateStudySolver(model).Solve(model, GridSim.Core.Analysis.ContingencyAnalysis.StudyOptions);
        for (int l = 0; l < 3; l++) Near(solved.LineFlows[l].PFromMw, r.Branches[l].Mean, 0.05, "AC flow " + l);
        Assert.Equal(3, r.Buses.Count);
        for (int b = 0; b < 3; b++) Near(solved.Vm[b], r.Buses[b].Mean, 1e-4, "Vm " + b);
        Assert.Equal(0.0, r.Buses[1].BelowMinProbability);
    }

    [Fact]
    public void Non_converged_ac_samples_are_kept_as_rows_and_left_out_of_every_probability()
    {
        // Slack at V = 1 behind X = 0.1 pu to a load with no reactive demand. V^4 - V^2 + X^2 P^2 = 0 has a real root only
        // while P <= 1/(2X) = 5 pu = 500 MW (discriminant 1 - 4 X^2 P^2), so every sample above 500 MW must fail.
        var model = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 400, Qd: 0, BaseKv: 132) },
            Generators = new[] { new Generator(1, Pg: 400) },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1, RateAMva: 450) },
        };
        UncertainInput[] inputs = { Load("l2", 2, new NormalDistribution(480, 40)) };
        PlfRequest req = Dc(ProbabilisticMethod.MonteCarlo, inputs, n: 400, seed: 3) with { Evaluator = PlfEvaluator.Ac };
        PlfResult r = ProbabilisticLoadFlow.Run(model, req);
        Assert.Equal(400, r.Samples);
        Assert.Equal(400, r.Converged + r.NonConverged);
        Assert.Equal(r.NonConverged, r.FailedSamples.Count);
        Assert.InRange(r.NonConverged, 80, 190);    // P(N(480, 40) > 500) = 0.31
        Assert.All(r.FailedSamples, f => Assert.True(f.InputValues[0] > 450, $"failed sample at {f.InputValues[0]} MW"));
        Assert.All(r.FailedSamples, f => Assert.False(string.IsNullOrEmpty(f.Status)));
        // Every sample above 500 MW failed: redraw the inputs independently and count them.
        var src = new UniformSource(ProbabilisticMethod.MonteCarlo, 1, 3, "plf");
        int above = 0;
        for (int i = 0; i < 400; i++) if (new NormalDistribution(480, 40).Quantile(src.U(i, 0)) > 500) above++;
        Assert.True(r.NonConverged >= above, $"{r.NonConverged} failed, {above} samples above the nose");
        // ANA-20 (re-pinned): the exceedance probability is over the CONVERGED samples (it used to divide by all of them, so
        // each failed sample, the heaviest draws, counted as "no overload"). It is a whole number of converged samples over
        // Converged, and the run is INCOMPLETE with the failed count in its reason.
        double p = r.Branches[0].ExceedProbability!.Value;
        Assert.Equal(Math.Round(p * r.Converged), p * r.Converged, 9);
        Assert.Equal(ProbabilisticStatus.Incomplete, r.Status);
        Assert.Contains($"{r.NonConverged} of 400 samples did not converge", r.Reason, StringComparison.Ordinal);
        // A request in which no sample can converge is INCOMPLETE, not a number.
        PlfResult none = ProbabilisticLoadFlow.Run(model, Dc(ProbabilisticMethod.MonteCarlo, new[] { Load("l2", 2, new NormalDistribution(900, 1)) }, n: 20) with { Evaluator = PlfEvaluator.Ac });
        Assert.Equal(ProbabilisticStatus.Incomplete, none.Status);
        Assert.Equal(20, none.NonConverged);
    }

    // ================================================================================================================
    // Source scan: only the foundation's generator and DetMath maths
    // ================================================================================================================

    [Fact]
    public void The_probabilistic_namespace_uses_neither_System_Random_nor_platform_maths()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GridSim.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string[] files = Directory.GetFiles(Path.Combine(dir!, "src", "GridSim.Core", "Analysis", "Probabilistic"), "*.cs");
        Assert.NotEmpty(files);
        string[] banned = { "System.Random", "new Random", "Random.Shared", "Math.Log", "Math.Exp", "Math.Pow", "Math.Sin", "Math.Cos", "Math.Atan", "Math.Tan", "MathF." };
        foreach (string f in files)
        {
            string code = string.Join('\n', File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith('*')));
            foreach (string b in banned)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, "(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(b)), $"{Path.GetFileName(f)} uses {b}");
        }
    }
}
