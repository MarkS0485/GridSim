namespace GridSim.Core.IO.Cim;

/// <summary>
/// Export pack (2026-10): the two opt-in reads <c>gridsim convert</c> needs to read its own CGMES output back to the same
/// power flow. <see cref="CimImportOptions.StartingVoltages"/> reads each TopologicalNode's <c>SvVoltage</c> (the state a
/// solve starts from); <see cref="CimImportOptions.ApparentPowerRatings"/> reads the permanent apparent-power limit of
/// each line and transformer (the branch rating a loading is measured against). Both are off by default, so a document
/// imports exactly as before unless a caller asks.
/// </summary>
public static partial class CimImporter
{
    /// <summary>The buses with their <c>SvVoltage</c> (v kV, angle degrees) where the document gives one for the bus's
    /// node. Two SvVoltages for one node, or one with no node or no v, are refused (which one applies would be a guess).
    /// An SvVoltage on a node that is not a bus of this view (a ConnectivityNode merged by the topology processor, a
    /// node of another document) is counted in a note.</summary>
    private static List<CimBus> WithStartingVoltages(List<CimObject> objects, List<CimBus> buses, List<string> notes)
    {
        var sv = new Dictionary<string, (double V, double? Angle)>(StringComparer.Ordinal);
        foreach (CimObject o in objects)
        {
            if (o.Class != "SvVoltage") continue;
            string node = o.Resource("SvVoltage.TopologicalNode")
                ?? throw new InvalidDataException($"CIM {o.Describe} names no node (SvVoltage.TopologicalNode).");
            double v = o.Number("SvVoltage.v")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no SvVoltage.v.");
            if (!(v > 0))
                throw new InvalidDataException($"CIM {o.Describe}: SvVoltage.v = {F(v)} kV is not a voltage a solve can start from.");
            if (!sv.TryAdd(node, (v, o.Number("SvVoltage.angle"))))
                throw new InvalidDataException($"CIM {o.Describe}: node {node} has two SvVoltages; which one applies is ambiguous.");
        }
        int used = 0;
        var result = new List<CimBus>(buses.Count);
        foreach (CimBus b in buses)
        {
            if (sv.TryGetValue(b.MRID, out (double V, double? Angle) s))
            {
                result.Add(b with { SvVKv = s.V, SvAngleDeg = s.Angle });
                used++;
            }
            else result.Add(b);
        }
        if (sv.Count > used)
            notes.Add($"{sv.Count - used} SvVoltage(s) name a node that is not a bus of this view; not used as a starting voltage.");
        return result;
    }

    /// <summary>
    /// The permanent apparent-power limit (MVA) of each piece of equipment that has one: every <c>ApparentPowerLimit</c>
    /// whose <c>OperationalLimitType</c> is permanent (<c>isInfiniteDuration</c> true, CGMES 3, or <c>limitType</c> PATL,
    /// CGMES 2.4.15), in an <c>OperationalLimitSet</c> on the equipment or on one of its terminals. With several (one per
    /// terminal), the smallest: the one that binds first. A limit that is not positive is refused. Temporary limits
    /// (TATL) and current limits (amperes) are not read.
    /// </summary>
    private static Dictionary<string, double> ApparentPowerRatings(List<CimObject> objects, List<string> notes)
    {
        var byId = new Dictionary<string, CimObject>(StringComparer.Ordinal);
        foreach (CimObject o in objects) byId.TryAdd(o.Mrid, o);
        string? EquipmentOfSet(CimObject set)
        {
            if (set.Resource("OperationalLimitSet.Equipment") is string eq) return eq;
            if (set.Resource("OperationalLimitSet.Terminal") is string t && byId.TryGetValue(t, out CimObject? term))
                return term.Resource("Terminal.ConductingEquipment");
            return null;
        }
        bool Permanent(CimObject limit)
        {
            if (limit.Resource("OperationalLimit.OperationalLimitType") is not string typeId
                || !byId.TryGetValue(typeId, out CimObject? type)) return false;
            if (type.Boolean("OperationalLimitType.isInfiniteDuration") == true) return true;
            return string.Equals(EnumLeaf(type.RawEnum("OperationalLimitType.limitType")), "patl", StringComparison.OrdinalIgnoreCase);
        }
        var rating = new Dictionary<string, double>(StringComparer.Ordinal);
        int skipped = 0;
        foreach (CimObject o in objects)
        {
            if (o.Class != "ApparentPowerLimit") continue;
            if (!Permanent(o)) { skipped++; continue; }
            string? equipment = o.Resource("OperationalLimit.OperationalLimitSet") is string setId && byId.TryGetValue(setId, out CimObject? set)
                ? EquipmentOfSet(set) : null;
            if (equipment is null) { skipped++; continue; }
            double value = o.Number("ApparentPowerLimit.value")
                ?? throw new InvalidDataException($"CIM {o.Describe} has no ApparentPowerLimit.value.");
            if (!(value > 0))
                throw new InvalidDataException($"CIM {o.Describe}: ApparentPowerLimit.value = {F(value)} MVA is not a rating.");
            rating[equipment] = rating.TryGetValue(equipment, out double had) ? Math.Min(had, value) : value;
        }
        if (skipped > 0)
            notes.Add($"{skipped} ApparentPowerLimit(s) are not a permanent (PATL) limit on a piece of equipment; not read as a rating.");
        return rating;
    }
}
