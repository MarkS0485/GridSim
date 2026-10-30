using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Web.Visuals;

namespace GridSim.Web.Projection;

/// <summary>One fuel's aggregate across the online fleet for a frame.</summary>
public sealed record FuelAgg(string Fuel, string Color, FuelClass Class, bool Synchronous, double Mw, double InertiaGvaS);

/// <summary>
/// Computes the per-fuel MW / inertia breakdown of a model's generators once, so the GEN&amp;INERTIA,
/// OPERATOR, MARKET and NESO RP1 projections all read from the same classification (via the ported
/// <see cref="Fuels"/> taxonomy) instead of each re-iterating and risking drift.
/// </summary>
public sealed class FuelBreakdown
{
    // Carbon factors now come from the shared Core presenter (CarbonFactors.IntensityKgPerMwh, kg/MWh)
    // so the MARKET tab's carbon line can never drift from the WPF MarketVm twin. Convert to kg/kWh (/1000).

    public IReadOnlyList<FuelAgg> Fuels { get; }
    public double TotalGenMw { get; }
    public double SyncMw { get; }
    public double ConverterMw { get; }
    public double FossilMw { get; }
    public double RenewableMw { get; }
    public double InertiaGvaS { get; }
    public double IntrinsicInertiaGvaS { get; }   // synchronous only
    public double LargestUnitMw { get; }
    public string LargestUnitName { get; }
    public double CarbonKgPerKwh { get; }

    public double SyncFraction => TotalGenMw > 0 ? SyncMw / TotalGenMw : 0;
    public double RenewableFraction => TotalGenMw > 0 ? RenewableMw / TotalGenMw : 0;

    public FuelBreakdown(GridModel model)
    {
        var agg = new Dictionary<string, (string color, FuelClass cls, bool sync, double mw, double inertia)>(StringComparer.Ordinal);
        double total = 0, sync = 0, conv = 0, fossil = 0, renew = 0, inertia = 0, intrinsic = 0, largest = 0, carbonNumer = 0;
        string largestName = "-";

        foreach (Generator g in model.Generators)
        {
            FuelInfo fi = Visuals.Fuels.Classify(g.Type);
            double mw = Math.Max(0, g.Pg);
            // N325 (DOC-17): this unit's rotating-mass share (H x MBase, or exactly 0 for a known converter technology,
            // N289), so the per-fuel figures and the total agree with SystemInertiaGvaS. Same value as before for every
            // unit that is not a converter type with H > 0 (no bundled case has one).
            double gInertia = GridModel.RotatingInertiaMwS(g) / 1000.0;   // GVA.s

            if (!agg.TryGetValue(fi.Name, out var cur)) cur = (fi.Color, fi.Class, fi.Synchronous, 0, 0);
            agg[fi.Name] = (cur.color, cur.cls, cur.sync, cur.mw + mw, cur.inertia + gInertia);

            total += mw;
            inertia += gInertia;
            if (fi.Synchronous) { sync += mw; intrinsic += gInertia; } else { conv += mw; }
            if (fi.Class == FuelClass.Fossil) fossil += mw;
            if (fi.IsRenewable) renew += mw;
            carbonNumer += mw * CarbonFactors.IntensityKgPerMwh(fi.Name, fi.Class == FuelClass.Fossil) / 1000.0;
        }

        // The largest secured unit: it feeds the RP1 credible loss and the OPERATOR largest-online figure. Q1 (2026-10):
        // computed by the ONE Core presenter the WPF NesoRp1Vm and OperatorVm also call, so the twins cannot drift
        // (largest positive Pg, first unit wins a tie, name falls back to the fuel name). N039 (B4, 2026-10): every
        // single infeed counts, converter plant and interconnector imports included (SQSS v2.10 para 5.1.2; FRCR
        // Methodology v3 Table 2); before, synchronous units only.
        (largest, largestName) = FrequencyRiskPresenter.LargestSecuredUnit(model.Generators);

        Fuels = agg.Select(kv => new FuelAgg(kv.Key, kv.Value.color, kv.Value.cls, kv.Value.sync, kv.Value.mw, kv.Value.inertia))
                   .OrderByDescending(f => f.Mw).ToList();
        TotalGenMw = total; SyncMw = sync; ConverterMw = conv; FossilMw = fossil; RenewableMw = renew;
        InertiaGvaS = inertia; IntrinsicInertiaGvaS = intrinsic;
        LargestUnitMw = largest; LargestUnitName = largestName;
        CarbonKgPerKwh = total > 0 ? carbonNumer / total : 0;
    }

