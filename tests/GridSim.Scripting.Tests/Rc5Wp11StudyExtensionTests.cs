using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>
/// RC5 WP11: the STUDIES extension of grammar 1 (<c>GRAMMAR 1 WITH STUDIES</c>). Without the header a script parses, runs
/// and records exactly as grammar 1. With it, STUDY CONTINGENCY, SHORTCIRCUIT and OPF run the Core entry points the CLI's
/// nminus1, iec60909 and acopf-full run, with their default options; the oracle here is those Core calls made directly on
/// the same model (bit for bit, in the run record's round-trip text). GridSim.Tests' Rc5Wp11ScriptChainTests ties the same
/// Core calls to the CLI verbs.
/// </summary>
public sealed class Rc5Wp11StudyExtensionTests
{
    private const string Header = "GRAMMAR 1 WITH STUDIES";

    private static GridModel Case9Model() => MatpowerParser.Parse(File.ReadAllText(TestPaths.Case("case9")), "case9", 60.0);

    private static RunResult RunScript(params string[] lines) =>
        Runner.Run(new System.Text.UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n"), new RunOptions(TestPaths.TempDir()));

    private static JsonElement Record(RunResult r)
    {
        using JsonDocument d = JsonDocument.Parse(r.RecordJson);
        return d.RootElement.Clone();
    }

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void Without_the_header_study_is_still_an_unknown_verb_and_its_words_are_free_names()
    {
        ScriptException a = Assert.Throws<ScriptException>(() => Parser.ParseScript("STUDY CONTINGENCY AS a"));
        ScriptException b = Assert.Throws<ScriptException>(() => Parser.ParseScript("FROBNICATE CONTINGENCY AS a"));
        Assert.Equal(b.Message.Replace("'FROBNICATE'", "'STUDY'", StringComparison.Ordinal), a.Message);
        Assert.StartsWith("unknown verb 'STUDY'; verbs are GRAMMAR, LOAD, SOLVE,", a.Message, StringComparison.Ordinal);
        Session s = TestPaths.Run(TestPaths.TempDir(), "LET STUDY = 1", "LET IK = 2", "LET OPF = 3");
        Assert.Equal(1m, s.Globals["STUDY"].Number);
        Assert.Throws<ScriptException>(() => Parser.ParseScript("REPORT STUDY a"));
        Assert.Throws<ScriptException>(() => Parser.ParseScript("GRAMMAR 2"));
        Assert.Throws<ScriptException>(() => Parser.ParseScript("GRAMMAR 1 WITH NOTHING"));
        Assert.Throws<ScriptException>(() => Parser.ParseScript("LET x = 1\n" + Header));
    }

    [Fact]
    public void With_the_header_the_extension_words_are_keywords()
    {
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(Header + "\nLET STUDY = 1"));
        Assert.Equal("'STUDY' is a keyword of the language and cannot be used as a name", e.Message);
        Assert.Equal(new SourcePos(2, 5), e.Pos);
        Assert.Throws<ScriptException>(() => Parser.ParseScript(Header + "\nLET ik = 1"));
        Assert.Throws<ScriptException>(() => Parser.ParseScript(Header + "\nPRINT 5 KA"));   // KA is no unit a number is written in
    }

