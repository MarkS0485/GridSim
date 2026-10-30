using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;

namespace GridSim.Core.Analysis;

/// <summary>The kind of developing condition a watch-list item represents.</summary>
public enum WatchCategory { Convergence, ThermalOverload, VoltageLimit, BoundaryTransfer, Frequency }

/// <summary>
/// One developing condition on the operator watch-list: what it is, where, and its ranking score. The score
/// is <b>consequence x proximity</b> - both in [0,1] - so a severe outcome that is close ranks above a severe
/// outcome that is far, or a near-certain event whose consequence is small.
/// </summary>
/// <param name="Category">The kind of condition.</param>
/// <param name="Location">The asset/bus it concerns.</param>
/// <param name="Description">A one-line operator-readable summary.</param>
/// <param name="Consequence">How bad if it happens, 0..1 (relative severity).</param>
/// <param name="Proximity">How close to the triggering condition, 0..1 (1 = at/over the limit now).</param>
/// <param name="LeadTimeSeconds">Optional estimated time until the condition fires (e.g. a thermal time-to-trip).</param>
public sealed record WatchItem(
    WatchCategory Category,
    string Location,
    string Description,
    double Consequence,
    double Proximity,
    double? LeadTimeSeconds = null)
{
    /// <summary>The ranking score: consequence x proximity.</summary>
    public double Score => Consequence * Proximity;
}

/// <summary>Options for building the watch-list.</summary>
public sealed record WatchListOptions
{
    /// <summary>How many items to keep (the short, actionable list). Default 5.</summary>
    public int TopN { get; init; } = 5;
    /// <summary>Branch loading (fraction of RateA) at/above which a thermal item is raised. Default
    /// <see cref="WatchList.DefaultThermalWatchFraction"/> (0.85).</summary>
    public double ThermalWatchFraction { get; init; } = WatchList.DefaultThermalWatchFraction;
    /// <summary>Deviation from the bus's reference voltage at/above which a voltage item is raised (pu). The
    /// reference is the regulated setpoint for a voltage-controlled bus and nominal 1.0 pu otherwise (see
    /// <see cref="WatchList"/>). Default <see cref="VoltageLimits.WatchBandPu"/> (0.04).</summary>
    public double VoltageWatchBandPu { get; init; } = VoltageLimits.WatchBandPu;
    /// <summary>The voltage deviation (pu) treated as maximal proximity (Vm at the statutory edge). Default
    /// <see cref="VoltageLimits.StatutoryBandPu"/> (0.10).</summary>
    public double VoltageLimitBandPu { get; init; } = VoltageLimits.StatutoryBandPu;
}

/// <summary>The ranked watch-list.</summary>
public sealed record WatchListReport(IReadOnlyList<WatchItem> Items)
{
    /// <summary>The single most pressing condition, or null if the system is clear.</summary>
    public WatchItem? Top => Items.Count > 0 ? Items[0] : null;
}

/// <summary>
/// Aggregates the tool's scattered precursor signals into ONE ranked watch-list of developing conditions -
/// the short "top five, worst first" an operator can action, instead of one warning at a time. Each source
/// contributes items scored by <b>consequence x proximity</b>: the physics gives the severity, the current
/// state gives how close we are, and the ranking is the product. It reads the solved state (branch loadings,
/// bus voltages, convergence health) and, when supplied, the protection assessment (thermal time-to-trip)
/// and boundary flows.
/// </summary>
public static class WatchList
{
    // ---- Named constants (October 2026 review, WS13 R157). Values unchanged.

    /// <summary>Default branch loading (fraction of RateA) at which a thermal item is raised.</summary>
    public const double DefaultThermalWatchFraction = 0.85;

    /// <summary>Boundary transfer (fraction of the boundary's security limit) at which a boundary item is
    /// raised. The same 0.85 as the thermal default but a separate concept: it is not affected by
    /// <see cref="WatchListOptions.ThermalWatchFraction"/>.</summary>
    public const double BoundaryWatchFraction = 0.85;

    /// <summary>A converged solve is listed as straining once it used this fraction of the iteration cap.</summary>
    public const double ConvergenceStrainFraction = 0.6;

    /// <summary>A boundary's consequence is its limit over this scale (MW), clamped to
    /// [<see cref="BoundaryConsequenceFloor"/>, 1]: a 5 GW boundary carries the largest transfer at risk.</summary>
    public const double BoundaryConsequenceScaleMw = 5000.0;

    /// <summary>Lowest consequence a boundary item can have, however small its limit.</summary>
    public const double BoundaryConsequenceFloor = 0.3;

