using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP20 part B and the WP20 part A review fixes (2.1.121):
/// <list type="number">
/// <item>Distributed generation in the composite load (<see cref="CompositeLoadGeneration"/>): a DER_A unit at the far-end
/// node. Verified by a flat start (the t = 0 draw is the power-flow P and Q, and the load stays at rest) and by bit-equality
/// of its DER_A states with an independent <see cref="DerAState"/> driven by the same far-end voltages (the existing DER_A
/// model, unchanged, is what runs).</item>
/// <item>Quiet start: a load whose tap changer, relays or contactors would act with no disturbance is refused.</item>
/// <item>Contactor data: reconnection voltages below the drop voltages, or 0, are refused.</item>
/// <item>Timers count whole steps (<see cref="StepTimer"/>): checked at steps of 0.005 s and 0.01 s, which are not powers of
/// two, so a summed timer would act one step late.</item>
/// <item>The CMLDBLU1/2 DYR refusal reasons (B29) and the unchanged CIMWBL reason.</item>
/// <item>The SciPy step response of case 20 at 34 sample times, with its bounds stated.</item>
/// <item>Bit-identity of everything else with the 2.1.120 build (see <see cref="The_full_part_a_load_alone_gives_the_2_1_120_bits"/>).</item>
/// </list>
/// </summary>
public sealed class Rc5Wp20PartBTests
{
    private const double Hz = 50.0;
    private const double Eps = 1.1102230246251565e-16;   // 2^-53, the unit roundoff

    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);

    private static InductionMotorParameters Motor(double torqueExponent, double h = 1.0) =>
        InductionMotorParameters.FromCircuit(0.01, 0.06, 4.0, 0.03, 0.04, h, InductionMotorTorque.PowerLaw(torqueExponent), Hz);

    private static readonly StaticLoadCharacteristic Static = new(0.5, 2.0, 0.3, 1.0);

    private static CompositeLoadData Network(double fma, CompositeLoadExtensions? x, double p = 80.0, double q = 30.0) =>
        new(1, p, q, 100.0, 0.08, 1.0, 0.02, 0.02, 0.04, 0.04, 0.5, fma, 0.8, Motor(2.0), 0.95, Static, Static) { Extensions = x };

    private static AcMotorParameters MotorDWecc => AcMotorParameters.Wecc(0.8, 0.97, 0.6, 9999.0, 0.124, 0.114);

    /// <summary>The case-20 parts of Rc5Wp20CompositeLoadTests: B 20 %, D 15 %, electronic 10 %.</summary>
    private static CompositeLoadExtensions Case20Parts => new()
    {
        MotorB = new CompositeLoadMotor(0.2, 0.7, Motor(0.0, 0.5)),
        MotorDFraction = 0.15, MotorD = MotorDWecc,
        ElectronicFraction = 0.1, Electronic = new ElectronicLoadParameters(0.95, 0.7, 0.5, 0.8),
    };

    private static CompositeLoadExtended.State Start(CompositeLoadData d, Complex v0)
    {
        DynamicLoadPlan plan = d.Plan(v0, 100.0, Hz);
        return (CompositeLoadExtended.State)plan.CreateModel();
    }

    private static Complex Drawn(IRmsConverterModel m, Complex v)
    {
        Complex inj = m.Commit(v);
        return v * Complex.Conjugate(-m.TerminalCurrent(inj, v));
    }

    // ------------------------------------------------------------------------------------ distributed generation

    private static CompositeLoadGeneration Dg(DeraParameters? p = null, double fraction = 0.2, double pf = 1.0) =>
        new(fraction, 0.8, p ?? new DeraParameters(), pf);

    [Fact]
    public void With_distributed_generation_the_load_starts_at_its_power_and_stays_at_rest()
    {
        foreach (double pf in new[] { 1.0, 0.95, -0.9 })
        {
            CompositeLoadData d = Network(0.3, Case20Parts with { Generation = Dg(pf: pf) }, 47.8, -3.9);
            Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
            CompositeLoadExtended.State m = Start(d, v0);
            DerAState g = m.Generation!;
            // DER_A starts at its loading factor on its own base, and with the power factor's Q.
            Assert.True(Math.Abs(g.Unit.P0 / g.Unit.RatingPu - 0.8) <= 2 * Eps, $"P0 on the rating {g.Unit.P0 / g.Unit.RatingPu:R}");
            double tan = Math.Sqrt(1.0 - pf * pf) / Math.Abs(pf) * Math.Sign(pf);
            Assert.True(Math.Abs(g.Unit.Q0 - g.Unit.P0 * tan) <= 2 * Eps * Math.Abs(g.Unit.Q0));
            Assert.True(g.Unit.P0 > 0.0);
            // The t = 0 draw: the part A flat-start bound (32 eps |Y| |V|^2, |Y| below 10 pu) plus the generation's current,
            // which DER_A forms as (P0 / V0) and (Q0 / V0) on its rating (a few ulp of a current below 0.2 pu): 8 eps.
            double tol = 32 * Eps * 10.0 * 1.01 * 1.01 + 8 * Eps;
            Complex s0 = Drawn(m, v0);
            Assert.True(Math.Abs(s0.Real - 0.478) <= tol && Math.Abs(s0.Imaginary + 0.039) <= tol, $"pf {pf}: draws {s0} at t = 0");
            Complex vf0 = m.FarEndVoltage;
            double worst = 0, worstVf = 0;
            for (int n = 0; n < 400; n++)   // 2 s of 5 ms
            {
                m.Step(n * 0.005, v0, new[] { v0 }, 0.005);
                worst = Math.Max(worst, (Drawn(m, v0) - s0).Magnitude);
                worstVf = Math.Max(worstVf, (m.FarEndVoltage - vf0).Magnitude);
            }
            // At rest: the internal solve stops when a pass moves the far end by under 1e-13 pu, and DER_A reads the far-end
            // voltage through its current P / V (gain below 1 on a 0.2 pu current), so the draw moves by well under 1e-12.
            Assert.True(worstVf <= 1e-12, $"pf {pf}: far end moved {worstVf:R} at rest");
            Assert.True(worst <= 1e-12, $"pf {pf}: draw moved {worst:R} at rest");
            Assert.False(g.FrequencyTripped);
        }
    }

    [Fact]
    public void The_generation_is_the_der_a_model_unchanged_driven_by_the_far_end_voltage()
    {
        // DER_A with voltage tripping (vl0 0.44, vl1 0.49, tvl0 0.16 s, Vrfrac 0): a dip of the bus to 0.3 pu for 0.4 s
        // takes the far end below vl0 for longer than tvl0, so the generation trips for good.
        var p = new DeraParameters { VtripFlag = 1 };
        CompositeLoadData d = Network(0.3, Case20Parts with { Generation = Dg(p) });
        CompositeLoadExtended.State m = Start(d, Complex.One);
        // An independent DER_A on the same unit, driven as the kernel drives a DER_A unit, with the far-end voltage the
        // composite load solved on each step: every state it reports must be the composite load's, bit for bit.
        var twin = new DerAState(m.Generation!.Unit);
        double ip0 = 0;
        const double dt = 1.0 / 256.0;
        for (int k = 0; k < 512; k++)
        {
            Complex v = k >= 26 && k < 128 ? new Complex(0.3, 0.0) : Complex.One;
            m.Commit(v);
            m.Step(k * dt, v, new[] { v }, dt);
            Complex vf = m.FarEndVoltage;
            twin.Commit(vf);
            twin.Step(k * dt, vf, new[] { vf }, dt);
            DerAState g = m.Generation;
            Assert.Equal(BitConverter.DoubleToInt64Bits(twin.ActiveCurrent), BitConverter.DoubleToInt64Bits(g.ActiveCurrent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(twin.ReactiveCurrent), BitConverter.DoubleToInt64Bits(g.ReactiveCurrent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(twin.PowerOrder), BitConverter.DoubleToInt64Bits(g.PowerOrder));
            Assert.Equal(BitConverter.DoubleToInt64Bits(twin.TripMultiplier), BitConverter.DoubleToInt64Bits(g.TripMultiplier));
            if (k == 0) ip0 = g.ActiveCurrent;
        }
        Assert.True(ip0 > 0.0);
        // Tripped: the multiplier went to 0 and stays there (Vrfrac 0); after 1.5 s at 1 pu the Tg and Tv lags (0.02 s) have
        // taken the active current to 0 within e^-70 of its start.
        Assert.True(m.Generation.TripMultiplier < 1e-12, $"multiplier {m.Generation.TripMultiplier:R}");
        Assert.True(m.Generation.ActiveCurrent <= 1e-12 * ip0, $"Ip {m.Generation.ActiveCurrent:R}");
        // The bus now carries the load without the generation: it draws more than its starting 0.8 pu.
        Assert.True(Drawn(m, Complex.One).Real > 0.8);
    }

    /// <summary>
    /// 2.1.122 (review of 2.1.121): an independent current balance of the internal network with the generation in it. On
    /// every step, before the load advances, the network is solved at the step's bus voltage, and the two node equations
    /// are checked from the setup's own data, not from the solver:
    /// <list type="bullet">
    /// <item>low-side node: yt (V/n - Vl) = j Bl Vl + yf (Vl - Vf);</item>
    /// <item>far-end node: yf (Vl - Vf) + sum over motors A to C of (E' - Vf) Y k + I_dg = j Bf Vf + (yD + Yel + Ys) Vf,
    /// with I_dg the current of the independent DER_A twin (the same unit, driven by the same far-end voltages) at Vf.</item>
    /// </list>
    /// The solver stops when a pass moves Vf by under 1e-13 pu and evaluates the voltage-dependent terms and the DER_A current
    /// at the previous pass's Vf, so the far-end residual is at most (|a22| + |dI_dg/dVf|) 1e-13, with |a22| below 100 pu
    /// here (yf = 1/|0.02 + j0.04| = 22, each motor's 1/|Ra + jX'| k below 20, the shunt and static terms below 2); 1e-10
    /// leaves a factor of 10 for the DER_A current's slope and rounding. The dip to 0.3 pu trips the generation part-way, so
    /// the balance is checked with the generation on, falling and off.
    /// </summary>
    [Fact]
    public void The_generation_current_enters_the_far_end_node_balance()
    {
        var p = new DeraParameters { VtripFlag = 1 };
        CompositeLoadData d = Network(0.3, Case20Parts with { Generation = Dg(p) });
        CompositeLoadExtended.State m = Start(d, Complex.One);
        var s = (CompositeLoadExtended.Setup)typeof(CompositeLoadExtended.State)
            .GetField("_s", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(m)!;
        var twin = new DerAState(m.Generation!.Unit);
        const double dt = 1.0 / 256.0;
        double worstFar = 0.0, worstLow = 0.0, largestDg = 0.0;
        for (int k = 0; k < 512; k++)
        {
            Complex v = k >= 26 && k < 128 ? new Complex(0.3, 0.0) : Complex.One;
            m.Commit(v);
            Complex src = Complex.Zero;
            foreach (CompositeLoadExtended.MotorRun r in m.Motors) src += r.Motor.Emf * r.Motor.Admittance * r.K;
            (Complex vl, Complex vf, _) = m.Solve(v, src, m.FarEndVoltage);

            double n = m.Tap;
            Complex vlp = v / n;
            Complex yt = Complex.One / new Complex(0.0, s.Xt), yf = Complex.One / s.Zf;
            double vm = vf.Magnitude;
            Complex yLoad = new Complex(0.0, s.Bf) + m.MotorDAdmittance;
            double fe = m.ElectronicFraction(vm);
            if (s.Electronic is not null && fe > 0.0) yLoad += Complex.Conjugate(new Complex(s.Pe0, s.Qe0) * fe) / (vm * vm);
            if (s.StaticFraction > 0.0)
            {
                (double ve, double scale) = LoadChecks.LowVoltage(vm, s.Vlow);
                double u = ve / s.V0f;
                yLoad += Complex.Conjugate(new Complex(s.Ps0 * s.Sp.Factor(u) * scale, s.Qs0 * s.Sq.Factor(u) * scale)) / (vm * vm);
            }
            Complex motors = Complex.Zero;
            foreach (CompositeLoadExtended.MotorRun r in m.Motors) motors += (r.Motor.Emf - vf) * r.Motor.Admittance * r.K;
            Complex idg = twin.TrialCurrent(vf);
            largestDg = Math.Max(largestDg, idg.Magnitude);
            worstFar = Math.Max(worstFar, (yf * (vl - vf) + motors + idg - yLoad * vf).Magnitude);
            worstLow = Math.Max(worstLow, (yt * (vlp - vl) - new Complex(0.0, s.Bl) * vl - yf * (vl - vf)).Magnitude);

            m.Step(k * dt, v, new[] { v }, dt);
            Complex vfs = m.FarEndVoltage;
            twin.Commit(vfs);
            twin.Step(k * dt, vfs, new[] { vfs }, dt);
        }
        Assert.True(largestDg > 0.05, $"the generation current reached only {largestDg:R} pu");
        Assert.True(worstFar <= 1e-10, $"far-end residual {worstFar:R}");
        Assert.True(worstLow <= 1e-10, $"low-side residual {worstLow:R}");
    }

    [Fact]
    public void A_zero_generation_fraction_is_the_load_without_generation_bit_for_bit()
    {
        Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        Assert.Equal(Trace(Network(0.3, Case20Parts), v0), Trace(Network(0.3, Case20Parts with { Generation = Dg(fraction: 0.0) }), v0));
    }

    private static string Trace(CompositeLoadData d, Complex v0)
    {
        DynamicLoadPlan plan = d.Plan(v0, 100.0, Hz);
        IRmsConverterModel m = plan.CreateModel();
        var sb = new StringBuilder();
        for (int k = 0; k < 300; k++)
        {
            Complex v = k < 20 ? v0 : k < 50 ? v0 * 0.6 : v0 * 0.99;
            Complex inj = m.Commit(v);
            m.Step(k * 0.005, v, new[] { v }, 0.005);
            sb.Append(BitConverter.DoubleToInt64Bits(inj.Real).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(BitConverter.DoubleToInt64Bits(inj.Imaginary).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    [Fact]
    public void Inconsistent_generation_data_is_refused_by_name()
    {
        Complex v0 = Complex.One;
        Assert.Contains("[0, 1)", Assert.ThrowsAny<ArgumentException>(() => Network(0.3, Case20Parts with { Generation = Dg(fraction: 1.0) }).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        Assert.Contains("power factor", Assert.ThrowsAny<ArgumentException>(() => Network(0.3, Case20Parts with { Generation = Dg(pf: 0.0) }).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        Assert.Contains("Imax", Assert.ThrowsAny<ArgumentException>(() => Network(0.3, Case20Parts with { Generation = Dg(new DeraParameters { Imax = 0.0 }) }).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
        // DER_A's own start check: the far end (about 1 pu) is not above vl1 = 1.2.
        var high = new DeraParameters { Vl0 = 1.15, Vl1 = 1.2, Vh1 = 1.3, Vh0 = 1.35 };
        Assert.Contains("vl1", Assert.Throws<InvalidOperationException>(() => Network(0.3, Case20Parts with { Generation = Dg(high) }).Plan(v0, 100.0, Hz)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ quiet start

    [Fact]
    public void A_start_that_would_set_off_a_relay_contactor_or_the_tap_changer_is_refused()
    {
        Complex v0 = Complex.One;
        InvalidOperationException Refused(CompositeLoadExtensions x) => Assert.Throws<InvalidOperationException>(() => Network(0.4, x).Plan(v0, 100.0, Hz));
        // The low side starts near 1 pu, outside [1.05, 1.1].
        Assert.Contains("outside the LTC band", Refused(new CompositeLoadExtensions { Ltc = new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 1.05, 1.1, 30.0, 5.0) }).Message, StringComparison.Ordinal);
        Assert.Contains("outside the LTC band", Refused(new CompositeLoadExtensions { Ltc = new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 0.8, 0.9, 30.0, 5.0) }).Message, StringComparison.Ordinal);
        // The far end starts near 1 pu, below a relay stage at 1.2 (each stage, each motor).
        Assert.Contains("motor A under-voltage relay", Refused(new CompositeLoadExtensions { MotorARelay = new MotorUnderVoltageRelay(1.2, 0.1, 0.2) }).Message, StringComparison.Ordinal);
        Assert.Contains("motor A under-voltage relay", Refused(new CompositeLoadExtensions { MotorARelay = new MotorUnderVoltageRelay(0.5, 0.1, 0.2, 1.2, 0.1, 0.2) }).Message, StringComparison.Ordinal);
        Assert.Contains("motor B under-voltage relay", Refused(new CompositeLoadExtensions { MotorB = new CompositeLoadMotor(0.2, 0.8, Motor(2.0), new MotorUnderVoltageRelay(1.2, 0.1, 0.2)) }).Message, StringComparison.Ordinal);
        // A stage with no fraction never acts, so its setting does not matter.
        Network(0.4, new CompositeLoadExtensions { MotorARelay = new MotorUnderVoltageRelay(1.2, 0.1, 0.0) }).Plan(v0, 100.0, Hz);
        // Motor D's under-voltage relay (with Fuvr above 0) and contactors.
        Assert.Contains("motor D's under-voltage relay", Refused(new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { UvFraction = 0.1, Vtr1 = 1.2, Ttr1 = 0.1 } }).Message, StringComparison.Ordinal);
        Assert.Contains("motor D's under-voltage relay", Refused(new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { UvFraction = 0.1, Vtr2 = 1.2, Ttr2 = 0.1 } }).Message, StringComparison.Ordinal);
        Network(0.4, new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { UvFraction = 0.0, Vtr1 = 1.2, Ttr1 = 0.1 } }).Plan(v0, 100.0, Hz);
        Assert.Contains("contactor setting Vc1off", Refused(new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { Vc1off = 1.2, Vc2off = 0.5, Vc1on = 1.25, Vc2on = 1.2 } }).Message, StringComparison.Ordinal);
        // The same direct on motor D's state.
        Assert.Contains("under-voltage relay", Assert.Throws<InvalidOperationException>(() => new AcMotorState(MotorDWecc with { UvFraction = 0.2, Vtr1 = 0.9 }, 0.85, "test")).Message, StringComparison.Ordinal);
        Assert.Contains("contactor", Assert.Throws<InvalidOperationException>(() => new AcMotorState(MotorDWecc with { Vc1off = 0.9, Vc2off = 0.7, Vc1on = 0.95, Vc2on = 0.9 }, 0.85, "test")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contactor_reconnection_settings_below_the_drop_settings_or_zero_are_refused()
    {
        Complex v0 = Complex.One;
        string Refused(AcMotorParameters md) => Assert.ThrowsAny<ArgumentException>(() =>
            Network(0.4, new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = md }).Plan(v0, 100.0, Hz)).Message;
        // The review's chattering case: Vc1off 0.5, Vc2off 0.4 with the reconnection voltages left at 0.
        Assert.Contains("must be above 0", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4 }), StringComparison.Ordinal);
        Assert.Contains("must be above 0", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.0, Vc1on = 0.6, Vc2on = 0.0 }), StringComparison.Ordinal);
        Assert.Contains("Vc1on must not be below Vc1off", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.45, Vc2on = 0.4 }), StringComparison.Ordinal);
        Assert.Contains("Vc2on must not be below Vc2off", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.6, Vc2on = 0.35 }), StringComparison.Ordinal);
        // 2.1.122: reconnection starting below the drop voltage (Vc2off < Vc2on < Vc1off) is refused. At a steady 0.47 pu,
        // (Vc1off, Vc2off, Vc1on, Vc2on) = (0.5, 0.4, 0.6, 0.45) drops part of motor D on one step and reconnects some of it on
        // the next, for ever (the review's simulation of the update rule: 0.7, 0.74, 0.7, ...). So did the settings this
        // test accepted before, (0.5, 0.4, 0.5, 0.4), at a steady 0.45 pu (0.5, 0.75, 0.5, ...).
        Assert.Contains("Vc2on must not be below Vc1off", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.6, Vc2on = 0.45 }), StringComparison.Ordinal);
        Assert.Contains("Vc2on must not be below Vc1off", Refused(MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.5, Vc2on = 0.4 }), StringComparison.Ordinal);
        // Reconnection starting at the drop voltage is accepted: the WECC defaults (0.5, 0.4, 0.6, 0.5), and the edge
        // (0.5, 0.4, 0.5, 0.5). Contactors not in use (Vc1off 0) take any reconnection setting.
        Network(0.4, new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.6, Vc2on = 0.5 } }).Plan(v0, 100.0, Hz);
        Network(0.4, new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc with { Vc1off = 0.5, Vc2off = 0.4, Vc1on = 0.5, Vc2on = 0.5 } }).Plan(v0, 100.0, Hz);
        Network(0.4, new CompositeLoadExtensions { MotorDFraction = 0.1, MotorD = MotorDWecc }).Plan(v0, 100.0, Hz);
    }

    // ------------------------------------------------------------------------------------------------------ timers

    [Fact]
    public void A_timer_counts_whole_steps()
    {
        // ceil(T / dt) with a 4-ulp guard. 30 / 0.005, 5 / 0.005, 0.1 / 0.01, 0.02 / 0.005 are whole numbers; 0.033 x 1024 =
        // 33.792 is not; a 0 delay acts on the first step.
        Assert.Equal(6000.0, StepTimer.Steps(30.0, 0.005));
        Assert.Equal(1000.0, StepTimer.Steps(5.0, 0.005));
        Assert.Equal(10.0, StepTimer.Steps(0.1, 0.01));
        Assert.Equal(4.0, StepTimer.Steps(0.02, 0.005));
        Assert.Equal(34.0, StepTimer.Steps(0.033, 1.0 / 1024.0));
        Assert.Equal(0.0, StepTimer.Steps(0.0, 0.01));
        Assert.True(StepTimer.Reached(1, 0.0, 0.01));
        // Summing the steps instead falls short: 10 x 0.01 is 0.09999999999999999, below 0.1.
        double sum = 0.0;
        for (int k = 0; k < 10; k++) sum += 0.01;
        Assert.True(sum < 0.1);
    }

    [Fact]
    public void The_tap_changer_moves_on_whole_steps_of_5_ms()
    {
        // The WP20 spec's LTC case (Tdel 30 s, Ttap 5 s, band [0.98, 1.02], step 0.00625, stiff 0.95 pu) at dt = 0.005 s:
        // moves at the ends of steps 6000, 7000, 8000, 9000 and 10000 (t = 30, 35, 40, 45, 50 s).
        var ltc = new CompositeLoadLtc(new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 0.98, 1.02, 30.0, 5.0), 1.0);
        var moves = new List<int>();
        for (int k = 0; k < 12000; k++)
            if (ltc.Advance(0.95 / ltc.Tap, 0.005)) moves.Add(k + 1);
        Assert.Equal(new[] { 6000, 7000, 8000, 9000, 10000 }, moves);
        Assert.Equal(-5, ltc.Position);
    }

    [Fact]
    public void Motor_d_timers_act_on_whole_steps_of_5_and_10_ms()
    {
        AcMotorParameters stall = AcMotorParameters.Wecc(1.0, 0.97, 0.6, 0.1, 0.124, 0.114);
        // Stall: Tstall 0.1 s at dt 0.01 s acts at the end of the 10th step below Vstall (k = 9).
        var s = new AcMotorState(stall, 1.0, "test");
        int stalledAt = -1;
        for (int k = 0; k < 20; k++)
        {
            s.Advance(0.55, 0.01);
            if (s.StalledA && stalledAt < 0) stalledAt = k;
        }
        Assert.Equal(9, stalledAt);
        // Restart: Frst 0.5, Vrst 0.9, Trst 0.1 s at dt 0.01: part B restarts at the end of the 10th step above Vrst.
        var r = new AcMotorState(stall with { StallSeconds = 0.0, RestartFraction = 0.5, RestartVoltage = 0.9, RestartSeconds = 0.1 }, 1.0, "test");
        r.Advance(0.5, 0.01);
        Assert.True(r.StalledA && r.StalledB);
        int restartAt = -1;
        for (int k = 0; k < 20; k++)
        {
            r.Advance(1.0, 0.01);
            if (!r.StalledB && restartAt < 0) restartAt = k;
        }
        Assert.Equal(9, restartAt);
        Assert.True(r.StalledA);
        // Under-voltage relay: Ttr1 0.02 s at dt 0.005 s trips at the end of the 4th step below Vtr1 (k = 3).
        var u = new AcMotorState(stall with { StallSeconds = 9999.0, UvFraction = 0.25, Vtr1 = 0.7, Ttr1 = 0.02 }, 1.0, "test");
        int tripAt = -1;
        for (int k = 0; k < 10; k++)
        {
            u.Advance(0.65, 0.005);
            if (u.UvTripped && tripAt < 0) tripAt = k;
        }
        Assert.Equal(3, tripAt);
    }

    [Fact]
    public void A_three_phase_motor_relay_acts_on_whole_steps_of_10_ms()
    {
        // Vtr1 0.7, Ttr1 0.1 s, Ftr1 0.2; the bus at 0.6 pu from the first step puts the far end below 0.7 at once (as in
        // Rc5Wp20CompositeLoadTests), so the stage trips at the end of the 10th step (k = 9).
        var x = new CompositeLoadExtensions { MotorARelay = new MotorUnderVoltageRelay(0.7, 0.1, 0.2) };
        CompositeLoadExtended.State m = Start(Network(0.4, x), Complex.One);
        var v = new Complex(0.6, 0.0);
        for (int k = 0; k < 20; k++)
        {
            m.Commit(v);
            m.Step(k * 0.01, v, new[] { v }, 0.01);
            Assert.True(m.FarEndVoltage.Magnitude < 0.7);
            Assert.Equal(k < 9 ? 1.0 : 0.8, m.Motors[0].Connected);
        }
    }

    // -------------------------------------------------------------------------------------------------------- DYR

    /// <summary>The CIMWBL reason as the 2.1.120 build gave it (read from that build before any 2.1.121 edit): unchanged.</summary>
    private const string CimwblReason = "the order of its last four values (A B D E) is given by one public source only (the PSS/E data sheet; PowerWorld's CIMW page says only that the model is CIM5's with another load torque), so it is not read with an unconfirmed order; give the motor through the API (InductionMotorLoadData with a polynomial load torque)";

    private const string Cmldblu1Reason = "GridSim's CMLD is a documented subset built through the API (CompositeLoadData with its Extensions: motors A to D, the electronic and static loads, the relays, the LTC and DER_A distributed generation); CMLDBLU1 is the first CMLD version and is not read, so give the load through the API";

    private const string Cmldblu2Reason = "GridSim's CMLD is a documented subset built through the API (CompositeLoadData with its Extensions: motors A to D, the electronic and static loads, the relays, the LTC and DER_A distributed generation); the record is not read because its value order is confirmed by one public source only (the PSS/E data sheet) and GridSim does not do the WECC initialisation it relies on (the substation shunt, the feeder compensation and the starting tap computed from the power flow are data in GridSim), so give the load through the API";

    [Fact]
    public void The_cmld_records_stay_refused_with_reasons_that_name_what_the_dyr_route_lacks()
    {
        Assert.Equal(CimwblReason, PsseDyrReader.LoadModels["CIMWBL"]);
        Assert.Equal(Cmldblu1Reason, PsseDyrReader.LoadModels["CMLDBLU1"]);
        Assert.Equal(Cmldblu2Reason, PsseDyrReader.LoadModels["CMLDBLU2"]);
        // B29 carries both rewordings as its qualitative claim.
        GridSim.Core.Journal.BreakingChange b = GridSim.Core.Journal.BreakingChangeMap.Default.Entries.Single(e => e.Id == "B29");
        Assert.Equal("2.1.121", b.Version);
        GridSim.Core.Journal.BreakingChangeClaim c = Assert.Single(b.Claims);
        Assert.Equal("qualitative", c.Kind);
        Assert.Equal("CMLDBLU1: " + Cmldblu1Reason + " | CMLDBLU2: " + Cmldblu2Reason, c.To);
        Assert.Contains("motors B to D, electronic load, LTC and relays are not modelled", c.From, StringComparison.Ordinal);

        // End to end through the reader with --dyr-extended: refused by name with the new reason; without it, unmapped as
        // before.
        string raw = File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(raw, "case14");
        const string Genrou = " 7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0";
        foreach ((string model, string reason) in new[] { ("CMLDBLU1", Cmldblu1Reason), ("CMLDBLU2", Cmldblu2Reason) })
        {
            string dyr = "     1 'GENROU' 1 " + Genrou + " /\n     3 '" + model + "' 1  1  2  3 /\n";
            var ex = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(dyr, r, "case14", new PsseDyrOptions { SourceName = "b.dyr", ExtendedLibrary = true }));
            Assert.Contains(model + " for load '1' at bus 3", ex.Message, StringComparison.Ordinal);
            Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
            var plain = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(dyr, r, "case14", new PsseDyrOptions { SourceName = "b.dyr" }));
            Assert.Contains(model + " x1", plain.Message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------ SciPy step response

    // tests/tooling/dynamic-library-reference.py case 20b (SciPy 1.18.1, Radau, rtol 1e-12): case 20 at every 0.01 s to
    // 0.3 s, then 0.5, 1, 2 and 5 s. The values at 0.05, 0.1, 0.2, 0.5, 1, 2 and 5 s are case 20's, digit for digit.
    private static readonly double[] Times =
    {
        0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07, 0.08, 0.09, 0.1, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16, 0.17, 0.18, 0.19, 0.2,
        0.21, 0.22, 0.23, 0.24, 0.25, 0.26, 0.27, 0.28, 0.29, 0.3, 0.5, 1.0, 2.0, 5.0,
    };

    private static readonly double[] SpeedARef =
    {
        0.9719239558523427, 0.971655735210197, 0.9714130577635072, 0.9711779713590218, 0.9709495570573944, 0.9707319791938868,
        0.9705296139890787, 0.9703454241823706, 0.9701807295256608, 0.9700354988155071, 0.9699087684217917, 0.969799025057009,
        0.9697045004309762, 0.9696233741653635, 0.9695539000513708, 0.9694944752723336, 0.9694436706333387, 0.9694002361474425,
        0.9693630924079831, 0.9693313148088263, 0.9693041150827774, 0.969280822781358, 0.9692608680819722, 0.9692437665290385,
        0.9692291058576008, 0.9692165348017892, 0.96920575367634, 0.9691965064842115, 0.9691885743078736, 0.9691817697678082,
        0.9691426081838337, 0.9691406936260721, 0.9691406927262787, 0.9691406927262788,
    };

    private static readonly double[] SpeedBRef =
    {
        0.9751553872613561, 0.9747237140470145, 0.9743971641944509, 0.974121402291046, 0.9738786894831445, 0.9736662420483586,
        0.9734852381974483, 0.9733361931657434, 0.973217689671224, 0.9731266068233351, 0.9730588281999943, 0.9730099432405833,
        0.9729757611069113, 0.9729526105913828, 0.9729374645575357, 0.97292794451241, 0.9729222559755988, 0.9729190927123105,
        0.9729175347756023, 0.9729169545707801, 0.972916937511956, 0.9729172189819413, 0.9729176365757772, 0.9729180953379722,
        0.9729185433404056, 0.9729189550861617, 0.9729193205952751, 0.9729196384678549, 0.9729199116378512, 0.9729201448871051,
        0.9729214477714089, 0.9729215106922915, 0.9729215107218491, 0.9729215107218491,
    };

    /// <summary>The speeds of motors A and B at each sample time for a step of <paramref name="dt"/>.</summary>
    private static double[][] Speeds(DynamicLoadPlan plan, double dt)
    {
        var m = (CompositeLoadExtended.State)plan.CreateModel();
        var v = new Complex(0.95, 0.0);
        int steps = (int)Math.Round(5.0 / dt), i = 0;
        var a = new double[Times.Length];
        var b = new double[Times.Length];
        for (int k = 1; k <= steps; k++)
        {
            m.Commit(v);
            m.Step((k - 1) * dt, v, new[] { v }, dt);
            if (i < Times.Length && k == (int)Math.Round(Times[i] / dt))
            {
                a[i] = m.Motors[0].Motor.Speed;
                b[i] = m.Motors[1].Motor.Speed;
                i++;
            }
        }
        Assert.Equal(Times.Length, i);
        return new[] { a, b };
    }

    [Fact]
    public void The_extended_composite_load_follows_scipy_at_every_sample_time()
    {
        CompositeLoadData d = Network(0.3, Case20Parts);
        DynamicLoadPlan plan = d.Plan(Complex.One, 100.0, Hz);
        var probe = (CompositeLoadExtended.State)plan.CreateModel();
        double[][] s1 = Speeds(plan, 1e-4), s2 = Speeds(plan, 5e-5);
        double[][] refs = { SpeedARef, SpeedBRef };
        for (int motor = 0; motor < 2; motor++)
        {
            double w0 = probe.Motors[motor].Motor.Speed;
            double change = refs[motor].Max(r => Math.Abs(r - w0));
            double worst1 = 0, worst2 = 0;
            for (int i = 0; i < Times.Length; i++)
            {
                double e1 = Math.Abs(s1[motor][i] - refs[motor][i]), e2 = Math.Abs(s2[motor][i] - refs[motor][i]);
                // The bound at each sample time is WP3b's (1 % of the largest change at 0.1 ms), not derived for this model.
                Assert.True(e1 <= 1e-2 * change, $"motor {motor} at {Times[i]} s: error {e1:R} at 0.1 ms against a change of {change:R}");
                worst1 = Math.Max(worst1, e1);
                worst2 = Math.Max(worst2, e2);
            }
            // First order in the step. For one mode x' = lambda x stepped explicitly, x_n = (1 + lambda dt)^n x0, and the error
            // at time t is C dt + D dt^2 with D / C = -(2 lambda / 3 + lambda^2 t / 4) (from ln(1 + z) = z - z^2/2 + z^3/3).
            // The ratio of the errors at dt/2 and dt is then 0.5 (1 - (D / C) dt / 2) to first order. The fastest pole here
            // is the motor flux: T'o = Xr / (w Rr) = 4.04 / (314.16 x 0.03) = 0.429 s open-circuit, and at the terminals
            // T' = T'o X' / X = 0.429 x 0.0996 / 4.06 = 0.0105 s (about 0.015 s with the transformer and feeder, so 0.0105 s is
            // the bound). It has decayed by e^-15 well before 0.3 s, so it sets the worst error only up to t = 0.3 s:
            // |D / C| <= 2/(3 x 0.0105) + 0.3/(4 x 0.0105^2) = 744 per s. The slow poles (tau >= 0.4 s, t <= 5 s) give at most
            // 2/(3 0.4) + 5/(4 0.16) = 9.5 per s. So the ratio lies within 0.5 (1 +- 744 x 1e-4 / 2) = 0.5 +- 0.019 for one
            // mode. The worst error over 34 times is taken so that no sample where two modes' errors cancel sets it; the window
            // [0.45, 0.55] leaves a factor of 2.6 over the one-mode figure for the mixing of modes.
            Assert.InRange(worst2 / worst1, 0.45, 0.55);
        }
    }

    // --------------------------------------------------------------------------------------------- bit-identity

    // Pinned from the 2.1.120 build before any 2.1.121 edit. Method: GridSim.Core.dll from src/GridSim.Core/bin/Release
    // (2.1.120, built from the sources of commit 732e07d6: no Core source changed after the build; SHA-256
    // CEA0A1519DAEB43B8DCE2EDEA7D0A1A5C9B147220318CE480255F3361E3970C2) was copied aside and a throwaway console program
    // referencing it ran the two scenarios below (the same code as these tests; the load's internal Plan and CreateModel
    // reached by reflection) and printed the SHA-256 of the raw IEEE 754 bits. The scenarios use steps of 2^-8 s, so the
    // part A timers acted on the same steps before and after the whole-step change.
    // 2.1.126: the fixture's Vc2on is 0.45 since 2.1.122 (0.4 is now refused), and the pins were taken with 0.4. They were
    // re-taken on 2.1.120 itself, not on current code: git archive 732e07d6 into a scratch folder outside the repository,
    // a Release build of GridSim.Core alone there, and the same probe referencing that dll. With Vc2on 0.4 the probe gave
    // both old pins again (so the scratch build is the 2.1.120 arithmetic); with 0.45 the load-alone bits are the same as
    // with 0.4 (the setting did not act on that run) and case14 gives the new pin below.

    private static CompositeLoadExtensions PartAAll => new()
    {
        MotorB = new CompositeLoadMotor(0.2, 0.7, Motor(0.0, 0.5)),
        MotorC = new CompositeLoadMotor(0.05, 0.75, Motor(1.0, 0.3), new MotorUnderVoltageRelay(0.5, 0.1, 0.5)),
        MotorARelay = new MotorUnderVoltageRelay(0.6, 0.1, 0.2, 0.4, 0.05, 0.3),
        MotorDFraction = 0.15,
        MotorD = AcMotorParameters.Wecc(0.8, 0.97, 0.6, 0.033, 0.124, 0.114) with
        {
            RestartFraction = 0.3, RestartSeconds = 0.2, UvFraction = 0.1, Vtr1 = 0.5, Ttr1 = 0.1,
            Vc1off = 0.45, Vc2off = 0.35, Vc1on = 0.5, Vc2on = 0.45, ThermalSeconds = 10.0,
        },
        ElectronicFraction = 0.1, Electronic = new ElectronicLoadParameters(0.95, 0.7, 0.5, 0.8),
        Ltc = new CompositeLoadLtcParameters(0.9, 1.1, 0.00625, 0.9, 1.1, 0.5, 0.25),
    };

    private static CompositeLoadData PartAFull(int bus, double p, double q) => new(bus, p, q, 100.0, 0.08, 1.0, 0.02, 0.02, 0.04, 0.04, 0.5,
        0.3, 0.8, Motor(2.0), 0.95, Static, Static) { Extensions = PartAAll };

    [Fact]
    public void The_full_part_a_load_alone_gives_the_2_1_120_bits()
    {
        // Every part A piece (motors A to D with relays, contactors, thermal relay, stall and restart, the electronic load and
        // the tap changer) under a scripted dip to 0.55 pu, then 0.8 pu, then back: 1024 steps of 2^-8 s, the injection at
        // every step.
        CompositeLoadData d = PartAFull(4, 47.8, -3.9);
        Complex v0 = GridSim.Core.Numerics.DetMath.FromPolar(1.01, -0.2);
        DynamicLoadPlan plan = d.Plan(v0, 100.0, Hz);
        IRmsConverterModel m = plan.CreateModel();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            const double dt = 1.0 / 256.0;
            for (int k = 0; k < 1024; k++)
            {
                Complex v = k < 20 ? v0 : k < 200 ? v0 * 0.55 : k < 400 ? v0 * 0.8 : v0;
                Complex inj = m.Commit(v);
                m.Step(k * dt, v, new[] { v }, dt);
                w.Write(BitConverter.DoubleToInt64Bits(inj.Real));
                w.Write(BitConverter.DoubleToInt64Bits(inj.Imaginary));
            }
        }
        Pin("9A24371F00DED22FCBFC5A9706CF05F91C3CD7139322237CFD2985B99DB76A0C", Convert.ToHexString(SHA256.HashData(ms.ToArray())));
    }

    [Fact]
    public void Case14_with_the_full_part_a_load_gives_the_2_1_120_bits()
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Rc5Wp20BitIdentityTests.Case14();
        var loaded = new RmsLoadedMachineData(data, new DynamicLoadData[] { PartAFull(4, 47.8, -3.9) });
        MultiMachineSubtransient.Result run = RmsTransientStability.Study(model, pf, loaded, 4, 0.1, 0.1, stepSeconds: 1.0 / 256.0, durationSeconds: 1.5);
        Assert.True(run.ConverterIterationsConverged);
        Pin("A63A35E53FFD21F1482E6B369E21D19D970295FCDFE38BD2F988990FCF88AB11", Rc5Wp20BitIdentityTests.Digest(run));
    }
}
