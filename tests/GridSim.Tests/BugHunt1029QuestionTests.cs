using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29: the question (Q) findings that were decided as code changes. See
/// BugHunt1029DynamicsTests.cs for the conventions.</summary>
public sealed partial class BugHunt1029Tests
{
    // BH08: an island whose units are all scheduled at 0 MW but can produce real power elects a reference; an island
    // with only a condenser (no real-power capability) is still blacked out.
    [Theory]
    [InlineData(50.0, 0, 1)]   // a 0 MW unit with 50 MW of capability: energised, nothing blacked out
    [InlineData(0.0, 1, 0)]    // a condenser (Pmax 0): blacked out, as before
    public void BH08_zero_scheduled_island_with_capability_is_energised(double pmax, int blackedOut, int elected)
    {
        var m = new GridModel
        {
            Name = "bh08",
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq, Pd: 20), new(3, BusType.Pq, Pd: 10), new(4, BusType.Pv) },
            Generators = new Generator[] { new(Bus: 1, Pg: 20), new(Bus: 4, Pg: 0, Pmax: pmax) },
            Branches = new Branch[] { new(1, 2, R: 0.01, X: 0.1), new(3, 4, R: 0.01, X: 0.1) },   // {1,2} and {3,4}
        };
        (GridModel solved, IslandReport report) = IslandPartition.Prepare(m);
        Assert.Equal(2, report.Islands);
        Assert.Equal(blackedOut, report.BlackedOutIslands);
        Assert.Equal(elected, report.ElectedSlackBuses.Count);
        if (elected == 1)
        {
            Assert.Equal(4, report.ElectedSlackBuses[0]);
            Assert.Equal(4, solved.Buses.Count);
        }
    }

    // BH09: the backward-forward sweep refuses a unit on an unknown bus, as the Newton engines do.
    [Fact]
    public void BH09_sweep_refuses_a_generator_on_a_bus_the_model_does_not_have()
    {
        var m = new GridModel
        {
            Name = "bh09",
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq, Pd: 1) },
            Generators = new Generator[] { new(Bus: 1, Pg: 1), new(Bus: 9, Pg: 0.5) },
            Branches = new Branch[] { new(1, 2, R: 0.01, X: 0.02) },
        };
        var ex = Assert.Throws<InvalidDataException>(() => new BackwardForwardSweep().Solve(m));
        Assert.Contains("bus 9", ex.Message, StringComparison.Ordinal);
    }

    // BH36: two v2 network files with one file name in different folders are refused, as asset overlays are.
    [Fact]
    public void BH36_editor_refuses_two_network_files_with_one_file_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bh36-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        Directory.CreateDirectory(Path.Combine(dir, "b"));
        try
        {
            const string net = """{ "schema": "gridsim-network/2", "buses": [] }""";
            File.WriteAllText(Path.Combine(dir, "a", "grid.network.json"), net);
            File.WriteAllText(Path.Combine(dir, "b", "grid.network.json"), net);
            string manifest = Path.Combine(dir, "bh36.case.json");
            File.WriteAllText(manifest, """{ "schema": "gridsim-case/2", "name": "bh36", "network": [ "a/grid.network.json", "b/grid.network.json" ] }""");
            var ex = Assert.Throws<InvalidDataException>(() => CaseSession.OpenV2(manifest, Editor.CaseSessionTests.Unlimited()));
            Assert.Contains("same file name as another file of the case", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // BH37 / BH42: file paths compare by the operating system's rule (case-insensitive on Windows only).
    [Fact]
    public void BH37_file_path_comparison_follows_the_operating_system()
    {
        Assert.Equal(OperatingSystem.IsWindows(), FilePathComparison.Comparer.Equals("C:/x/Net.json", "C:/x/net.json"));
        Assert.Equal(OperatingSystem.IsWindows(), string.Equals("Net.json", "net.json", FilePathComparison.Comparison));
        Assert.True(FilePathComparison.Comparer.Equals("same.json", "same.json"));
    }

    // BH40: the solve-export tag is matched exactly.
    [Fact]
    public void BH40_solve_import_refuses_a_schema_tag_it_does_not_know()
    {
        const string v1 = """
        { "schema": "gridsim-solve-export/1",
          "model": { "buses": [ { "id": 1, "type": "Slack" } ], "generators": [ { "bus": 1, "pg": 10 } ], "branches": [] },
          "solve": { "converged": true, "iterations": 3, "maxMismatch": 1e-10 },
          "result": { "buses": [], "lineFlows": [] } }
        """;
        _ = SolveImport.Read(v1);
        var ex = Assert.Throws<InvalidDataException>(() => SolveImport.Read(v1.Replace("solve-export/1", "solve-export/2", StringComparison.Ordinal)));
        Assert.Contains("expected gridsim-solve-export/1", ex.Message, StringComparison.Ordinal);
    }

    // BH46: an OLTC on the transformer's own winding-1 bus is written with a negative CONT (near side) and reads back;
    // a positive CONT naming that bus (PSS/E: far-side regulation) is refused.
    [Fact]
    public void BH46_own_bus_tap_control_is_written_near_side_and_a_positive_own_bus_cont_is_refused()
    {
        GridModel c = IeeeCases.Case14();
        int k = c.Branches.ToList().FindIndex(b => b.From == 4 && b.To == 7);
        Assert.True(k >= 0);
        var branches = c.Branches.Select((b, i) => i == k ? b with { Name = "T47" } : b).ToList();
        GridModel m = c.CopyWith(branches: branches, oltcs: new[] { new Oltc("T47", 4, TargetVpu: 1.0, MinTap: 0.5, MaxTap: 1.5, StepPu: 0.25, DeadbandPu: 0.25) });
        PsseWriteResult w = PsseRawWriter.Write(m);
        Assert.Contains("1, -4, ", w.Text, StringComparison.Ordinal);
        GridModel back = PsseRawParser.Parse(w.Text, "bh46", new PsseReadOptions { ApplyTapControl = true });
        Oltc o = Assert.Single(back.Oltcs);
        Assert.Equal(4, o.ControlsBus);

        string positive = w.Text.Replace("1, -4, ", "1, 4, ", StringComparison.Ordinal);
        var ex = Assert.ThrowsAny<Exception>(() => PsseRawParser.Parse(positive, "bh46", new PsseReadOptions { ApplyTapControl = true }));
        Assert.Contains("the winding's own bus given as positive", ex.Message, StringComparison.Ordinal);
    }

    // BH49: a MATLAB %{ ... %} block comment is comment, not data; an unclosed block is refused.
    [Fact]
    public void BH49_matpower_block_comments_are_stripped()
    {
        string text = MatpowerWriter.Write(IeeeCases.Case9()).Text;
        string block = "\n%{\nmpc.bus = [\n1 3 0 0 0 0 1 1 0 345 1 1.1 0.9;\n];\n%}\n";
        GridModel plain = MatpowerParser.Parse(text, "bh49");
        GridModel commented = MatpowerParser.Parse(text + block, "bh49");   // a second mpc.bus used to be refused
        Assert.Equal(plain.Buses.Count, commented.Buses.Count);
        Assert.Throws<InvalidDataException>(() => MatpowerParser.Parse(text + "\n%{\nmpc.x = 1;\n", "bh49"));
    }

    // BH81: an empty bus list binds a QSTS load profile to nothing and is refused.
    [Fact]
    public void BH81_qsts_load_binding_with_no_buses_is_refused()
    {
        string path = Path.Combine(Path.GetTempPath(), "bh81-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        { "format": "gridsim-qsts/1", "stepSeconds": 60, "steps": 2,
          "profiles": { "load": { "stepSeconds": 60, "values": [1.0, 0.9] } },
          "loads": [ { "buses": [], "profile": "load" } ] }
        """);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => QstsFile.Load(path, IeeeCases.Case9()));
            Assert.Contains("binds the profile to nothing", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
