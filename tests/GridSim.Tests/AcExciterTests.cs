using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The alternator-rectifier exciters EXAC1 and ESAC1A (<see cref="AcExciter"/>), checked against hand oracles and a
/// reduction identity rather than a second copy of the code:
/// <list type="bullet">
/// <item>the IEEE 421.5 rectifier regulation function FEX and the quadratic saturation fit at known points, by hand;</item>
/// <item>the initial exciter voltage VE reproduces the field through VE FEX(KC IFD / VE) on each of the three FEX
/// branches;</item>
/// <item>linear regime (KC = KD = KF = 0, no saturation, KE = 1, no lead-lag): a held error step drives two cascaded
/// exact zero-order-hold lags, whose discrete closed form is checked to 1e-13;</item>
/// <item>the full model settles where VE (KE + SE(VE)) + KD IFD = KA Verr and Efd = VE FEX(KC IFD / VE), solved here by
/// bisection;</item>
/// <item>ESAC1A's VE floor of 0 and VR limits;</item>
/// <item>in the RMS kernel, a reduced EXAC1 (KC = KD = 0, no saturation, KE = 1) is IEEET1 with KE = 1 and no saturation
/// operation for operation, so a whole case 9 study (undisturbed and faulted) must give the same bits with either: the
/// field current path changes nothing it should not.</item>
/// </list>
/// </summary>
public class AcExciterTests
{
    private static void Close(double expected, double actual, string what, double tol = 1e-13)
        => Assert.True(Math.Abs(expected - actual) <= tol * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    private static AcExciter Exac1(double kc = 0.2, double kd = 0.4, double kf = 0.03, double ke = 1.0, double se1 = 0.1, double se2 = 0.3,
        double tb = 0.0, double tc = 0.0, double ka = 200, double ta = 0.02, double vrMin = -10, double vrMax = 10)
        => new(AcExciterVariant.Exac1, tc, tb, ka, ta, vrMin, vrMax, double.NegativeInfinity, double.PositiveInfinity,
            0.8, kf, 1.0, kc, kd, ke, 4.0, se1, 5.3, se2);

    [Fact]
    public void Fex_and_the_quadratic_saturation_fit_match_hand_values()
    {
        Assert.Equal(1.0, AcExciter.Fex(-0.1));
        Assert.Equal(1.0 - 0.577 * 0.2, AcExciter.Fex(0.2));
        Assert.Equal(Math.Sqrt(0.75 - 0.36), AcExciter.Fex(0.6));
        Assert.Equal(1.732 * (1.0 - 0.9), AcExciter.Fex(0.9));
        Assert.Equal(0.0, AcExciter.Fex(1.2));

        // SE(E) = B (E - A)^2 / E passes through both points.
        (double a, double b) = AcExciter.SaturationFit("t", 4.0, 0.1, 5.3, 0.3);
        Close(0.1, b * (4.0 - a) * (4.0 - a) / 4.0, "SE(E1)", 1e-14);
        Close(0.3, b * (5.3 - a) * (5.3 - a) / 5.3, "SE(E2)", 1e-14);
        Assert.Equal((0.0, 0.0), AcExciter.SaturationFit("t", 4.0, 0.0, 5.3, 0.0));
        Assert.Throws<ArgumentException>(() => AcExciter.SaturationFit("t", 5.3, 0.1, 4.0, 0.3));
        Assert.Throws<ArgumentException>(() => AcExciter.SaturationFit("t", 4.0, 0.3, 5.3, 0.1));
    }

    [Theory]
    [InlineData(2.0, 0.2)]    // IN = 0.4 / 2.23 = 0.18: first branch
    [InlineData(1.0, 0.6)]    // second branch
    [InlineData(0.3, 1.5)]    // third branch
    public void The_initial_exciter_voltage_reproduces_the_field_on_every_fex_branch(double efd, double kc)
    {
        AcExciter x = Exac1(kc: kc, kd: 0.0, se1: 0, se2: 0, vrMin: -100, vrMax: 100);
        x.Initialize(efd);
        double ve = x.ExciterVoltage;
        double iN = kc * efd / ve;
        double fex = iN <= 0.433 ? 1 - 0.577 * iN : iN <= 0.75 ? Math.Sqrt(0.75 - iN * iN) : 1.732 * (1 - iN);
        Close(efd, ve * fex, "VE FEX(IN) gives the field back", 1e-15);
    }

    [Fact]
    public void A_linear_exac1_follows_the_closed_form_of_its_two_cascaded_lags()
    {
        double ka = 50, ta = 0.05, te = 0.8, dt = 0.005, efd0 = 1.8, step = 0.002;
        var x = new AcExciter(AcExciterVariant.Exac1, 0, 0, ka, ta, -50, 50, double.NegativeInfinity, double.PositiveInfinity,
            te, 0, 1.0, 0, 0, 1.0, 0, 0, 0, 0);
        x.Initialize(efd0);
        Assert.Equal(efd0 / ka, x.EquilibriumErrorPu(efd0));
        double c = efd0 + ka * step, d = efd0 - c;
        double a1 = DetMath.Exp(-dt / ta), a2 = DetMath.Exp(-dt / te);
        double A = (1 - a2) * d * a1 / (a1 - a2), B = efd0 - c - A;
        for (int n = 1; n <= 600; n++)
            Close(c + A * Math.Pow(a1, n) + B * Math.Pow(a2, n), x.Step(efd0 / ka + step, efd0, dt), $"Efd at step {n}");
    }

    [Fact]
    public void The_full_exac1_is_stationary_and_settles_on_the_hand_solved_operating_point()
    {
        AcExciter x = Exac1();
        double efd0 = 2.2;
        x.Initialize(efd0);
        double verr0 = x.EquilibriumErrorPu(efd0);
        for (int n = 0; n < 1000; n++) Close(efd0, x.Step(verr0, efd0, 0.005), $"stationary at step {n}", 1e-13);

        // A new held error and field current: the settled point solves VE KE + B (VE - A)^2 + KD IFD = KA Verr.
        double verr = verr0 + 0.001, ifd = 2.4, efd = 0;
        for (int n = 0; n < 40000; n++) efd = x.Step(verr, ifd, 0.005);
        (double sa, double sb) = AcExciter.SaturationFit("t", 4.0, 0.1, 5.3, 0.3);
        double target = 200 * verr - 0.4 * ifd;
        double lo = 0, hi = 50;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            double f = mid * 1.0 + (mid > sa ? sb * (mid - sa) * (mid - sa) : 0) - target;
            if (f > 0) hi = mid; else lo = mid;
        }
        double ve = 0.5 * (lo + hi), iN = 0.2 * ifd / ve;
        Assert.True(iN <= 0.433, "the operating point is on the first FEX branch");
        Close(ve * (1 - 0.577 * iN), efd, "settled field", 1e-10);
        Close(ve, x.ExciterVoltage, "settled exciter voltage", 1e-10);
    }

