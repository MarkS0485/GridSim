using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// The adaptive-step error test gives rotor angles the fixed scale atol + rtol x 1 rad, so the tolerance no longer loosens
/// as the absolute angles grow. Before 2.1.103 the angle scale was atol + rtol |delta|: at the defaults a drift to 94 rad
/// made it about 93 times looser than at 1 rad.
/// </summary>
public sealed class G4AdaptiveAngleToleranceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Angles and speeds of <see cref="Machines"/> machines: d delta/dt = ws w, dw/dt = -a w. The speeds never read the
    /// angles, so every stage derivative k is the same double whatever the angles start at. With <c>angles</c> false the
    /// problem declares no angle states, which is exactly the error test before 2.1.103 (every component scaled by its own
    /// size).
    /// </summary>
    private sealed class DriftOde(bool angles) : IAdaptiveOde
    {
        public const int Machines = 3;
        private const double Ws = 2.0 * Math.PI * 50.0;
        private static readonly double[] A = [0.7, 1.3, 2.9];
        public int Dimension => 2 * Machines;
        public int AngleStates => angles ? Machines : 0;
        public void Derivative(double t, double[] y, double[] dydt)
        {
            for (int i = 0; i < Machines; i++)
            {
                dydt[i] = Ws * y[Machines + i];
                dydt[Machines + i] = -A[i] * y[Machines + i];
            }
        }
    }

    private sealed record Trace(List<(double H, bool Accepted)> Steps, double[] Final);

    /// <summary>The kernel's own accept/reject loop on the controller, every trial step recorded.</summary>
    private static Trace Integrate(IAdaptiveOde ode, double[] y0, AdaptiveOptions o, double tEnd)
    {
        var ctl = new AdaptiveStepController(o);
        double[] y = (double[])y0.Clone(), yn = new double[y0.Length];
        var steps = new List<(double, bool)>();
        double t = 0.0, h = o.InitialStepSeconds;
        while (t < tEnd)
        {
            double hTry = Math.Min(h, tEnd - t);
            double err = ctl.Step(ode, t, y, hTry, yn);
            bool ok = err <= 1.0;
            steps.Add((hTry, ok));
            if (ok)
            {
                t = hTry == tEnd - t ? tEnd : t + hTry;
                Array.Copy(yn, y, y.Length);
                h = ctl.NextStep(hTry, err, h);
            }
            else h = ctl.NextStep(hTry, err, hTry);
        }
        return new Trace(steps, y);
    }

    private static double[] Start(double offset) => [0.01 + offset, -0.02 + offset, 0.03 + offset, 0.004, -0.003, 0.002];

    // The default tolerances (rtol 1e-6, atol 1e-8) with the step cap lifted to 1 s, so that the error test, not the cap,
    // sets every step.
    private static readonly AdaptiveOptions Defaults = new(MaxStepSeconds: 1.0);

    [Theory]
    [InlineData(1)]
    [InlineData(15)]     // 94 rad: the drift of a 0.5 % offset at 50 Hz over 60 s
    [InlineData(31)]     // 195 rad
    [InlineData(-15)]
    public void Offsetting_every_angle_by_2_pi_k_gives_the_identical_step_sequence(int k)
    {
        double offset = 2.0 * Math.PI * k;
        Trace a = Integrate(new DriftOde(true), Start(0.0), Defaults, 5.0);
        Trace b = Integrate(new DriftOde(true), Start(offset), Defaults, 5.0);
        output.WriteLine($"k = {k}: {a.Steps.Count(s => s.Accepted)} accepted, {a.Steps.Count(s => !s.Accepted)} rejected, both runs");
        // Every trial step, and whether it was accepted, is the same double: the angle scale no longer reads the angle.
        Assert.Equal(a.Steps, b.Steps);
        // The speeds are the same bits; the angles differ by the offset only (to the rounding of adding it).
        for (int i = 0; i < DriftOde.Machines; i++)
        {
            Assert.Equal(a.Final[DriftOde.Machines + i], b.Final[DriftOde.Machines + i]);
            Assert.True(Math.Abs(b.Final[i] - offset - a.Final[i]) <= 1e-11, $"angle {i}: {b.Final[i] - offset - a.Final[i]:E3}");
        }
    }

    [Fact]
    public void With_the_angle_scaled_by_its_own_size_the_same_offset_loosens_the_step_control()
    {
        // The rule before 2.1.103 (no angle states declared). On this problem the angle components set the step, so moving
        // the angles to 94 rad loosens their test by three orders of magnitude and the run takes fewer, longer steps: the defect the fixed
        // angle scale removes. The fixed-scale run is the same at both offsets (theory above).
        Trace near = Integrate(new DriftOde(false), Start(0.0), Defaults, 5.0);
        Trace far = Integrate(new DriftOde(false), Start(2.0 * Math.PI * 15), Defaults, 5.0);
        Trace fixedScale = Integrate(new DriftOde(true), Start(2.0 * Math.PI * 15), Defaults, 5.0);
        int nNear = near.Steps.Count(s => s.Accepted), nFar = far.Steps.Count(s => s.Accepted), nFixed = fixedScale.Steps.Count(s => s.Accepted);
        output.WriteLine($"relative angle scale: {nNear} accepted steps near 0 rad, {nFar} at 94 rad; fixed angle scale: {nFixed} at any offset");
        Assert.True(nFar < nNear, $"{nFar} steps at 94 rad against {nNear} near 0 rad");
        Assert.NotEqual(near.Steps, far.Steps);
    }

    [Fact]
    public void The_angle_scale_is_atol_plus_rtol_times_one_radian()
    {
        // One state driven by y' = t^4 from t = 0: y5 - y4 depends only on t and h, never on y. Declared as an angle, the
        // error norm times the stated scale must give back that same |y5 - y4| at any angle size.
        var o = new AdaptiveOptions(RelativeTolerance: 1e-6, AbsoluteTolerance: 1e-8);
        Assert.Equal(1.0, AdaptiveStepController.AngleReferenceRad);
        double expected = o.AbsoluteTolerance + o.RelativeTolerance * AdaptiveStepController.AngleReferenceRad;
        foreach (double angle in new[] { 0.0, 1.0, 94.0, -1000.0 })
        {
            var ctl = new AdaptiveStepController(o);
            double[] y = [angle], yn = new double[1];
            double err = ctl.Step(new RampAngle(), 0.0, y, 0.1, yn);
            // Compare with the same step on a non-angle state from y = 0 (scale atol + rtol * |y5|).
            double e = err * expected;
            Assert.True(e > 0.0);
            var plain = new AdaptiveStepController(o);
            double[] z = [0.0], zn = new double[1];
            double errPlain = plain.Step(new RampPlain(), 0.0, z, 0.1, zn);
            double ePlain = errPlain * (o.AbsoluteTolerance + o.RelativeTolerance * Math.Abs(zn[0]));
            Assert.True(Math.Abs(e - ePlain) <= 1e-12 * ePlain, $"angle {angle}: |y5 - y4| {e:E6} against {ePlain:E6}");
        }
    }

    private sealed class RampAngle : IAdaptiveOde
    {
        public int Dimension => 1;
        public int AngleStates => 1;
        public void Derivative(double t, double[] y, double[] dydt) => dydt[0] = t * t * t * t;
    }

    private sealed class RampPlain : IAdaptiveOde
    {
        public int Dimension => 1;
        public void Derivative(double t, double[] y, double[] dydt) => dydt[0] = t * t * t * t;
    }

    [Fact]
    public void An_angle_state_count_outside_the_problem_is_refused()
    {
        var ctl = new AdaptiveStepController(new AdaptiveOptions());
        Assert.Throws<ArgumentException>(() => ctl.Step(new BadAngles(), 0.0, [0.0], 0.01, new double[1]));
    }

    private sealed class BadAngles : IAdaptiveOde
    {
        public int Dimension => 1;
        public int AngleStates => 2;
        public void Derivative(double t, double[] y, double[] dydt) => dydt[0] = 0.0;
    }

    // -----------------------------------------------------------------------------------------------------------
    // The swing kernel declares its rotor angles: a machine against an infinite bus, 2 pi k away, steps the same.
    // -----------------------------------------------------------------------------------------------------------

    private const double E = 1.2, V = 1.0, X = 0.5, F0 = 50.0, H = 5.0;

    private sealed class SmibNetwork : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) =>
            ([(emf[0] - V) * new Complex(0.0, -1.0 / X)], []);
    }

    private sealed class ZeroNetwork : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) => ([Complex.Zero], []);
    }

    private static AdaptiveSwingResult Smib(double delta0)
    {
        // A light load (delta0 near 0.01 rad from the bus), so under the old rule the angle scale was atol + rtol 0.01: the
        // angle set the step. A fault at 0.1 s cleared at 0.2 s, then a free swing to 1.5 s, at the default tolerances.
        double pm = E * V / X * Math.Sin(0.01);
        var pre = new SmibNetwork();
        var topology = new StageListTopology(pre, [(0.1, new ZeroNetwork(), "bolted fault"), (0.2, pre, "fault cleared")], 1);
        return AdaptiveSwingKernel.Run(new AdaptiveSwingProblem
        {
            H = [H], D = [0.0], Pm = [pm], Eqpp = [E], Edpp = [0.0], Delta0 = [delta0], F0 = F0, DurationSeconds = 1.5,
            Topology = topology, Options = new AdaptiveOptions(),
        });
    }

    [Theory]
    [InlineData(15)]
    [InlineData(-31)]
    public void The_swing_kernel_takes_the_same_steps_with_every_angle_offset_by_2_pi_k(int k)
    {
        double offset = 2.0 * Math.PI * k;
        AdaptiveSwingResult a = Smib(0.01), b = Smib(0.01 + offset);
        output.WriteLine($"k = {k}: accepted {a.AcceptedSteps} / {b.AcceptedSteps}, rejected {a.RejectedSteps} / {b.RejectedSteps}");
        Assert.Contains("rotor angles / (atol + rtol x 1 rad)", a.Scheme);
        // The infinite bus reads the angle through sin and cos, which round differently 2 pi k away, so the doubles may
        // differ in their last bits; the step control must not. Same counts and the same time grid to rounding.
        Assert.Equal((a.AcceptedSteps, a.RejectedSteps), (b.AcceptedSteps, b.RejectedSteps));
        Assert.Equal(a.TimeSeconds.Count, b.TimeSeconds.Count);
        for (int i = 0; i < a.TimeSeconds.Count; i++)
        {
            Assert.True(Math.Abs(a.TimeSeconds[i] - b.TimeSeconds[i]) <= 1e-12, $"t[{i}] {a.TimeSeconds[i]:R} / {b.TimeSeconds[i]:R}");
            Assert.True(Math.Abs(b.DeltaRad[0][i] - offset - a.DeltaRad[0][i]) <= 1e-9, $"delta at t[{i}]");
            Assert.True(Math.Abs(b.SpeedPu[0][i] - a.SpeedPu[0][i]) <= 1e-12, $"speed at t[{i}]");
        }
    }
}
