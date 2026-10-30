using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL: carbon tracing - "what fuel is each load actually consuming?". It combines
/// <see cref="PowerFlowTracing"/> (which generator's power reached which load) with a per-generator
/// carbon intensity (from the generator's fuel type) to give each LOAD a physics-based carbon
/// intensity: the CO2 attributable to the electricity that generator-tracing says actually flowed to
/// it. Unlike a regional grid-average carbon-intensity figure, this is locational and flow-based - two
/// loads on the same system get different intensities depending on which generators feed them.
///
/// PROVENANCE: inherits the tracing convention (proportional sharing - an accounting method, not
/// physical electron tracking) and adds per-fuel emission factors. The factors (<see cref="FuelCarbon"/>,
/// tCO2/MWh) are the one carbon table's figures (<see cref="Presentation.CarbonIntensityTable"/>, the same figures the
/// market and machine views use; R145 / R146); real
/// per-plant/metered intensities would need a plant-emissions join (a GB-data follow-up). Generic IEEE
/// cases carry no fuel type, so they fall back to the "other" default - the trace is then structurally
/// correct but the intensities are placeholders. Opt-in via <c>experimental carbon</c>.
/// </summary>
public static class CarbonTracing
{
    /// <summary>A load's traced carbon: its demand, the CO2 rate attributed to it, and the resulting
    /// intensity in grams of CO2 per kWh (the figure carbon-intensity dashboards quote).</summary>
    public sealed record LoadCarbon(int LoadBus, double DemandMw, double TonnesPerHour, double GramsPerKwh);

    /// <summary>The carbon trace of a solved state.</summary>
    public sealed record CarbonTraceResult(
        IReadOnlyList<LoadCarbon> Loads,
        double SystemGramsPerKwh,
        double TotalTonnesPerHour,
        bool Traced);

    /// <summary>Trace the carbon each load consumes, given a solved state.</summary>
    /// <remarks>D-DOC-087 (October 2026): <paramref name="result"/> must have converged
    /// (<see cref="PowerFlow.ConvergenceContract.RequireConverged"/>); a non-converged iterate's flows are not traced.</remarks>
    public static CarbonTraceResult Trace(GridModel model, PowerFlowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        PowerFlow.ConvergenceContract.RequireConverged(result, "Carbon tracing");   // D-DOC-087: the convergence contract
        PowerFlowTracing.TraceResult tr = PowerFlowTracing.Trace(model, result);
        if (!tr.FlowGraphAcyclic)
            return new CarbonTraceResult(Array.Empty<LoadCarbon>(), 0, 0, Traced: false);

        // Per generation-bus carbon intensity (tCO2/MWh) = output-weighted mean over the fuels at the bus,
        // falling back to the unweighted mean when the scheduled Pg is ~0 (the slack's Pg is 0 in the
        // data even though its solved output is not - its fuel still determines its intensity).
        var wNum = new Dictionary<int, double>();
        var wDen = new Dictionary<int, double>();
        var uNum = new Dictionary<int, double>();
        var uCount = new Dictionary<int, int>();
        foreach (Generator g in model.Generators)
        {
            double intensity = FuelCarbon.IntensityTPerMwh(g.Type);
            double pg = Math.Max(0.0, g.Pg);
            wNum[g.Bus] = wNum.GetValueOrDefault(g.Bus) + pg * intensity;
            wDen[g.Bus] = wDen.GetValueOrDefault(g.Bus) + pg;
            uNum[g.Bus] = uNum.GetValueOrDefault(g.Bus) + intensity;
            uCount[g.Bus] = uCount.GetValueOrDefault(g.Bus) + 1;
        }
        double BusIntensity(int busId)
        {
            if (wDen.TryGetValue(busId, out double d) && d > 1e-9) return wNum[busId] / d;
            if (uCount.TryGetValue(busId, out int c) && c > 0) return uNum[busId] / c;
            return FuelCarbon.DefaultTPerMwh;
        }

        // Demand per bus. GR-483: the load each bus draws at its solved voltage (LoadAtVoltage), the demand the trace
        // allocated to it; the nominal Pd gave a ZIP bus the wrong intensity. Constant-power buses are unchanged.
        var vmById = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++) vmById[result.BusIds[i]] = result.Vm[i];
        var demand = new Dictionary<int, double>();
        foreach (Bus b in model.Buses)
        {
            double pd = vmById.TryGetValue(b.Id, out double vm) ? LoadAtVoltage.Mw(b, vm).PMw : b.Pd;
            if (pd > 1e-9) demand[b.Id] = pd;
        }

