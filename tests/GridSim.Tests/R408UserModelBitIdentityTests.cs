using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Stochastic;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>
/// R4-08, the proof: declarative models are BIT-IDENTICAL to the coded models they re-express. Each library model is
/// driven with the same input sequence as its coded twin (a step, a ramp, a large step that drives the limits, a pinned
/// random sequence from the R4-02 counter stream, and quiet stretches) for 10^5 steps at three step sizes; every output bit
/// is compared with <see cref="BitConverter.DoubleToInt64Bits"/>. Then whole models: the declarative governors and
/// exciters replace the coded ones in the RMS study on IEEE 9 and in the dynamics study on the 14-bus fixture; every trace
/// and every CCT bracket is bit-equal to the incumbent's. The oracle is the incumbent, whose own tests pin it.
/// </summary>
public sealed class R408UserModelBitIdentityTests
{
    private const int Steps = 100_000;
    private static readonly double[] StepSizes = { 0.001, 0.005, 0.02 };

    // ---------------------------------------------------------------------------------------------------------------
    // The pinned input sequence: eight segments of 12 500 steps.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Input k of channel <paramref name="channel"/> at amplitude <paramref name="a"/>: quiet, +step, -step (large:
    /// drives limits), ramp, random, small random, triangle, quiet.</summary>
    private static double[] Sequence(int channel, double a)
    {
        var s = new CounterStream(new StreamKey(0x5EED0408UL, "r4-08/seq/" + channel));
        var x = new double[Steps];
        const int seg = Steps / 8;
        for (int k = 0; k < Steps; k++)
        {
            int g = k / seg, j = k % seg;
            double u = s.Uniform((ulong)k);
            x[k] = g switch
            {
                0 => 0.0,
                1 => 0.3 * a,
                2 => -a,
                3 => a * (2.0 * j / seg - 1.0),
                4 => a * (2.0 * u - 1.0),
                5 => 0.1 * a * (2.0 * u - 1.0),
                6 => a * (1.0 - Math.Abs(4.0 * j / seg - 2.0)),
                _ => 0.0,
            };
        }
        return x;
    }

    private static void Same(double coded, double declared, string what, int k, double dt)
        => Assert.True(BitConverter.DoubleToInt64Bits(coded) == BitConverter.DoubleToInt64Bits(declared),
            $"{what}: step {k} at dt={dt}: coded {coded:R} ({BitConverter.DoubleToInt64Bits(coded):X16}) vs declarative {declared:R} ({BitConverter.DoubleToInt64Bits(declared):X16})");

