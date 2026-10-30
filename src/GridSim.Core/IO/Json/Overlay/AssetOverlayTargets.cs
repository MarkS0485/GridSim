using System.Text.Json;

namespace GridSim.Core.IO.Json.Overlay;

/// <summary>
/// The network elements of one v2 case that overlay references may point at, read from the case's network files
/// (gridsim-network/2) exactly as they spell them: bus ids, and the names of lines, transformers, generators and
/// demands. A name is counted, not just recorded, so a reference to a name two elements share is reported as
/// ambiguous instead of silently taking one of them.
///
/// <para>Read-only bookkeeping for <see cref="V2AssetChecker"/>; it reads the same files the loader maps, and builds
/// nothing the model sees.</para>
/// </summary>
public sealed class AssetOverlayTargets
{
    private readonly HashSet<long> _buses = new();
    private readonly Dictionary<AssetRefTarget, Dictionary<string, int>> _names = new()
    {
        [AssetRefTarget.Line] = new(StringComparer.Ordinal),
        [AssetRefTarget.Transformer] = new(StringComparer.Ordinal),
        [AssetRefTarget.Generator] = new(StringComparer.Ordinal),
        [AssetRefTarget.Demand] = new(StringComparer.Ordinal),
    };

    private AssetOverlayTargets() { }

    /// <summary>
    /// Collect the targets of every network file root given (the case's network is their union). Members that are
    /// not the expected shape are skipped here: the network schema check reports them, and this class never decides
    /// whether a network file is valid.
    /// </summary>
    public static AssetOverlayTargets FromNetworks(IEnumerable<JsonElement> networkRoots)
    {
        ArgumentNullException.ThrowIfNull(networkRoots);
        var t = new AssetOverlayTargets();
        foreach (JsonElement root in networkRoots)
        {
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (root.TryGetProperty("buses", out JsonElement buses) && buses.ValueKind == JsonValueKind.Array)
                foreach (JsonElement b in buses.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("id", out JsonElement id) && BusIdOf(id) is long bus)
                        t._buses.Add(bus);
            t.CountNames(root, "lines", AssetRefTarget.Line);
            t.CountNames(root, "transformers", AssetRefTarget.Transformer);
            t.CountNames(root, "generators", AssetRefTarget.Generator);
            t.CountNames(root, "demands", AssetRefTarget.Demand);
        }
        return t;
    }

    /// <summary>True when the case has a bus with this id.</summary>
    public bool HasBus(long id) => _buses.Contains(id);

    /// <summary>How many elements of <paramref name="target"/> type (line, transformer, generator or demand) carry this
    /// exact name: 0 = no such element, 1 = resolved, more = ambiguous.</summary>
    public int CountOf(AssetRefTarget target, string name)
        => _names.TryGetValue(target, out Dictionary<string, int>? byName)
            ? byName.GetValueOrDefault(name)
            : throw new ArgumentOutOfRangeException(nameof(target), target, "only line, transformer, generator and demand targets are named.");

    /// <summary>
    /// A bus id as a whole number, or null when the JSON value is not a number with no fractional part inside the
    /// range a bus id can take. The schema's integer type admits a written <c>12.0</c>, which is bus 12.
    /// </summary>
    internal static long? BusIdOf(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out long whole)) return whole;
        if (!v.TryGetDouble(out double d) || !double.IsFinite(d) || Math.Floor(d) != d) return null;
        // Only values that convert to a long exactly (|d| below 2^63) are ids; anything larger is not.
        return d >= -9.2233720368547758E18 && d < 9.2233720368547758E18 ? (long)d : null;
    }

    private void CountNames(JsonElement root, string member, AssetRefTarget target)
    {
        if (!root.TryGetProperty(member, out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;
        Dictionary<string, int> byName = _names[target];
        foreach (JsonElement e in list.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
                byName[n.GetString()!] = byName.GetValueOrDefault(n.GetString()!) + 1;
    }
}
