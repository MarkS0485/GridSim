using System.Globalization;
using System.Text.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Scripting.Tests;

public class ExecutionTests
{
    private static PowerFlowResult Direct(string caseName)
    {
        GridModel m = MatpowerParser.Parse(File.ReadAllText(TestPaths.Case(caseName)), caseName, 60.0);
        return new SteadyStateSolver().Solve(m, new PowerFlowOptions());
    }

    private static string Output(Session s, string line) => s.Execute(Parser.ParseLineText(line)!);

    [Theory]
    [InlineData("case9", 9, 9)]
    [InlineData("case14", 14, 20)]
    public void Load_solve_and_report_match_a_direct_core_solve(string name, int buses, int branches)
    {
        var s = new Session(TestPaths.TempDir());
        string loaded = Output(s, TestPaths.Load(name));
        Assert.Contains($"{buses} buses, {branches} branches", loaded, StringComparison.Ordinal);
        Assert.Contains("60 Hz", loaded, StringComparison.Ordinal);
        Assert.Contains("converged", Output(s, "SOLVE POWERFLOW"), StringComparison.Ordinal);

        PowerFlowResult r = Direct(name);
        string report = Output(s, "REPORT BUSES");
        string[] rows = report.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(buses + 1, rows.Length);
        for (int i = 0; i < buses; i++)
        {
            string expected = string.Create(CultureInfo.InvariantCulture, $"{r.BusIds[i],-6} {r.Vm[i],10:F6} {r.VaDeg[i],10:F4} {r.PInjMw[i],12:F4} {r.QInjMvar[i],12:F4}");
            Assert.Equal(expected, rows[i + 1]);
        }
        Assert.Equal(branches + 1, Output(s, "REPORT BRANCHES").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Case9_solution_balances()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        Output(s, "SOLVE POWERFLOW");
        PowerFlowResult r = s.Solved!.Result;
        Assert.Equal(1.04, r.Vm[0], 9);                                  // the slack set-point
        Assert.Equal(r.TotalLossMw, r.PInjMw.Sum(), 6);                  // net injection is the losses
        Assert.Equal(-315.0, r.PInjMw.Where(p => p < 0).Sum(), 6);       // 90 + 100 + 125 MW of load
    }

    [Fact]
    public void Names_follow_the_naming_rule()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        Assert.Equal(["G1", "G2", "G3"], s.Case!.GeneratorNames);
        Assert.Equal(["L5", "L7", "L9"], s.Case.LoadNames);
        Assert.Equal("1-4", s.Case.LineNames[0]);
        string elements = Output(s, "REPORT ELEMENTS");
        Assert.Contains("\"G2\" bus 2", elements, StringComparison.Ordinal);
        Assert.Contains("\"4-5\"", elements, StringComparison.Ordinal);
    }

    [Fact]
    public void Trip_and_close_change_the_solution_and_close_restores_it_exactly()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        Output(s, "SOLVE POWERFLOW");
        string base0 = Output(s, "REPORT BUSES");

        Output(s, "TRIP LOAD \"L5\"");
        Assert.Contains("TRIPPED", Output(s, "REPORT ELEMENTS"), StringComparison.Ordinal);
        Output(s, "SOLVE POWERFLOW");
        Assert.NotEqual(base0, Output(s, "REPORT BUSES"));

        Output(s, "CLOSE LOAD \"L5\"");
        Output(s, "SOLVE POWERFLOW");
        Assert.Equal(base0, Output(s, "REPORT BUSES"));

        Output(s, "TRIP LINE \"4-5\"");
        Output(s, "SOLVE POWERFLOW");
        Assert.NotEqual(base0, Output(s, "REPORT BUSES"));
        Output(s, "CLOSE LINE \"4-5\"");
        Output(s, "SOLVE POWERFLOW");
        Assert.Equal(base0, Output(s, "REPORT BUSES"));

