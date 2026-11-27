using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Abstractions;
using GridSim.Core.Estimation;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP13: area interchange control (<see cref="AreaInterchangePowerFlow"/>) and remote voltage regulation
/// (<see cref="RemoteVoltagePowerFlow"/>), both opt-in outer loops around the unchanged Newton engines, and the PSS/E
/// options that feed them (<see cref="PsseReadOptions.KeepAreaInterchange"/>, <see cref="PsseReadOptions.RemoteRegulation"/>).
/// Every expected value is a closed form derived in the test (base 100 MVA); the bit-identity pins were taken from the
/// 2.1.105 build. No comparison with a PSS/E solve was made.
/// </summary>
public class Rc5Wp13AreaControlTests
{
    private static readonly PowerFlowOptions Opt = new() { MaxIterations = 100, EnforceQLimits = true };

    private static IPowerFlowSolver[] Engines() => new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() };

    private static int At(PowerFlowResult r, int bus) => r.BusIds.ToList().IndexOf(bus);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The spacing of doubles at <paramref name="x"/> (one ulp).</summary>
    private static double Ulp(double x) => Math.BitIncrement(Math.Abs(x)) - Math.Abs(x);

    // ---- networks ------------------------------------------------------------------------------------------------------

    /// <summary>Test 1 network: bus 1 slack (area 1, V 1.0); bus 2 PV (area 2) with G2 at 40 MW, V 1.0; bus 3 PQ (area 2)
    /// with 100 MW, 0 Mvar; lines 1-2 and 2-3 with X 0.1, R = B = 0.</summary>
    private static GridModel Lossless(double pmax2 = 200) => new()
    {
        Name = "wp13-lossless",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 100) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 999),
            new Generator(2, 40, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: pmax2),
        },
        Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(2, 3, 0, 0.1) },
    };

    /// <summary>Test 2 network: bus 1 slack (area 1); bus 2 PV (area 2) at V 1.0 with G2 (40 MW to start) and a 100 MW load;
    /// the tie 1-2 has R 0.01 and X 0.1.</summary>
    private static GridModel Lossy() => new()
    {
        Name = "wp13-lossy",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Pd: 100) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 999),
            new Generator(2, 40, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 200),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
    };

    /// <summary>Test 6 network: bus 1 slack (V 1.0); bus 2 PV with G2 at P 0 and Q limits +-999 (or Qmax given); bus 3 PQ with
    /// 50 MW and 20 Mvar; lines 1-2 and 2-3 with X 0.1.</summary>
    private static GridModel Chain(double qmax2 = 999) => new()
    {
        Name = "wp13-chain",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 50, Qd: 20) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 999),
            new Generator(2, 0, Qmin: -999, Qmax: qmax2, Pmin: 0, Pmax: 999),
        },
        Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(2, 3, 0, 0.1) },
    };

    private static readonly AreaInterchangeSpec Area2Lossless = new(2, 2, -30, 0.1, new[] { 2, 3 });
    private static readonly AreaMembership Area1 = new(1, new[] { 1 });

    // ---- 1. lossless interchange -----------------------------------------------------------------------------------------

    [Fact]
    public void T01_lossless_interchange_moves_the_swing_unit_by_the_error_and_settles_in_two_passes()
    {
        // Pass 1: Pg2 = 40, so area 2 exports 40 - 100 = -60 MW (the tie 1-2 metered at bus 1, outside area 2, carries 60 MW
        // into it). dP = PDES - E = -30 - (-60) = +30, so Pg2 = 70. Pass 2: E = -30 within PTOL 0.1: stop, 2 passes.
        // Then P_12 = sin(th1 - th2) / X = 0.3 pu with V = 1 at both ends, so th2 = -asin(0.03) rad.
        //
        // Bounds. Each solve stops with every bus's real-power residual within its MaxMismatch (pu). The tie is lossless
        // and the only branch into area 2, so summing the residuals of buses 2 and 3 gives P_12 = (100 - Pg) / 100 +
        // eps2 + eps3 in pu, |eps2|, |eps3| <= MaxMismatch. Pass 1 (Pg = 40, the plain solve r1 below): E = -100 P_12, so
        // the update gives Pg2 = 40 + (-30 - E) = 70 + 100 (eps2 + eps3), within 2 x 100 x r1.MaxMismatch MW of 70; the
        // three roundings of that update (E, -30 - E, 40 + ...) are each at most half an ulp of a value below 128, so
        // 4 ulp of 70 cover them. Pass 2 adds its own residuals to the tie: |P_12 - 30| and |E2 + 30| are within
        // 2 x 100 x (r1.MaxMismatch + r.MaxMismatch) MW + 4 ulp of 30. (The dense engine gives Pg2 69.99999999999999.)
        PowerFlowResult? first = null;
        AreaInterchangePowerFlow? firstLoop = null;
        foreach (IPowerFlowSolver engine in Engines())
        {
            PowerFlowResult r1 = engine.Solve(Lossless(), Opt);   // the loop's pass 1: the unchanged model, same engine
            Assert.True(r1.Converged);
            var loop = new AreaInterchangePowerFlow(engine, new[] { Area2Lossless }, reported: new[] { Area1 });
            PowerFlowResult r = loop.Solve(Lossless(), Opt);
            Assert.True(r.Converged);
            Assert.True(loop.Settled, loop.LastNote);
            Assert.Equal(2, loop.LastPasses);
            double pgBound = 2 * 100 * r1.MaxMismatch + 4 * Ulp(70.0);
            double tieBound = 2 * 100 * (r1.MaxMismatch + r.MaxMismatch) + 4 * Ulp(30.0);
            Assert.True(Math.Abs(loop.LastDispatchMw[1] - 70.0) <= pgBound, $"Pg2 {loop.LastDispatchMw[1]:R}, bound {pgBound:R}");
            Assert.True(Math.Abs(r.LineFlows[0].PFromMw - 30.0) <= tieBound, $"P12 {r.LineFlows[0].PFromMw:R}, bound {tieBound:R}");
            Assert.True(Math.Abs(r.VaDeg[At(r, 2)] * Math.PI / 180.0 - -Math.Asin(0.03)) <= 1e-9, $"th2 {r.VaDeg[At(r, 2)]:R} deg");
            AreaInterchangeOutcome a2 = loop.LastAreas.Single(o => o.Area == 2);
            Assert.Equal(AreaInterchangeStatus.Ok, a2.Status);
            Assert.True(Math.Abs(a2.ExportMw - -30.0) <= tieBound, $"export {a2.ExportMw:R}");
            AreaInterchangeOutcome a1 = loop.LastAreas.Single(o => o.Area == 1);
            Assert.Equal(AreaInterchangeStatus.Reported, a1.Status);
            Assert.True(Math.Abs(a1.ExportMw - 30.0) <= tieBound, $"export {a1.ExportMw:R}");   // lossless: area 1 exports what area 2 imports
            Assert.Equal(new[] { 1, 2 }, loop.LastAreas.Select(o => o.Area));
            if (first is null) { first = r; firstLoop = loop; continue; }
            S10TestSupport.AssertBitIdentical("wp13 lossless dense vs sparse", first, r);
            Assert.Equal(BitConverter.DoubleToInt64Bits(firstLoop!.LastDispatchMw[1]), BitConverter.DoubleToInt64Bits(loop.LastDispatchMw[1]));
        }
    }

    // ---- 2. tie loss borne by the non-metered area -----------------------------------------------------------------------

    [Fact]
    public void T02_the_tie_loss_is_borne_by_the_area_at_the_far_end_from_the_meter()
    {
        // y = g + jb = 1 / (0.01 + j0.1). Metered at bus 1 (outside area 2), E2 = -P_12, so at the solution P_12 = 0.3 pu:
        // g(1 - cos th) - b sin th = 0.3 gives th = 0.030258840895160986 rad (bisection to 1e-17, SciPy brentq), and
        // P_21 = g(1 - cos th) + b sin th = -0.29909353703508956 pu. Area 2's balance: Pg2 - 100 = 100 P_21, so
        // Pg2 = 70.09064629649104 MW, and the tie loss 100 (P_12 + P_21) = 0.0906462964910 MW falls on area 2.
        // Bound: the loop stops at |E - PDES| <= PTOL = 1e-6 MW; dPg2/dE = 1 + dLoss/dP (about 1.006), and the engine's own
        // mismatch is 1e-8 pu = 1e-6 MW, so Pg2 is within about 2.1e-6 MW. 1e-5 MW is asserted.
        var spec = new AreaInterchangeSpec(2, 2, -30, 1e-6, new[] { 2 });
        var loop = new AreaInterchangePowerFlow(new NewtonRaphsonPowerFlow(), new[] { spec });
        PowerFlowResult r = loop.Solve(Lossy(), Opt);
        Assert.True(loop.Settled, loop.LastNote);
        Assert.True(Math.Abs(loop.LastDispatchMw[1] - 70.09064629649104) <= 1e-5, $"Pg2 {loop.LastDispatchMw[1]:R}");
        Assert.True(Math.Abs(r.LineFlows[0].PLossMw - 0.0906462964910) <= 1e-5, $"loss {r.LineFlows[0].PLossMw:R}");

        // Metered at bus 2 (inside area 2): E2 = P_21 = -30 MW, so Pg2 = 70 MW and the loss falls on area 1.
        var at2 = new AreaInterchangePowerFlow(new NewtonRaphsonPowerFlow(), new[] { spec },
            new Dictionary<int, BranchEnd> { [0] = BranchEnd.To });
        PowerFlowResult r2 = at2.Solve(Lossy(), Opt);
        Assert.True(at2.Settled, at2.LastNote);
        Assert.True(Math.Abs(at2.LastDispatchMw[1] - 70.0) <= 1e-5, $"Pg2 {at2.LastDispatchMw[1]:R}");
        Assert.True(Math.Abs(r2.LineFlows[0].PToMw - -30.0) <= 1e-5);
    }

    // ---- 3. the last solve is a plain solve ------------------------------------------------------------------------------

    [Fact]
    public void T03_the_returned_result_is_the_plain_engine_solve_of_the_adjusted_model_bit_for_bit()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        var east = new AreaInterchangeSpec(2, 6, 10, 0.1, Enumerable.Range(6, 9).ToArray());
        var loop = new AreaInterchangePowerFlow(SolverSelector.CreateNewton(m), new[] { east }, reported: new[] { new AreaMembership(1, new[] { 1, 2, 3, 4, 5 }) });
        PowerFlowResult r = loop.Solve(m, Opt);
        Assert.True(r.Converged);
        Assert.True(loop.Settled, loop.LastNote);
        Assert.Equal(AreaInterchangeStatus.Ok, loop.LastAreas.Single(o => o.Area == 2).Status);
        Assert.True(loop.LastPasses >= 2);

        var gens = new Generator[m.Generators.Count];
        for (int i = 0; i < gens.Length; i++) gens[i] = m.Generators[i] with { Pg = loop.LastDispatchMw[i] };
        GridModel adjusted = m.CopyWith(generators: gens);
        PowerFlowResult plain = SolverSelector.CreateNewton(adjusted).Solve(adjusted, Opt);
        S10TestSupport.AssertBitIdentical("case14 area interchange vs plain solve", plain, r);
        // Only the swing bus's unit moved.
        for (int i = 0; i < gens.Length; i++)
            if (m.Generators[i].Bus != 6) Assert.Equal(BitConverter.DoubleToInt64Bits(m.Generators[i].Pg), BitConverter.DoubleToInt64Bits(loop.LastDispatchMw[i]));
    }

    // ---- 4. refusals -----------------------------------------------------------------------------------------------------

    [Fact]
    public void T04_refusals_name_the_reason()
    {
        var engine = new NewtonRaphsonPowerFlow();
        var slackArea = new AreaInterchangePowerFlow(engine, new[] { new AreaInterchangeSpec(1, 1, 0, 1, new[] { 1 }) });
        var ex = Assert.Throws<ArgumentException>(() => slackArea.Solve(Lossless(), Opt));
        Assert.Contains("the system slack absorbs the remainder; PSS/E needs ISW to be the system swing bus there", ex.Message, StringComparison.Ordinal);

        var noUnit = new AreaInterchangePowerFlow(engine, new[] { new AreaInterchangeSpec(2, 3, -30, 1, new[] { 2, 3 }) });
        ex = Assert.Throws<ArgumentException>(() => noUnit.Solve(Lossless(), Opt));
        Assert.Contains("the swing bus 3 has no in-service unit to adjust", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<ArgumentException>(() => new AreaInterchangePowerFlow(engine, new[] { Area2Lossless, Area2Lossless with { Buses = new[] { 4 } } }));
        Assert.Contains("area interchange: area 2 is given twice", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => new AreaInterchangePowerFlow(engine, new[] { Area2Lossless }, reported: new[] { new AreaMembership(1, new[] { 1, 3 }) }));
        Assert.Contains("bus 3 is in area 2 and area 1", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => new AreaInterchangePowerFlow(engine, new[] { new AreaInterchangeSpec(2, 1, -30, 1, new[] { 2, 3 }) }));
        Assert.Contains("the swing bus 1 is not one of the area's buses", ex.Message, StringComparison.Ordinal);

        // Remote regulation.
        ex = Assert.Throws<ArgumentException>(() => new RemoteVoltagePowerFlow(engine, new[] { new RemoteVoltageControl(2, 3, 1.0), new RemoteVoltageControl(1, 3, 1.0) }));
        Assert.Contains("bus 3 is regulated by two controlling buses (2 and 1); sharing one regulated bus (PSS/E RMPCT) is not modelled", ex.Message, StringComparison.Ordinal);
        var pvTarget = new RemoteVoltagePowerFlow(engine, new[] { new RemoteVoltageControl(2, 1, 1.0) });
        ex = Assert.Throws<ArgumentException>(() => pvTarget.Solve(Chain(), Opt));
        Assert.Contains("the regulated bus 1 is the slack, which holds its own voltage", ex.Message, StringComparison.Ordinal);
        GridModel split = Chain().CopyWith(buses: Chain().Buses.Append(new Bus(4, BusType.Pq)).ToArray());
        var otherIsland = new RemoteVoltagePowerFlow(engine, new[] { new RemoteVoltageControl(2, 4, 1.0) });
        ex = Assert.Throws<ArgumentException>(() => otherIsland.Solve(split, Opt));
        Assert.Contains("the regulated bus 4 is not in the controlling bus's island", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T04b_pf_refuses_area_interchange_with_the_distributed_slack()
    {
        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            string raw = Path.Combine(dir, "areas.raw");
            File.WriteAllText(raw, Raw(met: 1, pt2: 999));
            CliProcess.Result both = CliProcess.RunBuilt("pf", raw, "--area-control", "--distributed-slack", "pmax");
            Assert.Equal(2, both.ExitCode);
            Assert.Contains("pf: area interchange control and --distributed-slack both move unit real power; give one.", both.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 5. AT LIMIT -----------------------------------------------------------------------------------------------------

    [Fact]
    public void T05_a_swing_unit_at_its_limit_leaves_its_area_at_limit()
    {
        // Pass 1 asks for Pg2 = 70 but Pmax is 60: the unit is held at 60, the area is AT LIMIT and is re-solved once to
        // report its export, 60 - 100 = -40 MW.
        var loop = new AreaInterchangePowerFlow(new NewtonRaphsonPowerFlow(), new[] { Area2Lossless });
        PowerFlowResult r = loop.Solve(Lossless(pmax2: 60), Opt);
        Assert.True(r.Converged);
        Assert.True(loop.Settled);
        Assert.Equal(60.0, loop.LastDispatchMw[1]);
        AreaInterchangeOutcome a2 = loop.LastAreas.Single();
        Assert.Equal(AreaInterchangeStatus.AtLimit, a2.Status);
        Assert.True(Math.Abs(a2.ExportMw - -40.0) <= 1e-6, $"export {a2.ExportMw:R}");
        Assert.Equal(2, loop.LastPasses);

        // The CLI on the same network written as a RAW file (lossless tie, PT 60): exit 0 and the AT LIMIT line, from the
        // RAW file's AREA records and from an area-interchange file.
        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            string raw = Path.Combine(dir, "areas.raw");
            File.WriteAllText(raw, Raw(met: 1, pt2: 60, r12: 0));
            CliProcess.Result viaRaw = CliProcess.RunBuilt("pf", raw, "--area-control");
            Assert.True(viaRaw.ExitCode == 0, viaRaw.StdErr);
            Assert.Contains("area 2: AT LIMIT", viaRaw.StdOut, StringComparison.Ordinal);
            Assert.Contains("area 1: REPORTED", viaRaw.StdOut, StringComparison.Ordinal);

            string file = Path.Combine(dir, "ai.json");
            File.WriteAllText(file, """{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2, "swingBus": 2, "desiredExportMw": -30, "toleranceMw": 0.1, "buses": [2, 3] } ] }""");
            CliProcess.Result viaFile = CliProcess.RunBuilt("pf", raw, "--area-interchange", file);
            Assert.True(viaFile.ExitCode == 0, viaFile.StdErr);
            Assert.Contains("area 2: AT LIMIT", viaFile.StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 6. remote regulation, closed form -------------------------------------------------------------------------------

    [Fact]
    public void T06_remote_regulation_reaches_the_closed_form_state()
    {
        // Lossless chain with V3 = 1: the 2-3 line carries P = 0.5 pu and delivers Q = 0.2 pu, so V2 sin d = P X = 0.05 and
        // V2 cos d = 1 + Q X = 1.02, giving V2 = sqrt(1.0429) = 1.0212247548899311. The 1-2 line carries the same 0.5 pu
        // into bus 2 with V1 = 1, so V2 cos(th1 - th2) = sqrt(V2^2 - 0.0025) = 1.02 too. The Q each line draws from bus 2 is
        // (V2^2 - 1.02) / 0.1 = 0.229 pu, so Qg2 = 45.8 Mvar. th2 = -asin(0.05 / V2) = -2.8063702517882496 deg and
        // th3 = 2 th2 = -5.612740503576499 deg.
        //
        // Bounds. The solved state is the exact solution of the same equations with V3 = 1 - e (e the loop's remaining
        // error, |e| <= 1e-8) and the residuals r_P2, r_P3, r_Q3 of the last engine solve (each within MaxMismatch m). To
        // first order each quantity moves by its sensitivity times these. The sensitivities, per pu of e, r_P2, r_P3 and
        // r_Q3, from finite differences of an independent solve of these three-bus equations (Python, standard library):
        //   Qg2: -1010.85, -4.90, -20.00, -208.00 Mvar;  V2: -0.976, 0, -0.0049, -0.0999 pu;
        //   th2: -2.685, 5.617, 5.604, -0.275 deg;      th3: -8.179, 5.617, 11.208, -0.549 deg.
        // Each bound below is the sum of the absolute sensitivities, rounded up, times |e| and m. Second-order terms are
        // of the order of the sensitivities times 1e-16 and are covered by the rounding up.
        PowerFlowResult? first = null;
        foreach (IPowerFlowSolver engine in Engines())
        {
            var loop = new RemoteVoltagePowerFlow(engine, new[] { new RemoteVoltageControl(2, 3, 1.0) });
            PowerFlowResult r = loop.Solve(Chain(), Opt);
            Assert.True(r.Converged);
            Assert.True(loop.Settled, loop.LastNote);
            Assert.Equal(RemoteVoltageStatus.Ok, loop.LastControls.Single().Status);
            double e = Math.Abs(1.0 - r.Vm[At(r, 3)]), m = r.MaxMismatch;
            Assert.True(e <= RemoteVoltagePowerFlow.VoltageTolerancePu, $"V3 {r.Vm[At(r, 3)]:R}");
            Assert.True(Math.Abs(r.Vm[At(r, 2)] - 1.0212247548899311) <= 0.98 * e + 0.11 * m, $"V2 {r.Vm[At(r, 2)]:R}");
            Assert.True(Math.Abs(r.QInjMvar[At(r, 2)] - 45.8) <= 1011 * e + 233 * m, $"Qg2 {r.QInjMvar[At(r, 2)]:R}, e {e:R}, m {m:R}");
            Assert.True(Math.Abs(r.VaDeg[At(r, 2)] - -2.8063702517882496) <= 2.7 * e + 11.6 * m, $"th2 {r.VaDeg[At(r, 2)]:R}");
            Assert.True(Math.Abs(r.VaDeg[At(r, 3)] - -5.612740503576499) <= 8.2 * e + 17.4 * m, $"th3 {r.VaDeg[At(r, 3)]:R}");
            Assert.True(loop.LastPasses <= 6);
            Assert.Equal(PinnedRemotePasses, loop.LastPasses);
            if (first is null) { first = r; continue; }
            S10TestSupport.AssertBitIdentical("wp13 remote regulation dense vs sparse", first, r);
        }
    }

    /// <summary>The pass count of test 6, pinned from the run.</summary>
    private const int PinnedRemotePasses = 4;

    // ---- 7. Q limit ------------------------------------------------------------------------------------------------------

    [Fact]
    public void T07_a_controlling_bus_at_its_reactive_limit_is_frozen()
    {
        var loop = new RemoteVoltagePowerFlow(new NewtonRaphsonPowerFlow(), new[] { new RemoteVoltageControl(2, 3, 1.0) });
        PowerFlowResult r = loop.Solve(Chain(qmax2: 30), Opt);
        Assert.True(r.Converged);
        RemoteVoltageOutcome o = loop.LastControls.Single();
        Assert.Equal(RemoteVoltageStatus.AtQLimit, o.Status);
        Assert.True(loop.Settled);
        Assert.True(Math.Abs(r.QInjMvar[At(r, 2)] - 30.0) <= 1e-6, $"Qg2 {r.QInjMvar[At(r, 2)]:R}");
        double v3 = r.Vm[At(r, 3)];
        Assert.True(v3 < 1.0);
        Assert.Equal(PinnedQLimitV3, v3);

        // The same case solved plainly with bus 2 as a PQ bus injecting 30 Mvar. Both solves stop at the engine's mismatch
        // tolerance, 1e-8 pu per equation, from different starts, so they agree to the sensitivity of V3 to that residual:
        // dV3/dQ3 is the 0.2 pu Thevenin reactance at bus 3 and dV3/dQ2 is 0.1 pu (the shared 1-2 line), so the two
        // states differ by at most about 2 x (0.2 + 0.1) x 1e-8 = 6e-9 pu in V3 (the P terms move V3 far less at these
        // small angles). The run gives about 1e-9. 1e-8 pu is asserted; the 1e-12 of a common Newton path is not available.
        GridModel pq = Chain(qmax2: 30);
        pq = pq.CopyWith(buses: pq.Buses.Select(b => b.Id == 2 ? b with { Type = BusType.Pq } : b).ToArray(),
            generators: pq.Generators.Select(g => g.Bus == 2 ? g with { Qg = 30 } : g).ToArray());
        PowerFlowResult plain = new NewtonRaphsonPowerFlow().Solve(pq, Opt);
        Assert.True(Math.Abs(plain.Vm[At(plain, 3)] - v3) <= 1e-8, $"plain V3 {plain.Vm[At(plain, 3)]:R} vs {v3:R}");
    }

    /// <summary>V3 of test 7, pinned from the run.</summary>
    private const double PinnedQLimitV3 = 0.98412239464211526;

    /// <summary>Test 7b network: bus 1 slack (V 1.0); bus 2 PV with unit A (P 0, Qmax 5 Mvar) regulating bus 3; bus 3 PQ with
    /// 50 MW and 20 Mvar; bus 4 PV with unit B (P 0, Q free) regulating bus 5; bus 5 PQ with no load; lines 1-2, 2-3, 3-4
    /// and 4-5 with X 0.1.</summary>
    private static GridModel TwoControls() => new()
    {
        Name = "wp13-two-controls",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv), new Bus(3, BusType.Pq, Pd: 50, Qd: 20), new Bus(4, BusType.Pv), new Bus(5, BusType.Pq) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 999),
            new Generator(2, 0, Qmin: -999, Qmax: 5, Pmin: 0, Pmax: 999),
            new Generator(4, 0, Qmin: -999, Qmax: 999, Pmin: 0, Pmax: 999),
        },
        Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(2, 3, 0, 0.1), new Branch(3, 4, 0, 0.1), new Branch(4, 5, 0, 0.1) },
    };

    [Fact]
    public void T07b_a_control_at_its_reactive_limit_is_released_when_a_later_control_brings_its_unit_off_the_limit()
    {
        // Alone, unit A cannot lift bus 3 to 1.0 pu within its 5 Mvar: it ends AT Q LIMIT.
        var alone = new RemoteVoltagePowerFlow(new NewtonRaphsonPowerFlow(), new[] { new RemoteVoltageControl(2, 3, 1.0) });
        alone.Solve(TwoControls(), Opt);
        Assert.Equal(RemoteVoltageStatus.AtQLimit, alone.LastControls.Single().Status);

        // With unit B after it holding bus 5 at 1.05 pu (bus 5 has no load, so V5 = V4), bus 4 at 1.05 pu feeds bus 3 from
        // the other side and lifts it above 1.0 pu with A at its first set point: A's bus is back on voltage control and A
        // lowers its set point. A is AT Q LIMIT after pass 1 and must be looked at again after B moves; both then end OK.
        PowerFlowResult? first = null;
        foreach (IPowerFlowSolver engine in Engines())
        {
            var loop = new RemoteVoltagePowerFlow(engine, new[] { new RemoteVoltageControl(2, 3, 1.0), new RemoteVoltageControl(4, 5, 1.05) });
            PowerFlowResult r = loop.Solve(TwoControls(), Opt);
            Assert.True(r.Converged);
            Assert.True(loop.Settled, loop.LastNote + " " + string.Join("; ", loop.LastControls));
            Assert.Equal(new[] { RemoteVoltageStatus.Ok, RemoteVoltageStatus.Ok }, loop.LastControls.Select(c => c.Status));
            Assert.True(Math.Abs(r.Vm[At(r, 3)] - 1.0) <= RemoteVoltagePowerFlow.VoltageTolerancePu, $"V3 {r.Vm[At(r, 3)]:R}");
            Assert.True(Math.Abs(r.Vm[At(r, 5)] - 1.05) <= RemoteVoltagePowerFlow.VoltageTolerancePu, $"V5 {r.Vm[At(r, 5)]:R}");
            Assert.Equal(BusType.Pv, r.SolvedBusTypes![At(r, 2)]);
            Assert.True(r.QInjMvar[At(r, 2)] < 5.0, $"Qg2 {r.QInjMvar[At(r, 2)]:R}");
            Assert.True(loop.LastControls[0].SetPointPu < 1.0, $"set point {loop.LastControls[0].SetPointPu:R}");
            if (first is null) { first = r; continue; }
            S10TestSupport.AssertBitIdentical("wp13 two controls dense vs sparse", first, r);
        }
    }

    // ---- 8. RAW flags ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A revision 33 three-bus case: bus 1 slack (area 1), bus 2 PV (area 2) with G2 at 40 MW regulating bus 3 (IREG 3,
    /// VS 1.0, PT <paramref name="pt2"/>), bus 3 PQ (area 2) with 100 MW; line 1-2 with R <paramref name="r12"/>, X 0.1 and
    /// MET <paramref name="met"/> (or a negative J), line 2-3 with X 0.1; AREA 1 (ISW 1) and AREA 2 (ISW 2, PDES -30, PTOL
    /// 0.1).
    /// </summary>
    internal static string Raw(int met, double pt2 = 999, double r12 = 0.01, bool negativeJ = false) => FormattableString.Invariant($"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, WP13 area interchange and remote regulation check
        GridSim WP13 check
        Three buses, two areas, a unit regulating a remote bus
        1, 'SRC', 132, 3, 1, 1, 1, 1, 0
        2, 'GEN', 132, 2, 2, 1, 1, 1, 0
        3, 'LOAD', 132, 1, 2, 1, 1, 1, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        3, '1', 1, 2, 1, 100, 0, 0, 0, 0, 0, 1, 1, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
        2, '1', 40, 0, 999, -999, 1, 3, 100, 0, 1, 0, 0, 1, 1, 100, {pt2}, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, {(negativeJ ? "-2" : "2")}, '1', {r12}, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, {met}, 0, 1, 1
        2, 3, '1', 0, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        1, 1, 0, 10, 'WEST'
        2, 2, -30, 0.1, 'EAST'
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA
        Q

        """).Replace("\r\n", "\n");

    // SHA-256 of what the 2.1.105 build gave for Raw(met: 2) with no new flag: the parse notes joined by "\n" (one note,
    // the IREG note), the MATPOWER text of the model, `pf <raw> --json` from "exitCode" to the end, and the same for
    // `pf case14 --json` and `pf case118 --json` (line endings normalised to LF; the text before "exitCode" holds the
    // version and the arguments).
    //
    // Provenance. These five were taken by the WP13 change from the Release build of 2.1.105: source commit aacf3e3b,
    // signed tag v2.1.105-RC-5. The SHA-256 of that build's binaries was not recorded at the time, and that build has
    // since been overwritten, so the pins cannot be traced to a binary hash. The commands, each with the file Raw(met: 2)
    // written as areas.raw:
    //   NotesSha105:   PsseRawParser.ParseWithDiagnostics(Raw(met: 2), "areas", new PsseReadOptions()).Notes, joined by "\n"
    //   ModelMSha105:  gridsim convert areas.raw old-areas.m
    //   PfRawSha105:   gridsim pf areas.raw --json old-areas.json
    //   PfCase14Sha105, PfCase118Sha105: gridsim pf case14 --json old-case14.json, gridsim pf case118 --json old-case118.json
    // The review re-derived NotesSha105 independently. Every new code path is gated on an option that is off here, so the
    // current code with the options off gives the same bits, and this test checks exactly that.
    private const string NotesSha105 = "353985b44a716142221a52dbc5efb514a0fcda7879d87075ec81ad26ccabb07c";
    private const string ModelMSha105 = "3b9a9511f6a19770d47505eda261e8b4a2abc82eb3195c3d5264c93fe739e26d";
    private const string PfRawSha105 = "fa68e4424ac74c41e5dfcabf7b83d0eeb49175662cb7591ce8569d2b0dc2da9e";
    private const string PfCase14Sha105 = "6687c067f91d283d70bac58c6b4225b67dcd6be76db5180cbd2c3e3c75edbeb3";
    private const string PfCase118Sha105 = "3bd1ca6fcd23ca59f3362ce5cb3685bbaa6b05399be314cc9ceb41a1dd9adfc5";

    private static string FromExitCode(string path)
    {
        string t = File.ReadAllText(path).Replace("\r\n", "\n");
        return t[t.IndexOf("\"exitCode\"", StringComparison.Ordinal)..];
    }

    [Fact]
    public void T08_without_the_flags_the_raw_parse_is_byte_identical_to_2_1_105()
    {
        var plainOptions = new PsseReadOptions();
        PsseParseResult plain = PsseRawParser.ParseWithDiagnostics(Raw(met: 2), "areas", plainOptions);
        Assert.Equal(NotesSha105, Sha(string.Join("\n", plain.Notes)));
        Assert.Empty(plain.AreaInterchange);
        Assert.Empty(plain.AreaReported);
        Assert.Empty(plain.BranchMeteredEnds);
        Assert.Empty(plain.RemoteVoltageControls);

        // The model is the same with the flags as without.
        PsseParseResult with = PsseRawParser.ParseWithDiagnostics(Raw(met: 2), "areas", new PsseReadOptions { KeepAreaInterchange = true, RemoteRegulation = true });
        Assert.Equal(MatpowerWriter.Write(plain.Model).Text, MatpowerWriter.Write(with.Model).Text);
        Assert.Equal(plain.Model.Buses, with.Model.Buses);
        Assert.Equal(plain.Model.Generators, with.Model.Generators);
        Assert.Equal(plain.Model.Branches, with.Model.Branches);

        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            string raw = Path.Combine(dir, "areas.raw");
            File.WriteAllText(raw, Raw(met: 2));
            CliProcess.Result conv = CliProcess.RunBuilt("convert", raw, Path.Combine(dir, "old-areas.m"));
            Assert.True(conv.ExitCode == 0, conv.StdErr);
            Assert.Equal(ModelMSha105, Sha(File.ReadAllText(Path.Combine(dir, "old-areas.m")).Replace("\r\n", "\n")));

            CliProcess.Result pf = CliProcess.RunBuilt("pf", raw, "--json", Path.Combine(dir, "old-areas.json"));
            Assert.True(pf.ExitCode == 0, pf.StdErr);
            Assert.Equal(PfRawSha105, Sha(FromExitCode(Path.Combine(dir, "old-areas.json"))));
            Assert.Contains("note: areas.raw: 1 generator(s) regulate a remote bus (IREG); GridSim holds the set point VS at the unit's own bus instead", pf.StdErr, StringComparison.Ordinal);

            foreach ((string name, string sha) in new[] { ("case14", PfCase14Sha105), ("case118", PfCase118Sha105) })
            {
                string json = Path.Combine(dir, $"old-{name}.json");
                CliProcess.Result c = CliProcess.RunBuilt("pf", name, "--json", json);
                Assert.True(c.ExitCode == 0, c.StdErr);
                Assert.Equal(sha, Sha(FromExitCode(json)));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A revision 30 four-bus case with a transformer: Raw()'s network with 80 MW at bus 3, and bus 4 (33 kV, area 1,
    /// 20 MW) fed from bus 3 through a 1.025 ratio transformer with NMETR 1 (metered at bus 4). Line 1-2 is written with a
    /// negative J (metered at bus 2). Revision 30 has no MET field, no fixed shunt section, and the switched shunts after
    /// the VSC DC section.</summary>
    internal static string Raw30() => """
        0, 100, 30, 0, 0, 50     / PSS/E-30 RAW, WP13 revision 30 check
        GridSim WP13 check
        Revision 30, two areas, a transformer tie
        1, 'SRC', 132, 3, 0, 0, 1, 1, 1, 0, 1
        2, 'GEN', 132, 2, 0, 0, 2, 1, 1, 0, 1
        3, 'LOAD', 132, 1, 0, 0, 2, 1, 1, 0, 1
        4, 'LV', 33, 1, 0, 0, 1, 1, 1, 0, 1
        0 / END OF BUS DATA, BEGIN LOAD DATA
        3, '1', 1, 2, 1, 80, 10, 0, 0, 0, 0, 1
        4, '1', 1, 1, 1, 20, 5, 0, 0, 0, 0, 1
        0 / END OF LOAD DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
        2, '1', 40, 0, 999, -999, 1, 3, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, -2, '1', 0.01, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1
        2, 3, '1', 0, 0.1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        3, 4, 0, '1', 1, 1, 1, 0, 0, 1, 'T34', 1, 1, 1
        0.002, 0.08, 100
        1.025, 0, 0, 0, 0, 0, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0
        1, 0
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        1, 1, 0, 10, 'WEST'
        2, 2, -30, 0.1, 'EAST'
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA
        Q

        """.Replace("\r\n", "\n");

    /// <summary>The same network in revision 34: the branch record carries a name and twelve ratings, MET 2 on line 1-2
    /// (metered at bus 2), a system switching device section, and the transformer's NMETR 1 with twelve ratings and a
    /// vector group.</summary>
    internal static string Raw34() => """
        0, 100, 34, 0, 0, 50     / PSS/E-34 RAW, WP13 revision 34 check
        GridSim WP13 check
        Revision 34, two areas, a transformer tie
        1, 'SRC', 132, 3, 1, 1, 1, 1, 0
        2, 'GEN', 132, 2, 2, 1, 1, 1, 0
        3, 'LOAD', 132, 1, 2, 1, 1, 1, 0
        4, 'LV', 33, 1, 1, 1, 1, 1, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        3, '1', 1, 2, 1, 80, 10, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0
        4, '1', 1, 1, 1, 20, 5, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
        2, '1', 40, 0, 999, -999, 1, 3, 100, 0, 1, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0.01, 0.1, 0, 'L12', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0, 1, 1
        2, 3, '1', 0, 0.1, 0, 'L23', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN SYSTEM SWITCHING DEVICE DATA
        0 / END OF SYSTEM SWITCHING DEVICE DATA, BEGIN TRANSFORMER DATA
        3, 4, 0, '1', 1, 1, 1, 0, 0, 1, 'T34', 1, 1, 1, 0, 1, 0, 1, 0, 1, ''
        0.002, 0.08, 100
        1.025, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1.1, 0.9, 1.1, 0.9, 33, 0, 0, 0, 0
        1, 0
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        1, 1, 0, 10, 'WEST'
        2, 2, -30, 0.1, 'EAST'
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA
        Q

        """.Replace("\r\n", "\n");

    // Pins for T08c. Method: these files and the console hashes were added after the area-control options existed, and no
    // 2.1.105 build is at hand (no second worktree is allowed on the build machine). They were taken from the current code
    // with both options off, where every new branch is skipped (the review agrees the default path is provably the 2.1.105
    // path). So they pin today's default behaviour from here on; they are not an independent record of 2.1.105.
    // Commands, with the fixture written as <name>.raw: the parse notes joined by "\n"
    // (PsseRawParser.ParseWithDiagnostics(text, name, new PsseReadOptions())), `gridsim convert <name>.raw <name>.m`,
    // `gridsim pf <name>.raw --json <name>.json` from "exitCode" to the end, and the stdout of that pf run (LF). The
    // console pins of Raw(met: 2), case14 and case118 are the stdout of the T08 pf runs. Taken on 2.1.107 (dev main
    // b82de211 plus this change), Release build, options off. The revision 30 and 34 notes are the one IREG note, the same
    // text as NotesSha105.
    private static readonly Dictionary<string, string> T08cPins = new(StringComparer.Ordinal)
    {
        ["rev30 notes"] = "353985b44a716142221a52dbc5efb514a0fcda7879d87075ec81ad26ccabb07c",
        ["rev30 .m"] = "2bc2d789dd858765cf730a8878b70cbddbc11a2c7e3b19288871acfc6ac68c84",
        ["rev30 pf json"] = "517fc87afa0ee6dc7d7264d9c4964727d4c901780ec9b636b91fbf8c52d8ad61",
        ["rev30 pf console"] = "112bf6c6e8fff1ff564e2164252713f05cb1df475774caf1ed00e1133e819082",
        ["rev34 notes"] = "353985b44a716142221a52dbc5efb514a0fcda7879d87075ec81ad26ccabb07c",
        ["rev34 .m"] = "17ad7d5ebc09f380dcb912b1e1de7cdacbe70817e91f84f9c69ba62620290cfd",
        ["rev34 pf json"] = "2c1b2af124e146e1c092d7d206e3880aaccae34b3645ce1c2abc8e60068a6f31",
        ["rev34 pf console"] = "eda39c8ee594662cb59b8580c92c89c9e4a394bbe9accdc1cd17e6a46a7cf565",
        ["rev33 pf console"] = "a24e9cdbe1dff45572a8c115bd500d363c18e4a784c2504e24910408583de92a",
        ["case14 pf console"] = "4602812eee79809bf58a0a24ee2fd712f782ce6eac6cc04bdfd4f87ebf9afb9d",
        ["case118 pf console"] = "5ab3c8f0754259382171673e85da9ea22cb0677cddec54d77e26acfb11f82f70",
    };

    [Fact]
    public void T08c_revision_30_and_34_files_with_a_transformer_and_the_pf_console_are_unchanged_without_the_flags()
    {
        var got = new Dictionary<string, string>(StringComparer.Ordinal);
        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            foreach ((string name, string text, int lineAtTo) in new[] { ("rev30", Raw30(), 0), ("rev34", Raw34(), 0) })
            {
                PsseParseResult plain = PsseRawParser.ParseWithDiagnostics(text, name, new PsseReadOptions());
                got[name + " notes"] = Sha(string.Join("\n", plain.Notes));
                Assert.Empty(plain.AreaInterchange);
                Assert.Empty(plain.BranchMeteredEnds);

                // With both flags the model is the same; the metered ends are read: line 1-2 at bus 2 (negative J in
                // revision 30, MET 2 in revision 34) and the transformer at bus 4 (NMETR 1).
                PsseParseResult with = PsseRawParser.ParseWithDiagnostics(text, name, new PsseReadOptions { KeepAreaInterchange = true, RemoteRegulation = true });
                Assert.Equal(MatpowerWriter.Write(plain.Model).Text, MatpowerWriter.Write(with.Model).Text);
                int xf = with.Model.Branches.ToList().FindIndex(b => b.IsTransformer);
                Assert.True(xf >= 0);
                Assert.Equal(new[] { lineAtTo, xf }, with.BranchMeteredEnds.Keys.Order().ToArray());
                Assert.All(with.BranchMeteredEnds.Values, v => Assert.Equal(BranchEnd.To, v));
                AreaInterchangeSpec spec = Assert.Single(with.AreaInterchange);
                Assert.Equal((2, 2, -30.0, 0.1), (spec.Area, spec.SwingBus, spec.DesiredExportMw, spec.ToleranceMw));
                Assert.Equal(new[] { 2, 3 }, spec.Buses);

                string raw = Path.Combine(dir, name + ".raw");
                File.WriteAllText(raw, text);
                CliProcess.Result conv = CliProcess.RunBuilt("convert", raw, Path.Combine(dir, name + ".m"));
                Assert.True(conv.ExitCode == 0, conv.StdErr);
                got[name + " .m"] = Sha(File.ReadAllText(Path.Combine(dir, name + ".m")).Replace("\r\n", "\n"));
                CliProcess.Result pf = CliProcess.RunBuilt("pf", raw, "--json", Path.Combine(dir, name + ".json"));
                Assert.True(pf.ExitCode == 0, pf.StdErr);
                got[name + " pf json"] = Sha(FromExitCode(Path.Combine(dir, name + ".json")));
                got[name + " pf console"] = Sha(pf.StdOut.Replace("\r\n", "\n"));
            }
            string raw33 = Path.Combine(dir, "areas.raw");
            File.WriteAllText(raw33, Raw(met: 2));
            got["rev33 pf console"] = Sha(CliProcess.RunBuilt("pf", raw33).StdOut.Replace("\r\n", "\n"));
            foreach (string name in new[] { "case14", "case118" })
                got[name + " pf console"] = Sha(CliProcess.RunBuilt("pf", name).StdOut.Replace("\r\n", "\n"));
        }
        finally { Directory.Delete(dir, true); }
        string[] off = T08cPins.Keys.Where(k => got[k] != T08cPins[k]).ToArray();
        Assert.True(off.Length == 0, "pins differ: " + string.Join("; ", off.Select(k => $"{k} {got[k]}")));
    }

    [Fact]
    public void T08d_an_area_with_ptol_0_or_a_swing_bus_without_a_unit_is_reported_with_a_note_and_the_run_goes_on()
    {
        var o = new PsseReadOptions { KeepAreaInterchange = true };
        string ptol0 = Raw(met: 2).Replace("2, 2, -30, 0.1, 'EAST'", "2, 2, -30, 0, 'EAST'", StringComparison.Ordinal);
        PsseParseResult a = PsseRawParser.ParseWithDiagnostics(ptol0, "areas", o);
        Assert.Empty(a.AreaInterchange);
        Assert.Equal(new[] { 1, 2 }, a.AreaReported.Select(x => x.Area));
        Assert.Contains("area interchange: area(s) 2 give PTOL 0 MW or less (or a PDES or PTOL that is not a number), so no export can be held within it; reported only.", a.Notes);

        string noUnit = Raw(met: 2).Replace("2, 2, -30, 0.1, 'EAST'", "2, 3, -30, 0.1, 'EAST'", StringComparison.Ordinal);
        PsseParseResult b = PsseRawParser.ParseWithDiagnostics(noUnit, "areas", o);
        Assert.Empty(b.AreaInterchange);
        Assert.Equal(new[] { 1, 2 }, b.AreaReported.Select(x => x.Area));
        Assert.Contains("area interchange: the swing bus (ISW) of area(s) 2 has no in-service unit to adjust; reported only.", b.Notes);

        // pf --area-control runs to the end (exit 0) and reports both areas.
        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            foreach ((string name, string text) in new[] { ("ptol0", ptol0), ("nounit", noUnit) })
            {
                string raw = Path.Combine(dir, name + ".raw");
                File.WriteAllText(raw, text);
                CliProcess.Result pf = CliProcess.RunBuilt("pf", raw, "--area-control");
                Assert.True(pf.ExitCode == 0, pf.StdErr + pf.StdOut);
                Assert.Contains("area 2: REPORTED", pf.StdOut, StringComparison.Ordinal);
                Assert.Contains("reported only.", pf.StdErr, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void T08b_with_the_flags_the_areas_metered_ends_and_remote_controls_are_read()
    {
        var o = new PsseReadOptions { KeepAreaInterchange = true, RemoteRegulation = true };
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(Raw(met: 2), "areas", o);
        // PDES and PTOL are MW in the RAW format and are kept as they are.
        AreaInterchangeSpec a = Assert.Single(r.AreaInterchange);
        Assert.Equal(new AreaInterchangeSpec(2, 2, -30, 0.1, a.Buses), a);
        Assert.Equal(new[] { 2, 3 }, a.Buses);
        AreaMembership rep = Assert.Single(r.AreaReported);
        Assert.Equal(1, rep.Area);
        Assert.Equal(new[] { 1 }, rep.Buses);
        Assert.Equal(BranchEnd.To, Assert.Single(r.BranchMeteredEnds).Value);
        Assert.Equal(0, r.BranchMeteredEnds.Single().Key);
        Assert.Equal(new RemoteVoltageControl(2, 3, 1.0), Assert.Single(r.RemoteVoltageControls));
        Assert.Contains("1 generator(s) regulate a remote bus (IREG); 1 remote voltage control(s) hold the remote bus at VS by moving the unit's set point (--remote-vreg).", r.Notes);
        Assert.DoesNotContain(r.Notes, n => n.Contains("as MATPOWER does", StringComparison.Ordinal));
        Assert.Contains("area interchange: 1 area(s) hold a slack bus, which absorbs the remainder; reported only.", r.Notes);

        // MET 1 meters at the from end (nothing listed); a negative J meters at J.
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(Raw(met: 1), "areas", o).BranchMeteredEnds);
        Assert.Equal(BranchEnd.To, PsseRawParser.ParseWithDiagnostics(Raw(met: 1, negativeJ: true), "areas", o).BranchMeteredEnds[0]);

        // pf with both flags: metered at bus 2, so Pg2 = 70 MW (test 2's closed form) and bus 3 held at VS = 1.0.
        string dir = TestTemp.CreateSubdirectory("wp13-").FullName;
        try
        {
            string raw = Path.Combine(dir, "areas.raw");
            File.WriteAllText(raw, Raw(met: 2));
            CliProcess.Result pf = CliProcess.RunBuilt("pf", raw, "--area-control", "--remote-vreg", "--json", Path.Combine(dir, "r.json"));
            Assert.True(pf.ExitCode == 0, pf.StdErr + pf.StdOut);
            Assert.Contains("area 2: OK", pf.StdOut, StringComparison.Ordinal);
            Assert.Contains("bus 2 -> bus 3: OK", pf.StdOut, StringComparison.Ordinal);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "r.json")));
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("area_settled").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("remote_vreg_settled").GetBoolean());
            string text = File.ReadAllText(Path.Combine(dir, "r.json"));
            Assert.Contains("\"areas\"", text, StringComparison.Ordinal);
            Assert.Contains("\"remote_vreg\"", text, StringComparison.Ordinal);

            // Given with a .raw file that is not read by pf, or with no .raw file, each flag says it had no effect.
            CliProcess.Result none = CliProcess.RunBuilt("pf", "case9", "--area-control");
            Assert.Contains("warning: --area-control had no effect: this run loaded no PSS/E .raw file.", none.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 9. determinism --------------------------------------------------------------------------------------------------

    [Fact]
    public void T09_two_runs_give_the_same_bits_and_controls_keep_the_file_order()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        // Two remote controls, in file order: the condenser at bus 8 holds bus 7, the unit at bus 3 holds bus 4.
        string json = """{ "format": "gridsim-remote-vreg/1", "controls": [ { "generatorBus": 8, "regulatedBus": 7, "targetVpu": 1.062 }, { "generatorBus": 3, "regulatedBus": 4, "targetVpu": 1.02 } ] }""";
        IReadOnlyList<RemoteVoltageControl> controls = ControlFiles.ReadRemoteVoltage(json, "t9");
        Assert.Equal(new[] { 8, 3 }, controls.Select(c => c.GeneratorBus));
        PowerFlowResult? first = null;
        RemoteVoltagePowerFlow? firstLoop = null;
        for (int run = 0; run < 2; run++)
        {
            var loop = new RemoteVoltagePowerFlow(SolverSelector.CreateNewton(m), controls);
            PowerFlowResult r = loop.Solve(m, Opt);
            Assert.True(r.Converged);
            Assert.True(loop.Settled, loop.LastNote + " " + string.Join("; ", loop.LastControls));
            Assert.Equal(new[] { 8, 3 }, loop.LastControls.Select(c => c.GeneratorBus));
            Assert.True(Math.Abs(r.Vm[At(r, 7)] - 1.062) <= 1e-8, $"V7 {r.Vm[At(r, 7)]:R}");
            Assert.True(Math.Abs(r.Vm[At(r, 4)] - 1.02) <= 1e-8, $"V4 {r.Vm[At(r, 4)]:R}");
            if (first is null) { first = r; firstLoop = loop; continue; }
            S10TestSupport.AssertBitIdentical("wp13 two runs", first, r);
            Assert.Equal(firstLoop!.LastPasses, loop.LastPasses);
            for (int c = 0; c < controls.Count; c++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(firstLoop.LastControls[c].SetPointPu), BitConverter.DoubleToInt64Bits(loop.LastControls[c].SetPointPu));
        }
    }

    [Fact]
    public void T10_the_file_readers_are_strict()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ControlFiles.ReadAreaInterchange("""{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2 } ] }""", "f"));
        Assert.Contains("area, swingBus, desiredExportMw, toleranceMw and buses are required", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<InvalidDataException>(() => ControlFiles.ReadAreaInterchange("""{ "format": "gridsim-area-interchange/1", "areas": [], "extra": 1 }""", "f"));
        Assert.Contains("unknown key 'extra'", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<InvalidDataException>(() => ControlFiles.ReadRemoteVoltage("""{ "format": "gridsim-remote-vreg/2", "controls": [] }""", "f"));
        Assert.Contains("\"format\" must be \"gridsim-remote-vreg/1\"", ex.Message, StringComparison.Ordinal);

        AreaInterchangeFile f = ControlFiles.ReadAreaInterchange("""{ "format": "gridsim-area-interchange/1", "areas": [ { "area": 2, "swingBus": 2, "desiredExportMw": -30, "toleranceMw": 1e-6, "buses": [2] } ], "meteredEnds": [ { "from": 2, "to": 1, "meteredBus": 2 } ] }""", "f");
        IReadOnlyDictionary<int, BranchEnd> ends = f.ResolveMeteredEnds(Lossy());
        Assert.Equal(BranchEnd.To, ends[0]);   // the model branch is 1 -> 2, so bus 2 is its to end
    }
}
