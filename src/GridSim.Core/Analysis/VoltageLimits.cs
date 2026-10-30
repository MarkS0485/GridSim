namespace GridSim.Core.Analysis;

/// <summary>
/// The one definition of the bus-voltage bands the analysis modules judge a solved state against (R136,
/// October 2026). Before this the band was a bare literal in each module - 0.94/1.06 in the contingency screen,
/// +/-0.04 and +/-0.10 about 1.0 pu in the watch-list, 0.9/1.1 in the restoration checker - so the same state
/// could be secure in one module and violating in another, and an edit to one copy would not reach the rest.
/// <list type="bullet">
/// <item><b>Operational band</b> (<see cref="OperationalLowPu"/>..<see cref="OperationalHighPu"/>, 0.94-1.06 pu):
/// the generous operating band a control room holds the transmission system inside. The N-1 contingency screen
/// (violations and its stress score) uses it; the SCADA console's alarm band
/// (<c>Presentation.ScadaThresholds.VoltageBandLowPu/HighPu</c>) carries the same values.</item>
/// <item><b>Statutory band</b> (<see cref="StatutoryLowPu"/>..<see cref="StatutoryHighPu"/>, 0.90-1.10 pu, i.e.
/// +/-<see cref="StatutoryBandPu"/> about nominal): the outer limit. The restoration sequencer checks each
/// energisation step against it, and the watch-list ramps a voltage item's proximity to 1 at it.</item>
/// <item><b>Watch band</b> (+/-<see cref="WatchBandPu"/> about the reference): the early-warning deviation at
/// which the watch-list starts listing a bus, well inside the operational band.</item>
/// </list>
/// The values are written as literals (not derived, e.g. 1.0 - 0.1) so each is the exact double the modules
/// always used; <c>VoltageLimitsTests</c> pins that the bands are consistent with each other.
/// </summary>
public static class VoltageLimits
{
    /// <summary>Nominal voltage (pu): the reference a PQ bus's deviation is measured from.</summary>
    public const double NominalPu = 1.0;

    /// <summary>Operational band, low edge (pu).</summary>
    public const double OperationalLowPu = 0.94;

    /// <summary>Operational band, high edge (pu).</summary>
    public const double OperationalHighPu = 1.06;

    /// <summary>Statutory band, low edge (pu).</summary>
    public const double StatutoryLowPu = 0.90;

    /// <summary>Statutory band, high edge (pu).</summary>
    public const double StatutoryHighPu = 1.10;

    /// <summary>Half-width of the statutory band about nominal (pu).</summary>
    public const double StatutoryBandPu = 0.10;

    /// <summary>Early-warning deviation (pu) at which the watch-list lists a bus.</summary>
    public const double WatchBandPu = 0.04;

    // ---- Per-bus limits (F17, October 2026) ----------------------------------------------------------------------
    //
    // A bus may carry its own limits (Bus.VminPu / Bus.VmaxPu: MATPOWER VMIN / VMAX, or JSON vminPu / vmaxPu). They
    // are used only where a check is asked to (PowerFlowOptions.PerBusVoltageLimits; the CLI --bus-vlimits flag):
    // every MATPOWER file carries the columns, so using them by default would move existing verdicts. Where a bus
    // gives no limit on a side, the operational band applies on that side, so a model with no per-bus data gives
    // exactly the band check.

    /// <summary>True when any bus of <paramref name="model"/> carries a per-bus lower or upper limit.</summary>
    public static bool HasPerBusLimits(GridSim.Core.Model.GridModel model)
    {
        foreach (GridSim.Core.Model.Bus b in model.Buses)
            if (b.VminPu is not null || b.VmaxPu is not null) return true;
        return false;
    }

    /// <summary>The lower limit (pu) bus <paramref name="bus"/> is judged against: its own <c>VminPu</c>, else
    /// <see cref="OperationalLowPu"/>.</summary>
    public static double OperationalLowFor(GridSim.Core.Model.Bus bus) => bus.VminPu ?? OperationalLowPu;

    /// <summary>The upper limit (pu) bus <paramref name="bus"/> is judged against: its own <c>VmaxPu</c>, else
    /// <see cref="OperationalHighPu"/>.</summary>
    public static double OperationalHighFor(GridSim.Core.Model.Bus bus) => bus.VmaxPu ?? OperationalHighPu;

    /// <summary>
    /// True when every solved voltage is inside its limits: with <paramref name="perBus"/> false (the default
    /// everywhere) the operational band for every bus, exactly the historical
    /// <c>Vm.Min() &gt;= OperationalLowPu &amp;&amp; Vm.Max() &lt;= OperationalHighPu</c>; with it true each bus's own
    /// limits where it has them (F17). <paramref name="busIds"/>[i] names the bus of <paramref name="vm"/>[i].
    /// </summary>
    public static bool AllWithinOperational(GridSim.Core.Model.GridModel model, IReadOnlyList<int> busIds, double[] vm, bool perBus)
    {
        if (!perBus || !HasPerBusLimits(model))
            return vm.Min() >= OperationalLowPu && vm.Max() <= OperationalHighPu;
        var byId = new Dictionary<int, GridSim.Core.Model.Bus>(model.Buses.Count);
        foreach (GridSim.Core.Model.Bus b in model.Buses) byId.TryAdd(b.Id, b);
        for (int i = 0; i < vm.Length && i < busIds.Count; i++)
        {
            if (!byId.TryGetValue(busIds[i], out GridSim.Core.Model.Bus? bus))
                throw new ArgumentException($"result bus {busIds[i]} is not in the model.", nameof(busIds));
            if (vm[i] < OperationalLowFor(bus) || vm[i] > OperationalHighFor(bus)) return false;
        }
        return true;
    }
}