    [Fact]
    public void Esac1a_holds_ve_at_its_floor_and_clamps_vr()
    {
        var x = new AcExciter(AcExciterVariant.Esac1a, 0, 0, 400, 0.02, -50, 50, -6, 6, 0.8, 0, 1.0, 0.2, 0.4, 1.0, 0, 0, 0, 0);
        x.Initialize(1.5);
        double efd = 1;
        for (int n = 0; n < 4000; n++) { efd = x.Step(-1.0, 1.5, 0.005); Assert.True(x.ExciterVoltage >= 0); }
        Assert.Equal(0.0, x.ExciterVoltage);
        Assert.Equal(0.0, efd);

        // A large positive error: VA reaches 50 but VR is clamped at 6, so VFE settles at 6: VE + 0.4 IFD = 6.
        var y = new AcExciter(AcExciterVariant.Esac1a, 0, 0, 400, 0.02, -50, 50, -6, 6, 0.8, 0, 1.0, 0.0, 0.4, 1.0, 0, 0, 0, 0);
        y.Initialize(1.5);
        for (int n = 0; n < 20000; n++) y.Step(1.0, 1.5, 0.005);
        Close(6 - 0.4 * 1.5, y.ExciterVoltage, "VE on the VR limit", 1e-10);
    }

    [Fact]
    public void Bad_ac_exciter_data_and_unreachable_starts_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Exac1(ka: 0));
        Assert.Throws<ArgumentException>(() => Exac1(tb: 0, tc: 0.5));
        Assert.Throws<ArgumentException>(() => new AcExciter(AcExciterVariant.Exac1, 0, 0, 200, 0.02, -10, 10, -5, 5, 0.8, 0, 1, 0, 0, 1, 0, 0, 0, 0));
        // VR needed at the start above VRMAX.
        Assert.Throws<ArgumentOutOfRangeException>(() => Exac1(vrMax: 2.0).Initialize(2.5));
        // A field the rectifier cannot deliver: KC IFD above VE on every branch is impossible for a positive field? Efd 0.
        Assert.Throws<ArgumentOutOfRangeException>(() => Exac1().Initialize(0.0));
    }

    private static string SchemasDir => Path.Combine(CliProcess.Root(), "schemas", "v2");

    private static string Fixture => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json");

    [Fact]
    public void A_reduced_exac1_is_ieeet1_in_the_rms_kernel_to_the_bit()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Fixture, SchemasDir, model);
        var data = model.Generators.Select((g, k) => set.For(k)!.Data).ToList();

        // IEEET1 with KE = 1, no saturation; EXAC1 with the same KA, TA, VR limits, TE, KF, TF and KC = KD = 0, KE = 1.
        List<RmsExciter?> Ieeet1() => model.Generators.Select(_ => (RmsExciter?)RmsExciter.FromIeeet1(50.0, 0.02, 1.0, 0.2, 0.05, 1.0, -10.0, 10.0)).ToList();
        List<RmsExciter?> Exac1Reduced() => model.Generators.Select(_ => (RmsExciter?)RmsExciter.FromAc(
            new AcExciter(AcExciterVariant.Exac1, 0, 0, 50.0, 0.02, -10.0, 10.0, double.NegativeInfinity, double.PositiveInfinity,
                0.2, 0.05, 1.0, 0.0, 0.0, 1.0, 0, 0, 0, 0))).ToList();

        foreach ((double onset, double duration) in new[] { (100.0, 3.0), (0.1, 2.0) })
        {
            MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: onset, clearingSeconds: 0.08,
                stepSeconds: 0.005, durationSeconds: duration, exciters: Ieeet1());
            MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: onset, clearingSeconds: 0.08,
                stepSeconds: 0.005, durationSeconds: duration, exciters: Exac1Reduced());
            Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
            for (int m = 0; m < a.Machines.Count; m++)
            {
                Assert.Equal(a.Machines[m].SpeedPu.Select(BitConverter.DoubleToInt64Bits), b.Machines[m].SpeedPu.Select(BitConverter.DoubleToInt64Bits));
                Assert.Equal(a.Machines[m].DeltaRad.Select(BitConverter.DoubleToInt64Bits), b.Machines[m].DeltaRad.Select(BitConverter.DoubleToInt64Bits));
            }
        }
    }

    [Fact]
    public void A_full_exac1_holds_case9_still_undisturbed_and_moves_under_a_fault()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        MachineDynamicsSet set = MachineDynamicsLoader.Load(Fixture, SchemasDir, model);
        var data = model.Generators.Select((g, k) => set.For(k)!.Data).ToList();
        List<RmsExciter?> Full() => model.Generators.Select(_ => (RmsExciter?)RmsExciter.FromAc(Exac1(vrMin: -20, vrMax: 20))).ToList();
        MultiMachineSubtransient.Result still = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 100.0, clearingSeconds: 0.1,
            stepSeconds: 0.005, durationSeconds: 5.0, exciters: Full());
        Assert.True(still.StayedInSynchronism);
        foreach (MultiMachineSubtransient.MachineTrace m in still.Machines)
            Assert.True(Math.Abs(m.SpeedPu[^1]) < 1e-7, $"speed drifted to {m.SpeedPu[^1]:R}");
        MultiMachineSubtransient.Result fault = RmsTransientStability.Study(model, pf, data, 7, onsetSeconds: 0.1, clearingSeconds: 0.08,
            stepSeconds: 0.005, durationSeconds: 2.0, exciters: Full());
        Assert.True(fault.Machines.Any(m => Math.Abs(m.SpeedPu[^1]) > 1e-6), "a fault must move the fleet");
    }

    [Fact]
    public void A_dyr_exac1_record_maps_value_by_value_and_a_transducer_lag_is_refused()
    {
        PsseParseResult raw = PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        const string machine = "     6 'GENROU' 1   7.0  0.03  0.50  0.05  3.0  0.0  1.60  1.55  0.25  0.40  0.18  0.12  0.0  0.0 /";
        string dyr = machine + "\n     6 'EXAC1' 1   0.0  0.1  0.5  400.0  0.02  9.0  -5.4  0.8  0.03  1.0  0.2  0.38  1.0  4.18  0.1  3.14  0.03 /";
        PsseDyrResult r = PsseDyrReader.Read(dyr, raw, "case14", new PsseDyrOptions { SourceName = "exac1.dyr" });
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-exac1-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "exac1.dynamics.json");
            File.WriteAllText(path, r.Json);
            // E1 4.18 above E2 3.14: the saturation fit refuses it, naming EXAC1 (the values arrived where the DYR put them).
            var ex = Assert.Throws<InvalidDataException>(() => MachineDynamicsLoader.Load(path, SchemasDir, raw.Model));
            Assert.Contains("EXAC1: the saturation points need E2 above E1 (got E1 4.18, E2 3.14)", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }

        string good = machine + "\n     6 'EXAC1' 1   0.0  0.1  0.5  400.0  0.02  9.0  -5.4  0.8  0.03  1.0  0.2  0.38  1.0  3.14  0.03  4.18  0.1 /";
        PsseDyrResult g = PsseDyrReader.Read(good, raw, "case14", new PsseDyrOptions { SourceName = "exac1.dyr" });
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "exac1.dynamics.json");
            File.WriteAllText(path, g.Json);
            MachineDynamicsEntry e = Assert.Single(MachineDynamicsLoader.Load(path, SchemasDir, raw.Model).Entries);
            Assert.Equal("EXAC1", e.Exciter!.Model);
            IReadOnlyDictionary<string, double> p = e.Exciter.Params;
            Assert.Equal((0.1, 0.5, 400.0, 0.02, 9.0, -5.4), (p["acLagS"], p["acLeadS"], p["acRegulatorGain"], p["acRegulatorTimeS"], p["acRegulatorMaxPu"], p["acRegulatorMinPu"]));
            Assert.Equal((0.8, 0.03, 1.0, 0.2, 0.38, 1.0), (p["acExciterTimeS"], p["acFeedbackGain"], p["acFeedbackTimeS"], p["acRectifierKc"], p["acDemagnetisingKd"], p["acExciterConstant"]));
            Assert.Equal((3.14, 0.03, 4.18, 0.1), (p["acSatE1"], p["acSatSe1"], p["acSatE2"], p["acSatSe2"]));
            Assert.True(e.Exciter.Create().NeedsFieldCurrent);
        }
        finally { Directory.Delete(dir, true); }

        string withTr = machine + "\n     6 'EXAC1' 1   0.02  0.1  0.5  400.0  0.02  9.0  -5.4  0.8  0.03  1.0  0.2  0.38  1.0  3.14  0.03  4.18  0.1 /";
        var trEx = Assert.Throws<InvalidDataException>(() => PsseDyrReader.Read(withTr, raw, "case14"));
        Assert.Contains("EXAC1 for machine '1' at bus 6 (line 2): a voltage transducer lag TR", trEx.Message, StringComparison.Ordinal);
    }
}
