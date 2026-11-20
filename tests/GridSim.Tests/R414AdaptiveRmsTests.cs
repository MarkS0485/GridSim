using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Protection.Devices;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-14: the variable-step controller, the event locator and the relay-in-loop kernel, each against an oracle derived in
/// the test (a closed form, the classical fixed-step kernel's own equations, or the relay curve equation).
/// </summary>
public sealed class R414AdaptiveRmsTests(ITestOutputHelper output)
{
    // -----------------------------------------------------------------------------------------------------------
    // (2) The linear test equation y' = -y: controller error versus exp(-t); order of the scheme.
    // -----------------------------------------------------------------------------------------------------------

    private sealed class LinearOde : IAdaptiveOde
    {
        public int Dimension => 1;
        public void Derivative(double t, double[] y, double[] dydt) => dydt[0] = -y[0];
    }

    private static (double Y, int Accepted, int Rejected, double Bound) Integrate(AdaptiveOptions o, double tEnd)
    {
        var ctl = new AdaptiveStepController(o);
        var ode = new LinearOde();
        double[] y = [1.0], yn = new double[1];
        double t = 0.0, h = o.InitialStepSeconds, bound = 0.0;
        int acc = 0, rej = 0;
        while (t < tEnd)
        {
            double hTry = Math.Min(h, tEnd - t);
            double err = ctl.Step(ode, t, y, hTry, yn);
            if (err <= 1.0)
            {
                acc++;
                bound += o.AbsoluteTolerance + o.RelativeTolerance * Math.Abs(yn[0]);   // each accepted step's local error is within this
                t = hTry == tEnd - t ? tEnd : t + hTry;
                y[0] = yn[0];
                h = ctl.NextStep(hTry, err, h);
            }
            else { rej++; h = ctl.NextStep(hTry, err, hTry); }
        }
        return (y[0], acc, rej, bound);
    }

    [Theory]
    [InlineData(1e-4)]
    [InlineData(1e-6)]
    [InlineData(1e-8)]
    [InlineData(1e-10)]
    public void The_controller_holds_y_prime_equals_minus_y_to_the_sum_of_its_accepted_local_tolerances(double rtol)
    {
        var o = new AdaptiveOptions(RelativeTolerance: rtol, AbsoluteTolerance: rtol * 1e-3, InitialStepSeconds: 1e-3, MaxStepSeconds: 1.0);
        (double y, int acc, int rej, double bound) = Integrate(o, 5.0);
        double exact = Math.Exp(-5.0);
        double error = Math.Abs(y - exact);
        output.WriteLine($"rtol {rtol:E1}: steps {acc} (+{rej} rejected), error {error:E3} against bound {bound:E3}, exact {exact:R}");
        // The embedded estimate bounds the local error of the fourth-order companion; each accepted step contributes at most its
        // tolerance and the equation is contractive, so the global error cannot exceed their sum.
        Assert.True(error <= bound, $"global error {error:E3} exceeds the accepted tolerance sum {bound:E3}");
    }

    [Fact]
    public void Tightening_the_tolerance_converges_to_the_exact_solution()
    {
        var errors = new List<double>();
        foreach (double rtol in new[] { 1e-4, 1e-6, 1e-8, 1e-10 })
        {
            var o = new AdaptiveOptions(RelativeTolerance: rtol, AbsoluteTolerance: rtol * 1e-3, InitialStepSeconds: 1e-3, MaxStepSeconds: 1.0);
            errors.Add(Math.Abs(Integrate(o, 5.0).Y - Math.Exp(-5.0)));
        }
        Assert.True(errors[1] < errors[0] && errors[2] < errors[1] && errors[3] < errors[2], string.Join(", ", errors));
        // Observed order of the error in the tolerance: for a fifth-order scheme with a (1/5)-power step rule, global error ~ tol^(4/5) to tol^1.
        double slope = Math.Log(errors[0] / errors[3]) / Math.Log(1e-4 / 1e-10);
        output.WriteLine($"observed order of the global error in rtol: {slope:F3} (errors {string.Join(", ", errors.Select(e => e.ToString("E2")))})");
        Assert.InRange(slope, 0.6, 1.3);
    }

