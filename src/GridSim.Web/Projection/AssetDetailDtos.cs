using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Web.Visuals;

namespace GridSim.Web.Projection;

// Richer per-asset detail records for the ASSETS explorer's per-class detail pages. The current AssetsTab
// reads TopologyDto + FrameEnvelope directly and shows a thin summary; these carry the deeper per-class
// fields (H / impedance / tap / thermal / connections) the full-parity detail pages need. They are built
// from the Core GridModel + solved PowerFlowResult, which carry R/X/B/H/tap that the wire DTOs do not.
//
// PLUMBED (parity upgrade): the ASSETS tab now receives the full asset graph per frame via
// FrameEnvelope.AssetGraph (see AssetGraphProjection.Build below), built in SimSession.BuildEnvelope from the
// live GridModel + solved PowerFlowResult. The single-record builders below remain as pure helpers; the
// explorer binds AssetGraphDto directly (one payload carries every class, so the pages cross-link freely).

/// <summary>Generator detail: fuel, output vs capacity, utilisation, inertia constant.</summary>
public sealed record GeneratorDetailDto(
    int Bus, string Name, string Fuel, string FuelColor, bool Synchronous,
    double OutputMw, double CapacityMw, double UtilisationPct,
    double HSeconds, double InertiaGvaS, double BusVm);

/// <summary>Transformer detail: loading, tap ratio, series impedance, end voltages.</summary>
public sealed record TransformerDetailDto(
    int Index, string Name, int From, int To, double RateAMva,
    double FlowMw, double LoadingPct, double TapRatio, double PhaseShiftDeg,
    double RPu, double XPu, double FromVm, double ToVm);

/// <summary>Circuit (line) detail: loading, pi-model impedance R+jX / charging B, length, loss.</summary>
public sealed record CircuitDetailDto(
    int Index, string Name, int From, int To, double RateAMva,
    double FlowMw, double LoadingPct, double RPu, double XPu, double BPu,
    double LengthKm, double LossMw);

/// <summary>Substation (bus) detail: voltage/angle, demand, connected generation, circuit count.</summary>
public sealed record SubstationDetailDto(
    int Id, string Name, string BusType, double BaseKv, double Vm, double VaDeg,
    double DemandMw, double GenerationMw, int ConnectionCount);

/// <summary>Interconnector detail: flow, capacity, direction, utilisation.</summary>
public sealed record InterconnectorDetailDto(
    int Bus, string Name, double FlowMw, double CapacityMw, bool Importing, double UtilisationPct);

