using System;

// Mirrors the JSON written by GridSim.Core.Viz.VizExport (camelCase). Unity's
// JsonUtility maps these field names directly. Array fields in VizFrame are indexed
// in the same order as VizScene.buses.

[Serializable]
public class VizScene
{
    public string name;
    public float baseMva;
    public VizBus[] buses;
    public VizBranch[] branches;
    public VizFrame[] frames;
}

[Serializable]
public class VizBus
{
    public int id;
    public string name;
    public float kv;
    public string type;   // Slack | Pv | Pq
    public float x;
    public float y;
}

[Serializable]
public class VizBranch
{
    public int from;
    public int to;
    public string name;
    public bool transformer;
}

[Serializable]
public class VizFrame
{
    public float hour;
    public float loadGw;
    public float genGw;
    public float lossMw;
    public float freqHz;
    // Whether this frame's power flow converged (VizFrame.Converged in Core). A non-converged frame's voltages are not
    // a network state, so the viewer greys it out instead of colouring it. JsonUtility leaves this false when the key is
    // missing, so GridViz checks that the export carries the key at all before trusting it (older exports do not).
    public bool converged;
    public float[] vm;
    public float[] vaDeg;
    public float[] pInjMw;
    public float[] qInjMvar;
}
