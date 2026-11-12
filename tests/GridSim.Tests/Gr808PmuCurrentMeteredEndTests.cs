using System;
using System.IO;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Synchrophasor;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-808 (November 2026): a PMU current channel had no metered end, so every current was taken at the branch's From end.
/// The channel map (<c>gridsim-pmu-channels/1</c>) now takes an optional <c>end</c> on a current channel, "from" (the
/// default when absent, so every existing map reads as before) or "to", and the mapper emits the current rows at that
/// end. Any other value is refused by name.
/// </summary>
public sealed class Gr808PmuCurrentMeteredEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-gr808-{Guid.NewGuid():N}");

    public Gr808PmuCurrentMeteredEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string Write(string endField)
    {
        string path = Path.Combine(_dir, $"map-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "schema": "gridsim-pmu-channels/1",
              "case": "t",
              "baseMva": 100,
              "reference": { "pmu": 101, "phasor": 0 },
              "pmus": [
                {
                  "pmu": 101,
                  "voltages": [ { "phasor": 0, "bus": 1, "baseVolts": 100, "vmSigmaPu": 0.002, "vaSigmaDeg": 0.05 } ],
                  "currents": [ { "phasor": 1, "from": 1, "to": 4, "baseAmps": 10, "iMagSigmaPu": 0.01, "iAngSigmaDeg": 0.1{{endField}} } ]
                }
              ]
            }
            """);
        return path;
    }

    [Fact]
    public void A_map_without_end_meters_at_the_from_end_as_before()
    {
        PmuChannelMapSet set = PmuChannelMapFile.Load(Write(""));
        Assert.Equal(BranchEnd.From, set.Maps.Single().Currents.Single().End);
    }

    [Theory]
    [InlineData("\"to\"", BranchEnd.To)]
    [InlineData("\"TO\"", BranchEnd.To)]
    [InlineData("\"from\"", BranchEnd.From)]
    public void The_end_field_is_read(string value, BranchEnd expected)
    {
        PmuChannelMapSet set = PmuChannelMapFile.Load(Write(", \"end\": " + value));
        Assert.Equal(expected, set.Maps.Single().Currents.Single().End);
    }

    [Fact]
    public void Any_other_end_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PmuChannelMapFile.Load(Write(", \"end\": \"middle\"")));
        Assert.Contains(".end is 'middle'; it must be 'from' or 'to'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mapper_emits_both_current_rows_at_the_metered_end()
    {
        DateTime now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime historical = now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5));
        var frame = new PdcSuperframe(historical,
            new[] { 101 }.ToDictionary(p => p, p => new PmuSample(p, historical, new[] { new Phasor(100.0, 0.0), new Phasor(101.0, 0.0) }, 50.0, 0.0)),
            Array.Empty<int>());
        PmuChannelMapSet set = PmuChannelMapFile.Load(Write(", \"end\": \"to\""));

        MeasurementSet m = PmuMeasurementSetMapper.Map(frame, set.Maps, utcNow: now, angleReference: set.Reference);

        Measurement[] currents = m.Measurements.Where(x => x.Kind is MeasurementKind.IMagPu or MeasurementKind.IAngDeg).ToArray();
        Assert.Equal(2, currents.Length);
        Assert.All(currents, x => Assert.Equal(BranchEnd.To, x.End));
        Assert.Equal(101.0 / 10.0, currents.Single(x => x.Kind == MeasurementKind.IMagPu).Value);
    }
}
