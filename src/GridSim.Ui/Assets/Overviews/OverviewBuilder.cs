using System.Globalization;
using GridSim.Core.Analysis;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;

namespace GridSim.Ui.Assets.Overviews;

/// <summary>
/// The content of every B2 OVERVIEW page (UI programme ASSETS B2, plan S8), made from existing data only: the solved
/// frame's model and result, the model's authored bundles (boundaries, protections, asset classes and bindings), the
/// out-of-service units the model keeps, and the case's demand records. Pure: the same key, index and frame give the same
/// content. Numbers are formatted invariantly (one decimal for MW and Mvar, as the dashboard's power figures; integers as
/// digits); a value that cannot be had this frame (no frame yet, a power flow that did not converge) reads " - ".
/// </summary>
public static class OverviewBuilder
{
    /// <summary>The most rows a class page lists (the A2 fleet pages' cap); the note says how many there were.</summary>
    public const int RowCap = 200;

    /// <summary>What a value reads when it cannot be had this frame (the A2 pages' placeholder).</summary>
    public const string NoValue = " - ";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Mw(double v) => v.ToString("F1", Inv) + " MW";

    private static string Mvar(double v) => v.ToString("F1", Inv) + " Mvar";

    private static string Count(int n) => n.ToString("N0", Inv);

    private static string Capped(int total) => total > RowCap ? $"showing {RowCap.ToString(Inv)} of {Count(total)}" : "";

    private static string BusText(AssetIndex ix, int busId)
    {
        int bi = ix.BusIndexOf(busId);
        string id = busId.ToString(Inv);
        return bi >= 0 && ix.Model.Buses[bi].Name is string n ? id + " '" + n + "'" : id;
    }

    private static OverviewRow BusLink(AssetIndex ix, int busId, string detail)
    {
        int bi = ix.BusIndexOf(busId);
        string name = bi >= 0 ? ix.Model.Buses[bi].Name ?? "bus " + busId.ToString(Inv) : "bus " + busId.ToString(Inv);
        return new OverviewRow(name, detail, "bus " + busId.ToString(Inv), bi >= 0 ? AssetKey.Substation(busId) : null);
    }

    /// <summary>The content of page <paramref name="key"/> over <paramref name="index"/> for <paramref name="frame"/>
    /// (null before any frame). Throws for a page kind that is not a B2 page.</summary>
    public static OverviewContent Build(AssetPageKey key, AssetIndex index, SimFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(index);
        return key.Kind switch
        {
            AssetPageKind.DemandFleet => DemandFleet(index, frame),
            AssetPageKind.Demand => Demand(key, index, frame),
            AssetPageKind.BoundaryFleet => BoundaryFleet(index, frame),
            AssetPageKind.Boundary => Boundary(key, index, frame),
            AssetPageKind.ProtectionFleet => ProtectionFleet(index),
            AssetPageKind.Protection => Protection(key, index, frame),
            AssetPageKind.AssetClassFleet => ClassFleet(index),
            AssetPageKind.AssetClass => Class(key, index),
            AssetPageKind.AssetBinding => Binding(key, index),
            AssetPageKind.OutOfServiceFleet => OutOfServiceFleet(index),
            AssetPageKind.OutOfServiceUnit => OutOfServiceUnit(key, index),
            // UI programme ASSETS slice 5-view: the overlay's pages (carried, not used by the solver). Phase 5g: the frame
            // is passed so an HVDC link's page can show the SOLVED output of the model generators it groups (read-only,
            // labelled solved); no overlay value ever depends on it.
            AssetPageKind.OverlayFleet or AssetPageKind.OverlayKind or AssetPageKind.OverlayRecord => OverlayPages.Build(key, index, frame),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Not a B2 overview page."),
        };
    }

    private static OverviewContent Missing(string eyebrow, string title, string what) => new(eyebrow, title,
        what + " is not in the current model", Array.Empty<OverviewTile>(), Array.Empty<OverviewFact>(), "",
        Array.Empty<OverviewRow>(), "", "", false);

    // ===================================================================================================================
    // DEMANDS
    // ===================================================================================================================

    /// <summary>
    /// A number of a demand record as the loader reads it: the written value, else the loader's default (the field spec's
    /// default literal); null when neither is a number.
    /// </summary>
    internal static double? DemandNumber(AssetIndex ix, DemandRecord d, string field)
    {
        CaseSession s = ix.Session ?? throw new InvalidOperationException("A demand record needs the case session (internal error).");
        CaseDocument doc = s.Documents[d.Record.Document];
        JsonSourceNode node = doc.Text.Resolve(d.Record.Pointer)!;
        if (RecordRead.Member(node, field, doc.KeyComparer) is { } m) return m.Kind == JsonSourceKind.Number ? m.NumberValue : null;
        string? lit = s.CollectionAt(d.Record.Document, d.Record.ArrayPointer)?.Shape.Field(field)?.DefaultLiteral;
        return lit is not null && double.TryParse(lit, NumberStyles.Float, Inv, out double v) ? v : null;
    }

