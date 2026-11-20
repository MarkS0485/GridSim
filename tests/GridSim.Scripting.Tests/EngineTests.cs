using System.Globalization;
using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>SELECT ENGINE: LIVE with topology (D1, D2), and the whole-horizon engines RMS, FREQUENCY, EMT and PROTECTION stepped by re-run (D3, D4).</summary>
public class EngineTests
{
    private static ScriptException Refused(Session s, params string[] lines)
    {
        foreach (Statement st in Parser.ParseScript(string.Join("\n", lines)))
        {
            try { s.Execute(st); }
            catch (ScriptException e) { return e; }
        }
        throw new Xunit.Sdk.XunitException("expected the script to be refused, but it ran");
    }

    private static Session Case9(params string[] lines)
    {
        var s = new Session(TestPaths.TempDir());
        TestPaths.Exec(s, [TestPaths.Load("case9"), .. lines]);
        return s;
    }

    private static double Reading(Session s, string expr)
    {
        string line = TestPaths.Exec(s, "PRINT " + expr).TrimEnd('\n');
        string value = line[(line.IndexOf(" = ", StringComparison.Ordinal) + 3)..];
        return double.Parse(value.Split(' ')[0], CultureInfo.InvariantCulture);
    }

    private static string Final(Session s, string key) => s.Steps[^1].Final.First(kv => kv.Key == key).Value;

    // ---- LIVE with topology (D1) and its reports (D2) --------------------------------------------------------------------

    [Fact]
    public void A_line_trip_under_live_changes_the_live_network_and_matches_a_static_solve()
    {
        Session quiet = Case9("SELECT ENGINE LIVE", "STEP SIM 200 MS");
        Session tripped = Case9("SELECT ENGINE LIVE", "TRIP LINE \"4-5\"", "STEP SIM 200 MS");
        double vQuiet = Reading(quiet, "VM OF BUS \"5\"");
        double vTripped = Reading(tripped, "VM OF BUS \"5\"");
        Assert.True(Math.Abs(vQuiet - vTripped) > 1e-3, $"{vQuiet} vs {vTripped}");
        Assert.Equal(0.0, Reading(tripped, "PFROM OF LINE \"4-5\""));

        // The live network settles on the same operating point a static solve of the same plant gives.
        Session stat = Case9("TRIP LINE \"4-5\"", "SOLVE POWERFLOW");
        Assert.Equal(Reading(stat, "VM OF BUS \"5\""), vTripped, 5);
        Assert.Equal(Reading(stat, "LOSSES"), Reading(tripped, "LOSSES"), 4);

        // The frequency does not feel a line trip (no infeed changed).
        Assert.Equal(quiet.Live!.LastFrame!.FrequencyHz, tripped.Live!.LastFrame!.FrequencyHz);
    }

    [Fact]
    public void Closing_the_line_again_brings_the_live_network_back()
    {
        Session quiet = Case9("SELECT ENGINE LIVE", "STEP SIM 200 MS");
        Session s = Case9("SELECT ENGINE LIVE", "TRIP LINE \"4-5\"", "STEP SIM 100 MS", "CLOSE LINE \"4-5\"", "STEP SIM 100 MS");
        Assert.Equal(Reading(quiet, "VM OF BUS \"5\""), Reading(s, "VM OF BUS \"5\""), 6);
        Assert.True(Reading(s, "PFROM OF LINE \"4-5\"") != 0.0);
    }

    [Fact]
    public void A_generator_trip_and_a_set_point_change_reach_the_live_network_as_well_as_the_frequency()
    {
        Session quiet = Case9("SELECT ENGINE LIVE", "STEP SIM 200 MS");
        Session gen = Case9("SELECT ENGINE LIVE", "TRIP GENERATOR \"G3\"", "STEP SIM 200 MS");
        Assert.True(Math.Abs(Reading(quiet, "VM OF BUS \"5\"") - Reading(gen, "VM OF BUS \"5\"")) > 1e-4);
        Assert.True(gen.Live!.LastFrame!.FrequencyHz < quiet.Live!.LastFrame!.FrequencyHz - 1e-3);

        Session set = Case9("SELECT ENGINE LIVE", "SET LOAD \"L5\" P 150 MW", "STEP SIM 200 MS");
        Assert.True(Math.Abs(Reading(quiet, "LOSSES") - Reading(set, "LOSSES")) > 0.1);
        Session stat = Case9("SET LOAD \"L5\" P 150 MW", "SOLVE POWERFLOW");
        Assert.Equal(Reading(stat, "LOSSES"), Reading(set, "LOSSES"), 4);
    }

    [Fact]
    public void A_scheduled_line_trip_lands_on_its_step_boundary()
    {
        Session s = Case9("SELECT ENGINE LIVE", "AT 100 MS TRIP LINE \"4-5\"", "STEP SIM 100 MS");
        Assert.True(Reading(s, "PFROM OF LINE \"4-5\"") != 0.0);   // the trip is for the step that begins at 100 ms
        TestPaths.Exec(s, "STEP SIM 20 MS");
        Assert.Equal(0.0, Reading(s, "PFROM OF LINE \"4-5\""));
        Assert.Equal(100_000, s.AppliedEvents.Single().TimeUs);
    }

