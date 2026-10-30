namespace GridSim.Core.Viz;

// Visualisation export schema. Serialized as camelCase JSON so a Unity (or web)
// front-end can read it with field names that match these property names. Arrays
// in VizFrame are indexed in the same order as VizScene.Buses.

public sealed class VizScene
{
    public string Name { get; set; } = "grid";
    public float BaseMva { get; set; }
    public VizBus[] Buses { get; set; } = Array.Empty<VizBus>();
    public VizBranch[] Branches { get; set; } = Array.Empty<VizBranch>();
    public VizFrame[] Frames { get; set; } = Array.Empty<VizFrame>();
}

public sealed class VizBus
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public float Kv { get; set; }
    public string Type { get; set; } = "";   // Slack | Pv | Pq
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class VizBranch
{
    public int From { get; set; }
    public int To { get; set; }
    public string Name { get; set; } = "";
    public bool Transformer { get; set; }
}

public sealed class VizFrame
{
    public float Hour { get; set; }
    public float LoadGw { get; set; }
    public float GenGw { get; set; }
    public float LossMw { get; set; }
    public float FreqHz { get; set; }
    /// <summary>Whether this frame's power flow converged. A non-converged frame's voltages are not a network
    /// state; consumers must not draw them as one (R296: frames used to be written as valid regardless).</summary>
    public bool Converged { get; set; }
    public float[] Vm { get; set; } = Array.Empty<float>();      // p.u., per bus
    public float[] VaDeg { get; set; } = Array.Empty<float>();   // degrees, per bus
    public float[] PInjMw { get; set; } = Array.Empty<float>();  // net MW, per bus
    public float[] QInjMvar { get; set; } = Array.Empty<float>();// net MVAr, per bus
}