    // True when a demand record writes the member.
    private static bool Writes(AssetIndex ix, DemandRecord d, string field)
    {
        CaseDocument doc = ix.Session!.Documents[d.Record.Document];
        return RecordRead.Member(doc.Text.Resolve(d.Record.Pointer)!, field, doc.KeyComparer) is not null;
    }

    private static OverviewContent DemandFleet(AssetIndex ix, SimFrame? frame)
    {
        IReadOnlyList<DemandRecord> ds = ix.Demands;
        double authored = 0;
        foreach (DemandRecord d in ds) authored += DemandNumber(ix, d, "pMw") ?? 0;
        var buses = new List<int>();
        foreach (DemandRecord d in ds) if (!buses.Contains(d.Bus)) buses.Add(d.Bus);
        string solved = NoValue;
        if (frame is not null)
        {
            double pd = 0;
            foreach (int id in buses)
                foreach (Bus b in frame.Model.Buses)
                    if (b.Id == id) { pd += b.Pd; break; }
            solved = Mw(pd);
        }
        var tiles = new[]
        {
            new OverviewTile("RECORDS", Count(ds.Count), "demand records in the case file"),
            new OverviewTile("AUTHORED P", Mw(authored), "the records' pMw as written"),
            new OverviewTile("BUSES", Count(buses.Count), "carrying demand records"),
            new OverviewTile("SOLVED BUS DEMAND", solved, "those buses' Pd as solved, this frame"),
        };
        var rows = new List<OverviewRow>();
        foreach (DemandRecord d in ds.Take(RowCap))
            rows.Add(new OverviewRow(d.Name ?? "(unnamed)", "bus " + BusText(ix, d.Bus) + "  " + d.Record.Display,
                DemandNumber(ix, d, "pMw") is double p ? Mw(p) : NoValue, AssetIndex.DemandKey(d)));
        return new OverviewContent("ASSET CLASS", AssetTree.DemandsLabel, "the demand records of the case file", tiles,
            Array.Empty<OverviewFact>(), "RECORDS", rows, Capped(ds.Count),
            "The solver keeps no demand element: every demand record on a bus is added into that bus's Pd and Qd. The "
            + "authored figures are the records as written; the solved figure is the buses' demand this frame.", false);
    }

    private static OverviewContent Demand(AssetPageKey key, AssetIndex ix, SimFrame? frame)
    {
        DemandRecord? d = key.Index >= 0 && key.Index < ix.Demands.Count ? ix.Demands[key.Index] : null;
        if (d is null || d.Bus != key.BusA || !string.Equals(d.Name, key.Name, StringComparison.Ordinal))
            return Missing("DEMAND RECORD", key.Name ?? "(unnamed)", "this demand record");
        string q = Writes(ix, d, "qMvar") && DemandNumber(ix, d, "qMvar") is double qv ? Mvar(qv)
            : "from power factor " + (DemandNumber(ix, d, "powerFactor") is double pf ? pf.ToString("R", Inv) : NoValue)
              + (Writes(ix, d, "powerFactor") ? "" : " (the default)");
        string folds = NoValue;
        if (frame is not null)
            foreach (Bus b in frame.Model.Buses)
                if (b.Id == d.Bus) { folds = "Pd " + Mw(b.Pd) + ", Qd " + Mvar(b.Qd) + " as solved"; break; }
        int onBus = ix.Demands.Count(x => x.Bus == d.Bus);
        var facts = new[]
        {
            new OverviewFact("Record", d.Record.Display),
            new OverviewFact("Bus", BusText(ix, d.Bus)),
            new OverviewFact("Authored P", DemandNumber(ix, d, "pMw") is double p ? Mw(p) : NoValue),
            new OverviewFact("Authored Q", q),
            new OverviewFact("Folds into", "bus " + d.Bus.ToString(Inv) + ": " + folds),
            new OverviewFact("Records on this bus", Count(onBus)),
        };
        return new OverviewContent("DEMAND RECORD", d.Name ?? "(unnamed)", d.Record.Display, Array.Empty<OverviewTile>(), facts,
            "FOLDS INTO", new[] { BusLink(ix, d.Bus, "the bus this record is added into") }, "",
            "The solved Pd and Qd are the whole bus's: every demand record on it, as the solver scaled them this frame.", false);
    }

