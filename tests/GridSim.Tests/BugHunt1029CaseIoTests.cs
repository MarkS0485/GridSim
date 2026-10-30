using System.Text;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29: case-file, boundary and storage findings (FB33 to FB73). See
/// BugHunt1029DynamicsTests.cs for the conventions.</summary>
public sealed partial class BugHunt1029Tests
{
    private static GridModel BHCostedCase9(bool named)
    {
        GridModel m = IeeeCases.Case9();
        return m.CopyWith(generators: m.Generators.Select((g, i) => g with
        {
            Name = named ? $"G{i + 1}" : g.Name,
            Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0.0, 0.0, new[] { 0.11, 5.0 + i, 150.0 }),
        }).ToList());
    }

    // BH33: a model whose loader dropped generator rows is written by unit name (Apply refuses index there), and a
    // non-finite cost is refused by name.
    [Fact]
    public void BH33_cost_file_for_a_model_with_dropped_rows_is_written_by_name_and_applies_back()
    {
        GridModel named = BHCostedCase9(named: true).CopyWith(droppedGeneratorRows: 1);
        string json = GeneratorCostFile.Write(named);
        Assert.Contains("\"name\": \"G2\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"index\"", json, StringComparison.Ordinal);
        GridModel stripped = named.CopyWith(generators: named.Generators.Select(g => g with { Cost = null }).ToList());
        GridModel back = GeneratorCostFile.Apply(stripped, json, "bh33");
        for (int i = 0; i < back.Generators.Count; i++)
            Assert.Equal(named.Generators[i].Cost!.Coefficients, back.Generators[i].Cost!.Coefficients);

        // Unnamed units on such a model cannot be addressed at all: refused, naming the unit.
        GridModel unnamed = BHCostedCase9(named: false).CopyWith(droppedGeneratorRows: 1)
            .CopyWith(generators: BHCostedCase9(false).Generators.Select(g => g with { Name = null }).ToList());
        Assert.Contains("no name", Assert.Throws<InvalidDataException>(() => GeneratorCostFile.Write(unnamed)).Message, StringComparison.Ordinal);

        // A NaN coefficient is refused by name (it used to be a bare ArgumentException from the JSON writer).
        GridModel bad = BHCostedCase9(true).CopyWith(generators: BHCostedCase9(true).Generators.Select((g, i) =>
            i == 0 ? g with { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { double.NaN, 1.0 }) } : g).ToList());
        Assert.Contains("non-finite", Assert.Throws<InvalidDataException>(() => GeneratorCostFile.Write(bad)).Message, StringComparison.Ordinal);
    }

    // BH34: "index": 1.0 is index 1, as every other integer member of the JSON family reads it.
    [Fact]
    public void BH34_cost_file_index_written_as_one_point_zero_is_index_one()
    {
        GridModel m = BHCostedCase9(named: false);
        string json = GeneratorCostFile.Write(m).Replace("\"index\": 1,", "\"index\": 1.0,", StringComparison.Ordinal);
        Assert.Contains("\"index\": 1.0,", json, StringComparison.Ordinal);
        GridModel stripped = m.CopyWith(generators: m.Generators.Select(g => g with { Cost = null }).ToList());
        GridModel back = GeneratorCostFile.Apply(stripped, json, "bh34");
        Assert.Equal(m.Generators[0].Cost!.Coefficients, back.Generators[0].Cost!.Coefficients);
        Assert.Throws<InvalidDataException>(() => GeneratorCostFile.Apply(stripped, json.Replace("\"index\": 1.0,", "\"index\": 1.5,", StringComparison.Ordinal), "bh34"));
    }

    // BH35: the case editor reads 12.0 as 12, like the v2 loader.
    [Fact]
    public void BH35_editor_integer_reader_accepts_a_whole_number_with_a_fraction_part()
    {
        JsonSourceNode Rec(string text) => JsonSourceText.Parse(Encoding.UTF8.GetBytes(text), "bh35.json", StringComparer.Ordinal).Root;
        Assert.Equal(12, RecordRead.Int(Rec("{\"id\": 12.0}"), "id", StringComparer.Ordinal));
        Assert.Equal(12, RecordRead.Int(Rec("{\"id\": 12}"), "id", StringComparer.Ordinal));
        Assert.Null(RecordRead.Int(Rec("{\"id\": 12.5}"), "id", StringComparer.Ordinal));
        Assert.Null(RecordRead.Int(Rec("{\"id\": \"12\"}"), "id", StringComparer.Ordinal));
        Assert.Null(RecordRead.Int(Rec("{\"id\": 3e10}"), "id", StringComparer.Ordinal));
    }

    // BH39: a boundary with no branches survives the bridge model block, as the JSON loader keeps it.
    [Fact]
    public void BH39_bridge_model_keeps_a_boundary_with_no_branches()
    {
        const string json = """
        {
          "type": "topology",
          "model": {
            "name": "bh39", "baseMva": 100, "busCount": 2,
            "buses": [ {"id":1,"type":"slack","baseKv":400}, {"id":2,"type":"pq","baseKv":400,"pd":10} ],
            "generators": [ {"bus":1,"pg":10} ],
            "branches": [ {"from":1,"to":2,"r":0.01,"x":0.1,"name":"CUT-1"} ],
            "boundaries": [ { "name":"Empty", "limitMw": 0, "branches": [] } ]
          }
        }
        """;
        var msg = System.Text.Json.JsonSerializer.Deserialize<BridgeProtocol.TopologyMessage>(json, BridgeProtocol.JsonOptions)!;
        Boundary b = Assert.Single(msg.ToModel().Boundaries);
        Assert.Equal("Empty", b.Name);
        Assert.Empty(b.Branches);
    }

    // BH41: a blank element name is refused by the v2 writer instead of being written into a case the loader refuses.
    [Fact]
    public void BH41_v2_writer_refuses_a_blank_name()
    {
        GridModel m = IeeeCases.Case9();
        GridModel blank = m.CopyWith(generators: m.Generators.Select((g, i) => i == 1 ? g with { Name = "   " } : g).ToList());
        string dir = Path.Combine(Path.GetTempPath(), "bh41-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => V2CaseWriter.Write(blank, Path.Combine(dir, "case.manifest.json")));
            Assert.Contains("generator 1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("only blanks", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // BH43: "unit" counts the bus's units in case order, out-of-service units included.
    [Fact]
    public void BH43_dynamics_unit_index_counts_out_of_service_units_in_case_order()
    {
        GridModel m = IeeeCases.Case9();
        var fleet = m.Generators.ToList();
        int k3 = fleet.FindIndex(g => g.Bus == 3);
        fleet.Insert(k3, fleet[k3] with { InService = false, Name = "spare" });   // an out-of-service unit first at bus 3
        GridModel withSpare = m.CopyWith(generators: fleet);
        Assert.Equal(m.Generators.Count, withSpare.Generators.Count);

        string fixture = File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json"));
        string schemas = Path.Combine(CliProcess.Root(), "schemas", "v2");
        string dir = Path.Combine(Path.GetTempPath(), "bh43-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Unit 1 in case order is the in-service machine (unit 0 is the spare): it binds to that machine, as on case9.
            string one = Path.Combine(dir, "one.dynamics.json");
            File.WriteAllText(one, fixture.Replace("\"bus\": 3, \"unit\": 0", "\"bus\": 3, \"unit\": 1", StringComparison.Ordinal));
            MachineDynamicsSet set = MachineDynamicsLoader.Load(one, schemas, withSpare);
            MachineDynamicsSet plain = MachineDynamicsLoader.Load(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynamics", "wscc9-genrou.dynamics.json"), schemas, m);
            int g3 = withSpare.Generators.ToList().FindIndex(g => g.Bus == 3);
            Assert.Equal(plain.For(g3)!.TemplateRef, set.For(g3)!.TemplateRef);
            Assert.Equal(plain.For(g3)!.Data, set.For(g3)!.Data);

            // Unit 0 is the out-of-service spare: refused by name. It used to bind silently to the in-service machine.
            string zero = Path.Combine(dir, "zero.dynamics.json");
            File.WriteAllText(zero, fixture);
            var ex = Assert.ThrowsAny<Exception>(() => MachineDynamicsLoader.Load(zero, schemas, withSpare));
            Assert.Contains("out of service", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // BH63: the stored SoC history is its rainflow turning points, which give the same cycles bit for bit.
    [Fact]
    public void BH63_turning_point_history_gives_the_same_rainflow_cycles_as_the_full_trace()
    {
        var rng = new Random(63);
        var full = new List<double>();
        var tp = new List<double>();
        double soc = 0.5;
        for (int i = 0; i < 20_000; i++)
        {
            soc = Math.Clamp(soc + ((rng.NextDouble() - 0.5) * 0.01) + (i % 500 < 250 ? 0.0004 : -0.0004), 0.05, 0.95);
            if (i % 97 == 0) soc = Math.Round(soc, 2);   // some exact repeats and plateaus
            full.Add(soc);
            StorageFleet.AppendTurningPoint(tp, soc);
        }
        Assert.True(tp.Count < full.Count / 2, $"turning points {tp.Count} of {full.Count}");
        Assert.Equal(Rainflow.Count(full), Rainflow.Count(tp));
    }

    // BH73 (finding dismissed): a boundary that names a branch name two branches share is ALREADY refused before any flow
    // is read (Boundary.RequireResolvedBranchNames, called by Evaluate); the post-outage path only sees models derived
    // from a checked one. This test pins that existing guard.
    [Fact]
    public void BH73_boundary_on_a_duplicated_branch_name_is_refused()
    {
        var m = new GridModel
        {
            Name = "bh73",
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq, Pd: 50) },
            Generators = new Generator[] { new(Bus: 1, Pg: 50) },
            Branches = new Branch[] { new(1, 2, R: 0.01, X: 0.1, Name: "TIE"), new(1, 2, R: 0.01, X: 0.1, Name: "TIE") },
            Boundaries = new[] { new Boundary("B", 100, new[] { new BoundaryBranch("TIE", 1) }) },
        };
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        var ex = Assert.Throws<InvalidDataException>(() => BoundaryAnalysis.Evaluate(m, r));
        Assert.Contains("do not resolve to exactly one branch", ex.Message, StringComparison.Ordinal);
    }
}
