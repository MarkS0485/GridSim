using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F-086: <see cref="SolverPropertyTests"/> reached the stress cases (RTS-79 24-bus, 33 generators; the 145-bus
/// system) but only ever solved them exactly as shipped, so the "property" tests were single-point checks there. These
/// tests PERTURB the two stress cases and require every law a converged solution must obey to hold at each perturbed
/// operating point.
///
/// <para>Perturbation (deterministic, no System.Random: a fixed SplitMix64 stream per seed, so every run, CPU and OS sees
/// the same models): every bus load Pd and Qd is scaled by (1 + g + 0.02 n), where g is a global factor and n a per-bus
/// value in [-1, 1); every in-service generator NOT at the slack bus has its Pg scaled by (1 + g), so the slack only
/// absorbs the per-bus spread and the change in losses, never the whole swing. g runs over -3 %, 0 and +3 %.</para>
///
/// <para>Bounds are the existing ones, not loosened: the Y-bus recomputation uses the same 1e-4 MW / MVAr bound as
/// SolverPropertyTests (solver tolerance 1e-8 pu, a factor of 100 for summation order); the balance identities use the
/// same 6 and 4 decimal places; determinism and dense/sparse agreement are exact, bit for bit, as
/// DenseSparseParityTests requires of every case.</para>
/// </summary>
public class F086StressCasePerturbationPropertyTests
{
    // Global factors x seeds per case: 3 x 4 = 12 perturbed operating points for each stress case.
    private static readonly double[] GlobalFactors = { -0.03, 0.0, 0.03 };
    private static readonly ulong[] Seeds = { 1, 2, 3, 4 };

    public static IEnumerable<object[]> Points()
    {
        foreach (string c in new[] { "24", "145" })
            foreach (double g in GlobalFactors)
                foreach (ulong s in Seeds)
                    yield return new object[] { c, g, s };
    }

    private static GridModel Base(string name) => name switch
    {
        "24" => IeeeCases.Case24Rts(),
        "145" => IeeeCases.Case145(),
        _ => throw new ArgumentException($"unknown stress case {name}", nameof(name)),
    };

    /// <summary>SplitMix64: a tiny, fully specified generator, so the perturbation never depends on a library version.</summary>
    private struct SplitMix64
    {
        private ulong _state;
        public SplitMix64(ulong seed) => _state = seed;

        /// <summary>The next value in [-1, 1), built from the top 53 bits (exact in a double).</summary>
        public double NextSigned()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            double u = (z >> 11) * (1.0 / 9007199254740992.0);   // [0, 1)
            return 2.0 * u - 1.0;
        }
    }

    /// <summary>The perturbed model. spread = 0 and g = 0 give factors of exactly 1.0, so the copy is the base case.</summary>
    internal static GridModel Perturb(GridModel m, double g, ulong seed, double spread = 0.02)
    {
        var rng = new SplitMix64(seed);
        var buses = new Bus[m.Buses.Count];
        for (int i = 0; i < buses.Length; i++)
        {
            Bus b = m.Buses[i];
            double k = 1.0 + g + spread * rng.NextSigned();
            Assert.True(k > 0.9 && k < 1.1, $"perturbation factor {k} out of its design range");   // guards the design itself
            buses[i] = b with { Pd = b.Pd * k, Qd = b.Qd * k };
        }

        int slackId = m.Buses.Single(b => b.Type == BusType.Slack).Id;
        var gens = m.AllGenerators
            .Select(x => x.InService && x.Bus != slackId ? x with { Pg = x.Pg * (1.0 + g) } : x)
            .ToArray();
        return m.CopyWith(buses: buses, generators: gens, generatorsAreWholeFleet: true);
    }

    private static (GridModel Model, PowerFlowResult Result) Solve(string name, double g, ulong seed)
    {
        GridModel m = Perturb(Base(name), g, seed);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged, $"case{name} g={g} seed={seed} did not converge (status {r.Status}, mismatch {r.MaxMismatch:E2})");
        return (m, r);
    }

    // ---- Plumbing identity ----------------------------------------------------------------------

    [Theory]
    [InlineData("24")]
    [InlineData("145")]
    public void F086_zero_perturbation_reproduces_the_base_solve_bit_for_bit(string name)
    {
        GridModel baseModel = Base(name);
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(baseModel);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(Perturb(baseModel, 0.0, 1, spread: 0.0));
        AssertBitIdentical($"case{name} zero perturbation", a, b);
    }

    [Theory]
    [InlineData("24")]
    [InlineData("145")]
    public void F086_perturbation_actually_moves_the_operating_point(string name)
    {
        // A perturbation that changed nothing would make every property below a repeat of the shipped-case tests.
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(Base(name));
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(Perturb(Base(name), 0.03, 1));
        Assert.True(a.Converged && b.Converged);
        Assert.NotEqual(a.TotalLossMw, b.TotalLossMw);
        Assert.Contains(Enumerable.Range(0, a.Vm.Length), i => a.Vm[i] != b.Vm[i]);
    }

    // ---- Laws at every perturbed point ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Points))]
    public void F086_perturbed_solve_meets_the_specified_injections_recomputed_from_the_ybus(string name, double g, ulong seed)
    {
        (GridModel m, PowerFlowResult r) = Solve(name, g, seed);

        YBus y = YBus.Build(m);
        int n = m.BusCount;
        var v = new System.Numerics.Complex[n];
        for (int i = 0; i < n; i++) v[i] = System.Numerics.Complex.FromPolarCoordinates(r.Vm[i], r.VaDeg[i] * Math.PI / 180.0);

        int checkedP = 0, checkedQ = 0;
        for (int i = 0; i < n; i++)
        {
            Bus b = m.Buses[i];
            if (b.Zip is not null) continue;
            System.Numerics.Complex current = System.Numerics.Complex.Zero;
            for (int j = 0; j < n; j++) current += y.Y[i, j] * v[j];
            System.Numerics.Complex s = v[i] * System.Numerics.Complex.Conjugate(current) * m.BaseMva;

            BusType solvedAs = r.SolvedBusTypes is null ? b.Type : r.SolvedBusTypes[i];
            Generator[] here = m.Generators.Where(gg => gg.Bus == b.Id).ToArray();
            if (b.Type != BusType.Slack)
            {
                double pSpec = here.Sum(gg => gg.Pg) - b.Pd;
                Assert.True(Math.Abs(s.Real - pSpec) < 1e-4,
                    $"case{name} g={g} seed={seed} bus {b.Id}: P from V and Y {s.Real:F8} MW vs specified {pSpec:F8} MW");
                checkedP++;
            }
            if (solvedAs == BusType.Pq && here.Length == 0)
            {
                Assert.True(Math.Abs(s.Imaginary - (-b.Qd)) < 1e-4,
                    $"case{name} g={g} seed={seed} bus {b.Id}: Q from V and Y {s.Imaginary:F8} MVAr vs specified {-b.Qd:F8} MVAr");
                checkedQ++;
            }
        }
        Assert.True(checkedP > 0 && checkedQ > 0, $"case{name}: nothing was checked (P {checkedP}, Q {checkedQ})");
    }

    [Theory]
    [MemberData(nameof(Points))]
    public void F086_perturbed_solve_balances_and_the_slack_closes_it(string name, double g, ulong seed)
    {
        (GridModel m, PowerFlowResult r) = Solve(name, g, seed);

        Assert.Equal(r.TotalLossMw, r.PInjMw.Sum(), 6);
        Assert.Equal(r.TotalLossMvar, r.QInjMvar.Sum(), 6);

        int slackId = m.Buses.Single(b => b.Type == BusType.Slack).Id;
        int slackIdx = r.BusIds.ToList().IndexOf(slackId);
        Assert.True(slackIdx >= 0, "slack bus id not present in the result");
        double nonSlackGen = m.Generators.Where(gg => gg.Bus != slackId).Sum(gg => gg.Pg);
        double expectedSlackGen = m.Buses.Sum(b => b.Pd) + r.TotalLossMw - nonSlackGen;
        Assert.Equal(expectedSlackGen, r.PInjMw[slackIdx] + m.Buses[slackIdx].Pd, 4);
    }

    [Theory]
    [MemberData(nameof(Points))]
    public void F086_perturbed_solve_is_deterministic_and_dense_equals_sparse_bit_for_bit(string name, double g, ulong seed)
    {
        GridModel m = Perturb(Base(name), g, seed);
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m);
        PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(Perturb(Base(name), g, seed));   // rebuilt from scratch
        Assert.True(a.Converged, $"case{name} g={g} seed={seed} did not converge");
        AssertBitIdentical($"case{name} g={g} seed={seed} dense twice", a, b);

        PowerFlowResult s = new SparseNewtonRaphsonPowerFlow().Solve(m);
        AssertBitIdentical($"case{name} g={g} seed={seed} dense vs sparse", a, s);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertBitIdentical(string label, PowerFlowResult a, PowerFlowResult b)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Status, b.Status);
        Assert.True(a.Iterations == b.Iterations, $"{label}: iterations {a.Iterations} vs {b.Iterations}");
        Bits(label, "MaxMismatch", a.MaxMismatch, b.MaxMismatch);
        Bits(label, "TotalLossMw", a.TotalLossMw, b.TotalLossMw);
        Bits(label, "TotalLossMvar", a.TotalLossMvar, b.TotalLossMvar);
        Assert.Equal(a.BusIds, b.BusIds);
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Bits(label, $"Vm[{i}]", a.Vm[i], b.Vm[i]);
            Bits(label, $"VaDeg[{i}]", a.VaDeg[i], b.VaDeg[i]);
            Bits(label, $"PInjMw[{i}]", a.PInjMw[i], b.PInjMw[i]);
            Bits(label, $"QInjMvar[{i}]", a.QInjMvar[i], b.QInjMvar[i]);
        }
        Assert.Equal(a.SolvedBusTypes is null, b.SolvedBusTypes is null);
        if (a.SolvedBusTypes is not null) Assert.Equal(a.SolvedBusTypes, b.SolvedBusTypes!);
    }

    private static void Bits(string label, string field, double a, double b)
    {
        if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b))
            Assert.Fail($"{label}: {field} {a:R} vs {b:R} (diff {a - b:E3})");
    }
}
