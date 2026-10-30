using System.Globalization;
using System.Text.Json;

namespace GridSim.Core.IO.Json.Overlay;

/// <summary>
/// One reference of an overlay record: the role, what it points at, and the address. A bus is addressed by its id
/// (<see cref="Bus"/>); a line, transformer, generator or demand by its name and another overlay record by its id
/// (<see cref="Key"/>).
/// </summary>
public sealed record AssetOverlayRef(string Role, AssetRefTarget Target, long? Bus, string? Key)
{
    /// <summary>The address as a reader would write it, for example <c>bus 12</c> or <c>line 'L1-2'</c>.</summary>
    public string Display => Target == AssetRefTarget.Bus
        ? "bus " + Bus!.Value.ToString(CultureInfo.InvariantCulture)
        : OverlayKindSet.TargetWord(Target) + " '" + Key + "'";
}

/// <summary>
/// One checked overlay record, exactly as its document wrote it. <see cref="Params"/> and <see cref="Prov"/> are
/// detached copies of the JSON (bare values or the {v, unit, prov} object form), read against the kind spec by the
/// consumer that needs them; nothing here interprets them, and nothing in the solver ever sees them.
/// </summary>
public sealed class AssetOverlayRecord
{
    /// <summary>The record id, unique across every overlay file of the case.</summary>
    public required string Id { get; init; }

    /// <summary>The overlay kind (schemas/v2/kinds/overlay/&lt;kind&gt;.kind.json).</summary>
    public required string Kind { get; init; }

    /// <summary>The parent record's id, or null for a top-level record.</summary>
    public string? Parent { get; init; }

    /// <summary>The references, in the order the document wrote them.</summary>
    public required IReadOnlyList<AssetOverlayRef> Refs { get; init; }

    /// <summary>The parameter object as written (a detached clone).</summary>
    public required JsonElement Params { get; init; }

    /// <summary>The record's own provenance object as written, or null when it gives none.</summary>
    public JsonElement? Prov { get; init; }

    /// <summary>The record's note, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The overlay file the record came from, as the case manifest lists it.</summary>
    public required string Document { get; init; }

    /// <summary>The JSON pointer of the record inside <see cref="Document"/> (for example <c>/records/3</c>).</summary>
    public required string Pointer { get; init; }
}

/// <summary>
/// Phase 5a (2026-10-20): the asset overlay of a v2 case, CARRIED BESIDE THE MODEL and NOT USED BY THE SOLVER. The v2
/// loader (V2CaseLoader) builds the model first, then reads the case's <c>assets</c> files into this object and returns
/// it on its load result; nothing here is ever passed to the model builder, so a case loads to the same model and
/// results with or without its overlay (pinned by AssetOverlayPinTests). The records have passed
/// <see cref="V2AssetChecker"/> against the loaded case before they get here.
/// </summary>
public sealed class CaseAssetOverlay
{
    private readonly Dictionary<string, AssetOverlayRecord> _byId;

    private CaseAssetOverlay(IReadOnlyList<string> documents, IReadOnlyList<AssetOverlayRecord> records)
    {
        Documents = documents;
        Records = records;
        _byId = records.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    /// <summary>The overlay files, as the manifest lists them, in that order.</summary>
    public IReadOnlyList<string> Documents { get; }

    /// <summary>Every record, file by file in manifest order and in document order within a file.</summary>
    public IReadOnlyList<AssetOverlayRecord> Records { get; }

    /// <summary>The record with this id, or null.</summary>
    public AssetOverlayRecord? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The records whose parent is <paramref name="parentId"/> (null = the top-level records), in
    /// <see cref="Records"/> order.</summary>
    public IReadOnlyList<AssetOverlayRecord> ChildrenOf(string? parentId)
        => Records.Where(r => string.Equals(r.Parent, parentId, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Read documents that have ALREADY PASSED <see cref="V2AssetChecker.Check"/> (it is the gate; this reader trusts
    /// the shape it guarantees and would throw on anything else). <paramref name="documents"/> are (name, root) pairs,
    /// the name being the path the manifest lists.
    /// </summary>
    internal static CaseAssetOverlay FromCheckedDocuments(IReadOnlyList<(string Name, JsonElement Root)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var records = new List<AssetOverlayRecord>();
        foreach ((string name, JsonElement root) in documents)
        {
            int i = 0;
            foreach (JsonElement r in root.GetProperty("records").EnumerateArray())
            {
                records.Add(new AssetOverlayRecord
                {
                    Id = r.GetProperty("id").GetString()!,
                    Kind = r.GetProperty("kind").GetString()!,
                    Parent = r.TryGetProperty("parent", out JsonElement p) ? p.GetString() : null,
                    Refs = r.GetProperty("refs").EnumerateObject().Select(ReadRef).ToList(),
                    Params = r.GetProperty("params").Clone(),
                    Prov = r.TryGetProperty("prov", out JsonElement pv) ? pv.Clone() : null,
                    Note = r.TryGetProperty("note", out JsonElement n) ? n.GetString() : null,
                    Document = name,
                    Pointer = "/records/" + i.ToString(CultureInfo.InvariantCulture),
                });
                i++;
            }
        }
        return new CaseAssetOverlay(documents.Select(d => d.Name).ToList(), records);
    }

    /// <summary>One schema-valid reference member: an object with exactly one member naming its target type.</summary>
    internal static AssetOverlayRef ReadRef(JsonProperty role)
    {
        JsonProperty only = role.Value.EnumerateObject().Single();
        AssetRefTarget target = OverlayKindSet.ParseTarget(only.Name);
        return target == AssetRefTarget.Bus
            ? new AssetOverlayRef(role.Name, target, AssetOverlayTargets.BusIdOf(only.Value), null)
            : new AssetOverlayRef(role.Name, target, null, only.Value.GetString());
    }
}
