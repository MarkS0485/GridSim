using System.Text.Json.Serialization;

namespace GridSim.Web.Projection;

/// <summary>
/// N022 (the R132 remainder): the per-frame state the network map is sent over the Blazor circuit, in a compact
/// COLUMN layout aligned to the topology the map already holds.
///
/// <para>Before N022, <c>NetworkMap</c> pushed <c>{ nodes: Frame.Nodes, edges: Frame.Edges }</c> every frame: one
/// JSON object per bus and per branch, each repeating its property names and its own id / index, plus a node field the
/// map never reads (<c>qInjMvar</c>). On gb-full that was 1,212,846 JSON characters per frame. The topology (bus ids,
/// branch indices, names, coordinates) is sent once per session already, so the per-frame message only needs the
/// numbers, in topology order.</para>
///
/// <para>Lossless: every number is the same <c>double</c> as before, written by the same serializer, so the map
/// script reads bit-identical values (pinned by MapStatePayloadTests, which decodes the payload and compares it with
/// the old arrays). No quantisation: a rounded voltage could fall on the other side of a colour-band threshold.</para>
///
/// <para>Column rules (the script, <c>gridsimMap.setStateColumns</c>, applies them):</para>
/// <list type="bullet">
/// <item><description><see cref="Vm"/>, <see cref="VaDeg"/>, <see cref="PInjMw"/> have one slot per topology node, in
/// <c>TopologyDto.Nodes</c> order. A node the frame carries no state for is null in all three (the map then treats it
/// as unsolved, exactly as when its id was absent from the old array).</description></item>
/// <item><description>The four edge columns have one slot per topology edge, in <c>TopologyDto.Edges</c> order. An edge
/// the frame carries no state for is null in all four.</description></item>
/// <item><description>A non-finite value is null (absent). Before, a NaN or infinity in the pushed arrays made Blazor's
/// strict interop serializer throw and stopped the map for the session; now that one value reads as absent.</description></item>
/// </list>
/// </summary>
public sealed record MapStatePayload(
    [property: JsonPropertyName("vm")] double?[] Vm,
    [property: JsonPropertyName("va")] double?[] VaDeg,
    [property: JsonPropertyName("pi")] double?[] PInjMw,
    [property: JsonPropertyName("ep")] double?[] EdgePFromMw,
    [property: JsonPropertyName("eq")] double?[] EdgeQFromMvar,
    [property: JsonPropertyName("el")] double?[] EdgePLossMw,
    [property: JsonPropertyName("ef")] double?[] EdgeLoadingFrac)
{
    /// <summary>Build the column payload for a frame against the topology the map holds.</summary>
    public static MapStatePayload Build(TopologyDto topology, FrameEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(frame);

        int nNodes = topology.Nodes.Count, nEdges = topology.Edges.Count;
        var nodeSlot = new Dictionary<int, int>(nNodes);
        for (int i = 0; i < nNodes; i++) nodeSlot.TryAdd(topology.Nodes[i].Id, i);
        var edgeSlot = new Dictionary<int, int>(nEdges);
        for (int i = 0; i < nEdges; i++) edgeSlot.TryAdd(topology.Edges[i].Index, i);

        var vm = new double?[nNodes];
        var va = new double?[nNodes];
        var pi = new double?[nNodes];
        foreach (NodeStateDto n in frame.Nodes)
        {
            // A state row for a bus the topology does not know has nowhere to go (the old script ignored it too:
            // it only ever looked buses up by the topology's ids).
            if (!nodeSlot.TryGetValue(n.Id, out int s)) continue;
            vm[s] = F(n.Vm); va[s] = F(n.VaDeg); pi[s] = F(n.PInjMw);
        }

        var ep = new double?[nEdges];
        var eq = new double?[nEdges];
        var el = new double?[nEdges];
        var ef = new double?[nEdges];
        foreach (EdgeStateDto e in frame.Edges)
        {
            if (!edgeSlot.TryGetValue(e.Index, out int s)) continue;
            ep[s] = F(e.PFromMw); eq[s] = F(e.QFromMvar); el[s] = F(e.PLossMw); ef[s] = F(e.LoadingFrac);
        }
        return new MapStatePayload(vm, va, pi, ep, eq, el, ef);
    }

    private static double? F(double v) => double.IsFinite(v) ? v : null;

    /// <summary>
    /// True when this payload is bit-for-bit the same as <paramref name="other"/> (every slot the same bits, nulls in
    /// the same places), so pushing it again would change nothing on the map. Bitwise, not ==, so 0.0 and -0.0 (which
    /// serialize differently) are different.
    /// </summary>
    public bool SameBitsAs(MapStatePayload? other)
    {
        if (other is null) return false;
        return Same(Vm, other.Vm) && Same(VaDeg, other.VaDeg) && Same(PInjMw, other.PInjMw)
            && Same(EdgePFromMw, other.EdgePFromMw) && Same(EdgeQFromMvar, other.EdgeQFromMvar)
            && Same(EdgePLossMw, other.EdgePLossMw) && Same(EdgeLoadingFrac, other.EdgeLoadingFrac);
    }

    private static bool Same(double?[] a, double?[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].HasValue != b[i].HasValue) return false;
            // Bit-exact, with any NaN canonicalised first: two NaNs that differ only in sign or payload (which IEEE 754
            // leaves undefined and the JIT can vary between tiers) are the same "not a number" and must not read as a
            // change between two identical frames.
            if (a[i] is double x && BitConverter.DoubleToInt64Bits(GridSim.Core.Numerics.DetMath.CanonicalNaN(x))
                != BitConverter.DoubleToInt64Bits(GridSim.Core.Numerics.DetMath.CanonicalNaN(b[i]!.Value))) return false;
        }
        return true;
    }
}