/// <summary>Pure builders that assemble the per-class detail records from the Core model + solved result.</summary>
public static class AssetDetails
{
    private static double VmAt(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) return i < r.Vm.Length ? r.Vm[i] : 1.0;
        return 1.0;
    }

    private static double VaAt(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++) if (r.BusIds[i] == busId) return i < r.VaDeg.Length ? r.VaDeg[i] : 0.0;
        return 0.0;
    }

    /// <summary>Detail for one generator (by index into <c>model.Generators</c>).</summary>
    public static GeneratorDetailDto Generator(GridModel model, PowerFlowResult r, int genIndex)
    {
        Generator g = model.Generators[genIndex];
        FuelInfo fi = Fuels.Classify(g.Type);
        double cap = g.MBase > 0 ? g.MBase : Math.Max(Math.Abs(g.Pg), g.Pmax is > 0 and < double.PositiveInfinity ? g.Pmax : 0);
        // N325 (DOC-17): the unit's rotating-mass inertia (0 for a known converter technology, N289), the share it adds to
        // SystemInertiaGvaS; unchanged for every synchronous unit.
        double inertia = GridModel.RotatingInertiaMwS(g) / 1000.0;   // GVA.s
        return new GeneratorDetailDto(
            g.Bus, g.Name ?? fi.Name, fi.Name, fi.Color, fi.Synchronous,
            g.Pg, cap, cap > 0 ? g.Pg / cap * 100.0 : 0,
            g.H, inertia, VmAt(r, g.Bus));
    }

    /// <summary>Detail for one branch (by index into <c>model.Branches</c>), used for both transformers and
    /// circuits - the caller picks the record it wants. <paramref name="branchIndex"/> is also the join key to
    /// the frame's per-edge <see cref="EdgeStateDto"/>.</summary>
    public static TransformerDetailDto Transformer(GridModel model, PowerFlowResult r, int branchIndex)
    {
        Branch br = model.Branches[branchIndex];
        LineFlow? lf = branchIndex < r.LineFlows.Count ? r.LineFlows[branchIndex] : null;
        return new TransformerDetailDto(
            branchIndex, br.Name ?? $"{br.From}-{br.To}", br.From, br.To, br.RateAMva,
            lf?.PFromMw ?? 0, lf?.LoadingPercent ?? 0, br.TapRatio, br.PhaseShiftDeg,
            br.R, br.X, VmAt(r, br.From), VmAt(r, br.To));
    }

    /// <summary>Circuit (line) detail for one branch.</summary>
    public static CircuitDetailDto Circuit(GridModel model, PowerFlowResult r, int branchIndex)
    {
        Branch br = model.Branches[branchIndex];
        LineFlow? lf = branchIndex < r.LineFlows.Count ? r.LineFlows[branchIndex] : null;
        return new CircuitDetailDto(
            branchIndex, br.Name ?? $"{br.From}-{br.To}", br.From, br.To, br.RateAMva,
            lf?.PFromMw ?? 0, lf?.LoadingPercent ?? 0, br.R, br.X, br.B,
            br.LengthKm, lf?.PLossMw ?? 0);
    }

    /// <summary>Substation (bus) detail: demand from the bus, connected generation summed over its generators,
    /// and how many in-service branches touch it.</summary>
    public static SubstationDetailDto Substation(GridModel model, PowerFlowResult r, int busId)
    {
        Bus? b = null;
        foreach (Bus x in model.Buses) if (x.Id == busId) { b = x; break; }
        double gen = 0;
        foreach (Generator g in model.Generators) if (g.Bus == busId && g.Pg > 0) gen += g.Pg;
        int conns = 0;
        foreach (Branch br in model.Branches) if (br.InService && (br.From == busId || br.To == busId)) conns++;
        return new SubstationDetailDto(
            busId, b?.Name ?? $"Bus {busId}", (b?.Type ?? BusType.Pq).ToString(), b?.BaseKv ?? 0,
            VmAt(r, busId), VaAt(r, busId), b?.Pd ?? 0, gen, conns);
    }

    /// <summary>Interconnector detail (by generator index) - a converter-connected import/export link.</summary>
    public static InterconnectorDetailDto Interconnector(GridModel model, int genIndex)
    {
        Generator g = model.Generators[genIndex];
        double cap = g.MBase > 0 ? g.MBase : Math.Abs(g.Pg);
        return new InterconnectorDetailDto(
            g.Bus, g.Name ?? "Interconnector", g.Pg, cap, g.Pg > 0,
            cap > 0 ? Math.Abs(g.Pg) / cap * 100.0 : 0);
    }
}

// ===========================================================================
// The asset graph - one per-frame payload carrying every asset class with the
// deeper electrical fields the navigable explorer needs. Static params (R/X/B,
// H/MBase, OLTC ranges) and live state (Pg/Qg, tap, solved V/flows) are carried
// together so the pages cross-link freely without re-reading the Core model.
// Every double is finite (Pmax/Q-limits can be +/-inf in the model - carried as
// a bool + 0 sentinel) so nothing non-finite crosses to the browser / JSON.
// ===========================================================================

/// <summary>One generator (or interconnector) with nameplate + live electrical state.</summary>
public sealed record GenAssetDto(
    int Index, int Bus, string Name, string Fuel, string FuelColor, string FuelClass,
    bool Synchronous, bool Interconnector,
    double Pg, double Qg, double PmaxMw, bool Capped, double MBase, double HSeconds,
    double Vg, double QminMw, double QmaxMw, bool QLimited);

/// <summary>One branch (line or transformer) with pi-model params + live solved flow.</summary>
public sealed record BranchAssetDto(
    int Index, int From, int To, string Name, bool IsTransformer,
    double RateAMva, double RPu, double XPu, double BPu, double TapRatio, double PhaseShiftDeg, double LengthKm,
    bool Solved, double PFromMw, double PToMw, double QFromMvar, double QToMvar,
    double PLossMw, double QLossMvar, double LoadingPercent, double RatingMva);

/// <summary>One bus (substation) with nominal demand + solved voltage/injection.</summary>
public sealed record BusAssetDto(
    int Id, string Name, double BaseKv, string BusType, double DemandMw, double DemandMvar,
    bool Solved, double Vm, double VaDeg, double PInjMw, double QInjMvar);

/// <summary>One on-load tap changer and the bus it regulates.</summary>
public sealed record OltcAssetDto(
    string Transformer, int ControlsBus, double TargetVpu, double DeadbandPu,
    double MinTap, double MaxTap, double StepPu);

