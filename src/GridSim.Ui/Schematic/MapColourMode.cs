namespace GridSim.Ui.Schematic;

/// <summary>
/// How the network map is coloured: by circuit loading, as a voltage heatmap, by per-bus asset condition (fragility),
/// or by per-branch stress. The last two are driven by the GDA-bridge overlays.
///
/// <para>Moved here from the WPF <c>FastSchematic.ColorModeKind</c> (UI programme Phase 0.2) so the map model, the
/// dashboard's colour legend and every front end name the same four modes. The members and their order are the old
/// ones, so a stored or compared value means what it meant before.</para>
/// </summary>
public enum MapColourMode
{
    /// <summary>Circuit loading against rating, green through amber to red.</summary>
    Loading,

    /// <summary>Bus voltage heatmap, 0.94 to 1.06 p.u.</summary>
    Voltage,

    /// <summary>Per-bus asset condition (fragility) from the GDA bridge, healthy to critical.</summary>
    Condition,

    /// <summary>Per-branch stress from the GDA bridge, cool to hot.</summary>
    Stress,
}