    /// <summary>A voltage item's consequence is <c>VoltageConsequenceBase + VoltageConsequenceSlope * proximity</c>:
    /// a fixed moderate severity (a voltage excursion is serious but usually local), rising to 1 at the
    /// statutory edge.</summary>
    public const double VoltageConsequenceBase = 0.5;

    /// <summary>See <see cref="VoltageConsequenceBase"/>.</summary>
    public const double VoltageConsequenceSlope = 0.5;

    public static WatchListReport Assess(
        GridModel model,
        PowerFlowResult result,
        WatchListOptions? options = null,
        ProtectionReport? protection = null,
        IReadOnlyList<BoundaryFlow>? boundaries = null,
        PowerFlowOptions? solveOptions = null)
    {
        WatchListOptions opt = options ?? new WatchListOptions();
        var items = new List<WatchItem>();

        items.AddRange(Convergence(result, solveOptions));
        // A non-converged iterate is not a state: its flows and voltages are not the grid's, so no thermal or voltage
        // item is built from them (they used to be, and listed beside the convergence item as if real). The
        // convergence item alone says what is wrong; boundary flows come from elsewhere and are kept.
        if (result.Status == PowerFlowStatus.Converged)
        {
            items.AddRange(BranchOverloads(result, opt, protection));
            items.AddRange(VoltageLimits(model, result, opt));
        }
        if (boundaries is not null) items.AddRange(Boundaries(boundaries));

        GridSim.Core.Numerics.FrozenIntroSort.Sort(items, (a, b) => b.Score.CompareTo(a.Score)); // worst first; GR-867: frozen .NET 10 introsort, so a tie keeps one order on every runtime (bit-identical to List.Sort today)
        return new WatchListReport(items.Take(opt.TopN).ToList());
    }

    // Non-convergence / a solve straining toward the iteration cap is a system-wide instability precursor.
    private static IEnumerable<WatchItem> Convergence(PowerFlowResult r, PowerFlowOptions? solveOpt)
    {
        int maxIter = (solveOpt ?? new PowerFlowOptions()).MaxIterations;
        if (r.Status != PowerFlowStatus.Converged)
        {
            yield return new WatchItem(WatchCategory.Convergence, "system",
                $"power flow did not converge ({r.Status})", Consequence: 1.0, Proximity: 1.0);
        }
        else if (maxIter > 0)
        {
            double prox = Math.Clamp((double)r.Iterations / maxIter, 0, 1);
            if (prox >= ConvergenceStrainFraction) // only worth listing when the solve is genuinely straining
                yield return new WatchItem(WatchCategory.Convergence, "system",
                    $"power flow took {r.Iterations}/{maxIter} iterations (approaching stress limit)",
                    Consequence: 1.0, Proximity: prox);
        }
    }

    // Branch thermal overloads. Proximity = loading vs rating; consequence = the circuit's share of the
    // largest flow (a bigger circuit tripping redistributes more). Both use the HEAVIER end's MVA (see Mva). A protection thermal time-to-trip, when
    // available, sharpens the proximity and provides the lead time.
    private static IEnumerable<WatchItem> BranchOverloads(PowerFlowResult r, WatchListOptions opt, ProtectionReport? protection)
    {
        double maxFlow = 0;
        foreach (LineFlow lf in r.LineFlows) maxFlow = Math.Max(maxFlow, Mva(lf));
        if (maxFlow <= 0) yield break;

        // Thermal time-to-trip per branch name, if a protection assessment was supplied.
        var timeToTrip = new Dictionary<string, double>(StringComparer.Ordinal);
        if (protection is not null)
            foreach (RelayVerdict v in protection.Verdicts)
                if (v.Device == "49" && v.Operate && v.TimeToTripS is { } t)
                    timeToTrip[v.LocationName] = timeToTrip.TryGetValue(v.LocationName, out double e) ? Math.Min(e, t) : t;

        foreach (LineFlow lf in r.LineFlows)
        {
            double loading = lf.RatingMva > 0 ? Mva(lf) / lf.RatingMva : 0.0;
            if (loading < opt.ThermalWatchFraction) continue;

            string name = lf.Name.Length > 0 ? lf.Name : $"branch {lf.From}-{lf.To}";
            double proximity = Math.Clamp(loading, 0, 1);
            double consequence = Math.Clamp(Mva(lf) / maxFlow, 0, 1);
            double? lead = timeToTrip.TryGetValue(name, out double tt) ? tt : (double?)null;
            if (lead is not null) proximity = Math.Max(proximity, 1.0); // an operating thermal element is at the limit

            yield return new WatchItem(WatchCategory.ThermalOverload, name,
                $"{name} at {loading * 100:F0}% of rating" + (lead is not null ? $"; thermal replica trips in {lead:F0} s" : ""),
                consequence, proximity, lead);
        }
    }

