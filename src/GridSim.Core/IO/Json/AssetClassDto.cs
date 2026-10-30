namespace GridSim.Core.IO.Json;

// Data carriers for asset-classes.json and asset-bindings.json. Both files are bare JSON arrays (JsonGridLoader.ReadArray):
// they carry no "schema" member and no schema id is checked. D-DOC-230: this line used to name a schema id,
// gridsim-asset-classes/1, that no file carries.
// Deliberately forgiving so device authors can hand-edit the files.

internal sealed class AssetClassDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";      // gridFollowingInverter | gridFormingInverter | transformer | line
    public string Mode { get; set; } = "";      // free-text label (e.g. grid-following / grid-forming)
    public Dictionary<string, double>? Params { get; set; }
    public Dictionary<string, ProvenanceDto>? Provenance { get; set; }
}

internal sealed class ProvenanceDto
{
    public string Tag { get; set; } = "CuratedPlaceholder";  // maps to Diagnostics.ProvenanceTag
    public string Note { get; set; } = "";
}

internal sealed class AssetBindingDto
{
    public string MatchType { get; set; } = "";
    public string Class { get; set; } = "";
}