    [Fact]
    public void The_scheme_is_fifth_order_and_the_embedded_estimate_is_the_local_error_of_the_lower_order_pair()
    {
        // Fixed-step errors over one unit of time at h and h/2: the ratio is 2^5 = 32 for a fifth-order scheme.
        double Run(double h)
        {
            var ctl = new AdaptiveStepController(new AdaptiveOptions());
            double[] y = [1.0], yn = new double[1];
            int n = (int)Math.Round(1.0 / h);
            for (int i = 0; i < n; i++) { ctl.Step(new LinearOde(), i * h, y, h, yn); y[0] = yn[0]; }
            return Math.Abs(y[0] - Math.Exp(-1.0));
        }
        double e1 = Run(0.1), e2 = Run(0.05), e3 = Run(0.025);
        double p12 = Math.Log(e1 / e2) / Math.Log(2.0), p23 = Math.Log(e2 / e3) / Math.Log(2.0);
        output.WriteLine($"observed orders {p12:F3}, {p23:F3} (errors {e1:E3}, {e2:E3}, {e3:E3})");
        Assert.InRange(p12, 4.7, 5.5);
        Assert.InRange(p23, 4.7, 5.5);

        // The estimate of a single step is the size of (y5 - y4): it must be at least the true local error of y5 (y5 is the better
        // solution) and of the same order as the true error of y4 (h^5 / 120 * ... = a few e-7 at h = 0.1 for this equation).
        var c = new AdaptiveStepController(new AdaptiveOptions(RelativeTolerance: 1.0 / 4.0, AbsoluteTolerance: 1e-30));
        double[] yy = [1.0], yo = new double[1];
        double est = c.Step(new LinearOde(), 0.0, yy, 0.1, yo) * (1e-30 + 0.25 * Math.Max(1.0, yo[0]));   // undo the scaling: |y5 - y4|
        double trueLocal5 = Math.Abs(yo[0] - Math.Exp(-0.1));
        output.WriteLine($"one step h = 0.1: |y5 - y4| = {est:E3}, true error of y5 = {trueLocal5:E3}");
        Assert.True(est >= trueLocal5);
        Assert.True(est < 1e-5);
    }

    [Fact]
    public void The_step_rule_is_a_pure_function_and_never_leaves_its_limits()
    {
        var c = new AdaptiveStepController(new AdaptiveOptions(MinStepSeconds: 1e-6, MaxStepSeconds: 0.01));
        Assert.Equal(c.NextStep(0.005, 0.37, 0.005), c.NextStep(0.005, 0.37, 0.005));
        Assert.Equal(0.01, c.NextStep(0.005, 0.0, 0.005));                // error 0: grow, limited by the maximum
        Assert.Equal(1e-6, c.NextStep(1e-6, double.PositiveInfinity, 1e-6));   // non-finite: shrink, limited by the minimum
        Assert.True(c.NextStep(0.004, 4.0, 0.004) < 0.004);                // a rejected step shrinks
        Assert.True(c.NextStep(0.004, 0.5, 0.004) > 0.004);                // a comfortable step grows
        // A step cut short by a boundary never shrinks the next trial below the step that was wanted.
        Assert.True(c.NextStep(1e-5, 0.5, 0.004) >= 0.004);
    }

    [Fact]
    public void A_step_that_cannot_meet_the_tolerance_at_the_minimum_step_stops_the_run_rather_than_being_accepted()
    {
        // y' = -1e12 y is far too stiff for a minimum step of 1e-3: the kernel must say so, never carry on.
        var p = Smib(onset: 0.1, clear: 0.2, duration: 0.5, options: new AdaptiveOptions(MinStepSeconds: 0.01, MaxStepSeconds: 0.02, InitialStepSeconds: 0.01,
            RelativeTolerance: 1e-14, AbsoluteTolerance: 1e-16));
        Assert.Throws<AdaptiveStepException>(() => AdaptiveSwingKernel.Run(p));
    }

