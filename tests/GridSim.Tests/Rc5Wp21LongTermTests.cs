using GridSim.Core.Dynamics.LongTerm;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP21: long-term (quasi-steady-state) simulation (<see cref="QssEngine"/>, verb <c>long-term</c>). Sources, stated per
/// test: the exact discrete solutions of the QSS recurrences (forward Euler at h: geometric sequences), the primary frequency
/// response beta = D + sum 1/R, tap move times counted in whole steps with the voltages from independent Newton solves, and
/// the kernel's event landing rule. Lossless networks (R = 0) are used where an equilibrium value is asserted, so the
/// imbalance is exactly the disturbance; the bounds then come from the engine's own stopping tolerance and the Newton
/// tolerance, derived in each test.
/// </summary>
public sealed class Rc5Wp21LongTermTests
{
    private const double Sbase = 100.0;

    /// <summary>Slack bus 1 (unit G1 scheduling the load) and load bus 2 through a lossless line (or two in parallel).</summary>
    private static GridModel TwoBus(double loadMw, double loadMvar = 0.0, double x = 0.1, bool parallel = false, IReadOnlyList<Oltc>? oltcs = null) => new()
    {
        Name = "lt2",
        Oltcs = oltcs ?? Array.Empty<Oltc>(),
        BaseMva = Sbase,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: loadMw, Qd: loadMvar, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: loadMw, Vg: 1.0, MBase: 100, Name: "G1") },
        Branches = parallel
            ? new[] { new Branch(1, 2, 0.0, 2 * x, Name: "L1"), new Branch(1, 2, 0.0, 2 * x, Name: "L2") }
            : new[] { new Branch(1, 2, 0.0, x, Name: "L1") },
    };

    private static LongTermCase Case(double h, double duration, IReadOnlyList<LongTermUnit> units, IReadOnlyList<LongTermEvent> events,
        AgcSpec? agc = null, IReadOnlyList<OltcTimerSpec>? timers = null, IReadOnlyList<RecoveringLoadSpec>? loads = null) =>
        new LongTermCase(h, duration, 0.0, units, agc, timers ?? Array.Empty<OltcTimerSpec>(), loads ?? Array.Empty<RecoveringLoadSpec>(), events)
        {
            // Stop each equilibrium within 1e-9 MW; Newton to 1e-12 pu (1e-10 MW per bus on 100 MVA).
            EquilibriumToleranceMw = 1e-9,
            PowerFlowTolerancePu = 1e-12,
        };

    // ---- 1. AGC ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Agc_follows_the_exact_geometric_sequence()
    {
        // Spec test 1, on the controller alone. One unit, dPL = 100 MW, Ki = 0.01 /s, h = 1 s, B = beta = 80 MW/Hz. With the
        // primary response at equilibrium df = -(dPL - P_agc) / beta, so P(n+1) = P(n) + Ki h (dPL - P(n)) and
        // P(100) = 100 (1 - 0.99^100) = 63.396765872677086 MW (the h -> 0 limit is 100 (1 - e^-1) = 63.212055882855765 MW).
        // Rounding: each step rounds P and two products, about 3 ulp(100) = 4e-14 MW, carried with a factor 0.99, so at
        // most about 100 x 4e-14 = 4e-12 MW after 100 steps; asserted to the spec's 1e-9 MW.
        var agc = new AgcModel(0.01, 80.0, null, new[] { 1.0 });
        for (int n = 0; n < 100; n++)
        {
            double expected = 100.0 * (1.0 - Math.Pow(0.99, n));
            Assert.True(Math.Abs(agc.TotalMw - expected) <= 1e-9, $"step {n}: {agc.TotalMw} vs {expected}");
            agc.Advance(-(100.0 - agc.TotalMw) / 80.0, 1.0);
        }
        Assert.True(Math.Abs(agc.TotalMw - 63.396765872677086) <= 1e-9, $"P_agc(100) = {agc.TotalMw}");
    }

    [Fact]
    public void Agc_in_the_engine_follows_the_geometric_sequence_on_a_lossless_network()
    {
        // The same sequence through the engine: G1 (R = 0.05 on 100 MVA, 1/R = 100 / (0.05 x 50) = 40 MW/Hz = beta, D = 0) is
        // the AGC unit with B = beta; a 100 MW load step at t = 1 s. At the sample t = 1 + m s, P_agc = 100 (1 - 0.99^m).
        // Bound: each equilibrium is accepted with the reference bus within 1e-9 MW of its allocation, and the Newton
        // mismatch at the load bus is at most 1e-12 pu = 1e-10 MW, so df carries an imbalance error of at most 1.1e-9 MW / beta
        // and each step adds at most Ki h 1.1e-9 MW to P_agc's error, which then shrinks by 0.99 a step: at most
        // 100 x 0.01 x 1.1e-9 = 1.1e-9 MW over 100 steps. Asserted to 2e-9 MW at every step.
        GridModel m = TwoBus(100.0);
        LongTermCase c = Case(1.0, 101.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermLoadStep(1.0, 2, 100.0, 0.0) },
            agc: new AgcSpec(0.01, null, null, new[] { new AgcUnit(0, 1.0) }));
        LongTermResult r = QssEngine.Run(m, c);
        Assert.True(r.Completed, r.StopReason);
        Assert.Equal(40.0, r.BetaMwPerHz, 12);
        Assert.Equal(102, r.Samples.Count);
        for (int mStep = 0; mStep <= 100; mStep++)
        {
            double want = 100.0 * (1.0 - Math.Pow(0.99, mStep));
            double got = r.Samples[1 + mStep].AgcUnitMw[0];
            Assert.True(Math.Abs(got - want) <= 2e-9, $"t = {1 + mStep}: P_agc {got} vs {want}");
        }
        Assert.True(Math.Abs(r.Samples[101].AgcUnitMw[0] - 63.396765872677086) <= 2e-9);
    }

    // ---- 2. Primary frequency response -------------------------------------------------------------------------------

    [Fact]
    public void Primary_response_gives_minus_dP_over_beta()
    {
        // Spec test 2. Two units with R = 0.05 pu on 100 MVA, D = 0, 50 Hz: beta = 2 x 100 / (0.05 x 50) = 80 MW/Hz. A 40 MW
        // load step gives df = -0.5 Hz at once (no AGC), and it holds at every later step. Lossless network.
        // Bound: the imbalance the units share is 40 MW to within the engine's 1e-9 MW stop plus the Newton mismatch at the
        // two non-reference buses, 2 x 1e-12 pu x 100 MVA, at this step and at the start: |df + 0.5| <= (1e-9 + 4e-10) / 80
        // = 1.75e-11 Hz. Asserted to the spec's 1e-9 Hz.
        var m = new GridModel
        {
            Name = "lt3",
            BaseMva = Sbase,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 200, Qd: 0, BaseKv: 132) },
            Generators = new[] { new Generator(1, Pg: 100, Vg: 1.0, MBase: 100, Name: "G1"), new Generator(2, Pg: 100, Vg: 1.0, MBase: 100, Name: "G2") },
            Branches = new[] { new Branch(1, 3, 0.0, 0.1, Name: "L13"), new Branch(2, 3, 0.0, 0.1, Name: "L23") },
        };
        LongTermCase c = Case(1.0, 5.0, new[] { new LongTermUnit(0, 0.05), new LongTermUnit(1, 0.05) },
            new LongTermEvent[] { new LongTermLoadStep(1.0, 3, 40.0, 0.0) });
        LongTermResult r = QssEngine.Run(m, c);
        Assert.True(r.Completed, r.StopReason);
        Assert.Equal(80.0, r.BetaMwPerHz, 12);
        Assert.Equal(0.0, r.Samples[0].FrequencyDeviationHz);
        for (int n = 1; n < r.Samples.Count; n++)
            Assert.True(Math.Abs(r.Samples[n].FrequencyDeviationHz + 0.5) <= 1e-9, $"t = {n}: df {r.Samples[n].FrequencyDeviationHz}");
    }

    // ---- 3. Tap changer timing ---------------------------------------------------------------------------------------

    [Fact]
    public void Tap_moves_come_at_the_timer_times_until_the_voltage_is_in_band()
    {
        // Spec test 3. Slack bus 1 at 1.0 pu, transformer T1 (X = 0.1, tap on bus 1's side) to load bus 2 with 50 MW + 20 Mvar.
        // The OLTC holds bus 2 in 1.0 +/- 0.01 with steps of 0.00625; Tdel = 30 s, Tdelstep = 5 s, h = 1 s. Bus 2 starts out
        // of band (about 0.978 pu), so the first move is due after 30 whole steps (t = 30) and each later one after 5 more,
        // while bus 2 stays out of band. The expected moves are found here from independent Newton solves at each tap; the
        // count comes from those solves.
        GridModel baseModel = TwoBus(50.0, 20.0, oltcs: new[] { new Oltc("L1", 2, TargetVpu: 1.0, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.00625, DeadbandPu: 0.01) });
        double V2(double tap)
        {
            GridModel m = baseModel.CopyWith(branches: new[] { baseModel.Branches[0] with { TapRatio = tap } });
            PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
            Assert.True(pf.Converged);
            return pf.Vm[1];
        }
        var expectedMoves = new List<(double At, double Tap)>();
        double t = 30.0, tapNow = 1.0;
        while (Math.Abs(V2(tapNow) - 1.0) > 0.01)
        {
            // Bus 2 is the untapped end: lowering the tap raises it (the steady-state loop's rule).
            tapNow = Math.Max(0.9, tapNow - 0.00625);
            expectedMoves.Add((t, tapNow));
            t += 5.0;
        }
        Assert.NotEmpty(expectedMoves);

        LongTermCase c = Case(1.0, 120.0, new[] { new LongTermUnit(0, 0.05) }, Array.Empty<LongTermEvent>(),
            timers: new[] { new OltcTimerSpec("L1", 30.0, 5.0) });
        LongTermResult r = QssEngine.Run(baseModel, c);
        Assert.True(r.Completed, r.StopReason);
        Assert.Equal(expectedMoves.Count, r.TapMoves.Count);
        for (int k = 0; k < expectedMoves.Count; k++)
        {
            Assert.Equal(expectedMoves[k].At, r.TapMoves[k].AtSeconds);
            Assert.Equal(expectedMoves[k].Tap, r.TapMoves[k].ToTap);
        }
        // Every sample carries the tap in force at its time.
        for (int n = 0; n < r.Samples.Count; n++)
        {
            double inForce = 1.0;
            foreach ((double at, double tap) in expectedMoves) if (at <= n) inForce = tap;
            Assert.Equal(inForce, r.Samples[n].Taps[0]);
        }
    }

    // ---- 4. Load recovery --------------------------------------------------------------------------------------------

    [Fact]
    public void Load_recovery_under_a_held_voltage_is_the_exact_geometric_sequence()
    {
        // Spec test 4. V0 = 1, V held at 0.9, alphaS = 0, alphaT = 2: Ps = 1, Pt = 0.81 (P0 = 1). Tp = 60 s, h = 1 s.
        // Pd(n) = 0.81 + 0.19 (1 - (59/60)^n); Pd(60) = 0.9306894609568648 (the continuous value is 0.930102906177426).
        // Rounding: a few ulp per step over 60 steps, below 1e-14; asserted to 1e-12 at every step. Q the same with Q0 = 0.5.
        var load = new LoadRecoveryQss(new RecoveringLoadSpec(2, 60.0, 60.0, 0.0, 2.0, 0.0, 2.0), 1.0, 0.5, 1.0);
        Assert.True(Math.Abs(load.PdMw(0.9) - 0.81) <= 1e-12);
        for (int n = 0; n <= 60; n++)
        {
            double decay = 1.0 - Math.Pow(59.0 / 60.0, n);
            Assert.True(Math.Abs(load.PdMw(0.9) - (0.81 + 0.19 * decay)) <= 1e-12, $"step {n}: P {load.PdMw(0.9)}");
            Assert.True(Math.Abs(load.QdMvar(0.9) - 0.5 * (0.81 + 0.19 * decay)) <= 1e-12, $"step {n}: Q {load.QdMvar(0.9)}");
            if (n < 60) load.Advance(0.9, 1.0);
        }
        Assert.True(Math.Abs(load.PdMw(0.9) - 0.9306894609568648) <= 1e-12);
    }

    [Fact]
    public void Load_recovery_in_the_engine_follows_its_recurrence_on_every_step()
    {
        // A recovering load (50 MW, 20 Mvar, alphaS = 0, alphaT = 2, betaS = 0, betaT = 2, Tp = Tq = 60 s) at bus 2, fed by
        // two lossless lines; one trips at t = 1 s and the voltage drops. With V(n) the step's solved voltage at bus 2 (the
        // lowest bus voltage, since the reference bus holds 1.0) and V0 the starting one, the drawn load is
        // Pd(n) = xp(n) + P0 (V(n)/V0)^2 and xp(n+1) = xp(n) + h / Tp (P0 - P0 (V(n)/V0)^2). Checked on every step, the state
        // recovered from each sample. Bound: each recovered xp is a difference of terms of about 50 MW computed with a few
        // roundings (ulp(50) = 7e-15), so 1e-12 MW is ample; asserted to 1e-10 MW.
        GridModel m = TwoBus(50.0, 20.0, parallel: true);
        LongTermCase c = Case(1.0, 120.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermBranchTrip(1.0, 1) },
            loads: new[] { new RecoveringLoadSpec(2, 60.0, 60.0, 0.0, 2.0, 0.0, 2.0) });
        LongTermResult r = QssEngine.Run(m, c);
        Assert.True(r.Completed, r.StopReason);
        double v0 = r.Samples[0].MinVoltagePu;
        Assert.Equal(2, r.Samples[0].MinVoltageBus);
        Assert.Equal(50.0, r.Samples[0].LoadPMw[0]);
        double xp = 0.0;
        for (int n = 0; n < r.Samples.Count; n++)
        {
            double v = r.Samples[n].MinVoltagePu, ratio = v / v0;
            Assert.Equal(2, r.Samples[n].MinVoltageBus);
            Assert.True(Math.Abs(r.Samples[n].LoadPMw[0] - (xp + 50.0 * ratio * ratio)) <= 1e-10, $"t = {n}: Pd {r.Samples[n].LoadPMw[0]} vs {xp + 50.0 * ratio * ratio}");
            xp += 1.0 / 60.0 * (-xp + 50.0 - 50.0 * ratio * ratio);
        }
        // The trip lowers the voltage, and the load then recovers toward its 50 MW.
        Assert.True(r.Samples[1].MinVoltagePu < v0);
        Assert.True(r.Samples[1].LoadPMw[0] < 50.0);
        Assert.True(r.Samples[^1].LoadPMw[0] > r.Samples[1].LoadPMw[0]);
    }

    // ---- 5. Events ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_event_between_steps_is_applied_at_the_next_step_and_reported_there()
    {
        // Spec test 5: a trip at t = 10.4 s with h = 1 s lands at t = 11 s (the first step at or after it).
        GridModel m = TwoBus(50.0, 0.0, parallel: true);
        LongTermCase c = Case(1.0, 20.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermBranchTrip(10.4, 1) });
        LongTermResult r = QssEngine.Run(m, c);
        Assert.True(r.Completed, r.StopReason);
        Assert.Equal(11.0, r.Events[0].AppliedSeconds);
        Assert.Equal(11, r.Events[0].AppliedStep);
        Assert.Equal(10.4, r.Events[0].RequestedSeconds);
        for (int n = 0; n < r.Samples.Count; n++)
            Assert.Equal(n == 11 ? "branch 1 trip" : "", r.Samples[n].Events);
        // The lost line raises the transfer reactance from 0.1 pu (two 0.2 pu lines in parallel) to 0.2 pu, so bus 2's
        // voltage falls at t = 11.
        Assert.Equal(r.Samples[10].MinVoltagePu, r.Samples[0].MinVoltagePu, 12);
        Assert.True(r.Samples[11].MinVoltagePu < r.Samples[10].MinVoltagePu);
    }

    [Fact]
    public void A_disturbance_at_t_zero_is_refused()
    {
        // The start is the undisturbed state the loads and timers begin from.
        LongTermCase c = Case(1.0, 10.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermLoadStep(0.0, 2, 10.0, 0.0) });
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(TwoBus(50.0), c));
    }

    [Fact]
    public void A_lost_equilibrium_stops_the_run_and_says_so()
    {
        // 50 MW over X = 0.5 pu, then 300 MW more: beyond the line's 200 MW transfer limit at 1 pu (V^2 / X), so the power
        // flow has no solution; the run stops at that step and keeps the samples before it.
        LongTermCase c = Case(1.0, 10.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermLoadStep(3.0, 2, 300.0, 0.0) });
        LongTermResult r = QssEngine.Run(TwoBus(50.0, 0.0, x: 0.5), c);
        Assert.False(r.Completed);
        Assert.NotNull(r.StopReason);
        Assert.StartsWith("loss of short-term equilibrium at t = 3 s", r.StopReason!, StringComparison.Ordinal);
        Assert.Equal(3, r.Samples.Count);
    }

    [Fact]
    public void Refusals_name_what_is_wrong()
    {
        GridModel m = TwoBus(50.0);
        // No frequency response at all.
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(1.0, 10.0, Array.Empty<LongTermUnit>(), Array.Empty<LongTermEvent>())));
        // A duration that is not a whole number of steps.
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(1.0, 10.5, new[] { new LongTermUnit(0, 0.05) }, Array.Empty<LongTermEvent>())));
        // A timer for a transformer with no tap changer.
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(1.0, 10.0, new[] { new LongTermUnit(0, 0.05) }, Array.Empty<LongTermEvent>(),
            timers: new[] { new OltcTimerSpec("L1", 30.0, 5.0) })));
        // A transient exponent the power flow cannot hold as a polynomial.
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(1.0, 10.0, new[] { new LongTermUnit(0, 0.05) }, Array.Empty<LongTermEvent>(),
            loads: new[] { new RecoveringLoadSpec(2, 60.0, 60.0, 0.0, 1.5, 0.0, 2.0) })));
        // A step at or beyond 2 Tp (forward Euler unstable).
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(2.0, 10.0, new[] { new LongTermUnit(0, 0.05) }, Array.Empty<LongTermEvent>(),
            loads: new[] { new RecoveringLoadSpec(2, 1.0, 60.0, 0.0, 2.0, 0.0, 2.0) })));
        // Tripping the only unit at the reference bus.
        Assert.ThrowsAny<ArgumentException>(() => QssEngine.Run(m, Case(1.0, 10.0, new[] { new LongTermUnit(0, 0.05) },
            new LongTermEvent[] { new LongTermGeneratorTrip(2.0, 0) })));
    }

    [Fact]
    public void Two_runs_give_the_same_bits()
    {
        GridModel m = TwoBus(50.0, 20.0, parallel: true);
        LongTermCase c = Case(1.0, 60.0, new[] { new LongTermUnit(0, 0.05) }, new LongTermEvent[] { new LongTermBranchTrip(1.0, 1) },
            agc: new AgcSpec(0.02, null, 5.0, new[] { new AgcUnit(0, 1.0) }), loads: new[] { new RecoveringLoadSpec(2, 30.0, 30.0, 0.0, 2.0, 0.0, 2.0) });
        Assert.Equal(QssEngine.Run(m, c).ToCsv(), QssEngine.Run(m, c).ToCsv());
    }

    // ---- Input file and CLI ------------------------------------------------------------------------------------------

    private const string Case9Input = """
        {
          "schema": "gridsim-long-term/1",
          "stepSeconds": 1,
          "durationSeconds": 10,
          "units": [ { "generator": 0, "droopPu": 0.05 }, { "generator": 1, "droopPu": 0.05 }, { "generator": 2, "droopPu": 0.05 } ],
          "agc": { "kiPerSecond": 0.01, "units": [ { "generator": 1, "participation": 1 } ] },
          "events": [ { "kind": "loadStep", "atSeconds": 2, "bus": 5, "pMw": 20, "qMvar": 0 } ]
        }
        """;

    [Fact]
    public void The_input_file_is_read_strictly()
    {
        GridModel c9 = GridSim.Core.IO.IeeeCases.Case9();
        LongTermCase c = LongTermCase.Parse(System.Text.Encoding.UTF8.GetBytes(Case9Input), c9);
        Assert.Equal(1.0, c.StepSeconds);
        Assert.Equal(3, c.Units.Count);
        Assert.Single(c.Events);
        Assert.IsType<LongTermLoadStep>(c.Events[0]);
        Assert.Equal(1, c.Agc!.Units[0].Generator);
        Assert.Throws<InvalidDataException>(() => LongTermCase.Parse(System.Text.Encoding.UTF8.GetBytes(Case9Input.Replace("\"units\"", "\"unit\"", StringComparison.Ordinal)), c9));
        Assert.Throws<InvalidDataException>(() => LongTermCase.Parse(System.Text.Encoding.UTF8.GetBytes(Case9Input.Replace("gridsim-long-term/1", "gridsim-long-term/2", StringComparison.Ordinal)), c9));
        Assert.Throws<InvalidDataException>(() => LongTermCase.Parse(System.Text.Encoding.UTF8.GetBytes(Case9Input.Replace("\"generator\": 2", "\"generator\": 9", StringComparison.Ordinal)), c9));
    }

    [Fact]
    public void Cli_runs_the_verb_and_writes_one_row_per_step()
    {
        string dir = TestTemp.Under("gridsim-wp21-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "lt.json"), csv = Path.Combine(dir, "lt.csv");
            File.WriteAllText(input, Case9Input);
            CliProcess.Result r = CliProcess.RunBuilt("long-term", "case9", input, "--out", csv);
            Assert.True(r.ExitCode == 0, r.ToString());
            string[] lines = File.ReadAllLines(csv);
            Assert.Equal(12, lines.Length);   // the header and t = 0 to 10
            Assert.StartsWith("t_s,df_hz,min_v_pu,min_v_bus,p_agc_gen1_mw,passes,residual_mw,events", lines[0], StringComparison.Ordinal);
            // 20 MW more load from t = 2 s: the frequency is below nominal there.
            double df2 = double.Parse(lines[3].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(df2 < 0.0, lines[3]);
            // An existing result file is never overwritten; a third argument is a usage error.
            Assert.Equal(2, CliProcess.RunBuilt("long-term", "case9", input, "--out", csv).ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("long-term", "case9", input, "extra").ExitCode);
            Assert.Equal(1, CliProcess.RunBuilt("long-term", "case9", Path.Combine(dir, "missing.json"), "--out", Path.Combine(dir, "x.csv")).ExitCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
