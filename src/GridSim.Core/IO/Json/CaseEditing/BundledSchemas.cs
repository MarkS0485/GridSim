using System.Reflection;
using System.Text;
using GridSim.Core.IO.Json.Schema;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// The v1 and v2 schema sets as embedded in GridSim.Core (resources GridSim.Core.schemas.v1.* and
/// GridSim.Core.schemas.v2.*, from the committed files under schemas/). schemas/ is not copied to every output, so
/// the case editor reads its schemas from the assembly; a test pins the embedded bytes equal to the on-disk files.
/// Each set is loaded once, on first use, and is immutable afterwards.
/// </summary>
internal static class BundledSchemas
{
    /// <summary>Resource-name prefix of the embedded v1 schema files.</summary>
    public const string V1Prefix = "GridSim.Core.schemas.v1.";

    /// <summary>Resource-name prefix of the embedded v2 schema files.</summary>
    public const string V2Prefix = "GridSim.Core.schemas.v2.";

    /// <summary>Resource-name prefix of the embedded v2 kind specs (schemas/v2/kinds; N327: read by the v2 loader the
    /// save gate runs). A different prefix from <see cref="V2Prefix"/>, so the v2 schema set is unchanged.</summary>
    public const string V2KindsPrefix = "GridSim.Core.schemas.v2kinds.";

    /// <summary>Resource-name prefix of the embedded asset overlay kind specs (schemas/v2/kinds/overlay; Phase 5a,
    /// 2026-10-20). A prefix of its own, so neither the v2 schema set nor the template kinds change.</summary>
    public const string V2OverlayKindsPrefix = "GridSim.Core.schemas.v2overlaykinds.";

    private static readonly Lazy<JsonSchemaSubset> V1Set = new(() => JsonSchemaSubset.FromSources(Sources(V1Prefix)));
    private static readonly Lazy<JsonSchemaSubset> V2Set = new(() => JsonSchemaSubset.FromSources(Sources(V2Prefix)));
    private static readonly Lazy<SchemaValidator> V1Validator = new(() => new SchemaValidator(V1Set.Value));
    private static readonly Lazy<SchemaValidator> V2Validator = new(() => new SchemaValidator(V2Set.Value));

    /// <summary>The v1 case-file schema set (schemas/v1).</summary>
    public static JsonSchemaSubset V1() => V1Set.Value;

    /// <summary>The v2 schema set (schemas/v2, top-level schema files only).</summary>
    public static JsonSchemaSubset V2() => V2Set.Value;

    /// <summary>A validator over <see cref="V1"/>.</summary>
    public static SchemaValidator V1Validate => V1Validator.Value;

    /// <summary>A validator over <see cref="V2"/>.</summary>
    public static SchemaValidator V2Validate => V2Validator.Value;

    // Phase 5b (2026-10-20): the asset overlay kind specs, from the same embeds the save gate writes for the v2 loader
    // (kinds/overlay), loaded through OverlayKindSet's own fail-loud loader against the embedded kind.schema.json. The
    // case editor reads them to check an overlay on Check and Save, and to edit overlay records by their kind.
    private static readonly Lazy<Overlay.OverlayKindSet> OverlayKindSpecs = new(() => Overlay.OverlayKindSet.FromSources(Sources(V2OverlayKindsPrefix), V2Set.Value));

    /// <summary>The asset overlay kind specs (schemas/v2/kinds/overlay), as embedded in GridSim.Core.</summary>
    public static Overlay.OverlayKindSet OverlayKinds() => OverlayKindSpecs.Value;

    /// <summary>
    /// Write the embedded v2 schema files to <paramref name="folder"/> and the kind specs to its <c>kinds</c> subfolder:
    /// the layout <see cref="V2CaseLoader"/> reads (N327). The files are byte copies of the embeds. Phase 5a
    /// (2026-10-20): the asset overlay kind specs go to <c>kinds/overlay</c>, where the loader looks for them when a
    /// case lists asset overlay files.
    /// </summary>
    public static void WriteV2SchemaFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string kinds = Path.Combine(folder, "kinds");
        string overlayKinds = Path.Combine(kinds, Overlay.OverlayKindSet.FolderName);
        Directory.CreateDirectory(kinds);
        Directory.CreateDirectory(overlayKinds);
        foreach (string name in Sources(V2Prefix).Keys)
            File.WriteAllBytes(Path.Combine(folder, name), Bytes(V2Prefix + name));
        foreach (string name in Sources(V2KindsPrefix).Keys)
            File.WriteAllBytes(Path.Combine(kinds, name), Bytes(V2KindsPrefix + name));
        foreach (string name in Sources(V2OverlayKindsPrefix).Keys)
            File.WriteAllBytes(Path.Combine(overlayKinds, name), Bytes(V2OverlayKindsPrefix + name));
    }

    /// <summary>
    /// The embedded files under a prefix, keyed by plain file name, read as UTF-8 text. Throws when none exist, so a
    /// build that lost the embeds fails loudly instead of validating against an empty set.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Sources(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        Assembly asm = typeof(BundledSchemas).Assembly;
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            sources[name[prefix.Length..]] = Encoding.UTF8.GetString(Bytes(name)).TrimStart('﻿');   // a BOM is not JSON
        if (sources.Count == 0)
            throw new InvalidOperationException($"GridSim.Core has no embedded schema files under '{prefix}': the build lost the schemas/ embeds (GridSim.Core.csproj).");
        return sources;
    }

    /// <summary>The raw bytes of one embedded resource (throws, naming it, when it is missing).</summary>
    public static byte[] Bytes(string resourceName)
    {
        using Stream s = typeof(BundledSchemas).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"GridSim.Core has no embedded resource '{resourceName}'.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