    // ===================================================================================================================
    // BOUNDARIES
    // ===================================================================================================================

    // The boundary flows of a converged frame (the Core boundary analysis, one per model boundary in order), else null.
    private static IReadOnlyList<BoundaryFlow>? Flows(SimFrame? frame) =>
        frame is not null && frame.Result.Converged && frame.Model.Boundaries.Count > 0
            ? BoundaryAnalysis.Evaluate(frame.Model, frame.Result) : null;

    private static OverviewContent BoundaryFleet(AssetIndex ix, SimFrame? frame)
    {
        IReadOnlyList<Boundary> bs = ix.Model.Boundaries;
        IReadOnlyList<BoundaryFlow>? flows = Flows(frame);
        bool aligned = flows is not null && flows.Count == bs.Count;
        var tiles = new[]
        {
            new OverviewTile("BOUNDARIES", Count(bs.Count), "authored transfer boundaries"),
            new OverviewTile("TOTAL LIMIT", Mw(bs.Sum(b => b.LimitMw)), "sum of the authored limits"),
            new OverviewTile("OVER LIMIT", aligned ? Count(flows!.Count(f => f.Overloaded)) : NoValue, "this frame"),
        };
        var rows = new List<OverviewRow>();
        for (int i = 0; i < bs.Count && i < RowCap; i++)
            rows.Add(new OverviewRow(bs[i].Name, Count(bs[i].Branches.Count) + " branches, limit " + Mw(bs[i].LimitMw),
                aligned && flows![i].Name == bs[i].Name ? Mw(flows[i].FlowMw) : NoValue, ix.BoundaryKey(i)));
        return new OverviewContent("ASSET CLASS", AssetTree.BoundariesLabel, "transfer boundaries and their flows", tiles,
            Array.Empty<OverviewFact>(), "BOUNDARIES", rows, Capped(bs.Count),
            "A boundary's flow is the signed sum of its branches' sending-end flows (the boundary analysis); it reads"
            + NoValue + "while the power flow has not converged.", false);
    }

    private static OverviewContent Boundary(AssetPageKey key, AssetIndex ix, SimFrame? frame)
    {
        IReadOnlyList<Boundary> bs = ix.Model.Boundaries;
        if (key.Index < 0 || key.Index >= bs.Count || bs[key.Index].Name != key.Name) return Missing("BOUNDARY", key.Name ?? "", "this boundary");
        Boundary b = bs[key.Index];
        IReadOnlyList<BoundaryFlow>? flows = Flows(frame);
        BoundaryFlow? f = flows is not null && flows.Count == bs.Count && flows[key.Index].Name == b.Name ? flows[key.Index] : null;
        var facts = new[]
        {
            new OverviewFact("Limit", b.LimitMw > 0 ? Mw(b.LimitMw) : "none set (0 MW)"),
            new OverviewFact("Flow", f is null ? NoValue : Mw(f.FlowMw)),
            new OverviewFact("Loading", f is null || b.LimitMw <= 0 ? NoValue : f.LoadingPercent.ToString("F1", Inv) + " %"),
            new OverviewFact("Status", f is null ? NoValue : f.Overloaded ? "OVER LIMIT" : "within limit"),
        };
        var rows = new List<OverviewRow>();
        foreach (BoundaryBranch bb in b.Branches)
        {
            int bi = ix.BranchIndexOf(bb.Branch);
            string flow = NoValue;
            if (frame is not null && bi >= 0 && AssetLookup.FlowAt(frame.Result, bi) is LineFlow lf) flow = Mw(lf.PFromMw);
            rows.Add(new OverviewRow(bb.Branch, "direction " + bb.Direction.ToString("+0;-0", Inv), flow,
                bi >= 0 ? AssetKey.Branch(ix.Model, bi) : null));
        }
        return new OverviewContent("BOUNDARY", b.Name, Count(b.Branches.Count) + " branches", Array.Empty<OverviewTile>(), facts,
            "BRANCHES (sending-end flow)", rows, "", "", false);
    }

    // ===================================================================================================================
    // PROTECTIONS
    // ===================================================================================================================

