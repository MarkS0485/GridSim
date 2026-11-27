using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Dynamics.CoSim;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Journal;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP27: the verbs harmonic-pf, harmonic-se, cosim and emt-partition over unchanged Core studies, and the unchanged emt3.
/// Every comparison with the library is to the bit; every comparison with a closed form carries a bound derived in the test.
///
/// Spec versus code (the code is followed):
/// <list type="bullet">
/// <item>The coupled harmonic power flow's coupling model takes the harmonic terminal voltage, not the fundamental voltage
/// (<see cref="IHarmonicCouplingModel.Injection"/>), and the library's only model is a Norton admittance with a constant
/// current. So T2 checks the Norton fixed point, <c>V = (Y5 + g e3 e3^T)^-1 e3 I</c>, not a V_1-dependent source.</item>
/// <item>The estimator reads bus voltage phasors only, so T3 uses the bus voltages and no branch current.</item>
/// <item>emt3's lines are modal three-phase lines that the Core partitioner cannot cut (manual ch. 19, emt3), so the
/// partitioned run is its own verb on a single-conductor network (T6), and <c>emt3 --partition</c> stays refused (T8).</item>
/// <item>The new verbs are not on the shared writer (its verb list is pinned by CliResultWriterTests and the Python wrapper
/// contract), so T7 checks their own --out documents and that --json is refused.</item>
/// <item>R419CoSimTests builds the interface before the load; the verb adds it after every element of the emt3 study, so
/// the R419 bits are not reproducible through any study file. T5 runs the R419 case through the CLI and compares it to
/// the library call in the verb's order, to the bit, and to the R419 phasor closed form at R419's own bound.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp27HarmonicCosimVerbsTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The unit roundoff, 2^-53.</summary>
    private const double U = 1.1102230246251565e-16;

    private readonly string _dir = TestTemp.Under("gridsim-wp27-" + Guid.NewGuid().ToString("N"));

    public Rc5Wp27HarmonicCosimVerbsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shared fixtures
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A meshed three-bus case: a generator at bus 1 (its harmonic source reactance is stamped by the study
    /// model), loads at buses 2 and 3 (damping shunts), three lines with charging.</summary>
    private const string ThreeBus = """
        function mpc = wp27three
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        1 3 0 0 0 0 1 1 0 33 1 1.1 0.9;
        2 1 20 5 0 0 1 1 0 33 1 1.1 0.9;
        3 1 15 4 0 0 1 1 0 33 1 1.1 0.9;
        ];
        mpc.gen = [
        1 0 0 300 -300 1 100 1 250 0;
        ];
        mpc.branch = [
        1 2 0.01 0.06 0.02 0 0 0 0 0 1 -360 360;
        2 3 0.015 0.08 0.02 0 0 0 0 0 1 -360 360;
        1 3 0.02 0.1 0.03 0 0 0 0 0 1 -360 360;
        ];
        """;

    /// <summary>One source at bus 3: 0.01 pu fundamental, 100 % at order 5, phase 0, so I_5 = 0.01 + j0 exactly.</summary>
    private const string Source5 = """
        {"schema": "gridsim-harmonics/1", "case": "wp27three", "baseMva": 100,
         "sources": [ { "bus": 3, "sourceType": "test", "fundamentalCurrentPu": 0.01,
                        "spectrum": [ { "order": 5, "magnitudePercent": 100, "phaseDeg": 0 } ] } ] }
        """;

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    /// <summary>The case as the CLI loads a .m file (Gated.Matpower: the parser, then the unit normaliser).</summary>
    private GridModel LoadThreeBus() => MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(Write("three.m", ThreeBus)));

    private static HarmonicModelOptions Order5 => HarmonicModelOptions.Study with { Orders = new[] { 5 } };

    private (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(_dir, CliProcess.IsolatedLicenceFileName);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(300_000));
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }

    private JsonElement ReadJson(string name)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_dir, name)));
        return d.RootElement.Clone();
    }

    private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

    /// <summary>Gaussian elimination with partial pivoting on a dense complex matrix (the test's own solver, independent of
    /// the library's sparse real-expanded LU).</summary>
    private static Complex[] DenseSolve(Complex[,] a0, Complex[] b0)
    {
        int n = b0.Length;
        var a = (Complex[,])a0.Clone();
        var b = (Complex[])b0.Clone();
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (a[i, k].Magnitude > a[p, k].Magnitude) p = i;
            if (p != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
                (b[k], b[p]) = (b[p], b[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                Complex f = a[i, k] / a[k, k];
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            Complex s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }

    private static Complex[,] Dense(ComplexCsr y)
    {
        var a = new Complex[y.N, y.N];
        for (int i = 0; i < y.N; i++)
            for (int k = y.RowPtr[i]; k < y.RowPtr[i + 1]; k++) a[i, y.ColIdx[k]] += new Complex(y.G[k], y.B[k]);
        return a;
    }

    /// <summary>The infinity-norm condition number, from the explicit inverse (n is 3).</summary>
    private static double KappaInf(Complex[,] a)
    {
        int n = a.GetLength(0);
        var inv = new Complex[n][];
        for (int j = 0; j < n; j++)
        {
            var e = new Complex[n];
            e[j] = Complex.One;
            inv[j] = DenseSolve(a, e);
        }
        double na = 0, ni = 0;
        for (int i = 0; i < n; i++)
        {
            double ra = 0, ri = 0;
            for (int j = 0; j < n; j++) { ra += a[i, j].Magnitude; ri += inv[j][i].Magnitude; }
            na = Math.Max(na, ra); ni = Math.Max(ni, ri);
        }
        return na * ni;
    }

    /// <summary>
    /// The forward-error bound between two backward-stable solves of the same well-posed system, relative to max |x|:
    /// LU with partial pivoting has a backward error of at most 3 n u g ||A|| (Higham, Accuracy and Stability, thm 9.5,
    /// first order), so each solution is within kappa 3 n u g of the exact one. The library solves the 6 x 6 real
    /// expansion (n 6, growth g at most 2^5); the test solves the 3 x 3 complex system (n 3, growth at most 2^2, each complex
    /// operation within sqrt(5) u). The infinity-norm kappa of the complex matrix bounds the real expansion's to a factor
    /// of 2. Doubled for the second-order terms.
    /// </summary>
    private static double SolveBound(double kappa) => 2.0 * 2.0 * kappa * (3 * 6 * 32 + 3 * 3 * Math.Sqrt(5.0) * 4) * U;

    // ---------------------------------------------------------------------------------------------------------------
    // T1, T2: harmonic-pf
    // ---------------------------------------------------------------------------------------------------------------

    private Complex[] HpfVoltages(string outName)
    {
        JsonElement r = ReadJson(outName);
        Assert.Equal("gridsim-harmonic-pf/1", r.GetProperty("schema").GetString());
        var v = new List<Complex>();
        foreach (JsonElement b in r.GetProperty("buses").EnumerateArray())
        {
            JsonElement h = b.GetProperty("harmonics")[0];
            Assert.Equal(5, h.GetProperty("order").GetInt32());
            v.Add(new Complex(h.GetProperty("re").GetDouble(), h.GetProperty("im").GetDouble()));
        }
        return v.ToArray();
    }

    [Fact]
    public void T1_harmonic_pf_linear_limit_is_the_impedance_column_times_the_injection()
    {
        GridModel model = LoadThreeBus();
        Write("src.json", Source5);
        var run = Cli("harmonic-pf", "three.m", "--spectra", "src.json", "--orders", "5", "--out", "hpf.json");
        Assert.True(run.Exit == 0, run.StdErr + run.StdOut);
        Complex[] vCli = HpfVoltages("hpf.json");
        Assert.Equal(3, vCli.Length);
        JsonElement doc = ReadJson("hpf.json");
        Assert.True(doc.GetProperty("converged").GetBoolean());
        // Experimental, as dist3ph and pmu-ingest: on the headline and in the result file.
        Assert.Contains("[EXPERIMENTAL]", run.StdOut.Split('\n')[0], StringComparison.Ordinal);
        Assert.True(doc.GetProperty("experimental").GetBoolean());
        Assert.Equal(0, doc.GetProperty("iterations").GetInt32());

        // (a) The library: one factorisation of Y(5), solved against I = 0.01 e_3 (the coupled solve with a zero coupling
        // admittance solves the unmodified Y(5) and stops when the repeat solve gives the same bits).
        ComplexCsr y5 = FrequencyDependentYBus.Build(model, 5, Order5);
        var inj = new Complex[3];
        inj[2] = new Complex(0.01, 0.0);
        Complex[] vLib = new RealExpandedComplexSolver().Factor(y5)!.Solve(inj)!;
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Bits(vLib[i].Real), Bits(vCli[i].Real));
            Assert.Equal(Bits(vLib[i].Imaginary), Bits(vCli[i].Imaginary));
        }

        // (b) The closed form: V = Z5(:, 3) 0.01 with Z5 the inverse of the dense Y(5), by the test's own elimination.
        Complex[,] yd = Dense(y5);
        var e3 = new Complex[3];
        e3[2] = Complex.One;
        Complex[] z3 = DenseSolve(yd, e3);
        double kappa = KappaInf(yd);
        double vmax = z3.Max(z => z.Magnitude) * 0.01;
        double tol = SolveBound(kappa) * vmax;
        for (int i = 0; i < 3; i++)
            Assert.True((vCli[i] - z3[i] * 0.01).Magnitude <= tol, $"bus {i + 1}: |dV| {(vCli[i] - z3[i] * 0.01).Magnitude:R} > {tol:R} (kappa {kappa:R})");
        Assert.True(vmax > 0.0);
    }

    [Fact]
    public void T2_harmonic_pf_with_a_norton_coupling_is_its_fixed_point_and_equals_the_library()
    {
        GridModel model = LoadThreeBus();
        Write("src.json", Source5);
        var run = Cli("harmonic-pf", "three.m", "--spectra", "src.json", "--orders", "5", "--norton-g", "0.02", "--out", "hpf-g.json");
        Assert.True(run.Exit == 0, run.StdErr + run.StdOut);
        Complex[] vCli = HpfVoltages("hpf-g.json");
        JsonElement doc = ReadJson("hpf-g.json");
        Assert.True(doc.GetProperty("converged").GetBoolean());
        // The Norton injection does not depend on the voltage, so the second solve repeats the first to the bit and the
        // loop stops at once: 0 further iterations (at most 2 was the spec's bound).
        Assert.Equal(0, doc.GetProperty("iterations").GetInt32());
        Assert.Equal(0.02, doc.GetProperty("nortonG").GetDouble());

        // The library call, to the bit.
        IReadOnlyList<HarmonicSource> sources = [new HarmonicSource(3, "test", 0.01, [new HarmonicComponent(5, 100.0, 0.0)])];
        CoupledHarmonicResult lib = CoupledHarmonicPowerFlow.Solve(model, sources, new NortonCouplingModel(0.02, 0.0), Order5);
        Assert.True(lib.Converged);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Bits(lib.Result.Voltage[0][i].Real), Bits(vCli[i].Real));
            Assert.Equal(Bits(lib.Result.Voltage[0][i].Imaginary), Bits(vCli[i].Imaginary));
        }

        // The closed form: (Y5 + 0.02 e3 e3^T) V = 0.01 e3, by the test's own elimination, within the derived bound.
        Complex[,] yd = Dense(FrequencyDependentYBus.Build(model, 5, Order5));
        yd[2, 2] += new Complex(0.02, 0.0);
        var rhs = new Complex[3];
        rhs[2] = new Complex(0.01, 0.0);
        Complex[] vRef = DenseSolve(yd, rhs);
        double tol = SolveBound(KappaInf(yd)) * vRef.Max(z => z.Magnitude);
        for (int i = 0; i < 3; i++)
            Assert.True((vCli[i] - vRef[i]).Magnitude <= tol, $"bus {i + 1}: |dV| {(vCli[i] - vRef[i]).Magnitude:R} > {tol:R}");
        // The coupling damps: the source bus voltage is smaller with the shunt than without it.
        Complex[] vPlain = DenseSolve(Dense(FrequencyDependentYBus.Build(model, 5, Order5)), rhs);
        Assert.True(vRef[2].Magnitude < vPlain[2].Magnitude);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // T3, T4: harmonic-se
    // ---------------------------------------------------------------------------------------------------------------

    private const double Sigma = 1e-4;

    /// <summary>The estimator's own column of Z(5) for bus 3 (the same factorisation, the same unit right-hand side).</summary>
    private static Complex[] Column3(GridModel model)
    {
        var e3 = new Complex[3];
        e3[2] = Complex.One;
        return new RealExpandedComplexSolver().Factor(FrequencyDependentYBus.Build(model, 5, HarmonicModelOptions.Study))!.Solve(e3)!;
    }

    private string MeasurementFile(string name, Complex[] z)
    {
        var sb = new StringBuilder();
        sb.Append("{\"schema\": \"gridsim-harmonic-meas/1\", \"takenUtc\": \"2026-11-01T12:00:00Z\", \"baseMva\": 100, \"orders\": [ {\"order\": 5, \"candidates\": [3], \"voltages\": [");
        for (int i = 0; i < z.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Inv, $"{{\"bus\": {i + 1}, \"re\": {z[i].Real:R}, \"im\": {z[i].Imaginary:R}, \"sigma\": {Sigma:R}}}");
        }
        sb.Append("] } ] }");
        return Write(name, sb.ToString());
    }

    /// <summary>
    /// The relative error bound of the one-candidate estimate x = (sum conj(h) w h z) / (sum conj(h) w h) when z = h x to one
    /// rounding: every term carries the phase of x, so the sums do not cancel. Numerator: conj(h) w (u), times h (sqrt5 u),
    /// times z (sqrt5 u), a sum of m terms ((m - 1) u). Gram: u + sqrt5 u + (m - 1) u. Data: u. Complex division: 7 u
    /// (Smith's algorithm, as in the 2.1.093 REI bound). Total (2m + 3 sqrt5 + 8) u at first order, doubled for the second
    /// order and the 1 x 1 dense solve.
    /// </summary>
    private static double EstimateBound(int m) => 2.0 * (2 * m + 3 * Math.Sqrt(5.0) + 8) * U;

    [Fact]
    public void T3_harmonic_se_recovers_the_injection_from_exact_measurements()
    {
        GridModel model = LoadThreeBus();
        Complex[] h = Column3(model);
        var x = new Complex(0.01, 0.0);
        Complex[] z = h.Select(v => v * x).ToArray();
        MeasurementFile("meas.json", z);
        var run = Cli("harmonic-se", "three.m", "meas.json", "--out", "se.json");
        Assert.True(run.Exit == 0, run.StdErr + run.StdOut);
        Assert.Contains("[EXPERIMENTAL]", run.StdOut.Split('\n')[0], StringComparison.Ordinal);
        Assert.True(ReadJson("se.json").GetProperty("experimental").GetBoolean());
        JsonElement o = ReadJson("se.json").GetProperty("orders")[0];
        Assert.True(o.GetProperty("observable").GetBoolean());
        Assert.False(o.GetProperty("badData").GetBoolean());
        Assert.Equal(4, o.GetProperty("degreesOfFreedom").GetInt32());
        JsonElement inj = o.GetProperty("injections")[0];
        Assert.Equal(3, inj.GetProperty("bus").GetInt32());
        var xHat = new Complex(inj.GetProperty("re").GetDouble(), inj.GetProperty("im").GetDouble());
        double rel = EstimateBound(3);
        Assert.True((xHat - x).Magnitude <= rel * x.Magnitude, $"|x - 0.01| = {(xHat - x).Magnitude:R} > {rel * x.Magnitude:R}");

        // Residuals: r_i = z_i - h_i x_hat, with z_i = h_i x (1 + d), |d| <= u: |r_i| <= |z_i| (rel + sqrt5 u + 2 u) at first order.
        foreach (JsonElement r in o.GetProperty("residuals").EnumerateArray())
        {
            int bus = r.GetProperty("bus").GetInt32();
            var res = new Complex(r.GetProperty("residualRe").GetDouble(), r.GetProperty("residualIm").GetDouble());
            double bound = 2.0 * z[bus - 1].Magnitude * (rel + (Math.Sqrt(5.0) + 2) * U);
            Assert.True(res.Magnitude <= bound, $"bus {bus}: |r| {res.Magnitude:R} > {bound:R}");
        }
        // The estimated voltages are the column times the estimate (the same products the verb forms).
        int k = 0;
        foreach (JsonElement v in o.GetProperty("voltages").EnumerateArray())
        {
            Complex ve = h[k] * xHat;
            Assert.Equal(Bits(ve.Real), Bits(v.GetProperty("re").GetDouble()));
            Assert.Equal(Bits(ve.Imaginary), Bits(v.GetProperty("im").GetDouble()));
            k++;
        }
    }

    [Fact]
    public void T4_harmonic_se_flags_a_20_sigma_error_and_points_at_it()
    {
        GridModel model = LoadThreeBus();
        Complex[] h = Column3(model);
        var x = new Complex(0.01, 0.0);
        Complex[] z = h.Select(v => v * x).ToArray();
        const int m = 3;
        // Corrupt the measurement whose share of |h|^2 is smallest (so its residual keeps most of the error).
        int k = Enumerable.Range(0, m).OrderBy(i => h[i].Magnitude).First();
        double eps = 20.0 * Sigma;
        z[k] += eps;

        // Closed forms for one candidate and equal sigma: Omega = sigma^2 (I - h h^H / |h|^2); with error eps at k only,
        // r = Omega e / sigma^2, J = eps^2 (1 - rho_k) / sigma^2 with rho_i = |h_i|^2 / |h|^2, and the normalised residual of
        // measurement i is |Omega_ik| eps / (sigma^2 sqrt(Omega_ii)).
        double hh = h.Sum(v => v.Magnitude * v.Magnitude);
        double[] rho = h.Select(v => v.Magnitude * v.Magnitude / hh).ToArray();
        double jClosed = eps * eps * (1 - rho[k]) / (Sigma * Sigma);
        var rnClosed = new double[m];
        for (int i = 0; i < m; i++)
        {
            double omegaIk = i == k ? Sigma * Sigma * (1 - rho[k]) : Sigma * Sigma * (h[i] * Complex.Conjugate(h[k])).Magnitude / hh;
            rnClosed[i] = omegaIk * eps / (Sigma * Sigma * Math.Sqrt(Sigma * Sigma * (1 - rho[i])));
        }
        // The case itself must make k the largest (Cauchy-Schwarz; strict unless two columns of Omega are parallel).
        Assert.Equal(k, Enumerable.Range(0, m).OrderByDescending(i => rnClosed[i]).First());
        // The exact chi-square(4) 0.99 quantile: 13.2767 to six figures (published tables), and the test's own root of
        // e^(-y) (1 + y) = 0.01, x = 2y (Chi4Quantile99). The case must exceed it with room.
        double exact = Chi4Quantile99();
        Assert.True(Math.Abs(exact - 13.2767) < 5e-5, $"{exact:R}");
        Assert.True(jClosed > 1.01 * exact, $"the fixture's J {jClosed:R} does not clear the threshold");

        MeasurementFile("bad.json", z);
        var run = Cli("harmonic-se", "three.m", "bad.json", "--out", "se-bad.json");
        Assert.Equal(3, run.Exit);
        JsonElement o = ReadJson("se-bad.json").GetProperty("orders")[0];
        Assert.True(o.GetProperty("badData").GetBoolean());
        double j = o.GetProperty("chiSquare").GetDouble();
        double thr = o.GetProperty("threshold").GetDouble();
        Assert.True(j > thr, $"J {j:R} <= threshold {thr:R}");
        // The Core's exact even-dof quantile (bisection on its own log-survival function) against the test's Newton root:
        // both are within a few ulps of the root of the same function (see Chi4Quantile99), so 1e-13 relative.
        Assert.True(Math.Abs(thr - exact) <= 1e-13 * exact, $"threshold {thr:R} vs {exact:R}");
        Assert.Equal(k + 1, o.GetProperty("largestNormalisedResidualBus").GetInt32());
        // J against the closed form. J = ||W^1/2 r||^2; each computed residual is within c u (|z_i| + eps) of the exact one
        // (c from EstimateBound plus the subtraction), so by Cauchy-Schwarz |dJ| <= 2 sqrt(J) d + d^2 with
        // d = sqrt(sum w (c u (|z_i| + eps))^2). The exact-data part of r is itself within that d.
        double c = EstimateBound(m) / U + Math.Sqrt(5.0) + 4;
        double d = Math.Sqrt(z.Sum(v => Math.Pow(c * U * (v.Magnitude + eps), 2)) / (Sigma * Sigma));
        // The sum of m squares adds (m + 3) u J of its own.
        double dj = 2.0 * Math.Sqrt(jClosed) * (2 * d) + 4 * d * d + (m + 3) * U * jClosed;
        Assert.True(Math.Abs(j - jClosed) <= dj, $"J {j:R} vs closed form {jClosed:R}: {Math.Abs(j - jClosed):R} > {dj:R}");
    }

    /// <summary>
    /// The 0.99 quantile of chi-square with 4 degrees of freedom by the test's own route: Q(x) = e^(-y)(1 + y) with y = x/2
    /// (the Poisson sum with k = 2), solved for Q = 0.01 by Newton on f(y) = ln(1 + y) - y - ln(0.01), f'(y) = -y / (1 + y),
    /// in System.Math, until the step is below 1 ulp. f is evaluated to a few ulps of ln 100 = 4.6, and |f'| = 0.87 at the
    /// root (y = 6.638), so the root is within about 1e-14 relative of the exact one; the Core's bisection on its own
    /// evaluation is within the same, hence the 1e-13 relative assert.
    /// </summary>
    private static double Chi4Quantile99()
    {
        double y = 6.0;
        for (int i = 0; i < 100; i++)
        {
            double f = Math.Log(1 + y) - y - Math.Log(0.01);
            double step = f / (-y / (1 + y));
            y -= step;
            if (Math.Abs(step) <= Math.BitIncrement(y) - y) break;
        }
        return 2 * y;
    }

    [Fact]
    public void T4c_the_even_dof_quantile_matches_published_values_and_the_closed_forms()
    {
        // k = 1: exponential, x = -2 ln(1 - p), exact.
        Assert.True(Math.Abs(GridSim.Core.Estimation.ChiSquare.QuantileEven(2, 0.99) - (-2 * Math.Log(0.01))) <= 1e-14 * 9.21);
        Assert.True(Math.Abs(GridSim.Core.Estimation.ChiSquare.QuantileEven(4, 0.99) - Chi4Quantile99()) <= 1e-13 * 13.28);
        // Published 0.99 quantiles (NIST/SEMATECH e-Handbook, table 1.3.6.7.4, to three decimals): 6 dof 16.812,
        // 10 dof 23.209, 20 dof 37.566, 100 dof 135.807.
        foreach ((int dof, double q) in new[] { (6, 16.812), (10, 23.209), (20, 37.566), (100, 135.807) })
            Assert.True(Math.Abs(GridSim.Core.Estimation.ChiSquare.QuantileEven(dof, 0.99) - q) < 6e-4, $"{dof}: {GridSim.Core.Estimation.ChiSquare.QuantileEven(dof, 0.99):R}");
        Assert.True(double.IsNaN(GridSim.Core.Estimation.ChiSquare.QuantileEven(3, 0.99)));
        Assert.True(double.IsNaN(GridSim.Core.Estimation.ChiSquare.QuantileEven(0, 0.99)));
        // A large dof does not underflow: 2000 degrees of freedom, mean 2000, the 0.99 quantile about 2150.
        double big = GridSim.Core.Estimation.ChiSquare.QuantileEven(2000, 0.99);
        Assert.InRange(big, 2140.0, 2160.0);
    }

    [Fact]
    public void T4b_the_measurement_reader_refuses_branch_currents_and_a_future_date()
    {
        string p = Write("cur.json", """
            {"schema": "gridsim-harmonic-meas/1", "takenUtc": "2026-11-01T12:00:00Z", "baseMva": 100,
             "orders": [ {"order": 5, "candidates": [3], "voltages": [ {"bus": 1, "re": 0, "im": 0, "sigma": 1e-4} ],
                          "currents": [] } ] }
            """);
        var ex = Assert.Throws<InvalidDataException>(() => HarmonicMeasurementSet.Load(p));
        Assert.Contains("branch current", ex.Message, StringComparison.Ordinal);
        string f = Write("future.json", """
            {"schema": "gridsim-harmonic-meas/1", "takenUtc": "2026-11-01T12:00:00Z", "baseMva": 100,
             "orders": [ {"order": 5, "candidates": [3], "voltages": [ {"bus": 1, "re": 0, "im": 0, "sigma": 1e-4} ] } ] }
            """);
        Assert.Throws<GridSim.Core.Diagnostics.ForwardInferenceException>(() =>
            HarmonicMeasurementSet.Load(f, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Single(HarmonicMeasurementSet.Load(f, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc)).Orders);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // T5: cosim
    // ---------------------------------------------------------------------------------------------------------------

    // The R419 co-simulation case: a 10 kV-peak source behind a lossless line (Zc 300 ohm, tau 0.5 ms) as the RMS side; the
    // EMT side the parallel R-L load (300 ohm, 0.477 H) with a 60 ohm shunt switched in at 0.2 s; the source steps to
    // 11 kV peak at 0.3105 s; step 50 us, exchange every 20 steps (1 ms).
    internal const string CoSimStudy = """
        {
          "schema": "gridsim-cosim/1",
          "boundary_bus": "B",
          "form": "thevenin",
          "exchange_every_steps": 20,
          "rms": { "source_re_v": 0, "source_im_v": -10000, "line": { "surge_ohm": 300, "travel_s": 5e-4 },
                   "source_changes": [ { "at_s": 0.3105, "re_v": 0, "im_v": -11000 } ] },
          "emt3": {
            "schema": "gridsim-emt3/1",
            "time_step_s": 5e-5,
            "duration_s": 0.35,
            "frequency_hz": 50,
            "buses": ["B", "X"],
            "shunts": [ { "name": "load", "bus": "B", "r_ohm": 300, "l_h": 0.477 }, { "name": "rx", "bus": "X", "r_ohm": 60 } ],
            "switches": [ { "name": "sw", "from": "B", "to": "X", "ron_ohm": 1e-4, "roff_ohm": 1e9, "events": [ { "at_s": 0.2, "close": true } ] } ],
            "outputs": [
              { "name": "vb_a", "kind": "voltage", "target": "B", "phase": "a" },
              { "name": "vb_b", "kind": "voltage", "target": "B", "phase": "b" },
              { "name": "vb_c", "kind": "voltage", "target": "B", "phase": "c" }
            ]
          }
        }
        """;

    /// <summary>The same study built through the library, in the verb's order: the emt3 elements, then the interface.</summary>
    private static (Emt3Result Res, RmsEmtInterface Itf) CoSimLibrary()
    {
        var study = new Emt3Study(5e-5, 0.35, 1, 50.0, IntegrationRule.Trapezoidal, NodalEventRule.Trapezoidal, new NewtonPolicy(),
            ["B", "X"], [], [], [new Emt3Shunt("load", "B", 300.0, 0.0, 0.477), new Emt3Shunt("rx", "X", 60.0, 0.0, 0.0)], [], [], [], [],
            [new Emt3Switch("sw", "B", "X", "abc", 1e-4, 1e9, false, [new Emt3SwitchEvent(0.2, true)])], [],
            [new Emt3Output("vb_a", "voltage", "B", "a", "from"), new Emt3Output("vb_b", "voltage", "B", "b", "from"), new Emt3Output("vb_c", "voltage", "B", "c", "from")],
            null, null, null, null);
        var side = new PhasorSide(new Complex(0.0, -10000.0), 50.0, 300.0, 5e-4);
        side.ScheduleSourceChange(0.3105, new Complex(0.0, -11000.0));
        RmsEmtInterface? itf = null;
        Emt3Result res = Emt3Runner.Run(study, (net, nodes) => itf = RmsEmtInterface.Add(net, "rms", "", nodes("B"), side, new CoSimOptions(CoSimForm.Thevenin, 20, 50.0), 5e-5));
        return (res, itf!);
    }

    private static string R(double v) => v.ToString("R", Inv);

    [Fact]
    public void T5_cosim_through_the_cli_equals_the_library_call_to_the_bit()
    {
        Write("cosim.json", CoSimStudy);
        var run = Cli("cosim", "cosim.json", "--out", "cosim-out.json", "--boundary-csv", "boundary.csv");
        Assert.True(run.Exit == 0, run.StdErr + run.StdOut);
        (Emt3Result lib, RmsEmtInterface itf) = CoSimLibrary();

        // The boundary CSV: every exchange, every number, as the exact round-trip text of the library's value.
        string[] lines = File.ReadAllText(Path.Combine(_dir, "boundary.csv")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("time_s,valid,emt_v_re,emt_v_im,emt_i_re,emt_i_im,rms_v_re,rms_v_im,emf_re,emf_im,z_re,z_im", lines[0]);
        Assert.Equal(itf.Exchanges.Count + 1, lines.Length);
        Assert.Equal(350, itf.Exchanges.Count);   // floor(0.35 / (20 x 50 us))
        for (int i = 0; i < itf.Exchanges.Count; i++)
        {
            ExchangeRecord e = itf.Exchanges[i];
            string expected = string.Join(",", R(e.TimeSeconds), e.MeasurementValid ? "1" : "0", R(e.EmtVoltage.Real), R(e.EmtVoltage.Imaginary),
                R(e.EmtCurrent.Real), R(e.EmtCurrent.Imaginary), R(e.RmsVoltage.Real), R(e.RmsVoltage.Imaginary),
                R(e.Equivalent.Emf.Real), R(e.Equivalent.Emf.Imaginary), R(e.Equivalent.Impedance.Real), R(e.Equivalent.Impedance.Imaginary));
            Assert.Equal(expected, lines[i + 1]);
        }

        // Not the same code twice: the RMS side of every valid exchange before the source step against the test's own
        // closed form. The equivalent is E / cos(w tau) and j Zc tan(w tau) (the line's ABCD constants, System.Math here,
        // DetMath in the Core: each sin / cos within 1 ulp), and the RMS voltage is that equivalent less Z I_emt. Bound: the
        // sin, cos, product and complex divide give at most 8 u relative on Emf and Z; the complex product and difference
        // add 4 u (|Emf| + |Z| |I|). So |dV| <= 16 u (|Emf| + |Z| |I|).
        double wt = 2.0 * Math.PI * 50.0 * 5e-4;
        Complex emf0 = new Complex(0.0, -10000.0) / Math.Cos(wt), z0 = new Complex(0.0, 300.0 * Math.Sin(wt) / Math.Cos(wt));
        int checkedRows = 0;
        foreach (ExchangeRecord e in itf.Exchanges.Where(e => e.MeasurementValid && e.TimeSeconds < 0.3105 - 1e-3))
        {
            Assert.True((e.Equivalent.Emf - emf0).Magnitude <= 8 * U * emf0.Magnitude, $"{e.TimeSeconds:R}: Emf {e.Equivalent.Emf}");
            Assert.True((e.Equivalent.Impedance - z0).Magnitude <= 8 * U * z0.Magnitude, $"{e.TimeSeconds:R}: Z {e.Equivalent.Impedance}");
            Complex v = emf0 - z0 * e.EmtCurrent;
            double bound = 16 * U * (emf0.Magnitude + z0.Magnitude * e.EmtCurrent.Magnitude);
            Assert.True((e.RmsVoltage - v).Magnitude <= bound, $"{e.TimeSeconds:R}: |dV| {(e.RmsVoltage - v).Magnitude:R} > {bound:R}");
            checkedRows++;
        }
        Assert.True(checkedRows > 280, $"{checkedRows} rows checked");

        // The EMT traces, to the bit.
        JsonElement doc = ReadJson("cosim-out.json");
        Assert.Equal("gridsim-cosim/1", doc.GetProperty("schema").GetString());
        Assert.Equal(itf.Description, doc.GetProperty("interface").GetString());
        double[] times = doc.GetProperty("timeS").EnumerateArray().Select(t => t.GetDouble()).ToArray();
        Assert.Equal(lib.Run.Times.Length, times.Length);
        for (int i = 0; i < times.Length; i++) Assert.Equal(Bits(lib.Run.Times[i]), Bits(times[i]));
        for (int k = 0; k < 3; k++)
        {
            double[] s = doc.GetProperty("traces").GetProperty(lib.Run.ProbeNames[k]).EnumerateArray().Select(t => t.GetDouble()).ToArray();
            Assert.Equal(lib.Run.Series[k].Length, s.Length);
            for (int i = 0; i < s.Length; i++) Assert.Equal(Bits(lib.Run.Series[k][i]), Bits(s[i]));
        }

        // The physics, at R419's own bound, taken from R419CoSimTests (3e-3 between the co-simulation and the phasor closed
        // form there; not derived here):
        // before the load step, |V_B| = |E_th Z_L / (Z_th + Z_L)| with E_th = E / cos(w tau), Z_th = j Zc tan(w tau).
        double w = 2.0 * Math.PI * 50.0, th = w * 5e-4;
        Complex eth = new Complex(0.0, -10000.0) / Math.Cos(th), zth = new Complex(0.0, 300.0 * Math.Tan(th));
        Complex zl = 1.0 / (1.0 / 300.0 + 1.0 / new Complex(0.0, w * 0.477));
        double vClosed = (eth * zl / (zth + zl)).Magnitude;
        double vCo = R419MmcTests.Ph(lib.Run, lib.Run.Series[0], 0.12, 0.20).Magnitude;
        Assert.InRange(Math.Abs(vCo - vClosed) / vClosed, 0.0, 3e-3);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // T6: emt-partition
    // ---------------------------------------------------------------------------------------------------------------

    // Two areas joined by two Bergeron lines (one whose travel time is not a whole number of steps, so it interpolates),
    // a fault switch that closes and opens in area A.
    private const string PartitionStudy = """
        {
          "schema": "gridsim-emt-partition/1",
          "time_step_s": 5e-5,
          "duration_s": 0.05,
          "sources": [
            { "name": "ea", "node": "a1", "r_ohm": 0.5, "amplitude_v": 9000, "frequency_hz": 50 },
            { "name": "eb", "node": "b1", "r_ohm": 0.5, "amplitude_v": 9500, "frequency_hz": 50, "phase_deg": -30 }
          ],
          "branches": [
            { "name": "la", "kind": "series-rl", "from": "a1", "to": "gnd", "r_ohm": 5, "l_h": 0.02 },
            { "name": "ca", "kind": "capacitor", "from": "a1", "to": "gnd", "c_f": 2e-6 },
            { "name": "lb", "kind": "series-rl", "from": "b1", "to": "gnd", "r_ohm": 6, "l_h": 0.02 },
            { "name": "cb", "kind": "capacitor", "from": "b1", "to": "gnd", "c_f": 2e-6 }
          ],
          "switches": [
            { "name": "fa", "from": "a1", "to": "gnd", "ron_ohm": 1e-3, "events": [ { "at_s": 0.0123, "close": true }, { "at_s": 0.0273, "close": false } ] }
          ],
          "lines": [
            { "name": "l1", "from": "a1", "to": "b1", "surge_ohm": 350, "travel_s": 4.1e-4, "attenuation": 0.999 },
            { "name": "l2", "from": "a1", "to": "b1", "surge_ohm": 350, "travel_s": 3e-4 }
          ],
          "outputs": [
            { "name": "va", "kind": "voltage", "target": "a1" },
            { "name": "vb", "kind": "voltage", "target": "b1" },
            { "name": "ifa", "kind": "current", "target": "fa" },
            { "name": "ila", "kind": "current", "target": "la" }
          ]
        }
        """;

    /// <summary>The same network through the library, in the verb's order (sources, branches, switches, lines).</summary>
    private static PartitionedResult PartitionLibrary(int threads)
    {
        PartitionedModel m = PartitionedModel.Create(["p1", "p2"], singlePartition: false);
        double w = 2.0 * Math.PI * 50.0, pha = 0.0 * Math.PI / 180.0, phb = -30.0 * Math.PI / 180.0;
        m.Add("p1", new NodalVoltageSource("ea", m.Node("p1", "a1"), 0.5, t => 9000.0 * DetMath.Sin(w * t + pha)));
        m.Add("p2", new NodalVoltageSource("eb", m.Node("p2", "b1"), 0.5, t => 9500.0 * DetMath.Sin(w * t + phb)));
        m.Add("p1", NodalBranch.SeriesRl("la", m.Node("p1", "a1"), m.Node("p1", "gnd"), 5.0, 0.02));
        m.Add("p1", NodalBranch.Capacitor("ca", m.Node("p1", "a1"), m.Node("p1", "gnd"), 2e-6));
        m.Add("p2", NodalBranch.SeriesRl("lb", m.Node("p2", "b1"), m.Node("p2", "gnd"), 6.0, 0.02));
        m.Add("p2", NodalBranch.Capacitor("cb", m.Node("p2", "b1"), m.Node("p2", "gnd"), 2e-6));
        m.Add("p1", new NodalSwitch("fa", m.Node("p1", "a1"), m.Node("p1", "gnd"), 1e-3, 1e9, false));
        m.AddLine("l1", "p1", "a1", "p2", "b1", 350.0, 4.1e-4, 0.999);
        m.AddLine("l2", "p1", "a1", "p2", "b1", 350.0, 3e-4, 1.0);
        List<NodalProbe> probes =
        [
            new("va", NodalProbeKind.NodeVoltage, "p1/a1"), new("vb", NodalProbeKind.NodeVoltage, "p2/b1"),
            new("ifa", NodalProbeKind.ElementCurrent, "fa"), new("ila", NodalProbeKind.ElementCurrent, "la"),
        ];
        List<NodalEvent> events = [new(0.0123, "fa", true), new(0.0273, "fa", false)];
        return PartitionedEngine.Run(m, 5e-5, 0.05, probes, events, new PartitionedOptions { Threads = threads, Nodal = new NodalOptions { SampleEvery = 1 } });
    }

    private void AssertTracesEqual(NodalResult lib, string outName)
    {
        JsonElement doc = ReadJson(outName);
        Assert.Equal("Completed", doc.GetProperty("status").GetString());
        double[] times = doc.GetProperty("timeS").EnumerateArray().Select(t => t.GetDouble()).ToArray();
        Assert.Equal(lib.Times.Length, times.Length);
        for (int i = 0; i < times.Length; i++) Assert.Equal(Bits(lib.Times[i]), Bits(times[i]));
        for (int k = 0; k < lib.ProbeNames.Count; k++)
        {
            double[] s = doc.GetProperty("traces").GetProperty(lib.ProbeNames[k]).EnumerateArray().Select(t => t.GetDouble()).ToArray();
            Assert.Equal(lib.Series[k].Length, s.Length);
            for (int i = 0; i < s.Length; i++) Assert.Equal(Bits(lib.Series[k][i]), Bits(s[i]));
        }
    }

    [Fact]
    public void T6_emt_partition_equals_the_partitioned_engine_and_auto_makes_the_same_split()
    {
        Write("part.json", PartitionStudy);
        Write("cut.json", """{"schema": "gridsim-emt-cut/1", "cut": ["l1", "l2"]}""");
        PartitionedResult lib = PartitionLibrary(1);
        Assert.Equal(NodalStatus.Completed, lib.Run.Status);
        Assert.Equal(2, lib.Plan.Partitions);
        // The window is the shortest cut travel time in whole steps: floor(3e-4 / 5e-5) = 6.
        Assert.Equal(6, lib.Plan.WindowSteps);

        var file = Cli("emt-partition", "part.json", "--partition", "cut.json", "--out", "p-file.json");
        Assert.True(file.Exit == 0, file.StdErr + file.StdOut);
        AssertTracesEqual(lib.Run, "p-file.json");
        JsonElement d = ReadJson("p-file.json");
        Assert.Equal(2, d.GetProperty("partitions").GetInt32());
        Assert.Equal(new[] { "l1", "l2" }, d.GetProperty("cutLines").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(6, d.GetProperty("windowSteps").GetInt32());

        // --partition auto cuts both lines (both at least one step), so the split and the bits are the same.
        var auto = Cli("emt-partition", "part.json", "--partition", "auto", "--out", "p-auto.json");
        Assert.True(auto.Exit == 0, auto.StdErr + auto.StdOut);
        AssertTracesEqual(lib.Run, "p-auto.json");
        Assert.Equal(new[] { "l1", "l2" }, ReadJson("p-auto.json").GetProperty("cutLines").EnumerateArray().Select(x => x.GetString()).ToArray());

        // Two threads: the same bits (the engine's contract).
        var two = Cli("emt-partition", "part.json", "--partition", "cut.json", "--threads", "2", "--out", "p-two.json");
        Assert.True(two.Exit == 0, two.StdErr + two.StdOut);
        AssertTracesEqual(lib.Run, "p-two.json");

        // The single-network form of the same partitions on the incumbent solver: the same bits (the engine's oracle).
        var mono = Cli("emt-partition", "part.json", "--partition", "cut.json", "--monolithic", "--out", "p-mono.json");
        Assert.True(mono.Exit == 0, mono.StdErr + mono.StdOut);
        AssertTracesEqual(lib.Run, "p-mono.json");
        Assert.Equal("monolithic", ReadJson("p-mono.json").GetProperty("form").GetString());
    }

    [Fact]
    public void T6b_a_cut_line_shorter_than_the_step_is_refused()
    {
        Write("short.json", PartitionStudy.Replace("\"travel_s\": 3e-4", "\"travel_s\": 3e-5", StringComparison.Ordinal));
        Write("cut.json", """{"schema": "gridsim-emt-cut/1", "cut": ["l1", "l2"]}""");
        var run = Cli("emt-partition", "short.json", "--partition", "cut.json");
        Assert.Equal(1, run.Exit);
        Assert.Contains("shorter than the step", run.StdErr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // T7: result documents; the shared writer is not used
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void T7_the_new_verbs_write_their_own_documents_and_refuse_the_shared_writer_flags()
    {
        LoadThreeBus();
        Write("src.json", Source5);
        var json = Cli("harmonic-pf", "three.m", "--spectra", "src.json", "--json", "x.json");
        Assert.Equal(2, json.Exit);
        Assert.False(File.Exists(Path.Combine(_dir, "x.json")));
        Assert.Equal(2, Cli("harmonic-se", "three.m", "m.json", "--csv", "x.csv").Exit);
        // An existing --out is never replaced.
        File.WriteAllText(Path.Combine(_dir, "taken.json"), "keep");
        Assert.Equal(2, Cli("harmonic-pf", "three.m", "--spectra", "src.json", "--out", "taken.json").Exit);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_dir, "taken.json")));
        // Each verb's own schema id.
        Assert.Equal(0, Cli("harmonic-pf", "three.m", "--spectra", "src.json", "--orders", "5", "--out", "a.json").Exit);
        Assert.Equal("gridsim-harmonic-pf/1", ReadJson("a.json").GetProperty("schema").GetString());
        // Each new verb is classified re-runnable by breaking-change validate (its inputs are command-line files).
        var none = new HashSet<string>(StringComparer.Ordinal);
        foreach (string v in new[] { "harmonic-pf", "harmonic-se", "cosim", "emt-partition" })
            Assert.Null(ReplayPolicy.Refusal(v, [v, "x", "--out", "o.json"], none));
        Assert.Contains(("cosim", "--boundary-csv"), ReplayPolicy.OutputPathFlags);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // T8: emt3 is unchanged
    // ---------------------------------------------------------------------------------------------------------------

    private const string SmallEmt3 = """
        {
          "schema": "gridsim-emt3/1",
          "time_step_s": 5e-5,
          "duration_s": 0.05,
          "frequency_hz": 50,
          "buses": ["S", "B", "X"],
          "sources": [ { "name": "src", "bus": "S", "v_ll_rms": 11000, "rs_ohm": 0.1, "ls_h": 0.01 } ],
          "branches": [ { "name": "feeder", "from": "S", "to": "B", "r1_ohm": 0.5, "l1_h": 0.02 } ],
          "shunts": [ { "name": "load", "bus": "B", "r_ohm": 300, "l_h": 0.477 }, { "name": "extra", "bus": "X", "r_ohm": 60 } ],
          "switches": [ { "name": "brk", "from": "B", "to": "X", "ron_ohm": 1e-4, "events": [ { "at_s": 0.02, "close": true } ] } ],
          "outputs": [
            { "name": "vb_a", "kind": "voltage", "target": "B", "phase": "a" },
            { "name": "vb_b", "kind": "voltage", "target": "B", "phase": "b" },
            { "name": "i_feeder_a", "kind": "current", "target": "feeder", "phase": "a" }
          ]
        }
        """;

    /// <summary>SHA-256 of a text with CRLF made LF and, for a result document, its "tool" line (the build stamp) and its "buildStatus" line left out.</summary>
    private static string Sha(string text, bool dropTool)
    {
        string t = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (dropTool) t = string.Join("\n", t.Split('\n').Where(l => !l.StartsWith("  \"tool\": ", StringComparison.Ordinal) && !l.StartsWith("  \"buildStatus\": ", StringComparison.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(t)));
    }

    // Pinned from the 2.1.132 Release CLI (built from the 2.1.132 tree, Core dll sha256 4fa37d04...) before this package's
    // edits, run exactly as here: a fresh working folder, "emt3 <name>.json --json <name>-out.json", the licence isolated;
    // JSON hashed with CRLF made LF and the tool line out, stdout with CRLF made LF.
    [Theory]
    [InlineData("small", "da5ec7c1ac414c827f3b5c22d2fd9c3da7cdbba98788238ec308a7bb5665bd87", "8f6c246ddb2fdb83f3dc68d2597d7560a28525cec63eabfb6a5383fac6b91971")]
    [InlineData("lcc", "531fce5d1af4d13f14f623eee7630601f41e05b34b0797d349c0b61f5075b597", "c0f89bbd45415192e615eb3790ab89d0062772f8ec3f268bb75f1459f5c4325b")]
    public void T8_emt3_writes_the_2_1_132_bytes(string name, string jsonSha, string stdoutSha)
    {
        string text = name == "small"
            ? SmallEmt3
            : File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "examples", "emt3", "hvdc-lcc-bridge.json"));
        Write(name + ".json", text);
        var run = Cli("emt3", name + ".json", "--json", name + "-out.json");
        Assert.Equal(0, run.Exit);
        Assert.Equal(jsonSha, Sha(File.ReadAllText(Path.Combine(_dir, name + "-out.json")), dropTool: true));
        Assert.Equal(stdoutSha, Sha(run.StdOut, dropTool: false));
    }

    [Fact]
    public void T8b_emt3_still_refuses_a_partition_flag()
    {
        Write("small.json", SmallEmt3);
        var run = Cli("emt3", "small.json", "--partition", "auto");
        Assert.Equal(2, run.Exit);
    }
}
