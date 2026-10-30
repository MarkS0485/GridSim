namespace GridSim.Core.IO.Json;

/// <summary>Data carrier for one placed protective device in protections.json, a bare JSON array of these objects (no
/// <c>schema</c> member; no schema id is checked). D-DOC-230: this comment used to name a schema id,
/// gridsim-protections/1, that no file carries.</summary>
internal sealed class ProtectionInstanceDto
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";      // an asset-classes.json class of kind "protection"
    public int MonitorBus { get; set; }
    public double CapacityMw { get; set; }        // MW it trips (infeed) or sheds (demand)
    public string? TargetBranch { get; set; }     // for a topology recloser (Phase 3)
}
