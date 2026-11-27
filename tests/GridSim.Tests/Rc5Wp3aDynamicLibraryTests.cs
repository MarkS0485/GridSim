using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP3a: the extended standard dynamic-model library. Machines GENCLS (<see cref="Gencls"/>) and GENTPJ (the Kis term
/// of <see cref="SubtransientMachine.SaturationCurve.GentpjIncrements"/>); exciters ESST1A and EXST1 (<see cref="St1Exciter"/>),
/// SCRX (<see cref="Scrx"/>), ESDC1A (<see cref="Esdc1a"/>) and the ESAC1A DYR record; stabilisers IEEEST (<see cref="Ieeest"/>),
/// PSS2A and PSS2B (<see cref="Pss2"/>). Verification:
/// <list type="number">
/// <item>Flat start: each model alone, and every model in the RMS kernel on case9 and on a case14 DYR fleet, stays at its
/// initial point to rounding (the held-field run is matched within a derived rounding bound and the flux states of GENCLS are held bit for bit).</item>
/// <item>Step responses against an independent continuous-time integration: tests/tooling/dynamic-library-reference.py
/// (SciPy 1.18.1: the exact LTI step response by the matrix exponential, and solve_ivp Radau with rtol 1e-12 for the loops
/// with rate feedback). GridSim's blocks are exact zero-order holds of a held input, so a chain of them converges on the
/// continuous response at first order in the step: the error is pinned below 1 % of the peak at 0.1 ms and is shown to
/// halve with the step. Analytical values close the linear regions exactly: a lead-lag alone (the closed form), final
/// values, and each limit engaging at exactly its level (non-windup: the state leaves the limit on the first reversed
/// step).</item>
/// <item>GENTPJ with Kis = 0 is GENROU bit for bit: the increments for the same saturation inputs, the loader's machine
/// data, and a saturated case9 run.</item>
/// <item>DYR: every record maps value by value in its PSS/E order and re-reads to the same document; the extended library
/// needs <see cref="PsseDyrOptions.ExtendedLibrary"/>, so the default reader is unchanged.</item>
/// <item>Determinism: dense and sparse power flows, repeated and parallel runs, and the three SIMD modes of the built CLI
/// give the same bits.</item>
/// </list>
/// </summary>
public sealed class Rc5Wp3aDynamicLibraryTests
{
    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string Case9Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    private static string PsseFixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    /// <summary>
    /// The rounding bound of a run at rest, derived rather than fitted. A state updated by x <- a x + (1 - a) b with b = x
    /// at rest gains at most two roundings of 2^-53 |x| per step (the products and the sum), and with a close to 1 over a
    /// run of n steps the contraction does not cancel them, so its drift is at most 2 eps n |x|. Summed over the states of
    /// the field path (at most four exciter states and E'q, each under 3 pu, so at most 12 pu together) a held-input run of
    /// n steps stays within RestBound(n) = 2 eps n 12 of the run without the exciter. 1000 steps of 5 ms give 2.7e-12.
    /// </summary>
    private static double RestBound(int steps) => 2.0 * Math.Pow(2, -53) * steps * 12.0;

    private static FieldControlInputs At(double speed = 0.0, double ifd = double.NaN, double vt = double.NaN, double pe = double.NaN) => new(speed, ifd, vt, pe);

    // ----------------------------------------------------------------------------------------------------- model data

    private static St1Exciter Esst1a() => St1Exciter.Esst1a(0.02, 0.1, -0.1, 1.0, 10.0, 0.0, 0.0, 210.0, 0.02, 6.43, -6.0, 6.43, -6.0, 0.038, 0.0, 1.0, 4.54, 4.4, 1);
    private static St1Exciter Exst1() => St1Exciter.Exst1(0.0, 0.2, -0.2, 1.0, 10.0, 200.0, 0.02, 7.0, -6.4, 0.05, 0.02, 1.0);
    private static Scrx ScrxBus() => new(1.0, 10.0, 200.0, 0.05, 0.0, 5.0, busFed: true, crowbarRatio: 10.0);
    private static Scrx ScrxSolid() => new(1.0, 10.0, 200.0, 0.05, -5.0, 5.0, busFed: false, crowbarRatio: 0.0);
    private static Esdc1a Esdc1a() => new(0.0, 46.0, 0.06, 0.0, 0.0, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33);
    private static AcExciter Esac1a() => new(AcExciterVariant.Esac1a, 0.0, 0.0, 400.0, 0.02, -14.5, 14.5, -5.43, 6.03, 0.8, 0.03, 1.0, 0.2, 0.38, 1.0, 3.14, 0.03, 4.18, 0.1);
    // KS -0.1: with KS -1 on the ESAC1A case9 fleet the closed loop at rest is small-signal unstable (a 2e-16 rounding
    // grows to 2e-8 in 7 s and settles into a limit cycle), so that setting has no equilibrium to hold.
    private static Ieeest IeeestPower() => new(3, 0.05, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, -0.1, 0.1, -0.1, 1.2, 0.0);
    private static Ieeest IeeestSpeed() => new(1, 0.0, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 2.0, 2.0, 10.0, 0.1, -0.1, 0.0, 0.0);
    private static Pss2 Pss2aDual() => Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, -0.1);
    private static Pss2 Pss2bDual() => Pss2.Pss2b(1, 3, 5, 1, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.0, 0.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1);

    /// <summary>Each exciter model at rest, by name.</summary>
    public static TheoryData<string> ExciterNames => new() { "ESST1A", "EXST1", "SCRX bus fed", "SCRX solid fed", "ESDC1A" };

    private static IRmsExciterModel ExciterOf(string name) => name switch
    {
        "ESST1A" => Esst1a(),
        "EXST1" => Exst1(),
        "SCRX bus fed" => ScrxBus(),
        "SCRX solid fed" => ScrxSolid(),
        _ => Esdc1a(),
    };

    // --------------------------------------------------------------------------------------------------- 1. flat start

    [Theory]
    [MemberData(nameof(ExciterNames))]
    public void Each_exciter_alone_holds_its_initial_field_to_rounding(string name)
    {
        foreach (double efd0 in new[] { 1.2, 2.4 })
        {
            IRmsExciterModel m = ExciterOf(name);
            m.Initialize(efd0);
            double e0 = m.EquilibriumErrorPu(efd0);
            double worst = 0;
            for (int n = 0; n < 4000; n++)
                worst = Math.Max(worst, Math.Abs(m.Step(e0, 0.0, At(0.0, efd0, 1.02, 0.8), 0.005) - efd0));
            Assert.True(worst <= 1e-14 * efd0, $"{name} at Efd0 {efd0}: drifted {worst:R}");
        }
    }

    [Fact]
    public void Each_stabiliser_alone_starts_at_rest_to_rounding()
    {
        foreach (IRmsStabiliser s in new IRmsStabiliser[] { IeeestPower(), IeeestSpeed(), Pss2aDual(), Pss2bDual() })
        {
            s.Initialize();
            double worst = 0;
            for (int n = 0; n < 4000; n++) worst = Math.Max(worst, Math.Abs(s.Step(At(0.0, 1.5, 1.02, 0.8), 0.005)));
            Assert.True(worst <= 1e-13, $"{s.Kind}: moved {worst:R} at rest");
        }
    }

    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Case9Fixture, SchemasDir, model);
        return (model, pf, model.Generators.Select((g, k) => set.For(k)!.Data).ToList());
    }

    /// <summary>A fleet of library exciters for case9 (three machines), each with a library stabiliser or none.</summary>
    public static TheoryData<string> Fleets => new()
    {
        "ESST1A+PSS2A", "EXST1+IEEEST power", "SCRX bus fed+IEEEST speed", "SCRX solid fed", "ESDC1A+PSS2B",
        "SEXS+PSS2A", "ESAC1A+IEEEST power",
    };

    private static List<RmsExciter?> FleetOf(string name, int units) => Enumerable.Range(0, units).Select(_ => (RmsExciter?)(name switch
    {
        "ESST1A+PSS2A" => RmsExciter.FromModel(Esst1a(), libraryStabiliser: Pss2aDual()),
        "EXST1+IEEEST power" => RmsExciter.FromModel(Exst1(), libraryStabiliser: IeeestPower()),
        "SCRX bus fed+IEEEST speed" => RmsExciter.FromModel(ScrxBus(), libraryStabiliser: IeeestSpeed()),
        "SCRX solid fed" => RmsExciter.FromModel(ScrxSolid()),
        "ESDC1A+PSS2B" => RmsExciter.FromModel(Esdc1a(), libraryStabiliser: Pss2bDual()),
        "SEXS+PSS2A" => RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0).WithLibraryStabiliser(Pss2aDual()),
        _ => RmsExciter.FromAc(Esac1a()).WithLibraryStabiliser(IeeestPower()),
    })).ToList();

    [Theory]
    [MemberData(nameof(Fleets))]
    public void A_library_fleet_holds_case9_at_rest_to_rounding_and_a_fault_moves_it(string fleet)
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0);
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0, exciters: FleetOf(fleet, model.Generators.Count));
        Assert.True(still.StayedInSynchronism);
        for (int i = 0; i < still.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace a = still.Machines[i], b = held.Machines[i];
            for (int k = 0; k < a.SpeedPu.Count; k++)
            {
                // Speed: the held run itself drifts with the power flow's residual; the library may add at most RestBound in
                // E'q, whose power change over the 5 s run at H >= 3 s moves the speed by under RestBound * 5 / (2 * 3).
                Assert.True(Math.Abs(a.SpeedPu[k] - b.SpeedPu[k]) <= RestBound(1000), $"{fleet}: machine {i} speed {a.SpeedPu[k]:R} against the held {b.SpeedPu[k]:R} at sample {k}");
                Assert.True(Math.Abs(a.EqpPu[k] - b.EqpPu[k]) <= RestBound(1000), $"{fleet}: machine {i} E'q moved {a.EqpPu[k] - b.EqpPu[k]:R} from the held field at sample {k}");
            }
        }
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: FleetOf(fleet, model.Generators.Count));
        MultiMachineSubtransient.Result heldFault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.Contains(fault.Machines.Zip(heldFault.Machines), p => Math.Abs(p.First.EqpPu[^1] - p.Second.EqpPu[^1]) > 1e-4);
    }

    [Fact]
    public void A_gencls_fleet_starts_at_rest_and_holds_its_flux_states_bit_for_bit_through_a_fault()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        double[] xdp = { 0.0608, 0.1198, 0.1813 };   // the case9 (Anderson-Fouad) transient reactances
        List<SubtransientInit.MachineData> data = model.Generators.Select((g, k) => Gencls.Data(g.H > 0 ? g.H : 5.0, 0.0, xdp[k] * g.MBase / model.BaseMva)).ToList();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0);
        foreach (MultiMachineSubtransient.MachineTrace m in still.Machines)
            Assert.All(m.SpeedPu, w => Assert.True(Math.Abs(w) <= 1e-12, $"speed {w:R}"));
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0);
        Assert.Contains(fault.Machines, m => Math.Abs(m.SpeedPu[^1]) > 1e-5);
        foreach (MultiMachineSubtransient.MachineTrace m in fault.Machines)
        {
            Assert.All(m.EqpPu, v => Assert.Equal(m.EqpPu[0], v));
            Assert.All(m.EdpPu, v => Assert.Equal(m.EdpPu[0], v));
            Assert.All(m.EqppPu, v => Assert.Equal(m.EqppPu[0], v));
            Assert.All(m.EddpPu, v => Assert.Equal(m.EddpPu[0], v));
            // The classical internal voltage: E'' = E' (no damper circuits), E'd = 0 to rounding.
            Assert.Equal(m.EqpPu[0], m.EqppPu[0]);
            Assert.True(Math.Abs(m.EdpPu[0]) <= 1e-14, $"E'd {m.EdpPu[0]:R}");
        }
    }

    [Fact]
    public void A_gentpj_with_kis_starts_at_rest()
    {
        (GridModel model, PowerFlowResult pf, List<SubtransientInit.MachineData> data) = Case9();
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        List<SubtransientInit.MachineData> tpj = data.Select(d => d with { Saturation = sat, Xl = 0.1, SaturationKis = 0.08 }).ToList();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, tpj, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0);
        foreach (MultiMachineSubtransient.MachineTrace m in still.Machines)
            for (int k = 0; k < m.SpeedPu.Count; k++)
            {
                Assert.True(Math.Abs(m.SpeedPu[k]) <= 1e-12, $"speed {m.SpeedPu[k]:R}");
                Assert.True(Math.Abs(m.EqpPu[k] - m.EqpPu[0]) <= 1e-12, $"E'q moved {m.EqpPu[k] - m.EqpPu[0]:R}");
            }
        // Kis moves the operating point: the seeded field differs from the Kis = 0 machine's.
        var p0 = SubtransientInit.FromSolvedCase(model, pf, data.Select(d => d with { Saturation = sat, Xl = 0.1 }).ToList(), 7);
        var p1 = SubtransientInit.FromSolvedCase(model, pf, tpj, 7);
        Assert.All(Enumerable.Range(0, p0.Efd.Length), i => Assert.True(p1.Efd[i] > p0.Efd[i], $"machine {i}: Kis must add field"));
    }

    // ------------------------------------------------------------------------------------- 2. step responses (SciPy)

    private static readonly double[] Times = { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };

    // Pinned from tests/tooling/dynamic-library-reference.py (SciPy 1.18.1), in the order of Times.
    private static readonly double[] Exst1Ref = { 0.006486454453798884, 0.009155607810881464, 0.011163940535627085, 0.01395871406593266, 0.018155409404203696, 0.02594395198289611, 0.045137930277249265 };
    private static readonly double[] Esdc1aRef = { 1.506834302130826, 1.518346660688384, 1.5332562589849548, 1.541368585638048, 1.5531957179740365, 1.5742442315566394, 1.6209515285471487 };
    private static readonly double[] Esst1aRef = { 2.0156301694510423, 2.0216648710019185, 2.026318358688166, 2.033924711291496, 2.045133239929537, 2.0648219072281586, 2.1096273798320855 };
    private static readonly double[] IeeestRef = { 0.031234022871176065, 0.021727726605709815, 0.006694873015869733, 0.004577255686764502, 0.003850524447675075, 0.0023371971914965916, 0.0005214992126837384 };
    private static readonly double[] Pss2aSpeedRef = { 0.0004742835808520308, 0.004732677026342875, 0.01749792696808066, 0.007350103129458199, 0.001508563515811374, -0.0006091461563461145, -0.0011990206458937286 };
    private static readonly double[] Pss2aPowerRef = { -0.07600116353105443, -0.06689016818320948, -0.04220285415811328, 0.012630313737560267, 0.010672889879014276, 0.004313208182903806, -0.0003724464253369843 };

    /// <summary>The largest error at the sample times of a response stepped with <paramref name="dt"/>, where
    /// <paramref name="run"/> returns the value at the end of each of the given number of steps.</summary>
    private static double WorstError(Func<double, int, double[]> run, double[] reference, double dt, double offset)
    {
        int steps = (int)Math.Round(Times[^1] / dt);
        double[] y = run(dt, steps);
        double worst = 0;
        for (int i = 0; i < Times.Length; i++)
        {
            int k = (int)Math.Round(Times[i] / dt) - 1;
            worst = Math.Max(worst, Math.Abs(y[k] - offset - reference[i]));
        }
        return worst;
    }

    // The 1 % bound is derived from the zero-order-hold step. A lag of time constant tau stepped with dt carries a local
    // error of (dt / tau)^2 / 2 of its change, which the lag damps at dt / tau per step, so the accumulated error is at most
    // (dt / (2 tau)) of the change of that block, and at the sample times (0.05 s and later) never above it. The fastest time
    // constant in these cases is 0.02 (the PSS2A T6) s, so at dt = 0.1 ms the bound is 1e-4 / (2 x 0.02) = 0.25 % of the block's change; the
    // 1 % tolerance therefore allows four times amplification by the lead-lag stages and gains between that block and the
    // output, measured against the peak of the response.
    private static void AgreesAndConverges(string what, Func<double, int, double[]> run, double[] reference, double offset = 0.0)
    {
        double peak = reference.Max(Math.Abs);
        double e1 = WorstError(run, reference, 1e-4, offset), e2 = WorstError(run, reference, 5e-5, offset);
        Assert.True(e1 <= 1e-2 * peak, $"{what}: worst error {e1:R} at 0.1 ms against a peak of {peak:R}");
        Assert.True(e2 <= 0.6 * e1, $"{what}: the error must fall with the step (first order): {e1:R} at 0.1 ms, {e2:R} at 0.05 ms");
    }

    private static Func<double, int, double[]> ExciterRun(Func<IRmsExciterModel> make, double efd0, double error, FieldControlInputs inputs)
        => (dt, steps) =>
        {
            IRmsExciterModel m = make();
            m.Initialize(efd0);
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = m.Step(error, 0.0, inputs, dt);
            return y;
        };

    private static Func<double, int, double[]> StabiliserRun(Func<IRmsStabiliser> make, FieldControlInputs before, FieldControlInputs after)
        => (dt, steps) =>
        {
            IRmsStabiliser s = make();
            s.Initialize();
            s.Step(before, dt);   // the undisturbed point sets the states
            var y = new double[steps];
            for (int k = 0; k < steps; k++) y[k] = s.Step(after, dt);
            return y;
        };

    [Fact]
    public void Exst1_follows_the_scipy_step_response_and_its_lead_lag_alone_is_the_closed_form()
    {
        Func<IRmsExciterModel> make = () => St1Exciter.Exst1(0.0, 10.0, -10.0, 1.0, 10.0, 200.0, 0.05, 100.0, -100.0, 0.0, 0.0, 0.0);
        AgreesAndConverges("EXST1", ExciterRun(make, 2.0, 0.01 + 0.0005, At(0.0, 2.0, 1.0)), Exst1Ref, offset: 2.0);

        // TA = 0: the lead-lag is the only dynamics and its zero-order hold is exact for the held error, so Efd is the closed
        // form KA dE (1 - (TB - TC)/TB e^(-t/TB)) above Efd0 at every step, to rounding: the lag state xL (at most
        // (1 - c) u = 0.9 x 0.0105) gains at most two roundings of eps |xL| per step, so after k steps Efd = KA y is within
        // KA 2 eps k |xL| plus a few roundings of Efd (4 eps 2.1).
        IRmsExciterModel exact = St1Exciter.Exst1(0.0, 10.0, -10.0, 1.0, 10.0, 200.0, 0.0, 100.0, -100.0, 0.0, 0.0, 0.0);
        exact.Initialize(2.0);
        for (int k = 1; k <= 2000; k++)
        {
            double efd = exact.Step(0.0105, 0.0, At(0.0, 2.0, 1.0), 0.005);
            double want = 2.0 + 200.0 * 0.0005 * (1.0 - 0.9 * Math.Exp(-k * 0.005 / 10.0));
            double bound = 200.0 * 2.0 * Math.Pow(2, -53) * k * (0.9 * 0.0105) + 4.0 * Math.Pow(2, -53) * 2.1;
            Assert.True(Math.Abs(efd - want) <= bound, $"step {k}: {efd:R} against {want:R} (bound {bound:R})");
        }
    }

    [Fact]
    public void Esdc1a_with_rate_feedback_follows_the_scipy_integration()
        => AgreesAndConverges("ESDC1A", ExciterRun(() => new Esdc1a(0.0, 100.0, 0.05, 0.0, 0.0, 10.0, -10.0, 1.0, 0.5, 0.05, 1.0, 0, 0, 0, 0),
            1.5, 0.015 + 0.002, At()), Esdc1aRef.Select(v => v - 1.5).ToArray(), offset: 1.5);

    [Fact]
    public void Esst1a_with_rate_feedback_follows_the_scipy_integration()
        => AgreesAndConverges("ESST1A", ExciterRun(() => St1Exciter.Esst1a(0.0, 1.0, -1.0, 0.5, 2.0, 0.0, 0.0, 50.0, 0.1, 10.0, -10.0, 10.0, -10.0, 0.0, 0.1, 1.0, 0.0, 0.0),
            2.0, 2.0 / 50.0 + 0.004, At(0.0, 2.0, 1.0)), Esst1aRef.Select(v => v - 2.0).ToArray(), offset: 2.0);

    [Fact]
    public void Ieeest_on_speed_follows_the_exact_lti_step_response()
        => AgreesAndConverges("IEEEST", StabiliserRun(() => new Ieeest(1, 0.05, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, 10.0, 1.0, -1.0, 0.0, 0.0),
            At(0.0), At(0.001)), IeeestRef);

    [Fact]
    public void Pss2a_follows_the_exact_lti_step_response_on_each_input()
    {
        Func<IRmsStabiliser> make = () => Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 1.0, -1.0);
        AgreesAndConverges("PSS2A speed", StabiliserRun(make, At(0.0, pe: 0.8), At(0.001, pe: 0.8)), Pss2aSpeedRef);
        AgreesAndConverges("PSS2A power", StabiliserRun(make, At(0.0, pe: 0.8), At(0.0, pe: 0.9)), Pss2aPowerRef);
    }

    // ------------------------------------------------------------------------------------------- 2b. limits, exactly

    [Fact]
    public void Esst1a_regulator_limit_is_non_windup_and_the_ceiling_is_vt_vrmax_minus_kc_ifd()
    {
        // VA on VAMAX exactly; reversing the error leaves the limit on the first step (no wound-up state).
        St1Exciter va = St1Exciter.Esst1a(0.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 50.0, 0.1, 3.0, -3.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        va.Initialize(2.0);
        for (int k = 0; k < 400; k++) va.Step(1.0, 0.0, At(0.0, 2.0, 1.0), 0.005);
        Assert.Equal(3.0, va.RegulatorOutput);
        va.Step(-1.0, 0.0, At(0.0, 2.0, 1.0), 0.005);
        Assert.True(va.RegulatorOutput < 3.0);

        // Output ceiling Vt VRMAX - KC IFD at Vt 0.9, IFD 2.0.
        St1Exciter ceil = St1Exciter.Esst1a(0.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 50.0, 0.0, 10.0, -10.0, 2.8, -2.0, 0.1, 0.0, 0.0, 0.0, 0.0);
        ceil.Initialize(2.0);
        double efd = 0;
        for (int k = 0; k < 10; k++) efd = ceil.Step(1.0, 0.0, At(0.0, 2.0, 0.9), 0.005);
        Assert.Equal(0.9 * 2.8 - 0.1 * 2.0, efd);
        // ... and the floor Vt VRMIN.
        for (int k = 0; k < 10; k++) efd = ceil.Step(-1.0, 0.0, At(0.0, 2.0, 0.9), 0.005);
        Assert.Equal(0.9 * -2.0, efd);

        // Field current limiter: with TA = 0 and no lead-lag, Efd = KA e - KLR (IFD - ILR) once IFD passes ILR.
        St1Exciter lim = St1Exciter.Esst1a(0.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 50.0, 0.0, 10.0, -10.0, 10.0, -10.0, 0.0, 0.0, 0.0, 2.0, 2.1);
        lim.Initialize(2.0);
        Assert.Equal(50.0 * 0.05 - 2.0 * (2.5 - 2.1), lim.Step(0.05, 0.0, At(0.0, 2.5, 1.0), 0.005));
        Assert.Equal(50.0 * 0.05, lim.Step(0.05, 0.0, At(0.0, 2.0, 1.0), 0.005));
        // VOS 2: the stabiliser joins after the regulator, so with TA = 0 it adds one for one to Efd.
        St1Exciter vos = St1Exciter.Esst1a(0.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 50.0, 0.0, 10.0, -10.0, 10.0, -10.0, 0.0, 0.0, 0.0, 0.0, 0.0, vos: 2);
        vos.Initialize(2.0);
        Assert.Equal(50.0 * 0.04 + 0.01, vos.Step(0.04, 0.01, At(0.0, 2.0, 1.0), 0.005));
    }

    [Fact]
    public void Exst1_input_limiter_caps_the_regulator_input()
    {
        St1Exciter x = St1Exciter.Exst1(0.0, 0.05, -0.05, 0.0, 0.0, 200.0, 0.0, 20.0, -20.0, 0.0, 0.0, 0.0);
        x.Initialize(2.0);
        Assert.Equal(200.0 * 0.05, x.Step(1.0, 0.0, At(0.0, 2.0, 1.0), 0.005));
        Assert.Equal(200.0 * -0.05, x.Step(-1.0, 0.0, At(0.0, 2.0, 1.0), 0.005));
        // An equilibrium the input limiter cannot hold is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => St1Exciter.Exst1(0.0, 0.005, -0.005, 0.0, 0.0, 200.0, 0.0, 20.0, -20.0, 0.0, 0.0, 0.0).Initialize(2.0));
    }

    [Fact]
    public void Esdc1a_regulator_limit_is_non_windup_and_the_field_settles_where_vrmax_meets_ke_plus_saturation()
    {
        // Unsaturated: VR on VRMAX exactly, Efd settles at VRMAX / KE.
        var lin = new Esdc1a(0.0, 100.0, 0.05, 0.0, 0.0, 3.0, -3.0, 1.0, 0.5, 0.0, 0.0, 0, 0, 0, 0);
        lin.Initialize(1.5);
        double efd = 0;
        for (int k = 0; k < 6000; k++) efd = lin.Step(1.0, 0.0, At(), 0.005);
        Assert.Equal(3.0, lin.RegulatorOutput);
        Assert.True(Math.Abs(efd - 3.0) <= 1e-12, $"Efd {efd:R}");
        lin.Step(-1.0, 0.0, At(), 0.005);
        Assert.True(lin.RegulatorOutput < 3.0, "non-windup: VR leaves VRMAX on the first reversed step");

        // Saturated: VRMAX = KE x + B (x - A)^2, the quadratic through (E1, SE1), (E2, SE2) solved here independently.
        double e1 = 2.3, se1 = 0.1, e2 = 3.1, se2 = 0.33, ke = 1.0, vrMax = 3.5;
        double r = Math.Sqrt(se1 * e1 / (se2 * e2)), a = (e1 - r * e2) / (1 - r), b = se2 * e2 / ((e2 - a) * (e2 - a));
        double qa = b, qb = ke - 2 * a * b, qc = b * a * a - vrMax;
        double want = (-qb + Math.Sqrt(qb * qb - 4 * qa * qc)) / (2 * qa);
        Assert.True(want > a);
        var sat = new Esdc1a(0.0, 100.0, 0.05, 0.0, 0.0, vrMax, -vrMax, ke, 0.5, 0.0, 0.0, e1, se1, e2, se2);
        sat.Initialize(1.5);
        for (int k = 0; k < 8000; k++) efd = sat.Step(1.0, 0.0, At(), 0.005);
        Assert.True(Math.Abs(efd - want) <= 1e-9, $"Efd {efd:R} against {want:R}");
        // SE(E1) at E1 is the data point: (KE + SE(E1)) E1 at rest.
        Assert.True(Math.Abs(sat.EquilibriumErrorPu(e1) * 100.0 - (ke + se1) * e1) <= 1e-13);
    }

    [Fact]
    public void Scrx_limits_bus_feeding_and_the_crowbar_are_exact()
    {
        Scrx bus = new(0.0, 0.0, 200.0, 0.0, 0.0, 5.0, busFed: true, crowbarRatio: 10.0);
        bus.Initialize(2.0);
        double first = bus.Step(2.0 / 200.0, 0.0, At(0.0, 2.0, 1.04), 0.005);
        Assert.True(Math.Abs(first - 2.0) <= 1e-15, $"bus fed start {first:R}");
        Assert.Equal(5.0 * 1.04, bus.Step(1.0, 0.0, At(0.0, 2.0, 1.04), 0.005));          // EMAX times Vt
        Assert.Equal(5.0 * 0.5, bus.Step(1.0, 0.0, At(0.0, 2.0, 0.5), 0.005));            // the ceiling falls with Vt
        Assert.Equal(10.0 * 0.5, bus.Step(1.0, 0.0, At(0.0, -0.5, 0.5), 0.005));          // negative IFD: the crowbar
        Scrx solid = new(0.0, 0.0, 200.0, 0.0, -5.0, 5.0, busFed: false, crowbarRatio: 0.0);
        solid.Initialize(2.0);
        Assert.Equal(-5.0, solid.Step(-1.0, 0.0, At(0.0, -0.5), 0.005));                // rc/rfd 0: negative current capability
        Assert.False(solid.NeedsTerminalQuantities);
        // Bus fed, x0 = Efd0 / Vt0 = 2.0 above EMAX 1.5: refused on the first step, which measures Vt0.
        Scrx over = new(0.0, 0.0, 200.0, 0.0, 0.0, 1.5, busFed: true, crowbarRatio: 0.0);
        over.Initialize(2.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => over.Step(0.01, 0.0, At(0.0, 2.0, 1.0), 0.005));
    }

    [Fact]
    public void Ieeest_and_pss2_limits_cut_offs_and_final_values_are_exact()
    {
        Ieeest big = new(1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, 2.0, 1000.0, 0.05, -0.04, 0.0, 0.0);
        big.Initialize();
        big.Step(At(0.0), 0.005);
        Assert.Equal(0.05, big.Step(At(0.01), 0.005));
        Assert.Equal(-0.04, big.Step(At(-0.02), 0.005));
        Ieeest cut = new(3, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, 2.0, 1.0, 0.5, -0.5, 1.2, 0.8);
        cut.Initialize();
        cut.Step(At(pe: 0.8, vt: 1.0), 0.005);
        Assert.NotEqual(0.0, cut.Step(At(pe: 0.9, vt: 1.0), 0.005));
        Assert.Equal(0.0, cut.Step(At(pe: 0.9, vt: 1.25), 0.005));   // above VCU
        Assert.Equal(0.0, cut.Step(At(pe: 0.9, vt: 0.75), 0.005));   // below VCL

        Pss2 a = Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.01, -0.01);
        a.Initialize();
        a.Step(At(0.0, pe: 0.8), 0.005);
        double vs = 0, peak = 0;
        for (int k = 0; k < 24000; k++) { vs = a.Step(At(0.001, pe: 0.8), 0.005); peak = Math.Max(peak, vs); }
        Assert.Equal(0.01, peak);                                  // VSTMAX engages exactly
        Assert.True(Math.Abs(vs) <= 1e-12, $"washed out to {vs:R}");   // a held speed step washes out

        // PSS2B with VS1MAX = 0.001 and no third stage, stepped by 0.05, is PSS2A stepped by 0.001, bit for bit.
        Pss2 lim = Pss2.Pss2b(1, 3, 5, 1, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.0, 0.0,
            0.001, -1.0, double.PositiveInfinity, double.NegativeInfinity, 1.0, -1.0);
        Pss2 free = Pss2.Pss2a(1, 3, 5, 1, 2.0, 2.0, 0.02, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 1.0, -1.0);
        lim.Initialize(); free.Initialize();
        lim.Step(At(0.0, pe: 0.8), 0.005); free.Step(At(0.0, pe: 0.8), 0.005);
        for (int k = 0; k < 2000; k++) Assert.Equal(free.Step(At(0.001, pe: 0.8), 0.005), lim.Step(At(0.05, pe: 0.8), 0.005));
    }

    [Fact]
    public void Bad_library_data_is_refused_by_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => St1Exciter.Exst1(0, 1, -1, 1.0, 0.0, 200, 0, 5, -5, 0, 0, 0));         // TC with no TB
        Assert.Throws<ArgumentOutOfRangeException>(() => St1Exciter.Exst1(0, 1, -1, 0, 0, 200, 0, 5, -5, 0, 0.1, 0));           // KF with TF 0
        Assert.Throws<ArgumentOutOfRangeException>(() => St1Exciter.Esst1a(0, 1, -1, 0, 0, 0, 0, 200, 0, 5, -5, 5, -5, 0, 0, 0, 0, 0, vos: 3));
        Assert.Throws<ArgumentException>(() => St1Exciter.Exst1(0, 1, -1, 0, 0, 200, 0, -5, 5, 0, 0, 0));                         // VRMAX below VRMIN
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeest(2, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, -1, 0, 0));             // bus frequency
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ieeest(1, 0, 0, 0.1, 0, 0, 0, 0, 1, 1, 1, 1, -1, 0, 0));           // A5 with no lag
        Assert.Throws<ArgumentOutOfRangeException>(() => Pss2.Pss2a(1, 3, 5, 2, 2, 2, 0, 2, 0, 2, 0.4, 1, 0.5, 0.1, 10, 0, 0, 0, 0, 1, -1)); // M N 10
        Assert.Throws<ArgumentOutOfRangeException>(() => Pss2.Pss2a(1, 3, 1, 1, 2, 2, 0, 2, 0, 2, 0.4, 1, 0.5, 0.0, 10, 0, 0, 0, 0, 1, -1)); // T8 with T9 0
        Assert.Throws<ArgumentOutOfRangeException>(() => Gencls.Data(0.0, 0.0, 0.3));
        Assert.Throws<ArgumentException>(() => RmsExciter.FromModel(Exst1(), new Pss1a(1, 1, 0, 0, 0, 0), IeeestSpeed()));
        Assert.Throws<InvalidOperationException>(() => RmsExciter.FromSexs(0.1, 1, 100, 0.05, stabiliser: new Pss1a(1, 1, 0, 0, 0, 0)).WithLibraryStabiliser(IeeestSpeed()));
    }

    // ------------------------------------------------------------------------------------ 3. GENTPJ Kis = 0 is GENROU

    [Fact]
    public void Gentpj_increments_with_kis_zero_are_genrou_bit_for_bit_for_the_same_saturation_inputs()
    {
        var sat = new SubtransientMachine.SaturationCurve(0.10, 0.30);
        for (int i = 0; i <= 40; i++)
            for (int j = -10; j <= 10; j++)
            {
                double eqpp = 0.6 + 0.02 * i, edpp = 0.05 * j;
                foreach (double q in new[] { 0.5, 0.9, 1.0 })
                    Assert.Equal(sat.GenrouIncrements(eqpp, edpp, q), sat.GentpjIncrements(eqpp, edpp, q, 0.0));
            }
        // Above zero the flux magnitude grows by Kis |It|, so the saturation grows with it.
        Assert.True(sat.GentpjIncrements(1.05, 0.2, 0.9, 0.1).D > sat.GenrouIncrements(1.05, 0.2, 0.9).D);
    }

    private static string WriteTemp(string text, string name)
    {
        string dir = TestTemp.Under("gridsim-wp3a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static void DeleteTemp(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_gentpj_with_kis_zero_loads_and_runs_as_genrou_bit_for_bit()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        string genrou = File.ReadAllText(Case9Fixture).Replace("\r\n", "\n", StringComparison.Ordinal);
        // The bus-1 template (saturated, S(1.0) 0.10, S(1.2) 0.30) becomes GENTPJ with Kis = 0.
        const string head = "\"title\": \"Bus 1 unit (hydro, saturated)\",\n      \"params\": {";
        Assert.Contains(head, genrou, StringComparison.Ordinal);
        string gentpj = genrou.Replace(head, head + " \"model\": \"GENTPJ\", \"saturationKis\": 0.0,", StringComparison.Ordinal);
        Assert.Contains("\"model\": \"GENTPJ\"", gentpj, StringComparison.Ordinal);
        string path = WriteTemp(gentpj, "gentpj.dynamics.json");
        try
        {
            MachineDynamicsSet a = MachineDynamicsLoader.Load(Case9Fixture, SchemasDir, model);
            MachineDynamicsSet b = MachineDynamicsLoader.Load(path, SchemasDir, model);
            Assert.NotNull(a.For(0)!.Data.Saturation);
            for (int k = 0; k < model.Generators.Count; k++) Assert.Equal(a.For(k)!.Data, b.For(k)!.Data);
            List<SubtransientInit.MachineData> da = model.Generators.Select((_, k) => a.For(k)!.Data).ToList();
            List<SubtransientInit.MachineData> db = model.Generators.Select((_, k) => b.For(k)!.Data).ToList();
            MultiMachineSubtransient.Result ra = RmsTransientStability.Study(model, pf, da, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0);
            MultiMachineSubtransient.Result rb = RmsTransientStability.Study(model, pf, db, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0);
            Assert.Equal(Digest(ra), Digest(rb));
            // Kis above 0 is a different machine.
            MachineDynamicsSet c = MachineDynamicsLoader.Load(WriteBeside(path, gentpj.Replace("\"saturationKis\": 0.0", "\"saturationKis\": 0.1", StringComparison.Ordinal)), SchemasDir, model);
            Assert.Equal(0.1, c.For(0)!.Data.SaturationKis);
            List<SubtransientInit.MachineData> dc = model.Generators.Select((_, k) => c.For(k)!.Data).ToList();
            Assert.NotEqual(Digest(ra), Digest(RmsTransientStability.Study(model, pf, dc, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 2.0)));
        }
        finally { DeleteTemp(path); }
    }

    private static string WriteBeside(string path, string text)
    {
        string p = Path.Combine(Path.GetDirectoryName(path)!, "kis.dynamics.json");
        File.WriteAllText(p, text);
        return p;
    }

    private static string Digest(MultiMachineSubtransient.Result r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
                foreach (IReadOnlyList<double> s in new[] { m.DeltaRad, m.SpeedPu, m.EqpPu, m.EdpPu, m.EqppPu, m.EddpPu })
                    foreach (double v in s) w.Write(BitConverter.DoubleToInt64Bits(v));
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    // ------------------------------------------------------------------------------------------------------ 4. DYR

    /// <summary>case14 with a source reactance ZX of 0.25 on the bus-8 machine (the GENCLS unit's X'd).</summary>
    private static string Case14Raw()
    {
        string raw = File.ReadAllText(PsseFixture("case14.raw"));
        const string bus8 = "8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 1, 0,";
        Assert.Contains(bus8, raw, StringComparison.Ordinal);
        return raw.Replace(bus8, "8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 0.25, 0,", StringComparison.Ordinal);
    }

    private const string Genrou = " 7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0";

    // One record of each extended model the DYR reader maps, on the case14 units (bus 1, 2, 3, 6 and 8). The record orders
    // are the PSS/E model library data sheets' (public copy: pdfcoffee.com/models-pdf-free.html), cross-checked against
    // NREL PowerSystems.jl src/parsers/psse_dynamic_mapping.yaml (GENCLS, EXST1, SCRX, ESAC1A, ESDC1A, ESST1A, IEEEST) and
    // the PowerWorld transient-model pages (ESST1A, ESAC1A, PSS2A, PSS2B, SCRX, IEEEST). GENTPJ has no public record order and
    // is refused (below).
    private const string ExtendedDyr =
          "     1 'GENROU' 1 " + Genrou + " /\n"
        + "     1 'ESST1A' 1  1  1  0.02  0.1 -0.1  1.0  10.0  0.0  0.0  210.0  0.02  6.43 -6.0  6.43 -6.0  0.038  0.0  1.0  4.54  6.0 /\n"
        + "     1 'PSS2A' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.1 -0.1 /\n"
        + "     2 'GENROU' 1 " + Genrou + " /\n"
        + "     2 'EXST1' 1  0.0  0.2 -0.2  1.0  10.0  200.0  0.02  7.0 -6.4  0.05  0.02  1.0 /\n"
        + "     2 'IEEEST' 1  3  0  0.05  0.0  0.0  0.0  0.0  0.0  0.2  0.05  0.3  0.1  1.5  2.0 -1.0  0.1 -0.1  1.2  0.0 /\n"
        + "     3 'GENROU' 1 " + Genrou + " /\n"
        + "     3 'SCRX' 1  0.1  10.0  200.0  0.05  0.0  5.0  0  10.0 /\n"
        + "     6 'GENROU' 1 " + Genrou + " /\n"
        + "     6 'ESDC1A' 1  0.0  46.0  0.06  0.0  0.0  5.0 -5.0  1.0  0.46  0.1  1.0  0  2.3  0.1  3.1  0.33 /\n"
        + "     6 'PSS2B' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.0  0.0  0.5 -0.5  2.0 -2.0  0.1 -0.1 /\n"
        + "     8 'GENCLS' 1  5.0  0.0 /\n";

    private static PsseDyrResult ReadExtended(string dyr, PsseParseResult raw, bool allow = false)
        => PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "wp3a.dyr", ExtendedLibrary = true, AllowUnmodelled = allow });

    private static MachineDynamicsSet LoadJson(PsseDyrResult r, GridModel model)
    {
        string path = WriteTemp(r.Json, "wp3a.dyr.dynamics.json");
        try { return MachineDynamicsLoader.Load(path, SchemasDir, model); }
        finally { DeleteTemp(path); }
    }

    private static PsseParseResult Raw14() => PsseRawParser.ParseWithDiagnostics(Case14Raw(), "case14");

    [Fact]
    public void Every_extended_dyr_record_maps_value_by_value_in_its_psse_order()
    {
        PsseParseResult raw = Raw14();
        Assert.Equal(0.25, raw.MachineSourceReactances[4]);
        Assert.Equal(1.0, raw.MachineSourceReactances[0]);
        PsseDyrResult r = ReadExtended(ExtendedDyr, raw);
        Assert.Equal(5, r.Machines);
        MachineDynamicsSet set = LoadJson(r, raw.Model);
        MachineDynamicsEntry U(int bus) => set.Entries.Single(e => raw.Model.Generators[e.GeneratorIndex].Bus == bus);

        IReadOnlyDictionary<string, double> p = U(1).Exciter!.Params;
        Assert.Equal("ESST1A", U(1).Exciter!.Model);
        Assert.Equal(new[] { 0.02, 0.1, -0.1, 1.0, 10.0, 0.0, 0.0, 210.0, 0.02, 6.43, -6.0, 6.43, -6.0, 0.038, 0.0, 1.0, 4.54, 6.0, 1.0 },
            new[] { "st1TrS", "st1ViMaxPu", "st1ViMinPu", "st1TcS", "st1TbS", "st1aTc1S", "st1aTb1S", "st1Ka", "st1TaS", "st1aVaMaxPu", "st1aVaMinPu",
                "st1VrMaxPu", "st1VrMinPu", "st1Kc", "st1Kf", "st1TfS", "st1aKlr", "st1aIlrPu", "st1aVos" }.Select(k => p[k]));
        Assert.Equal("PSS2A", U(1).Exciter!.PssModel);
        IReadOnlyDictionary<string, double> s = U(1).Exciter!.Pss!;
        Assert.Equal(new[] { 1.0, 3.0, 5.0, 1.0, 2.0, 2.0, 0.0, 2.0, 0.0, 2.0, 0.4, 1.0, 0.5, 0.1, 10.0, 0.15, 0.03, 0.15, 0.03, 0.1, -0.1 },
            new[] { "pss2Input1", "pss2Input2", "pss2M", "pss2N", "pss2Tw1S", "pss2Tw2S", "pss2T6S", "pss2Tw3S", "pss2Tw4S", "pss2T7S", "pss2Ks2", "pss2Ks3",
                "pss2T8S", "pss2T9S", "pss2Ks1", "pss2T1S", "pss2T2S", "pss2T3S", "pss2T4S", "pss2OutputMaxPu", "pss2OutputMinPu" }.Select(k => s[k]));

        p = U(2).Exciter!.Params;
        Assert.Equal(new[] { 0.0, 0.2, -0.2, 1.0, 10.0, 200.0, 0.02, 7.0, -6.4, 0.05, 0.02, 1.0 },
            new[] { "st1TrS", "st1ViMaxPu", "st1ViMinPu", "st1TcS", "st1TbS", "st1Ka", "st1TaS", "st1VrMaxPu", "st1VrMinPu", "st1Kc", "st1Kf", "st1TfS" }.Select(k => p[k]));
        s = U(2).Exciter!.Pss!;
        Assert.Equal("IEEEST", U(2).Exciter!.PssModel);
        Assert.Equal(new[] { 3.0, 0.05, 0.0, 0.0, 0.2, 0.05, 0.3, 0.1, 1.5, 2.0, -1.0, 0.1, -0.1, 1.2, 0.0 },
            new[] { "ieeestInput", "ieeestA1S", "ieeestA3S", "ieeestA5S", "ieeestT1S", "ieeestT2S", "ieeestT3S", "ieeestT4S", "ieeestT5S", "ieeestT6S",
                "ieeestKs", "ieeestOutputMaxPu", "ieeestOutputMinPu", "ieeestVcuPu", "ieeestVclPu" }.Select(k => s[k]));

        p = U(3).Exciter!.Params;
        Assert.Equal(new[] { 0.1 * 10.0, 10.0, 200.0, 0.05, 0.0, 5.0, 0.0, 10.0 },
            new[] { "scrxLeadS", "scrxLagS", "scrxGain", "scrxExciterTimeS", "scrxFieldMinPu", "scrxFieldMaxPu", "scrxSourceSwitch", "scrxCrowbarRatio" }.Select(k => p[k]));
        p = U(6).Exciter!.Params;
        Assert.Equal(new[] { 0.0, 46.0, 0.06, 0.0, 0.0, 5.0, -5.0, 1.0, 0.46, 0.1, 1.0, 2.3, 0.1, 3.1, 0.33 },
            new[] { "dc1aTrS", "dc1aKa", "dc1aTaS", "dc1aTbS", "dc1aTcS", "dc1aVrMaxPu", "dc1aVrMinPu", "dc1aKe", "dc1aTeS", "dc1aKf", "dc1aTf1S",
                "dc1aSatE1", "dc1aSatSe1", "dc1aSatE2", "dc1aSatSe2" }.Select(k => p[k]));
        Assert.Equal("PSS2B", U(6).Exciter!.PssModel);
        s = U(6).Exciter!.Pss!;
        Assert.Equal(new[] { 0.0, 0.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1 },
            new[] { "pss2bT10S", "pss2bT11S", "pss2bInput1MaxPu", "pss2bInput1MinPu", "pss2bInput2MaxPu", "pss2bInput2MinPu", "pss2OutputMaxPu", "pss2OutputMinPu" }.Select(k => s[k]));

        SubtransientInit.MachineData cls = U(8).Data;
        Assert.Null(U(8).Exciter);
        Assert.Equal((5.0, 0.0), (cls.H, cls.Damping));
        Assert.All(new[] { cls.Xd, cls.Xdp, cls.Xdpp, cls.Xq, cls.Xqp, cls.Xqpp }, x => Assert.Equal(0.25, x));
        Assert.All(new[] { cls.Tdop, cls.Tdopp, cls.Tqop, cls.Tqopp }, t => Assert.Equal(Gencls.HeldFluxTimeSeconds, t));
        Assert.Contains("ZSORCE", r.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_esac1a_record_maps_va_and_vr_limits_to_their_places()
    {
        PsseDyrResult r = ReadExtended("     1 'GENROU' 1 " + Genrou + " /\n"
            + "     1 'ESAC1A' 1  0.0  0.0  0.0  400.0  0.02  14.5 -14.5  0.8  0.03  1.0  0.2  0.38  1.0  3.14  0.03  4.18  0.1  6.03 -5.43 /\n", Raw14());
        MachineDynamicsSet set = LoadJson(r, Raw14().Model);
        IReadOnlyDictionary<string, double> p = Assert.Single(set.Entries).Exciter!.Params;
        Assert.Equal("ESAC1A", set.Entries[0].Exciter!.Model);
        Assert.Equal(new[] { 0.0, 0.0, 400.0, 0.02, 14.5, -14.5, 0.8, 0.03, 1.0, 0.2, 0.38, 1.0, 3.14, 0.03, 4.18, 0.1, 6.03, -5.43 },
            new[] { "acLagS", "acLeadS", "acRegulatorGain", "acRegulatorTimeS", "acRegulatorMaxPu", "acRegulatorMinPu", "acExciterTimeS", "acFeedbackGain",
                "acFeedbackTimeS", "acRectifierKc", "acDemagnetisingKd", "acExciterConstant", "acSatE1", "acSatSe1", "acSatE2", "acSatSe2", "acOutputMaxPu", "acOutputMinPu" }.Select(k => p[k]));
        Assert.Equal("ESAC1A", set.Entries[0].Exciter!.Create().Kind);
    }

    [Fact]
    public void The_dyr_records_round_trip_through_text_to_the_same_document()
    {
        PsseParseResult raw = Raw14();
        PsseDyrResult first = ReadExtended(ExtendedDyr, raw);
        var sb = new StringBuilder();
        foreach (PsseDyrRecord rec in first.Records)
            sb.Append(rec.Bus.ToString(CultureInfo.InvariantCulture)).Append(" '").Append(rec.Model).Append("' '").Append(rec.Id).Append("' ")
                .Append(string.Join(", ", rec.Values)).Append(" /\n");
        PsseDyrResult second = ReadExtended(sb.ToString(), raw);
        Assert.Equal(first.Records.Count, second.Records.Count);
        for (int i = 0; i < first.Records.Count; i++)
        {
            Assert.Equal((first.Records[i].Bus, first.Records[i].Model, first.Records[i].Id), (second.Records[i].Bus, second.Records[i].Model, second.Records[i].Id));
            Assert.Equal(first.Records[i].Values, second.Records[i].Values);
        }
        // One record per line in both texts, so even the provenance line numbers agree: the documents are the same bytes.
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(first.Notes, second.Notes);
    }

    [Fact]
    public void The_default_reader_is_unchanged_and_the_extended_library_refuses_what_it_cannot_represent()
    {
        PsseParseResult raw = Raw14();
        var plain = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(ExtendedDyr, raw, "case14"));
        Assert.Contains("models this reader does not map: ESDC1A x1", plain.Message, StringComparison.Ordinal);
        Assert.Contains("GENCLS x1", plain.Message, StringComparison.Ordinal);

        void Refused(string dyr, string what)
        {
            var ex = Assert.Throws<InvalidDataException>(() => ReadExtended(dyr, raw));
            Assert.Contains(what, ex.Message, StringComparison.Ordinal);
        }
        Refused("     2 'GENTPJ' 1  6.5  0.035  0.6  0.05  4.0  0.0  1.7  1.6  0.28  0.45  0.2  0.13  0.1  0.3  0.05 /\n",
            "the order of the PSS/E GENTPJ record is not in any public source");
        Refused("     1 'GENROU' 1 " + Genrou + " /\n     1 'ESDC1A' 1  0.0  46.0  0.06  0.0  0.0  0.0 -5.0  1.0  0.46  0.1  1.0  0  2.3  0.1  3.1  0.33 /\n", "VRMAX = 0");
        Refused("     8 'GENCLS' 1  5.0  0.0 /\n     8 'SCRX' 1  0.1  10.0  200.0  0.05  0.0  5.0  0  10.0 /\n", "the unit's machine is GENCLS");
        Refused("     8 'GENCLS' 1  0.0  0.0 /\n", "infinite bus");
        Refused("     1 'GENROU' 1 " + Genrou + " /\n     1 'SEXS' 1  0.1  10.0  100.0  0.05  0.0  5.0 /\n"
            + "     1 'IEEEST' 1  1  0  0.05  0.01  0.0  0.0  0.0  0.0  0.2  0.05  0.3  0.1  1.5  2.0 -1.0  0.1 -0.1  0.0  0.0 /\n", "the second-order filter terms");
        Refused("     1 'GENROU' 1 " + Genrou + " /\n     1 'SEXS' 1  0.1  10.0  100.0  0.05  0.0  5.0 /\n"
            + "     1 'PSS2A' 1  2  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.1 -0.1 /\n", "an input other than rotor speed deviation");
        Refused("     1 'GENROU' 1 " + Genrou + " /\n     1 'ESDC1A' 1  0.0  46.0  0.06  0.0  0.0  5.0 -5.0  0.0  0.46  0.1  1.0  0  2.3  0.1  3.1  0.33 /\n", "KE = 0");
        Refused("     1 'GENROU' 1 " + Genrou + " /\n     1 'EXST1' 1  0.0  0.2 -0.2 /\n", "EXST1 needs exactly 12 values");
        // GENCLS on a RAW record whose ZX is 0.
        var zx0 = PsseRawParser.ParseWithDiagnostics(Case14Raw().Replace("8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 0.25, 0,", "8, '1', 0, 17.4, 24, -6, 1.09, 0, 100, 0, 0, 0,", StringComparison.Ordinal), "case14");
        var bad = Assert.Throws<InvalidDataException>(() => ReadExtended("     8 'GENCLS' 1  5.0  0.0 /\n", zx0));
        Assert.Contains("ZSORCE", bad.Message, StringComparison.Ordinal);
        // An old-AVR unit takes a library stabiliser through the same route (SEXS + PSS2A).
        PsseDyrResult sexs = ReadExtended("     1 'GENROU' 1 " + Genrou + " /\n     1 'SEXS' 1  0.1  10.0  100.0  0.05  0.0  5.0 /\n"
            + "     1 'PSS2A' 1  1  0  3  0  5  1  2.0  2.0  0.0  2.0  0.0  2.0  0.4  1.0  0.5  0.1  10.0  0.15  0.03  0.15  0.03  0.1 -0.1 /\n", raw);
        RmsExciter made = Assert.Single(LoadJson(sexs, raw.Model).Entries).Exciter!.Create();
        Assert.Equal("SEXS", made.Kind);
        Assert.Equal("PSS2A", made.LibraryStabiliser!.Kind);
        Assert.True(made.NeedsTerminalQuantities);
    }

    private static (GridModel Model, PowerFlowResult Pf, List<SubtransientInit.MachineData> Data, List<RmsExciter?> Exciters) Case14Fleet(PowerFlowResult? pf = null)
    {
        PsseParseResult raw = Raw14();
        MachineDynamicsSet set = LoadJson(ReadExtended(ExtendedDyr, raw), raw.Model);
        GridModel m = raw.Model;
        // Solved to 1e-13, not the default 1e-8: a 1e-8 pu mismatch is a real departure from equilibrium that the AVRs answer
        // (E'q moves 1e-11 from the held-field run within 1 s), so a flat-start check needs the power flow solved to rounding.
        pf ??= new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-13 });
        return (m, pf, m.Generators.Select((_, k) => set.For(k)!.Data).ToList(), m.Generators.Select((_, k) => set.For(k)!.Exciter?.Create()).ToList());
    }

    [Fact]
    public void The_case14_dyr_fleet_starts_at_rest_and_rides_through_a_fault()
    {
        (GridModel m, PowerFlowResult pf, List<SubtransientInit.MachineData> data, List<RmsExciter?> ex) = Case14Fleet();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0, exciters: ex);
        // Against the held-field run of the same data: its own drift comes from the power flow's residual mismatch, not the
        // models; the exciters add at most RestBound (see there).
        MultiMachineSubtransient.Result held = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0);
        for (int i = 0; i < still.Machines.Count; i++)
            for (int k = 0; k < still.Machines[i].SpeedPu.Count; k++)
            {
                Assert.True(Math.Abs(still.Machines[i].SpeedPu[k] - held.Machines[i].SpeedPu[k]) <= RestBound(1000), $"machine {i} speed {still.Machines[i].SpeedPu[k]:R} against the held {held.Machines[i].SpeedPu[k]:R}");
                Assert.True(Math.Abs(still.Machines[i].EqpPu[k] - held.Machines[i].EqpPu[k]) <= RestBound(1000), $"machine {i} E'q {still.Machines[i].EqpPu[k] - held.Machines[i].EqpPu[k]:R} from the held field");
            }
        (_, _, _, List<RmsExciter?> ex2) = Case14Fleet(pf);
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(m, pf, data, 4, onsetSeconds: 0.1, clearingSeconds: 0.05,
            stepSeconds: 0.005, durationSeconds: 3.0, exciters: ex2);
        Assert.True(fault.StayedInSynchronism);
        Assert.Contains(fault.Machines, t => Math.Abs(t.SpeedPu[^1]) > 1e-6);
    }

    // ------------------------------------------------------------------------------------------- 5. determinism

    [Fact]
    public void Dense_and_sparse_power_flow_repeated_and_parallel_runs_give_the_same_bits()
    {
        PsseParseResult raw = Raw14();
        PowerFlowResult dense = new NewtonRaphsonPowerFlow().Solve(raw.Model), sparse = new SparseNewtonRaphsonPowerFlow().Solve(raw.Model);
        string Go(PowerFlowResult pf)
        {
            (GridModel m, _, List<SubtransientInit.MachineData> data, List<RmsExciter?> ex) = Case14Fleet(pf);
            return Digest(RmsTransientStability.Study(m, pf, data, 4, 0.1, 0.05, stepSeconds: 0.005, durationSeconds: 1.5, exciters: ex));
        }
        string want = Go(dense);
        Assert.Equal(want, Go(sparse));
        Assert.Equal(want, Go(dense));
        var par = new string[4];
        Parallel.For(0, par.Length, i => par[i] = Go(i % 2 == 0 ? dense : sparse));
        Assert.All(par, d => Assert.Equal(want, d));
    }

    /// <summary>The built CLI in the three SIMD modes the release gate runs (default, DOTNET_EnableAVX2=0,
    /// DOTNET_EnableHWIntrinsic=0) gives the same machine rows for the case14 DYR fleet read with --dyr-extended.</summary>
    [Fact]
    public void The_cli_dyr_extended_run_is_the_same_bits_in_every_simd_mode()
    {
        string root = TestTemp.Under("gridsim-wp3a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rawPath = Path.Combine(root, "case14.raw"), dyrPath = Path.Combine(root, "case14.dyr");
            File.WriteAllText(rawPath, Case14Raw());
            File.WriteAllText(dyrPath, ExtendedDyr);
            CliProcess.Result plain = CliProcess.RunBuilt("stability", rawPath, "--rms", "--dyn-data", dyrPath);
            Assert.Equal(1, plain.ExitCode);
            Assert.Contains("models this reader does not map", plain.StdErr, StringComparison.Ordinal);
            var modes = new (string Name, Dictionary<string, string?> Env)[]
            {
                ("default", new Dictionary<string, string?>()),
                ("noavx2", new Dictionary<string, string?> { ["DOTNET_EnableAVX2"] = "0" }),
                ("nohw", new Dictionary<string, string?> { ["DOTNET_EnableHWIntrinsic"] = "0" }),
            };
            string? want = null;
            foreach ((string name, Dictionary<string, string?> env) in modes)
            {
                string outDir = Path.Combine(root, name);
                CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "stability", rawPath, "--rms", "--dyn-data", dyrPath, "--dyr-extended",
                    "--events", "fault:4@0.1,clear:4@0.15", "--events-out", outDir);
                Assert.True(r.ExitCode == 0, r.ToString());
                string rows = string.Join("\n", File.ReadAllLines(Path.Combine(outDir, RmsEventsExport.MachinesCsv)).Where(l => !l.StartsWith('#')));
                want ??= rows;
                Assert.Equal(want, rows);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
