using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R078 (October 2026 review): the EMT build used to drop every transformer tap and phase shift and turned a
/// negative series reactance (series compensation) into a plain resistor without a word. It now models the tap
/// as an ideal From-side transformer and X &lt; 0 as a series R-C arm, and refuses by name what it cannot model.
/// These tests validate the new elements against INDEPENDENT oracles (closed-form circuit responses, the phasor
/// stamp of <see cref="AdmittanceStamp"/>, and the Newton-Raphson power flow), and pin every failure path.
/// </summary>
public class EmtTapAndSeriesCapacitorTests
{
    private const double F = 50.0;
    private static readonly double W = 2.0 * Math.PI * F;
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    private const double SteadyStateRunSeconds = 1.0;   // long enough for the sub-synchronous series-capacitor modes to die away

    // ---- Helpers -------------------------------------------------------------------------------------------

    /// <summary>The fundamental phasor (peak, cosine reference) of probe <paramref name="j"/> over the LAST whole
    /// cycle of the run: a one-bin DFT, exact for a settled sinusoid sampled at an integer number of points per
    /// cycle.</summary>
    private static Complex LastCycleFundamental(EmtResult res, int j, double omega, double dt)
    {
        int nPer = (int)Math.Round(2.0 * Math.PI / (omega * dt));
        Complex acc = Complex.Zero;
        for (int k = res.Points.Count - nPer; k < res.Points.Count; k++)
            acc += res.Points[k].Probe[j] * Complex.FromPolarCoordinates(1.0, -omega * res.Points[k].TSeconds);
        return acc * 2.0 / nPer;
    }

    /// <summary>Closed-form series-RLC step-response current, i(0)=0, v_C(0)=0 (the same textbook oracle as
    /// <see cref="EmtRlcAnalyticTests"/>, repeated here so this file stands alone).</summary>
    private static double SeriesRlcCurrent(double r, double l, double c, double v, double t)
    {
        double w0 = 1.0 / Math.Sqrt(l * c);
        double zeta = (r / 2.0) * Math.Sqrt(c / l);
        if (Math.Abs(zeta - 1.0) < 1e-12) return (v / l) * t * Math.Exp(-w0 * t);
        if (zeta < 1.0)
        {
            double wd = w0 * Math.Sqrt(1.0 - zeta * zeta);
            return (v / (wd * l)) * Math.Exp(-zeta * w0 * t) * Math.Sin(wd * t);
        }
        double disc = w0 * Math.Sqrt(zeta * zeta - 1.0);
        double s1 = -zeta * w0 + disc, s2 = -zeta * w0 - disc;
        return (v / (l * (s1 - s2))) * (Math.Exp(s1 * t) - Math.Exp(s2 * t));
    }