    [Fact]
    public void The_event_locator_bisects_to_the_stated_width_with_a_count_fixed_by_the_bracket()
    {
        double root = 0.3141592653589793;
        LocatedEvent e = EventLocator.Locate(0.0, 1.0, 1e-9, t => t >= root);
        Assert.True(e.LowSeconds < root && root <= e.HighSeconds);
        Assert.True(e.HighSeconds - e.LowSeconds <= 1e-9);
        Assert.Equal(30, e.Bisections);   // ceil(log2(1 / 1e-9)) = 30
        Assert.Equal(e, EventLocator.Locate(0.0, 1.0, 1e-9, t => t >= root));
        Assert.Throws<ArgumentException>(() => EventLocator.Locate(1.0, 0.5, 1e-9, _ => true));
    }

    // -----------------------------------------------------------------------------------------------------------
    // (1) Equal-area closed form: classical machine against an infinite bus, fault at the terminals.
    // -----------------------------------------------------------------------------------------------------------

    private const double E = 1.2, V = 1.0, X = 0.5, F0 = 50.0, H = 5.0;
    private static readonly double Pmax = E * V / X;                  // 2.4 pu
    private static readonly double Pm = 0.5 * Pmax;                   // initial equilibrium at delta0 = 30 degrees
    private static readonly double Delta0 = Math.Asin(Pm / Pmax);     // pi / 6
    private static readonly double DeltaU = Math.PI - Delta0;         // the unstable equilibrium