    private static Dictionary<string, double> P(params (string, double)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    // ---------------------------------------------------------------------------------------------------------------
    // Unit level: one model, one sequence, three step sizes.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tgov1_declarative_is_bit_identical_to_the_coded_model_at_three_step_sizes()
    {
        double[] speed = Sequence(0, 0.05);
        UserModel um = UserModelLibrary.Load("TGOV1", P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.02), ("Vmin", 0.0), ("Vmax", 1.2)));
        foreach (double dt in StepSizes)
        {
            var coded = new Tgov1(0.05, 0.5, 1.0, 5.0, 0.02, 0.0, 1.2);
            coded.Initialize(0.8);
            ModelInstance decl = um.NewInstance();
            decl.Initialize(0.8);
            bool hitMax = false;
            for (int k = 0; k < Steps; k++)
            {
                double a = coded.Step(speed[k], dt);
                double b = decl.Step(speed[k], dt);
                Same(a, b, "TGOV1 Pmech", k, dt);
                hitMax |= coded.ValvePosition >= 1.2 - 1e-9;
            }
            Assert.True(hitMax, "the sequence must drive the valve to its upper limit, so the clamp is exercised");
        }
    }

    [Fact]
    public void Sexs_declarative_is_bit_identical_including_the_equilibrium_error()
    {
        double[] verr = Sequence(1, 0.2);
        UserModel um = UserModelLibrary.Load("SEXS", P(("K", 100.0), ("Ta", 0.1), ("Tb", 1.0), ("Te", 0.05), ("Emin", -5.0), ("Emax", 5.0)));
        foreach (double dt in StepSizes)
        {
            var coded = new Sexs(0.1, 1.0, 100.0, 0.05, -5.0, 5.0);
            coded.Initialize(1.5);
            ModelInstance decl = um.NewInstance();
            decl.Initialize(1.5);
            Same(1.5 / 100.0, decl.EquilibriumError, "SEXS equilibrium error", 0, dt);
            bool clamped = false;
            for (int k = 0; k < Steps; k++)
            {
                double a = coded.Step(verr[k], dt);
                double b = decl.Step(verr[k], 0.0, dt);
                Same(a, b, "SEXS Efd", k, dt);
                clamped |= Math.Abs(a) >= 5.0 - 1e-12;
            }
            Assert.True(clamped, "the field limits must be exercised");
        }
    }

    [Fact]
    public void Pss1a_declarative_is_bit_identical()
    {
        double[] u = Sequence(2, 0.05);
        UserModel um = UserModelLibrary.Load("PSS1A", P(("Ks", 10.0), ("Tw", 10.0), ("T1", 0.15), ("T2", 0.05), ("T3", 0.15), ("T4", 0.05), ("Vsmin", -0.1), ("Vsmax", 0.1)));
        foreach (double dt in StepSizes)
        {
            var coded = new Pss1a(10.0, 10.0, 0.15, 0.05, 0.15, 0.05, -0.1, 0.1);
            coded.Initialize(0.002);
            ModelInstance decl = um.NewInstance();
            decl.Initialize(0.002);
            bool limited = false;
            for (int k = 0; k < Steps; k++)
            {
                double a = coded.Step(u[k], dt);
                double b = decl.Step(u[k], dt);
                Same(a, b, "PSS1A Vs", k, dt);
                limited |= Math.Abs(a) >= 0.1 - 1e-15;
            }
            Assert.True(limited, "the stabiliser output limit must be exercised");
        }
    }

    [Theory]
    [InlineData(0.0123, 1.2, 1.0)]   // saturated exciter, Ke = 1
    [InlineData(0.0, 0.0, 0.0)]      // Ke = 0 and Se = 0: the pole sits at the origin, the integrator branch
    public void Ieeet1_declarative_is_bit_identical_including_saturation_and_the_pole_at_the_origin(double aex, double bex, double ke)
    {
        double[] verr = Sequence(3, 0.05);
        double efd0 = ke == 0.0 ? 1.0 : 2.0;
        UserModel um = UserModelLibrary.Load("IEEET1", P(("Ka", 400.0), ("Ta", 0.04), ("Ke", ke), ("Te", 0.8), ("Kf", 0.03), ("Tf", 1.0),
            ("Vrmin", -7.3), ("Vrmax", 7.3), ("Aex", aex), ("Bex", bex)));
        foreach (double dt in StepSizes)
        {
            var coded = new Ieeet1(400.0, 0.04, ke, 0.8, 0.03, 1.0, -7.3, 7.3, aex, bex);
            ModelInstance decl = um.NewInstance();
            if (ke == 0.0)
            {
                // Vr = (Ke + Se) Efd = 0 at equilibrium: both start there.
                coded.Initialize(efd0);
                decl.Initialize(efd0);
            }
            else { coded.Initialize(efd0); decl.Initialize(efd0); }
            double vr0 = (ke + aex * Core_Exp(bex * efd0)) * efd0;
            Same(vr0 / 400.0, decl.EquilibriumError, "IEEET1 equilibrium error", 0, dt);
            for (int k = 0; k < Steps; k++)
            {
                double a = coded.Step(verr[k], dt);
                double b = decl.Step(verr[k], 0.0, dt);
                Same(a, b, "IEEET1 Efd", k, dt);
            }
        }
    }

    // The same exponential the coded model uses (DetMath), so the expected equilibrium error is computed independently of the interpreter.
    private static double Core_Exp(double x) => GridSim.Core.Numerics.DetMath.Exp(x);

    [Fact]
    public void Gast_declarative_is_bit_identical_with_the_temperature_limit_gate_active()
    {
        double[] speed = Sequence(4, 0.03);
        UserModel um = UserModelLibrary.Load("GAST", P(("R", 0.05), ("T1", 0.4), ("T2", 0.1), ("T3", 3.0), ("AT", 1.0), ("KT", 2.0), ("Vmin", 0.0), ("Vmax", 1.2), ("Dturb", 0.01)));
        foreach (double dt in StepSizes)
        {
            var coded = new Gast(0.05, 0.4, 0.1, 3.0, 1.0, 2.0, 0.0, 1.2, 0.01);
            coded.Initialize(0.8);
            ModelInstance decl = um.NewInstance();
            decl.Initialize(0.8);
            bool gated = false;
            for (int k = 0; k < Steps; k++)
            {
                gated |= coded.TemperatureLimit < 0.8 - speed[k] / 0.05;   // the low-value gate picks the temperature limit
                double a = coded.Step(speed[k], dt);
                double b = decl.Step(speed[k], dt);
                Same(a, b, "GAST Pmech", k, dt);
            }
            Assert.True(gated);
        }
    }

    [Fact]
    public void Ieeeg1_declarative_is_bit_identical_hp_plus_lp_shaft_power()
    {
        double[] speed = Sequence(5, 0.03);
        var ps = P(("R", 0.05), ("T1", 0.2), ("T2", 0.1), ("T3", 0.3), ("T4", 0.2), ("T5", 7.0), ("T6", 0.4), ("T7", 0.4),
            ("K1", 0.25), ("K2", 0.05), ("K3", 0.35), ("K4", 0.05), ("K5", 0.2), ("K6", 0.03), ("K7", 0.05), ("K8", 0.02), ("Pmin", 0.0), ("Pmax", 3.0));
        UserModel um = UserModelLibrary.Load("IEEEG1", ps);
        foreach (double dt in StepSizes)
        {
            var coded = new Ieeeg1(0.05, 0.1, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, k1: 0.25, k3: 0.35, k5: 0.2, k7: 0.05,
                valveMinPu: 0.0, valveMaxPu: 3.0, k2: 0.05, k4: 0.05, k6: 0.03, k8: 0.02);
            coded.Initialize(0.8);
            ModelInstance decl = um.NewInstance();
            decl.Initialize(0.8);
            for (int k = 0; k < Steps; k++)
            {
                double hp = coded.Step(speed[k], dt);
                double a = hp + coded.LowPressureShaftPowerPu;   // the study wiring (Ieeeg1Data.Tandem)
                double b = decl.Step(speed[k], dt);
                Same(a, b, "IEEEG1 Pmech", k, dt);
            }
        }
    }

    [Fact]
    public void Sexs_with_pss1a_declarative_is_bit_identical_to_the_RmsExciter_wiring()
    {
        double[] verr = Sequence(6, 0.2), speed = Sequence(7, 0.05);
        UserModel um = UserModelLibrary.Load("SEXS_PSS1A");
        foreach (double dt in StepSizes)
        {
            RmsExciter coded = RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, -5.0, 5.0,
                new Pss1a(10.0, 10.0, 0.15, 0.05, 0.15, 0.05, -0.1, 0.1));
            RmsExciter decl = RmsExciter.FromUser(new UserModelFieldController(um), "USER:SEXS_PSS1A");
            coded.Initialize(1.5);
            decl.Initialize(1.5);
            Same(coded.EquilibriumErrorPu(1.5), decl.EquilibriumErrorPu(1.5), "equilibrium error", 0, dt);
            for (int k = 0; k < Steps; k++)
                Same(coded.Step(verr[k], speed[k], dt), decl.Step(verr[k], speed[k], dt), "SEXS+PSS1A Efd", k, dt);
        }
    }

    [Fact]
    public void Repeat_runs_of_a_declarative_model_give_the_same_bits()
    {
        double[] speed = Sequence(8, 0.05);
        UserModel um = UserModelLibrary.Load("TGOV1");
        var first = new double[20_000];
        for (int pass = 0; pass < 2; pass++)
        {
            ModelInstance m = um.NewInstance();
            m.Initialize(0.8);
            for (int k = 0; k < first.Length; k++)
            {
                double v = m.Step(speed[k], 0.005);
                if (pass == 0) first[k] = v; else Same(first[k], v, "repeat", k, 0.005);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Whole models on the RMS study (IEEE 9).
    // ---------------------------------------------------------------------------------------------------------------

    private static (GridModel Model, PowerFlowResult Pf) Case9()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        return (model, pf);
    }

    private static List<SubtransientInit.MachineData> Data(GridModel model, Func<int, GovernorData?> governor)
    {
        var data = new List<SubtransientInit.MachineData>();
        for (int k = 0; k < model.Generators.Count; k++)
            data.Add(new SubtransientInit.MachineData(H: model.Generators[k].H, Damping: 0.0,
                Xd: 1.0, Xdp: 0.30, Xdpp: 0.22, Xq: 0.65, Xqp: 0.40, Xqpp: 0.22,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.40, Tqopp: 0.05, Governor: governor(k)));
        return data;
    }

    private static void AssertRunsIdentical(MultiMachineSubtransient.Result a, MultiMachineSubtransient.Result b)
    {
        Assert.Equal(a.StayedInSynchronism, b.StayedInSynchronism);
        Assert.Equal(a.Machines.Count, b.Machines.Count);
        for (int i = 0; i < a.Machines.Count; i++)
        {
            MultiMachineSubtransient.MachineTrace x = a.Machines[i], y = b.Machines[i];
            Assert.Equal(x.DeltaRad.Count, y.DeltaRad.Count);
            for (int k = 0; k < x.DeltaRad.Count; k++)
            {
                Same(x.DeltaRad[k], y.DeltaRad[k], $"machine {i} angle", k, 0);
                Same(x.SpeedPu[k], y.SpeedPu[k], $"machine {i} speed", k, 0);
                Same(x.EqpPu[k], y.EqpPu[k], $"machine {i} Eq'", k, 0);
                Same(x.EdpPu[k], y.EdpPu[k], $"machine {i} Ed'", k, 0);
            }
        }
    }

    [Fact]
    public void Whole_model_declarative_governors_give_the_incumbents_study_trace_bit_for_bit()
    {
        (GridModel model, PowerFlowResult pf) = Case9();
        // Machine 0 TGOV1, machine 1 GAST, machine 2 IEEEG1 (no rate limits): each declarative twin from the same numbers.
        var tgov = new Tgov1Data(0.05, 0.5, 1.0, 5.0, TurbineDampingPu: 0.0, ValveMinPu: 0.0, ValveMaxPu: 3.0);
        var gast = new GastData(0.05, 0.4, 0.1, 3.0, AmbientLimitPu: 3.0, TemperatureGainPu: 2.0, ValveMinPu: 0.0, ValveMaxPu: 3.0, TurbineDampingPu: 0.0);
        var g1 = new Ieeeg1Data(0.05, 0.1, 0.2, 0.3, 0.2, 7.0, 0.4, 0.4, K1: 0.3, K3: 0.4, K5: 0.3, ValveMaxPu: 3.0);
        GovernorData[] coded = { tgov, gast, g1 };
        GovernorData[] declared =
        {
            new UserModelGovernorData(UserModelLibrary.Load("TGOV1", P(("R", 0.05), ("T1", 0.5), ("T2", 1.0), ("T3", 5.0), ("Dt", 0.0), ("Vmin", 0.0), ("Vmax", 3.0)))),
            new UserModelGovernorData(UserModelLibrary.Load("GAST", P(("R", 0.05), ("T1", 0.4), ("T2", 0.1), ("T3", 3.0), ("AT", 3.0), ("KT", 2.0), ("Vmin", 0.0), ("Vmax", 3.0), ("Dturb", 0.0)))),
            new UserModelGovernorData(UserModelLibrary.Load("IEEEG1", P(("R", 0.05), ("T1", 0.2), ("T2", 0.1), ("T3", 0.3), ("T4", 0.2), ("T5", 7.0), ("T6", 0.4), ("T7", 0.4),
                ("K1", 0.3), ("K2", 0.0), ("K3", 0.4), ("K4", 0.0), ("K5", 0.3), ("K6", 0.0), ("K7", 0.0), ("K8", 0.0), ("Pmin", 0.0), ("Pmax", 3.0)))),
        };
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, Data(model, k => coded[k]), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 6.0);
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, Data(model, k => declared[k]), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 6.0);
        AssertRunsIdentical(a, b);
        // The governors acted: the governed fleet differs from the ungoverned one (so the identity is not vacuous).
        MultiMachineSubtransient.Result plain = RmsTransientStability.Study(model, pf, Data(model, _ => null), 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 6.0);
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(plain.Machines[0].SpeedPu[^1]), BitConverter.DoubleToInt64Bits(a.Machines[0].SpeedPu[^1]));
    }

    [Fact]
    public void Whole_model_declarative_exciters_give_the_incumbents_study_trace_and_cct_bit_for_bit()
    {
        (GridModel model, PowerFlowResult pf) = Case9();
        List<SubtransientInit.MachineData> data = Data(model, _ => null);
        UserModel sexs = UserModelLibrary.Load("SEXS", P(("K", 100.0), ("Ta", 0.1), ("Tb", 1.0), ("Te", 0.05), ("Emin", 0.0), ("Emax", 8.0)));
        UserModel st1 = UserModelLibrary.Load("IEEET1", P(("Ka", 50.0), ("Ta", 0.02), ("Ke", 1.0), ("Te", 0.2), ("Kf", 0.05), ("Tf", 1.0), ("Vrmin", -10.0), ("Vrmax", 10.0), ("Aex", 0.0), ("Bex", 0.0)));
        UserModel sp = UserModelLibrary.Load("SEXS_PSS1A", P(("K", 100.0), ("Ta", 0.1), ("Tb", 1.0), ("Te", 0.05), ("Emin", 0.0), ("Emax", 8.0),
            ("Ks", 20.0), ("Tw", 10.0), ("T1", 0.15), ("T2", 0.05), ("T3", 0.15), ("T4", 0.05), ("Vsmin", -0.1), ("Vsmax", 0.1)));
        List<RmsExciter?> Coded() => new()
        {
            RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0),
            RmsExciter.FromIeeet1(50.0, 0.02, 1.0, 0.2, 0.05, 1.0, -10.0, 10.0),
            RmsExciter.FromSexs(0.1, 1.0, 100.0, 0.05, 0.0, 8.0, new Pss1a(20.0, 10.0, 0.15, 0.05, 0.15, 0.05, -0.1, 0.1)),
        };
        List<RmsExciter?> Declared() => new()
        {
            RmsExciter.FromUser(new UserModelFieldController(sexs), "USER:SEXS"),
            RmsExciter.FromUser(new UserModelFieldController(st1), "USER:IEEET1"),
            RmsExciter.FromUser(new UserModelFieldController(sp), "USER:SEXS_PSS1A"),
        };
        MultiMachineSubtransient.Result a = RmsTransientStability.Study(model, pf, data, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0, exciters: Coded());
        MultiMachineSubtransient.Result b = RmsTransientStability.Study(model, pf, data, 7, 0.1, 0.08, stepSeconds: 0.005, durationSeconds: 5.0, exciters: Declared());
        AssertRunsIdentical(a, b);

        double cctCoded = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, 7, stepSeconds: 0.005, iterations: 10, exciters: Coded());
        double cctDecl = RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, 7, stepSeconds: 0.005, iterations: 10, exciters: Declared());
        Same(cctCoded, cctDecl, "critical clearing time", 0, 0.005);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Whole study: the dynamics study runner on the 14-bus fixture, declarative TGOV1 and SEXS in place of the DYR ones.
    // ---------------------------------------------------------------------------------------------------------------

    private static DynamicsStudyRequest Case14Request(IReadOnlyList<UserModelAssignment>? users)
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        var cct = new DynamicsCaseRequest("cct-4", DynamicsCaseKind.CriticalClearing,
            Cct: new DynamicsCctRequest(4, new[] { Br(4, 5) }, 0.1, 0.4));
        return Request(raw, Text(Study("case14-study.dyr")), new[] { Fault4("fault-4"), cct }) with { UserModels = users };
    }

    [Fact]
    public void Dynamics_study_with_declarative_tgov1_and_sexs_is_bit_identical_to_the_dyr_models_and_the_coverage_names_them()
    {
        PsseParseResult raw = Raw(Psse("case14.raw"));
        // The DYR file's own TGOV1 and SEXS numbers, as the incumbent loader derived them.
        PsseDyrResult dyr = PsseDyrReader.Read(Text(Study("case14-study.dyr")), raw, "case14", new PsseDyrOptions { SourceName = "case14.dyr" });
        MachineDynamicsSet set = DynamicsModelCoverage.BindDocument(dyr.Json, "case14.dyr", Schemas, raw.Model);
        int g1 = Enumerable.Range(0, raw.Model.Generators.Count).First(k => raw.Model.Generators[k].Bus == 1);
        MachineDynamicsEntry e1 = set.For(g1)!;
        var tg = Assert.IsType<Tgov1Data>(e1.Data.Governor);
        IReadOnlyDictionary<string, double> ex = e1.Exciter!.Params;
        UserModel userGov = UserModelLibrary.Load("TGOV1", P(("R", tg.DroopPu), ("T1", tg.ValveTimeSeconds), ("T2", tg.ReheatLeadSeconds), ("T3", tg.ReheatLagSeconds),
            ("Dt", tg.TurbineDampingPu), ("Vmin", tg.ValveMinPu), ("Vmax", tg.ValveMaxPu)));
        UserModel userExc = UserModelLibrary.Load("SEXS", P(("K", ex["sexsGain"]), ("Ta", ex["sexsLeadS"]), ("Tb", ex["sexsLagS"]), ("Te", ex["sexsExciterTimeS"]),
            ("Emin", ex.GetValueOrDefault("sexsFieldMinPu", double.NegativeInfinity)), ("Emax", ex.GetValueOrDefault("sexsFieldMaxPu", double.PositiveInfinity))));

        DynamicsStudyResult baseline = DynamicsStudyRunner.Run(Case14Request(null));
        var users = new[]
        {
            new UserModelAssignment(M(1), userGov),
            new UserModelAssignment(M(1), userExc),
        };
        DynamicsStudyResult declared = DynamicsStudyRunner.Run(Case14Request(users));

        Assert.Equal(baseline.Status, declared.Status);
        Assert.Equal(baseline.Cases.Count, declared.Cases.Count);
        // Events row: every channel, every sample, every bit.
        DynamicsRunEvidence ra = baseline.Cases[0].Run!, rb = declared.Cases[0].Run!;
        Assert.Equal(ra.Samples, rb.Samples);
        Assert.Equal(ra.StayedInSynchronism, rb.StayedInSynchronism);
        for (int c = 0; c < ra.Channels.Count; c++)
        {
            Assert.Equal(ra.Channels[c].Label, rb.Channels[c].Label);
            for (int k = 0; k < ra.Channels[c].Values.Count; k++)
                Same(ra.Channels[c].Values[k], rb.Channels[c].Values[k], ra.Channels[c].Label, k, 0.005);
        }
        // CCT row: the bracket (searched with the same controllers re-initialised each trial).
        Assert.NotNull(baseline.Cases[1].Cct);
        Assert.Equal(baseline.Cases[1].Cct!.Coarse, declared.Cases[1].Cct!.Coarse);
        Assert.Equal(baseline.Cases[1].Disposition, declared.Cases[1].Disposition);

        // The coverage report: the records bound to user models are listed with their own status and hash.
        DyrRecordCoverage tgRec = declared.Coverage.Records.Single(r => r.Model == "TGOV1");
        DyrRecordCoverage sxRec = declared.Coverage.Records.Single(r => r.Model == "SEXS");
        Assert.Equal($"SUPPORTED (USER MODEL, sha256:{userGov.Hash})", tgRec.Status);
        Assert.Equal($"SUPPORTED (USER MODEL, sha256:{userExc.Hash})", sxRec.Status);
        Assert.Contains(declared.Coverage.ReaderNotes, n => n.Contains(userGov.Hash, StringComparison.Ordinal) && n.StartsWith("user model TGOV1", StringComparison.Ordinal));
        Assert.Equal("COMPLETE", declared.Coverage.Status);
        DynamicsUnitCoverage u1 = declared.Coverage.Units.Single(u => u.Bus == 1);
        Assert.StartsWith("USER:TGOV1 (sha256:", u1.Governor, StringComparison.Ordinal);
        Assert.StartsWith("USER:SEXS (sha256:", u1.Exciter, StringComparison.Ordinal);
        // The baseline's records are untouched.
        Assert.All(baseline.Coverage.Records, r => Assert.Equal(DyrRecordStatus.Supported, r.Status));
    }

    [Fact]
    public void Negative_control_a_different_user_parameter_changes_the_study_so_the_identity_is_not_vacuous()
    {
        DynamicsStudyResult baseline = DynamicsStudyRunner.Run(Case14Request(null));
        // A TGOV1 with a different droop and a SEXS with a different gain, bound to bus 1: the traces must move.
        var users = new[]
        {
            new UserModelAssignment(M(1), UserModelLibrary.Load("TGOV1", P(("R", 0.02), ("T1", 0.49), ("T2", 2.1), ("T3", 7.0), ("Dt", 0.0), ("Vmin", 0.0), ("Vmax", 2.5)))),
            new UserModelAssignment(M(1), UserModelLibrary.Load("SEXS", P(("K", 30.0), ("Ta", 1.0), ("Tb", 10.0), ("Te", 0.05), ("Emin", 0.0), ("Emax", 6.0)))),
        };
        DynamicsStudyResult changed = DynamicsStudyRunner.Run(Case14Request(users));
        DynamicsChannelTrace a = baseline.Cases[0].Run!.Channels.First(c => c.Label.StartsWith("speed 1", StringComparison.Ordinal));
        DynamicsChannelTrace b = changed.Cases[0].Run!.Channels.First(c => c.Label.StartsWith("speed 1", StringComparison.Ordinal));
        Assert.Contains(Enumerable.Range(0, a.Values.Count), k => BitConverter.DoubleToInt64Bits(a.Values[k]) != BitConverter.DoubleToInt64Bits(b.Values[k]));
    }

    [Fact]
    public void A_user_exciter_that_would_silently_drop_a_coded_stabiliser_is_refused()
    {
        // Bus 2 carries IEEET1 + PSS1A in the fixture: an exciter model there must declare that it replaces the stabiliser.
        UserModel userExc = UserModelLibrary.Load("SEXS");
        var ex = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(Case14Request(new[] { new UserModelAssignment(M(2), userExc) })));
        Assert.Contains("PSS1A", ex.Message, StringComparison.Ordinal);
        Assert.Contains("machine '1' at bus 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_model_for_a_machine_the_case_does_not_hold_is_refused_by_identity()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DynamicsStudyRunner.Run(Case14Request(new[] { new UserModelAssignment(M(9), UserModelLibrary.Load("TGOV1")) })));
        Assert.Contains("bus 9", ex.Message, StringComparison.Ordinal);
    }
}
