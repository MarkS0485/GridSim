using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// <see cref="SolveImport"/> is the inverse of <see cref="SolveExport"/>: a solve exported to JSON and read back
/// must reconstruct the same network and the same solved result the views render. Locks the export<->import contract.
/// </summary>
public class SolveImportTests
{
    private static string ExportCase9()
    {
        var trace = new SolveTrace();
        var r = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("case9", "cpu", "deadbeef", "2026-07-02T00:00:00Z", 1.0, "test");
        return SolveExport.ToJson(IeeeCases.Case9(), r, trace, meta);
    }

    [Fact]
    public void Round_trips_the_model()
    {
        GridModel original = IeeeCases.Case9();
        SolveSnapshot snap = SolveImport.Read(ExportCase9());

        Assert.Equal("case9", snap.Case);
        Assert.Equal(original.BusCount, snap.Model.BusCount);
        Assert.Equal(original.Generators.Count, snap.Model.Generators.Count);
        Assert.Equal(original.Branches.Count, snap.Model.Branches.Count);

        // Bus types survive the string round-trip.
        Assert.Equal(1, snap.Model.Buses.Count(b => b.Type == BusType.Slack));

        // A representative branch's electrical parameters match.
        Branch ob = original.Branches[0];
        Branch nb = snap.Model.Branches[0];
        Assert.Equal(ob.From, nb.From);
        Assert.Equal(ob.To, nb.To);
        Assert.Equal(ob.X, nb.X, 9);
        Assert.Equal(ob.R, nb.R, 9);
    }

    [Fact]
    public void Round_trips_the_solved_result()
    {
        var trace = new SolveTrace();
        PowerFlowResult direct = new NewtonRaphsonPowerFlow().Solve(IeeeCases.Case9(), new PowerFlowOptions { Trace = trace });
        SolveSnapshot snap = SolveImport.Read(ExportCase9());
        PowerFlowResult r = snap.Result;

        Assert.True(r.Converged);
        Assert.Equal(direct.Iterations, r.Iterations);
        Assert.Equal(direct.BusIds.Count, r.Vm.Length);
        Assert.Equal(direct.TotalLossMw, r.TotalLossMw, 6);
        Assert.Equal(direct.LineFlows.Count, r.LineFlows.Count);

        // Every bus voltage/angle round-trips.
        for (int i = 0; i < direct.Vm.Length; i++)
        {
            Assert.Equal(direct.BusIds[i], r.BusIds[i]);
            Assert.Equal(direct.Vm[i], r.Vm[i], 6);
            Assert.Equal(direct.VaDeg[i], r.VaDeg[i], 6);
        }
    }

