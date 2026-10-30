using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GridSim.Core.Estimation;
using GridSim.Gda.Interpose;
using Parquet.Serialization;
using Xunit;

namespace GridSim.Tests;

/// <summary>The quarantine live store writes GDA-shaped Parquet (snake_case, unit-named columns,
/// source_class=telemetry) into a hive-partitioned tree with a firewall sidecar, and refuses to open inside
/// the GDA public lake.</summary>
public class QuarantineLiveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gridsim-quar-" + Guid.NewGuid().ToString("N"));

    private static MeasurementSet Sample() => new()
    {
        Case = "case14",
        BaseMva = 100.0,
        TakenUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
        Measurements = new[]
        {
            new Measurement(MeasurementKind.VmPu, 1.03, 0.01, Bus: 4, Source: "rtu"),
            new Measurement(MeasurementKind.PFlowMw, 42.0, 2.0, From: 1, To: 2, Source: "rtu"),
        },
    };

    [Fact]
    public async Task Writes_gda_shaped_partition_with_snake_case_columns_and_sidecar()
    {
        var store = new QuarantineLiveStore(_root, "rtu_live");
        store.WriteWindow(Sample(), "iec104", cadenceMin: 1.0 / 60);

        // Hive partition path: Parquet/rtu_live/year=2026/week=30/part-*.parquet (2026-07-20 is ISO week 30).
        string partDir = Path.Combine(_root, "Parquet", "rtu_live", "year=2026", "week=30");
        string[] parts = Directory.GetFiles(partDir, "part-*.parquet");
        Assert.Single(parts);

        await using Stream fs = File.OpenRead(parts[0]);
        var result = await ParquetSerializer.DeserializeUntypedAsync(fs);
        string[] cols = result.Schema.DataFields.Select(f => f.Name).ToArray();
        foreach (string expected in new[] { "timestamp_utc", "source", "source_class", "vm_pu", "p_mw", "sigma", "provenance", "year", "week" })
            Assert.Contains(expected, cols);

        // Every row is tagged telemetry (never a public source_class), and the value lands in its unit column.
        Assert.All(result.Data, r => Assert.Equal("telemetry", r["source_class"]));
        Assert.Contains(result.Data, r => r.TryGetValue("vm_pu", out object? v) && v is double d && Math.Abs(d - 1.03) < 1e-9);

        // Firewall sidecar present and marked non-evidential.
        string sidecar = Path.Combine(_root, "Parquet", "rtu_live", "_QUARANTINE.json");
        Assert.True(File.Exists(sidecar));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sidecar));
        Assert.False(doc.RootElement.GetProperty("evidential").GetBoolean());
    }

    [Fact]
    public void Refuses_to_open_inside_the_gda_public_lake()
    {
        // A directory carrying the lake's master-contract file (DataSchema.json) is the evidence estate.
        string lake = Path.Combine(_root, "gda-lake");
        Directory.CreateDirectory(lake);
        File.WriteAllText(Path.Combine(lake, "DataSchema.json"), "{}");
        string inside = Path.Combine(lake, "DataSources", "rtu");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new QuarantineLiveStore(inside));
        Assert.Contains("public lake", ex.Message);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
