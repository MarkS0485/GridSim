using GridSim.Core.Model;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-332: GdaModelWriter counted a transformer's shunt susceptance and its zero-sequence data in a note and wrote the
/// case without them, so the write "succeeded" with a case that differed from the model. It now refuses, by name and
/// count, before any file is written. Pinned: a transformer with B, and a transformer with zero-sequence impedance, are
/// each refused and leave no directory behind; a transformer with neither is written with no note, as before.
/// </summary>
public sealed class Gr332GdaWriterRefusesLossTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-gr332-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static GridModel Model(Branch transformer) => new()
    {
        Name = "gr332",
        BaseMva = 100.0,
        FrequencyHz = 50.0,
        Buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132.0, Name: "A"),
            new(2, BusType.Pq, Pd: 40.0, Qd: 10.0, BaseKv: 33.0, Name: "B"),
        },
        Generators = new List<Generator>
        {
            new(1, Pg: 50.0, Vg: 1.02, Qmin: -80.0, Qmax: 80.0, Pmin: 0, Pmax: 100.0, MBase: 100.0, H: 5.0, Name: "G1", Type: "ccgt"),
        },
        Branches = new List<Branch> { transformer },
        Oltcs = new List<Oltc>(),
        Boundaries = new List<Boundary>(),
    };

    [Fact]
    public void Gr332_a_transformer_shunt_susceptance_is_refused_and_nothing_is_written()
    {
        GridModel m = Model(new Branch(1, 2, 0.01, 0.05, 0.02, Name: "T12", RateAMva: 100.0, IsTransformer: true));
        var ex = Assert.Throws<InvalidDataException>(() => GdaModelWriter.WriteDirectory(m, _dir, addGridInfeed: false));
        Assert.Contains("1 transformer(s) carry a shunt susceptance", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Gr332_transformer_zero_sequence_impedance_is_refused_and_nothing_is_written()
    {
        Branch t = new Branch(1, 2, 0.01, 0.05, 0.0, Name: "T12", RateAMva: 100.0, IsTransformer: true) with { R0 = 0.02, X0 = 0.1 };
        var ex = Assert.Throws<InvalidDataException>(() => GdaModelWriter.WriteDirectory(Model(t), _dir, addGridInfeed: false));
        Assert.Contains("1 branch(es) carry transformer winding connections", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Gr332_a_plain_transformer_is_written_with_no_note()
    {
        GridModel m = Model(new Branch(1, 2, 0.01, 0.05, 0.0, Name: "T12", RateAMva: 100.0, IsTransformer: true));
        Assert.Empty(GdaModelWriter.WriteDirectory(m, _dir, addGridInfeed: false));
        Assert.True(File.Exists(Path.Combine(_dir, "transformer.json")));
    }
}