    [Fact]
    public void Rejects_a_non_solve_export_document()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => SolveImport.Read("{\"schema\":\"something-else/1\"}"));
    }

    // GR-180: a document that does not say whether it converged is refused, never read as converged.
    private const string NoSolveBlock = """
        { "schema": "gridsim-solve-export/1",
          "model": { "name": "old", "baseMva": 100,
            "buses": [ { "id": 1, "type": "Slack", "vm": 1.0 }, { "id": 2, "type": "Pq", "pd": 10, "qd": 2 } ],
            "generators": [ { "bus": 1, "pg": 10, "vg": 1.0 } ],
            "branches": [ { "from": 1, "to": 2, "r": 0.01, "x": 0.1 } ] },
          "result": { "buses": [], "lineFlows": [] } }
        """;

    [Fact]
    public void GR180_a_document_with_no_solve_block_is_refused()
    {
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => SolveImport.Read(NoSolveBlock));
        Assert.Contains("no solve block", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GR180_a_solve_block_with_no_converged_flag_is_refused()
    {
        string json = NoSolveBlock.Replace("\"result\":", "\"solve\": { \"iterations\": 3, \"maxMismatch\": 1e-10 }, \"result\":",
            System.StringComparison.Ordinal);
        Assert.NotEqual(NoSolveBlock, json);
        var ex = Assert.Throws<System.IO.InvalidDataException>(() => SolveImport.Read(json));
        Assert.Contains("no 'converged' flag", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GR180_a_non_converged_document_without_a_status_reads_MaxIterations()
    {
        string json = NoSolveBlock.Replace("\"result\":",
            "\"solve\": { \"converged\": false, \"iterations\": 30, \"maxMismatch\": 0.5 }, \"result\":",
            System.StringComparison.Ordinal);
        PowerFlowResult r = SolveImport.Read(json).Result;
        Assert.False(r.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
        Assert.Equal(30, r.Iterations);
    }

    [Fact]
    public void Round_trips_bus_and_generator_names()
    {
        // Asset identity must survive replay: a named bus/generator should come back named, not "gen@412".
        var model = new GridModel
        {
            Name = "named",
            BaseMva = 100,
            Buses = new List<Bus>
            {
                new(1, BusType.Slack, BaseKv: 400, Name: "Sizewell 400kV"),
                new(2, BusType.Pq, Pd: 50, BaseKv: 400, Name: "Bramford"),
            },
            Generators = new List<Generator> { new(1, Pg: 100, Name: "Sizewell B") },
            Branches = new List<Branch> { new(1, 2, 0.01, 0.1, Name: "SZW-BRF") },
        };
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("named", "cpu", "deadbeef", "2026-07-03T00:00:00Z", 1.0, "test");
        SolveSnapshot snap = SolveImport.Read(SolveExport.ToJson(model, r, trace, meta));

        Assert.Equal("Sizewell 400kV", snap.Model.Buses[0].Name);
        Assert.Equal("Bramford", snap.Model.Buses[1].Name);
        Assert.Equal("Sizewell B", snap.Model.Generators[0].Name);
    }

    [Fact]
    public void Round_trips_tap_ratio_and_line_flow_values()
    {
        // A non-unity transformer tap and the solved line-flow VALUES (not just their Count) must survive the
        // round-trip: TapRatio is load-bearing for THE MACHINE's tap view, and the flow magnitudes/loadings drive
        // the corridor panels. Also documents the contract that OLTCs (which the export doesn't carry) default empty.
        var model = new GridModel
        {
            Name = "tap",
            BaseMva = 100,
            Buses = new List<Bus>
            {
                new(1, BusType.Slack, BaseKv: 400, Name: "slack"),
                new(2, BusType.Pq, Pd: 80, Qd: 30, BaseKv: 132, Name: "load"),
            },
            Generators = new List<Generator> { new(1, Pg: 80, Name: "g") },
            Branches = new List<Branch> { new(1, 2, 0.01, 0.1, TapRatio: 0.95, RateAMva: 100, Name: "T1", IsTransformer: true) },
        };
        var trace = new SolveTrace();
        PowerFlowResult direct = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        var meta = new SolveExportMeta("tap", "cpu", "deadbeef", "2026-07-05T00:00:00Z", 1.0, "test");
        SolveSnapshot snap = SolveImport.Read(SolveExport.ToJson(model, direct, trace, meta));

        Branch b = snap.Model.Branches[0];
        Assert.Equal(0.95, b.TapRatio, 9);          // must NOT silently reset to nominal 1.0
        Assert.True(b.IsTransformer);

        Assert.Equal(direct.LineFlows.Count, snap.Result.LineFlows.Count);
        Assert.Equal(direct.LineFlows[0].PFromMw, snap.Result.LineFlows[0].PFromMw, 6);
        Assert.Equal(direct.LineFlows[0].QToMvar, snap.Result.LineFlows[0].QToMvar, 6);
        Assert.Equal(direct.LineFlows[0].LoadingPercent, snap.Result.LineFlows[0].LoadingPercent, 6);

        Assert.Empty(snap.Model.Oltcs);             // export carries no OLTCs - documented default-empty contract
    }
}
