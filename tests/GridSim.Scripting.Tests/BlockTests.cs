using System.Text.Json;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>Layers 3 to 6: FOR EACH, IF / ELSE, named values with unit-checked arithmetic, and procedures.</summary>
public class BlockTests
{
    private static Session Loaded(params string[] more)
    {
        var s = new Session(TestPaths.TempDir());
        TestPaths.Exec(s, [TestPaths.Load("case9"), .. more]);
        return s;
    }

    private static ScriptException Refused(Session s, params string[] lines)
    {
        string text = string.Join("\n", lines);
        // Parse and run statement by statement; the error of the first refused one is the answer.
        foreach (Statement st in Parser.ParseScript(text))
        {
            try { s.Execute(st); }
            catch (ScriptException e) { return e; }
        }
        throw new Xunit.Sdk.XunitException("expected the script to be refused, but it ran");
    }

    private static string Expr(Session s, string expr) => TestPaths.Exec(s, "PRINT " + expr).TrimEnd('\n');

    // ---- parsing -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Blocks_round_trip_through_canonical_form()
    {
        string text = string.Join("\n",
            "proc shed(generator g, mw cut)",
            "  if p of generator g > cut and not line \"4-5\" is tripped",
            "    set generator g p p of generator g - cut",
            "  else",
            "    for each load l where q of load l >= 10 mvar or p of load l < 5 mw",
            "      trip load l",
            "    end",
            "  end",
            "end",
            "let x = -(2 mw + 3 mw) * 2",
            "call shed(\"G2\", x / -1)");
        IReadOnlyList<Statement> list = Parser.ParseScript(text);
        Assert.Equal(3, list.Count);
        string canonical = string.Join("\n", list.Select(s => s.Canonical()));
        Assert.Contains("PROC shed(GENERATOR g, MW cut)", canonical, StringComparison.Ordinal);
        Assert.Contains("\n  IF P OF GENERATOR g > cut AND NOT LINE \"4-5\" IS TRIPPED\n    SET GENERATOR g P P OF GENERATOR g - cut\n  ELSE\n", canonical, StringComparison.Ordinal);
        Assert.Contains("LET x = -(2 MW + 3 MW) * 2", canonical, StringComparison.Ordinal);
        IReadOnlyList<Statement> again = Parser.ParseScript(canonical);
        Assert.Equal(canonical, string.Join("\n", again.Select(s => s.Canonical())));
    }