    private static OverviewContent ProtectionFleet(AssetIndex ix)
    {
        IReadOnlyList<ProtectionInstance> ps = ix.Protections;
        var tiles = new[]
        {
            new OverviewTile("PROTECTIONS", Count(ps.Count), "placed protective devices"),
            new OverviewTile("CAPACITY", Mw(ps.Sum(p => p.CapacityMw)), "the devices' authored capacity"),
        };
        var rows = new List<OverviewRow>();
        for (int i = 0; i < ps.Count && i < RowCap; i++)
            rows.Add(new OverviewRow(ps[i].Name, ps[i].ClassName + " at bus " + BusText(ix, ps[i].MonitorBus), Mw(ps[i].CapacityMw), ix.ProtectionKey(i)));
        return new OverviewContent("ASSET CLASS", AssetTree.ProtectionsLabel, "placed protective devices (protections bundle)", tiles,
            Array.Empty<OverviewFact>(), "DEVICES", rows, Capped(ps.Count), "", false);
    }

    private static OverviewContent Protection(AssetPageKey key, AssetIndex ix, SimFrame? frame)
    {
        IReadOnlyList<ProtectionInstance> ps = ix.Protections;
        if (key.Index < 0 || key.Index >= ps.Count || ps[key.Index].Name != key.Name) return Missing("PROTECTION", key.Name ?? "", "this protection");
        ProtectionInstance p = ps[key.Index];
        string vm = NoValue;
        if (frame is not null)
            for (int k = 0; k < frame.Result.BusIds.Count; k++)
                if (frame.Result.BusIds[k] == p.MonitorBus) { vm = frame.Result.Vm[k].ToString("F3", Inv) + " pu"; break; }
        var facts = new[]
        {
            new OverviewFact("Class", p.ClassName),
            new OverviewFact("Monitored bus", BusText(ix, p.MonitorBus)),
            new OverviewFact("Monitored bus voltage", vm),
            new OverviewFact("Capacity", Mw(p.CapacityMw)),
            new OverviewFact("Target branch", p.TargetBranch ?? "none"),
        };
        var rows = new List<OverviewRow> { BusLink(ix, p.MonitorBus, "monitored bus") };
        if (p.TargetBranch is string tb)
        {
            int bi = ix.BranchIndexOf(tb);
            rows.Add(new OverviewRow(tb, "target branch", "", bi >= 0 ? AssetKey.Branch(ix.Model, bi) : null));
        }
        int ci = ix.ClassIndexOf(p.ClassName);
        rows.Add(new OverviewRow(p.ClassName, "protection class", "", ci >= 0 ? ix.ClassKey(ci) : null));
        return new OverviewContent("PROTECTION", p.Name, p.ClassName, Array.Empty<OverviewTile>(), facts, "CONNECTED", rows, "", "", false);
    }

    // ===================================================================================================================
    // ASSET CLASSES AND BINDINGS
    // ===================================================================================================================

    private const string InverterFleetNote =
        "The live response of the authored classes (the fleet roll-up per class) is on the INVERTER FLEET tab.";

    private static OverviewContent ClassFleet(AssetIndex ix)
    {
        IReadOnlyList<AssetClass> cs = ix.Classes;
        IReadOnlyList<AssetBinding> bs = ix.Bindings;
        var tiles = new[]
        {
            new OverviewTile("CLASSES", Count(cs.Count), "authored asset classes"),
            new OverviewTile("BINDINGS", Count(bs.Count), "type-to-class rules"),
        };
        var rows = new List<OverviewRow>();
        for (int i = 0; i < cs.Count && rows.Count < RowCap; i++)
            rows.Add(new OverviewRow(cs[i].Name, cs[i].Kind + (cs[i].Mode.Length > 0 ? "  " + cs[i].Mode : ""),
                Count(cs[i].Params.Count) + " params", ix.ClassKey(i)));
        for (int i = 0; i < bs.Count && rows.Count < RowCap; i++)
            rows.Add(new OverviewRow(bs[i].MatchType, "binds to " + bs[i].ClassName, "binding", ix.BindingKey(i)));
        return new OverviewContent("ASSET CLASS", AssetTree.AssetClassesLabel, "authored asset classes and their bindings", tiles,
            Array.Empty<OverviewFact>(), "CLASSES AND BINDINGS", rows, Capped(cs.Count + bs.Count), InverterFleetNote, true);
    }

