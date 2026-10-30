using GridSim.Core.Estimation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A measurement entry must state its kind and its value (docs-programme finding): a reading with no value used to
/// load as 0 and one with no kind as VmPu, silently. <see cref="MeasurementSet.Load"/> now refuses both, naming every
/// offending entry.
/// </summary>
public class MeasurementSetRequiredFieldsTests
{
    private static string Write(string measurementsJson)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gridsim-ms-{Guid.NewGuid():N}.json");
        // Moved 2026-10 (D-DOC-061, docs-session finding, Ruling R1: Mark delegated 2026-10-04): the fixture had no
        // takenUtc, and the two "loads" tests relied on an undated set loading, which is the defect D-DOC-061 reports (an
        // undated set skipped the horizon guard). MeasurementSet.Load now refuses an undated set, so the fixture states a
        // historical date; what the tests check (the kind / value fields) is unchanged. Before:
        //   { "schema": "gridsim-measurements/1", "case": "case9", "baseMva": 100, "measurements": [ ... ] }
        // After: the same with "takenUtc": "2024-03-01T12:00:00Z" after "baseMva".
        File.WriteAllText(path, $$"""{ "schema": "gridsim-measurements/1", "case": "case9", "baseMva": 100, "takenUtc": "2024-03-01T12:00:00Z", "measurements": [ {{measurementsJson}} ] }""");
        return path;
    }

    [Fact]
    public void A_complete_set_loads_unchanged()
    {
        string path = Write("""{ "kind": "VmPu", "value": 1.04, "sigma": 0.004, "bus": 1 }""");
        try
        {
            MeasurementSet s = MeasurementSet.Load(path);
            Assert.Single(s.Measurements);
            Assert.Equal(MeasurementKind.VmPu, s.Measurements[0].Kind);
            Assert.Equal(1.04, s.Measurements[0].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_kind_or_value_is_refused_naming_each_entry()
    {
        string path = Write("""
            { "value": 1.04, "sigma": 0.004, "bus": 1 },
            { "kind": "VmPu", "value": 1.0, "sigma": 0.004, "bus": 2 },
            { "kind": "VmPu", "sigma": 0.004, "bus": 9 }
            """);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => MeasurementSet.Load(path));
            Assert.Contains("measurement #0 (bus 1): missing 'kind'", ex.Message);
            Assert.Contains("measurement #2 (bus 9): missing 'value'", ex.Message);
            Assert.DoesNotContain("#1", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Property_names_are_matched_case_insensitively_like_the_deserialiser()
    {
        string path = Write("""{ "Kind": "VmPu", "VALUE": 1.04, "sigma": 0.004, "bus": 1 }""");
        try { Assert.Single(MeasurementSet.Load(path).Measurements); }
        finally { File.Delete(path); }
    }
}
