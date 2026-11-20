using System.Text;
using Xunit;

namespace GridSim.Scripting.Tests;

/// <summary>The additive hosting hooks (RC4 console panels): the gate, the limits, the statement classification and the interactive console as a library.</summary>
public class HostingTests
{
    private static Statement Parse(string line) => Parser.ParseStatementText(line)!;

    [Theory]
    [InlineData("PRINT 1 MW", false)]
    [InlineData("LET x = 2 MW", false)]
    [InlineData("REPORT BUSES", false)]
    [InlineData("SOLVE POWERFLOW", true)]
    [InlineData("TRIP LINE \"4-5\"", true)]
    [InlineData("SELECT ENGINE LIVE", true)]
    [InlineData("STEP SIM 100 MS", true)]
    [InlineData("AT 100 MS TRIP GENERATOR \"G2\"", true)]
    [InlineData("EXPORT RESULT \"x.json\"", true)]
    public void Simple_statements_are_classified(string text, bool changes) =>
        Assert.Equal(changes, StatementEffects.ChangesState(Parse(text)));

    [Fact]
    public void A_block_changes_state_when_its_body_does_and_a_call_when_its_procedure_does()
    {
        Assert.False(StatementEffects.ChangesState(Parse("IF 1 MW < 2 MW\n  PRINT 1 MW\nEND")));
        Assert.True(StatementEffects.ChangesState(Parse("IF 1 MW < 2 MW\n  SOLVE POWERFLOW\nEND")));

        var s = new Session(TestPaths.TempDir());
        s.Execute(Parse("PROC quiet()\n  PRINT 1 MW\nEND"));
        s.Execute(Parse("PROC loud()\n  SOLVE POWERFLOW\nEND"));
        Assert.False(StatementEffects.ChangesState(Parse("CALL quiet(1 MW)"), s));
        Assert.True(StatementEffects.ChangesState(Parse("CALL loud(1 MW)"), s));
    }

    [Fact]
    public void The_gate_refuses_a_statement_at_its_line_and_leaves_the_session_as_it_was()
    {
        var s = new Session(TestPaths.TempDir()) { Gate = (st, sess) => StatementEffects.ChangesState(st, sess) ? "needs the Operator role" : null };
        Assert.Equal("", s.Execute(Parse("LET a = 1 MW")));
        ScriptException e = Assert.Throws<ScriptException>(() => s.Execute(Parser.ParseStatementText("SOLVE POWERFLOW", 7)!));
        Assert.Equal(ErrorPhase.Validation, e.Phase);
        Assert.Equal(7, e.Pos.Line);
        Assert.Contains("Operator", e.Message, StringComparison.Ordinal);
        Assert.Single(s.Statements);
    }

    [Fact]
    public void The_step_limits_refuse_a_long_step_and_the_run_record_is_unchanged_without_limits()
    {
        string load = TestPaths.Load("case9");
        var s = new Session(TestPaths.TempDir()) { Limits = new SessionLimits(MaxTicksPerStep: 10, MaxTotalTicks: 15) };
        TestPaths.Exec(s, load, "SOLVE POWERFLOW", "SELECT ENGINE LIVE STEP 20 MS");
        ScriptException e = Assert.Throws<ScriptException>(() => TestPaths.Exec(s, "STEP SIM 1 S"));
        Assert.Contains("limit of 10 ticks", e.Message, StringComparison.Ordinal);
        TestPaths.Exec(s, "STEP SIM 200 MS");                                        // 10 ticks
        e = Assert.Throws<ScriptException>(() => TestPaths.Exec(s, "STEP SIM 200 MS"));   // 20 in all
        Assert.Contains("15 stepped ticks", e.Message, StringComparison.Ordinal);

        string text = string.Join("\n", "GRAMMAR 1", load, "SOLVE POWERFLOW", "SELECT ENGINE LIVE STEP 20 MS", "STEP SIM 200 MS", "REPORT SIM", "");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string dir = TestPaths.TempDir();
        RunResult plain = Runner.Run(bytes, new RunOptions(dir));
        RunResult limited = Runner.Run(bytes, new RunOptions(dir, null, null, new SessionLimits(100, 100)));
        Assert.True(plain.Ok, plain.Error?.Display);
        Assert.Equal(plain.RecordJson, limited.RecordJson);
    }

    [Fact]
    public void A_cancelled_token_stops_a_run_with_an_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        RunResult r = Runner.Run(Encoding.UTF8.GetBytes("GRAMMAR 1\nLET a = 1 MW\n"), new RunOptions(TestPaths.TempDir(), null, null, new SessionLimits(Cancel: cts.Token)));
        Assert.False(r.Ok);
        Assert.Contains("time limit", r.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_interactive_console_records_like_gridsim_script_and_the_recording_replays_to_the_same_canonical_script()
    {
        string dir = TestPaths.TempDir();
        var lines = new List<string>();
        var c = new InteractiveConsole(dir, lines.Add);
        Assert.Equal(ConsoleReplyKind.Ran, c.Submit(TestPaths.Load("case9")).Kind);
        Assert.Equal(ConsoleReplyKind.Blank, c.Submit("# a comment").Kind);
        ConsoleReply bad = c.Submit("TRIP LINE \"nope\"");
        Assert.Equal(ConsoleReplyKind.Refused, bad.Kind);
        Assert.Equal(5, bad.Error!.Pos.Line);   // the header comment, GRAMMAR 1, the load and the comment come first
        Assert.Equal(ConsoleReplyKind.Continued, c.Submit("IF 1 MW < 2 MW").Kind);
        Assert.True(c.InBlock);
        Assert.Equal(ConsoleReplyKind.Continued, c.Submit("  PRINT 1 MW").Kind);
        ConsoleReply done = c.Submit("END");
        Assert.Equal(ConsoleReplyKind.Ran, done.Kind);
        Assert.Contains("1 MW", done.Output, StringComparison.Ordinal);
        Assert.Equal(ConsoleReplyKind.Continued, c.Submit("PROC left(MW by)").Kind);   // never closed

        ConsoleRecording rec = c.Finish();
        Assert.StartsWith("# gridsim-script console session, recorded; refused lines are kept as comments\nGRAMMAR 1\n", rec.GssText, StringComparison.Ordinal);
        Assert.Contains("# REFUSED (5:", rec.GssText, StringComparison.Ordinal);
        Assert.Contains("# UNFINISHED BLOCK, not run:", rec.GssText, StringComparison.Ordinal);
        Assert.Equal(rec.GssText, string.Concat(lines.Select(l => l + "\n")));
        Assert.Equal(Runner.Sha256(rec.GssBytes), System.Text.Json.JsonDocument.Parse(rec.RunRecordJson).RootElement.GetProperty("scriptSha256").GetString());

        // Run the recording as a script: the same statements ran, so the canonical script hash is the same.
        RunResult again = Runner.Run(rec.GssBytes, new RunOptions(dir));
        Assert.True(again.Ok, again.Error?.Display);
        Assert.Equal(c.Session.CanonicalScriptSha256, again.Session.CanonicalScriptSha256);
    }
}