    [Fact]
    public void Reports_under_live_show_the_live_network_labelled_with_the_script_time_and_under_static_the_last_solve()
    {
        Session s = Case9("SOLVE POWERFLOW");
        string staticBuses = TestPaths.Exec(s, "REPORT BUSES");
        Assert.DoesNotContain("live network", staticBuses, StringComparison.Ordinal);
        TestPaths.Exec(s, "SELECT ENGINE LIVE");
        Assert.Equal(staticBuses, TestPaths.Exec(s, "REPORT BUSES"));   // no step yet: still the last solve
        TestPaths.Exec(s, "TRIP LINE \"4-5\"", "STEP SIM 100 MS");
        string live = TestPaths.Exec(s, "REPORT BUSES");
        Assert.StartsWith("live network as of the last step, t = 100 ms (tick 4)\n", live, StringComparison.Ordinal);
        Assert.NotEqual(staticBuses, live[(live.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
        string branches = TestPaths.Exec(s, "REPORT BRANCHES");
        Assert.StartsWith("live network as of the last step, t = 100 ms", branches, StringComparison.Ordinal);
        Assert.Matches(@"4-5\s+4\s+5\s+0\.0000\s+0\.0000\s+0\.0000", branches);
        // Selecting STATIC again brings the last solve back (the plant has the tripped line, so the solve is out of date).
        TestPaths.Exec(s, "SELECT ENGINE STATIC");
        Assert.Contains("out of date", Refused(s, "REPORT BUSES").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_engine_takes_a_settings_clause()
    {
        Session s = Case9("SELECT ENGINE LIVE STEP 50 MS WITH SOLVER DENSE ASSETS OFF");
        Assert.Equal(50_000, s.Live!.StepUs);
        Assert.Equal(GridSim.Core.PowerFlow.SolverKind.DenseNewton, s.Live.Settings.Solver);
        Assert.False(s.Live.Settings.Assets);
        EngineEntry e = Assert.Single(s.Engines);
        Assert.Equal("LIVE", e.Kind);
        Assert.Contains("solver DenseNewton, assets off", e.Settings, StringComparison.Ordinal);
        Assert.Contains("SOLVER AUTO, DENSE, SPARSE or SWEEP", Refused(s, "SELECT ENGINE LIVE WITH SOLVER FAST").Message.Replace("takes AUTO, DENSE, SPARSE or SWEEP, got FAST", "SOLVER AUTO, DENSE, SPARSE or SWEEP"), StringComparison.Ordinal);
        Assert.Contains("LIVE has no setting named INERTIA", Refused(s, "SELECT ENGINE LIVE WITH INERTIA 5 S").Message, StringComparison.Ordinal);
        Assert.Contains("ASSETS takes ON or OFF", Refused(s, "SELECT ENGINE LIVE WITH ASSETS MAYBE").Message, StringComparison.Ordinal);
        Assert.Contains("SOLVER is given twice", Refused(s, "SELECT ENGINE LIVE WITH SOLVER AUTO SOLVER DENSE").Message, StringComparison.Ordinal);
        Assert.Contains("STATIC takes no settings", Refused(s, "SELECT ENGINE STATIC WITH SOLVER AUTO").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_engine_with_a_network_change_rolls_back_by_replay_bit_identically()
    {
        Session s = Case9("SELECT ENGINE LIVE", "TRIP LINE \"4-5\"", "STEP SIM 100 MS");
        LiveEngine engine = s.Live!;
        double f = engine.LastFrame!.FrequencyHz;
        LiveEngine rebuilt = engine.RebuildTo(engine.JournalCount);
        Assert.Equal(BitConverter.DoubleToInt64Bits(f), BitConverter.DoubleToInt64Bits(rebuilt.LastFrame!.FrequencyHz));
        Assert.Equal(engine.LastFrame.Result.Vm, rebuilt.LastFrame.Result.Vm);
        // A refused statement after a network change leaves the engine where it was.
        int journal = engine.JournalCount;
        Assert.Throws<ScriptException>(() => TestPaths.Exec(s, "TRIP LINE \"4-5\""));
        Assert.Equal(journal, s.Live!.JournalCount);
    }

    [Fact]
    public void The_live_engine_without_edits_is_the_core_live_sim_step_for_step()
    {
        Session s = Case9("SELECT ENGINE LIVE", "STEP SIM 200 MS");
        GridModel m = s.Case!.Model;
        var direct = new LiveSim(m, holdSteady: true, solver: GridSim.Core.PowerFlow.SolverKind.Auto, dynamicsConfig: LiveSim.AuthoredBundleConfig(m));
        SimFrame last = null!;
        for (int k = 0; k < 10; k++) last = direct.Step(k, 20_000 / 60_000_000.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(last.FrequencyHz), BitConverter.DoubleToInt64Bits(s.Live!.LastFrame!.FrequencyHz));
        Assert.Equal(last.Result.Vm, s.Live.LastFrame.Result.Vm);
    }

    [Fact]
    public void With_the_asset_fleet_running_a_generator_trip_is_refused_and_assets_off_allows_it()
    {
        var s = new Session(TestPaths.TempDir());
        TestPaths.Exec(s, TestPaths.LoadGb(), "SELECT ENGINE LIVE");
        Assert.True(s.Live!.HasAssetFleet);
        ScriptException e = Refused(s, "TRIP GENERATOR \"North Scotland Hydro\"");
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Contains("SELECT ENGINE LIVE WITH ASSETS OFF", e.Message, StringComparison.Ordinal);
        TestPaths.Exec(s, "SET GENERATOR \"North Scotland Hydro\" P 600 MW");   // a set-point keeps the count: accepted
        TestPaths.Exec(s, "SELECT ENGINE LIVE WITH ASSETS OFF");
        Assert.False(s.Live!.HasAssetFleet);
        TestPaths.Exec(s, "TRIP GENERATOR \"North Scotland Hydro\"");
        Assert.False(s.Plant!.Model.AllGenerators[0].InService);
    }

    // ---- FREQUENCY ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_frequency_engine_plays_a_loss_of_infeed_and_a_chunked_run_equals_one_run()
    {
        Session one = Case9("SELECT ENGINE FREQUENCY LUMPED", "AT 100 MS TRIP GENERATOR \"G2\"", "STEP SIM 2 S");
        Session many = Case9("SELECT ENGINE FREQUENCY LUMPED", "AT 100 MS TRIP GENERATOR \"G2\"");
        for (int i = 0; i < 20; i++) TestPaths.Exec(many, "STEP SIM 100 MS");   // the trip lands exactly on the second boundary
        Assert.Equal(Final(one, "frequencyHz"), Final(many, "frequencyHz"));
        Assert.Equal(Final(one, "nadirHz"), Final(many, "nadirHz"));
        Assert.True(double.Parse(Final(one, "frequencyHz"), CultureInfo.InvariantCulture) < 59.0);
        Assert.Equal(21, many.Steps.Count + 1);
        Assert.Equal(2_000_000, many.ClockUs);
        Assert.Equal(double.Parse(Final(many, "frequencyHz"), CultureInfo.InvariantCulture), Reading(many, "FREQUENCY"), 9);
        Assert.Contains("engine FREQUENCY", TestPaths.Exec(many, "REPORT SIM"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_made_at_a_boundary_equals_the_same_event_scheduled_for_it()
    {
        Session sched = Case9("SELECT ENGINE FREQUENCY LUMPED", "AT 1 S TRIP GENERATOR \"G2\"", "STEP SIM 1 S", "STEP SIM 2 S");
        Session live = Case9("SELECT ENGINE FREQUENCY LUMPED", "STEP SIM 1 S", "TRIP GENERATOR \"G2\"", "STEP SIM 2 S");
        Assert.Equal(Final(sched, "frequencyHz"), Final(live, "frequencyHz"));
        Assert.Equal(sched.Steps[^1].SeriesSha256, live.Steps[^1].SeriesSha256);
        // And the part shown before the event is untouched by it.
        Session quiet = Case9("SELECT ENGINE FREQUENCY LUMPED", "STEP SIM 1 S");
        Assert.Equal(quiet.Steps[0].SeriesSha256, live.Steps[0].SeriesSha256);
    }

    [Fact]
    public void The_frequency_engine_takes_settings_and_refuses_what_a_lumped_swing_cannot_do()
    {
        Session s = Case9("SELECT ENGINE FREQUENCY LUMPED STEP 10 MS WITH DROOP 0.04 PU RESPONSIVE 0.3 PU GOVERNOR 6 S");
        Assert.Equal(10_000, s.Replay!.StepUs);
        Assert.Equal(0.04m, s.Replay.Settings.DroopPu);
        Assert.Contains("droop 0.04 pu; responsive fraction 0.3 pu; governor time constant 6 s", s.Engines[0].Settings, StringComparison.Ordinal);
        Assert.Contains("whole multiple of the engine's integration step (10 ms)", Refused(s, "STEP SIM 25 MS").Message, StringComparison.Ordinal);
        Assert.Contains("TRIP LINE has no meaning for the FREQUENCY engine", Refused(s, "TRIP LINE \"4-5\"").Message, StringComparison.Ordinal);
        Assert.Contains("SET Q has no effect", Refused(s, "SET LOAD \"L5\" Q 5 MVAR").Message, StringComparison.Ordinal);
        Assert.Contains("FAULT needs the RMS or EMT engine", Refused(s, "FAULT BUS \"5\"").Message, StringComparison.Ordinal);
        Assert.Contains("FREQUENCY has no value yet", Refused(s, "PRINT FREQUENCY").Message, StringComparison.Ordinal);
        Assert.Contains("DROOP of 0.9 PU is outside 0.001 to 0.5 PU", Refused(s, "SELECT ENGINE FREQUENCY LUMPED WITH DROOP 0.9 PU").Message, StringComparison.Ordinal);
        Assert.Contains("FREQUENCY has no setting named INERTIA", Refused(s, "SELECT ENGINE FREQUENCY LUMPED WITH INERTIA 5 S").Message, StringComparison.Ordinal);
        Assert.Contains("unit mismatch: expected PU, got S", Refused(s, "SELECT ENGINE FREQUENCY LUMPED WITH DROOP 1 S").Message, StringComparison.Ordinal);
    }

    // ---- RMS ---------------------------------------------------------------------------------------------------------------

    private static Session Rms(params string[] lines) =>
        Case9(["SOLVE POWERFLOW", "SELECT ENGINE RMS CLASSICAL WITH INERTIA 5 S", .. lines]);

    [Fact]
    public void The_rms_engine_runs_a_fault_and_a_chunked_run_equals_one_run()
    {
        Session one = Rms("AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 1 S");
        Session many = Rms("AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"");
        for (int i = 0; i < 10; i++) TestPaths.Exec(many, "STEP SIM 100 MS");    // both events sit exactly on a boundary
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(many, kv.Key));
        Assert.Equal("ok", Final(one, "synchronism"));
        Assert.Contains("fault at bus 7 from 100 ms to 200 ms", TestPaths.Exec(one, "REPORT SIM"), StringComparison.Ordinal);
        // The fault really did something: a run without it keeps the machines still.
        Session quiet = Rms("STEP SIM 1 S");
        Assert.NotEqual(Final(one, "angleDeg:G2"), Final(quiet, "angleDeg:G2"));
        Assert.True(Math.Abs(double.Parse(Final(quiet, "speedPu:G2"), CultureInfo.InvariantCulture)) < 1e-9);
    }

    [Fact]
    public void An_rms_event_made_at_a_boundary_equals_the_same_event_scheduled_for_it()
    {
        Session sched = Rms("AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 500 MS");
        Session live = Rms("STEP SIM 100 MS", "FAULT BUS \"7\"", "STEP SIM 100 MS", "CLEAR FAULT BUS \"7\"", "STEP SIM 300 MS");
        foreach (KeyValuePair<string, string> kv in sched.Steps[0].Final) Assert.Equal(kv.Value, Final(live, kv.Key));
    }

    [Fact]
    public void The_rms_engine_refuses_what_it_cannot_represent()
    {
        Session s = Case9();
        Assert.Contains("run SOLVE POWERFLOW first", Refused(s, "SELECT ENGINE RMS CLASSICAL WITH INERTIA 5 S").Message, StringComparison.Ordinal);
        TestPaths.Exec(s, "SOLVE POWERFLOW");
        // No inertia constant in a MATPOWER case: every machine is held, and the engine says what to do.
        ScriptException none = Refused(s, "SELECT ENGINE RMS CLASSICAL", "STEP SIM 100 MS");
        Assert.Equal(ErrorPhase.Execution, none.Phase);
        Assert.Contains("WITH INERTIA", none.Message, StringComparison.Ordinal);

        Session r = Rms();
        Assert.Contains("the RMS engine takes faults only", Refused(r, "TRIP GENERATOR \"G2\"").Message, StringComparison.Ordinal);
        Assert.Contains("the RMS engine takes faults only", Refused(r, "SET LOAD \"L5\" P 1 MW").Message, StringComparison.Ordinal);
        Assert.Contains("no bus named \"99\"", Refused(r, "FAULT BUS \"99\"").Message, StringComparison.Ordinal);
        Assert.Contains("no fault is in place at this bus", Refused(r, "CLEAR FAULT BUS \"7\"").Message, StringComparison.Ordinal);
        TestPaths.Exec(r, "FAULT BUS \"7\"");
        Assert.Contains("already faulted", Refused(r, "FAULT BUS \"7\"").Message, StringComparison.Ordinal);
        TestPaths.Exec(r, "CLEAR FAULT BUS \"7\"");
        Assert.Contains("one fault window", Refused(r, "FAULT BUS \"5\"").Message, StringComparison.Ordinal);
        Assert.Contains("RMS has no setting named SOURCE", Refused(r, "SELECT ENGINE RMS CLASSICAL WITH SOURCE \"1\"").Message, StringComparison.Ordinal);
        Assert.Contains("INERTIA of 500 S is outside", Refused(r, "SELECT ENGINE RMS CLASSICAL WITH INERTIA 500 S").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheduled_rms_event_the_engine_refuses_is_reported_at_its_own_line_before_the_engine_moves()
    {
        Session r = Rms("AT 100 MS CLEAR FAULT BUS \"7\"");
        ScriptException e = Refused(r, "STEP SIM 200 MS");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("scheduled event (AT 100 MS)", e.Message, StringComparison.Ordinal);
        Assert.Contains("no fault is in place", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, r.ClockUs);
        Assert.Empty(r.Steps);
        Assert.Single(r.Pending);
    }

    // ---- EMT ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_emt_engine_runs_a_bus_fault_and_a_chunked_run_equals_one_run()
    {
        string[] events = ["AT 10 MS FAULT BUS \"5\"", "AT 20 MS CLEAR FAULT BUS \"5\""];
        Session one = Case9(["SELECT ENGINE EMT SINGLE_PHASE", .. events, "STEP SIM 30 MS"]);
        Session many = Case9(["SELECT ENGINE EMT SINGLE_PHASE", .. events]);
        for (int i = 0; i < 3; i++) TestPaths.Exec(many, "STEP SIM 10 MS");
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(many, kv.Key));
        Assert.Equal(300, one.Steps[0].Ticks);

        // During the fault the bus voltage is the fault's: nearly zero.
        Session during = Case9(["SELECT ENGINE EMT SINGLE_PHASE", "AT 10 MS FAULT BUS \"5\"", "STEP SIM 15 MS"]);
        Assert.True(Math.Abs(double.Parse(Final(during, "v:5"), CultureInfo.InvariantCulture)) < 1e-3);
        Session healthy = Case9("SELECT ENGINE EMT SINGLE_PHASE", "STEP SIM 15 MS");
        Assert.True(Math.Abs(double.Parse(Final(healthy, "v:5"), CultureInfo.InvariantCulture)) > 1e-3);
        Assert.Contains("peak", TestPaths.Exec(one, "REPORT SIM"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_emt_engine_takes_a_source_and_a_step_and_refuses_the_rest()
    {
        Session s = Case9("SELECT ENGINE EMT SINGLE_PHASE STEP 0.2 MS WITH SOURCE \"3\"");
        Assert.Equal(200, s.Replay!.StepUs);
        TestPaths.Exec(s, "STEP SIM 2 MS");
        Assert.Contains("source at bus 3", TestPaths.Exec(s, "REPORT SIM"), StringComparison.Ordinal);
        Assert.Contains("whole multiple of the engine's integration step (0.2 ms)", Refused(s, "STEP SIM 0.3 MS").Message, StringComparison.Ordinal);
        Assert.Contains("no bus named \"77\"", Refused(s, "SELECT ENGINE EMT SINGLE_PHASE WITH SOURCE \"77\"").Message, StringComparison.Ordinal);
        Assert.Contains("the EMT engine takes faults only", Refused(s, "TRIP LINE \"4-5\"").Message, StringComparison.Ordinal);
    }

    // ---- PROTECTION --------------------------------------------------------------------------------------------------------

    private static string[] GbCascade => [TestPaths.LoadGbFrequencyOnly(), "SELECT ENGINE PROTECTION FREQUENCY",
        "FOR EACH GENERATOR g WHERE P OF GENERATOR g > 500 MW", "  AT 1 S TRIP GENERATOR g", "END"];

    [Fact]
    public void The_protection_engine_operates_the_cases_protection_bundle()
    {
        var s = new Session(TestPaths.TempDir());
        TestPaths.Exec(s, [.. GbCascade, "STEP SIM 20 S"]);
        string report = TestPaths.Exec(s, "REPORT SIM");
        Assert.Matches(@"protection: 1 device\(s\) operated \(800\.00 MW\)", report);
        Assert.True(int.Parse(Final(s, "trippedUnits"), CultureInfo.InvariantCulture) > 1);
        Assert.True(double.Parse(Final(s, "frequencyHz"), CultureInfo.InvariantCulture) < 49.0);

        // A single 650 MW loss on this system is inside the bands: nothing operates.
        var small = new Session(TestPaths.TempDir());
        TestPaths.Exec(small, TestPaths.LoadGbFrequencyOnly(), "SELECT ENGINE PROTECTION FREQUENCY", "AT 1 S TRIP GENERATOR \"North Scotland Hydro\"", "STEP SIM 10 S");
        Assert.Equal("0", Final(small, "trippedUnits"));
        Assert.True(double.Parse(Final(small, "frequencyHz"), CultureInfo.InvariantCulture) > 49.9);
    }

    [Fact]
    public void A_chunked_protection_run_equals_one_run_and_a_case_without_a_bundle_is_refused()
    {
        var one = new Session(TestPaths.TempDir());
        TestPaths.Exec(one, [.. GbCascade, "STEP SIM 10 S"]);
        var many = new Session(TestPaths.TempDir());
        TestPaths.Exec(many, [.. GbCascade]);
        for (int i = 0; i < 5; i++) TestPaths.Exec(many, "STEP SIM 2 S");
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(many, kv.Key));

        Session c9 = Case9();
        ScriptException e = Refused(c9, "SELECT ENGINE PROTECTION FREQUENCY");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("no protection bundle", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_engine_with_more_than_one_possible_model_requires_its_model_and_reserved_models_are_refused()
    {
        foreach ((string text, int col) in new[] { ("SELECT ENGINE RMS", 18), ("SELECT ENGINE EMT STEP 1 MS", 22), ("SELECT ENGINE FREQUENCY", 24), ("SELECT ENGINE PROTECTION WITH X 1 S", 28), ("SELECT ENGINE RMS FAST", 19) })
        {
            ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(text));
            Assert.Equal(ErrorPhase.Syntax, e.Phase);
            Assert.Contains("needs a model", e.Message, StringComparison.Ordinal);
            _ = col;
        }
        Session s = Case9("SOLVE POWERFLOW");
        Assert.Contains("RMS SUBTRANSIENT runs on machine dynamic data", Refused(s, "SELECT ENGINE RMS SUBTRANSIENT").Message, StringComparison.Ordinal);
        Assert.Contains("RMS GOVERNED runs on machine dynamic data", Refused(s, "SELECT ENGINE RMS GOVERNED STEP 5 MS").Message, StringComparison.Ordinal);
        Assert.Contains("CLASSICAL has no setting named DYNAMICS", Refused(s, "SELECT ENGINE RMS CLASSICAL WITH DYNAMICS \"x.json\"").Message.Replace("RMS has no", "CLASSICAL has no"), StringComparison.Ordinal);
        Assert.Contains("RMS has no setting named INERTIA", Refused(s, "SELECT ENGINE RMS SUBTRANSIENT WITH INERTIA 5 S").Message, StringComparison.Ordinal);
        Assert.Contains("PROBE", Refused(s, "SELECT ENGINE EMT SINGLE_PHASE WITH PROBE \"5\"").Message, StringComparison.Ordinal);
        Assert.Equal("SELECT ENGINE RMS CLASSICAL STEP 5 MS WITH INERTIA 5 S", Parser.ParseScript("select engine rms classical step 5 ms with inertia 5 s").Single().Canonical());
        Assert.Contains("model classical;", Rms().Engines[0].Settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Protection_frequency_refuses_a_case_with_stages_it_cannot_evaluate_instead_of_skipping_them()
    {
        var s = new Session(TestPaths.TempDir());
        TestPaths.Exec(s, TestPaths.LoadGb());
        ScriptException e = Refused(s, "SELECT ENGINE PROTECTION FREQUENCY");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("voltage or loading stages", e.Message, StringComparison.Ordinal);
        Assert.Contains("never skips a stage", e.Message, StringComparison.Ordinal);
        Assert.Equal(EngineKind.Static, s.EngineKind);
    }

    // ---- RMS SUBTRANSIENT and GOVERNED ---------------------------------------------------------------------------------------

    private static Session Dyn(string model, params string[] lines) =>
        Case9(["SOLVE POWERFLOW", $"SELECT ENGINE RMS {model} STEP 5 MS {TestPaths.WithDynamics()}", .. lines]);

    [Theory]
    [InlineData("SUBTRANSIENT")]
    [InlineData("GOVERNED")]
    public void The_dynamic_rms_models_run_a_fault_and_a_chunked_run_equals_one_run(string model)
    {
        string[] events = ["AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\""];
        Session one = Dyn(model, [.. events, "STEP SIM 1 S"]);
        Session many = Dyn(model, events);
        for (int i = 0; i < 10; i++) TestPaths.Exec(many, "STEP SIM 100 MS");
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(many, kv.Key));
        Assert.Equal("ok", Final(one, "synchronism"));
        Assert.Contains($"({model.ToLowerInvariant()})", TestPaths.Exec(one, "REPORT SIM"), StringComparison.Ordinal);
        Assert.Contains(one.Inputs, i => i.Kind == "dynamics" && i.Sha256.Length == 64);
        Assert.Contains("six-state subtransient", one.Engines[0].Settings, StringComparison.Ordinal);
        // An event made at a boundary equals the same event scheduled for it.
        Session live = Dyn(model, "STEP SIM 100 MS", "FAULT BUS \"7\"", "STEP SIM 100 MS", "CLEAR FAULT BUS \"7\"", "STEP SIM 800 MS");
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(live, kv.Key));
    }

    [Fact]
    public void The_governed_model_differs_from_the_subtransient_one_and_both_differ_from_classical()
    {
        string[] run = ["AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 2 S"];
        Session sub = Dyn("SUBTRANSIENT", run);
        Session gov = Dyn("GOVERNED", run);
        Session cla = Rms(run);
        Assert.NotEqual(Final(sub, "speedPu:G2"), Final(gov, "speedPu:G2"));
        Assert.NotEqual(Final(sub, "angleDeg:G2"), Final(cla, "angleDeg:G2"));
        Assert.Contains("controllers of the file not used", sub.Engines[0].Settings, StringComparison.Ordinal);
        Assert.Contains("governors and exciters of the file", gov.Engines[0].Settings, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dynamic_rms_models_refuse_missing_data_at_selection_and_never_fall_back()
    {
        Session s = Case9("SOLVE POWERFLOW");
        Assert.Contains("does not exist", Refused(s, "SELECT ENGINE RMS SUBTRANSIENT WITH DYNAMICS \"nope.json\"").Message, StringComparison.Ordinal);
        ScriptException noGov = Refused(s, $"SELECT ENGINE RMS GOVERNED {TestPaths.WithDynamics(TestPaths.DynamicsWithoutGovernors())}");
        Assert.Equal(ErrorPhase.Execution, noGov.Phase);
        Assert.Contains("no machine in the dynamic data file has a governor", noGov.Message, StringComparison.Ordinal);
        Assert.Equal(EngineKind.Static, s.EngineKind);
        // The file binds the machines of case9; case14's generators are not all in it.
        var c14 = new Session(TestPaths.TempDir());
        TestPaths.Exec(c14, TestPaths.Load("case14"), "SOLVE POWERFLOW");
        ScriptException wrongCase = Refused(c14, $"SELECT ENGINE RMS SUBTRANSIENT {TestPaths.WithDynamics()}");
        Assert.Equal(ErrorPhase.Execution, wrongCase.Phase);
        Assert.Contains("RMS SUBTRANSIENT:", wrongCase.Message, StringComparison.Ordinal);
        Assert.Contains("schemas", Refused(s, $"SELECT ENGINE RMS SUBTRANSIENT WITH DYNAMICS \"{TestPaths.DynamicsFixture.Replace("\\", "\\\\", StringComparison.Ordinal)}\" SCHEMAS \"nowhere\"").Message.Replace("refused", "schemas"), StringComparison.Ordinal);
        Session r = Dyn("SUBTRANSIENT");
        Assert.Contains("cannot be applied at 0 ms", Refused(r, "FAULT BUS \"7\"").Message, StringComparison.Ordinal);
        Assert.Contains("the RMS engine takes faults only", Refused(r, "TRIP GENERATOR \"G2\"").Message, StringComparison.Ordinal);
    }

    // ---- EMT THREE_PHASE ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_three_phase_emt_engine_runs_a_fault_and_a_chunked_run_equals_one_run()
    {
        string[] head = ["SELECT ENGINE EMT THREE_PHASE WITH PROBE \"5\"", "AT 10 MS FAULT BUS \"5\"", "AT 20 MS CLEAR FAULT BUS \"5\""];
        Session one = Case9([.. head, "STEP SIM 30 MS"]);
        Session many = Case9(head);
        for (int i = 0; i < 3; i++) TestPaths.Exec(many, "STEP SIM 10 MS");
        foreach (KeyValuePair<string, string> kv in one.Steps[0].Final) Assert.Equal(kv.Value, Final(many, kv.Key));
        Assert.Equal(300, one.Steps[0].Ticks);
        Assert.Contains("three-phase EMT", TestPaths.Exec(one, "REPORT SIM"), StringComparison.Ordinal);

        Session during = Case9("SELECT ENGINE EMT THREE_PHASE WITH PROBE \"5\"", "AT 10 MS FAULT BUS \"5\"", "STEP SIM 15 MS");
        foreach (string ph in new[] { "va", "vb", "vc" })
            Assert.True(Math.Abs(double.Parse(Final(during, ph + ":5"), CultureInfo.InvariantCulture)) < 1e-3);
        Session healthy = Case9("SELECT ENGINE EMT THREE_PHASE WITH PROBE \"5\"", "STEP SIM 15 MS");
        Assert.True(Math.Abs(double.Parse(Final(healthy, "va:5"), CultureInfo.InvariantCulture)) > 1e-3);
        // Three phases 120 degrees apart: the instantaneous sum is nearly zero in the healthy network.
        double sum = new[] { "va", "vb", "vc" }.Sum(ph => double.Parse(Final(healthy, ph + ":5"), CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(sum) < 1e-6, $"sum {sum}");
    }

    [Fact]
    public void The_three_phase_emt_engine_states_its_limits()
    {
        Session s = Case9("SELECT ENGINE EMT THREE_PHASE");
        Assert.Contains("one fault window per bus", Refused(s, "FAULT BUS \"5\"", "CLEAR FAULT BUS \"5\"", "FAULT BUS \"5\"").Message, StringComparison.Ordinal);
        Assert.Contains("PROBE takes a bus name", Refused(s, "SELECT ENGINE EMT THREE_PHASE WITH PROBE 5 MW").Message.Replace("expected", "PROBE takes a bus name"), StringComparison.Ordinal);
        var c14 = new Session(TestPaths.TempDir());
        TestPaths.Exec(c14, TestPaths.Load("case14"), "SELECT ENGINE EMT THREE_PHASE");
        Assert.Contains("tap or phase shift", Refused(c14, "STEP SIM 1 MS").Message, StringComparison.Ordinal);
    }

    // ---- common ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_engine_is_deterministic_run_twice_and_in_a_fresh_session()
    {
        string[][] scripts =
        [
            [TestPaths.Load("case9"), "SELECT ENGINE LIVE", "TRIP LINE \"4-5\"", "AT 100 MS SET LOAD \"L7\" P 90 MW", "STEP SIM 300 MS", "REPORT BRANCHES", "REPORT SIM"],
            [TestPaths.Load("case9"), "SELECT ENGINE FREQUENCY LUMPED", "AT 100 MS TRIP GENERATOR \"G2\"", "STEP SIM 1 S", "REPORT SIM", "SET LOAD \"L5\" P 120 MW", "STEP SIM 1 S", "REPORT SIM"],
            [TestPaths.Load("case9"), "SOLVE POWERFLOW", "SELECT ENGINE RMS CLASSICAL WITH INERTIA 5 S DAMPING 2 PU", "AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 500 MS", "REPORT SIM", "STEP SIM 500 MS", "REPORT SIM"],
            [TestPaths.Load("case9"), "SOLVE POWERFLOW", $"SELECT ENGINE RMS SUBTRANSIENT STEP 5 MS {TestPaths.WithDynamics()}", "AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 500 MS", "REPORT SIM", "STEP SIM 500 MS"],
            [TestPaths.Load("case9"), "SOLVE POWERFLOW", $"SELECT ENGINE RMS GOVERNED STEP 5 MS {TestPaths.WithDynamics()}", "AT 100 MS FAULT BUS \"7\"", "AT 200 MS CLEAR FAULT BUS \"7\"", "STEP SIM 500 MS", "REPORT SIM", "STEP SIM 500 MS"],
            [TestPaths.Load("case9"), "SELECT ENGINE EMT THREE_PHASE WITH PROBE \"9\"", "AT 5 MS FAULT BUS \"9\"", "AT 12 MS CLEAR FAULT BUS \"9\"", "STEP SIM 20 MS", "REPORT SIM"],
            [TestPaths.Load("case9"), "SELECT ENGINE EMT SINGLE_PHASE", "AT 5 MS FAULT BUS \"9\"", "AT 12 MS CLEAR FAULT BUS \"9\"", "STEP SIM 20 MS", "REPORT SIM"],
            [.. GbCascade, "STEP SIM 5 S", "REPORT SIM", "STEP SIM 5 S", "REPORT SIM"],
        ];
        foreach (string[] script in scripts)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", script) + "\n");
            (string Record, string Out) Go()
            {
                var o = new StringWriter();
                RunResult r = Runner.Run(bytes, new RunOptions(TestPaths.TempDir(), o));
                Assert.True(r.Ok, r.Error?.Display);
                return (r.RecordJson, o.ToString());
            }
            var first = Go();
            var second = Go();
            (string Record, string Out)? third = null;
            var t = new Thread(() => third = Go());
            t.Start();
            t.Join();
            Assert.Equal(first, second);
            Assert.Equal(first, third);
            using JsonDocument doc = JsonDocument.Parse(first.Record);
            Assert.NotEqual(0, doc.RootElement.GetProperty("engines").GetArrayLength());
            JsonElement step = doc.RootElement.GetProperty("steps")[0];
            Assert.Equal(64, step.GetProperty("seriesSha256").GetString()!.Length);
            Assert.NotEqual(JsonValueKind.Undefined, step.GetProperty("final").ValueKind);
        }
    }

    [Fact]
    public void A_failed_step_of_a_replay_engine_leaves_the_session_as_it_was()
    {
        Session s = Case9("SELECT ENGINE FREQUENCY LUMPED", "AT 40 MS TRIP GENERATOR \"G2\"", "AT 60 MS TRIP GENERATOR \"G2\"");
        long clock = s.ClockUs;
        ScriptException e = Refused(s, "STEP SIM 100 MS");
        Assert.Contains("already tripped", e.Message, StringComparison.Ordinal);
        Assert.Equal(clock, s.ClockUs);
        Assert.Equal(2, s.Pending.Count);
        Assert.Empty(s.Replay!.Events);
        Assert.True(s.Plant!.Model.AllGenerators[1].InService);
        TestPaths.Exec(s, "SELECT ENGINE STATIC");
    }

    [Fact]
    public void Engines_need_a_loaded_case_and_stepping_verbs_need_an_engine()
    {
        var s = new Session(TestPaths.TempDir());
        foreach (string kind in new[] { "RMS CLASSICAL", "FREQUENCY LUMPED", "EMT SINGLE_PHASE", "PROTECTION FREQUENCY" })
            Assert.Contains("no case is loaded", Refused(s, "SELECT ENGINE " + kind).Message, StringComparison.Ordinal);
        Session c9 = Case9();
        Assert.Contains("FAULT needs the RMS or EMT engine", Refused(c9, "FAULT BUS \"5\"").Message, StringComparison.Ordinal);
        Assert.Contains("CLEAR FAULT needs the RMS or EMT engine", Refused(c9, "CLEAR FAULT BUS \"5\"").Message, StringComparison.Ordinal);
        Assert.Contains("STEP SIM needs a stepping engine", Refused(c9, "STEP SIM 10 MS").Message, StringComparison.Ordinal);
    }
}
