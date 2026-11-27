using GridSim.Core.Analysis.CableRating.Sizing;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-20d (cable sizing by the IEC 60364-5-52 / BS 7671 design rule): <c>gridsim cable-size &lt;study.json&gt; [--out &lt;result.json&gt;]</c>.
/// The method is built; every table number comes from the table file the study names (<c>tables</c>, relative to the study file), which the
/// owner of the standard fills with provenance. <c>cable-rating</c> and <see cref="GridSim.Core.Analysis.CableRating.Iec60287CableRating"/> are untouched.
/// </summary>
internal static class CableSizeCommand
{
    private const string Verb = "cable-size";

    private sealed record Request(int SchemaVersion, string Label, string Tables, SizingCircuit Circuit);

    public static int Run(List<string> arguments) => SpecialistStudyCli.Run(Verb, arguments, inv =>
    {
        Request q = SpecialistStudyCli.Parse<Request>(inv.Bytes, r => r.SchemaVersion);
        if (string.IsNullOrWhiteSpace(q.Label)) throw new InvalidDataException("label must not be empty.");
        string tablePath = Path.GetFullPath(q.Tables, Path.GetDirectoryName(inv.InputPath) ?? ".");
        byte[] tableBytes = File.ReadAllBytes(tablePath);
        CableTables tables;
        try { tables = CableTables.Parse(System.Text.Encoding.UTF8.GetString(tableBytes).TrimStart('\uFEFF')); }
        catch (FormatException ex) { throw new InvalidDataException(ex.Message); }
        CableSizingResult result = CableSizing.Compute(q.Label, tables, q.Circuit);
        object json = new
        {
            schemaVersion = 1,
            study = Verb,
            provenance = new
            {
                studySha256 = SpecialistStudyCli.Hash(inv.Bytes), tablesSha256 = SpecialistStudyCli.Hash(tableBytes),
                engine = GridSim.Core.BuildInfo.InformationalVersion,
                note = "implemented from public documentation, not verified against the standard (IEC 60364-5-52, BS 7671)",
            },
            units = new { current = "A", voltage = "V", length = "m", size = "mm2", voltDrop = "percent of system voltage" },
            result,
        };
        return (json, result.Status == "EVALUATED" ? CliExit.Ok : CliExit.Verdict);
    });
}
