using System.Globalization;
using System.Text.Json;
using GridSim.Core.Estimation;

namespace GridSim.Core.IO;

/// <summary>The residual table of one <c>gridsim-estimate-export/1</c> document, as read back for a corpus study.</summary>
/// <param name="Path">The file it was read from.</param>
/// <param name="Converged">The export's own <c>estimate.converged</c> flag. A non-converged estimate is not a state, so
/// its residuals mean nothing; the noise-floor study skips such exports (and says so).</param>
/// <param name="Residuals">One entry per residual row, in file order, carrying the measurement (kind, value, sigma,
/// location, source tag), the predicted value, the residual and the normalised residual (NaN where the export had NaN:
/// a critical measurement, no redundancy, or a lean run).</param>
public sealed record EstimateExportResidualTable(string Path, bool Converged, IReadOnlyList<MeasurementResidual> Residuals);

/// <summary>
/// Reads the residual table back out of a <c>gridsim-estimate-export/1</c> document (written by
/// <see cref="EstimateExport"/>, i.e. by <c>gridsim estimate</c>). This is the input of the
/// <c>estimate-noise-floor</c> verb (R062): <see cref="NoiseFloorAnalysis"/> needs residuals from MANY estimates,
/// grouped by measurement source tag and kind, and the export is the one place that carries the source tag next to
/// each normalised residual (residuals.csv does not).
///
/// <para>Fail loud: a file that is not JSON, carries a different schema, lacks the residual table, or has a row with
/// an unknown measurement kind throws <see cref="InvalidDataException"/> naming the file. Nothing is skipped
/// silently. D-DOC-069 (October 2026, docs-session finding): a row's location is required too. A bus measurement
/// must carry an integer <c>bus</c>, a branch measurement integer <c>from</c> and <c>to</c> and an <c>end</c> of
/// "From" or "To" (the export always writes them). A missing one used to read as bus 0 (or the From end), so a
/// damaged row was grouped and reported as a measurement at a bus that does not exist.</para>
/// </summary>
public static class EstimateExportResiduals
{
    /// <summary>The schema id this reader accepts.</summary>
    public const string SchemaId = "gridsim-estimate-export/1";

    /// <summary>Read one export file.</summary>
    public static EstimateExportResidualTable Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string text = File.ReadAllText(path);   // IOException / FileNotFoundException propagate as input errors
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new InvalidDataException($"'{path}' is not valid JSON: {ex.Message}", ex); }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"'{path}': the document is not a JSON object.");
            string? schema = root.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (schema != SchemaId)
                throw new InvalidDataException($"'{path}': schema is '{schema ?? "(missing)"}', expected '{SchemaId}' (an estimate-export.json from 'gridsim estimate').");

            if (!root.TryGetProperty("estimate", out JsonElement est) || !est.TryGetProperty("converged", out JsonElement conv)
                || conv.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"'{path}': missing estimate.converged.");
            bool converged = conv.GetBoolean();

            if (!root.TryGetProperty("residuals", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"'{path}': missing the residuals array.");

            var list = new List<MeasurementResidual>(rows.GetArrayLength());
            int index = 0;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                string where = $"'{path}' residuals[{index}]";
                if (!row.TryGetProperty("measurement", out JsonElement m) || m.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"{where}: missing the measurement object.");
                string kindText = RequiredString(m, "kind", where);
                // GR-646: a kind is a NAME; Enum.TryParse also accepts a number ("3"), which the export never writes.
                if (kindText.Length == 0 || char.IsAsciiDigit(kindText[0]) || kindText[0] is '-' or '+'
                    || !Enum.TryParse(kindText, ignoreCase: false, out MeasurementKind kind) || !Enum.IsDefined(kind))
                    throw new InvalidDataException($"{where}: unknown measurement kind '{kindText}'.");

                // D-DOC-069: the location the kind needs is required (see the class remarks). The other location fields
                // are left at the record's defaults, as the export (which writes null for them) means.
                bool isBranch = new Measurement(kind, 0, 1).IsBranch;
                int bus = 0, from = 0, to = 0;
                BranchEnd end = BranchEnd.From;
                if (isBranch)
                {
                    from = RequiredInt(m, "from", where, kindText);
                    to = RequiredInt(m, "to", where, kindText);
                    string endText = RequiredString(m, "end", where);
                    end = endText switch
                    {
                        "From" => BranchEnd.From,
                        "To" => BranchEnd.To,
                        _ => throw new InvalidDataException($"{where}: 'end' is '{endText}', expected \"From\" or \"To\" (a {kindText} measurement meters one end of a branch)."),
                    };
                }
                else
                {
                    bus = RequiredInt(m, "bus", where, kindText);
                }

                var measurement = new Measurement(
                    kind,
                    Number(m, "value", where),
                    Number(m, "sigma", where),
                    Bus: bus,
                    From: from,
                    To: to,
                    End: end,
                    Circuit: OptionalString(m, "circuit"),
                    // The export always writes the source tag; an absent one reads as the empty class, exactly as
                    // NoiseFloorAnalysis treats a null source.
                    Source: OptionalString(m, "source") ?? "");

                list.Add(new MeasurementResidual(
                    measurement,
                    Number(row, "predicted", where),
                    Number(row, "residual", where),
                    Number(row, "normalizedResidual", where),
                    row.TryGetProperty("critical", out JsonElement c) && c.ValueKind == JsonValueKind.True));
                index++;
            }
            return new EstimateExportResidualTable(path, converged, list);
        }
    }

    // A number field: a JSON number, or one of the named literals the export writes for NaN / infinities.
    private static double Number(JsonElement obj, string name, string where)
    {
        if (!obj.TryGetProperty(name, out JsonElement v))
            throw new InvalidDataException($"{where}: missing '{name}'.");
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            string? t = v.GetString();
            if (t is "NaN" or "Infinity" or "-Infinity")
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        throw new InvalidDataException($"{where}: '{name}' is not a number.");
    }

    private static string RequiredString(JsonElement obj, string name, string where)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidDataException($"{where}: missing '{name}'.");

    private static string? OptionalString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // D-DOC-069: a location field the measurement kind needs: present, a JSON number, and a whole number in the int range.
    // (Its absence used to read as 0.)
    private static int RequiredInt(JsonElement obj, string name, string where, string kindText)
    {
        if (!obj.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException($"{where}: missing '{name}' (a {kindText} measurement needs it).");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int value))
            throw new InvalidDataException($"{where}: '{name}' is not a whole number.");
        return value;
    }
}
