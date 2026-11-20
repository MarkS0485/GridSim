using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 audit IO-02 and IO-25: what the RAW writer and the solve export cannot carry is stated.</summary>
public sealed class PostRc3ExportLossNoteTests
{
    private static GridModel Small(Branch br, Generator g, Bus? extraBus = null) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, Qd: 2, BaseKv: 132) with { X = extraBus?.X ?? 0 },
        },
        Generators = new[] { g },
        Branches = new[] { br },
    };

    [Fact]
    public void Raw_writer_notes_zero_sequence_branch_data_it_cannot_hold()
    {
        var br = new Branch(1, 2, 0.01, 0.1, Name: "T1", IsTransformer: true, R0: 0.02, X0: 0.2,
            FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.Delta);
        PsseWriteResult w = PsseRawWriter.Write(Small(br, new Generator(1, 5, Qmin: -50, Qmax: 50, Pmax: 100)));
        Assert.Contains(w.Notes, n => n.Contains("zero-sequence", StringComparison.Ordinal) && n.StartsWith("1 branch", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_writer_counts_generator_extras_costs_and_layout()
    {
        var g = new Generator(1, 5, Qmin: -50, Qmax: 50, Pmax: 100, NegativeSeqXPu: 0.2) with { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new double[] { 10, 0 }) };
        PsseWriteResult w = PsseRawWriter.Write(Small(new Branch(1, 2, 0.01, 0.1), g, new Bus(2, BusType.Pq, X: 0.5)));
        Assert.Contains(w.Notes, n => n.StartsWith("1 generator(s) carry a name, negative-sequence", StringComparison.Ordinal));
        Assert.Contains(w.Notes, n => n.StartsWith("1 generator(s) carry a cost curve", StringComparison.Ordinal));
        Assert.Contains(w.Notes, n => n.StartsWith("1 bus(es) carry layout", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_writer_adds_no_loss_note_for_a_plain_model()
    {
        PsseWriteResult w = PsseRawWriter.Write(Small(new Branch(1, 2, 0.01, 0.1), new Generator(1, 5, Qmin: -50, Qmax: 50, Pmax: 100)));
        Assert.DoesNotContain(w.Notes, n => n.Contains("zero-sequence", StringComparison.Ordinal) || n.Contains("layout", StringComparison.Ordinal)
            || n.Contains("cost curve", StringComparison.Ordinal));
    }

    private static readonly SolveExportMeta Meta = new("t", "cpu", "deadbeef", "2026-10-10T00:00:00Z", 1.0, "test");

    private static string Export(GridModel m)
    {
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Trace = trace });
        return SolveExport.ToJson(m, r, trace, Meta);
    }

    [Fact]
    public void Solve_export_of_a_model_with_an_svc_says_what_it_does_not_carry()
    {
        GridModel m0 = Small(new Branch(1, 2, 0.01, 0.1), new Generator(1, 5, Qmin: -50, Qmax: 50, Pmax: 100));
        GridModel m = new GridModel
        {
            BaseMva = m0.BaseMva, Buses = m0.Buses, Generators = m0.Generators, Branches = m0.Branches,
            StaticVarCompensators = new[] { new StaticVarCompensator(2, -20, 20, 0, 0.99, 1.01, "SVC 2") },
        };
        using JsonDocument doc = JsonDocument.Parse(Export(m));
        JsonElement nc = doc.RootElement.GetProperty("model").GetProperty("notCarried");
        Assert.Contains(nc.EnumerateArray().Select(e => e.GetString()), s => s!.Contains("static var compensator", StringComparison.Ordinal));
    }

    [Fact]
    public void Solve_export_of_a_plain_model_has_no_not_carried_member()
    {
        string json = Export(Small(new Branch(1, 2, 0.01, 0.1), new Generator(1, 5, Qmin: -50, Qmax: 50, Pmax: 100)));
        Assert.DoesNotContain("notCarried", json, StringComparison.Ordinal);
    }
}
