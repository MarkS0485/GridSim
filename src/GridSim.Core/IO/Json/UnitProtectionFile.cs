using System.Text.Json;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Protection;

namespace GridSim.Core.IO.Json;

/// <summary>
/// RC5: a unit protection file (<c>gridsim-unit-protection/1</c>, <c>schemas/unitprotection/unit-protection.schema.json</c>):
/// generating units' own relay settings for a study whose case is not a v2 case (a PSS/E RAW case in
/// <c>dynamics-study</c>). It holds unitProtection templates in the v2 library template shape and one entry per unit (the
/// template it takes and, optionally, the unit's own values). The file is validated against its schema together with the
/// v2 schema set, and its templates and per-unit values are checked by the v2 loader's own unit protection code, so the
/// rules are exactly those of a v2 case. Units are named as the study names them: a RAW-bound study by the PSS/E machine
/// key <c>&lt;bus&gt;:&lt;id&gt;</c> (<see cref="MachineKey"/>). Whether each unit exists is checked when the set is bound to a
/// case (an unknown unit is refused there).
/// </summary>
public static class UnitProtectionFile
{
    /// <summary>The document's <c>schema</c> tag.</summary>
    public const string SchemaTag = "gridsim-unit-protection/1";

    /// <summary>The schema file name.</summary>
    public const string SchemaFileName = "unit-protection.schema.json";

    /// <summary>The schema folder beside <c>schemas/v2</c>.</summary>
    public const string SchemaFolder = "unitprotection";

    /// <summary>The PSS/E machine key a RAW-bound study names a unit by: the bus number and the machine ID, <c>101:1</c>.
    /// <paramref name="id"/> is the ID as the RAW reader stores it (quotes removed, trimmed, case kept).</summary>
    public static string MachineKey(int bus, string id) =>
        bus.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (id ?? throw new ArgumentNullException(nameof(id)));

    /// <summary>The schema path for the v2 schema directory <paramref name="v2SchemasDir"/> (its sibling folder).</summary>
    public static string SchemaPathFor(string v2SchemasDir)
    {
        ArgumentNullException.ThrowIfNull(v2SchemasDir);
        string v2 = Path.GetFullPath(v2SchemasDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(v2) ?? throw new InvalidDataException($"The v2 schema directory {v2SchemasDir} has no parent folder.");
        return Path.Combine(parent, SchemaFolder, SchemaFileName);
    }

    /// <summary>Read and check a unit protection file; throws <see cref="InvalidDataException"/> listing every problem.</summary>
    public static UnitProtectionSet Load(string path, string v2SchemasDir)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(GridSim.Core.IO.InputFileLimits.ReadAllBytes(path), Path.GetFileName(path), v2SchemasDir);
    }

    /// <summary>Check a unit protection document's bytes; <paramref name="shown"/> names it in messages.</summary>
    public static UnitProtectionSet Parse(byte[] bytes, string shown, string v2SchemasDir)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(v2SchemasDir);
        if (!Directory.Exists(v2SchemasDir)) throw new DirectoryNotFoundException($"Schema directory not found: {v2SchemasDir}");
        string schemaPath = SchemaPathFor(v2SchemasDir);
        if (!File.Exists(schemaPath)) throw new InvalidDataException($"The unit protection schema {SchemaFileName} was not found (looked for {schemaPath}).");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string p in Directory.EnumerateFiles(v2SchemasDir, "*.schema.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal))
            sources[Path.GetFileName(p)] = GridSim.Core.IO.InputFileLimits.ReadAllText(p);
        sources[SchemaFileName] = GridSim.Core.IO.InputFileLimits.ReadAllText(schemaPath);
        JsonSchemaSubset schemas = JsonSchemaSubset.FromSources(sources);
        KindSpecRegistry kinds = KindSpecRegistry.LoadDirectory(Path.Combine(v2SchemasDir, "kinds"), schemas);
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) text = reader.ReadToEnd();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, JsonSchemaSubset.ReaderOptions); }
        catch (JsonException ex) { throw new InvalidDataException($"{shown}: {ex.Message}", ex); }
        using (doc)
        {
            SchemaValidator.ThrowIfAny(new SchemaValidator(schemas).Validate(doc.RootElement, SchemaFileName), shown);
            return V2CaseLoader.UnitProtectionDocument(doc.RootElement, shown, kinds);
        }
    }
}