        Output(s, "TRIP GENERATOR \"G3\"");
        Output(s, "SOLVE POWERFLOW");
        Assert.NotEqual(base0, Output(s, "REPORT BUSES"));
        Output(s, "CLOSE GENERATOR \"G3\"");
        Output(s, "SOLVE POWERFLOW");
        Assert.Equal(base0, Output(s, "REPORT BUSES"));
    }

    [Fact]
    public void Set_changes_load_and_generator_set_points()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        Output(s, "SET LOAD \"L5\" P 120 MW");
        Output(s, "SET LOAD \"L5\" Q 40 MVAR");
        Output(s, "SET GENERATOR \"G2\" P 100 MW");
        Output(s, "SET GENERATOR \"G2\" V 1.03 PU");
        Output(s, "SOLVE POWERFLOW");
        Bus b5 = s.Plant!.Model.Buses.Single(b => b.Id == 5);
        Assert.Equal(120.0, b5.Pd);
        Assert.Equal(40.0, b5.Qd);
        Generator g2 = s.Plant.Model.AllGenerators[1];
        Assert.Equal(100.0, g2.Pg);
        Assert.Equal(1.03, g2.Vg);
    }

    [Fact]
    public void A_refused_statement_leaves_the_session_as_it_was()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        Output(s, "SOLVE POWERFLOW");
        Output(s, "TRIP GENERATOR \"G3\"");
        Plant before = s.Plant!;
        SolvedState? solved = s.Solved;
        int statements = s.Statements.Count;

        // Execution-time refusal: already tripped.
        ScriptException e = Assert.Throws<ScriptException>(() => Output(s, "TRIP GENERATOR \"G3\""));
        Assert.Equal(ErrorPhase.Execution, e.Phase);
        Assert.Contains("already tripped", e.Message, StringComparison.Ordinal);
        Assert.Same(before, s.Plant);
        Assert.Same(solved, s.Solved);
        Assert.Equal(statements, s.Statements.Count);

        // Setting a tripped generator is refused too.
        Assert.Throws<ScriptException>(() => Output(s, "SET GENERATOR \"G3\" P 10 MW"));
        Assert.Same(before, s.Plant);

        // A stale report is refused, not guessed.
        ScriptException stale = Assert.Throws<ScriptException>(() => Output(s, "REPORT BUSES"));
        Assert.Contains("out of date", stale.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_case_file_is_an_error_naming_the_path_position_and_changes_nothing()
    {
        var s = new Session(TestPaths.TempDir());
        Output(s, TestPaths.Load("case9"));
        CaseInfo? ci = s.Case;
        ScriptException e = Assert.Throws<ScriptException>(() => Output(s, "LOAD CASE \"nope/missing.m\""));
        Assert.Equal(new SourcePos(1, 11), e.Pos);
        Assert.Same(ci, s.Case);
    }

    [Fact]
    public void Export_writes_the_solve_export_json_and_never_overwrites_different_content()
    {
        string dir = TestPaths.TempDir();
        var s = new Session(dir);
        Output(s, TestPaths.Load("case14"));
        Assert.Throws<ScriptException>(() => Output(s, "EXPORT RESULT \"r.json\""));   // nothing solved yet
        Output(s, "SOLVE POWERFLOW");
        Output(s, "EXPORT RESULT \"out/r.json\"");
        string path = Path.Combine(dir, "out", "r.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("gridsim-solve-export/1", doc.RootElement.GetProperty("schema").GetString());
        Assert.Equal(14, doc.RootElement.GetProperty("model").GetProperty("busCount").GetInt32());
        Assert.DoesNotContain('\r', File.ReadAllText(path));

        Output(s, "EXPORT RESULT \"out/r.json\"");   // identical bytes: allowed
        Output(s, "TRIP LOAD \"L9\"");
        Output(s, "SOLVE POWERFLOW");
        ScriptException e = Assert.Throws<ScriptException>(() => Output(s, "EXPORT RESULT \"out/r.json\""));
        Assert.Contains("never overwrites", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_stops_at_the_first_error_and_records_it()
    {
        string dir = TestPaths.TempDir();
        string script = string.Join("\n", TestPaths.Load("case9"), "SOLVE POWERFLOW", "TRIP GENERATOR \"Nope\"", "REPORT BUSES", "");
        RunResult r = Runner.Run(System.Text.Encoding.UTF8.GetBytes(script), new RunOptions(dir));
        Assert.False(r.Ok);
        Assert.Equal(new SourcePos(3, 16), r.Error!.Pos);
        Assert.Equal(2, r.Session.Statements.Count);
        using JsonDocument doc = JsonDocument.Parse(r.RecordJson);
        JsonElement res = doc.RootElement.GetProperty("result");
        Assert.Equal("error", res.GetProperty("status").GetString());
        Assert.Equal(3, res.GetProperty("error").GetProperty("line").GetInt32());
        Assert.Equal(16, res.GetProperty("error").GetProperty("column").GetInt32());
    }

    [Fact]
    public void Runner_refuses_a_script_with_a_syntax_error_before_running_anything()
    {
        string script = string.Join("\n", TestPaths.Load("case9"), "SOLVE", "");
        RunResult r = Runner.Run(System.Text.Encoding.UTF8.GetBytes(script), new RunOptions(TestPaths.TempDir()));
        Assert.False(r.Ok);
        Assert.Empty(r.Session.Statements);
        Assert.Null(r.Session.Case);
    }
}
