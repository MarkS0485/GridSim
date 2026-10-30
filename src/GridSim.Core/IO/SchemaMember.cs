using System.Text.Json;

namespace GridSim.Core.IO;

/// <summary>
/// D-DOC-070 (October 2026, docs-session finding): a versioned GridSim input file must SAY what it is. The readers of
/// <c>gridsim-measurements/1</c> (<see cref="Estimation.MeasurementSet"/>), <c>gridsim-harmonics/1</c>
/// (<see cref="HarmonicSourceSet"/>) and <c>gridsim-rtu-pointmap/1</c> (<see cref="Interpose.RtuPointMap"/>) bind the
/// file onto a record whose <c>Schema</c> property DEFAULTS to the expected id, so a file with no <c>schema</c> member
/// at all (an unrelated JSON file, a hand-written file that forgot it, another tool's output) passed the schema check
/// and loaded as a set: an empty measurement set, harmonic set or point map, or worse, a half-understood one. The check
/// below reads the raw document first and refuses a file without the member, naming the file and the id it must
/// declare. A file WITH the member is checked by the reader as before (the exact id, ordinal). Every file GridSim writes
/// carries the member, and so does every bundled fixture.
/// </summary>
public static class SchemaMember
{
    /// <summary>
    /// Throw <see cref="InvalidDataException"/> when the JSON object in <paramref name="json"/> has no <c>schema</c>
    /// member (property names compared case-insensitively, as the readers' deserialisers do). A document that is not a
    /// JSON object is left to the reader, which reports the wrong shape itself; text that is not JSON throws the
    /// parser's <see cref="JsonException"/>, as the reader would.
    /// </summary>
    /// <param name="json">The file's text.</param>
    /// <param name="path">The file, for the message.</param>
    /// <param name="expectedSchema">The id the file must declare, for the message (e.g. <c>gridsim-measurements/1</c>).</param>
    /// <param name="what">What the file is, in plain words (e.g. "measurement set").</param>
    public static void Require(string json, string path, string expectedSchema, string what)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, "schema", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException(
            $"'{path}' has no \"schema\" member, so it is not known to be a {what}; a {what} file must declare " +
            $"\"schema\": \"{expectedSchema}\".");
    }
}