/// <summary>The whole model as an asset graph for one solved instant.</summary>
public sealed record AssetGraphDto(
    IReadOnlyList<GenAssetDto> Generators,
    IReadOnlyList<BranchAssetDto> Branches,
    IReadOnlyList<BusAssetDto> Buses,
    IReadOnlyList<OltcAssetDto> Oltcs,
    double SystemInertiaGvaS);

/// <summary>Projects the live model + solved result into a single <see cref="AssetGraphDto"/> per frame.</summary>
public static class AssetGraphProjection
{
    private static double Finite(double v, double fallback = 0) =>
        double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

    public static AssetGraphDto Build(GridModel model, PowerFlowResult r)
    {
        // O(1) solved lookups by bus id (first match wins, as the WPF twins do).
        int n = r.BusIds.Count;
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) idx.TryAdd(r.BusIds[i], i);

        var gens = new List<GenAssetDto>(model.Generators.Count);
        for (int gi = 0; gi < model.Generators.Count; gi++)
        {
            Generator g = model.Generators[gi];
            FuelInfo fi = Fuels.Classify(g.Type);
            bool ic = FuelTaxonomy.IsInterconnectorLink(g.Type);   // the one link rule (bug #28)
            bool capped = g.Pmax > 0 && !double.IsInfinity(g.Pmax);
            bool qlim = !double.IsInfinity(g.Qmin) && !double.IsInfinity(g.Qmax);
            gens.Add(new GenAssetDto(
                gi, g.Bus, g.Name ?? (ic ? $"link @ bus {g.Bus}" : $"unit @ bus {g.Bus}"),
                fi.Name, fi.Color, fi.Class.ToString(), fi.Synchronous, ic,
                Finite(g.Pg), Finite(g.Qg), capped ? Finite(g.Pmax) : 0, capped,
                Finite(g.MBase), Finite(g.H), Finite(g.Vg, 1.0),
                qlim ? Finite(g.Qmin) : 0, qlim ? Finite(g.Qmax) : 0, qlim));
        }

        var branches = new List<BranchAssetDto>(model.Branches.Count);
        for (int bi = 0; bi < model.Branches.Count; bi++)
        {
            Branch b = model.Branches[bi];
            LineFlow? lf = bi < r.LineFlows.Count ? r.LineFlows[bi] : null;
            branches.Add(new BranchAssetDto(
                bi, b.From, b.To, b.Name ?? $"{b.From}-{b.To}", b.IsTransformer,
                Finite(b.RateAMva), Finite(b.R), Finite(b.X), Finite(b.B),
                Finite(b.TapRatio, 1.0), Finite(b.PhaseShiftDeg), Finite(b.LengthKm),
                lf is not null,
                Finite(lf?.PFromMw ?? 0), Finite(lf?.PToMw ?? 0), Finite(lf?.QFromMvar ?? 0), Finite(lf?.QToMvar ?? 0),
                Finite(lf?.PLossMw ?? 0), Finite(lf?.QLossMvar ?? 0), Finite(lf?.LoadingPercent ?? 0), Finite(lf?.RatingMva ?? 0)));
        }

        var buses = new List<BusAssetDto>(model.Buses.Count);
        foreach (Bus b in model.Buses)
        {
            bool solved = idx.TryGetValue(b.Id, out int i);
            string type = b.Type switch { BusType.Slack => "SLACK", BusType.Pv => "PV", _ => "PQ" };
            buses.Add(new BusAssetDto(
                b.Id, b.Name ?? $"bus {b.Id}", Finite(b.BaseKv), type, Finite(b.Pd), Finite(b.Qd),
                solved,
                solved && i < r.Vm.Length ? Finite(r.Vm[i], 1.0) : 1.0,
                solved && i < r.VaDeg.Length ? Finite(r.VaDeg[i]) : 0,
                solved && i < r.PInjMw.Length ? Finite(r.PInjMw[i]) : 0,
                solved && i < r.QInjMvar.Length ? Finite(r.QInjMvar[i]) : 0));
        }

        var oltcs = new List<OltcAssetDto>(model.Oltcs.Count);
        foreach (Oltc o in model.Oltcs)
            oltcs.Add(new OltcAssetDto(
                o.Transformer, o.ControlsBus, Finite(o.TargetVpu, 1.0), Finite(o.DeadbandPu),
                Finite(o.MinTap), Finite(o.MaxTap), Finite(o.StepPu)));

        return new AssetGraphDto(gens, branches, buses, oltcs, Finite(model.SystemInertiaGvaS));
    }
}
