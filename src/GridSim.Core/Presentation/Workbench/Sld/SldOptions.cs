namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>
/// Every size and spacing of the diagram, in diagram units (about one screen pixel at 100 % zoom). The defaults are the values
/// the tests' hand-worked layouts use; the layout and the router are pure functions of the model and these numbers. The values are
/// whole numbers on purpose: every coordinate the layout makes is then a sum of whole numbers and halves, exact in binary.
/// </summary>
public sealed record SldOptions
{
    /// <summary>Thickness of a busbar.</summary>
    public double BusbarThickness { get; init; } = 4;
    /// <summary>Distance between the connection slots of a busbar.</summary>
    public double SlotPitch { get; init; } = 28;
    /// <summary>Space at each end of a busbar before the first slot.</summary>
    public double BusbarMargin { get; init; } = 14;
    /// <summary>Gap between neighbouring busbars of one row.</summary>
    public double BusGap { get; init; } = 40;
    /// <summary>Length of the stub from a busbar to the top of an edge symbol.</summary>
    public double StubLead { get; init; } = 8;
    /// <summary>Length of the stub from a busbar to the top of a hanging symbol (generator, load, shunt, earth switch).</summary>
    public double InjectionStem { get; init; } = 12;
    /// <summary>Distance between the parallel horizontal runs of the connections in one channel.</summary>
    public double TrackPitch { get; init; } = 6;
    /// <summary>Free space left above a row of busbars after the last track of the channel above it (a multiple of <see cref="TrackPitch"/>).</summary>
    public double RowClearance { get; init; } = 24;
    /// <summary>Distance from the outermost busbar to the first vertical lane used by connections that pass several rows.</summary>
    public double LaneStart { get; init; } = 30;
    /// <summary>Distance between the vertical lanes.</summary>
    public double LanePitch { get; init; } = 10;
    /// <summary>Vertical gap between two unconnected parts of the case.</summary>
    public double ComponentGap { get; init; } = 60;
    /// <summary>Two busbars whose centres differ by less than this in y count as one row (the connection between them is routed round the bottom).</summary>
    public double SameRowTolerance { get; init; } = 12;
    /// <summary>Case coordinates (x, y) times this are diagram units, for a diagram placed from the case.</summary>
    public double LayoutScale { get; init; } = 2000;
    /// <summary>Space added round the bounds.</summary>
    public double BoundsMargin { get; init; } = 20;

    public static double SymbolWidth(SldKind k) => k switch
    {
        SldKind.Line => 12, SldKind.Transformer => 18, SldKind.Breaker => 12, SldKind.Disconnector => 12, SldKind.LoadBreak => 12,
        SldKind.Generator => 20, SldKind.Load => 16, SldKind.Shunt => 16, SldKind.EarthSwitch => 12, _ => 0,
    };

    public static double SymbolHeight(SldKind k) => k switch
    {
        SldKind.Line => 14, SldKind.Transformer => 26, SldKind.Breaker => 12, SldKind.Disconnector => 12, SldKind.LoadBreak => 12,
        SldKind.Generator => 20, SldKind.Load => 16, SldKind.Shunt => 16, SldKind.EarthSwitch => 12, _ => 0,
    };

    /// <summary>The depth below a busbar that its symbols can reach, plus one track: where the first horizontal run of a channel lies.</summary>
    public double Drop => Math.Max(StubLead + 26, InjectionStem + 20) + TrackPitch;

    /// <summary>The width of a busbar that carries <paramref name="slots"/> connections on its busier side.</summary>
    public double BusbarWidth(int slots) => 2 * BusbarMargin + Math.Max(1, slots) * SlotPitch;
}

/// <summary>Where each busbar's centre is, by node index, and how that was decided ("auto" or "case").</summary>
public sealed record SldPlacement(double[] X, double[] Y, string Layout);

/// <summary>Which side of a busbar a connection arrives on.</summary>
internal enum SldSide { Top, Bottom }

/// <summary>One thing attached to a busbar slot: an end of an edge or a hanging symbol.</summary>
internal sealed record SldAttachment(int Node, int Element, bool IsEdge, bool IsFirstEnd, SldSide Side, double OtherX);

/// <summary>The attachment analysis shared by the auto layout (which needs only the counts) and the geometry builder (which needs the order).</summary>
internal static class SldAttachments
{
    /// <summary>
    /// For every node the attachments on its top and its bottom side, in slot order. An edge's first end A is the end with the smaller
    /// y (the one higher on the page; for two ends in one row the lower node index) and leaves A's bottom; its second end B arrives at
    /// B's top, or at B's bottom when both ends are in one row. Hanging symbols take the bottom slots after the edges. Edge ends are
    /// ordered by the x of the node at the other end, then by element order, so the stubs fan out without crossing.
    /// </summary>
    public static (List<SldAttachment>[] Top, List<SldAttachment>[] Bottom) Analyse(SldModel m, double[] x, double[] y, double tolerance)
    {
        int n = m.Nodes.Count;
        var top = new List<SldAttachment>[n];
        var bottom = new List<SldAttachment>[n];
        for (int i = 0; i < n; i++) { top[i] = new List<SldAttachment>(); bottom[i] = new List<SldAttachment>(); }
        var hanging = new List<SldAttachment>[n];
        for (int i = 0; i < n; i++) hanging[i] = new List<SldAttachment>();
        for (int e = 0; e < m.Elements.Count; e++)
        {
            SldElement el = m.Elements[e];
            if (!el.IsEdge) { hanging[el.Node].Add(new SldAttachment(el.Node, e, false, true, SldSide.Bottom, 0)); continue; }
            (int a, int b, bool same) = Ends(el, y, tolerance);
            bottom[a].Add(new SldAttachment(a, e, true, true, SldSide.Bottom, x[b]));
            if (same) bottom[b].Add(new SldAttachment(b, e, true, false, SldSide.Bottom, x[a]));
            else top[b].Add(new SldAttachment(b, e, true, false, SldSide.Top, x[a]));
        }
        Comparison<SldAttachment> cmp = (p, q) =>
        {
            int c = p.OtherX.CompareTo(q.OtherX);
            if (c != 0) return c;
            c = p.Element.CompareTo(q.Element);
            return c != 0 ? c : q.IsFirstEnd.CompareTo(p.IsFirstEnd);
        };
        for (int i = 0; i < n; i++)
        {
            top[i] = top[i].OrderBy(a => a, Comparer<SldAttachment>.Create(cmp)).ToList();
            bottom[i] = bottom[i].OrderBy(a => a, Comparer<SldAttachment>.Create(cmp)).ToList();
            bottom[i].AddRange(hanging[i]);
        }
        return (top, bottom);
    }

    /// <summary>The first end, the second end and whether both lie in one row.</summary>
    public static (int A, int B, bool SameRow) Ends(SldElement el, double[] y, double tolerance)
    {
        int p = el.Node, q = el.OtherNode;
        if (Math.Abs(y[p] - y[q]) < tolerance) return p <= q ? (p, q, true) : (q, p, true);
        return y[p] < y[q] ? (p, q, false) : (q, p, false);
    }
}