    // Bus voltage-band excursions. Proximity ramps from the watch band to the statutory edge.
    //
    // The deviation is measured from the voltage the bus is MEANT to sit at (R229, October 2026): a slack or PV
    // bus with a generator is regulated to its setpoint (the shared VoltageSetpoint rule, as the solvers use),
    // so a plant bus deliberately held at 1.04-1.06 pu is not a developing condition and must not fill the
    // top-N list (SyntheticGb regulates every 400 kV plant bus at 1.04). If such a bus has drifted off its
    // setpoint - its unit has hit a reactive limit - the deviation from the setpoint is exactly the signal. A
    // PQ bus (or a voltage-controlled bus with no unit, which the solvers treat as PQ) is measured from nominal.
    private static IEnumerable<WatchItem> VoltageLimits(GridModel model, PowerFlowResult r, WatchListOptions opt)
    {
        var nameById = new Dictionary<int, string>(model.Buses.Count);
        var refById = new Dictionary<int, double>(model.Buses.Count);
        foreach (Bus b in model.Buses)
        {
            nameById[b.Id] = b.Name ?? $"bus {b.Id}";
            double? setpoint = b.Type is BusType.Slack or BusType.Pv ? VoltageSetpoint.ForBus(model, b.Id) : null;
            refById[b.Id] = setpoint ?? Analysis.VoltageLimits.NominalPu;
        }

        for (int i = 0; i < r.Vm.Length; i++)
        {
            if (!refById.TryGetValue(r.BusIds[i], out double vRef))
                throw new InvalidOperationException(
                    $"Watch-list: the solved result has bus {r.BusIds[i]}, which is not in model '{model.Name}'.");
            double dev = Math.Abs(r.Vm[i] - vRef);
            if (dev < opt.VoltageWatchBandPu) continue;
            double proximity = Math.Clamp(dev / opt.VoltageLimitBandPu, 0, 1);
            // A voltage excursion is serious but usually local: a fixed-moderate consequence, scaled a little
            // by how far out of band it already is.
            double consequence = Math.Clamp(VoltageConsequenceBase + VoltageConsequenceSlope * proximity, 0, 1);
            string nm = nameById.GetValueOrDefault(r.BusIds[i], $"bus {r.BusIds[i]}");
            bool low = r.Vm[i] < vRef;
            string against = vRef == Analysis.VoltageLimits.NominalPu ? "" : $" against its {vRef:F3} pu setpoint";
            yield return new WatchItem(WatchCategory.VoltageLimit, nm,
                $"{nm} at {r.Vm[i]:F3} pu ({(low ? "low" : "high")}{against})", consequence, proximity);
        }
    }

    // Boundary transfers vs their security limit.
    private static IEnumerable<WatchItem> Boundaries(IReadOnlyList<BoundaryFlow> flows)
    {
        foreach (BoundaryFlow b in flows)
        {
            if (b.LimitMw <= 0) continue;
            double proximity = Math.Clamp(b.LoadingPercent / 100.0, 0, 1);
            if (proximity < BoundaryWatchFraction) continue;
            double consequence = Math.Clamp(b.LimitMw / BoundaryConsequenceScaleMw, BoundaryConsequenceFloor, 1); // bigger boundary = bigger transfer at risk
            yield return new WatchItem(WatchCategory.BoundaryTransfer, b.Name,
                $"boundary {b.Name} at {b.LoadingPercent:F0}% of its {b.LimitMw:F0} MW limit", consequence, proximity);
        }
    }

    // The branch's MVA for thermal purposes: the HEAVIER of its two ends (R230, October 2026), exactly the
    // max(|S_from|, |S_to|) the solvers put in LineFlow.LoadingPercent and the N-1 table reports. This used to read
    // the From end only, so a branch whose receiving end is heavier (cable charging, a transformer feeding reactive
    // load) was under-ranked and its stated percentage disagreed with the N-1 table.
    private static double Mva(LineFlow lf)
    {
        double sFrom = Math.Sqrt(lf.PFromMw * lf.PFromMw + lf.QFromMvar * lf.QFromMvar);
        double sTo = Math.Sqrt(lf.PToMw * lf.PToMw + lf.QToMvar * lf.QToMvar);
        return Math.Max(sFrom, sTo);
    }
}
