using Xunit;

namespace GridSim.Scripting.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("LOAD CASE \"a.m\"", "LOAD CASE \"a.m\"")]
    [InlineData("load case \"a.m\" frequency 60 hz", "LOAD CASE \"a.m\" FREQUENCY 60 HZ")]
    [InlineData("Solve PowerFlow", "SOLVE POWERFLOW")]
    [InlineData("trip generator \"G1\"", "TRIP GENERATOR \"G1\"")]
    [InlineData("TRIP LINE \"4-5\"", "TRIP LINE \"4-5\"")]
    [InlineData("close load \"L5\"", "CLOSE LOAD \"L5\"")]
    [InlineData("SET LOAD \"L5\" P 90.50 MW", "SET LOAD \"L5\" P 90.5 MW")]
    [InlineData("set generator \"G2\" q -12 mvar", "SET GENERATOR \"G2\" Q -12 MVAR")]
    [InlineData("SET GENERATOR \"G2\" V 1.02 PU", "SET GENERATOR \"G2\" V 1.02 PU")]
    [InlineData("report buses", "REPORT BUSES")]
    [InlineData("REPORT BRANCHES", "REPORT BRANCHES")]
    [InlineData("REPORT ELEMENTS", "REPORT ELEMENTS")]
    [InlineData("REPORT SIM", "REPORT SIM")]
    [InlineData("EXPORT RESULT \"out/r.json\"", "EXPORT RESULT \"out/r.json\"")]
    [InlineData("SELECT ENGINE LIVE", "SELECT ENGINE LIVE")]
    [InlineData("select engine live step 0.5 s", "SELECT ENGINE LIVE STEP 0.5 S")]
    [InlineData("STEP SIM 20 ms", "STEP SIM 20 MS")]
    [InlineData("step sim 1.5 S", "STEP SIM 1.5 S")]
    [InlineData("AT 100 MS TRIP GENERATOR \"G2\"", "AT 100 MS TRIP GENERATOR \"G2\"")]
    [InlineData("GRAMMAR 1", "GRAMMAR 1")]
    public void Valid_statement_round_trips_through_canonical_form(string text, string canonical)
    {
        Statement s = Parser.ParseScript(text).Single();
        Assert.Equal(canonical, s.Canonical());
        Assert.Equal(s.Canonical(), Parser.ParseScript(s.Canonical()).Single().Canonical());
    }

    [Fact]
    public void Comments_blank_lines_and_crlf_are_ignored_and_lines_are_counted()
    {
        IReadOnlyList<Statement> list = Parser.ParseScript("# header\r\n\r\nSOLVE POWERFLOW # trailing\r\n\r\nREPORT BUSES\r\n");
        Assert.Equal(2, list.Count);
        Assert.Equal(new SourcePos(3, 1), list[0].Pos);
        Assert.Equal(new SourcePos(5, 1), list[1].Pos);
    }

    [Fact]
    public void Names_keep_their_case_and_escapes()
    {
        var t = (TripStatement)Parser.ParseScript("TRIP GENERATOR \"Gen \\\"A\\\" \\\\ b\"").Single();
        Assert.Equal("Gen \"A\" \\ b", t.Name.Value);
        Assert.Equal("TRIP GENERATOR \"Gen \\\"A\\\" \\\\ b\"", t.Canonical());
    }

    [Theory]
    [InlineData("FROB \"x\"", 1, 1, "unknown verb")]
    [InlineData("\n\nTRIP GENERATOR 5", 3, 16, "expected the generator name in double quotes")]
    [InlineData("TRIP BUS \"1\"", 1, 6, "expected GENERATOR, LINE or LOAD")]
    [InlineData("SOLVE", 1, 6, "expected POWERFLOW")]
        [InlineData("SET LOAD \"L5\" X 90 MW", 1, 15, "expected a property")]
    [InlineData("SET LOAD \"L5\" P 90 WATTS", 1, 20, "expected a unit")]
    [InlineData("REPORT CHAOS", 1, 8, "expected BUSES")]
    [InlineData("TRIP GENERATOR \"G1\" now", 1, 21, "unexpected 'now'")]
    [InlineData("STEP SIM 20 MS 5", 1, 16, "unexpected")]
    [InlineData("TRIP LINE \"abc", 1, 11, "unterminated name")]
    [InlineData("SOLVE POWERFLOW ;", 1, 17, "unexpected character")]
    [InlineData("STEP SIM 1e3 MS", 1, 11, "exponent")]
    [InlineData("STEP SIM 5. MS", 1, 11, "decimal point")]
    [InlineData("AT 100 MS REPORT BUSES", 1, 11, "AT schedules TRIP, CLOSE, SET, FAULT or CLEAR FAULT only")]
    [InlineData("AT 100 MS AT 200 MS TRIP LINE \"1-4\"", 1, 11, "AT cannot be nested")]
    [InlineData("GRAMMAR 2", 1, 9, "implements grammar 1")]
    [InlineData("SOLVE POWERFLOW\nGRAMMAR 1", 2, 1, "GRAMMAR must be the first")]
    public void Syntax_errors_name_line_and_column(string text, int line, int col, string fragment)
    {
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(text));
        Assert.Equal(ErrorPhase.Syntax, e.Phase);
        Assert.Equal(new SourcePos(line, col), e.Pos);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.StartsWith($"line {line}, column {col}: syntax error", e.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_anywhere_refuses_the_whole_script()
    {
        Assert.Throws<ScriptException>(() => Parser.ParseScript("SOLVE POWERFLOW\nREPORT BUSES\nTRIP\n"));
    }

    [Fact]
    public void Console_line_errors_carry_the_given_line_number()
    {
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseLineText("TRIP", 17));
        Assert.Equal(17, e.Pos.Line);
        Assert.Null(Parser.ParseLineText("   # nothing", 4));
    }

    [Fact]
    public void Grammar_has_no_loops_and_no_clock_or_network_verbs()
    {
        foreach (string verb in new[] { "WHILE", "LOOP", "UNTIL", "REPEAT", "SLEEP", "NOW", "RANDOM", "FETCH", "READ", "WRITE", "EXEC" })
        {
            ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(verb + " 1"));
            Assert.Contains("unknown verb", e.Message, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("WHILE", Grammar.Verbs);
    }

    [Theory]
    [InlineData("MW")] [InlineData("MVAR")] [InlineData("MVA")] [InlineData("PU")] [InlineData("KV")]
    [InlineData("HZ")] [InlineData("MS")] [InlineData("S")] [InlineData("DEG")]
    public void Every_unit_keyword_is_known_in_any_case(string unit)
    {
        Assert.True(Units.TryParse(unit.ToLowerInvariant(), out Unit u));
        Assert.Equal(unit, Units.Keyword(u));
    }
}
