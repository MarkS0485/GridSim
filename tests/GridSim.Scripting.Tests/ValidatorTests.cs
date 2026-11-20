using Xunit;

namespace GridSim.Scripting.Tests;

public class ValidatorTests
{
    private static Session Loaded(string caseName = "case9") =>
        TestPaths.Run(TestPaths.TempDir(), TestPaths.Load(caseName));

    private static ScriptException Refused(Session s, string line, int lineNo = 5)
    {
        Statement st = Parser.ParseLineText(line, lineNo)!;
        return Assert.Throws<ScriptException>(() => s.Execute(st));
    }

    [Fact]
    public void Nothing_but_LOAD_runs_before_a_case_is_loaded()
    {
        var s = new Session(TestPaths.TempDir());
        foreach (string text in new[] { "SOLVE POWERFLOW", "TRIP GENERATOR \"G1\"", "REPORT BUSES", "EXPORT RESULT \"x.json\"", "SELECT ENGINE LIVE" })
        {
            ScriptException e = Refused(s, text);
            Assert.Equal(ErrorPhase.Validation, e.Phase);
            Assert.Contains("no case is loaded", e.Message, StringComparison.Ordinal);
            Assert.Equal(new SourcePos(5, 1), e.Pos);
        }
    }

    [Fact]
    public void An_unknown_name_is_refused_at_the_name_with_its_column()
    {
        Session s = Loaded();
        ScriptException e = Refused(s, "TRIP GENERATOR \"Nope\"");
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(new SourcePos(5, 16), e.Pos);
        Assert.Contains("no generator named \"Nope\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("no load named", Refused(s, "TRIP LOAD \"G1\"").Message, StringComparison.Ordinal);
        Assert.Contains("no line named", Refused(s, "CLOSE LINE \"9-9\"").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_are_case_sensitive()
    {
        Session s = Loaded();
        Assert.Contains("no generator named \"g1\"", Refused(s, "TRIP GENERATOR \"g1\"").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SET LOAD \"L5\" P 90 MVAR", "expected MW, got MVAR", 17)]
    [InlineData("SET LOAD \"L5\" P 90 PU", "expected MW, got PU", 17)]
    [InlineData("SET LOAD \"L5\" Q 30 MW", "expected MVAR, got MW", 17)]
    [InlineData("SET GENERATOR \"G2\" P 100 MVA", "expected MW, got MVA", 22)]
    [InlineData("SET GENERATOR \"G2\" V 1.02 MW", "expected PU or KV, got MW", 22)]
    [InlineData("SET GENERATOR \"G2\" P 100 DEG", "expected MW, got DEG", 22)]
    [InlineData("SET GENERATOR \"G2\" Q 10 HZ", "expected MVAR, got HZ", 22)]
    public void A_unit_mismatch_is_refused_at_validation(string text, string fragment, int col)
    {
        Session s = Loaded();
        ScriptException e = Refused(s, text);
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Contains("unit mismatch", e.Message, StringComparison.Ordinal);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.Equal(new SourcePos(5, col), e.Pos);
    }

    [Fact]
    public void Verb_and_noun_pairs_that_make_no_sense_are_refused()
    {
        Session s = Loaded();
        Assert.Contains("SET does not apply to LINE", Refused(s, "SET LINE \"1-4\" P 5 MW").Message, StringComparison.Ordinal);
        Assert.Contains("GENERATOR only", Refused(s, "SET LOAD \"L5\" V 1 PU").Message, StringComparison.Ordinal);
        Assert.Contains("cannot be negative", Refused(s, "SET LOAD \"L5\" P -5 MW").Message, StringComparison.Ordinal);
        Assert.Contains("outside 0.5 to 1.5 PU", Refused(s, "SET GENERATOR \"G2\" V 2 PU").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Frequency_must_be_hertz_and_sane()
    {
        var s = new Session(TestPaths.TempDir());
        Assert.Contains("unit mismatch", Refused(s, "LOAD CASE \"a.m\" FREQUENCY 60 MW").Message, StringComparison.Ordinal);
        Assert.Contains("outside 10 to 100", Refused(s, "LOAD CASE \"a.m\" FREQUENCY 600 HZ").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stepping_verbs_need_the_live_engine()
    {
        Session s = Loaded();
        Assert.Contains("SELECT ENGINE LIVE", Refused(s, "STEP SIM 20 MS").Message, StringComparison.Ordinal);
        Assert.Contains("SELECT ENGINE LIVE", Refused(s, "AT 20 MS TRIP GENERATOR \"G1\"").Message, StringComparison.Ordinal);
        Assert.Contains("SELECT ENGINE LIVE", Refused(s, "REPORT SIM").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_must_be_a_time_a_whole_microsecond_count_and_a_multiple_of_the_integration_step()
    {
        Session s = TestPaths.Run(TestPaths.TempDir(), TestPaths.Load("case9"), "SELECT ENGINE LIVE");
        Assert.Contains("unit mismatch", Refused(s, "STEP SIM 20 MW").Message, StringComparison.Ordinal);
        Assert.Contains("whole multiple of the engine's integration step (20 ms)", Refused(s, "STEP SIM 25 MS").Message, StringComparison.Ordinal);
        Session ok = TestPaths.Run(TestPaths.TempDir(), TestPaths.Load("case9"), "SELECT ENGINE LIVE", "STEP SIM 0.04 S");
        Assert.Equal(40_000, ok.ClockUs);
        Assert.Contains("not a whole number of microseconds", Refused(s, "STEP SIM 0.0005 MS").Message, StringComparison.Ordinal);
        Assert.Contains("must be above zero", Refused(s, "STEP SIM 0 MS").Message, StringComparison.Ordinal);
        Assert.Contains("whole multiple", Refused(s, "AT 30 MS TRIP GENERATOR \"G1\"").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integration_step_belongs_to_live_only()
    {
        Session s = Loaded();
        Assert.Contains("STATIC has no integration step", Refused(s, "SELECT ENGINE STATIC STEP 20 MS").Message, StringComparison.Ordinal);
    }
}
