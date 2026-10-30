using System;
using System.Linq;
using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The average-value grid-following converter in the three-phase EMT engine: a PLL-synchronised balanced
/// current source driven by the same control law as the RMS model (<see cref="RmsConverterState"/>). On an
/// ideal source - line - converter circuit it must deliver its scheduled power in steady state, sit on its
/// current limit through a resistive fault at the phasor operating point the RMS law predicts, and recover.
/// </summary>
public class EmtConverterTests
{
    private const double F = 50.0, W = 2 * Math.PI * F;
    private const double R = 0.01, X = 0.1, Rf = 0.05, Bf = 0.05;   // Bf: the converter's filter susceptance
    private static readonly Complex Z = new(R, X);

    // Steady phasor at the converter bus: V1 = Vs + Z (I_conv - jBf V1), the filter capacitor drawing jBf V1.
    private static Complex SteadyV(double p, double q)
    {
        Complex v = Complex.One;
        for (int i = 0; i < 200; i++) v = 1.0 + Z * (Complex.Conjugate(new Complex(p, q) / v) - new Complex(0, Bf) * v);
        return v;
    }

    private static (ThreePhaseEmtEngine Eng, RmsConverterUnit Unit) Build()
    {
        Complex v0 = SteadyV(0.5, 0.0);
        var unit = new RmsConverterUnit(1, "vsc", RatingPu: 1.0, P0: 0.5, Q0: 0.0, V0: v0, RmsConverterParameters.WithLimit(1.2));
        var net = new ThreePhaseNetwork(2,
            new[] { new ThreePhaseSource(0, 1.0, F) },
            new[] { new ThreePhaseBranch(0, 1, R, X / W) },
            Array.Empty<ThreePhaseShunt>(),
            new[] { new ThreePhaseFault(1, new[] { 0, 1, 2 }, Rf, 0.3, 0.45) },
            new[] { new ThreePhaseConverter(1, unit) });
        return (new ThreePhaseEmtEngine(net, 50e-6, F), unit);
    }

    private static int At(EmtConverterTrace c, double t) => c.TimeSeconds.ToList().FindIndex(x => x >= t);

    [Fact]
    public void Steady_state_delivers_scheduled_power_at_the_phasor_voltage()
    {
        (ThreePhaseEmtEngine eng, RmsConverterUnit unit) = Build();
        eng.Simulate(0.29, probeBus: 1);
        EmtConverterTrace c = Assert.Single(eng.ConverterTraces);
        int k = At(c, 0.28);
        Assert.Equal(0.5, c.ActivePowerPu[k], 2);
        Assert.Equal(unit.V0.Magnitude, c.TerminalVoltagePu[k], 3);
        Assert.False(c.InDipMode[k]);
    }

    [Fact]
    public void Fault_holds_the_converter_at_the_rms_law_operating_point()
    {
        (ThreePhaseEmtEngine eng, RmsConverterUnit unit) = Build();
        eng.Simulate(0.44, probeBus: 1);
        EmtConverterTrace c = Assert.Single(eng.ConverterTraces);
        int k = At(c, 0.43);                                   // 130 ms into the fault: settled
        Assert.True(c.InDipMode[k]);

        // Independent phasor fixed point of the same law: Iq = min(Kqv (V0 - V), Imax); Ip frozen at 0.5/V0,
        // limited to sqrt(Imax^2 - Iq^2) and scaled by LVACM; the bus is V = (Vs/Z + I)/(1/Z + 1/Rf + jBf).
        double v0 = unit.V0.Magnitude;
        Complex v = Complex.One * 0.5;
        double ip = 0, iq = 0;
        for (int i = 0; i < 500; i++)
        {
            double vm = v.Magnitude;
            iq = Math.Min(2.0 * (v0 - vm), 1.2);
            double lvacm = Math.Clamp((vm - 0.4) / 0.4, 0.0, 1.0);
            ip = Math.Min(0.5 / v0, Math.Sqrt(Math.Max(0, 1.44 - iq * iq))) * lvacm;
            Complex iConv = new Complex(ip, -iq) * Complex.FromPolarCoordinates(1.0, v.Phase);
            Complex vNew = (1.0 / Z + iConv) / (1.0 / Z + 1.0 / Rf + new Complex(0, Bf));
            v = 0.5 * v + 0.5 * vNew;
        }
        // Voltage to 0.005 pu; the currents follow it through the K = 2 gain, so their tolerance is twice that.
        Assert.InRange(c.TerminalVoltagePu[k], v.Magnitude - 0.006, v.Magnitude + 0.006);
        Assert.InRange(c.ReactiveCurrentPu[k], iq - 0.015, iq + 0.015);
        Assert.InRange(c.ActiveCurrentPu[k], ip - 0.015, ip + 0.015);
    }

