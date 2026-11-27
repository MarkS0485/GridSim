using GridSim.Core.Model;

namespace GridSim.Core.Analysis;

/// <summary>
/// The demand a DC balance must meet, counted the one way every DC consumer counts it (B33): a bus's demand Pd plus its
/// shunt conductance Gs (MW drawn at 1.0 pu, MATPOWER's GS), which the DC model treats as a constant-power load, as
/// MATPOWER's <c>Pg = Pinj + Pload + Gs</c> does. Each helper returns the very operand the old code used (Pd, or the
/// island's sum of Pd) when the shunt part is exactly zero, so a model with no Gs gives the same bits as before.
/// </summary>
internal static class DcDemand
{
    /// <summary>The DC demand of one bus (MW): Pd, plus Gs when Gs is not zero.</summary>
    internal static double BusMw(Bus bus) => bus.Gs == 0.0 ? bus.Pd : bus.Pd + bus.Gs;

    /// <summary>The DC demand of a whole model (MW), summed over the buses in model order.</summary>
    internal static double TotalMw(GridModel model) => model.Buses.Sum(b => BusMw(b));

    /// <summary>The total shunt conductance load of a model (MW at 1.0 pu), in model order: 0 when no bus has Gs.</summary>
    internal static double ShuntMw(GridModel model)
    {
        double s = 0.0;
        foreach (Bus b in model.Buses) if (b.Gs != 0.0) s += b.Gs;
        return s;
    }

    /// <summary>The DC demand of an island (MW): its Pd sum, plus its Gs sum when that is not zero.</summary>
    internal static double IslandMw(DcIsland island) => island.ShuntLoadMw == 0.0 ? island.LoadMw : island.LoadMw + island.ShuntLoadMw;

    /// <summary>
    /// The model the DC computations made before B33 saw (rows 11 and 12 of the B33 consumer list: the Newton DC-start
    /// seed and the AC-OPF's inner DC-OPF passes): every branch tap ratio 1, every phase shift 0 and every bus Gs 0, all
    /// else carried by <see cref="GridModel.CopyWith"/>. Fed to the DC classes it gives exactly the reactance-only model
    /// of earlier releases. A model with none of the three is returned itself, not copied.
    /// </summary>
    internal static GridModel LegacyView(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        bool branches = false, buses = false;
        foreach (Branch br in model.Branches)
            if (br.TapRatio != 1.0 || br.PhaseShiftDeg != 0.0) { branches = true; break; }
        foreach (Bus b in model.Buses)
            if (b.Gs != 0.0) { buses = true; break; }
        if (!branches && !buses) return model;
        return model.CopyWith(
            buses: buses ? model.Buses.Select(b => b.Gs != 0.0 ? b with { Gs = 0.0 } : b).ToList() : null,
            branches: branches
                ? model.Branches.Select(br => br.TapRatio != 1.0 || br.PhaseShiftDeg != 0.0 ? br with { TapRatio = 1.0, PhaseShiftDeg = 0.0 } : br).ToList()
                : null);
    }
}
