using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.FrequencyDependent;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Lines;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-17: the R4-16 nodal solver gained additive hooks (a per-step element pre-step and a matrix-varying flag). This pins
/// the exact bit pattern of two R4-16 studies, recorded on the solver BEFORE the hooks were added, so the hooks provably leave
/// every existing result unchanged (new coverage, not a moved expectation).
/// </summary>
public sealed class R417SolverBaselineTests(ITestOutputHelper output)
{
    private static LineGeometry Overhead(double f = 50) => new(
        [new ConductorData("A", ConductorRole.Phase, -4, 15, 0.07, 0.0116, 0.0149),
         new ConductorData("B", ConductorRole.Phase, 0, 15, 0.07, 0.0116, 0.0149),
         new ConductorData("C", ConductorRole.Phase, 4, 15, 0.07, 0.0116, 0.0149)],
        f, 100.0, EarthReturnMethod.EarthIntegral, true);

    private static readonly (double X, double Y)[] Curve = [(4.0, 1.0), (4.5, 51.0)];

    private static Emt3Study Mixed(double dt) => new(dt, 0.05, 2, 50, IntegrationRule.Trapezoidal, NodalEventRule.BackwardEulerStep, new NewtonPolicy(),
        ["S", "M", "R"], [new Emt3Source("G", "S", 11_000, 50, 0, 0.5, 0.0)],
        [new Emt3Branch("B", "S", "M", 0.5, 0.01, 1.5, 0.03)], [new Emt3Shunt("C", "M", 0, 1e-6, 0)],
        [new Emt3Line("L1", "M", "R", 30, "fd", Overhead(), new FdFitOptions { BandHzLow = 10, BandHzHigh = 5000, Samples = 60, Order = 6 })],
        [new Emt3SaturableTransformer("T", "S", "M", 0.1, 0.02, 0.1, 0.02, 0, Curve, [0.4, -0.2, 0.0])], [],
        [new Emt3Arrester("ZnO", "R", "abc", 12_000, 1.0, 20, null)], [],
        [new Emt3Fault("F", "R", "a", 5.0, true, 0.02, 0.03)],
        [new Emt3Output("vr", "voltage", "R", "a", ""), new Emt3Output("ib", "current", "B", "b", ""), new Emt3Output("lam", "flux", "T", "a", "")]);

    private static ulong Hash(NodalResult r)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long bits) { for (int i = 0; i < 8; i++) { h ^= (ulong)((bits >> (8 * i)) & 0xff); h *= 1099511628211UL; } }
        foreach (double[] s in r.Series) foreach (double x in s) Mix(BitConverter.DoubleToInt64Bits(x));
        foreach (double x in r.Times) Mix(BitConverter.DoubleToInt64Bits(x));
        Mix(r.Stats.Steps); Mix(r.Stats.Refactorisations); Mix(r.Stats.NewtonIterations); Mix(r.Stats.MaxNewtonIterations);
        return h;
    }

    private static NodalResult SwitchedRlc()
    {
        var net = new NodalNetwork();
        int s = net.Node("S"), a = net.Node("A"), b = net.Node("B");
        net.Add(new NodalVoltageSource("E", s, 0.0, t => 1000.0 * GridSim.Core.Numerics.DetMath.Sin(2 * Math.PI * 50 * t)));
        net.Add(new NodalSwitch("SW", s, a, 1e-6, 1e9, false));
        net.Add(NodalBranch.SeriesRl("RL", a, b, 10, 0.1));
        net.Add(NodalBranch.Capacitor("C", b, NodalNetwork.Ground, 100e-6));
        return NodalSolver.Run(net, 2e-5, 0.1, [new NodalProbe("i", NodalProbeKind.ElementCurrent, "RL"), new NodalProbe("vb", NodalProbeKind.NodeVoltage, "B")],
            [new NodalEvent(0.0123, "SW", true), new NodalEvent(0.0456, "SW", false)], new NodalOptions { EventRule = NodalEventRule.BackwardEulerStep });
    }

    [Fact]
    public void R416_results_are_bit_identical_to_the_pre_R417_solver()
    {
        ulong mixed = Hash(Emt3Runner.Run(Mixed(10e-6)).Run);
        ulong rlc = Hash(SwitchedRlc());
        output.WriteLine($"HASH mixed={mixed} rlc={rlc}");
        Assert.Equal(PINNED_MIXED, mixed);
        Assert.Equal(PINNED_RLC, rlc);
    }

    private const ulong PINNED_MIXED = 5482862411946586279UL;
    private const ulong PINNED_RLC = 16529822280278531675UL;
}
