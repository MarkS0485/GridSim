using Xunit;

namespace GridSim.Scripting.Tests;

public class SteppingTests
{
    private static string Out(Session s, string line) => s.Execute(Parser.ParseLineText(line)!);

    private static Session Live(string dir, string caseName = "case9", string step = "")
    {
        var s = new Session(dir);
        Out(s, TestPaths.Load(caseName));
        Out(s, "SELECT ENGINE LIVE" + step);
        return s;
    }

    [Fact]
    public void Step_advances_the_clock_in_whole_integration_steps()
    {
        Session s = Live(TestPaths.TempDir());
        Assert.Equal(LiveEngine.DefaultStepUs, s.Live!.StepUs);
        string text = Out(s, "STEP SIM 100 MS");
        Assert.Equal(100_000, s.ClockUs);
        Assert.Equal(5, s.Steps.Single().Ticks);
        Assert.Equal(5, s.Live.JournalCount);
        Assert.Contains("stepped 5 x 20 ms", text, StringComparison.Ordinal);
        Out(s, "STEP SIM 0.02 S");
        Assert.Equal(120_000, s.ClockUs);
        Assert.Contains("t 120 ms", Out(s, "REPORT SIM"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_that_is_not_a_whole_multiple_is_refused_and_changes_nothing()
    {
        Session s = Live(TestPaths.TempDir());
        Out(s, "STEP SIM 40 MS");
        long clock = s.ClockUs;
        int journal = s.Live!.JournalCount;
        ScriptException e = Assert.Throws<ScriptException>(() => Out(s, "STEP SIM 30 MS"));
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Contains("not a whole multiple", e.Message, StringComparison.Ordinal);
        Assert.Equal(clock, s.ClockUs);
        Assert.Equal(journal, s.Live.JournalCount);
        Assert.Single(s.Steps);
    }

    [Fact]
    public void The_integration_step_can_be_chosen_and_the_multiple_rule_follows_it()
    {
        Session s = Live(TestPaths.TempDir(), step: " STEP 50 MS");
        Assert.Equal(50_000, s.Live!.StepUs);
        Assert.Throws<ScriptException>(() => Out(s, "STEP SIM 20 MS"));
        Out(s, "STEP SIM 0.2 S");
        Assert.Equal(200_000, s.ClockUs);
        Assert.Equal(4, s.Steps.Single().Ticks);
    }

    [Fact]
    public void A_scheduled_trip_lands_on_its_step_boundary_and_drops_the_frequency()
    {
        Session quiet = Live(TestPaths.TempDir());
        Out(quiet, "STEP SIM 1 S");
        double f0 = quiet.Live!.LastFrame!.FrequencyHz;

        Session s = Live(TestPaths.TempDir());
        Out(s, "AT 100 MS TRIP GENERATOR \"G2\"");
        Assert.Single(s.Pending);
        Out(s, "STEP SIM 1 S");
        Assert.Empty(s.Pending);
        AppliedEvent ev = Assert.Single(s.AppliedEvents);
        Assert.Equal(100_000, ev.TimeUs);
        Assert.Equal("TRIP GENERATOR \"G2\"", ev.Text);
        Assert.False(s.Plant!.Model.AllGenerators[1].InService);
        double f1 = s.Live!.LastFrame!.FrequencyHz;
        Assert.True(f1 < f0 - 0.05, $"expected the trip to pull the frequency down: {f1} vs {f0}");
        Assert.True(double.IsFinite(f1));
    }

    [Fact]
    public void An_immediate_trip_takes_effect_in_the_next_step()
    {
        Session quiet = Live(TestPaths.TempDir());
        Out(quiet, "STEP SIM 400 MS");
        Session s = Live(TestPaths.TempDir());
        Out(s, "TRIP GENERATOR \"G2\"");
        Out(s, "STEP SIM 400 MS");
        Assert.True(s.Live!.LastFrame!.FrequencyHz < quiet.Live!.LastFrame!.FrequencyHz - 0.05);
    }

    [Fact]
    public void Same_time_events_keep_script_order_and_later_events_wait()
    {
        Session s = Live(TestPaths.TempDir());
        Out(s, "AT 200 MS CLOSE GENERATOR \"G3\"");
        Out(s, "AT 200 MS TRIP GENERATOR \"G3\"");
        Out(s, "AT 60 MS SET LOAD \"L5\" P 100 MW");
        Out(s, "AT 500 MS TRIP LOAD \"L7\"");
        // The close was scripted before the trip: at apply time G3 is in service, so CLOSE is refused and the step fails whole.
        ScriptException e = Assert.Throws<ScriptException>(() => Out(s, "STEP SIM 300 MS"));
        Assert.Contains("already in service", e.Message, StringComparison.Ordinal);
        Assert.Equal(new SourcePos(1, 27), e.Pos);

        // Nothing moved: clock, engine, plant and the four pending events are as before the STEP.
        Assert.Equal(0, s.ClockUs);
        Assert.Equal(0, s.Live!.JournalCount);
        Assert.Equal(4, s.Pending.Count);
        Assert.True(s.Plant!.Model.AllGenerators[2].InService);
        Assert.Empty(s.Steps);
        Assert.Empty(s.AppliedEvents);
    }

    [Fact]
    public void Same_time_events_apply_in_script_order()
    {
        Session s = Live(TestPaths.TempDir());
        Out(s, "AT 100 MS TRIP GENERATOR \"G3\"");
        Out(s, "AT 100 MS CLOSE GENERATOR \"G3\"");
        Out(s, "AT 40 MS SET LOAD \"L5\" P 100 MW");
        Out(s, "AT 500 MS TRIP LOAD \"L7\"");
        Out(s, "STEP SIM 300 MS");
        Assert.Equal(["SET LOAD \"L5\" P 100 MW", "TRIP GENERATOR \"G3\"", "CLOSE GENERATOR \"G3\""], s.AppliedEvents.Select(a => a.Text));
        Assert.Equal([40_000L, 100_000L, 100_000L], s.AppliedEvents.Select(a => a.TimeUs));
        Assert.True(s.Plant!.Model.AllGenerators[2].InService);
        Assert.Single(s.Pending);                       // the 500 ms event waits
        Assert.Equal(500_000, s.Pending[0].TimeUs);
        Out(s, "STEP SIM 200 MS");                      // clock 500 ms: an event at T applies in the step that begins at T
        Assert.Single(s.Pending);
        Out(s, "STEP SIM 20 MS");
        Assert.Empty(s.Pending);
        Assert.Equal(4, s.AppliedEvents.Count);
        Assert.Equal(500_000, s.AppliedEvents[3].TimeUs);
    }

    [Fact]
    public void An_event_in_the_past_is_refused()
    {
        Session s = Live(TestPaths.TempDir());
        Out(s, "STEP SIM 100 MS");
        ScriptException e = Assert.Throws<ScriptException>(() => Out(s, "AT 40 MS TRIP GENERATOR \"G1\""));
        Assert.Contains("in the past", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rolling_back_a_live_engine_by_replay_is_bit_identical()
    {
        Session s = Live(TestPaths.TempDir());
        Out(s, "TRIP GENERATOR \"G2\"");
        Out(s, "STEP SIM 200 MS");
        LiveEngine engine = s.Live!;
        double f = engine.LastFrame!.FrequencyHz;
        LiveEngine rebuilt = engine.RebuildTo(engine.JournalCount);
        Assert.Equal(BitConverter.DoubleToInt64Bits(f), BitConverter.DoubleToInt64Bits(rebuilt.LastFrame!.FrequencyHz));
        // And a shorter prefix really is the earlier state.
        LiveEngine shorter = engine.RebuildTo(engine.JournalCount - 3);
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(f), BitConverter.DoubleToInt64Bits(shorter.LastFrame!.FrequencyHz));
    }

    [Fact]
    public void Case14_steps_too()
    {
        Session s = Live(TestPaths.TempDir(), "case14");
        Out(s, "AT 20 MS TRIP LOAD \"L3\"");
        Out(s, "STEP SIM 100 MS");
        Assert.Equal(100_000, s.ClockUs);
        Assert.True(double.IsFinite(s.Live!.LastFrame!.FrequencyHz));
        Assert.True(s.Live.LastFrame.Result.Converged);
    }
}
