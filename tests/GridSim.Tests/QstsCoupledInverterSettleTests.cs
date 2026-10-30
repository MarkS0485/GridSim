using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Many strongly coupled volt-var inverters (the shape of a national case with embedded PV at every demand bus, here on
/// a small synthetic feeder so the test is fast). The per-unit damped loop cannot settle this set: every unit's factor is
/// halved on each round its change grows and never recovers, so the units creep. After the per-unit loop fails its cap
/// the engine switches to the accelerated iteration over all units together; the step must then settle on the curves,
/// with the same bits on every run, thread, Newton engine and SIMD tier.
/// <para>
/// Every existing QSTS test settles inside the per-unit loop's cap, so none of them reaches the switch; their pinned
/// digests (<see cref="QstsEngineTests"/>) are the proof that the per-unit path is unchanged bit for bit.
/// </para>
/// </summary>
public sealed class QstsCoupledInverterSettleTests
{
    private static readonly PowerFlowOptions Opt = new();

    private const int Units = 20;
    private const double UnitMw = 0.6;

    /// <summary>
    /// A radial 11 kV feeder on a 10 MVA base: slack bus 1 at 1.0 pu, then buses 2 to <paramref name="n"/> + 1 in a chain
    /// (R = 0.01, X = 0.02 pu per section, rated 20 MVA), each with 0.1 MW + 0.03 MVAr of load.
    /// </summary>
    internal static GridModel CoupledFeeder(int n = Units)
    {
        var buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 11, Vm: 1.0) };
        for (int i = 2; i <= n + 1; i++) buses.Add(new Bus(i, BusType.Pq, Pd: 0.1, Qd: 0.03, BaseKv: 11));
        var branches = new List<Branch>();
        for (int i = 1; i <= n; i++) branches.Add(new Branch(i, i + 1, 0.01, 0.02, RateAMva: 20, Name: $"L{i}"));
        return new GridModel
        {
            BaseMva = 10, Name = "qsts-coupled-feeder", Buses = buses, Branches = branches,
            Generators = new List<Generator> { new(1, 0, Vg: 1.0) },
        };
    }

    /// <summary>A Category B volt-var PV unit of <paramref name="mw"/> MW (inverter 1.1 x, var priority) at every load
    /// bus, full sun, one 60 s step (step 0, so no response lag: the target is the curve itself).</summary>
    internal static QstsStudy CoupledStudy(int n = Units, double mw = UnitMw, int maxContinuous = 200, QstsStorageUnit[]? storage = null)
    {
        var pv = new QstsPvUnit[n];
        for (int i = 0; i < n; i++) pv[i] = new QstsPvUnit($"PV{i + 2}", i + 2, mw, 1.1 * mw, null, new InverterSettings(InverterFunction.VoltVar));
        return new QstsStudy
        {
            StepSeconds = 60, Steps = 1, Pv = pv, MaxContinuousIterations = maxContinuous,
            Storage = storage ?? Array.Empty<QstsStorageUnit>(),
        };
    }

    /// <summary>The engine's own settle test, recomputed from the recorded state: every unit's target at the solved
    /// voltage of its bus is within ContinuousTolerance x rating of its set point, in P and in Q. The target is the
    /// Category B curve x rating, then the var-priority apparent-power limit (Q within S, P within sqrt(S^2 - Q^2)).</summary>
    private static void AssertOnCurves(QstsResult q, QstsStudy study)
    {
        QstsStepRecord s = q.Steps[0];
        double tol = study.ContinuousTolerance;
        for (int u = 0; u < study.Pv.Count; u++)
        {
            QstsPvUnit pv = study.Pv[u];
            double v = s.Vm![pv.Bus - 1];
            double qt = Math.Clamp(VoltVarCurve.Ieee1547CategoryB.At(v) * pv.RatedMva, -pv.RatedMva, pv.RatedMva);
            double pt = pv.RatedMw * pv.RatedMw + qt * qt <= pv.RatedMva * pv.RatedMva
                ? pv.RatedMw : Math.Min(pv.RatedMw, Math.Sqrt(Math.Max(0.0, pv.RatedMva * pv.RatedMva - qt * qt)));
            Assert.True(Math.Abs(s.PvQ[u] - qt) <= tol * pv.RatedMva, $"{pv.Name}: Q {s.PvQ[u]} vs curve {qt} at {v} pu");
            Assert.True(Math.Abs(s.PvP[u] - pt) <= tol * pv.RatedMva, $"{pv.Name}: P {s.PvP[u]} vs limit {pt}");
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(1000)]
    public void Coupled_volt_var_units_settle_on_their_curves_after_the_per_unit_loop_fails_its_cap(int cap)
    {
        QstsStudy study = CoupledStudy(maxContinuous: cap);
        QstsResult q = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(CoupledFeeder(), study);
        Assert.True(q.Completed);
        QstsStepRecord s = q.Steps[0];
        // More continuous rounds than the cap: the per-unit loop ran its full cap without settling (the stall), and the
        // accelerated iteration then settled the step. With the cap at 1000 the per-unit loop still had not settled,
        // which is the creep the switch exists for.
        Assert.True(s.ContinuousIterations > cap, $"continuous rounds {s.ContinuousIterations}, cap {cap}");
        Assert.True(s.ContinuousIterations <= 2 * cap, $"continuous rounds {s.ContinuousIterations}, cap {cap}");
        Assert.True(s.Settled, $"settled {s.Settled}, continuous rounds {s.ContinuousIterations}");
        Assert.True(s.VmaxPu > 1.02, $"export must push the feeder onto the absorbing slope (vmax {s.VmaxPu})");
        Assert.True(s.PvMvar < 0, $"the units absorb (Q {s.PvMvar})");
        AssertOnCurves(q, study);
        // A fixed point does not depend on how the per-unit loop wandered first: both caps land within the tolerance.
        if (cap == 1000)
        {
            QstsResult q200 = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(CoupledFeeder(), CoupledStudy(maxContinuous: 200));
            for (int u = 0; u < Units; u++)
                Assert.True(Math.Abs(q.Steps[0].PvQ[u] - q200.Steps[0].PvQ[u]) <= 1e-5 * 1.1 * UnitMw,
                    $"unit {u}: Q {q.Steps[0].PvQ[u]} (cap 1000) vs {q200.Steps[0].PvQ[u]} (cap 200)");
        }
    }

    [Fact]
    public void The_accelerated_settle_has_the_same_bits_across_repetitions_threads_and_newton_engines()
    {
        GridModel m = CoupledFeeder();
        QstsStudy study = CoupledStudy();
        string d0 = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(m, study).Digest();
        Assert.Equal(d0, new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(m, study).Digest());
        Assert.Equal(d0, new QstsEngine(new NewtonRaphsonPowerFlow(), Opt).Run(m, study).Digest());
        var digests = new string[8];
        Parallel.For(0, digests.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => digests[i] = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(m, study).Digest());
        Assert.All(digests, d => Assert.Equal(d0, d));
    }

    // The coupled run digested over every number, pinned so the slices require the same bits in every SIMD mode they run
    // in (default, DOTNET_EnableHWIntrinsic=0, DOTNET_EnableAVX2=0).
    internal const string PinnedCoupledDigestNewton = "621221dbe30460a9f2c195efe8b8dddfe3488bfe1084abcbf4b00e6957d8df53";
    internal const string PinnedCoupledDigestSweep = "026dda68bb76cd6d28745d189325f1ba5276b9076ff469c0fd7d330d108919f4";

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("sweep")]
    public void The_coupled_digest_is_pinned_for_every_simd_tier(string engine)
    {
        IPowerFlowSolver solver = engine switch
        {
            "dense" => new NewtonRaphsonPowerFlow(),
            "sparse" => new SparseNewtonRaphsonPowerFlow(),
            _ => new BackwardForwardSweep(),
        };
        QstsResult q = new QstsEngine(solver, Opt).Run(CoupledFeeder(), CoupledStudy());
        Assert.True(q.Completed && q.Steps[0].Settled && q.Steps[0].ContinuousIterations > 200);
        string digest = q.Digest(), pinned = engine == "sweep" ? PinnedCoupledDigestSweep : PinnedCoupledDigestNewton;
        Assert.True(digest == pinned, $"{engine}: digest {digest}, pinned {pinned}");
    }

    [Fact]
    public void A_self_consumption_battery_among_the_coupled_units_settles_on_its_request()
    {
        // A 0.5 MW / 1 MWh battery at bus 11 charging from that bus's PV surplus (self-consumption): its request
        // (bus load - bus PV) moves with the PV unit's var-priority P limit, so it iterates with the inverters.
        var battery = new QstsStorageUnit("B11", 11, 0.5, 1.0, 0.5, 0.1, 0.9, 0.95, 0.95, StorageMode.SelfConsumption);
        QstsStudy study = CoupledStudy(storage: new[] { battery });
        QstsResult q = new QstsEngine(new SparseNewtonRaphsonPowerFlow(), Opt).Run(CoupledFeeder(), study);
        Assert.True(q.Completed);
        QstsStepRecord s = q.Steps[0];
        Assert.True(s.Settled && s.ContinuousIterations > 200, $"settled {s.Settled}, continuous rounds {s.ContinuousIterations}");
        AssertOnCurves(q, study);
        // Bus 11 is PV unit index 9; the request is load minus PV, a charge here, clamped to the rating and the energy
        // room over the step (0.4 MWh to SoC max at 0.95 charge efficiency over 60 s is far above 0.5 MW).
        double request = Math.Clamp(0.1 - s.PvP[9], -0.5, 0.5);
        Assert.True(request < 0, $"the bus exports, so the battery charges (request {request})");
        Assert.True(Math.Abs(s.StorageP[0] - request) <= study.ContinuousTolerance * 0.5, $"battery {s.StorageP[0]} vs request {request}");
    }
}