    /// <summary>Series RLC loop with the R and C inside ONE <see cref="SeriesCapacitorBranch"/>: ideal V step at
    /// node 0 -> series R-C (element 1) -> node 1 -> inductor (element 2) to ground. Returns the max deviation of
    /// the loop current from the closed form.</summary>
    private static double SeriesRcLoopError(double r, double l, double c, double dt, double duration, IntegrationRule rule)
    {
        const double v = 1.0;
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(3, Bus: 0, Rs: 0, Emf: _ => v),
            new SeriesCapacitorBranch(1, From: 0, To: 1, R: r, C: c),
            new SeriesBranch(2, From: 1, To: EmtNetwork.Ground, R: 0.0, L: l),
        });
        var probes = new[] { new EmtProbe("i", EmtProbeKind.BranchCurrent, 1) };
        EmtResult res = new EmtEngine(net, dt, new EmtOptions { Rule = rule }).Simulate(new EmtCase(net, dt, duration, probes));
        double maxErr = 0.0;
        foreach (EmtPoint p in res.Points)
            maxErr = Math.Max(maxErr, Math.Abs(p.Probe[0] - SeriesRlcCurrent(r, l, c, v, p.TSeconds)));
        return maxErr;
    }

    /// <summary>A two-bus model: bus 1 slack (the EMT source), bus 2 a PQ load (80 MW, 30 MVAr on 100 MVA), one
    /// branch 1-&gt;2 with the given pi data.</summary>
    private static GridModel TwoBus(double r, double x, double b, double tap, double shiftDeg = 0.0, bool isTransformer = true, string? name = null) => new()
    {
        BaseMva = 100.0,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 80.0, Qd: 30.0) },
        Generators = new[] { new Generator(1, 80.0) },
        Branches = new[] { new Branch(1, 2, r, x, b, tap, shiftDeg, Name: name, IsTransformer: isTransformer) },
    };

    // ---- The series-capacitor companion against closed forms ------------------------------------------------

    [Theory]
    [InlineData(0.4, 1e-3, 1e-3)]   // underdamped  (zeta = 0.2)
    [InlineData(2.0, 1e-3, 1e-3)]   // critically damped (zeta = 1)
    [InlineData(4.0, 1e-3, 1e-3)]   // overdamped   (zeta = 2)
    public void Series_rc_branch_reproduces_the_closed_form_series_rlc_step_response(double r, double l, double c)
    {
        // The R and C of the loop live in the new series R-C companion; the oracle is the textbook RLC current.
        double maxErr = SeriesRcLoopError(r, l, c, 1e-6, 3e-3, IntegrationRule.Trapezoidal);
        Assert.True(maxErr < 1e-5, $"max |i_emt - i_analytic| = {maxErr:E3} exceeded 1e-5");
    }

    [Fact]
    public void Series_rc_trapezoidal_error_is_second_order_in_dt()
    {
        double eCoarse = SeriesRcLoopError(0.4, 1e-3, 1e-3, 2e-6, 2e-3, IntegrationRule.Trapezoidal);
        double eFine = SeriesRcLoopError(0.4, 1e-3, 1e-3, 1e-6, 2e-3, IntegrationRule.Trapezoidal);
        Assert.True(eCoarse / eFine > 3.0, $"error ratio {eCoarse / eFine:F2} (expected about 4 for second order)");
    }

    [Fact]
    public void Series_rc_backward_euler_converges_at_first_order()
    {
        // The backward-Euler form (used for the CDA burst after a switching event) must also be a consistent
        // discretisation: first order, so halving dt halves the error.
        double eCoarse = SeriesRcLoopError(0.4, 1e-3, 1e-3, 2e-6, 2e-3, IntegrationRule.BackwardEuler);
        double eFine = SeriesRcLoopError(0.4, 1e-3, 1e-3, 1e-6, 2e-3, IntegrationRule.BackwardEuler);
        double ratio = eCoarse / eFine;
        Assert.True(ratio > 1.8 && ratio < 2.2, $"error ratio {ratio:F3} (expected about 2 for first order)");
        Assert.True(eFine < 1e-2, $"backward-Euler error {eFine:E3} too large");
    }

    [Fact]
    public void Series_rc_with_zero_resistance_is_algebraically_the_capacitor_rule()
    {
        // R = 0 must reduce to the plain capacitor companion (Geq = 2C/dt, I_hist = -Geq*v - i), up to rounding.
        const double c = 2.5e-4, dt = 2e-5;
        Companion rc = EmtCompanionStamp.SeriesRc(0.0, c, dt, IntegrationRule.Trapezoidal);
        Companion cap = EmtCompanionStamp.Capacitor(c, dt, IntegrationRule.Trapezoidal);
        Assert.Equal(cap.Geq, rc.Geq, 9);
        Assert.Equal(cap.HistCoeffV, rc.HistCoeffV, 9);
        Assert.Equal(cap.HistCoeffI, rc.HistCoeffI, 12);
        Companion rcBe = EmtCompanionStamp.SeriesRc(0.0, c, dt, IntegrationRule.BackwardEuler);
        Companion capBe = EmtCompanionStamp.Capacitor(c, dt, IntegrationRule.BackwardEuler);
        Assert.Equal(capBe.Geq, rcBe.Geq, 9);
        Assert.Equal(capBe.HistCoeffV, rcBe.HistCoeffV, 9);
        Assert.Equal(0.0, rcBe.HistCoeffI);
    }

    // ---- The tap companion against closed forms and the phasor stamp ----------------------------------------

    [Fact]
    public void Tapped_branch_dc_steady_state_is_the_ideal_transformer_solution()
    {
        // 1 V DC -> ideal a:1 transformer -> R-L arm -> node 1 -> G to ground. At DC the inductor is a short, so
        // the arm sees V/a and i = (V/a)/(R + 1/G), v1 = i/G. Time constant L/(R + 1/G) = 1 ms; 50 ms = 50 tau.
        const double v = 1.0, a = 1.1, r = 0.5, l = 1e-3, g = 2.0, dt = 1e-5;
        var net = new EmtNetwork(2, new List<EmtElement>
        {
            new VoltageSource(3, Bus: 0, Rs: 0, Emf: _ => v),
            new SeriesBranch(1, From: 0, To: 1, R: r, L: l, Tap: a),
            new ShuntElement(2, Bus: 1, G: g),
        });
        var probes = new[] { new EmtProbe("i", EmtProbeKind.BranchCurrent, 1), new EmtProbe("v1", EmtProbeKind.NodeVoltage, 1) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, 0.05, probes));
        double iExpected = (v / a) / (r + 1.0 / g);
        Assert.Equal(iExpected, res.Points[^1].Probe[0], 12);
        Assert.Equal(iExpected / g, res.Points[^1].Probe[1], 12);
    }

    /// <summary>The headline R078 check: an EMT build of a two-bus case must settle to exactly the phasor solution
    /// of the power flow's own branch stamp (<see cref="AdmittanceStamp.Branch"/>), for tapped lines, tapped
    /// transformers, series capacitors (tapped and not, with charging) and a negative-resistance series-capacitor
    /// equivalent of the kind case145 carries. The oracle solves the 2x2 phasor network directly:
    /// V2 = -Ytf*V1 / (Ytt + Yload). The tolerance covers the trapezoidal rule's frequency warping at
    /// dt = 10 us (relative reactance error (w*dt)^2/12 = 8e-7) and the residual transient.</summary>
    [Theory]
    [InlineData(0.02, 0.20, 0.10, 0.95)]      // tapped line with charging (From-end charging behind the tap)
    [InlineData(0.01, 0.15, 0.00, 1.05)]      // tapped transformer
    [InlineData(0.01, -0.12, 0.00, 0.97)]     // tapped series capacitor
    [InlineData(0.02, -0.05, 0.04, 1.00)]     // untapped series capacitor with charging
    [InlineData(-0.009, -0.1718, 0.00, 0.935)] // case145's negative-R series-capacitor equivalent, as tapped there
    [InlineData(0.00, -0.08, 0.00, 1.00)]     // ideal (R = 0) series capacitor, as in case240 / case300
    [InlineData(0.00, -0.08, 0.02, 1.02)]     // ideal series capacitor behind a tap, with charging
    [InlineData(0.02, 0.20, 0.10, 0.00)]      // tap 0 means a plain line, exactly as in the power flow
    public void Two_bus_emt_steady_state_matches_the_admittance_stamp_phasor(double r, double x, double b, double tap)
    {
        GridModel m = TwoBus(r, x, b, tap);
        EmtNetwork net = EmtNetwork.FromGridModel(m,
            new EmtBuildOptions(new[] { new EmtSourceSpec(1, Rs: 0, AmplitudePu: 1.0, FrequencyHz: F) }, IncludeLoads: true));
        const double dt = 1e-5;
        var probes = new[] { new EmtProbe("v2", EmtProbeKind.NodeVoltage, net.BusToNode[2]) };
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, SteadyStateRunSeconds, probes));

        BranchStamp st = AdmittanceStamp.Branch(r, x, b, tap, 0.0);
        Complex yLoad = new Complex(80.0, -30.0) / 100.0;   // constant impedance at 1 pu: (Pd - jQd)/BaseMva
        Complex v2Phasor = -st.Ytf * Complex.One / (st.Ytt + yLoad);

        Complex v2Emt = LastCycleFundamental(res, 0, W, dt);
        double err = (v2Emt - v2Phasor).Magnitude;
        Assert.True(err < 2e-6, $"|V2_emt - V2_phasor| = {err:R} (EMT {v2Emt}, phasor {v2Phasor})");
    }

    /// <summary>
    /// The case-level R078 acceptance check on bundled tapped cases (IEEE 14: 3 taps, IEEE 57: 15 taps). Every
    /// slack/PV bus is an ideal 50 Hz source at its power-flow voltage and each PQ bus's net power-flow injection is
    /// a constant admittance at its power-flow voltage, so the network's exact phasor solution IS the power-flow
    /// solution. The EMT steady state must reproduce every bus voltage. Before R078 (taps dropped) the worst bus
    /// was off by 0.016146232237465356 pu (case14, bus 5) and 0.09263089867708872 pu (case57, bus 46); after it,
    /// 5.442925472231795E-06 and 5.353021737251943E-06 pu (residual transient plus trapezoidal warping).
    /// </summary>
    [Theory]
    [InlineData("case14")]
    [InlineData("case57")]
    public void Tapped_bundled_case_emt_steady_state_matches_the_power_flow(string name)
    {
        GridModel m = name == "case14" ? IeeeCases.Case14() : IeeeCases.Case57();
        Assert.Contains(m.Branches, br => br.TapRatio > 0 && br.TapRatio != 1.0);   // the case really is tapped
        double worst = PowerFlowEquivalentMaxDv(m, 1e-5, 0.5, out int worstBus);
        Assert.True(worst < 2e-5, $"{name}: max |V_emt - V_pf| = {worst:R} pu at bus {worstBus}");
    }

    /// <summary>Build the power-flow-equivalent EMT network (see the test above), run it, and return the worst
    /// last-cycle phasor deviation from the power-flow bus voltages.</summary>
    internal static double PowerFlowEquivalentMaxDv(GridModel m, double dt, double duration, out int worstBus)
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = false });
        Assert.True(pf.Converged, "the power flow must converge for the comparison to mean anything");
        double omega = 2.0 * Math.PI * m.FrequencyHz;
        var vpf = new Dictionary<int, Complex>();
        for (int i = 0; i < pf.BusIds.Count; i++) vpf[pf.BusIds[i]] = Complex.FromPolarCoordinates(pf.Vm[i], pf.VaDeg[i] * Math.PI / 180.0);

        EmtSourceSpec[] sources = m.Buses.Where(b => b.Type != BusType.Pq)
            .Select(b => new EmtSourceSpec(b.Id, 0, vpf[b.Id].Magnitude, m.FrequencyHz, vpf[b.Id].Phase * 180.0 / Math.PI)).ToArray();
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(sources, IncludeLoads: false));
        var elements = new List<EmtElement>(net.Elements);
        int id = 10_000_000;
        for (int i = 0; i < m.Buses.Count; i++)
        {
            Bus b = m.Buses[i];
            if (b.Type != BusType.Pq) continue;
            Complex s = new Complex(pf.PInjMw[i], pf.QInjMvar[i]) / m.BaseMva;
            if (s.Magnitude < 1e-9) continue;   // a zero-injection bus: only the power flow's residual is left
            Complex y = -Complex.Conjugate(s) / (pf.Vm[i] * pf.Vm[i]);   // the injection as an admittance at |V|
            double c = y.Imaginary > 0 ? y.Imaginary / omega : 0.0;
            double l = y.Imaginary < 0 ? 1.0 / (-y.Imaginary * omega) : 0.0;
            elements.Add(new ShuntElement(id++, net.BusToNode[b.Id], G: y.Real, C: c, L: l));
        }
        net = new EmtNetwork(net.NodeCount, elements, net.BusToNode, net.SeriesBranchIds);
        var probes = m.Buses.Select(b => new EmtProbe("v" + b.Id, EmtProbeKind.NodeVoltage, net.BusToNode[b.Id])).ToArray();
        EmtResult res = new EmtEngine(net, dt).Simulate(new EmtCase(net, dt, duration, probes));

        double worst = 0.0;
        worstBus = -1;
        for (int j = 0; j < probes.Length; j++)
        {
            double d = (LastCycleFundamental(res, j, omega, dt) - vpf[m.Buses[j].Id]).Magnitude;
            if (!(d <= worst)) { worst = d; worstBus = m.Buses[j].Id; }   // a NaN is caught as the worst
        }
        return worst;
    }

    // ---- The build mapping -------------------------------------------------------------------------------------

    [Fact]
    public void Build_maps_tap_and_from_end_charging_exactly_as_the_power_flow_stamp()
    {
        const double r = 0.02, x = 0.2, b = 0.1, a = 0.95;
        EmtNetwork net = EmtNetwork.FromGridModel(TwoBus(r, x, b, a),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false));
        SeriesBranch arm = Assert.Single(net.Elements.OfType<SeriesBranch>());
        Assert.Equal(a, arm.Tap);
        Assert.Equal(x / W, arm.L);
        ShuntElement[] charging = net.Elements.OfType<ShuntElement>().ToArray();
        Assert.Equal(2, charging.Length);
        double cEnd = (b / 2.0) / W;
        Assert.Equal(cEnd / (a * a), charging.Single(s => s.Bus == net.BusToNode[1]).C);   // From end, behind the tap
        Assert.Equal(cEnd, charging.Single(s => s.Bus == net.BusToNode[2]).C);             // To end
    }

    [Fact]
    public void Build_maps_a_negative_reactance_to_a_series_capacitor_with_the_same_reactance()
    {
        const double r = 0.01, x = -0.12, a = 0.97;
        EmtNetwork net = EmtNetwork.FromGridModel(TwoBus(r, x, 0.0, a),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false));
        Assert.Empty(net.Elements.OfType<SeriesBranch>());
        SeriesCapacitorBranch cap = Assert.Single(net.Elements.OfType<SeriesCapacitorBranch>());
        Assert.Equal(r, cap.R);
        Assert.Equal(a, cap.Tap);
        Assert.Equal(x, -1.0 / (W * cap.C), 12);   // its reactance at the system frequency is X
        // The series capacitor is still addressable as a series branch for a current probe.
        Assert.Contains(net.SeriesBranchIds, s => s.From == 1 && s.To == 2 && s.ElementId == cap.Id);
    }

    [Fact]
    public void Untapped_branch_builds_exactly_the_original_element()
    {
        // Tap 1 and tap 0 (the "plain line" convention) both give the pre-R078 element, value for value.
        foreach (double tap in new[] { 1.0, 0.0 })
        {
            EmtNetwork net = EmtNetwork.FromGridModel(TwoBus(0.02, 0.2, 0.1, tap),
                new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false));
            SeriesBranch arm = Assert.Single(net.Elements.OfType<SeriesBranch>());
            Assert.Equal(new SeriesBranch(arm.Id, 0, 1, 0.02, 0.2 / W), arm);
            Assert.All(net.Elements.OfType<ShuntElement>(), s => Assert.Equal(0.05 / W, s.C));
        }
    }

    [Fact]
    public void Travelling_wave_mode_never_drops_a_tap()
    {
        // A charged, positive-X branch NOT flagged as a transformer would qualify for a lossless Bergeron line,
        // which has no tap. With an off-nominal tap it must stay a lumped tapped arm instead.
        EmtNetwork net = EmtNetwork.FromGridModel(TwoBus(0.02, 0.2, 0.1, 1.05, isTransformer: false),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false, UseTravellingWaveLines: true));
        Assert.Empty(net.Elements.OfType<BergeronLine>());
        Assert.Equal(1.05, Assert.Single(net.Elements.OfType<SeriesBranch>()).Tap);

        // The untapped twin still becomes a travelling-wave line, as before.
        EmtNetwork plain = EmtNetwork.FromGridModel(TwoBus(0.02, 0.2, 0.1, 1.0, isTransformer: false),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false, UseTravellingWaveLines: true));
        Assert.Single(plain.Elements.OfType<BergeronLine>());
    }

    // ---- Fail loud: the build -------------------------------------------------------------------------------

    [Fact]
    public void Build_refuses_a_phase_shifter_naming_the_branch()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmtNetwork.FromGridModel(TwoBus(0.01, 0.1, 0.0, 1.0, shiftDeg: 30.0, name: "PST-A"),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) })));
        Assert.Contains("branch #0 (1->2, 'PST-A')", ex.Message);
        Assert.Contains("phase-shifting", ex.Message);
    }

    [Fact]
    public void Build_refuses_a_zero_impedance_branch_naming_it()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmtNetwork.FromGridModel(TwoBus(0.0, 0.0, 0.0, 1.0),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) })));
        Assert.Contains("branch #0 (1->2)", ex.Message);
        Assert.Contains("R = X = 0", ex.Message);
    }

    [Fact]
    public void Build_still_accepts_a_pure_resistance_branch()
    {
        // X = 0 with R != 0 is a real (resistive) branch and is modelled, not refused.
        EmtNetwork net = EmtNetwork.FromGridModel(TwoBus(0.05, 0.0, 0.0, 1.0),
            new EmtBuildOptions(new[] { new EmtSourceSpec(1) }, IncludeLoads: false));
        SeriesBranch arm = Assert.Single(net.Elements.OfType<SeriesBranch>());
        Assert.Equal(0.0, arm.L);
        Assert.Equal(0.05, arm.R);
    }

    [Fact]
    public void Series_capacitance_helper_refuses_a_non_negative_reactance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EmtUnits.SeriesC(0.0, W));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmtUnits.SeriesC(0.1, W));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmtUnits.SeriesC(double.NaN, W));
        Assert.Equal(1.0 / (0.2 * W), EmtUnits.SeriesC(-0.2, W));
    }

    // ---- Fail loud: the engine -------------------------------------------------------------------------------

    private static EmtNetwork OneArm(EmtElement arm) => new(2, new List<EmtElement>
    {
        new VoltageSource(10, Bus: 0, Rs: 0, Emf: _ => 1.0),
        arm,
        new ShuntElement(11, Bus: 1, G: 1.0),
    });

    [Fact]
    public void Engine_refuses_a_negative_inductance_instead_of_making_it_a_resistor()
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesBranch(1, 0, 1, 0.01, -1e-3)), 1e-5));
        Assert.Contains("negative inductance", ex.Message);
        Assert.Contains("SeriesCapacitorBranch", ex.Message);
    }

    [Fact]
    public void Engine_refuses_a_zero_impedance_series_arm()
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesBranch(1, 0, 1, 0.0, 0.0)), 1e-5));
        Assert.Contains("R = L = 0", ex.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Engine_refuses_a_tap_that_is_not_a_positive_finite_ratio(double tap)
    {
        var ex = Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesBranch(1, 0, 1, 0.01, 1e-3, tap)), 1e-5));
        Assert.Contains("tap ratio", ex.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesCapacitorBranch(1, 0, 1, 0.01, 1e-3, tap)), 1e-5));
        Assert.Contains("tap ratio", ex2.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1e-3)]
    [InlineData(double.NaN)]
    public void Engine_refuses_a_series_capacitor_without_a_positive_capacitance(double c)
    {
        Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesCapacitorBranch(1, 0, 1, 0.01, c)), 1e-5));
    }

    [Fact]
    public void Engine_refuses_non_finite_series_parameters()
    {
        Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesBranch(1, 0, 1, double.NaN, 1e-3)), 1e-5));
        Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesBranch(1, 0, 1, 0.01, double.PositiveInfinity)), 1e-5));
        Assert.Throws<ArgumentException>(() => new EmtEngine(OneArm(new SeriesCapacitorBranch(1, 0, 1, double.NaN, 1e-3)), 1e-5));
    }

    [Fact]
    public void Engine_names_a_series_capacitor_whose_negative_resistance_cancels_its_companion()
    {
        // C = 0.5, dt = 1e-3: the trapezoidal companion resistance is dt/(2C) = 1e-3 exactly, and R = -1e-3
        // cancels it, giving an infinite conductance. It must be named, not left to surface as a NaN trajectory.
        var ex = Assert.Throws<InvalidOperationException>(() => new EmtEngine(OneArm(new SeriesCapacitorBranch(7, 0, 1, -1e-3, 0.5)), 1e-3));
        Assert.Contains("element 7", ex.Message);
        Assert.Contains("non-finite companion", ex.Message);
    }

    // ---- Bundled cases -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("case145", 24, 24)]   // 24 series capacitors, all behind an off-nominal tap
    [InlineData("case300", 1, 0)]
    [InlineData("pglib_opf_case240_pserc", 12, 0)]
    [InlineData("pglib_opf_case588_sdet", 7, 4)]
    public void Bundled_series_compensated_cases_build_their_capacitors(string name, int capacitors, int tappedCapacitors)
    {
        GridModel m = name switch
        {
            "case145" => IeeeCases.Case145(),
            "case300" => IeeeCases.Case300(),
            _ => MatpowerParser.ParseFile(Path.Combine(DataDir, "matpower", "nesta", name + ".m")),
        };
        int src = m.Generators[0].Bus;
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(src, FrequencyHz: F) }));
        SeriesCapacitorBranch[] caps = net.Elements.OfType<SeriesCapacitorBranch>().ToArray();
        Assert.Equal(capacitors, caps.Length);
        Assert.Equal(tappedCapacitors, caps.Count(c => c.Tap != 1.0));
        // Every element must be accepted by the engine (no refused series element, no non-finite companion).
        _ = new EmtEngine(net, 20e-6);
    }

    [Fact]
    public void Bundled_phase_shifter_case_is_refused_naming_the_branch()
    {
        GridModel m = MatpowerParser.ParseFile(Path.Combine(DataDir, "matpower", "benchmarks", "cigre_mv.m"));
        var ex = Assert.Throws<ArgumentException>(() => EmtNetwork.FromGridModel(m,
            new EmtBuildOptions(new[] { new EmtSourceSpec(m.Generators[0].Bus, FrequencyHz: F) })));
        Assert.Contains("branch #12 (0->1)", ex.Message);
        Assert.Contains("shift 30 deg", ex.Message);
    }

    [Fact]
    public void Gb_full_builds_no_tapped_arm_and_no_series_capacitor()
    {
        // gb-full has every tap at 1 and no X <= 0, so R078 must not change a single element of its build (its
        // trajectory digest is pinned in EmtUntappedBitIdentityTests).
        GridModel m = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(DataDir, "gb-full"));
        EmtNetwork net = EmtNetwork.FromGridModel(m, new EmtBuildOptions(new[] { new EmtSourceSpec(m.Generators[0].Bus) }));
        Assert.Empty(net.Elements.OfType<SeriesCapacitorBranch>());
        Assert.All(net.Elements.OfType<SeriesBranch>(), s => Assert.Equal(1.0, s.Tap));
    }
}
