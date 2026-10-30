namespace GridSim.Ui.Assets.Pages;

/// <summary>
/// The one per-frame bus lookup every ASSETS page uses (ASSETS fix S3, 2026-10-20): a table from bus id to the value
/// the solved result holds for that bus, or to the position of that bus in the result, built ONCE per refresh in
/// O(buses) and then read in O(1).
/// </summary>
/// <remarks>
/// <para>The rule is FIRST-WINS: when two entries of <c>PowerFlowResult.BusIds</c> carry the same id, the table keeps
/// the first entry's value (Dictionary.TryAdd does not overwrite). That is the rule every page except one already used
/// (the home page's voltage and angle tables, the generator phasor page's angle and voltage tables, the transformer
/// pages' result index, each built with TryAdd), and it is the answer the pages' remaining linear scans give too (each
/// stops at the first matching id). So for those pages this helper is a mechanical move of the same loop: the same
/// keys, the same values, the same answer for every id.</para>
/// <para>The one page it changes is the substation fleet (SubstationFleetModel.Refresh), whose voltage table was
/// LAST-WINS (an indexer assignment, <c>vmById[id] = vm</c>, so a later entry with the same id overwrote an earlier
/// one). Its Vm column, its Vm colour and its OUT OF BAND count therefore read a different bus's voltage from every
/// other page whenever two buses share an id. ASSETS plan section 4.3 S3, decided in section 8 decision 4.</para>
/// <para>Where the two rules differ: only where two buses share an id. Every bundled case has distinct bus ids (the
/// loaders refuse a repeated id: GR-147, the post-build structural check), so on every bundled case first-wins and
/// last-wins give the same table and nothing displayed changes; UiAssetFixesTests lists the repeated ids per bundled
/// case (none) and builds a case with a repeated id to show the old and new values exactly.</para>
/// <para>No bounds are checked beyond what the old loops did: a value array shorter than the id list throws
/// IndexOutOfRangeException at the first missing entry, as the old loops did (a malformed result is never shown).</para>
/// </remarks>
internal static class FrameLookup
{
    /// <summary>
    /// Bus id -> the value at the FIRST position of <paramref name="busIds"/> carrying that id (for example the solved
    /// voltage magnitude <c>r.Vm</c> or angle <c>r.VaDeg</c>, which are index-aligned with <c>r.BusIds</c>).
    /// </summary>
    public static Dictionary<int, double> ByBusId(IReadOnlyList<int> busIds, double[] values)
    {
        ArgumentNullException.ThrowIfNull(busIds);
        ArgumentNullException.ThrowIfNull(values);
        var byId = new Dictionary<int, double>(busIds.Count);
        for (int i = 0; i < busIds.Count; i++)
            byId.TryAdd(busIds[i], values[i]);   // TryAdd keeps the FIRST entry for a repeated id (S3: first-wins)
        return byId;
    }

    /// <summary>
    /// Bus id -> the FIRST position of <paramref name="busIds"/> carrying that id (the position the old linear loops
    /// stopped at).
    /// </summary>
    public static Dictionary<int, int> IndexByBusId(IReadOnlyList<int> busIds)
    {
        ArgumentNullException.ThrowIfNull(busIds);
        var idx = new Dictionary<int, int>(busIds.Count);
        for (int i = 0; i < busIds.Count; i++)
            idx.TryAdd(busIds[i], i);            // TryAdd keeps the FIRST position for a repeated id (S3: first-wins)
        return idx;
    }
}