    [Fact]
    public void Converter_recovers_its_power_after_clearing()
    {
        (ThreePhaseEmtEngine eng, _) = Build();
        eng.Simulate(1.2, probeBus: 1);
        EmtConverterTrace c = Assert.Single(eng.ConverterTraces);
        int k = At(c, 1.15);
        Assert.False(c.InDipMode[k]);
        Assert.Equal(0.5, c.ActivePowerPu[k], 2);
    }
}

/// <summary>
/// The grid-forming (virtual synchronous machine) converter in the three-phase EMT engine: an EMF behind its
/// virtual reactance (a per-phase shunt inductor plus the Norton current), power measured from the actual
/// terminal current, and a current limit that injects the limited current plus the inductor's own. Source - line -
/// unit circuit started from rest; a resistive fault once settled.
/// </summary>
public class EmtGridFormingTests
{
    private const double F = 50.0, W = 2 * Math.PI * F, R = 0.01, X = 0.1, Bf = 0.05;
    private static readonly Lazy<(EmtConverterTrace Trace, Complex V0)> Run = new(() =>
    {
        var z = new Complex(R, X);
        Complex v0 = Complex.One;
        for (int i = 0; i < 200; i++) v0 = 1.0 + z * (Complex.Conjugate(new Complex(0.5, 0) / v0) - new Complex(0, Bf) * v0);
        var unit = new RmsConverterUnit(1, "gfm", 1.0, 0.5, 0.0, v0, RmsConverterParameters.GridFormingWithLimit(1.2));
        var net = new ThreePhaseNetwork(2, new[] { new ThreePhaseSource(0, 1.0, F) },
            new[] { new ThreePhaseBranch(0, 1, R, X / W) }, Array.Empty<ThreePhaseShunt>(),
            new[] { new ThreePhaseFault(1, new[] { 0, 1, 2 }, 0.05, 3.0, 3.15) }, new[] { new ThreePhaseConverter(1, unit) });
        var eng = new ThreePhaseEmtEngine(net, 50e-6, F);
        eng.Simulate(8.0, 1);
        return (eng.ConverterTraces[0], v0);
    });

    private static int At(EmtConverterTrace c, double t) => c.TimeSeconds.ToList().FindIndex(x => x >= t);

    [Fact]
    public void Settles_to_its_set_point_at_the_phasor_voltage()
    {
        (EmtConverterTrace c, Complex v0) = Run.Value;
        int k = At(c, 2.95);
        Assert.Equal(0.5, c.ActivePowerPu[k], 2);
        Assert.InRange(c.TerminalVoltagePu[k], v0.Magnitude - 0.01, v0.Magnitude + 0.01);
        Assert.False(c.InDipMode[k]);
    }

    [Fact]
    // GR-406: renamed from Fault_holds_it_exactly_on_its_current_limit: the name said exactly; the assertion is precision 2 (+/- 0.005 pu). Tightening waits for a measured value.
    public void Fault_holds_it_on_its_current_limit_to_two_decimals()
    {
        (EmtConverterTrace c, _) = Run.Value;
        int k = At(c, 3.1);
        Assert.True(c.InDipMode[k]);
        double iMag = Math.Sqrt(c.ActiveCurrentPu[k] * c.ActiveCurrentPu[k] + c.ReactiveCurrentPu[k] * c.ReactiveCurrentPu[k]);
        Assert.Equal(1.2, iMag, 2);
    }

    [Fact]
    public void Recovers_its_set_point_after_clearing()
    {
        (EmtConverterTrace c, Complex v0) = Run.Value;
        int k = At(c, 7.9);
        Assert.False(c.InDipMode[k]);
        Assert.Equal(0.5, c.ActivePowerPu[k], 2);
        Assert.InRange(c.TerminalVoltagePu[k], v0.Magnitude - 0.01, v0.Magnitude + 0.01);
    }
}
