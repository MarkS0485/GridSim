using GridSim.Core.Model;

namespace GridSim.Core.Presentation;

/// <summary>
/// Frame-to-frame memory of each generator's output, for the MECHANICAL / machine-engineer ramp readouts (web
/// <c>MechanicalService</c> and WPF <c>MachineEngineerVm</c>).
/// <para>
/// Each unit is identified by its INDEX in <see cref="GridModel.Generators"/>, checked against its bus and name.
/// It used to be keyed by <see cref="Generator.Name"/>, which is not unique: gb-full has many units sharing a name
/// ("- chp" x18 above 10 MW, "- other" x25, "Bolney 132kV chp" at 20.26 and 246.05 MW). Within one frame the
/// second namesake was diffed against the first one's output, so a steady operating point showed ramps of tens
/// of MW/min every frame (and the WPF torsional-pulse counter grew for ever). The index is unique and stable
/// while the topology is unchanged; the bus/name check means a different unit at the same index (a model swap
/// that slipped past a <see cref="Reset"/>) starts with no history instead of a phantom ramp.
/// </para>
/// </summary>
public sealed class UnitRampHistory
{
    private readonly Dictionary<int, (int Bus, string? Name, double Pg)> _previous = new();

    /// <summary>Forget everything: call on a timeline discontinuity (replay seek, model switch).</summary>
    public void Reset() => _previous.Clear();

    /// <summary>
    /// Return the output this same unit had when it was last recorded (null when there is no history for it), and
    /// record its current output for the next frame.
    /// </summary>
    /// <param name="index">The unit's index in the frame's <see cref="GridModel.Generators"/>.</param>
    /// <param name="unit">The unit itself (its bus and name confirm the identity; its Pg is recorded).</param>
    public double? Exchange(int index, Generator unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        double? previous = null;
        if (_previous.TryGetValue(index, out (int Bus, string? Name, double Pg) p)
            && p.Bus == unit.Bus
            && string.Equals(p.Name, unit.Name, StringComparison.Ordinal))
            previous = p.Pg;

        _previous[index] = (unit.Bus, unit.Name, unit.Pg);
        return previous;
    }
}