    [Theory]
    [InlineData("FOR EACH GENERATOR g\n  TRIP GENERATOR g\n", 3, 1, "unfinished block")]
    [InlineData("FOR EACH WIDGET w\nEND", 1, 10, "expected GENERATOR, LINE, LOAD or BUS")]
    [InlineData("FOR EACH GENERATOR WHERE\nEND", 1, 20, "is a keyword")]
    [InlineData("IF 5 MW\n  SOLVE POWERFLOW\nEND", 1, 8, "expected a comparison")]
    [InlineData("IF TIME > 5 MS SOLVE POWERFLOW\nEND", 1, 16, "the body of IF starts on the next line")]
    [InlineData("END", 1, 1, "unknown verb")]
    [InlineData("FOR EACH LOAD l\n  LOAD CASE \"x.m\"\nEND", 2, 3, "cannot sit inside a block")]
    [InlineData("IF TIME > 1 MS\n  PROC p\n  END\nEND", 2, 3, "cannot sit inside a block")]
    [InlineData("LET load = 5 MW", 1, 5, "is a keyword")]
    [InlineData("LET x 5 MW", 1, 7, "expected =")]
    [InlineData("LET x = 5 MW +", 1, 15, "expected a value")]
    [InlineData("PRINT P OF GENERATOR", 1, 21, "expected the generator name")]
    [InlineData("PRINT ABS(1, 2)", 1, 12, "expected )")]
    [InlineData("PRINT MIN(1)", 1, 12, "takes two values")]
    [InlineData("PROC pr(WIDGET w)\nEND", 1, 9, "expected a parameter type")]
    [InlineData("PROC pr(MW a, PU a)\nEND", 1, 18, "named twice")]
    [InlineData("CALL", 1, 5, "the procedure name")]
    [InlineData("SET x = ", 1, 9, "expected a value")]
    [InlineData("FAULT LINE \"1-4\"", 1, 7, "expected BUS after FAULT")]
    public void Block_and_expression_syntax_errors_name_line_and_column(string text, int line, int col, string fragment)
    {
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(text));
        Assert.Equal(ErrorPhase.Syntax, e.Phase);
        Assert.Equal(new SourcePos(line, col), e.Pos);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfinished_block_is_marked_so_a_console_keeps_reading()
    {
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseStatementText("FOR EACH LOAD l\n  TRIP LOAD l"));
        Assert.True(e.Unfinished);
        Assert.NotNull(Parser.ParseStatementText("FOR EACH LOAD l\n  TRIP LOAD l\nEND"));
        Assert.False(Assert.Throws<ScriptException>(() => Parser.ParseStatementText("TRIP")).Unfinished);
    }

    [Fact]
    public void Blocks_nest_to_a_fixed_depth_and_no_further()
    {
        string Nest(int n) => string.Concat(Enumerable.Repeat("IF TIME = 0 MS\n", n)) + "SOLVE POWERFLOW\n" + string.Concat(Enumerable.Repeat("END\n", n));
        Assert.Single(Parser.ParseScript(Nest(Grammar.MaxBlockDepth)));
        ScriptException e = Assert.Throws<ScriptException>(() => Parser.ParseScript(Nest(Grammar.MaxBlockDepth + 1)));
        Assert.Contains("nested deeper", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_while_and_no_loop_but_over_a_finite_collection()
    {
        foreach (string verb in new[] { "WHILE", "LOOP", "UNTIL", "REPEAT", "GOTO", "SLEEP", "RANDOM", "NOW" })
            Assert.Contains("unknown verb", Assert.Throws<ScriptException>(() => Parser.ParseScript(verb + " 1")).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WHILE", Grammar.Verbs);
        Assert.Contains("FOR", Grammar.Verbs);
    }

    // ---- layer 3: FOR EACH -----------------------------------------------------------------------------------------------

    [Fact]
    public void For_each_runs_over_the_matching_elements_in_model_order()
    {
        Session s = Loaded();
        TestPaths.Exec(s,
            "FOR EACH GENERATOR g WHERE P OF GENERATOR g > 80 MW",
            "  TRIP GENERATOR g",
            "END");
        LoopEntry loop = Assert.Single(s.Loops);
        Assert.Equal(3, loop.Total);
        Assert.Equal(new[] { "G2", "G3" }, loop.Matched.ToArray());
        Assert.True(s.Plant!.Model.AllGenerators[0].InService);
        Assert.False(s.Plant.Model.AllGenerators[1].InService);
        Assert.False(s.Plant.Model.AllGenerators[2].InService);
        // The record shows the statement as written and as it ran, one entry per iteration.
        StatementEntry[] trips = s.Statements.Where(x => x.Text == "TRIP GENERATOR g").ToArray();
        Assert.Equal(["TRIP GENERATOR \"G2\"", "TRIP GENERATOR \"G3\""], trips.Select(t => t.Resolved));
        Assert.All(trips, t => Assert.Equal(1, t.Depth));
    }

    [Fact]
    public void For_each_covers_every_noun_and_nests()
    {
        Session s = Loaded("SOLVE POWERFLOW");
        string o = TestPaths.Exec(s,
            "FOR EACH LOAD l",
            "  FOR EACH GENERATOR g WHERE P OF GENERATOR g < 80 MW",
            "    PRINT P OF LOAD l",
            "  END",
            "END",
            "FOR EACH BUS b WHERE VM OF BUS b < 1 PU",
            "  PRINT VM OF BUS b",
            "END",
            "FOR EACH LINE ln WHERE LINE ln IS IN SERVICE AND LOADING OF LINE ln > 0.6 PU",
            "  PRINT LOADING OF LINE ln",
            "END");
        Assert.All(s.Loops.Where(l => l.Noun == "GENERATOR"), l => Assert.Equal(new[] { "G1" }, l.Matched.ToArray()));   // G1 has P = 0; the inner loop ran once per load
        Assert.Equal(3, s.Loops.Count(l => l.Noun == "GENERATOR"));
        Assert.Equal(3, o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("P OF LOAD", StringComparison.Ordinal)));
        Assert.Contains("VM OF BUS b = 0.99", o, StringComparison.Ordinal);
        Assert.Contains("LOADING OF LINE ln = 0.65", o, StringComparison.Ordinal);
        Assert.Equal(9, s.Loops.Single(l => l.Noun == "BUS").Total);
        Assert.Equal(9, s.Loops.Single(l => l.Noun == "LINE").Total);
    }

    [Fact]
    public void A_name_that_is_not_in_the_model_is_refused_before_the_loop_starts()
    {
        Session s = Loaded();
        ScriptException e = Refused(s,
            "FOR EACH LOAD l",
            "  TRIP LOAD l",
            "  TRIP GENERATOR \"Nope\"",
            "END");
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(new SourcePos(3, 18), e.Pos);
        Assert.Contains("no generator named \"Nope\"", e.Message, StringComparison.Ordinal);
        Assert.Empty(s.Plant!.TrippedLoads);   // nothing ran
        Assert.DoesNotContain(s.Statements, x => x.Text.StartsWith("TRIP", StringComparison.Ordinal));
    }

    [Fact]
    public void A_loop_variable_has_the_type_of_its_noun_and_is_not_visible_outside()
    {
        Session s = Loaded();
        ScriptException wrongNoun = Refused(s, "FOR EACH GENERATOR g", "  TRIP LINE g", "END");
        Assert.Equal(ErrorPhase.Validation, wrongNoun.Phase);
        Assert.Equal(new SourcePos(2, 13), wrongNoun.Pos);
        Assert.Contains("holds a generator, not a line", wrongNoun.Message, StringComparison.Ordinal);

        Assert.Contains("no value named 'g'", Refused(s, "FOR EACH GENERATOR g", "END", "TRIP GENERATOR g").Message, StringComparison.Ordinal);
        Assert.Contains("already names a value", Refused(s, "LET g = 1 MW", "FOR EACH GENERATOR g", "END").Message, StringComparison.Ordinal);
        Assert.Contains("a number or a quantity is needed", Refused(Loaded(), "FOR EACH GENERATOR g", "  LET x = g + 1", "END").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_where_clause_is_unit_checked_at_validation_with_its_column()
    {
        Session s = Loaded();
        string line = "FOR EACH GENERATOR g WHERE P OF GENERATOR g > 5 MVAR";
        ScriptException e = Refused(s, line, "END");
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(new SourcePos(1, line.IndexOf('>', StringComparison.Ordinal) + 1), e.Pos);
        Assert.Contains("unit mismatch: cannot compare MW with MVAR", e.Message, StringComparison.Ordinal);
        Assert.Contains("V does not apply to a load; V reads a GENERATOR", Refused(s, "FOR EACH LOAD l WHERE V OF LOAD l > 1 PU", "END").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_half_way_through_a_loop_undoes_the_whole_loop()
    {
        Session s = Loaded("SOLVE POWERFLOW");
        Plant before = s.Plant!;
        int statements = s.Statements.Count;
        ScriptException e = Refused(s,
            "FOR EACH LOAD l",
            "  TRIP LOAD l",
            "  TRIP LOAD l",
            "END");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Equal(new SourcePos(3, 13), e.Pos);
        Assert.Contains("already tripped", e.Message, StringComparison.Ordinal);
        Assert.Same(before, s.Plant);
        Assert.Equal(statements, s.Statements.Count);
        Assert.Empty(s.Loops);
    }

    [Fact]
    public void A_reading_that_needs_a_solve_says_so()
    {
        Session s = Loaded();
        ScriptException e = Refused(s, "FOR EACH BUS b WHERE VM OF BUS b < 1 PU", "END");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("no solved state", e.Message, StringComparison.Ordinal);
    }

    // ---- layer 4: IF / ELSE ----------------------------------------------------------------------------------------------

    [Fact]
    public void If_reads_state_results_and_time()
    {
        Session s = Loaded("SOLVE POWERFLOW");
        string o = TestPaths.Exec(s,
            "IF VM OF BUS \"9\" < 1 PU",
            "  PRINT 1",
            "ELSE",
            "  PRINT 2",
            "END",
            "IF GENERATOR \"G2\" IS TRIPPED",
            "  PRINT 3",
            "ELSE",
            "  PRINT 4",
            "END",
            "IF TIME = 0 MS AND NOT LOSSES > 10 MW",
            "  PRINT 5",
            "END",
            "SELECT ENGINE LIVE",
            "STEP SIM 100 MS",
            "IF TIME >= 0.1 S AND FREQUENCY < 70 HZ",
            "  PRINT 6",
            "END");
        Assert.Equal(new[] { "1 = 1", "4 = 4", "5 = 5", "6 = 6" }, o.Split((char)10, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length < 8).ToArray());
        Assert.Equal(4, s.Decisions.Count);
        DecisionEntry d = s.Decisions[0];
        Assert.True(d.Result);
        Assert.Equal("VM OF BUS \"9\" < 1 PU", d.Condition);
        Assert.Contains("PU < 1 PU is true", d.Detail, StringComparison.Ordinal);
        Assert.False(s.Decisions[1].Result);
    }

    [Fact]
    public void If_with_a_mismatched_comparison_is_refused_before_anything_in_it_runs()
    {
        Session s = Loaded("SOLVE POWERFLOW");
        string line = "IF P OF LOAD \"L5\" > 50 MVAR";
        ScriptException e = Refused(s, line, "  TRIP LOAD \"L5\"", "END");
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(new SourcePos(1, line.IndexOf('>', StringComparison.Ordinal) + 1), e.Pos);
        Assert.Empty(s.Plant!.TrippedLoads);
        Assert.Contains("unit mismatch", Refused(s, "IF 5 MW = 5", "END").Message, StringComparison.Ordinal);
        Assert.Contains("unit mismatch", Refused(s, "IF TIME > 5 MW", "END").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_on_a_stale_solve_is_an_execution_error_with_the_line()
    {
        Session s = Loaded("SOLVE POWERFLOW", "TRIP LOAD \"L5\"");
        ScriptException e = Refused(s, "IF VM OF BUS \"5\" < 1 PU", "  PRINT 1", "END");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("out of date", e.Message, StringComparison.Ordinal);
        Assert.Equal(new SourcePos(1, 1), e.Pos);
    }

    // ---- layer 5: named values and arithmetic ----------------------------------------------------------------------------

    [Fact]
    public void Arithmetic_keeps_units_and_precedence()
    {
        Session s = Loaded();
        Assert.Equal("2 MW + 3 MW * 2 = 8 MW", Expr(s, "2 MW + 3 MW * 2"));
        Assert.Equal("(2 MW + 3 MW) * 2 = 10 MW", Expr(s, "(2 MW + 3 MW) * 2"));
        Assert.Equal("5 MW / 2 MW = 2.5", Expr(s, "5 MW / 2 MW"));
        Assert.Equal("10 MW / 4 = 2.5 MW", Expr(s, "10 MW / 4"));
        Assert.Equal("1 S + 500 MS = 1.5 S", Expr(s, "1 S + 500 MS"));
        Assert.Equal("500 MS + 1 S = 1500 MS", Expr(s, "500 MS + 1 S"));
        Assert.Equal("-2 MW = -2 MW", Expr(s, "-(2 MW)"));
        Assert.Equal("-(x) = -5 MW".Replace("x", "2 MW + 3 MW"), Expr(s, "-(2 MW + 3 MW)"));
        Assert.Equal("5 MW - -3 MW = 8 MW", Expr(s, "5 MW - -3 MW"));
        Assert.Equal("ABS(2 MW - 5 MW) = 3 MW", Expr(s, "ABS(2 MW - 5 MW)"));
        Assert.Equal("MIN(2 MW, 5 MW) = 2 MW", Expr(s, "MIN(2 MW, 5 MW)"));
        Assert.Equal("MAX(1 S, 500 MS) = 1 S", Expr(s, "MAX(1 S, 500 MS)"));
        Assert.Equal("1 / 4 = 0.25", Expr(s, "1 / 4"));
    }

    [Theory]
    [InlineData("LET x = 5 MW + 3 MVAR", "unit mismatch: cannot add MVAR to MW", 14)]
    [InlineData("LET x = 5 MW - 3 PU", "unit mismatch: cannot subtract PU from MW", 14)]
    [InlineData("LET x = 5 MW + 3", "unit mismatch: cannot add a number without a unit to MW", 14)]
    [InlineData("LET x = 2 MW * 3 MW", "is not a unit grammar 1 has", 14)]
    [InlineData("LET x = 2 / 3 MW", "is not a unit grammar 1 has", 11)]
    [InlineData("LET x = 5 MW / 2 MVAR", "is not a unit grammar 1 has", 14)]
    [InlineData("LET x = ABS(1 MW) + MIN(1 MW, 2 MVAR)", "unit mismatch: MIN needs both values in the same unit", 31)]
    [InlineData("LET x = \"G1\"", "name in quotes", 9)]
    [InlineData("LET x = y", "no value named 'y'", 9)]
    public void A_unit_mismatch_is_refused_at_validation_with_its_column(string text, string fragment, int col)
    {
        Session s = Loaded();
        ScriptException e = Refused(s, text);
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(new SourcePos(2, col).Column, e.Pos.Column);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.Empty(s.Globals);
    }

    [Fact]
    public void Named_values_are_created_with_let_changed_with_set_and_type_checked()
    {
        Session s = Loaded();
        TestPaths.Exec(s, "LET limit = 80 MW", "LET spare = limit - P OF GENERATOR \"G3\"", "SET limit = limit + 5 MW");
        Assert.Equal(85m, s.Globals["limit"].Number);
        Assert.Equal(-5m, s.Globals["spare"].Number);
        Assert.Equal("spare = -5 MW", Expr(s, "spare").Replace("PRINT ", "", StringComparison.Ordinal));
        Assert.Contains("unit mismatch: 'limit' holds MW, cannot take MVAR", Refused(s, "SET limit = 5 MVAR").Message, StringComparison.Ordinal);
        Assert.Contains("already names a value", Refused(s, "LET limit = 1 MW").Message, StringComparison.Ordinal);
        Assert.Contains("no value named 'nope'", Refused(s, "SET nope = 1 MW").Message, StringComparison.Ordinal);

        // A value made inside a block is local to it, and SET inside a block changes the outer value.
        TestPaths.Exec(s, "LET total = 0 MW", "FOR EACH LOAD l", "  LET mine = P OF LOAD l", "  SET total = total + mine", "END");
        Assert.Equal(315m, s.Globals["total"].Number);
        Assert.False(s.Globals.ContainsKey("mine"));
        Assert.Contains("no value named 'mine'", Refused(s, "PRINT mine").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_in_an_expression_can_drive_a_set_a_step_and_a_schedule()
    {
        Session s = Loaded("SELECT ENGINE LIVE");
        TestPaths.Exec(s,
            "LET t = 2 * 20 MS",
            "LET cut = 10 MW",
            "SET GENERATOR \"G2\" P P OF GENERATOR \"G2\" - cut",
            "AT t TRIP LOAD \"L5\"",
            "STEP SIM t + t");
        Assert.Equal(153m, (decimal)s.Plant!.Model.AllGenerators[1].Pg);
        Assert.Equal(80_000, s.ClockUs);
        Assert.Equal(40_000, s.AppliedEvents.Single().TimeUs);
        Assert.Equal("AT 40 MS TRIP LOAD \"L5\"", s.Statements.Single(x => x.Text.StartsWith("AT", StringComparison.Ordinal)).Resolved);
        // A computed step that is not a whole multiple is refused with the same rule as a written one.
        Assert.Contains("whole multiple", Refused(s, "LET odd = 25 MS", "STEP SIM odd").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Division_by_zero_and_non_finite_readings_are_execution_errors()
    {
        Session s = Loaded();
        ScriptException e = Refused(s, "LET z = 0 MW", "PRINT 5 MW / z");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("division by zero", e.Message, StringComparison.Ordinal);
        Assert.Equal(new SourcePos(2, 12), e.Pos);
    }

    // ---- layer 6: procedures ---------------------------------------------------------------------------------------------

    private const string ShedProc = "PROC shed(GENERATOR g, MW cut)\n  SET GENERATOR g P P OF GENERATOR g - cut\nEND";

    [Fact]
    public void A_procedure_is_called_with_typed_arguments()
    {
        Session s = Loaded();
        TestPaths.Exec(s, ShedProc, "CALL shed(\"G2\", 10 MW)", "FOR EACH GENERATOR g WHERE P OF GENERATOR g > 80 MW", "  CALL shed(g, 1 MW)", "END");
        Assert.Equal(152.0, s.Plant!.Model.AllGenerators[1].Pg);
        Assert.Equal(84.0, s.Plant.Model.AllGenerators[2].Pg);
        ProcEntry pe = Assert.Single(s.ProcEntries);
        Assert.Equal("shed", pe.Name);
        StatementEntry call = s.Statements.First(x => x.Text.StartsWith("CALL", StringComparison.Ordinal));
        Assert.Equal("CALL shed(GENERATOR \"G2\", 10 MW)", call.Resolved);
        Assert.Contains(s.Statements, x => x.Depth == 1 && x.Text.StartsWith("SET GENERATOR g", StringComparison.Ordinal));
    }

    [Fact]
    public void A_procedure_sees_the_session_values_of_its_definition_but_not_the_callers_locals()
    {
        Session s = Loaded("LET base = 5 MW");
        TestPaths.Exec(s, "PROC bump", "  SET base = base + 1 MW", "END", "CALL bump", "CALL bump");
        Assert.Equal(7m, s.Globals["base"].Number);
        Assert.Equal(ErrorPhase.Syntax, Assert.Throws<ScriptException>(() => Parser.ParseScript("FOR EACH LOAD l\n  PROC inner\n  END\nEND")).Phase);
        // The body is checked where it is defined: a value that is not there, and not a parameter, is refused.
        ScriptException bad = Refused(s, "PROC peek", "  PRINT mine", "END");
        Assert.Equal(ErrorPhase.Validation, bad.Phase);
        Assert.Equal(new SourcePos(2, 9), bad.Pos);
    }

    [Fact]
    public void A_call_with_the_wrong_arguments_is_refused()
    {
        Session s = Loaded(ShedProc);
        Assert.Contains("takes 2 argument(s), the call gives 1", Refused(s, "CALL shed(\"G2\")").Message, StringComparison.Ordinal);
        ScriptException unit = Refused(s, "CALL shed(\"G2\", 10 MVAR)");
        Assert.Contains("unit mismatch: argument 2 of 'shed' must be MW, got MVAR", unit.Message, StringComparison.Ordinal);
        Assert.Contains("argument 1 of 'shed' must be a generator, got MW", Refused(s, "CALL shed(5 MW, 1 MW)").Message, StringComparison.Ordinal);
        Assert.Contains("no generator named \"Nope\"", Refused(s, "CALL shed(\"Nope\", 1 MW)").Message, StringComparison.Ordinal);
        Assert.Contains("no procedure named 'nope'", Refused(s, "CALL nope").Message, StringComparison.Ordinal);
        Assert.Contains("already defined", Refused(s, ShedProc).Message, StringComparison.Ordinal);
        Assert.Contains("argument 1 of 'shed' must be a generator, got a load", Refused(s, "FOR EACH LOAD l", "  CALL shed(l, 1 MW)", "END").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_inside_a_procedure_names_the_line_in_the_body_and_the_call()
    {
        Session s = Loaded("PROC cut_load(LOAD l)", "  TRIP LOAD l", "  TRIP LOAD l", "END");
        ScriptException e = Refused(s, "CALL cut_load(\"L5\")");
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("already tripped", e.Message, StringComparison.Ordinal);
        Assert.Equal(new SourcePos(4, 13), e.Pos);
        Assert.Empty(s.Plant!.TrippedLoads);
    }

    [Fact]
    public void Recursion_is_refused_directly_and_through_other_procedures()
    {
        Session s = Loaded();
        ScriptException direct = Refused(s, "PROC a", "  CALL a", "END");
        Assert.Equal(ErrorPhase.Validation, direct.Phase);
        Assert.Equal(new SourcePos(2, 8), direct.Pos);
        Assert.Contains("calls itself: recursion is not allowed", direct.Message, StringComparison.Ordinal);

        // Through another procedure, defined one at a time as a console would.
        TestPaths.Exec(s, "PROC b", "  CALL c", "END");   // c is not there yet: looked up when b is called
        ScriptException indirect = Refused(s, "PROC c", "  CALL b", "END");
        Assert.Equal(new SourcePos(2, 8), indirect.Pos);
        Assert.Contains("leads back to 'c': recursion is not allowed", indirect.Message, StringComparison.Ordinal);
        Assert.Contains("no procedure named 'c'", Refused(s, "CALL b").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_script_with_a_cycle_of_procedures_is_refused_whole_before_anything_runs()
    {
        string script = string.Join("\n", TestPaths.Load("case9"), "PROC a", "  CALL b", "END", "SOLVE POWERFLOW", "PROC b", "  IF TIME = 0 MS", "    CALL a", "  END", "END", "");
        RunResult r = Runner.Run(System.Text.Encoding.UTF8.GetBytes(script), new RunOptions(TestPaths.TempDir()));
        Assert.False(r.Ok);
        Assert.Equal(ErrorPhase.Validation, r.Error!.Phase);
        Assert.Equal(new SourcePos(3, 8), r.Error.Pos);
        Assert.Contains("procedure 'a' calls 'b', which leads back to 'a'", r.Error.Message, StringComparison.Ordinal);
        Assert.Empty(r.Session.Statements);   // not even the LOAD ran
        string dup = string.Join("\n", "PROC pr", "END", "PROC pr", "END", "");
        Assert.Contains("already defined at line 1", Runner.Run(System.Text.Encoding.UTF8.GetBytes(dup), new RunOptions(TestPaths.TempDir())).Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forward_call_between_procedures_works_when_the_callee_is_defined_by_the_time_of_the_call()
    {
        string script = string.Join("\n", TestPaths.Load("case9"),
            "PROC outer", "  CALL inner(1 MW)", "END",
            "PROC inner(MW x)", "  PRINT x", "END",
            "CALL outer", "");
        var o = new StringWriter();
        RunResult r = Runner.Run(System.Text.Encoding.UTF8.GetBytes(script), new RunOptions(TestPaths.TempDir(), o));
        Assert.True(r.Ok, r.Error?.Display);
        Assert.Contains("x = 1 MW", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_script_terminates_the_work_is_bounded_by_the_model_and_the_text()
    {
        // Nested loops over the model run exactly (elements)^depth times; there is nothing that can run for ever.
        Session s = Loaded();
        TestPaths.Exec(s, "LET n = 0", "FOR EACH BUS a", "  FOR EACH BUS b", "    FOR EACH BUS c", "      SET n = n + 1", "    END", "  END", "END");
        Assert.Equal(729m, s.Globals["n"].Number);
    }

    // ---- provenance (layer 7) ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_run_record_holds_the_decisions_loops_procedures_values_and_the_canonical_script_hash()
    {
        string script = string.Join("\n",
            "GRAMMAR 1",
            TestPaths.Load("case9"),
            "SOLVE POWERFLOW",
            "LET lim = 95 MW",
            "PROC cut(LOAD l)",
            "  SET LOAD l P P OF LOAD l - 1 MW",
            "END",
            "FOR EACH LOAD l WHERE P OF LOAD l >= lim",
            "  CALL cut(l)",
            "END",
            "IF lim > 50 MW",
            "  PRINT lim",
            "END",
            "");
        RunResult r = Runner.Run(System.Text.Encoding.UTF8.GetBytes(script), new RunOptions(TestPaths.TempDir()));
        Assert.True(r.Ok, r.Error?.Display);
        using JsonDocument doc = JsonDocument.Parse(r.RecordJson);
        JsonElement root = doc.RootElement;
        Assert.Equal(64, root.GetProperty("canonicalScriptSha256").GetString()!.Length);
        Assert.Equal("cut", root.GetProperty("procedures")[0].GetProperty("name").GetString());
        JsonElement loop = root.GetProperty("loops")[0];
        Assert.Equal(3, loop.GetProperty("elements").GetInt32());
        Assert.Equal(new[] { "L7", "L9" }, loop.GetProperty("matched").EnumerateArray().Select(x => x.GetString()!).ToArray());
        JsonElement dec = root.GetProperty("decisions")[0];
        Assert.True(dec.GetProperty("result").GetBoolean());
        Assert.Equal("lim > 50 MW", dec.GetProperty("condition").GetString());
        Assert.Equal("lim", root.GetProperty("variables")[0].GetProperty("name").GetString());
        Assert.Equal("95 MW", root.GetProperty("variables")[0].GetProperty("value").GetString());
        JsonElement[] stmts = root.GetProperty("statements").EnumerateArray().ToArray();
        JsonElement call = stmts.First(x => x.GetProperty("text").GetString()!.StartsWith("CALL", StringComparison.Ordinal));
        Assert.Equal("CALL cut(LOAD \"L7\")", call.GetProperty("resolved").GetString());
        Assert.Equal(2, stmts.Count(x => x.TryGetProperty("depth", out JsonElement d) && d.GetInt32() == 2));
    }

    [Fact]
    public void The_canonical_hash_ignores_comments_spacing_and_keyword_case_but_not_meaning()
    {
        string a = string.Join("\n", TestPaths.Load("case9"), "LET x = 5 MW", "PRINT x + 1 MW", "");
        string b = string.Join("\n", "# a comment", TestPaths.Load("case9"), "  let   x   =   5   mw  # why", "", "print x + 1 mw", "");
        string c = a.Replace("5 MW", "6 MW", StringComparison.Ordinal);
        RunResult ra = Runner.Run(System.Text.Encoding.UTF8.GetBytes(a), new RunOptions(TestPaths.TempDir()));
        RunResult rb = Runner.Run(System.Text.Encoding.UTF8.GetBytes(b), new RunOptions(TestPaths.TempDir()));
        RunResult rc = Runner.Run(System.Text.Encoding.UTF8.GetBytes(c), new RunOptions(TestPaths.TempDir()));
        Assert.True(ra.Ok && rb.Ok && rc.Ok);
        Assert.Equal(ra.Session.CanonicalScriptSha256, rb.Session.CanonicalScriptSha256);
        Assert.NotEqual(ra.Session.CanonicalScriptSha256, rc.Session.CanonicalScriptSha256);
        using JsonDocument da = JsonDocument.Parse(ra.RecordJson), db = JsonDocument.Parse(rb.RecordJson);
        Assert.NotEqual(da.RootElement.GetProperty("scriptSha256").GetString(), db.RootElement.GetProperty("scriptSha256").GetString());
        Assert.Equal(da.RootElement.GetProperty("canonicalScriptSha256").GetString(), db.RootElement.GetProperty("canonicalScriptSha256").GetString());
    }

    [Fact]
    public void Layers_three_to_six_give_identical_bytes_run_twice_and_in_a_fresh_session()
    {
        string script = string.Join("\n",
            TestPaths.Load("case9"),
            "SOLVE POWERFLOW",
            "LET lim = 90 MW",
            "PROC cut(LOAD l, MW by)",
            "  SET LOAD l P P OF LOAD l - by",
            "END",
            "FOR EACH LOAD l WHERE P OF LOAD l >= lim",
            "  CALL cut(l, 5 MW)",
            "END",
            "SOLVE POWERFLOW",
            "IF LOSSES < 10 MW",
            "  EXPORT RESULT \"out/a.json\"",
            "ELSE",
            "  PRINT LOSSES",
            "END",
            "FOR EACH BUS b WHERE VM OF BUS b < 1 PU",
            "  PRINT VM OF BUS b",
            "END",
            "");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(script);
        (string Record, string Out, string File) Go()
        {
            string dir = TestPaths.TempDir();
            var o = new StringWriter();
            RunResult r = Runner.Run(bytes, new RunOptions(dir, o));
            Assert.True(r.Ok, r.Error?.Display);
            return (r.RecordJson, o.ToString(), File.ReadAllText(Path.Combine(dir, "out", "a.json")));
        }
        var first = Go();
        var second = Go();
        (string Record, string Out, string File)? third = null;
        var t = new Thread(() => third = Go());
        t.Start();
        t.Join();
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }
}