        // load CO2 (tCO2/h) = sum over generators of (traced MW to this load) * (gen intensity tCO2/MWh).
        var loadTph = new Dictionary<int, double>();
        foreach (PowerFlowTracing.GenLoadFlow f in tr.Allocations)
            loadTph[f.LoadBus] = loadTph.GetValueOrDefault(f.LoadBus) + f.Mw * BusIntensity(f.GenBus);

        var loads = new List<LoadCarbon>();
        double totalTph = 0.0, totalMw = 0.0;
        foreach ((int busId, double pd) in demand)
        {
            double tph = loadTph.GetValueOrDefault(busId);
            // tCO2/MWh * 1000 = gCO2/kWh.
            double gPerKwh = pd > 1e-9 ? tph / pd * 1000.0 : 0.0;
            loads.Add(new LoadCarbon(busId, pd, tph, gPerKwh));
            totalTph += tph;
            totalMw += pd;
        }
        GridSim.Core.Numerics.FrozenIntroSort.Sort(loads, (x, y) => y.GramsPerKwh.CompareTo(x.GramsPerKwh));   // GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)
        double systemG = totalMw > 1e-9 ? totalTph / totalMw * 1000.0 : 0.0;
        return new CarbonTraceResult(loads, systemG, totalTph, Traced: true);
    }
}

/// <summary>
/// Per-fuel CO2 emission factors (tonnes CO2 per MWh) for carbon tracing, classified from a generator's free-text fuel
/// <c>Type</c>. R145 / R146 (CF4): the free text is classified to Core's one fuel taxonomy (<see cref="Model.FuelTypes"/>,
/// economics reader) and the figure is the ONE figure of that kind in <see cref="Presentation.CarbonIntensityTable"/>
/// (NESO Carbon Intensity Regional Forecast Methodology, May 2024, Table 1, direct combustion; see the table for the
/// source and the kind mapping). This class carries no figure of its own, so the carbon trace, the MACHINE tab and the
/// MARKET tab give the same CO2 for the same dispatch. Non-combusting sources are zero, imports and grid infeeds zero
/// (combustion outside the network), and an unknown / empty type takes the table's "Other" figure
/// (<see cref="DefaultTPerMwh"/>), the case for fuel-less IEEE test systems.
/// </summary>
public static class FuelCarbon
{
    /// <summary>Fallback intensity for an unknown/empty fuel type (tCO2/MWh): the table's "Other" figure.</summary>
    public const double DefaultTPerMwh = Presentation.CarbonIntensityTable.OtherTPerMwh;

    /// <summary>Carbon intensity (tCO2/MWh) for a generator's fuel <paramref name="type"/>.
    /// <para>R305 rest and N020 (October 2026): the fuel is classified by Core's one taxonomy,
    /// <see cref="Model.FuelTypes"/> (economics reader), whole words by their start. R145 / R146 (CF4): the kind's figure
    /// is read from the one carbon table (<see cref="Presentation.CarbonIntensityTable.TPerMwh"/>).</para>
    /// </summary>
    public static double IntensityTPerMwh(string? type)
        => Presentation.CarbonIntensityTable.TPerMwh(Model.FuelTypes.Classify(type, Model.FuelReader.Economics));
}
