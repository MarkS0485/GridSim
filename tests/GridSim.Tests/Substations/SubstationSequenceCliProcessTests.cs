using System.Text.Json;
using GridSim.Core.Model.Substations;
using Xunit;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 wiring: <c>substation-state --sequence steps.json</c> runs Core's <see cref="SwitchingScenario"/> from the starting state and
/// reports every step. The oracle is Core itself on the same model and states: each step's bus count, group count, demand accounting
/// and the groups removed and added must be what <see cref="SwitchingScenario.Run"/> records, and opening a switch and reclosing it
/// must return the starting buses (Core's own property of the scenario).
/// </summary>
public sealed class SubstationSequenceCliProcessTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-wire5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private const string Steps = """
        {"schema":"gridsim-switching-sequence/1","steps":[
          {"action":"open","switch":"CPL"},
          {"action":"open","switch":"CBL2","note":"isolate feeder 2"},
          {"action":"close","switch":"CPL"},
          {"action":"close","switch":"CBL2"},
          {"action":"close","switch":"CBL2"}]}
        """;

    private static readonly SwitchingAction[] Actions =
    [
        new("CPL", SwitchAction.Open), new("CBL2", SwitchAction.Open), new("CPL", SwitchAction.Close), new("CBL2", SwitchAction.Close), new("CBL2", SwitchAction.Close),
    ];

    private static string Run(string d, string stepsText, out CliProcess.Result result, params string[] extra)
    {
        string site = Path.Combine(d, "site.json"), steps = Path.Combine(d, "steps.json"), o = Path.Combine(d, "r-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(site, SubstationJson.Write(SubstationFixtures.TwoFeed()));
        File.WriteAllText(steps, stepsText);
        result = CliProcess.RunBuilt(new[] { "substation-state", site, "--unspecified", "normal", "--sequence", steps, "--json", o }.Concat(extra).ToArray());
        return o;
    }

    [Fact]
    public void Every_step_is_what_the_core_scenario_records_and_reclosing_returns_the_starting_buses()
    {
        string d = Dir();
        string o = Run(d, Steps, out CliProcess.Result r);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("sequence: 5 step(s)", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("(already in that state)", r.StdOut, StringComparison.Ordinal);

        SubstationModel m = SubstationFixtures.TwoFeed();
        IReadOnlyList<SwitchingStep> core = SwitchingScenario.Run(m, m.NormalStates(), Actions, new SubstationProjectionOptions(UnspecifiedStatePolicy.UseNormalState));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(o));
        JsonElement table = doc.RootElement.GetProperty("tables").GetProperty("sequence");
        Assert.Equal(core.Count, table.GetArrayLength());
        for (int i = 0; i < core.Count; i++)
        {
            JsonElement row = table[i];
            SwitchingStep s = core[i];
            Assert.Equal(i, row.GetProperty("step").GetInt32());
            Assert.Equal(s.Projection.Buses.Count, row.GetProperty("buses").GetInt32());
            Assert.Equal(s.Projection.Groups.Count, row.GetProperty("node_groups").GetInt32());
            Assert.Equal(s.Projection.DemandMwServed, row.GetProperty("demand_mw_served").GetDouble());
            Assert.Equal(s.Projection.GenerationMwConnected, row.GetProperty("generation_mw_connected").GetDouble());
            Assert.Equal(s.Changed, row.GetProperty("changed").GetBoolean());
            Assert.Equal(string.Join(" | ", s.GroupsRemoved), row.GetProperty("groups_removed").GetString());
            Assert.Equal(string.Join(" | ", s.GroupsAdded), row.GetProperty("groups_added").GetString());
        }
        // Opening the coupler splits the substation; the demand behind the isolated feeder is not served; reclosing returns every bus.
        Assert.True(core[1].Projection.Groups.Count != core[0].Projection.Groups.Count || core[1].GroupsAdded.Count > 0);
        Assert.False(core[5].Changed);
        string Buses(int step) => string.Join(";", doc.RootElement.GetProperty("tables").GetProperty("sequence_buses").EnumerateArray()
            .Where(b => b.GetProperty("step").GetInt32() == step).Select(b => b.GetProperty("bus_id").GetInt32() + ":" + b.GetProperty("nodes").GetString()));
        Assert.Equal(Buses(0), Buses(4));
        Assert.Equal(Buses(0), Buses(5));
    }

    [Fact]
    public void Two_runs_give_identical_bytes()
    {
        string d = Dir();
        string o1 = Run(d, Steps, out CliProcess.Result a);
        byte[] first = File.ReadAllBytes(o1);
        File.Delete(o1);
        string site = Path.Combine(d, "site.json"), steps = Path.Combine(d, "steps.json");
        CliProcess.Result b = CliProcess.RunBuilt("substation-state", site, "--unspecified", "normal", "--sequence", steps, "--json", o1);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
        Assert.Equal(first, File.ReadAllBytes(o1));
    }

    [Fact]
    public void Refusals_name_the_problem_and_use_the_documented_exit_codes()
    {
        string d = Dir();
        Run(d, """{"schema":"gridsim-switching-sequence/1","steps":[{"action":"open","switch":"NOPE"}]}""", out CliProcess.Result unknown);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("NOPE", unknown.StdErr, StringComparison.Ordinal);
        Run(d, """{"schema":"gridsim-switching-sequence/2","steps":[]}""", out CliProcess.Result schema);
        Assert.Equal(1, schema.ExitCode);
        Assert.Contains("gridsim-switching-sequence/1", schema.StdErr, StringComparison.Ordinal);
        Run(d, """{"schema":"gridsim-switching-sequence/1","steps":[{"action":"toggle","switch":"CPL"}]}""", out CliProcess.Result action);
        Assert.Equal(1, action.ExitCode);
        Assert.Contains("open", action.StdErr, StringComparison.Ordinal);
        Run(d, """{"schema":"gridsim-switching-sequence/1","steps":[{"action":"open","switch":"CPL","colour":"red"}]}""", out CliProcess.Result key);
        Assert.Equal(1, key.ExitCode);
        Assert.Contains("colour", key.StdErr, StringComparison.Ordinal);

        Run(d, Steps, out CliProcess.Result both, "--emit", Path.Combine(d, "p.m"));
        Assert.Equal(2, both.ExitCode);
        string site = Path.Combine(d, "site.json");
        CliProcess.Result missing = CliProcess.RunBuilt("substation-state", site, "--unspecified", "normal", "--sequence", Path.Combine(d, "nothere.json"));
        Assert.Equal(1, missing.ExitCode);
    }
}
