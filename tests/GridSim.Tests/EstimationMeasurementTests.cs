using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

public class EstimationMeasurementTests
{
    private static MeasurementSet SampleSet(DateTime? takenUtc = null) => new()
    {
        Case = "case9",
        BaseMva = 100.0,
        TakenUtc = takenUtc,
        Note = "test fixture",
        Measurements = new[]
        {
            new Measurement(MeasurementKind.VmPu, 1.04, 0.004, Bus: 1),
            new Measurement(MeasurementKind.PInjMw, -125.0, 1.0, Bus: 5),
            new Measurement(MeasurementKind.QInjMvar, -50.0, 1.0, Bus: 5),
            new Measurement(MeasurementKind.PFlowMw, 71.6, 1.6, From: 1, To: 4),
            new Measurement(MeasurementKind.QFlowMvar, -10.0, 1.6, From: 1, To: 4, End: BranchEnd.To, Source: "pseudo"),
        },
    };

    [Fact]
    public void Round_trips_through_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-mset-{Guid.NewGuid():N}.json");
        try
        {
            MeasurementSet original = SampleSet(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc));
            original.Save(path);
            MeasurementSet loaded = MeasurementSet.Load(path);

            Assert.Equal(MeasurementSet.SchemaId, loaded.Schema);
            Assert.Equal(original.Case, loaded.Case);
            Assert.Equal(original.BaseMva, loaded.BaseMva);
            Assert.Equal(original.TakenUtc, loaded.TakenUtc);
            Assert.Equal(original.Note, loaded.Note);
            Assert.Equal(original.Measurements.Count, loaded.Measurements.Count);
            for (int i = 0; i < original.Measurements.Count; i++)
                Assert.Equal(original.Measurements[i], loaded.Measurements[i]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_a_wrong_schema_tag()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-mset-{Guid.NewGuid():N}.json");
        try
        {
            (SampleSet() with { Schema = "gridsim-measurements/999" }).Save(path);
            var ex = Assert.Throws<InvalidDataException>(() => MeasurementSet.Load(path));
            Assert.Contains("gridsim-measurements/999", ex.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_a_timestamp_newer_than_the_historical_horizon()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-mset-{Guid.NewGuid():N}.json");
        try
        {
            DateTime now = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
            SampleSet(now.AddHours(-1)).Save(path); // 1 h old: inside the horizon => rejected
            Assert.Throws<ForwardInferenceException>(() => MeasurementSet.Load(path, now));

            SampleSet(now.AddHours(-(ForwardInferenceGuard.HorizonHours + 1))).Save(path); // past the horizon => accepted
            Assert.NotNull(MeasurementSet.Load(path, now));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validate_accepts_a_well_formed_set()
    {
        SampleSet().Validate(IeeeCases.Case9());
    }

    [Fact]
    public void Validate_names_every_offender()
    {
        var bad = new MeasurementSet
        {
            Case = "case9",
            BaseMva = 100.0,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, double.NaN, 0.004, Bus: 1),          // non-finite value
                new Measurement(MeasurementKind.PInjMw, -125.0, 0.0, Bus: 5),              // sigma must be > 0
                new Measurement(MeasurementKind.QInjMvar, -50.0, 1.0, Bus: 999),           // no such bus
                new Measurement(MeasurementKind.PFlowMw, 71.6, 1.6, From: 1, To: 9),       // no such branch
            },
        };
        var ex = Assert.Throws<InvalidDataException>(() => bad.Validate(IeeeCases.Case9()));
        Assert.Contains("#0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sigma", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 999", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no branch 1->9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_flags_a_base_mva_mismatch()
    {
        var set = SampleSet() with { BaseMva = 50.0 };
        var ex = Assert.Throws<InvalidDataException>(() => set.Validate(IeeeCases.Case9()));
        Assert.Contains("baseMva", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parallel_circuits_need_a_circuit_name()
    {
        var twin = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 50),
            },
            Generators = new[] { new Generator(1, 50) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1, Name: "CCT-A"),
                new Branch(1, 2, 0.01, 0.1, Name: "CCT-B"),
            },
        };

        var ambiguous = new MeasurementSet
        {
            Case = "twin",
            Measurements = new[] { new Measurement(MeasurementKind.PFlowMw, 25.0, 1.0, From: 1, To: 2) },
        };
        var ex = Assert.Throws<InvalidDataException>(() => ambiguous.Validate(twin));
        Assert.Contains("parallel", ex.Message, StringComparison.Ordinal);

        var named = new MeasurementSet
        {
            Case = "twin",
            Measurements = new[] { new Measurement(MeasurementKind.PFlowMw, 25.0, 1.0, From: 1, To: 2, Circuit: "CCT-B") },
        };
        named.Validate(twin); // resolves cleanly
    }
}