    /// <summary>
    /// RAW replay breakdown: build the same per-fuel aggregate from the bridge's published generation MIX (MW by
    /// fuel) instead of a solved model's generators, using the REAL system inertia (raw Outturn Inertia, GVA.s)
    /// rather than a summed per-machine figure. No solve, no inference: the per-fuel MW are the published mix, the
    /// aggregate inertia is the measured value, and each synchronous fuel's inertia row is that measured total
    /// apportioned by its share of synchronous MW (a display split whose sum stays exactly the measured value).
    /// Per-unit fields (<see cref="LargestUnitMw"/>) are not derivable from an aggregate mix and read zero.
    /// </summary>
    public FuelBreakdown(BridgeProtocol.GenerationMixBlock mix, double inertiaGvaS)
    {
        var agg = new Dictionary<string, (string color, FuelClass cls, bool sync, double mw, double inertia)>(StringComparer.Ordinal);
        double total = 0, sync = 0, conv = 0, fossil = 0, renew = 0, carbonNumer = 0;

        foreach ((string label, double mw) in mix.Rows())     // finite MW rows, one per published fuel
        {
            if (mw <= 0) continue;
            FuelInfo fi = Visuals.Fuels.Classify(label);
            if (!agg.TryGetValue(fi.Name, out var cur)) cur = (fi.Color, fi.Class, fi.Synchronous, 0, 0);
            agg[fi.Name] = (cur.color, cur.cls, cur.sync, cur.mw + mw, 0);   // per-fuel inertia apportioned below
            total += mw;
            if (fi.Synchronous) sync += mw; else conv += mw;
            if (fi.Class == FuelClass.Fossil) fossil += mw;
            if (fi.IsRenewable) renew += mw;
            carbonNumer += mw * CarbonFactors.IntensityKgPerMwh(fi.Name, fi.Class == FuelClass.Fossil) / 1000.0;
        }

        double measured = double.IsFinite(inertiaGvaS) && inertiaGvaS > 0 ? inertiaGvaS : 0;
        if (measured > 0 && sync > 0)
            foreach (string k in agg.Keys.ToList())
            {
                var v = agg[k];
                if (v.sync) agg[k] = (v.color, v.cls, v.sync, v.mw, measured * (v.mw / sync));
            }

        Fuels = agg.Select(kv => new FuelAgg(kv.Key, kv.Value.color, kv.Value.cls, kv.Value.sync, kv.Value.mw, kv.Value.inertia))
                   .OrderByDescending(f => f.Mw).ToList();
        TotalGenMw = total; SyncMw = sync; ConverterMw = conv; FossilMw = fossil; RenewableMw = renew;
        InertiaGvaS = measured; IntrinsicInertiaGvaS = measured;   // measured system inertia is synchronous by definition
        // The aggregate mix carries no per-unit data, so the live largest online unit can't be computed; anchor the
        // credible loss to the SQSS secured-event standard (1,800 MW - today's rules) so RP1's purchased time / MIF
        // stay calculable rather than an infinite ride-through against a 0 MW loss.
        LargestUnitMw = GridSim.Core.Presentation.CredibleLossBasis.FictionLossMw;   // 1,800 MW (R204: was a literal)
        LargestUnitName = "SQSS secured-event standard";
        CarbonKgPerKwh = total > 0 ? carbonNumer / total : 0;
    }
}