    /// <summary>I = (E - V) / (jX): one machine behind a reactance to an infinite bus of voltage V at angle 0.</summary>
    private sealed class SmibNetwork : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) =>
            ([(emf[0] - V) * new Complex(0.0, -1.0 / X)], []);
    }

    /// <summary>A bolted fault at the machine terminals: no current leaves the machine, so Pe = 0.</summary>
    private sealed class ZeroNetwork : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) => ([Complex.Zero], []);
    }

    private static AdaptiveSwingProblem Smib(double onset, double clear, double duration, AdaptiveOptions? options = null,
        RelayInLoop? relays = null, IRelayCurrentSource? source = null, IRmsNetwork? fault = null, IRmsNetwork? post = null, Func<int, IRmsNetwork>? trip = null)
    {
        var pre = new SmibNetwork();
        var topology = new StageListTopology(pre,
            [(onset, fault ?? new ZeroNetwork(), "bolted fault"), (clear, post ?? pre, "fault cleared")], 1, trip);
        return new AdaptiveSwingProblem
        {
            H = [H], D = [0.0], Pm = [Pm], Eqpp = [E], Edpp = [0.0], Delta0 = [Delta0], F0 = F0, DurationSeconds = duration,
            Topology = topology, Options = options ?? new AdaptiveOptions(RelativeTolerance: 1e-10, AbsoluteTolerance: 1e-12, MaxStepSeconds: 0.01),
            Relays = relays, CurrentSource = source,
        };
    }

    private static readonly double CriticalAngle = Math.Acos(Pm / Pmax * (DeltaU - Delta0) + Math.Cos(DeltaU));   // equal-area criterion
    private static readonly double CriticalClearingSeconds = Math.Sqrt(2.0 * H * (CriticalAngle - Delta0) / (Math.PI * F0 * Pm));   // inverted fault-phase quadratic

    [Fact]
    public void The_fault_phase_follows_the_closed_form_quadratic_and_lands_on_the_event_times_exactly()
    {
        double onset = 0.1234, clear = 0.1234 + 0.2;
        AdaptiveSwingResult r = AdaptiveSwingKernel.Run(Smib(onset, clear, 1.0));
        double k = Math.PI * F0 * Pm / (2.0 * H);   // delta(t) = delta0 + k (t - onset)^2 while Pe = 0
        int checkedSamples = 0;
        for (int i = 0; i < r.TimeSeconds.Count; i++)
        {
            double t = r.TimeSeconds[i];
            if (t < onset || t > clear) continue;
            Assert.True(Math.Abs(r.DeltaRad[0][i] - (Delta0 + k * (t - onset) * (t - onset))) < 1e-9, $"delta at {t}");
            Assert.True(Math.Abs(r.SpeedPu[0][i] - Pm * (t - onset) / (2.0 * H)) < 1e-9, $"speed at {t}");
            checkedSamples++;
        }
        Assert.True(checkedSamples >= 5);
        // No quantisation: both events are applied at exactly the requested times, and the sample set contains them.
        Assert.Equal(new[] { onset, clear }, r.Events.Where(e => e.Source == "scheduled").Select(e => e.AppliedSeconds).ToArray());
        Assert.Contains(onset, r.TimeSeconds);
        Assert.Contains(clear, r.TimeSeconds);
        Assert.Equal("adaptive", r.Integration);
        Assert.True(r.AcceptedSteps > 0);
        output.WriteLine($"accepted {r.AcceptedSteps}, rejected {r.RejectedSteps}, steps between {r.MinStepSeconds:E2} and {r.MaxStepSeconds:E2} s");
    }

    private static bool StableAfter(double clearingSeconds, AdaptiveOptions options)
    {
        double onset = 0.1, clear = onset + clearingSeconds;
        AdaptiveSwingResult r = AdaptiveSwingKernel.Run(Smib(onset, clear, clear + 1.5, options));
        return r.DeltaRad[0].All(d => d < DeltaU);   // the equal-area boundary: the angle never reaches the unstable equilibrium
    }

    [Fact]
    public void The_critical_clearing_time_converges_to_the_equal_area_closed_form_as_the_tolerance_tightens()
    {
        output.WriteLine($"closed form: critical angle {CriticalAngle:R} rad ({CriticalAngle * 180.0 / Math.PI:F4} deg), critical clearing time {CriticalClearingSeconds:R} s");
        var o = new AdaptiveOptions(RelativeTolerance: 1e-11, AbsoluteTolerance: 1e-13, MaxStepSeconds: 0.01, EventTimeToleranceSeconds: 1e-9);
        var errors = new List<(double Tol, double Error)>();
        foreach (double tol in new[] { 1e-2, 1e-3, 1e-4, 1e-5, 1e-6 })
        {
            AdaptiveCct.Result cct = AdaptiveCct.Search(c => StableAfter(c, o), 1.0, tol);
            Assert.Equal(AdaptiveCct.Bracketed, cct.Disposition);
            double lo = cct.StableSeconds, hi = cct.UnstableSeconds!.Value;
            Assert.True(hi - lo <= tol);
            // The closed-form critical time lies inside the located bracket (integration error far below the tolerance).
            Assert.True(lo <= CriticalClearingSeconds + 1e-8 && CriticalClearingSeconds <= hi + 1e-8, $"tol {tol}: [{lo}, {hi}] vs {CriticalClearingSeconds}");
            double err = Math.Abs(0.5 * (lo + hi) - CriticalClearingSeconds);
            errors.Add((tol, err));
            output.WriteLine($"tolerance {tol:E0}: bracket [{lo:R}, {hi:R}], midpoint error {err:E3}, trials {cct.Trials}");
            Assert.True(err <= tol);
        }
        // Order of convergence of the bracket width in the tolerance is 1 (bisection): the largest error at each tolerance is bounded by it.
        double slope = Math.Log(errors[0].Error / errors[^1].Error) / Math.Log(errors[0].Tol / errors[^1].Tol);
        output.WriteLine($"observed order of the midpoint error in the tolerance (noisy, bisection is first order): {slope:F2}");
        Assert.True(errors[^1].Error < errors[0].Error);
    }

    [Fact]
    public void Stable_through_the_window_and_unstable_at_zero_are_reported_as_such()
    {
        var o = new AdaptiveOptions(RelativeTolerance: 1e-9, AbsoluteTolerance: 1e-11, MaxStepSeconds: 0.01);
        Assert.Equal(AdaptiveCct.StableThroughWindow, AdaptiveCct.Search(c => StableAfter(c, o), CriticalClearingSeconds * 0.5, 1e-3).Disposition);
        Assert.Equal(AdaptiveCct.UnstableAtZero, AdaptiveCct.Search(_ => false, 1.0, 1e-3).Disposition);
    }

    // -----------------------------------------------------------------------------------------------------------
    // Variable step against the classical fixed-step kernel (the same equations), within the error estimate.
    // -----------------------------------------------------------------------------------------------------------

    private static readonly Complex[,] YPre = { { new Complex(0.20, -2.5), new Complex(-0.10, 1.2) }, { new Complex(-0.10, 1.2), new Complex(0.15, -2.0) } };
    private static readonly Complex[,] YFault = { { new Complex(0.02, -0.4), new Complex(-0.01, 0.1) }, { new Complex(-0.01, 0.1), new Complex(0.02, -0.3) } };
    private static readonly double[] TwoH = [4.0, 6.0], TwoD = [0.5, 0.8], TwoEq = [1.1, 1.05], TwoDelta0 = [0.35, 0.15];

    private static double[] EquilibriumPower()
    {
        var pm = new double[2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double dij = TwoDelta0[i] - TwoDelta0[j];
                pm[i] += TwoEq[i] * TwoEq[j] * (YPre[i, j].Real * Math.Cos(dij) + YPre[i, j].Imaginary * Math.Sin(dij));
            }
        return pm;
    }

    private static AdaptiveSwingResult TwoMachineAdaptive(double[] pm, AdaptiveOptions options, params (double At, Complex[,] Y, string What)[] stages) =>
        AdaptiveSwingKernel.Run(new AdaptiveSwingProblem
        {
            H = TwoH, D = TwoD, Pm = pm, Eqpp = TwoEq, Edpp = [0.0, 0.0], Delta0 = TwoDelta0, F0 = F0, DurationSeconds = 1.0,
            Topology = new StageListTopology(new DenseMachineNetwork(YPre), stages.Select(x => (x.At, (IRmsNetwork)new DenseMachineNetwork(x.Y), x.What)), 2),
            Options = options,
        });

    private static double Gap(AdaptiveSwingResult run, ClassicalMultiMachine.Result fixedRun, double step, double at, out double bound, AdaptiveOptions o)
    {
        int ia = run.TimeSeconds.ToList().FindIndex(t => Math.Abs(t - at) < 1e-12);
        Assert.True(ia >= 0, $"the adaptive run has a sample at {at}");
        int ifx = (int)Math.Round(at / step);
        bound = run.AcceptedSteps * (o.AbsoluteTolerance + o.RelativeTolerance * 10.0);   // accepted local tolerances, summed
        double gap = 0.0;
        for (int m = 0; m < 2; m++)
        {
            gap = Math.Max(gap, Math.Abs(run.DeltaRad[m][ia] - fixedRun.Machines[m].DeltaRad[ifx]));
            gap = Math.Max(gap, Math.Abs(run.SpeedPu[m][ia] - fixedRun.Machines[m].SpeedPu[ifx]));
        }
        return gap;
    }

    [Fact]
    public void On_a_smooth_swing_the_adaptive_run_agrees_with_the_fixed_step_classical_kernel_within_its_error_estimate()
    {
        // A free swing from an imbalance (no network event): both integrate the same smooth equations.
        double[] pe = EquilibriumPower();
        double[] pm = [pe[0] + 0.3, pe[1] - 0.1];
        const double step = 1e-4;
        ClassicalMultiMachine.Result fixedRun = ClassicalMultiMachine.Simulate(TwoH, TwoD, pm, TwoEq, TwoDelta0, YPre, F0, step, 1.0);
        var o = new AdaptiveOptions(RelativeTolerance: 1e-9, AbsoluteTolerance: 1e-11, MaxStepSeconds: 0.02);
        // A no-change stage at 0.5 s makes the adaptive run land on a fixed-step sample there as well as at the end.
        AdaptiveSwingResult run = TwoMachineAdaptive(pm, o, (0.5, YPre, "no change"));
        foreach (double at in new[] { 0.5, 1.0 })
        {
            double gap = Gap(run, fixedRun, step, at, out double bound, o);
            output.WriteLine($"smooth swing, t = {at}: max |d state| {gap:E2} against the estimate bound {bound:E2}");
            Assert.True(gap <= bound, $"gap {gap:E3} above the accepted-tolerance sum {bound:E3}");
        }
        Assert.True(run.AcceptedSteps < 200, $"the variable step needs far fewer steps than the {1.0 / step:F0} fixed ones (took {run.AcceptedSteps})");
    }

    [Fact]
    public void Across_a_fault_edge_the_fixed_step_kernel_carries_a_first_order_edge_error_the_adaptive_run_does_not()
    {
        // The fixed kernel picks the network at each RK4 sub-stage time, so the step that ends on a fault edge sees the new network in its
        // last stages: an error of order h at every edge. The adaptive run lands on the edge and integrates each side exactly. The gap to the
        // fixed run must therefore shrink in proportion to the fixed step, to the adaptive estimate.
        double[] pm = EquilibriumPower();
        const double onset = 0.1, clear = 0.25;
        var o = new AdaptiveOptions(RelativeTolerance: 1e-10, AbsoluteTolerance: 1e-12, MaxStepSeconds: 0.01);
        AdaptiveSwingResult run = TwoMachineAdaptive(pm, o, (onset, YFault, "fault"), (clear, YPre, "clear"));
        var gaps = new List<double>();
        foreach (double step in new[] { 1e-3, 1e-4, 1e-5 })
        {
            ClassicalMultiMachine.Result fixedRun = ClassicalMultiMachine.Simulate(TwoH, TwoD, pm, TwoEq, TwoDelta0, YPre, F0, step, 1.0,
                new ClassicalMultiMachine.Fault(onset, clear, YFault));
            double gap = Gap(run, fixedRun, step, 1.0, out double bound, o);
            gaps.Add(gap);
            output.WriteLine($"fixed step {step:E0}: max |d state| at 1 s {gap:E3} (estimate bound {bound:E2})");
        }
        Assert.True(gaps[1] < gaps[0] / 5.0 && gaps[2] < gaps[1] / 5.0, $"gaps {string.Join(", ", gaps)} should fall about tenfold per decade of step");
        Assert.True(gaps[2] < 1e-5);
        Assert.True(run.StayedInSynchronism);
    }

    // -----------------------------------------------------------------------------------------------------------
    // (3) Relays in the loop: trip time equals the curve equation at the (constant) overload current.
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>Reads a fixed primary current per network object (the test stands in for the branch the relay measures).</summary>
    private sealed class ConstantCurrent(IRmsNetwork overloadNetwork, double overloadAmps, double normalAmps) : IRelayCurrentSource
    {
        public void PrimaryCurrentsA(IRmsNetwork network, Complex[] emf, double[] amps) => amps[0] = ReferenceEquals(network, overloadNetwork) ? overloadAmps : normalAmps;
    }

    private static PlacedDevice SiRelay(double pickupA, double tms) =>
        new("R1", DeviceType.Relay, "51", new DeviceLocation(0, 1, 2, "from"), 1.0, 1.0, 0.0, 0.0,
            [new ShotSetting("iec-si", pickupA, tms, 0.0)], null, [], null, null);

    [Fact]
    public void A_constant_overload_trips_at_the_iec_standard_inverse_equation_time_within_one_located_event_tolerance()
    {
        const double pickup = 400.0, tms = 0.1, amps = 1200.0, onset = 0.1234, tol = 1e-6;
        // IEC 60255-151 standard inverse, derived here: t = TMS * 0.14 / ((I / Is)^0.02 - 1).
        double expected = tms * 0.14 / (Math.Pow(amps / pickup, 0.02) - 1.0);
        var overload = new ZeroNetwork();
        var after = new SmibNetwork();
        var relays = new RelayInLoop([SiRelay(pickup, tms)], new CurveLibrary(), tol);
        var source = new ConstantCurrent(overload, amps, 100.0);
        AdaptiveSwingResult r = AdaptiveSwingKernel.Run(Smib(onset, 2.0, 2.0,
            new AdaptiveOptions(RelativeTolerance: 1e-10, AbsoluteTolerance: 1e-12, MaxStepSeconds: 0.05, EventTimeToleranceSeconds: tol),
            relays, source, fault: overload, post: overload, trip: _ => after));
        AdaptiveRelayRecord rec = Assert.Single(r.Relays);
        output.WriteLine($"equation: {expected:R} s after pick-up; pick-up {rec.PickupSeconds:R}, trip {rec.TripSeconds:R}, bracket low {rec.TripBracketLowSeconds:R}, current {rec.TripCurrentA}");
        Assert.True(rec.Tripped);
        Assert.Equal(onset, rec.PickupSeconds);   // the overload begins at the scheduled event: pick-up is applied there exactly
        double delay = rec.TripSeconds!.Value - rec.PickupSeconds!.Value;
        Assert.True(Math.Abs(delay - expected) <= tol, $"trip delay {delay:R} vs equation {expected:R}");
        Assert.True(rec.TripBracketLowSeconds!.Value <= onset + expected + 1e-9 && onset + expected <= rec.TripSeconds.Value + 1e-9);   // the true time is inside the bracket
        Assert.Equal(amps, rec.TripCurrentA);
        // The trip is a branch-open event in the ordered event list, after the pick-up.
        Assert.Equal(new[] { "scheduled", "relay", "relay" }, r.Events.Take(3).Select(e => e.Source).ToArray());
        Assert.Equal(new[] { "pickup", "trip" }, r.Events.Where(e => e.Source == "relay").Select(e => e.Kind).ToArray());
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1e-6)]
    [InlineData(1e-9)]
    public void The_trip_time_error_is_bounded_by_the_event_tolerance_at_every_tolerance(double tol)
    {
        const double pickup = 400.0, tms = 0.3, amps = 2000.0, onset = 0.2;
        double expected = tms * 0.14 / (Math.Pow(amps / pickup, 0.02) - 1.0);
        var overload = new ZeroNetwork();
        var relays = new RelayInLoop([SiRelay(pickup, tms)], new CurveLibrary(), tol);
        AdaptiveSwingResult r = AdaptiveSwingKernel.Run(Smib(onset, 3.0, 3.0,
            new AdaptiveOptions(RelativeTolerance: 1e-10, AbsoluteTolerance: 1e-12, MaxStepSeconds: 0.1, EventTimeToleranceSeconds: tol),
            relays, new ConstantCurrent(overload, amps, 0.0), fault: overload, post: overload, trip: _ => new SmibNetwork()));
        double err = r.Relays[0].TripSeconds!.Value - (onset + expected);
        output.WriteLine($"tolerance {tol:E0}: trip {r.Relays[0].TripSeconds:R} vs equation {onset + expected:R}, error {err:E2}");
        Assert.InRange(err, -1e-9, tol + 1e-9);   // never early, at most one bracket late
    }

    [Fact]
    public void An_instantaneous_element_trips_on_pickup_and_a_current_below_pickup_never_trips()
    {
        var overload = new ZeroNetwork();
        var inst = new PlacedDevice("R50", DeviceType.Relay, "50", new DeviceLocation(0, 1, 2, "from"), 1.0, 1.0, 0.0, 0.0, [], new InstantaneousSetting(2000.0, 0.0), [], null, null);
        var opts = new AdaptiveOptions(RelativeTolerance: 1e-9, AbsoluteTolerance: 1e-11, MaxStepSeconds: 0.05);
        AdaptiveSwingResult hit = AdaptiveSwingKernel.Run(Smib(0.3, 1.0, 1.0, opts, new RelayInLoop([inst], new CurveLibrary(), 1e-6),
            new ConstantCurrent(overload, 2500.0, 100.0), fault: overload, post: overload, trip: _ => new SmibNetwork()));
        Assert.True(hit.Relays[0].Tripped);
        Assert.Equal(0.3, hit.Relays[0].TripSeconds);

        AdaptiveSwingResult miss = AdaptiveSwingKernel.Run(Smib(0.3, 1.0, 1.0, opts, new RelayInLoop([SiRelay(400.0, 0.1)], new CurveLibrary(), 1e-6),
            new ConstantCurrent(overload, 300.0, 100.0), fault: overload, post: overload, trip: _ => new SmibNetwork()));
        Assert.False(miss.Relays[0].Tripped);
        Assert.Null(miss.Relays[0].PickupSeconds);
        Assert.DoesNotContain(miss.Events, e => e.Source == "relay");
    }

    [Fact]
    public void A_current_that_drops_out_before_the_operate_time_resets_the_relay()
    {
        // Overload for 0.2 s (the SI operate time at 3x pickup with TMS 0.1 is 0.63 s), then back to normal: pick-up, drop-out, no trip.
        var overload = new ZeroNetwork();
        AdaptiveSwingResult r = AdaptiveSwingKernel.Run(Smib(0.1, 0.3, 2.0,
            new AdaptiveOptions(RelativeTolerance: 1e-9, AbsoluteTolerance: 1e-11, MaxStepSeconds: 0.05),
            new RelayInLoop([SiRelay(400.0, 0.1)], new CurveLibrary(), 1e-6), new ConstantCurrent(overload, 1200.0, 100.0),
            fault: overload, post: new SmibNetwork(), trip: _ => new SmibNetwork()));
        Assert.False(r.Relays[0].Tripped);
        Assert.Equal(new[] { "pickup", "dropout" }, r.Events.Where(e => e.Source == "relay").Select(e => e.Kind).ToArray());
    }

    // -----------------------------------------------------------------------------------------------------------
    // Determinism: bit-identical across runs and across thread counts.
    // -----------------------------------------------------------------------------------------------------------

    private static string Digest(AdaptiveSwingResult r)
    {
        var bytes = new List<byte>();
        foreach (double t in r.TimeSeconds) bytes.AddRange(BitConverter.GetBytes(t));
        foreach (var s in r.DeltaRad) foreach (double v in s) bytes.AddRange(BitConverter.GetBytes(v));
        foreach (var s in r.SpeedPu) foreach (double v in s) bytes.AddRange(BitConverter.GetBytes(v));
        foreach (AdaptiveAppliedEvent e in r.Events) { bytes.AddRange(BitConverter.GetBytes(e.AppliedSeconds)); bytes.AddRange(BitConverter.GetBytes(e.BracketLowSeconds)); }
        bytes.AddRange(BitConverter.GetBytes(r.AcceptedSteps)); bytes.AddRange(BitConverter.GetBytes(r.RejectedSteps));
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static AdaptiveSwingResult RelayRun()
    {
        var overload = new ZeroNetwork();
        return AdaptiveSwingKernel.Run(Smib(0.1234, 2.0, 2.0, new AdaptiveOptions(RelativeTolerance: 1e-9, AbsoluteTolerance: 1e-11, MaxStepSeconds: 0.05),
            new RelayInLoop([SiRelay(400.0, 0.1)], new CurveLibrary(), 1e-6), new ConstantCurrent(overload, 1200.0, 100.0),
            fault: overload, post: overload, trip: _ => new SmibNetwork()));
    }

    [Fact]
    public void The_adaptive_run_is_bit_identical_across_repeats_and_across_thread_counts()
    {
        string serial = Digest(RelayRun());
        Assert.Equal(serial, Digest(RelayRun()));
        foreach (int threads in new[] { 2, 4, 8 })
        {
            var digests = new string[threads * 2];
            Parallel.For(0, digests.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => digests[i] = Digest(RelayRun()));
            Assert.All(digests, d => Assert.Equal(serial, d));
        }
    }
}
