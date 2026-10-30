using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The QSTS engine (<see cref="QstsEngine"/>): a one-step run is the single power flow bit for bit (dense, sparse and
/// sweep engines); a one-step run in steady mode is the existing tap loop / shunt loop bit for bit; the timed framework
/// with zero delays reproduces the same loops at a single step; warm starts are exactly GridScenario's; and runs are
/// bit-identical across repetitions and threads.
/// </summary>
public sealed class QstsEngineTests
{
    internal static readonly PowerFlowOptions Opt = new();

    /// <summary>
    /// A radial test feeder on a 10 MVA base: slack bus 1 (33 kV), the grid transformer T1 (bus 1 to bus 2, tapped on
    /// its From end, X = 0.08 pu) with an OLTC holding bus 2 at <paramref name="target"/> +/- <paramref name="deadband"/>,
    /// and an 11 kV feeder 2-3-4-5-6 (R = 0.03, X = 0.04 pu per section, rated 8 MVA) with 1 MW + 0.3 MVAr at each of
    /// buses 3 to 6.
    /// </summary>
    internal static GridModel Feeder(double target = 1.03, double deadband = 0.005, bool oltc = true, double loadMw = 1.0)
    {
        var buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 33, Vm: 1.0) };
        buses.Add(new Bus(2, BusType.Pq, BaseKv: 11));
        for (int i = 3; i <= 6; i++) buses.Add(new Bus(i, BusType.Pq, Pd: loadMw, Qd: 0.3 * loadMw, BaseKv: 11));
        var branches = new List<Branch> { new(1, 2, 0.005, 0.08, RateAMva: 12, Name: "T1", IsTransformer: true) };
        for (int i = 2; i < 6; i++) branches.Add(new Branch(i, i + 1, 0.03, 0.04, RateAMva: 8, Name: $"L{i}{i + 1}"));
        return new GridModel
        {
            BaseMva = 10, Name = "qsts-feeder", Buses = buses, Branches = branches,
            Generators = new List<Generator> { new(1, 0, Vg: 1.0) },
            Oltcs = oltc ? new List<Oltc> { new("T1", 2, TargetVpu: target, DeadbandPu: deadband) } : Array.Empty<Oltc>(),
        };
    }

    /// <summary>The feeder of the day-long scenarios: bus 2 held at 1.02 +/- 0.004 pu, so the daily load and PV swing move
    /// the tap.</summary>
    internal static GridModel DayFeeder() => Feeder(target: 1.02, deadband: 0.004);

    internal static IPowerFlowSolver[] Engines() => new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow(), new BackwardForwardSweep() };

    private static IPowerFlowSolver EngineByName(string name) => name switch
    {
        "dense" => new NewtonRaphsonPowerFlow(),
        "sparse" => new SparseNewtonRaphsonPowerFlow(),
        _ => new BackwardForwardSweep(),
    };

    [Theory]
    [InlineData("dense", "feeder")]
    [InlineData("sparse", "feeder")]
    [InlineData("sweep", "feeder")]
    [InlineData("dense", "case14")]
    [InlineData("sparse", "case30")]
    public void One_step_with_nothing_varying_is_the_single_power_flow_bit_for_bit(string engine, string which)
    {
        GridModel m = which == "feeder" ? Feeder(oltc: false) : S10TestSupport.LoadMatpower($"lib/{which}.m").Model;
        PowerFlowResult single = EngineByName(engine).Solve(m, Opt);
        foreach (QstsControlMode mode in new[] { QstsControlMode.Timed, QstsControlMode.Steady })
        {
            QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Mode = mode, OltcEnabled = false });
            Assert.True(q.Completed);
            S10TestSupport.AssertBitIdentical($"{engine}/{which}/{mode}", single, q.LastResult!);
            Assert.Equal(1, q.Steps[0].Solves);
        }
    }

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void Steady_one_step_is_the_existing_tap_loop_bit_for_bit(string engine)
    {
        GridModel m = Feeder();
        var loop = new SteadyStateSolver(EngineByName(engine));
        PowerFlowResult expected = loop.Solve(m, Opt);
        Assert.True(loop.OltcPasses >= 2, $"the feeder must make the tap loop work (passes {loop.OltcPasses})");
        QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Mode = QstsControlMode.Steady });
        S10TestSupport.AssertBitIdentical($"steady taps {engine}", expected, q.LastResult!);
        Assert.Equal(loop.RegulatedModel.Branches[0].TapRatio, q.Steps[0].Taps[0]);
        Assert.Equal(loop.OltcPasses, q.TapOperations[0]);
    }

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void Timed_zero_delay_one_step_is_the_existing_tap_loop_bit_for_bit(string engine)
    {
        GridModel m = Feeder();
        var loop = new SteadyStateSolver(EngineByName(engine));
        PowerFlowResult expected = loop.Solve(m, Opt);
        QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(m, new QstsStudy { StepSeconds = 60, Steps = 1 });
        S10TestSupport.AssertBitIdentical($"timed taps {engine}", expected, q.LastResult!);
        Assert.Equal(loop.RegulatedModel.Branches[0].TapRatio, q.Steps[0].Taps[0]);
        Assert.Equal(loop.OltcPasses, q.TapOperations[0]);
        Assert.Equal(loop.OltcPasses, q.Steps[0].DiscreteIterations);
        Assert.Equal(loop.OltcPasses, q.Operations.Count);
        Assert.True(q.Steps[0].Settled);
    }

    private static QstsCapacitor Bank(string name, int bus, double step, int max, double on, double off, double delay = 0) =>
        new(name, bus, step, 0, max, 0, CapacitorControl.Voltage, on, off, delay);

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void Steady_one_step_with_banks_is_the_existing_tap_and_shunt_loops_bit_for_bit(string engine)
    {
        GridModel m = Feeder(target: 1.0, deadband: 0.01, loadMw: 1.6);
        QstsCapacitor[] caps = { Bank("C6", 6, 0.3, 4, 0.985, 1.04), Bank("C4", 4, 0.2, 3, 0.99, 1.04) };
        var banks = caps.Select(c => new SwitchedShunt(c.Bus, c.StepMvar, c.MinSteps, c.MaxSteps, c.InitialSteps, c.OnValue, c.OffValue, c.Name)).ToArray();
        var shunt = new SwitchedShuntPowerFlow(EngineByName(engine), banks);
        var loop = new SteadyStateSolver(shunt);
        PowerFlowResult expected = loop.Solve(m, Opt);
        Assert.True(shunt.LastSteps.Sum() > 0, "the banks must switch in this case");
        QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Mode = QstsControlMode.Steady, Capacitors = caps });
        S10TestSupport.AssertBitIdentical($"steady banks {engine}", expected, q.LastResult!);
        Assert.Equal(shunt.LastSteps, q.Steps[0].CapacitorSteps);
    }

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void Timed_zero_delay_one_step_with_banks_only_is_the_existing_shunt_loop_bit_for_bit(string engine)
    {
        GridModel m = Feeder(oltc: false, loadMw: 1.6);
        QstsCapacitor[] caps = { Bank("C6", 6, 0.3, 4, 0.97, 1.04), Bank("C4", 4, 0.2, 3, 0.975, 1.04) };
        var banks = caps.Select(c => new SwitchedShunt(c.Bus, c.StepMvar, c.MinSteps, c.MaxSteps, c.InitialSteps, c.OnValue, c.OffValue, c.Name)).ToArray();
        var shunt = new SwitchedShuntPowerFlow(EngineByName(engine), banks);
        PowerFlowResult expected = shunt.Solve(m, Opt);
        Assert.True(shunt.LastSteps.Sum() >= 2, $"the banks must switch more than once (steps {string.Join(",", shunt.LastSteps)})");
        QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(m, new QstsStudy { StepSeconds = 60, Steps = 1, Capacitors = caps });
        S10TestSupport.AssertBitIdentical($"timed banks {engine}", expected, q.LastResult!);
        Assert.Equal(shunt.LastSteps, q.Steps[0].CapacitorSteps);
        Assert.Equal(shunt.LastPasses, q.Steps[0].Solves);
        Assert.Equal(shunt.LastSteps.Sum(), q.TotalCapacitorOperations);
    }

    [Fact]
    public void A_warm_started_step_is_GridScenario_warm_start_bit_for_bit()
    {
        GridModel m = Feeder(oltc: false);
        var prof = new QstsProfile("p", 60, new[] { 1.0, 1.25, 0.8 });
        var study = new QstsStudy { StepSeconds = 60, Steps = 3, Profiles = new[] { prof }, Loads = new[] { new QstsLoadBinding(null, "p") } };
        foreach (IPowerFlowSolver engine in Engines())
        {
            QstsResult q = new QstsEngine(engine, Opt).Run(m, study);
            // By hand: each step scales Pd and Qd and warm-starts from the previous converged result.
            PowerFlowResult? prev = null;
            PowerFlowResult? last = null;
            for (int k = 0; k < 3; k++)
            {
                double f = prof.Values[k];
                GridModel mk = m.CopyWith(buses: m.Buses.Select(b => b.Pd == 0 && b.Qd == 0 ? b : b with { Pd = b.Pd * f, Qd = b.Qd * f }).ToList());
                if (prev is not null) mk = GridScenario.WithSolvedVoltages(mk, prev);
                last = engine.Solve(mk, Opt);
                for (int i = 0; i < last.Vm.Length; i++) Assert.Equal(last.Vm[i], q.Steps[k].Vm![i]);
                Assert.Equal(last.TotalLossMw, q.Steps[k].LossMw);
                prev = last;
            }
            S10TestSupport.AssertBitIdentical("warm start", last!, q.LastResult!);
        }
    }

    [Fact]
    public void Generation_following_load_is_the_chain_verbs_AtLoad_re_dispatch_bit_for_bit()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case118.m").Model;
        var prof = new QstsProfile("p", 900, new[] { 0.7, 0.85, 1.1 });
        var study = new QstsStudy
        {
            StepSeconds = 900, Steps = 3, Profiles = new[] { prof }, Loads = new[] { new QstsLoadBinding(null, "p") }, GenerationFollowsLoad = true,
        };
        QstsResult q = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(m, study);
        Assert.True(q.Completed);
        PowerFlowResult? prev = null;
        for (int k = 0; k < 3; k++)
        {
            GridModel mk = GridScenario.AtLoad(m, prof.Values[k]);
            if (prev is not null) mk = GridScenario.WithSolvedVoltages(mk, prev);
            PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(mk, Opt);
            for (int i = 0; i < r.Vm.Length; i++) Assert.Equal(r.Vm[i], q.Steps[k].Vm![i]);
            Assert.Equal(r.TotalLossMw, q.Steps[k].LossMw);
            prev = r;
        }
        S10TestSupport.AssertBitIdentical("AtLoad chain", prev!, q.LastResult!);
    }

    [Fact]
    public void Profiles_read_exactly_by_hold_and_linear_interpolation()
    {
        var hold = new QstsProfile("h", 1800, new[] { 0.5, 0.75, 1.0 });
        Assert.Equal(0.5, hold.ValueAt(0));
        Assert.Equal(0.5, hold.ValueAt(1799));
        Assert.Equal(0.75, hold.ValueAt(1800));
        Assert.Equal(1.0, hold.ValueAt(5399));
        Assert.Throws<InvalidOperationException>(() => hold.ValueAt(5400));
        var lin = hold with { Interpolation = QstsInterpolation.Linear };
        Assert.Equal(0.5, lin.ValueAt(0));
        Assert.Equal(0.625, lin.ValueAt(900));    // 0.5 + 0.5 * 0.25: exact in binary
        Assert.Equal(0.75, lin.ValueAt(1800));
        Assert.Equal(1.0, lin.ValueAt(3600));
        Assert.Throws<InvalidOperationException>(() => lin.ValueAt(3601));
        var wrap = hold with { Wrap = true };
        Assert.Equal(0.5, wrap.ValueAt(5400));
        Assert.Equal(0.75, wrap.ValueAt(5400 + 1800));
    }

    /// <summary>A day of 15-minute steps on the feeder with a load profile, PV with volt-var, a bank and a timed OLTC:
    /// the shared scenario of the determinism tests.</summary>
    internal static QstsStudy DayStudy(int stepSeconds = 900)
    {
        int n = 86400 / stepSeconds;
        var load = new double[96]; var solar = new double[96];
        for (int i = 0; i < 96; i++)
        {
            double h = i / 4.0;
            load[i] = LoadProfile.DailyFactor(h);
            double x = (h - 6.0) / 12.0;
            solar[i] = x <= 0 || x >= 1 ? 0.0 : DetMath.Sin(Math.PI * x);
        }
        return new QstsStudy
        {
            StepSeconds = stepSeconds, Steps = n,
            Profiles = new[] { new QstsProfile("load", 900, load, Wrap: true), new QstsProfile("solar", 900, solar, Wrap: true) },
            Loads = new[] { new QstsLoadBinding(null, "load") },
            Pv = new[] { new QstsPvUnit("PV5", 5, 2.5, 2.75, "solar", new InverterSettings(InverterFunction.VoltVar, ResponseSeconds: 5)) },
            Capacitors = new[] { new QstsCapacitor("C6", 6, 0.3, 0, 2, 0, CapacitorControl.Voltage, 0.98, 1.04, DelaySeconds: 60, OffDelaySeconds: 60, DeadTimeSeconds: 300) },
            OltcDefault = new OltcTiming(DelaySeconds: 30, InterTapSeconds: 10),
        };
    }

    [Fact]
    public void Runs_are_bit_identical_across_repetitions_and_threads()
    {
        GridModel m = DayFeeder();
        QstsStudy study = DayStudy();
        string d0 = new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, study).Digest();
        Assert.Equal(d0, new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, study).Digest());
        var digests = new string[8];
        Parallel.For(0, digests.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => digests[i] = new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, study).Digest());
        Assert.All(digests, d => Assert.Equal(d0, d));
    }

    [Fact]
    public void The_dense_and_sparse_engines_give_the_same_bits_over_a_day()
    {
        GridModel m = DayFeeder();
        QstsStudy study = DayStudy();
        QstsResult d = new QstsEngine(new NewtonRaphsonPowerFlow(), Opt).Run(m, study);
        QstsResult s = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(m, study);
        Assert.True(d.Completed && s.Completed);
        Assert.Equal(d.Digest(), s.Digest());
    }

    // The day on DayFeeder (load profile, volt-var PV, a timed bank, timed OLTC), digested over every number. Pinned so the
    // same bits are required in every SIMD mode the slices run in (default, DOTNET_EnableHWIntrinsic=0,
    // DOTNET_EnableAVX2=0): the digest is SHA-256 over the raw bits, so one ulp anywhere changes it.
    internal const string PinnedDayDigestNewton = "6115e66b9be103c2fcf4f64cfd8778cd587c89a482570a8828c7b2fdabebf274";
    internal const string PinnedDayDigestSweep = "42a25d41872eb552736a621d4b8eea856598fbce4665d89af9a8314bcecf0092";

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void The_day_digest_is_pinned_for_every_simd_tier(string engine)
    {
        QstsResult q = new QstsEngine(EngineByName(engine), Opt).Run(DayFeeder(), DayStudy());
        Assert.True(q.Completed);
        Assert.True(q.TotalTapOperations > 0 && q.TotalCapacitorOperations > 0, $"taps {q.TotalTapOperations} banks {q.TotalCapacitorOperations}");
        string digest = q.Digest(), pinned = engine == "sweep" ? PinnedDayDigestSweep : PinnedDayDigestNewton;
        Assert.True(digest == pinned, $"{engine}: digest {digest}, pinned {pinned}");
    }

    [Fact]
    public void A_refused_study_lists_every_problem()
    {
        GridModel m = Feeder();
        var bad = new QstsStudy
        {
            StepSeconds = 0, Steps = 0,
            Loads = new[] { new QstsLoadBinding(new[] { 99 }, "nope") },
            Pv = new[] { new QstsPvUnit("PV", 1, 2, 1, null, new InverterSettings(InverterFunction.VoltVar)) },
            OltcSettings = new Dictionary<string, OltcTiming> { ["T9"] = new OltcTiming() },
        };
        var ex = Assert.Throws<InvalidDataException>(() => new QstsEngine(new BackwardForwardSweep()).Run(m, bad));
        foreach (string part in new[] { "stepSeconds", "steps must", "profile 'nope'", "bus 99", "ratedMva (1) must be at least", "Slack bus", "'T9'" })
            Assert.Contains(part, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_does_not_converge_stops_the_run_and_is_never_success()
    {
        GridModel m = Feeder(oltc: false);
        var study = new QstsStudy
        {
            StepSeconds = 60, Steps = 3,
            Profiles = new[] { new QstsProfile("p", 60, new[] { 1.0, 40.0, 1.0 }) },
            Loads = new[] { new QstsLoadBinding(null, "p") },
        };
        QstsResult q = new QstsEngine(new NewtonRaphsonPowerFlow(), Opt).Run(m, study);
        Assert.False(q.Completed);
        Assert.Equal(1, q.FailedStep);
        Assert.Single(q.Steps);
        Assert.Contains("did not converge", q.FailureReason!, StringComparison.Ordinal);
    }
}
