using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP19: exciters EXDC2 and IEEEX1 (<see cref="Exdc2"/>), ESAC8B (<see cref="Esac8b"/>) and ESST3A
/// (<see cref="Esst3a"/>), the stabiliser PSS2C (<see cref="Pss2c"/>) and the governor IEEEG3 (<see cref="Ieeeg3"/>), and their
/// DYR records (extended library only). Verification:
/// <list type="number">
/// <item>Flat start: each model alone holds its initial point to a derived rounding bound, and every new exciter (with PSS2C)
/// and the governor in the RMS kernel on case9 stay with the held run within that bound for 2 s.</item>
/// <item>Closed forms: the EXDC2 ceiling at Vt VRMAX exactly; the ESAC8B two-lag response and integral ramp; the ESST3A
/// source voltage; the IEEEG3 water-hammer step response, exact at every sample.</item>
/// <item>Step responses against tests/tooling/dynamic-library-reference.py (SciPy 1.18.1, cases 15 to 19): the exact LTI
/// step response or solve_ivp (Radau, rtol 1e-12), pinned below 1 % of the peak change at 0.1 ms and falling with the
/// step.</item>
/// <item>Reductions: PSS2C with T12 = T13 = 0 and no output logic is PSS2B bit for bit; IEEEX1 is ESDC1A bit for bit.</item>
/// <item>DYR: each record maps value by value in its PSS/E order, a wrong count is refused, and the records without two
/// agreeing public sources are refused by name.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp19ControllerLibraryTests
{
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string Case9Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    private static readonly double Eps = Math.Pow(2, -53);

    /// <summary>
    /// The rounding bound of a run at rest (the WP3a derivation): a state updated by x &lt;- a x + (1 - a) b with b = x at rest
    /// gains at most two roundings of eps |x| per step, which the contraction (a close to 1) does not cancel, so after n steps
    /// it is within 2 eps n |x|. Summed over the states of a field or governor path (at most five states, each under 3 pu in
    /// these cases, so at most 12 pu together, as in WP3a) a held run of n steps stays within RestBound(n) = 2 eps n 12.
    /// 400 steps (2 s at 5 ms) give 1.07e-12.
    /// </summary>
    private static double RestBound(int steps) => 2.0 * Eps * steps * 12.0;

    private static FieldControlInputs At(double speed = 0.0, double ifd = double.NaN, double vt = double.NaN, double pe = double.NaN) => new(speed, ifd, vt, pe);

    // --------------------------------------------------------------------------------------------------------- model data

    private static Exdc2 Exdc2Typical() => Exdc2.Exdc2Model(0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279);
    private static Exdc2 Ieeex1Typical() => Exdc2.Ieeex1(0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279);
    private static Esac8b Esac8bTypical() => new(0.0, 80.0, 5.0, 10.0, 0.2, 1.0, 0.0, 35.0, 0.0, 0.8, 1.0, 6.5, 0.3, 9.0, 3.0);
    private static Esac8b Esac8bRectified() => new(0.0, 80.0, 5.0, 10.0, 0.2, 1.0, 0.0, 35.0, 0.0, 0.8, 1.0, 6.5, 0.3, 9.0, 3.0, kc: 0.2, kdExc: 0.1);
    private static Esst3a Esst3aTypical() => new(0.0, 0.2, -0.2, 7.93, 1.0, 6.67, 20.0, 0.0, 10.0, -10.0, 1.0, 6.15, 0.0, 6.9, 0.2, 0.081, 5.8, 20.0, 0.4, 1.0, 0.0);
    private static Pss2 Pss2bDual() => Pss2.Pss2b(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.0, 0.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1);
    private static Pss2c Pss2cDual(double t12 = 0.0, double t13 = 0.0, double on = double.NaN, double off = double.NaN)
        => new(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.0, 0.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1, t12, t13, on, off);
    private static Ieeeg3 Ieeeg3Typical() => new(0.2, 0.04, 0.16, -0.16, 2.0, 0.0, 0.05, 0.3, 5.0, 1.0, 0.5, 1.0, 1.5, 1.0);

    // The undisturbed point of the model-alone checks: |Vt| 1.02 at angle 0.3 rad, It 0.8 pu lagging it by 0.4 rad (machine base).
    private static readonly Complex Vt0 = Complex.FromPolarCoordinates(1.02, 0.3);
    private static readonly Complex It0 = Complex.FromPolarCoordinates(0.8, -0.1);

    // ------------------------------------------------------------------------------------------------------ 1. flat start

    public static TheoryData<string> ExciterNames => new() { "EXDC2", "IEEEX1", "ESAC8B", "ESAC8B KC KD", "ESST3A" };

    private static IRmsExciterModel ExciterOf(string name) => name switch
    {
        "EXDC2" => Exdc2Typical(),
        "IEEEX1" => Ieeex1Typical(),
        "ESAC8B" => Esac8bTypical(),
        "ESAC8B KC KD" => Esac8bRectified(),
        _ => Esst3aTypical(),
    };

    private static double StepAny(IRmsExciterModel m, double error, double efd0, double dt)
        => m is Esst3a st3
            ? st3.Step(error, 0.0, At(0.0, efd0, Vt0.Magnitude, 0.8), Vt0, It0, dt)
            : m.Step(error, 0.0, At(0.0, efd0, Vt0.Magnitude, 0.8), dt);

    [Theory]
    [MemberData(nameof(ExciterNames))]
    public void Each_new_exciter_alone_holds_its_initial_field_on_every_step(string name)
    {
        foreach (double efd0 in new[] { 1.2, 2.4 })
        {
            IRmsExciterModel m = ExciterOf(name);
            m.Initialize(efd0);
            double e0 = m.EquilibriumErrorPu(efd0);
            for (int n = 1; n <= 400; n++)
            {
                double efd = StepAny(m, e0, efd0, 0.005);
                Assert.True(Math.Abs(efd - efd0) <= RestBound(n), $"{name} at Efd0 {efd0}: step {n} drifted {efd - efd0:R}");
            }
        }
    }

    [Fact]
    public void Pss2c_and_ieeeg3_alone_hold_their_initial_point_on_every_step()
    {
        foreach (Pss2c s in new[] { Pss2cDual(), Pss2cDual(0.2, 0.04), Pss2cDual(0.2, 0.04, 0.3, 0.2) })
        {
            s.Initialize();
            for (int n = 1; n <= 400; n++)
            {
                double vs = s.Step(At(0.0, 1.5, 1.02, 0.8), 0.005);
                // Every washout starts holding its input, so each later state is exactly 0 and so is the output.
                Assert.Equal(0.0, vs);
            }
        }
        foreach (double pm0 in new[] { 0.3, 0.9 })
        {
            Ieeeg3 g = Ieeeg3Typical();
            g.Initialize(pm0);
            for (int n = 1; n <= 400; n++)
            {
                double pm = g.Step(0.0, 0.005);
                Assert.True(Math.Abs(pm - pm0) <= RestBound(n), $"IEEEG3 at Pm0 {pm0}: step {n} moved {pm - pm0:R}");
                Assert.Equal(pm0, g.GatePosition);   // err is exactly 0: sigma g0 - sigma g0, the dashpot output g0 - g0
            }
        }
    }

    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Case9Fixture, SchemasDir, model);
        return (model, pf, model.Generators.Select((g, k) => set.For(k)!.Data).ToList());
    }

    public static TheoryData<string> Fleets => new() { "EXDC2+PSS2C", "IEEEX1", "ESAC8B+PSS2C lead-lag", "ESAC8B KC KD", "ESST3A+PSS2C logic" };

    private static List<RmsExciter?> FleetOf(string name, int units) => Enumerable.Range(0, units).Select(_ => (RmsExciter?)(name switch
    {
        "EXDC2+PSS2C" => RmsExciter.FromModel(Exdc2Typical(), libraryStabiliser: Pss2cDual()),
        "IEEEX1" => RmsExciter.FromModel(Ieeex1Typical()),
        "ESAC8B+PSS2C lead-lag" => RmsExciter.FromModel(Esac8bTypical(), libraryStabiliser: Pss2cDual(0.2, 0.04)),
        "ESAC8B KC KD" => RmsExciter.FromModel(Esac8bRectified()),
        _ => RmsExciter.FromModel(Esst3aTypical(), libraryStabiliser: Pss2cDual(0.2, 0.04, 0.3, 0.2)),
    })).ToList();

    [Theory]
    [MemberData(nameof(Fleets))]
    public void A_new_exciter_fleet_holds_case9_at_rest_for_two_seconds_and_a_fault_moves_it(string fleet)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0);
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: FleetOf(fleet, model.Generators.Count));
        for (int i = 0; i < still.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace a = still.Machines[i], b = held.Machines[i];
            for (int k = 0; k < a.SpeedPu.Count; k++)
            {
                Assert.True(Math.Abs(a.SpeedPu[k] - b.SpeedPu[k]) <= RestBound(400), $"{fleet}: machine {i} speed {a.SpeedPu[k]:R} against {b.SpeedPu[k]:R} at sample {k}");
                Assert.True(Math.Abs(a.EqpPu[k] - b.EqpPu[k]) <= RestBound(400), $"{fleet}: machine {i} E'q moved {a.EqpPu[k] - b.EqpPu[k]:R} at sample {k}");
            }
        }
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: FleetOf(fleet, model.Generators.Count));
        MultiMachineSubtransient.Result heldFault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(fault.StayedInSynchronism);
        Assert.Contains(fault.Machines.Zip(heldFault.Machines), p => Math.Abs(p.First.EqpPu[^1] - p.Second.EqpPu[^1]) > 1e-4);
    }

    [Fact]
    public void An_ieeeg3_fleet_holds_case9_at_rest_and_answers_a_fault()
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        var gov = new Ieeeg3Data(0.2, 0.04, 0.16, -0.16, 2.0, 0.0, 0.05, 0.3, 5.0, 1.0, 0.5, 1.0, 1.5, 1.0);
        List<SubtransientInit.MachineData> governed = data.Select(d => d with { Governor = gov }).ToList();
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0);
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, governed, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0);
        for (int i = 0; i < still.Machines.Count; i++)
            for (int k = 0; k < still.Machines[i].SpeedPu.Count; k++)
                Assert.True(Math.Abs(still.Machines[i].SpeedPu[k] - held.Machines[i].SpeedPu[k]) <= RestBound(400),
                    $"machine {i} speed {still.Machines[i].SpeedPu[k]:R} against {held.Machines[i].SpeedPu[k]:R} at sample {k}");
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, governed, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0);
        MultiMachineSubtransient.Result heldFault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.True(fault.StayedInSynchronism);
        Assert.Contains(fault.Machines.Zip(heldFault.Machines), p => Math.Abs(p.First.SpeedPu[^1] - p.Second.SpeedPu[^1]) > 1e-7);
    }

    // ------------------------------------------------------------------------------------------- 2. EXDC2 limit scaling

    [Fact]
    public void Exdc2_ceiling_scales_with_the_terminal_voltage_and_ieeex1_does_not()
    {
        const double vrMax = 4.95, vt = 0.5;
        foreach (bool scaled in new[] { true, false })
        {
            Exdc2 m = scaled ? Exdc2Typical() : Ieeex1Typical();
            m.Initialize(1.0);   // VR0 = KE Efd0 + SE(1.0) Efd0 = 1.0 (1.0 is below the saturation knee A, so SE is 0 there)
            Assert.Equal(1.0, m.RegulatorOutput);
            double ceiling = scaled ? vt * vrMax : vrMax;   // 2.475 is 0.5 x 4.95 to the last bit
            bool reached = false;
            for (int n = 1; n <= 400; n++)
            {
                m.Step(1.0, 0.0, At(0.0, double.NaN, vt), 0.005);
                double vr = m.RegulatorOutput;
                Assert.True(vr <= ceiling, $"{m.Kind}: step {n} VR {vr:R} above {ceiling:R}");
                // KA 300 on an error of 1 pu drives the lag far past the ceiling (TA = 0.01 s, dt/TA = 0.5: one step already
                // targets above 100 pu), so from the first step VR sits on its ceiling, bit for bit, and stays there.
                if (vr == ceiling) reached = true;
                if (reached) Assert.Equal(ceiling, vr);
            }
            Assert.True(reached, $"{m.Kind} never reached its ceiling");
        }
        // A VR0 outside the scaled limits at the first step is refused: Efd0 3.0 needs VR0 above 3 (KE 1 plus saturation),
        // inside [-4.95, 4.95] but outside 0.5 x [-4.95, 4.95].
        Exdc2 low = Exdc2Typical();
        low.Initialize(3.0);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => low.Step(low.EquilibriumErrorPu(3.0), 0.0, At(0.0, double.NaN, vt), 0.005));
        Assert.Contains("EXDC2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ieeex1_is_esdc1a_bit_for_bit_on_every_step()
    {
        // The same data in the same order: the DC1A and type 1 diagrams are the same (Exdc2 is a separate implementation).
        var dc1a = new Esdc1a(0.0, 46.0, 0.06, 0.1, 0.5, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33);
        Exdc2 x1 = Exdc2.Ieeex1(0.0, 46.0, 0.06, 0.1, 0.5, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33);
        dc1a.Initialize(2.0); x1.Initialize(2.0);
        Assert.Equal(dc1a.EquilibriumErrorPu(2.0), x1.EquilibriumErrorPu(2.0));
        for (int n = 1; n <= 1000; n++)
        {
            double err = dc1a.EquilibriumErrorPu(2.0) + 0.05 * Math.Sin(n * 0.01) + (n > 500 ? 0.2 : 0.0);
            Assert.Equal(dc1a.Step(err, 0.001 * n % 0.003, At(), 0.005), x1.Step(err, 0.001 * n % 0.003, At(), 0.005));
            Assert.Equal(dc1a.RegulatorOutput, x1.RegulatorOutput);
        }
    }

    // --------------------------------------------------------------------------------------------- 3 and 4. ESAC8B

    // tests/tooling/dynamic-library-reference.py case 15: the closed form, and the discrete chain at 1 ms by scipy.signal.dlsim,
    // at 0.05, 0.1, 0.2, 0.5, 1 and 2 s.
    private static readonly double[] Esac8bTimes = { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0 };
    private static readonly double[] Esac8bClosed = { 0.0020585892383209073, 0.006855641894538345, 0.01959337632646041, 0.05418351852854685, 0.08308422459366749, 0.09771054519043708 };
    private static readonly double[] Esac8bChain = { 0.00209580306857389, 0.006911885689633428, 0.019660115787083536, 0.05422857100518839, 0.08310110203479915, 0.09771283006995016 };

    [Fact]
    public void Esac8b_two_lag_response_matches_the_scipy_chain_and_the_closed_form_bound_on_every_step()
    {
        // KP 10, KI = KD = 0, KA 1, TA 0.1, KE 1, TE 0.5, KC 0, SE 0. From Efd0 = 1 (VR0 = 1, error VR0/(KA KP) = 0.1) the
        // error steps by 0.01, so u steps by 0.1.
        const double dt = 1e-3, ta = 0.1, te = 0.5;
        var m = new Esac8b(0.0, 10.0, 0.0, 0.0, 0.0, 1.0, ta, 50.0, -50.0, te, 1.0, 0.0, 0.0, 0.0, 0.0);
        m.Initialize(1.0);
        double e0 = m.EquilibriumErrorPu(1.0);
        Assert.Equal(0.1, e0, 15);
        // Bound against the continuous closed form, derived: the regulator lag's samples are exact (exact hold of a held u);
        // the exciter is driven over each step by the step's END value VR(t_n+1) instead of VR(t), which exceeds it by at most
        // VR(t_n+1) - VR(t_n) <= (dt / TA) KA du = 1e-3 x 1 x 0.1 / 0.1 = 1e-3. The exciter lag has unity DC gain and is a
        // contraction, so its output error is at most that input error: 1e-3, which is 1 % of the final change 0.1. A few
        // roundings are added (100 eps).
        double bound = dt / ta * 1.0 * 0.1 + 100 * Eps;
        int next = 0;
        for (int n = 1; n <= 2000; n++)
        {
            double d = m.Step(e0 + 0.01, 0.0, At(), dt) - 1.0;
            double t = n * dt;
            double closed = 0.1 * (1.0 - (ta * Math.Exp(-t / ta) - te * Math.Exp(-t / te)) / (ta - te));
            Assert.True(Math.Abs(d - closed) <= bound, $"step {n}: dEfd {d:R} against the closed form {closed:R}");
            if (next < Esac8bTimes.Length && n == (int)Math.Round(Esac8bTimes[next] / dt))
            {
                Assert.True(Math.Abs(closed - Esac8bClosed[next]) <= 1e-15, $"closed form at {t}: {closed:R} against SciPy's {Esac8bClosed[next]:R}");
                // The same zero-order-hold chain: the only differences are the exponentials (1 ulp each) and the order of the
                // roundings, a few eps of the 0.1 change per step over 2000 steps: well under 1e-12.
                Assert.True(Math.Abs(d - Esac8bChain[next]) <= 1e-12, $"at {t}: dEfd {d:R} against the dlsim chain {Esac8bChain[next]:R}");
                next++;
            }
        }
        Assert.Equal(Esac8bTimes.Length, next);
        Assert.Equal(0.05418351852854685, Esac8bClosed[3]);   // the spec's figure at 0.5 s
    }

    [Fact]
    public void Esac8b_integral_path_is_an_exact_ramp_on_every_step()
    {
        // KP = KD = 0, KI = 2, error 0.01 from rest (Efd0 = 0, so VR0 = 0 and the integral starts at 0): u(t) = 0.02 t. Each
        // step adds KI e dt = 1e-4 (one rounding of 2^-53 x 1e-4 in the product) to the integral (one rounding of at most
        // eps |u| <= eps 0.02 t in the sum), so after n steps |u - 0.02 t| <= n eps (1e-4 + 0.01).
        var m = new Esac8b(0.0, 0.0, 2.0, 0.0, 0.0, 1.0, 0.0, 50.0, -50.0, 0.8, 1.0, 0.0, 0.0, 0.0, 0.0);
        m.Initialize(0.0);
        Assert.Equal(0.0, m.EquilibriumErrorPu(0.0));
        for (int n = 1; n <= 100; n++)
        {
            m.Step(0.01, 0.0, At(), 0.005);
            Assert.True(Math.Abs(m.PidOutput - 0.02 * n * 0.005) <= n * Eps * (1e-4 + 0.01), $"step {n}: u {m.PidOutput:R}");
        }
        Assert.True(Math.Abs(m.PidOutput - 0.01) <= 1e-15, $"u(0.5) = {m.PidOutput:R}");
    }

    // ---------------------------------------------------------------------------------------------- 5. ESST3A source

    [Fact]
    public void Esst3a_source_voltage_is_the_hand_value()
    {
        // KP 1, KI 0.5, XL 0.1, THETAP 0, VT = 1 at 0, IT = 0.8 at -30 degrees:
        // VE = |1 + j (0.5 + 0.1) 0.8 at -30| = |1 + 0.48 at 60| = sqrt((1 + 0.24)^2 + (0.48 sin 60)^2) = sqrt(1.5376 + 0.1728).
        var m = new Esst3a(0.0, 1.0, -1.0, 1.0, 0.0, 0.0, 1.0, 0.0, 10.0, -10.0, 0.0, 1.0, 0.5, 10.0, 0.0, 0.1, 10.0, 0.0, 0.1, 10.0, 0.0);
        double ve = m.SourceVoltage(Complex.One, Complex.FromPolarCoordinates(0.8, -Math.PI / 6.0));
        Assert.True(Math.Abs(ve - 1.3078226179417451) <= 1e-14, $"VE {ve:R}");
        Assert.True(Math.Abs(1.3078226179417451 - Math.Sqrt(1.5376 + 0.1728)) <= 4e-16);
        // THETAP rotates KP: with THETAP 90 degrees and KI = XL = 0, VE = |j VT| = 1.
        var r = new Esst3a(0.0, 1.0, -1.0, 1.0, 0.0, 0.0, 1.0, 0.0, 10.0, -10.0, 0.0, 1.0, 0.0, 10.0, 0.0, 0.0, 10.0, 90.0, 0.1, 10.0, 0.0);
        Assert.True(Math.Abs(r.SourceVoltage(Complex.One, Complex.Zero) - 1.0) <= 2e-16);
    }

    // ------------------------------------------------------------------------------------------------- 6. IEEEG3 turbine

    [Fact]
    public void Ieeeg3_water_column_is_exact_at_every_sample()
    {
        // Classical a-values (0.5, 1, 1.5, 1), TW 1 s, a unit gate step from rest: Pm(t) = 1 - 3 e^(-2t/TW), Pm(0+) = -2. The turbine
        // is -2 g plus the lag 3/(1 + 0.5 s) of g, whose exact hold is exact at the samples; the remaining error is rounding:
        // the lag state (at most 3) gains two roundings per step (2 eps n 3) plus the exponentials here and in the code
        // (1 ulp of 3 each per step).
        Ieeeg3 g = Ieeeg3Typical();
        g.InitializeTurbine(0.0);
        const double dt = 0.005;
        for (int n = 1; n <= 400; n++)
        {
            double pm = g.TurbineStep(1.0, dt);
            double want = 1.0 - 3.0 * Math.Exp(-2.0 * n * dt / 1.0);
            Assert.True(Math.Abs(pm - want) <= 4.0 * Eps * n * 3.0, $"step {n}: Pm {pm:R} against {want:R}");
            if (n == 200) Assert.True(Math.Abs(pm - 0.5939941502901619) <= 1e-12, $"Pm(1 s) {pm:R}");
        }
        // Pm(0+) = -2: the feedthrough alone, before the lag moves (a step of 1e-12 s).
        Ieeeg3 h = Ieeeg3Typical();
        h.InitializeTurbine(0.0);
        Assert.True(Math.Abs(h.TurbineStep(1.0, 1e-12) + 2.0) <= 1e-11);
    }

    // ------------------------------------------------------------------------------------------------- 7. PSS2C = PSS2B

    [Fact]
    public void Pss2c_without_its_extra_stage_and_logic_is_pss2b_bit_for_bit()
    {
        Pss2 b = Pss2bDual();
        Pss2c c = Pss2cDual();
        b.Initialize(); c.Initialize();
        for (int n = 0; n < 400; n++)
        {
            // Inputs that drive both limits: speed and power swings large enough to clip the input and output limits.
            var x = At(0.01 * Math.Sin(n * 0.07), 1.5, 1.02, 0.8 + 3.0 * Math.Sin(n * 0.03));
            Assert.Equal(b.Step(x, 0.005), c.Step(x, 0.005));
        }
        Assert.Contains("PSS2C", c.Kind, StringComparison.Ordinal);
    }

    [Fact]
    public void Pss2c_output_logic_switches_with_the_power_on_every_step()
    {
        // PPSSon 0.5, PPSSoff 0.2: on above 0.5, off below 0.2, unchanged between. The speed input moves so the chain is busy.
        Pss2c c = Pss2cDual(0.2, 0.04, 0.5, 0.2);
        Pss2c free = Pss2cDual(0.2, 0.04);
        c.Initialize(); free.Initialize();
        double[] pe = { 0.6, 0.6, 0.4, 0.3, 0.19, 0.1, 0.3, 0.45, 0.51, 0.6, 0.3, 0.21, 0.2, 0.19 };
        bool[] on = { true, true, true, true, false, false, false, false, true, true, true, true, true, false };
        for (int n = 0; n < pe.Length; n++)
        {
            // The same power on both so the chains match; only the output logic differs.
            var x = At(0.002 * (n + 1), 1.5, 1.02, pe[n]);
            double want = free.Step(x, 0.005);
            double got = c.Step(x, 0.005);
            Assert.Equal(on[n], c.IsOn);
            Assert.Equal(on[n] ? want : 0.0, got);
        }
        Assert.Throws<ArgumentException>(() => Pss2cDual(0.2, 0.04, 0.2, 0.5));
        Assert.Throws<ArgumentException>(() => Pss2cDual(0.2, 0.04, 0.5, double.NaN));
    }

    // ------------------------------------------------------------------------------------- step responses against SciPy

    private static double WorstError(Func<double, int, double[]> run, double[] times, double[] reference, double dt, double offset)
    {
        int steps = (int)Math.Round(times[^1] / dt);
        double[] y = run(dt, steps);
        double worst = 0;
        for (int i = 0; i < times.Length; i++)
            worst = Math.Max(worst, Math.Abs(y[(int)Math.Round(times[i] / dt) - 1] - offset - reference[i]));
        return worst;
    }

    // The 1 % bound, as derived in Rc5Wp3aDynamicLibraryTests: a lag of time constant tau stepped with the exact hold of a
    // held input carries a local error of (dt/tau)^2/2 of its change, damped at dt/tau per step, so at most dt/(2 tau) of its
    // change accumulates. The fastest time constant in these cases is 0.02 s (PSS2C T6), so at 0.1 ms the bound is 0.25 %;
    // 1 % of the peak change allows four times amplification through the gains and lead-lags. A first-order error falls
    // with the step, so halving the step must cut the error below 0.6 of it.
    private static void AgreesAndConverges(string what, Func<double, int, double[]> run, double[] times, double[] reference, double offset = 0.0)
    {
        double peak = reference.Max(Math.Abs);
        double e1 = WorstError(run, times, reference, 1e-4, offset), e2 = WorstError(run, times, reference, 5e-5, offset);
        Assert.True(e1 <= 1e-2 * peak, $"{what}: worst error {e1:R} at 0.1 ms against a peak of {peak:R}");
        Assert.True(e2 <= 0.6 * e1, $"{what}: the error must fall with the step: {e1:R} at 0.1 ms, {e2:R} at 0.05 ms");
    }

    private static readonly double[] Times = { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };

    [Fact]
    public void Esac8b_pid_follows_the_exact_lti_step_response()
    {
        // Case 16: KP 5, KI 2, KD 0.5, TD 0.05, KA 2, TA 0.05, KE 1, TE 0.8, error step 0.002 from rest (Efd0 = 0).
        double[] reference = { 0.001097627430352245, 0.0027782212432631978, 0.00549392000369853, 0.010956381793830652, 0.017858717278186036 };
        AgreesAndConverges("ESAC8B PID", (dt, steps) =>
        {
            var m = new Esac8b(0.0, 5.0, 2.0, 0.5, 0.05, 2.0, 0.05, 50.0, -50.0, 0.8, 1.0, 0.0, 0.0, 0.0, 0.0);
            m.Initialize(0.0);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = m.Step(0.002, 0.0, At(), dt);
            return y;
        }, Times[..5], reference);
    }

    [Fact]
    public void Esst3a_follows_the_scipy_integration()
    {
        // Case 18: KC 0 and fixed terminal phasors, so VB = VE is held: KP 5, KI = XL = THETAP = 0, |VT| 1 gives VB 5. KA 20,
        // TA 0.02, TC 1, TB 5, KM 8, TM 0.4, KG 1; Efd0 2; the error steps by 0.001 after the first (initialising) step.
        double[] reference = { 2.003371921554844, 2.00406966933983, 2.0044244955411634, 2.005303455286133, 2.006655595655421, 2.008986101758544, 2.013735352601583 };
        AgreesAndConverges("ESST3A", (dt, steps) =>
        {
            var m = new Esst3a(0.0, 1.0, -1.0, 8.0, 1.0, 5.0, 20.0, 0.02, 10.0, -10.0, 1.0, 5.0, 0.0, 10.0, 0.0, 0.0, 10.0, 0.0, 0.4, 1.0, 0.0);
            m.Initialize(2.0);
            var x = At(0.0, 2.0, 1.0, 0.8);
            m.Step(0.0, 0.0, x, Complex.One, new Complex(0.8, 0.0), 1e-12);   // sets the states at the operating point
            Assert.Equal(5.0, m.LastVb);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = m.Step(0.001, 0.0, x, Complex.One, new Complex(0.8, 0.0), dt);
            return y;
        }, Times, reference.Select(v => v - 2.0).ToArray(), offset: 2.0);
    }

    [Fact]
    public void Pss2c_with_its_fourth_stage_follows_the_exact_lti_step_response()
    {
        // Case 19: speed step 0.001 pu on input 1, power held on input 2 (M = N = 0); T10/T11 0.1/0.05, T12/T13 0.2/0.04.
        double[] reference = { -0.11521505409743801, -0.14627896869252072, 0.024261592496252607, 0.003068147401507887, 0.001260388136027045, -0.0007017064329896733, -0.0011380175934763198 };
        AgreesAndConverges("PSS2C", (dt, steps) =>
        {
            var s = new Pss2c(1, 3, 0, 0, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, 0.05,
                10.0, -10.0, 10.0, -10.0, 10.0, -10.0, 0.2, 0.04);
            s.Initialize();
            s.Step(At(0.0, 1.5, 1.02, 0.8), dt);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = s.Step(At(0.001, 1.5, 1.02, 0.8), dt);
            return y;
        }, Times, reference);
    }

    [Fact]
    public void Ieeeg3_closed_loop_follows_the_scipy_integration()
    {
        // Case 17: speed step +0.001 pu from g0 = 0.5; TG 0.2, TP 0.04, sigma 0.05, delta 0.3, TR 5, TW 1, classical a-values.
        double[] times = { 0.5, 1.0, 2.0, 5.0, 10.0 };
        double[] reference = { 0.5014025617748683, 0.500107088250345, 0.4979727102593019, 0.4961059554118946, 0.49414061077174054 };
        AgreesAndConverges("IEEEG3", (dt, steps) =>
        {
            Ieeeg3 g = Ieeeg3Typical();
            g.Initialize(0.5);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = g.Step(0.001, dt);
            return y;
        }, times, reference.Select(v => v - 0.5).ToArray(), offset: 0.5);
    }

    // -------------------------------------------------------------------------------------------------------- 8. DYR

    private static PsseParseResult Raw14() => PsseRawParser.ParseWithDiagnostics(Rc5Wp19BitIdentityTests.Case14Raw(), "case14");

    private const string G = Rc5Wp19BitIdentityTests.Genrou;

    private const string MappedDyr =
          "     1 'GENROU' 1 " + G + " /\n"
        + "     1 'ESAC8B' 1  0.0  80.0  5.0  10.0  0.2  1.0  0.0  35.0 -1.0  0.8  1.0  6.5  0.3  9.0  3.0 /\n"
        + "     2 'GENROU' 1 " + G + " /\n"
        + "     2 'IEEEX1' 1  0.02  310.0  0.02  0.1  0.5  4.9 -4.8  1.1  1.3  0.07  0.7  0  2.3  0.12  3.1  0.28 /\n"
        + "     2 'IEEEG3' 1  0.2  0.04  0.16 -0.16  2.0  0.0  0.05  0.3  5.0  1.0  0.5  1.0  1.5  1.0 /\n"
        + "     3 'GENROU' 1 " + G + " /\n"
        + "     3 'EXDC2' 1  0.0  300.0  0.01  0.0  0.0  4.95 -4.95  1.0  1.33  0.0675  0.6  0  2.29  0.117  3.05  0.279 /\n"
        + "     6 'GENROU' 1 " + G + " /\n"
        + "     6 'ESST3A' 1  0.0  0.2 -0.2  7.93  1.0  6.67  20.0  0.0  10.0 -10.0  1.0  6.15  0.0  6.9  0.2  0.081  5.8  20.0  0.4  1.0  0.0 /\n"
        + "     8 'GENROU' 1 " + G + " /\n";

    private static PsseDyrResult ReadExtended(string dyr, bool allow = false)
        => PsseDyrReader.Read(dyr, Raw14(), "case14", new PsseDyrOptions { SourceName = "wp19.dyr", ExtendedLibrary = true, AllowUnmodelled = allow });

    private static MachineDynamicsSet LoadJson(string json, GridModel model)
    {
        string dir = TestTemp.Under("gridsim-wp19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "wp19.dynamics.json");
        File.WriteAllText(path, json);
        try { return MachineDynamicsLoader.Load(path, SchemasDir, model); }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void Each_new_dyr_record_maps_value_by_value_in_its_psse_order()
    {
        PsseParseResult raw = Raw14();
        PsseDyrResult r = ReadExtended(MappedDyr);
        Assert.Equal(5, r.Machines);
        MachineDynamicsSet set = LoadJson(r.Json, raw.Model);
        MachineDynamicsEntry U(int bus) => set.Entries.Single(e => raw.Model.Generators[e.GeneratorIndex].Bus == bus);
        string[] dc = { "dc1aTrS", "dc1aKa", "dc1aTaS", "dc1aTbS", "dc1aTcS", "dc1aVrMaxPu", "dc1aVrMinPu", "dc1aKe", "dc1aTeS", "dc1aKf", "dc1aTf1S",
            "dc1aSatE1", "dc1aSatSe1", "dc1aSatE2", "dc1aSatSe2" };

        Assert.Equal("EXDC2", U(3).Exciter!.Model);
        Assert.Equal(new[] { 0.0, 300.0, 0.01, 0.0, 0.0, 4.95, -4.95, 1.0, 1.33, 0.0675, 0.6, 2.29, 0.117, 3.05, 0.279 }, dc.Select(k => U(3).Exciter!.Params[k]));
        Assert.Equal("EXDC2", U(3).Exciter!.Create().Kind);
        Assert.Null(U(8).Exciter);

        Assert.Equal("IEEEX1", U(2).Exciter!.Model);
        Assert.Equal(new[] { 0.02, 310.0, 0.02, 0.1, 0.5, 4.9, -4.8, 1.1, 1.3, 0.07, 0.7, 2.3, 0.12, 3.1, 0.28 }, dc.Select(k => U(2).Exciter!.Params[k]));
        var gov = Assert.IsType<Ieeeg3Data>(U(2).Data.Governor);
        Assert.Equal(new Ieeeg3Data(0.2, 0.04, 0.16, -0.16, 2.0, 0.0, 0.05, 0.3, 5.0, 1.0, 0.5, 1.0, 1.5, 1.0), gov);

        Assert.Equal("ESAC8B", U(1).Exciter!.Model);
        Assert.Equal(new[] { 0.0, 80.0, 5.0, 10.0, 0.2, 1.0, 0.0, 35.0, -1.0, 0.8, 1.0, 6.5, 0.3, 9.0, 3.0 },
            new[] { "ac8bTrS", "ac8bKp", "ac8bKi", "ac8bKd", "ac8bTdS", "ac8bKa", "ac8bTaS", "ac8bVrMaxPu", "ac8bVrMinPu", "ac8bTeS", "ac8bKe",
                "ac8bSatE1", "ac8bSatSe1", "ac8bSatE2", "ac8bSatSe2" }.Select(k => U(1).Exciter!.Params[k]));

        Assert.Equal("ESST3A", U(6).Exciter!.Model);
        Assert.Equal(new[] { 0.0, 0.2, -0.2, 7.93, 1.0, 6.67, 20.0, 0.0, 10.0, -10.0, 1.0, 6.15, 0.0, 6.9, 0.2, 0.081, 5.8, 20.0, 0.4, 1.0, 0.0 },
            new[] { "st3aTrS", "st3aViMaxPu", "st3aViMinPu", "st3aKm", "st3aTcS", "st3aTbS", "st3aKa", "st3aTaS", "st3aVrMaxPu", "st3aVrMinPu",
                "st3aKg", "st3aKp", "st3aKi", "st3aVbMaxPu", "st3aKc", "st3aXlPu", "st3aVgMaxPu", "st3aThetapDeg", "st3aTmS", "st3aVmMaxPu", "st3aVmMinPu" }
                .Select(k => U(6).Exciter!.Params[k]));
        Assert.Equal("ESST3A", U(6).Exciter!.Create().Kind);
    }

    [Fact]
    public void A_wrong_value_count_and_psse_computed_zeros_are_refused()
    {
        void Refused(string dyr, string what)
        {
            var ex = Assert.Throws<InvalidDataException>(() => ReadExtended("     1 'GENROU' 1 " + G + " /\n" + dyr));
            Assert.Contains(what, ex.Message, StringComparison.Ordinal);
        }
        Refused("     1 'EXDC2' 1  0.0  300.0  0.01 /\n", "EXDC2 needs exactly 16 values after the machine ID; the record has 3.");
        Refused("     1 'IEEEX1' 1  0.0  300.0  0.01 /\n", "IEEEX1 needs exactly 16 values");
        Refused("     1 'ESAC8B' 1  0.0  80.0 /\n", "ESAC8B needs exactly 15 values");
        Refused("     1 'ESST3A' 1  0.0  0.2 /\n", "ESST3A needs exactly 21 values");
        Refused("     1 'IEEEG3' 1  0.2  0.04 /\n", "IEEEG3 needs exactly 14 values");
        Refused("     1 'EXDC2' 1  0.0  300.0  0.01  0.0  0.0  4.95 -4.95  0.0  1.33  0.0675  0.6  0  2.29  0.117  3.05  0.279 /\n", "KE = 0");
        Refused("     1 'IEEEX1' 1  0.0  300.0  0.01  0.0  0.0  0.0 -4.95  1.0  1.33  0.0675  0.6  0  2.29  0.117  3.05  0.279 /\n", "VRMAX = 0");
        Refused("     1 'EXDC2' 1  0.0  300.0  0.01  0.0  0.0  4.95 -4.95  1.0  1.33  0.0675  0.6  1  2.29  0.117  3.05  0.279 /\n", "SWITCH not 0");
        Refused("     1 'ESAC8B' 1  0.0  80.0  5.0  10.0  0.2  1.0  0.0  0.0  0.0  0.8  1.0  6.5  0.3  9.0  3.0 /\n", "VRMAX = 0");
        Refused("     1 'ESAC8B' 1  0.0  80.0  5.0  10.0  0.2  1.0  0.0  35.0  0.0  0.8  0.0  6.5  0.3  9.0  3.0 /\n", "KE = 0 (refused: PSS/E's handling of KE = 0 for ESAC8B is not confirmed)");
    }

    [Fact]
    public void The_records_without_two_public_sources_are_refused_by_name_with_the_reason()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ReadExtended(Rc5Wp19BitIdentityTests.Wp19Dyr));
        foreach ((string model, string line, string at) in new[] { ("PSS2C", "3", "machine '1' at bus 1"), ("HYGOV4", "9", "machine '1' at bus 3"),
            ("ESAC7B", "13", "machine '1' at bus 8"), ("GENQEC", "14", "machine '2' at bus 8") })
            Assert.Contains($"{model} for {at} (line {line}): {PsseDyrReader.RefusedLibraryModels[model]}", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            "GridSim models PSS2C (a gridsim-dynamics/2 stabiliser template with model PSS2C), but its PSS/E record order is given by one public source only (the PowerWorld PSS2C page; the public PSS/E 33 model library predates the model), so it is not read with an unconfirmed order; write the stabiliser as a gridsim-dynamics/2 template",
            PsseDyrReader.RefusedLibraryModels["PSS2C"]);
        Assert.Equal("GridSim does not model GENQEC: its record order and saturation function are not public, so it is not read",
            PsseDyrReader.RefusedLibraryModels["GENQEC"]);
        Assert.StartsWith("GridSim does not model ESAC7B: its PSS/E record order is given by one public source only", PsseDyrReader.RefusedLibraryModels["ESAC7B"], StringComparison.Ordinal);
        Assert.StartsWith("GridSim does not model HYGOV4: its parameter list is given by one public source only", PsseDyrReader.RefusedLibraryModels["HYGOV4"], StringComparison.Ordinal);
        Assert.Equal(4, PsseDyrReader.RefusedLibraryModels.Count);
        // GENTPJ stays refused (WP3a's reason) and is not in the new list.
        Assert.False(PsseDyrReader.RefusedLibraryModels.ContainsKey("GENTPJ"));
        // The refused models are not in the mapped lists, so the default reader still calls them unmapped.
        foreach (string m in PsseDyrReader.RefusedLibraryModels.Keys)
        {
            Assert.False(PsseDyrReader.MappedModels.ContainsKey(m));
            Assert.False(PsseDyrReader.ExtendedModels.ContainsKey(m));
        }
        foreach (string m in new[] { "EXDC2", "IEEEX1", "ESAC8B", "ESST3A", "IEEEG3" })
        {
            Assert.False(PsseDyrReader.MappedModels.ContainsKey(m));
            Assert.True(PsseDyrReader.ExtendedModels.ContainsKey(m));
        }
        // With unmodelled dynamics allowed, the refused records are left out and named in the notes.
        PsseDyrResult allowed = ReadExtended(Rc5Wp19BitIdentityTests.Wp19Dyr, allow: true);
        Assert.Contains(allowed.Notes, n => n.Contains("PSS2C for machine '1' at bus 1 (line 3)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pss2c_template_loads_through_the_file_route_and_runs_as_pss2b_when_its_stage_is_bypassed()
    {
        // The WP3a case14 file's bus-6 PSS2B, as a dynamics document, then the same template as PSS2C with T12 = T13 = 0.
        PsseParseResult raw = Raw14();
        PsseDyrResult r = PsseDyrReader.Read(Rc5Wp19BitIdentityTests.Wp3aDyr, raw, "case14", new PsseDyrOptions { SourceName = "wp3a.dyr", ExtendedLibrary = true });
        const string head = "\"model\": \"PSS2B\",";
        Assert.Contains(head, r.Json, StringComparison.Ordinal);
        string json = r.Json.Replace(head, "\"model\": \"PSS2C\", \"pss2cT12S\": 0.0, \"pss2cT13S\": 0.0,", StringComparison.Ordinal);
        MachineDynamicsSet b = LoadJson(r.Json, raw.Model), c = LoadJson(json, raw.Model);
        MachineDynamicsEntry eb = b.Entries.Single(e => e.Exciter?.PssModel == "PSS2B"), ec = c.Entries.Single(e => e.Exciter?.PssModel == "PSS2C");
        IRmsStabiliser sb = eb.Exciter!.Create().LibraryStabiliser!, sc = ec.Exciter!.Create().LibraryStabiliser!;
        Assert.Equal("PSS2C", sc.Kind);
        sb.Initialize(); sc.Initialize();
        for (int n = 0; n < 200; n++)
        {
            var x = At(0.01 * Math.Sin(n * 0.05), 1.5, 1.02, 0.8 + 0.5 * Math.Sin(n * 0.02));
            Assert.Equal(sb.Step(x, 0.005), sc.Step(x, 0.005));
        }
        // A PSS2C template without T12 is refused by the loader, naming the parameter.
        string missing = r.Json.Replace(head, "\"model\": \"PSS2C\", \"pss2cT13S\": 0.0,", StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => LoadJson(missing, raw.Model));
        Assert.Contains("'pss2cT12S' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mapped_case14_fleet_starts_at_rest_and_rides_through_a_fault()
    {
        PsseParseResult raw = Raw14();
        MachineDynamicsSet set = LoadJson(ReadExtended(MappedDyr).Json, raw.Model);
        GridModel m = raw.Model;
        // Solved to 1e-13 (as WP3a): a 1e-8 pu mismatch is a real departure from equilibrium that the AVRs answer.
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        List<SubtransientInit.MachineData> data = m.Generators.Select((_, k) => set.For(k)?.Data ?? throw new InvalidOperationException($"unit {k} unbound")).ToList();
        List<RmsExciter?> Ex() => m.Generators.Select((_, k) => set.For(k)!.Exciter?.Create()).ToList();
        List<SubtransientInit.MachineData> heldData = data.Select(d => d with { Governor = null }).ToList();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: Ex());
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(m, pf, heldData, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 2.0);
        for (int i = 0; i < still.Machines.Count; i++)
            for (int k = 0; k < still.Machines[i].SpeedPu.Count; k++)
            {
                Assert.True(Math.Abs(still.Machines[i].SpeedPu[k] - held.Machines[i].SpeedPu[k]) <= RestBound(400), $"machine {i} speed at sample {k}");
                Assert.True(Math.Abs(still.Machines[i].EqpPu[k] - held.Machines[i].EqpPu[k]) <= RestBound(400), $"machine {i} E'q at sample {k}");
            }
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 0.1, clearingSeconds: 0.05,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: Ex());
        Assert.True(fault.StayedInSynchronism);
        Assert.Contains(fault.Machines, t => Math.Abs(t.SpeedPu[^1]) > 1e-6);
    }
}