    [Fact]
    public void A_grammar_1_record_has_no_extension_member_and_a_studies_record_has_both()
    {
        JsonElement plain = Record(RunScript("GRAMMAR 1", TestPaths.Load("case9"), "SOLVE POWERFLOW"));
        Assert.False(plain.TryGetProperty("grammarExtensions", out _));
        Assert.False(plain.TryGetProperty("studies", out _));
        Assert.Equal("1", plain.GetProperty("grammarVersion").GetString());

        RunResult r = RunScript(Header, TestPaths.Load("case9"), "STUDY SHORTCIRCUIT BUS \"5\" AS sc");
        Assert.True(r.Ok, r.Error?.Message);
        JsonElement rec = Record(r);
        Assert.Equal("1", rec.GetProperty("grammarVersion").GetString());
        Assert.Equal(["STUDIES"], rec.GetProperty("grammarExtensions").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(1, rec.GetProperty("studies").GetArrayLength());
    }

    [Fact]
    public void Contingency_study_counts_what_the_nminus1_core_screen_counts()
    {
        RunResult r = RunScript(Header, TestPaths.Load("case9"), "STUDY CONTINGENCY AS a", "LET nv = VIOLATIONS OF STUDY a", "LET w = WORST LOADING OF STUDY a", "REPORT STUDY a");
        Assert.True(r.Ok, r.Error?.Message);

        GridModel m = Case9Model();
        PowerFlowOptions o = ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = true, PostFaultRating = PostFaultRating.RateA };
        ContingencyResult b = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), o);
        var results = ContingencyAnalysis.NetAgainstBase(ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), o), b);
        int failing = results.Count(c => !c.Converged || !c.Secure);
        double worst = results.Where(c => c.Converged).Max(c => c.MaxBranchLoadingPercent / 100.0);

        JsonElement st = Record(r).GetProperty("studies")[0];
        Assert.Equal("CONTINGENCY", st.GetProperty("study").GetString());
        Assert.Equal(results.Count, st.GetProperty("screened").GetInt32());
        Assert.Equal(failing, st.GetProperty("violations").GetInt32());
        Assert.Equal(R(worst), st.GetProperty("worstLoadingPu").GetString());
        Assert.Equal((decimal)failing, r.Session.Globals["nv"].Number);
        Assert.Equal(VType.Scalar, r.Session.Globals["nv"].Type);
        Assert.Equal((decimal)worst, r.Session.Globals["w"].Number);
        Assert.Equal(Unit.Pu, r.Session.Globals["w"].Type.Unit);
    }

    [Fact]
    public void Short_circuit_study_is_the_iec60909_core_duty_bit_for_bit()
    {
        RunResult r = RunScript(Header, TestPaths.Load("case9"), "STUDY SHORTCIRCUIT BUS \"5\" AS sc", "LET ik = 1", "LET k = IK OF BUS \"5\" IN STUDY sc", "PRINT k");
        Assert.False(r.Ok);   // ik is a keyword here
        r = RunScript(Header, TestPaths.Load("case9"), "STUDY SHORTCIRCUIT BUS \"5\" AS sc", "LET k = IK OF BUS \"5\" IN STUDY sc", "PRINT k / (1 * k)");
        Assert.True(r.Ok, r.Error?.Message);

        GridModel m = Case9Model();
        Iec60909.Duty d = Iec60909.ThreePhase(m, 5, ShortCircuitObjective.Maximum, f0Hz: m.FrequencyHz, breakTimeSeconds: 0.10, faultDurationSeconds: 1.0,
            peakFactorMethod: PeakFactorMethod.MethodB);
        JsonElement st = Record(r).GetProperty("studies")[0];
        Assert.Equal(R(d.IkppKa), st.GetProperty("ikKa").GetString());
        Assert.Equal(5, st.GetProperty("busId").GetInt32());
        Value k = r.Session.Globals["k"];
        Assert.Equal((decimal)d.IkppKa, k.Number);
        Assert.Equal("KA", Units.Keyword(k.Type.Unit));
        Assert.EndsWith(" KA", k.Display(), StringComparison.Ordinal);

        RunResult wrongBus = RunScript(Header, TestPaths.Load("case9"), "STUDY SHORTCIRCUIT BUS \"5\" AS sc", "PRINT IK OF BUS \"7\" IN STUDY sc");
        Assert.Contains("is the fault at bus \"5\", not at bus \"7\"", wrongBus.Error!.Message, StringComparison.Ordinal);
        RunResult noStudy = RunScript(Header, TestPaths.Load("case9"), "PRINT VIOLATIONS OF STUDY nothing");
        Assert.Contains("no study named 'nothing' has run", noStudy.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opf_study_is_the_acopf_full_core_optimum()
    {
        RunResult r = RunScript(Header, TestPaths.Load("case9"), "STUDY OPF AS o", "LET obj = OBJECTIVE OF STUDY o", "LET p2 = P OF GENERATOR \"G2\" IN STUDY o");
        Assert.True(r.Ok, r.Error?.Message);
        AcOpfIpmResult opf = AcOpfInteriorPoint.Solve(Case9Model(), new AcOpfIpmOptions { MaxIterations = 150 });
        Assert.True(opf.Converged);
        JsonElement st = Record(r).GetProperty("studies")[0];
        Assert.Equal(R(opf.ObjectivePerHour), st.GetProperty("objective").GetString());
        Assert.Equal(R(opf.Generators[1].PgMw), st.GetProperty("generators")[1].GetProperty("pMw").GetString());
        Assert.Equal("G2", st.GetProperty("generators")[1].GetProperty("name").GetString());
        Assert.Equal((decimal)opf.Generators[1].PgMw, r.Session.Globals["p2"].Number);
        Assert.Equal(Unit.Mw, r.Session.Globals["p2"].Type.Unit);
    }

    [Fact]
    public void A_study_sees_the_plant_as_edited()
    {
        RunResult r = RunScript(Header, TestPaths.Load("case9"), "STUDY SHORTCIRCUIT BUS \"5\" AS before", "TRIP GENERATOR \"G2\"",
            "STUDY SHORTCIRCUIT BUS \"5\" AS after", "LET b = IK OF BUS \"5\" IN STUDY before", "LET a = IK OF BUS \"5\" IN STUDY after");
        Assert.True(r.Ok, r.Error?.Message);
        Assert.True(r.Session.Globals["a"].Number < r.Session.Globals["b"].Number);   // one infeed fewer
        Assert.Equal(2, Record(r).GetProperty("studies").GetArrayLength());
    }

    [Fact]
    public void The_canonical_text_round_trips_and_a_rerun_gives_the_same_record()
    {
        string[] script =
        [
            Header, TestPaths.Load("case9"), "study contingency as a", "Study ShortCircuit Bus \"5\" As sc", "STUDY OPF AS o",
            "LET x = violations of study a + 1", "PRINT worst loading of study a * 2", "PRINT p of generator \"G1\" in study o",
            "IF IK OF BUS \"5\" IN STUDY sc > IK OF BUS \"5\" IN STUDY sc * 0.5", "  PRINT OBJECTIVE OF STUDY o", "END", "REPORT STUDY sc",
        ];
        IReadOnlyList<Statement> parsed = Parser.ParseScript(string.Join("\n", script));
        string canonical = string.Join("\n", parsed.Select(st => st.Canonical()));
        Assert.Equal(Header, parsed[0].Canonical());
        Assert.Equal("STUDY SHORTCIRCUIT BUS \"5\" AS sc", parsed[3].Canonical());
        Assert.Equal("LET x = VIOLATIONS OF STUDY a + 1", parsed[5].Canonical());
        Assert.Equal("PRINT P OF GENERATOR \"G1\" IN STUDY o", parsed[7].Canonical());
        Assert.Equal(canonical, string.Join("\n", Parser.ParseScript(canonical).Select(st => st.Canonical())));

        RunResult one = RunScript(script), two = RunScript(script);
        Assert.True(one.Ok, one.Error?.Message);
        Assert.Equal(one.RecordJson, two.RecordJson);
        Assert.Contains("\"kind\": \"report\"", one.RecordJson, StringComparison.Ordinal);
    }
}
