using System.Text;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>
/// 2.1.074 security hardening of the scripting library: the parser's fixed expression nesting and depth limits (a deeply nested
/// script is a syntax error naming line and column, never a crashed host), the hosting limits checked inside blocks (cancellation,
/// the statement budget, the output budget, the open-block cap), the confined base folder, the hidden base folder and the licence
/// gate. With no hosting hook set, a valid script's run record is byte for byte what it was.
/// </summary>
public sealed class HostingSafetyTests
{
    /// <summary>The acceptance side of the limits runs on a 1 MB stack, the default stack of a Windows thread (the smallest default of the
    /// platforms GridSim ships for). On the 256 KB stack the second guard (the execution-stack check) may refuse a script at the limit
    /// cleanly; <see cref="On_a_small_stack_every_depth_is_parsed_or_refused_never_a_crash"/> covers that.</summary>
    private static T OnDefaultStack<T>(Func<T> work) => OnStack(1024 * 1024, work);

    /// <summary>Run <paramref name="work"/> on a thread with a small stack (256 KB, well under any platform's default), so a parse that
    /// recursed with the depth of its input would end the test process here rather than pass. Every refusal test runs here.</summary>
    private static T OnSmallStack<T>(Func<T> work) => OnStack(256 * 1024, work);

    /// <summary>Run <paramref name="work"/> on a thread with a stack of <paramref name="bytes"/>; a parse that
    /// recursed with the depth of its input would end the test process here rather than pass.</summary>
    private static T OnStack<T>(int bytes, Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        }, bytes);
        t.Start();
        t.Join();
        if (failure is not null) throw new InvalidOperationException("the work threw", failure);
        return result;
    }

    private static ScriptException CheckRefused(string script)
    {
        ScriptException? e = OnSmallStack(() => Runner.Check(script));
        Assert.NotNull(e);
        Assert.Equal(ErrorPhase.Syntax, e!.Phase);
        Assert.True(e.Pos.Line > 0 && e.Pos.Column > 0, e.Display);
        return e;
    }

    public static TheoryData<string> DeepScripts()
    {
        const int n = 4000;
        return new TheoryData<string>
        {
            "GRAMMAR 1\nPRINT " + new string('(', n) + "1" + new string(')', n) + "\n",
            "GRAMMAR 1\nPRINT " + string.Concat(Enumerable.Repeat("- ", n)) + "1\n",
            "GRAMMAR 1\nPRINT " + string.Concat(Enumerable.Repeat("-(", n)) + "1" + new string(')', n) + "\n",
            "GRAMMAR 1\nIF " + string.Concat(Enumerable.Repeat("NOT ", n)) + "1 MW < 2 MW\nEND\n",
            "GRAMMAR 1\nIF " + new string('(', n) + "1 MW < 2 MW" + new string(')', n) + "\nEND\n",
            "GRAMMAR 1\nPRINT " + string.Concat(Enumerable.Repeat("ABS(", n)) + "1" + new string(')', n) + "\n",
            // About 64 KB of brackets, the web console's script limit.
            "GRAMMAR 1\nPRINT " + new string('(', 32 * 1024) + "1" + new string(')', 32 * 1024) + "\n",
        };
    }

    [Theory]
    [MemberData(nameof(DeepScripts))]
    public void A_deeply_nested_expression_is_a_syntax_error_on_line_2_not_a_crash(string script)
    {
        ScriptException e = CheckRefused(script);
        Assert.Equal(2, e.Pos.Line);
        Assert.Contains("nests too deeply", e.Message, StringComparison.Ordinal);
        Assert.Contains(Grammar.MaxExpressionNesting.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_nesting_limit_admits_exactly_its_own_depth()
    {
        int max = Grammar.MaxExpressionNesting;
        string at = "GRAMMAR 1\nPRINT " + new string('(', max) + "1" + new string(')', max) + "\n";
        Assert.Null(OnDefaultStack(() => Runner.Check(at)));
        string over = "GRAMMAR 1\nPRINT " + new string('(', max + 1) + "1" + new string(')', max + 1) + "\n";
        ScriptException? e = OnDefaultStack(() => Runner.Check(over));
        Assert.NotNull(e);
        Assert.Equal(new SourcePos(2, 7 + max), e!.Pos);   // the bracket that goes past the limit
        CheckRefused(over);   // and refused on the small stack too
    }

    [Fact]
    public void On_a_small_stack_every_depth_is_parsed_or_refused_never_a_crash()
    {
        // Each depth from 1 to past the limit, in every nesting form, on the 256 KB stack: the answer is ok or a syntax error with its
        // line and column. A depth the stack cannot hold is refused by the execution-stack guard, never an overflow.
        for (int d = 1; d <= Grammar.MaxExpressionNesting + 8; d++)
        {
            foreach (string script in new[]
            {
                "GRAMMAR 1\nPRINT " + new string('(', d) + "1" + new string(')', d) + "\n",
                "GRAMMAR 1\nPRINT " + string.Concat(Enumerable.Repeat("-(", d)) + "1 MW" + new string(')', d) + "\n",
                "GRAMMAR 1\nIF " + string.Concat(Enumerable.Repeat("NOT ", d)) + "1 MW < 2 MW\nEND\n",
                "GRAMMAR 1\nIF " + new string('(', d) + "1 MW < 2 MW" + new string(')', d) + "\nEND\n",
            })
            {
                ScriptException? e = OnSmallStack(() => Runner.Check(script));
                if (e is not null)
                {
                    Assert.Equal(ErrorPhase.Syntax, e.Phase);
                    Assert.Equal(2, e.Pos.Line);
                }
                if (d > Grammar.MaxExpressionNesting) Assert.NotNull(e);
            }
        }
    }

    [Fact]
    public void A_long_operator_chain_is_refused_past_the_depth_limit_and_admitted_at_it()
    {
        int max = Grammar.MaxExpressionDepth;
        string at = "GRAMMAR 1\nPRINT 1" + string.Concat(Enumerable.Repeat(" + 1", max)) + "\n";
        Assert.Null(OnDefaultStack(() => Runner.Check(at)));
        ScriptException e = CheckRefused("GRAMMAR 1\nPRINT 1" + string.Concat(Enumerable.Repeat(" + 1", max + 1)) + "\n");
        Assert.Contains("too deep", e.Message, StringComparison.Ordinal);
        CheckRefused("GRAMMAR 1\nPRINT 1" + string.Concat(Enumerable.Repeat(" + 1", 16 * 1024)) + "\n");
        CheckRefused("GRAMMAR 1\nIF 1 MW < 2 MW" + string.Concat(Enumerable.Repeat(" OR 1 MW < 2 MW", max + 1)) + "\nEND\n");
    }

    [Fact]
    public void An_expression_at_the_depth_limit_still_runs_and_prints_its_value()
    {
        int max = Grammar.MaxExpressionDepth;
        var s = new Session(TestPaths.TempDir());
        string output = OnDefaultStack(() => s.Execute(Parser.ParseLineText("PRINT 1" + string.Concat(Enumerable.Repeat(" + 1", max)))!));
        Assert.EndsWith(" = " + (max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_interactive_console_refuses_a_deep_line_and_carries_on()
    {
        var c = new InteractiveConsole(TestPaths.TempDir());
        ConsoleReply r = OnSmallStack(() => c.Submit("PRINT " + new string('(', 4000) + "1" + new string(')', 4000)));
        Assert.Equal(ConsoleReplyKind.Refused, r.Kind);
        Assert.Equal(ErrorPhase.Syntax, r.Error!.Phase);
        Assert.Equal(ConsoleReplyKind.Ran, c.Submit("PRINT 2 MW").Kind);
    }

    // ---------------------------------------------------------------- limits inside blocks

    private static string Script(params string[] lines) => string.Join("\n", lines) + "\n";

    private static RunResult Run(string script, SessionLimits? limits, string? baseDir = null) =>
        Runner.Run(Encoding.UTF8.GetBytes(script), new RunOptions(baseDir ?? TestPaths.TempDir(), null, null, limits));

    [Fact]
    public void A_cancelled_run_stops_inside_a_block_whose_body_runs_no_simple_statement()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string script = Script("GRAMMAR 1", TestPaths.Load("case14"), "FOR EACH BUS a", "  FOR EACH BUS b", "  END", "END");
        // The GRAMMAR line is the first to meet the cancelled token.
        RunResult r = Run(script, new SessionLimits(Cancel: cts.Token));
        Assert.NotNull(r.Error);
        Assert.Contains("time limit", r.Error!.Message, StringComparison.Ordinal);

        // A token cancelled after the load stops the block before it does anything.
        var late = new CancellationTokenSource();
        var limits = new SessionLimits(Cancel: late.Token);
        var s = new Session(TestPaths.TempDir()) { Limits = limits };
        s.Execute(Parser.ParseLineText(TestPaths.Load("case14"))!);
        late.Cancel();
        ScriptException e = Assert.Throws<ScriptException>(() => s.Execute(Parser.ParseStatementText("FOR EACH BUS a\n  FOR EACH BUS b\n  END\nEND", 3)!));
        Assert.Contains("time limit", e.Message, StringComparison.Ordinal);
        Assert.Empty(s.Loops);   // the session is as it was
        late.Dispose();
    }

    [Fact]
    public void The_statement_budget_stops_nested_empty_loops_and_leaves_the_session_as_it_was()
    {
        var s = new Session(TestPaths.TempDir()) { Limits = new SessionLimits { MaxStatements = 100 } };
        s.Execute(Parser.ParseLineText(TestPaths.Load("case14"))!);
        int before = s.Statements.Count;
        ScriptException e = Assert.Throws<ScriptException>(() =>
            s.Execute(Parser.ParseStatementText("FOR EACH BUS a\n  FOR EACH BUS b\n  END\nEND", 2)!));
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("100 statements, blocks and loop iterations", e.Message, StringComparison.Ordinal);
        Assert.True(e.Pos.Line is 2 or 3, e.Display);
        Assert.Equal(before, s.Statements.Count);
        Assert.Empty(s.Loops);
    }

    [Fact]
    public void The_output_budget_stops_a_printing_loop_as_soon_as_it_is_passed()
    {
        var s = new Session(TestPaths.TempDir());
        s.Execute(Parser.ParseLineText(TestPaths.Load("case14"))!);
        s.Limits = new SessionLimits { MaxOutputChars = 100 };   // counted from here: 14 lines of 12 characters pass it
        ScriptException e = Assert.Throws<ScriptException>(() =>
            s.Execute(Parser.ParseStatementText("FOR EACH BUS b\n  PRINT 1 MW\nEND", 2)!));
        Assert.Contains("100 characters of output", e.Message, StringComparison.Ordinal);
        Assert.Equal(3, e.Pos.Line);
    }

    [Fact]
    public void Generous_limits_leave_a_valid_scripts_run_record_byte_identical()
    {
        string dir = TestPaths.TempDir();
        string script = Script("GRAMMAR 1", TestPaths.Load("case9"), "SOLVE POWERFLOW", "LET total = 0 MW",
            "FOR EACH GENERATOR g", "  SET total = total + P OF GENERATOR g", "  PRINT -(-(P OF GENERATOR g)) + ABS(MIN(1 MW, 2 MW))", "END",
            "IF NOT (total < 1 MW) AND ((1 MW < 2 MW))", "  REPORT BUSES", "END", "PRINT total");
        RunResult plain = Run(script, null, dir);
        Assert.True(plain.Ok, plain.Error?.Display);
        using var cts = new CancellationTokenSource();
        RunResult limited = Run(script, new SessionLimits(1_000_000, 1_000_000, cts.Token) { MaxStatements = 1_000_000, MaxOutputChars = 10_000_000 }, dir);
        Assert.True(limited.Ok, limited.Error?.Display);
        Assert.Equal(plain.RecordBytes, limited.RecordBytes);
    }

    [Fact]
    public void An_open_block_over_the_hosts_cap_is_refused_and_recorded_as_comments()
    {
        var c = new InteractiveConsole(TestPaths.TempDir()) { MaxPendingChars = 64 };
        Assert.Equal(ConsoleReplyKind.Continued, c.Submit("FOR EACH BUS b").Kind);
        ConsoleReply r = c.Submit("  PRINT " + new string('1', 80) + " MW");
        Assert.Equal(ConsoleReplyKind.Refused, r.Kind);
        Assert.Contains("limit of 64 characters", r.Error!.Message, StringComparison.Ordinal);
        Assert.False(c.InBlock);
        Assert.Contains("# FOR EACH BUS b", c.RecordedLines);
        Assert.Equal(ConsoleReplyKind.Ran, c.Submit("PRINT 1 MW").Kind);
    }

    // ---------------------------------------------------------------- paths

    private static string Quote(string p) => "\"" + p.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";

    [Fact]
    public void A_confined_session_refuses_paths_outside_its_base_folder_and_network_paths()
    {
        string outside = TestPaths.TempDir();
        string base1 = TestPaths.TempDir();
        File.Copy(TestPaths.Case("case9"), Path.Combine(base1, "case9.m"));
        var s = new Session(base1) { ConfineToBaseDirectory = true };
        Assert.Contains("loaded \"case9\"", s.Execute(Parser.ParseLineText("LOAD CASE \"case9.m\" FREQUENCY 60 HZ")!), StringComparison.Ordinal);

        foreach (string bad in new[] { TestPaths.Case("case14"), "../x.m", @"\\server\share\x.m", "//server/share/x.m" })
        {
            ScriptException e = Assert.Throws<ScriptException>(() => s.Execute(Parser.ParseLineText("LOAD CASE " + Quote(bad))!));
            Assert.Contains("confined to its base folder", e.Message, StringComparison.Ordinal);
        }
        s.Execute(Parser.ParseLineText("SOLVE POWERFLOW")!);
        string target = Path.Combine(outside, "sub", "r.json");
        ScriptException x = Assert.Throws<ScriptException>(() => s.Execute(Parser.ParseLineText("EXPORT RESULT " + Quote(target))!));
        Assert.Contains("confined", x.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(outside, "sub")));   // nothing was created outside
        Assert.Contains("exported", s.Execute(Parser.ParseLineText("EXPORT RESULT \"out/r.json\"")!), StringComparison.Ordinal);

        // Not confined (the default): the same absolute path loads, as it always has.
        var open = new Session(base1);
        Assert.Contains("loaded", open.Execute(Parser.ParseLineText(TestPaths.Load("case14"))!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_confined_session_refuses_the_dynamics_file_and_schemas_folder_outside_the_base_folder()
    {
        string base1 = TestPaths.TempDir();
        var s = new Session(base1) { ConfineToBaseDirectory = true };
        File.Copy(TestPaths.Case("case9"), Path.Combine(base1, "case9.m"));
        s.Execute(Parser.ParseLineText("LOAD CASE \"case9.m\" FREQUENCY 60 HZ")!);
        s.Execute(Parser.ParseLineText("SOLVE POWERFLOW")!);
        string dyn = Path.Combine(AppContext.BaseDirectory, "dynamics", "wscc9-genrou.dynamics.json");
        ScriptException e = Assert.Throws<ScriptException>(() =>
            s.Execute(Parser.ParseLineText("SELECT ENGINE RMS SUBTRANSIENT WITH DYNAMICS " + Quote(dyn))!));
        Assert.Contains("DYNAMICS", e.Message, StringComparison.Ordinal);
        Assert.Contains("confined", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_hides_the_base_folder_never_names_its_absolute_path()
    {
        string dir = TestPaths.TempDir();
        var shown = new Session(dir);
        ScriptException a = Assert.Throws<ScriptException>(() => shown.Execute(Parser.ParseLineText("LOAD CASE \"nope.m\"")!));
        Assert.Contains(Path.GetFullPath(dir), a.Message, StringComparison.Ordinal);   // unchanged by default

        var hidden = new Session(dir) { HideBaseDirectory = true };
        ScriptException b = Assert.Throws<ScriptException>(() => hidden.Execute(Parser.ParseLineText("LOAD CASE \"nope.m\"")!));
        Assert.DoesNotContain(Path.GetFullPath(dir), b.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the base folder", b.Message, StringComparison.Ordinal);
        Assert.Equal(a.Pos, b.Pos);
    }

    // ---------------------------------------------------------------- licence

    [Fact]
    public void A_licence_gate_refuses_a_user_model_over_the_bus_limit_with_the_gates_own_message()
    {
        // A copy of the GB synthetic case (2227 buses) outside the install is a user model, not a Bundled Network.
        string copy = TestPaths.TempDir();
        foreach (string f in Directory.GetFiles(TestPaths.GbFolder, "*.json")) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var gate = new LicenceGate(Entitlement.FreeDefault);
        var ungated = new Session(TestPaths.TempDir());
        ungated.Execute(Parser.ParseLineText("LOAD CASE " + Quote(copy))!);   // without a gate (the library default) it loads
        GateResult expected = gate.Check(ungated.Plant!.Model.BusCount, bundled: false);
        Assert.False(expected.Allowed);

        var s = new Session(TestPaths.TempDir()) { Licence = gate };
        ScriptException e = Assert.Throws<ScriptException>(() => s.Execute(Parser.ParseLineText("LOAD CASE " + Quote(copy))!));
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Equal(expected.Message, e.Message);
        Assert.Null(s.Case);

        // A model within the limit loads exactly as without a gate.
        var gated = new Session(TestPaths.TempDir()) { Licence = gate };
        var plain = new Session(TestPaths.TempDir());
        Assert.Equal(plain.Execute(Parser.ParseLineText(TestPaths.Load("case9"))!), gated.Execute(Parser.ParseLineText(TestPaths.Load("case9"))!));
    }
}
