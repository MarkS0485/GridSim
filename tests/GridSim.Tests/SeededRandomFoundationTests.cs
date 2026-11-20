using GridSim.Core.Stochastic;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-02: the seeded reproducible random foundation (generator gridsim-rng/1, Sobol quasi-Monte Carlo, fixed-order
/// reduction, provenance). Oracles are published or derived here independently of the code under test.
/// </summary>
public class SeededRandomFoundationTests
{
    // ---- SplitMix64 ----------------------------------------------------------------------------------------------

    [Fact]
    public void SplitMix64_seed0_reference_vectors_through_the_counter_form()
    {
        // Vigna's reference SplitMix64 with state 0: first three outputs. Counter form with base 0 maps onto it exactly.
        var s = CounterStream.FromBase(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, s.DrawU64(0));
        Assert.Equal(0x6E789E6AA1B965F4UL, s.DrawU64(1));
        Assert.Equal(0x06C45D188009454FUL, s.DrawU64(2));
    }

    [Fact]
    public void Sequential_reference_implementation_matches_the_counter_form_for_a_nonzero_base()
    {
        // Independent sequential form: state += G; output = finalise(state).
        const ulong g = 0x9E3779B97F4A7C15UL;
        ulong state = 0x0123456789ABCDEFUL;
        var s = CounterStream.FromBase(state);
        for (ulong k = 0; k < 1000; k++)
        {
            unchecked { state += g; }
            ulong z = state;
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
            }
            Assert.Equal(z, s.DrawU64(k));
        }
    }

    [Fact]
    public void Draws_are_independent_of_consumption_order()
    {
        var s = new CounterStream(new StreamKey(42, "order", 3));
        var forward = Enumerable.Range(0, 500).Select(k => s.DrawU64((ulong)k)).ToArray();
        var backward = Enumerable.Range(0, 500).Reverse().Select(k => s.DrawU64((ulong)k)).Reverse().ToArray();
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Stream_base_follows_the_documented_formula()
    {
        // H(domain) recomputed here: first 8 bytes of SHA-256("load"), big-endian.
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("load"));
        ulong hd = 0;
        for (int i = 0; i < 8; i++) hd = (hd << 8) | h[i];
        ulong expect = unchecked(7UL ^ hd ^ (5UL * 0x9E3779B97F4A7C15UL));
        Assert.Equal(expect, new StreamKey(7, "load", 5).Base);
    }

    [Fact]
    public void Different_seed_domain_or_index_give_different_streams()
    {
        ulong a = new CounterStream(new StreamKey(1, "x", 0)).DrawU64(0);
        Assert.NotEqual(a, new CounterStream(new StreamKey(2, "x", 0)).DrawU64(0));
        Assert.NotEqual(a, new CounterStream(new StreamKey(1, "y", 0)).DrawU64(0));
        Assert.NotEqual(a, new CounterStream(new StreamKey(1, "x", 1)).DrawU64(0));
    }

    [Fact]
    public void Pinned_regression_vector_for_a_keyed_stream()
    {
        // Regression pin (not an independent oracle): changing the algorithm, hash or byte order changes this.
        var s = new CounterStream(new StreamKey(12345, "gridsim-test", 7));
        Assert.Equal(PinnedKeyedDraws, Enumerable.Range(0, 3).Select(k => s.DrawU64((ulong)k)).ToArray());
    }

    private static readonly ulong[] PinnedKeyedDraws = ComputePinned();
    private static ulong[] ComputePinned()
    {
        // Built from first principles (separate code path) so the pin documents the specification, not the class.
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("gridsim-test"));
        ulong hd = 0;
        for (int i = 0; i < 8; i++) hd = (hd << 8) | h[i];
        const ulong g = 0x9E3779B97F4A7C15UL;
        ulong b = unchecked(12345UL ^ hd ^ (7UL * g));
        var r = new ulong[3];
        for (int k = 0; k < 3; k++)
        {
            ulong z = unchecked(b + (ulong)(k + 1) * g);
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
            }
            r[k] = z;
        }
        return r;
    }

    [Fact]
    public void Empty_domain_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new StreamKey(1, ""));
    }

    [Fact]
    public void Uniform_is_the_exact_53_bit_map_in_zero_one()
    {
        Assert.Equal(0.0, Uniform.FromU64(0));
        Assert.Equal(1.0 - Math.Pow(2, -53), Uniform.FromU64(ulong.MaxValue));
        Assert.Equal(0.5, Uniform.FromU64(1UL << 63));
    }

    // ---- bit repeatability ---------------------------------------------------------------------------------------

    [Fact]
    public void One_and_sixteen_threads_give_identical_bits_for_a_million_draws_and_the_reduced_mean()
    {
        var key = new StreamKey(20261117, "repeat");
        var a = new double[1_000_000];
        var b = new double[1_000_000];
        Uniform.Fill(key, a, 1);
        Uniform.Fill(key, b, 16);
        Assert.Equal(StochasticProvenance.Digest(a), StochasticProvenance.Digest(b));
        double ma = FixedOrderReduction.Mean(a, 1), mb = FixedOrderReduction.Mean(b, 16), mc = FixedOrderReduction.Mean(a, 0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(ma), BitConverter.DoubleToInt64Bits(mb));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ma), BitConverter.DoubleToInt64Bits(mc));
        // A rerun reproduces the same digest.
        var c = new double[1_000_000];
        Uniform.Fill(key, c, 4);
        Assert.Equal(StochasticProvenance.Digest(a), StochasticProvenance.Digest(c));
        // A different seed or domain differs.
        var d = new double[1000];
        Uniform.Fill(new StreamKey(20261118, "repeat"), d, 1);
        Assert.NotEqual(StochasticProvenance.Digest(a.Take(1000)), StochasticProvenance.Digest(d));
        Uniform.Fill(new StreamKey(20261117, "repeat2"), d, 1);
        Assert.NotEqual(StochasticProvenance.Digest(a.Take(1000)), StochasticProvenance.Digest(d));
    }

    [Fact]
    public void Reduction_tree_is_the_documented_one_and_ignores_thread_count()
    {
        // Values chosen so that a different summation order changes the bits.
        int n = 5000;
        double V(int i) => (i % 7 == 0 ? 1e16 : 1.0) * (i % 2 == 0 ? 1 : -1) + i * 1e-3;
        double s1 = FixedOrderReduction.Sum(n, V, 1);
        double s16 = FixedOrderReduction.Sum(n, V, 16);
        Assert.Equal(BitConverter.DoubleToInt64Bits(s1), BitConverter.DoubleToInt64Bits(s16));
        // Reference: explicit 5 blocks (4 x 1024 + 904), pairwise: ((b0+b1)+(b2+b3))+b4.
        var blk = new double[5];
        for (int b = 0; b < 5; b++)
        {
            double s = 0;
            for (int i = b * 1024; i < Math.Min(n, b * 1024 + 1024); i++) s += V(i);
            blk[b] = s;
        }
        double expect = ((blk[0] + blk[1]) + (blk[2] + blk[3])) + blk[4];
        Assert.Equal(BitConverter.DoubleToInt64Bits(expect), BitConverter.DoubleToInt64Bits(s1));
    }

    [Fact]
    public void Failing_block_rethrows_the_lowest_unwrapped()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FixedOrderReduction.ForBlocks(10_000, 8, (lo, hi) => throw new InvalidOperationException("block " + lo)));
        Assert.Equal("block 0", ex.Message);
    }

    // ---- statistical properties ----------------------------------------------------------------------------------

    [Fact]
    public void Uniform_mean_variance_and_bins_are_within_documented_bounds_for_the_pinned_seed()
    {
        const int n = 1_000_000;
        var u = new double[n];
        Uniform.Fill(new StreamKey(2026, "stats"), u, 0);
        double mean = FixedOrderReduction.Mean(u);
        double se = Math.Sqrt(1.0 / 12.0 / n); // variance of the mean of N U(0,1) draws is 1/(12 N)
        Assert.InRange(mean, 0.5 - 4 * se, 0.5 + 4 * se);
        double var = FixedOrderReduction.Sum(n, i => (u[i] - mean) * (u[i] - mean)) / (n - 1);
        Assert.InRange(var, 1.0 / 12 * 0.99, 1.0 / 12 * 1.01);
        // Chi-square over 100 equal bins; 99 degrees of freedom, 0.1 per cent critical value 148.2.
        var bins = new int[100];
        foreach (double x in u) bins[(int)(x * 100)]++;
        double chi = bins.Sum(c => (c - n / 100.0) * (c - n / 100.0) / (n / 100.0));
        Assert.True(chi < 148.2, "chi-square " + chi);
        // Lag-1 serial correlation is small: standard error 1/sqrt(N).
        double cov = FixedOrderReduction.Sum(n - 1, i => (u[i] - 0.5) * (u[i + 1] - 0.5)) / (n - 1) / (1.0 / 12);
        Assert.True(Math.Abs(cov) < 4.0 / Math.Sqrt(n), "lag-1 " + cov);
    }

    [Fact]
    public void Gaussian_deviates_have_zero_mean_and_unit_variance_for_the_pinned_seed()
    {
        const int n = 500_000;
        var u = new double[n];
        Uniform.Fill(new StreamKey(2026, "gauss"), u, 0);
        var z = u.Select(GaussianInverse.FromUniform).ToArray();
        double mean = FixedOrderReduction.Mean(z);
        Assert.True(Math.Abs(mean) < 4 / Math.Sqrt(n), "mean " + mean);
        double var = FixedOrderReduction.Sum(n, i => z[i] * z[i]) / n;
        Assert.InRange(var, 1 - 4 * Math.Sqrt(2.0 / n), 1 + 4 * Math.Sqrt(2.0 / n)); // Var(z^2) = 2
    }

    // ---- AS241 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Gaussian_inverse_published_quantiles()
    {
        Assert.Equal(0.0, GaussianInverse.Quantile(0.5));
        Assert.Equal(1.959963984540054, GaussianInverse.Quantile(0.975), 14);
        Assert.Equal(1.0, GaussianInverse.Quantile(0.8413447460685429), 14);
        Assert.Equal(-GaussianInverse.Quantile(0.975), GaussianInverse.Quantile(0.025), 13);
    }

    [Fact]
    public void Gaussian_inverse_round_trips_through_an_independent_forward_cdf()
    {
        // Forward CDF by the convergent series Phi(x) = 1/2 + phi(x) * sum x^(2k+1) / (1 * 3 * ... * (2k+1)).
        static double Phi(double x)
        {
            double term = x, sum = x;
            for (int k = 1; k < 400; k++)
            {
                term *= x * x / (2 * k + 1);
                sum += term;
                if (Math.Abs(term) < 1e-18 * Math.Abs(sum)) break;
            }
            return 0.5 + Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI) * sum;
        }
        for (int i = 1; i < 1000; i++)
        {
            double p = i / 1000.0;
            Assert.Equal(p, Phi(GaussianInverse.Quantile(p)), 13);
        }
        // Branch coverage of the tail rational forms: 1e-6 (r about 3.7) and 1e-12 (r about 5.3).
        Assert.InRange(Phi(GaussianInverse.Quantile(1e-6)) / 1e-6, 1 - 1e-8, 1 + 1e-8);
        // 1e-12 (r about 5.3, the second tail form): Mills asymptotic series, relative error below 4e-6 at x = 7.
        double x = -GaussianInverse.Quantile(1e-12);
        double x2 = x * x;
        double tail = Math.Exp(-0.5 * x2) / Math.Sqrt(2 * Math.PI) / x * (1 - 1 / x2 + 3 / (x2 * x2) - 15 / (x2 * x2 * x2) + 105 / (x2 * x2 * x2 * x2));
        Assert.InRange(tail / 1e-12, 1 - 1e-5, 1 + 1e-5);
    }

    [Fact]
    public void Gaussian_inverse_refuses_out_of_range_and_nudges_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaussianInverse.Quantile(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GaussianInverse.Quantile(1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GaussianInverse.Quantile(double.NaN));
        double z = GaussianInverse.FromUniform(0.0);
        Assert.True(double.IsFinite(z) && z < -6);
    }

    // ---- Sobol ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Sobol_published_first_eight_points_of_dimensions_one_and_two()
    {
        double[][] expect =
        {
            new[] { 0.0, 0.0 }, new[] { 0.5, 0.5 }, new[] { 0.75, 0.25 }, new[] { 0.25, 0.75 },
            new[] { 0.375, 0.375 }, new[] { 0.875, 0.875 }, new[] { 0.625, 0.125 }, new[] { 0.125, 0.625 },
        };
        var s = new SobolSequence(2);
        var p = new double[2];
        for (int i = 0; i < 8; i++)
        {
            s.Point((ulong)i, p);
            Assert.Equal(expect[i], p);
        }
    }

    [Fact]
    public void Sobol_dimension_one_mean_of_two_to_the_m_points_is_exactly_n_minus_one_over_two_n()
    {
        var s = new SobolSequence(1);
        for (int m = 1; m <= 16; m++)
        {
            long n = 1L << m;
            double sum = 0;
            for (long i = 0; i < n; i++) sum += s.Coordinate((ulong)i, 0);
            Assert.Equal((n - 1.0) / (2.0 * n), sum / n);
        }
    }

    [Fact]
    public void Every_dimension_is_a_one_dimensional_net_and_the_first_two_are_a_two_dimensional_net()
    {
        const int m = 10, n = 1 << m;
        var s = new SobolSequence(SobolDirectionNumbers.MaxDimension);
        for (int d = 0; d < s.Dimension; d++)
        {
            var seen = new bool[n];
            for (int i = 0; i < n; i++) seen[(int)(s.Coordinate((ulong)i, d) * n)] = true;
            Assert.True(seen.All(x => x), "dimension " + d);
        }
        for (int a = 0; a <= m; a++)
        {
            // Elementary intervals 2^-a by 2^-(m-a): exactly one point in each.
            var cell = new int[n];
            for (int i = 0; i < n; i++)
            {
                int cx = (int)(s.Coordinate((ulong)i, 0) * (1 << a));
                int cy = (int)(s.Coordinate((ulong)i, 1) * (1 << (m - a)));
                cell[cx * (1 << (m - a)) + cy]++;
            }
            Assert.True(cell.All(c => c == 1), "split " + a);
        }
    }

    [Fact]
    public void Derived_direction_numbers_match_hand_derivation_and_primitive_polynomials_are_verified_by_brute_force()
    {
        Assert.Equal(3u, SobolDirectionNumbers.Polynomial(1));   // x + 1
        Assert.Equal(7u, SobolDirectionNumbers.Polynomial(2));   // x^2 + x + 1
        Assert.Equal(11u, SobolDirectionNumbers.Polynomial(3));  // x^3 + x + 1
        Assert.Equal(13u, SobolDirectionNumbers.Polynomial(4));  // x^3 + x^2 + 1
        // Dimension 2: m1 = m2 = 1; m3 = 2*1*m2 xor 4*m1 xor m1 = 2 xor 4 xor 1 = 7 -> v3 = 7 << 29.
        var v = SobolDirectionNumbers.Directions(2);
        Assert.Equal(1u << 31, v[0]);
        Assert.Equal(1u << 30, v[1]);
        Assert.Equal(7u << 29, v[2]);
        // Dimension 1 (x + 1): m_i = 2 m_{i-1} xor m_{i-1}: 1, 3, 5, 15.
        var w = SobolDirectionNumbers.Directions(1);
        Assert.Equal(1u << 31, w[0]);
        Assert.Equal(3u << 30, w[1]);
        Assert.Equal(5u << 29, w[2]);
        Assert.Equal(15u << 28, w[3]);

        // Brute force: the order of x modulo p, by repeated multiplication, must be exactly 2^s - 1.
        var perDegree = new Dictionary<int, int>();
        for (int d = 1; d < SobolDirectionNumbers.MaxDimension; d++)
        {
            uint p = SobolDirectionNumbers.Polynomial(d);
            int s = 31 - System.Numerics.BitOperations.LeadingZeroCount(p);
            perDegree[s] = perDegree.GetValueOrDefault(s) + 1;
            uint cur = s == 1 ? 1u : 2u;
            long order = 1;
            while (cur != 1)
            {
                cur <<= 1;
                if (((cur >> s) & 1) != 0) cur ^= p;
                order++;
                Assert.True(order < (1L << s), "order exceeded for " + p);
            }
            Assert.Equal((1L << s) - 1, order);
        }
        // Number of primitive polynomials of degree s is phi(2^s - 1) / s: 1,1,2,2,6,6,18,16,48 (degrees 1..9).
        Assert.Equal(1, perDegree[1]); Assert.Equal(1, perDegree[2]); Assert.Equal(2, perDegree[3]);
        Assert.Equal(2, perDegree[4]); Assert.Equal(6, perDegree[5]); Assert.Equal(6, perDegree[6]);
        Assert.Equal(18, perDegree[7]); Assert.Equal(16, perDegree[8]);
        Assert.Equal(63 - 1 - 1 - 2 - 2 - 6 - 6 - 18 - 16, perDegree[9]);
    }

    [Fact]
    public void Sobol_integrates_a_smooth_function_better_than_the_error_scale_of_monte_carlo()
    {
        // Integral of x*y over the unit square is 1/4; Monte Carlo standard error at N = 2^14 is about 0.0016.
        var s = new SobolSequence(2);
        const int n = 1 << 14;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += s.Coordinate((ulong)i, 0) * s.Coordinate((ulong)i, 1);
        Assert.True(Math.Abs(sum / n - 0.25) < 2e-4, "error " + (sum / n - 0.25));
    }

    [Fact]
    public void Digital_shift_is_deterministic_seed_dependent_and_keeps_the_exact_mean_structure()
    {
        var a = new SobolSequence(3, 99, "qmc");
        var b = new SobolSequence(3, 99, "qmc");
        var c = new SobolSequence(3, 100, "qmc");
        Assert.True(a.Shifted);
        Assert.Equal(a.Coordinate(5, 2), b.Coordinate(5, 2));
        Assert.NotEqual(a.Coordinate(5, 2), c.Coordinate(5, 2));
        // The first 2^m points are a permutation of the top m bits plus a constant low part (the shift's low bits).
        const int m = 10;
        long n = 1L << m;
        double sum = 0;
        for (long i = 0; i < n; i++) sum += a.Coordinate((ulong)i, 0);
        uint mask = DigitalShift.Mask(99, "qmc", 0);
        double low = (mask & ((1u << (32 - m)) - 1)) / 4294967296.0;
        Assert.Equal((n - 1.0) / (2.0 * n) + low, sum / n, 12);
        Assert.Throws<ArgumentException>(() => new SobolSequence(2, 1, ""));
    }

    [Fact]
    public void Dimension_and_index_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SobolSequence(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SobolSequence(SobolDirectionNumbers.MaxDimension + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SobolSequence(1).Coordinate(1UL << 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SobolDirectionNumbers.Directions(SobolDirectionNumbers.MaxDimension));
    }

    [Fact]
    public void Direction_table_digest_is_stable_across_calls()
    {
        Assert.Equal(SobolDirectionNumbers.Digest(), SobolDirectionNumbers.Digest());
        Assert.Equal("gridsim-sobol-derived/1", SobolDirectionNumbers.TableId);
    }

    // ---- provenance and refusals ---------------------------------------------------------------------------------

    [Fact]
    public void Provenance_records_the_required_fields_and_refuses_a_missing_seed()
    {
        var vals = new[] { 0.25, 0.5 };
        var p = StochasticProvenance.Create(5UL, new[] { "a", "b" }, SamplingMethod.QuasiMonteCarlo, 2, vals);
        Assert.Equal("gridsim-rng/1", p.GeneratorVersion);
        Assert.Equal("gridsim-sobol-derived/1", p.DirectionTableId);
        Assert.Equal(StochasticProvenance.Digest(vals), p.ResultDigest);
        Assert.Equal(64, p.ResultDigest.Length);
        Assert.Null(StochasticProvenance.Create(5UL, new[] { "a" }, SamplingMethod.MonteCarlo, 2, vals).DirectionTableId);
        Assert.Throws<ArgumentException>(() => StochasticProvenance.Create(null, new[] { "a" }, SamplingMethod.MonteCarlo, 2, vals));
        Assert.Throws<ArgumentOutOfRangeException>(() => StochasticProvenance.Create(1UL, new[] { "a" }, SamplingMethod.MonteCarlo, 0, vals));
        Assert.Throws<ArgumentException>(() => StochasticProvenance.Create(1UL, Array.Empty<string>(), SamplingMethod.MonteCarlo, 2, vals));
        Assert.Throws<ArgumentOutOfRangeException>(() => StochasticProvenance.Create(1UL, new[] { "a" }, (SamplingMethod)99, 2, vals));
    }

    [Fact]
    public void Digest_of_known_bits_matches_an_independent_sha256()
    {
        byte[] bytes = BitConverter.GetBytes(1.0); // little-endian on every supported platform
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), StochasticProvenance.Digest(new[] { 1.0 }));
    }

    // ---- source scan ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_stochastic_namespace_uses_neither_System_Random_nor_platform_maths()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GridSim.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string[] files = Directory.GetFiles(Path.Combine(dir!, "src", "GridSim.Core", "Stochastic"), "*.cs");
        Assert.NotEmpty(files);
        string[] banned = { "System.Random", "new Random", "Random.Shared", "Math.Log", "Math.Exp", "Math.Pow", "Math.Sin", "Math.Cos", "MathF." };
        foreach (string f in files)
        {
            // Ignore comment lines so that the prose may name what is banned.
            string code = string.Join('\n', File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith('*')));
            foreach (string b in banned)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, "(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(b)), $"{Path.GetFileName(f)} uses {b}");
        }
    }
}