    private static OverviewContent Class(AssetPageKey key, AssetIndex ix)
    {
        IReadOnlyList<AssetClass> cs = ix.Classes;
        if (key.Index < 0 || key.Index >= cs.Count || cs[key.Index].Name != key.Name) return Missing("ASSET CLASS", key.Name ?? "", "this class");
        AssetClass c = cs[key.Index];
        int bound = 0;
        if (ix.Model.AssetDynamics is { } dyn)
            foreach (Generator g in ix.Model.AllGenerators)
                if (string.Equals(dyn.Bindings.Resolve(g.DynamicsClass, g.Type), c.Name, StringComparison.OrdinalIgnoreCase)) bound++;
        var facts = new[]
        {
            new OverviewFact("Kind", c.Kind.ToString()),
            new OverviewFact("Mode", c.Mode.Length > 0 ? c.Mode : "none"),
            new OverviewFact("Parameters", Count(c.Params.Count)),
            new OverviewFact("Bindings to it", Count(ix.Bindings.Count(b => string.Equals(b.ClassName, c.Name, StringComparison.OrdinalIgnoreCase)))),
            new OverviewFact("Units bound to it", Count(bound)),
        };
        var rows = new List<OverviewRow>();
        foreach ((string name, double value) in c.Params)
            rows.Add(new OverviewRow(name, c.ProvenanceFor(name).Tag.ToString(), AssetIndex.Num(value), null));
        return new OverviewContent("ASSET CLASS", c.Name, c.Kind.ToString(), Array.Empty<OverviewTile>(), facts, "PARAMETERS (provenance)",
            rows, "", InverterFleetNote, true);
    }

    private static OverviewContent Binding(AssetPageKey key, AssetIndex ix)
    {
        IReadOnlyList<AssetBinding> bs = ix.Bindings;
        if (key.Index < 0 || key.Index >= bs.Count || bs[key.Index].MatchType != key.Name) return Missing("ASSET BINDING", key.Name ?? "", "this binding");
        AssetBinding b = bs[key.Index];
        int ci = ix.ClassIndexOf(b.ClassName);
        var facts = new[] { new OverviewFact("Match type", b.MatchType), new OverviewFact("Class", b.ClassName) };
        return new OverviewContent("ASSET BINDING", b.MatchType, "binds to " + b.ClassName, Array.Empty<OverviewTile>(), facts, "BOUND CLASS",
            new[] { new OverviewRow(b.ClassName, "the class this rule binds", "", ci >= 0 ? ix.ClassKey(ci) : null) }, "", InverterFleetNote, true);
    }

    // ===================================================================================================================
    // OUT OF SERVICE
    // ===================================================================================================================

    private static OverviewContent OutOfServiceFleet(AssetIndex ix)
    {
        IReadOnlyList<Generator> fleet = ix.Model.AllGenerators;
        var rows = new List<OverviewRow>();
        int n = 0;
        double mva = 0;
        for (int k = 0; k < fleet.Count; k++)
        {
            Generator g = fleet[k];
            if (g.InService) continue;
            n++;
            mva += g.MBase;
            if (rows.Count < RowCap)
                rows.Add(new OverviewRow(g.Name ?? $"unit @ bus {g.Bus}", g.Type + " at bus " + BusText(ix, g.Bus),
                    g.MBase.ToString("F0", Inv) + " MVA", AssetKey.OutOfService(ix.Model, k)));
        }
        var tiles = new[]
        {
            new OverviewTile("UNITS", Count(n), "marked out of service"),
            new OverviewTile("CAPACITY", mva.ToString("F0", Inv) + " MVA", "their rating"),
        };
        return new OverviewContent("ASSET CLASS", AssetTree.OutOfServiceLabel, "units kept in the case but never solved", tiles,
            Array.Empty<OverviewFact>(), "UNITS", rows, Capped(n),
            "An out-of-service unit stays in the case (and is written back on save) but the solver never sees it.", false);
    }

    private static OverviewContent OutOfServiceUnit(AssetPageKey key, AssetIndex ix)
    {
        IReadOnlyList<Generator> fleet = ix.Model.AllGenerators;
        if (key.Index < 0 || key.Index >= fleet.Count || fleet[key.Index].InService || fleet[key.Index].Bus != key.BusA
            || !string.Equals(fleet[key.Index].Name, key.Name, StringComparison.Ordinal))
            return Missing("OUT-OF-SERVICE UNIT", key.Name ?? $"unit @ bus {key.BusA}", "this unit");
        Generator g = fleet[key.Index];
        var facts = new[]
        {
            new OverviewFact("Technology", g.Type),
            new OverviewFact("Bus", BusText(ix, g.Bus)),
            new OverviewFact("Capacity", g.MBase.ToString("F0", Inv) + " MVA"),
            new OverviewFact("In service", "no: kept in the case, never solved"),
        };
        return new OverviewContent("OUT-OF-SERVICE UNIT", g.Name ?? $"unit @ bus {g.Bus}", g.Type, Array.Empty<OverviewTile>(), facts,
            "CONNECTED", new[] { BusLink(ix, g.Bus, "the bus it would connect to") }, "", "", false);
    }
}
