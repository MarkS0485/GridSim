using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Pages;

// UI programme ASSETS A2: moved here from src/GridSim.Wpf/Views/Assets/TransformerPages.cs (commit 6e729a66), line for
// line, with brushes as colour tokens and commands as actions (see AssetPages.cs for the full list of what changed;
// nothing else did).
//
// ---------------------------------------------------------------------------
// Transformers & tap changers.
//
// Transformers and OLTCs both live on the branch list (a transformer is a
// Branch with IsTransformer; an Oltc references it by Branch.Name), so the
// fleet pages read straight off the live model and the solved LineFlows. The
// unit page mixes that live electrical state with a set of UI-first static
// asset-management sections (thermal, DGA, protection, event log) lifted from
// the v1 deep transformer page  -  they're grouped and labelled so a real
// monitoring dataset can drop in behind them later.
// ---------------------------------------------------------------------------

/// <summary>Shared helpers for reading transformer state off a solved frame.</summary>
internal static class TransformerInfo
{
    /// <summary>Bus voltage magnitude (p.u.) for a bus id, or null if not solved.</summary>
    public static double? Vpu(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++)
            if (r.BusIds[i] == busId) return r.Vm[i];
        return null;
    }

    /// <summary>The solved flow for a branch, read by its INDEX (LineFlows is index-aligned with Branches, as
    /// every other view reads it). It used to match LineFlow.Name, so on gb-full (58 duplicated branch names) a
    /// transformer showed its first namesake's loading and flow (R055).</summary>
    public static LineFlow? Flow(PowerFlowResult r, int branchIndex) => AssetLookup.FlowAt(r, branchIndex);

    /// <summary>"400->132 kV" from the endpoint base voltages, highest first.</summary>
    public static string Band(GridModel m, Branch b)
    {
        double hi = KvOf(m, b.From), lo = KvOf(m, b.To);
        if (lo > hi) (hi, lo) = (lo, hi);
        if (hi <= 0 && lo <= 0) return " - ";
        return $"{hi:F0}->{lo:F0} kV";
    }

    public static double KvOf(GridModel m, int busId)
    {
        Bus? bus = m.Buses.FirstOrDefault(x => x.Id == busId);
        return bus?.BaseKv ?? 0;
    }

    public static string NameOf(GridModel m, int busId)
    {
        Bus? bus = m.Buses.FirstOrDefault(x => x.Id == busId);
        return bus?.Name ?? $"bus {busId}";
    }

    // ---- per-refresh lookup tables for the FLEET pages (R112, October 2026) ----
    // The fleet pages used to call Band / Vpu once or twice per row, and each call scanned the whole bus list
    // (Buses.FirstOrDefault / a loop over BusIds). On gb-full that is 2,620 transformers x 2 scans of 3,539 buses
    // every frame on the UI thread. These build an id -> bus (and id -> result index) table ONCE per refresh, in
    // O(buses), and the overloads below read it in O(1). They keep the FIRST bus with a given id, exactly as
    // FirstOrDefault and the old loop did, so every value shown is unchanged.

    /// <summary>Bus id -> the first <see cref="Bus"/> carrying that id (the same bus FirstOrDefault returns).</summary>
    public static Dictionary<int, Bus> BusById(GridModel m)
    {
        var byId = new Dictionary<int, Bus>(m.Buses.Count);
        foreach (Bus b in m.Buses) byId.TryAdd(b.Id, b);   // TryAdd keeps the first, like FirstOrDefault
        return byId;
    }

    /// <summary>Bus id -> the first index in <see cref="PowerFlowResult.BusIds"/> carrying it (the index the old
    /// linear <see cref="Vpu(PowerFlowResult, int)"/> loop stopped at).</summary>
    /// <remarks>ASSETS fix S3 (2026-10-20): the table is the one shared per-frame lookup (FrameLookup.IndexByBusId), the
    /// same first-wins TryAdd loop this method held inline, moved unchanged, so every value shown is the same.</remarks>
    public static Dictionary<int, int> ResultIndexById(PowerFlowResult r) => FrameLookup.IndexByBusId(r.BusIds);

    /// <summary><see cref="Vpu(PowerFlowResult, int)"/> through a prebuilt index table.</summary>
    public static double? Vpu(PowerFlowResult r, IReadOnlyDictionary<int, int> idx, int busId) =>
        idx.TryGetValue(busId, out int i) ? r.Vm[i] : null;

    /// <summary><see cref="Band(GridModel, Branch)"/> through a prebuilt bus table.</summary>
    public static string Band(IReadOnlyDictionary<int, Bus> busById, Branch b)
    {
        double hi = KvOf(busById, b.From), lo = KvOf(busById, b.To);
        if (lo > hi) (hi, lo) = (lo, hi);
        if (hi <= 0 && lo <= 0) return " - ";
        return $"{hi:F0}->{lo:F0} kV";
    }

    /// <summary><see cref="KvOf(GridModel, int)"/> through a prebuilt bus table.</summary>
    public static double KvOf(IReadOnlyDictionary<int, Bus> busById, int busId) =>
        busById.TryGetValue(busId, out Bus? bus) ? bus.BaseKv : 0;

    /// <summary><see cref="NameOf(GridModel, int)"/> through a prebuilt bus table.</summary>
    public static string NameOf(IReadOnlyDictionary<int, Bus> busById, int busId) =>
        busById.TryGetValue(busId, out Bus? bus) && bus.Name is not null ? bus.Name : $"bus {busId}";

    /// <summary>
    /// Branch index -> the OLTC record the solver applies to that branch, from the solver's own mapping
    /// (<see cref="SteadyStateSolver.MapOltcRecordsToBranches"/>: the branch carrying the record's name with its
    /// controlled bus at one end, else the first branch of the name). Every OLTC view reads this, so a page shows the tap
    /// changer on exactly the transformer the solver regulates, never on a namesake. When two records map to one
    /// branch the first is kept (model order).
    /// </summary>
    public static Dictionary<int, Oltc> OltcByBranch(GridModel m)
    {
        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(m);
        var byBranch = new Dictionary<int, Oltc>(map.Length);
        for (int k = 0; k < map.Length; k++)
            if (map[k] >= 0) byBranch.TryAdd(map[k], m.Oltcs[k]);
        return byBranch;
    }
}

/// <summary>
/// The PROTECTION panel's real rows: the case's authored protective devices (GridModel.Protections, from the case's
/// protections.json) that apply to one transformer, with their settings exactly as Core holds them. ASSETS fix S7.
/// </summary>
/// <remarks>
/// <para>How Core links a device to the network (ProtectionInstance; checked at load by ProtectionModel.ValidateAgainst):
/// every device MONITORS one bus (MonitorBus), and a recloser also OPENS one branch, named by TargetBranch (a name only
/// one branch may carry). So a device applies to a transformer when its monitored bus is one of the transformer's two
/// terminal buses (it watches the voltage, frequency or loading there), or when its target branch is this
/// transformer's name (operating it opens this unit). Nothing else links a device to a transformer.</para>
/// <para>For each applicable device, in the case's order, the rows are:
/// <list type="bullet">
/// <item>the device: its name, its class name, and how it applies ("OPENS THIS UNIT", "HV BUS" or "LV BUS");</item>
/// <item>"Monitored bus": the bus name and id, and which end of this transformer it is ("HV END", "LV END", or
/// "OTHER BUS" for a recloser that watches a bus elsewhere);</item>
/// <item>"Capacity": the MW the device trips or sheds ("AUTHORED");</item>
/// <item>"Target branch", for a device that names one ("THIS UNIT" or "OTHER BRANCH");</item>
/// <item>"Mode": the class's mode text, when it has one ("AUTHORED");</item>
/// <item>one row per class parameter, in the class's own order: the parameter key as the case writes it, its value with
/// its unit, and the parameter's provenance tag (Recorded, DerivedFromRecorded, CuratedPlaceholder, ...), as
/// AssetClass.ProvenanceFor gives it;</item>
/// <item>if the device's class is missing from the library (the loader refuses that, so only a hand-built model can
/// carry it): one "Class" row saying so, status "MISSING", in the alarm colour.</item>
/// </list>
/// Numbers are written as the shortest text that reads back as the same double, invariant culture (the "R" format, the
/// case editor's own number format, JsonLiteral.FormatNumber), so the panel shows exactly the value Core holds whatever
/// the machine culture.</para>
/// </remarks>
internal static class TransformerProtection
{
    /// <summary>
    /// The unit of each protection-class parameter, by key: exactly the keys a protection class may carry
    /// (AssetClassSupport.AcceptedKeys(AssetKind.Protection), pinned equal by UiAssetFixesTests), each read from the
    /// parameter's definition (the unit its name carries). A loading fraction is a multiple of the branch's RateA, a
    /// RoCoF uplift is a plain multiplier.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GridSim.Core.Assets.AssetParams.RocofTripHzPerS] = "Hz/s",
        [GridSim.Core.Assets.AssetParams.RocofTripConfirmS] = "s",
        [GridSim.Core.Assets.AssetParams.DfDtTripHzPerS] = "Hz/s",
        [GridSim.Core.Assets.AssetParams.DfDtTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.LocalRocofUpliftFactor] = "x",
        [GridSim.Core.Assets.AssetParams.UfTripHz] = "Hz",
        [GridSim.Core.Assets.AssetParams.UfTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.OfTripHz] = "Hz",
        [GridSim.Core.Assets.AssetParams.OfTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.UvTripPu] = "pu",
        [GridSim.Core.Assets.AssetParams.UvTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.OvTripPu] = "pu",
        [GridSim.Core.Assets.AssetParams.OvTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.LvrtVoltagePu] = "pu",
        [GridSim.Core.Assets.AssetParams.VectorShiftTripDeg] = "deg",
        [GridSim.Core.Assets.AssetParams.DetectionLatencyS] = "s",
        [GridSim.Core.Assets.AssetParams.OverloadTripFraction] = "x RateA",
        [GridSim.Core.Assets.AssetParams.OverloadTripDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.LfddStageHz] = "Hz",
        [GridSim.Core.Assets.AssetParams.LfddBlockMw] = "MW",
        [GridSim.Core.Assets.AssetParams.LfddDwellS] = "s",
        [GridSim.Core.Assets.AssetParams.AutoRecloseDelayS] = "s",
        [GridSim.Core.Assets.AssetParams.SettingsNominalHz] = "Hz",
    };

    /// <summary>The status a row carries when the device's class is missing from the library.</summary>
    internal const string MissingStatus = "MISSING";

    /// <summary>A number as Core holds it: the shortest round-trip text, invariant culture (JsonLiteral.FormatNumber's
    /// format; "NaN" or "Infinity" are written as such rather than refused, so a hand-built model cannot crash the
    /// page).</summary>
    internal static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A value with its unit ("0.125 Hz/s"); a key with no known unit says so rather than guess one.</summary>
    internal static string WithUnit(string key, double v) =>
        Units.TryGetValue(key, out string? unit) ? $"{Num(v)} {unit}" : $"{Num(v)} (unit not known)";

    /// <summary>The status chip colour: the alarm colour for a missing class, grey otherwise (the panel reports
    /// settings, not a live relay state, so no row claims "OK" or "healthy").</summary>
    internal static UiColour ChipOf(string status) =>
        status == MissingStatus ? ColourRamps.Loading(1.2) : DashboardColours.Gray;

    /// <summary>
    /// The rows for transformer <paramref name="b"/>, whose higher-kV end is bus <paramref name="hv"/> and lower-kV end
    /// bus <paramref name="lv"/> (as the page picks them); empty when the case has no protections or none applies.
    /// </summary>
    public static List<(string Label, string Value, string Status)> Rows(GridModel m, Branch b, int hv, int lv)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(b);
        var rows = new List<(string Label, string Value, string Status)>();
        if (m.Protections is not { } prot) return rows;

        foreach (GridSim.Core.Assets.Protection.ProtectionInstance d in prot.Instances)
        {
            bool opensIt = d.TargetBranch is not null && b.Name is not null && string.Equals(d.TargetBranch, b.Name, StringComparison.Ordinal);
            bool atHv = d.MonitorBus == hv, atLv = d.MonitorBus == lv;
            if (!opensIt && !atHv && !atLv) continue;   // this device is not linked to this transformer

            rows.Add((d.Name, d.ClassName, opensIt ? "OPENS THIS UNIT" : atHv ? "HV BUS" : "LV BUS"));
            rows.Add(("Monitored bus", $"{TransformerInfo.NameOf(m, d.MonitorBus)} (bus {d.MonitorBus})",
                atHv ? "HV END" : atLv ? "LV END" : "OTHER BUS"));
            rows.Add(("Capacity", $"{Num(d.CapacityMw)} MW", "AUTHORED"));
            if (d.TargetBranch is not null)
                rows.Add(("Target branch", d.TargetBranch, opensIt ? "THIS UNIT" : "OTHER BRANCH"));

            if (!prot.Library.Contains(d.ClassName))
            {
                rows.Add(("Class", $"{d.ClassName} is not in the class library", MissingStatus));
                continue;
            }
            GridSim.Core.Assets.AssetClass cls = prot.Library.Get(d.ClassName);
            if (cls.Mode.Length > 0) rows.Add(("Mode", cls.Mode, "AUTHORED"));
            foreach ((string key, double value) in cls.Params)
                rows.Add((key, WithUnit(key, value), cls.ProvenanceFor(key).Tag.ToString()));
        }
        return rows;
    }
}

// ===========================================================================
// Fleet page
// ===========================================================================

/// <summary>One transformer's row on the fleet page.</summary>
public class TransformerFleetRowModel : ObservableModel
{
    /// <summary>The transformer's branch index in the model: the row's identity (branch names repeat).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Band { get; init; } = "";
    public string Rating { get; init; } = "";
    /// <summary>What a click on the row does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };

    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _tap = ""; public string Tap { get => _tap; set => Set(ref _tap, value); }
    private string _oltc = ""; public string Oltc { get => _oltc; set => Set(ref _oltc, value); }
    private bool _hasOltc; public bool HasOltc { get => _hasOltc; set => Set(ref _hasOltc, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Was the WPF Bar brush (default Brushes.Gray).
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
    private double _loadingFrac; public double LoadingFrac { get => _loadingFrac; set => _loadingFrac = value; }
}

/// <summary>The transformer fleet page: every grid/GSP transformer, ordered by live loading.</summary>
/// <typeparam name="TRow">The row type the front end shows.</typeparam>
public class TransformerFleetModel<TRow>(AssetExplorerModel explorer) : AssetPageModel where TRow : TransformerFleetRowModel, new()
{
    public const double BarTrack = 240.0;
    private const int RowCap = 200;   // big GDA models carry thousands of transformers

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "TRANSFORMERS";
    public override string Sub => "GSP and grid transformers by voltage band  -  loading, rating and OLTC state per unit";

    public ObservableCollection<TRow> Rows { get; } = new();
    private readonly Dictionary<int, TRow> _byIndex = new();   // keyed by branch index (R055)

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _bands = " - "; public string Bands { get => _bands; set => Set(ref _bands, value); }
    private string _meanLoading = " - "; public string MeanLoading { get => _meanLoading; set => Set(ref _meanLoading, value); }
    private string _oltcFitted = " - "; public string OltcFitted { get => _oltcFitted; set => Set(ref _oltcFitted, value); }
    private string _capNote = ""; public string CapNote { get => _capNote; set => Set(ref _capNote, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // OLTCs indexed by the BRANCH the solver drives with each (SteadyStateSolver.MapOltcRecordsToBranches: by name AND
        // controlled-bus end, then the first of the name). They were indexed by transformer name, so every namesake of a
        // regulated transformer was tagged OLTC-fitted, including parallel units the OLTC does not act on.
        Dictionary<int, Oltc> oltcByBranch = TransformerInfo.OltcByBranch(m);

        // Bus id -> bus, built once for this refresh so each row's voltage band is an O(1) lookup (R112).
        Dictionary<int, Bus> busById = TransformerInfo.BusById(m);

        // Build a scratch list of (branch, index, loading) so we can order by loading desc. The branch index is
        // the identity: it reads the solved flow (LineFlows is index-aligned) and keys the row.
        List<(Branch b, int idx, double frac)> txs = new();
        var bands = new SortedSet<string>(Comparer<string>.Create((a, x) => string.CompareOrdinal(x, a)));
        double loadSum = 0; int loadN = 0;

        for (int bi = 0; bi < m.Branches.Count; bi++)
        {
            Branch b = m.Branches[bi];
            if (!b.IsTransformer) continue;
            LineFlow? f = TransformerInfo.Flow(r, bi);
            double frac = f is { RatingMva: > 0 } ? f.LoadingPercent / 100.0 : 0;
            if (f is { RatingMva: > 0 }) { loadSum += f.LoadingPercent; loadN++; }
            txs.Add((b, bi, frac));
            string band = TransformerInfo.Band(busById, b);
            if (band != " - ") bands.Add(band);
        }

        txs.Sort((a, x) => x.frac.CompareTo(a.frac));

        // Prune rows that fell out of the top-RowCap (or vanished on a model
        // swap): unrefreshed rows kept displaying their LAST loading/flow/tap
        // as if live. A row whose index now holds a differently named branch is rebuilt too.
        var keep = new Dictionary<int, string>();
        for (int i = 0; i < txs.Count && i < RowCap; i++)
            keep[txs[i].idx] = txs[i].b.Name ?? $"TX {txs[i].b.From}-{txs[i].b.To}";
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (keep.TryGetValue(Rows[i].Index, out string? nm) && nm == Rows[i].Name) continue;
            _byIndex.Remove(Rows[i].Index);
            Rows.RemoveAt(i);
        }

        int shown = 0;
        foreach ((Branch b, int idx, double frac) in txs)
        {
            if (shown >= RowCap) break;
            shown++;
            string name = b.Name ?? $"TX {b.From}-{b.To}";
            LineFlow? f = TransformerInfo.Flow(r, idx);
            oltcByBranch.TryGetValue(idx, out Oltc? oltc);

            if (!_byIndex.TryGetValue(idx, out TRow? row))
            {
                Branch branch = b;
                row = new TRow
                {
                    Index = idx,
                    Name = name,
                    Band = TransformerInfo.Band(busById, b),
                    Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0} MVA" : " - ",
                    OnOpen = () => explorer.OpenTransformer(idx, branch),
                };
                _byIndex[idx] = row;
                Rows.Add(row);
            }

            // Rows are reused but the live order changes; keep the collection in loading order.
            int target = shown - 1;
            int current = Rows.IndexOf(row);
            if (current != target && current >= 0) Rows.Move(current, target);

            row.Loading = f is { RatingMva: > 0 } ? $"{f.LoadingPercent:F0}%" : " - ";
            row.LoadingFrac = frac;
            row.BarWidth = Math.Min(1.0, frac) * BarTrack;
            row.BarColour = ColourRamps.Loading(frac);
            row.Flow = f is not null ? $"{f.PFromMw:+#,0;-#,0;0} MW" : " - ";
            row.Tap = $"{b.TapRatio:F3}";
            row.HasOltc = oltc is not null;
            row.Oltc = oltc is not null ? "OLTC" : "";
        }

        Count = txs.Count.ToString();
        Bands = bands.Count > 0 ? string.Join(" / ", bands.Take(3).Select(s => s.Replace("->", "/").Replace(" kV", ""))) + " kV" : " - ";
        MeanLoading = loadN > 0 ? $"{loadSum / loadN:F0}%" : " - ";
        // ASSETS fix S12 (2026-10-20, Mark approved fixing the quirks as their own slices): "OLTC FITTED" counts the
        // TRANSFORMERS that have at least one OLTC record mapped to them, by the solver's own rule
        // (SteadyStateSolver.MapOltcRecordsToBranches, read through oltcByBranch above, the same table that tags each
        // row "OLTC"). Before this fix it counted OLTC RECORDS (m.Oltcs.Count), which overstated the tile where two
        // records map to one transformer, or where a record maps to no branch at all (or to a branch that is not a
        // transformer), and could even read more fitted than there are transformers. Every bundled case maps each record
        // to its own transformer, so their tiles do not change; the difference shows only on such hand-built cases.
        // txs holds every transformer branch (not only the capped rows), so this counts the whole fleet.
        int fitted = 0;
        foreach ((Branch b, int idx, double frac) tx in txs)
            if (oltcByBranch.ContainsKey(tx.idx)) fitted++;
        OltcFitted = $"{fitted} of {txs.Count}";
        CapNote = txs.Count > RowCap ? $"showing {RowCap} of {txs.Count}  -  heaviest loaded first" : "";
    }
}

// ===========================================================================
// Unit page
// ===========================================================================

/// <summary>A labelled measurement row on the unit page  -  name, value, status chip.</summary>
public class TransformerReadingModel : ObservableModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>The status chip's colour (was the WPF <c>Chip</c> brush).</summary>
    public UiColour ChipColour { get; init; } = DashboardColours.Gray;
}

/// <summary>One line in the unit's event log.</summary>
public class TransformerEventModel : ObservableModel
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>The status chip's colour (was the WPF <c>Chip</c> brush).</summary>
    public UiColour ChipColour { get; init; } = DashboardColours.Gray;
}

/// <summary>
/// One transformer's dashboard: live electrical state and OLTC mechanics off the
/// solved frame, plus the asset-management sections (thermal, DGA, protection,
/// event log) the v1 deep page carried. Those last four are static demo content
/// today  -  sensible nameplate values with alarm context  -  wired so a monitoring
/// feed can replace them without touching the layout. Since ASSETS fix S7 (2026-10-20) the PROTECTION section is fed
/// from the case itself when the case has protection settings for this transformer (see
/// <see cref="TransformerProtection"/> and <see cref="ProtectionIsDemo"/>); otherwise it is the demo rows as before.
/// </summary>
/// <remarks>The transformer is identified by its branch INDEX, confirmed by its ends and model name
/// (<see cref="AssetLookup.FindBranch"/>); name lookup opened, and read the flow of, the first namesake (R055).</remarks>
/// <typeparam name="TReading">The measurement row type the front end shows.</typeparam>
/// <typeparam name="TEvent">The event-log row type the front end shows.</typeparam>
public class TransformerDetailModel<TReading, TEvent>(int index, int fromBus, int toBus, string? branchName, AssetExplorerModel explorer)
    : AssetPageModel
    where TReading : TransformerReadingModel, new()
    where TEvent : TransformerEventModel, new()
{
    private const int HistoryLength = 96;

    // Display name (page title) - the same label the fleet row shows. The OLTC is found by branch index, not by this.
    private readonly string name = branchName ?? $"TX {fromBus}-{toBus}";

    public override string Eyebrow => "TRANSFORMER";
    public override string Title => name;
    public override string Sub => _band;
    private string _band = "";

    // Click-through to the HV/LV endpoint substations and the bus the OLTC regulates (were the WPF OpenHv, OpenLv and
    // OpenOltcBus commands, whose parameters bind HvBusId, LvBusId and OltcBusId).
    public void OpenHvSubstation(int id) => explorer.OpenSubstation(id);
    public void OpenLvSubstation(int id) => explorer.OpenSubstation(id);
    public void OpenOltcBusSubstation(int id) => explorer.OpenSubstation(id);
    private int _hvBusId; public int HvBusId { get => _hvBusId; private set => Set(ref _hvBusId, value); }
    private int _lvBusId; public int LvBusId { get => _lvBusId; private set => Set(ref _lvBusId, value); }
    private int _oltcBusId; public int OltcBusId { get => _oltcBusId; private set => Set(ref _oltcBusId, value); }

    // --- live electrical ----------------------------------------------------
    private string _loading = " - "; public string Loading { get => _loading; set => Set(ref _loading, value); }
    // Was the WPF LoadingBrush (default Brushes.Gray).
    private UiColour _loadingColour = DashboardColours.Gray; public UiColour LoadingColour { get => _loadingColour; set => Set(ref _loadingColour, value); }
    private string _flowFrom = " - "; public string FlowFrom { get => _flowFrom; set => Set(ref _flowFrom, value); }
    private string _flowTo = " - "; public string FlowTo { get => _flowTo; set => Set(ref _flowTo, value); }
    private string _rating = " - "; public string Rating { get => _rating; set => Set(ref _rating, value); }
    private string _tap = " - "; public string Tap { get => _tap; set => Set(ref _tap, value); }

    // --- dials (doubles so a real thermal feed drops straight in) -----------
    // LoadingPct is the live solve; the two temps are the static demo estimates
    // bound as numbers now, ready for a monitoring source.
    private double _loadingPct; public double LoadingPct { get => _loadingPct; set => Set(ref _loadingPct, value); }
    private double _topOilC = 58; public double TopOilC { get => _topOilC; set => Set(ref _topOilC, value); }
    private double _hotSpotC = 71; public double HotSpotC { get => _hotSpotC; set => Set(ref _hotSpotC, value); }

    // --- endpoint buses -----------------------------------------------------
    // Per-side: bus name, base kV, solved voltage in pu and in actual kV, plus
    // the reactive flow through the unit at each terminal.
    private string _hvBus = " - "; public string HvBus { get => _hvBus; set => Set(ref _hvBus, value); }
    private string _hvKv = " - "; public string HvKv { get => _hvKv; set => Set(ref _hvKv, value); }
    private string _hvVm = " - "; public string HvVm { get => _hvVm; set => Set(ref _hvVm, value); }
    private string _hvKvLive = " - "; public string HvKvLive { get => _hvKvLive; set => Set(ref _hvKvLive, value); }
    private string _lvBus = " - "; public string LvBus { get => _lvBus; set => Set(ref _lvBus, value); }
    private string _lvKv = " - "; public string LvKv { get => _lvKv; set => Set(ref _lvKv, value); }
    private string _lvVm = " - "; public string LvVm { get => _lvVm; set => Set(ref _lvVm, value); }
    private string _lvKvLive = " - "; public string LvKvLive { get => _lvKvLive; set => Set(ref _lvKvLive, value); }
    private string _qHv = " - "; public string Qhv { get => _qHv; set => Set(ref _qHv, value); }
    private string _qLv = " - "; public string Qlv { get => _qLv; set => Set(ref _qLv, value); }

    // --- OLTC (only when fitted) -------------------------------------------
    private bool _hasOltc; public bool HasOltc { get => _hasOltc; set => Set(ref _hasOltc, value); }
    private string _oltcBus = " - "; public string OltcBus { get => _oltcBus; set => Set(ref _oltcBus, value); }
    private string _oltcTarget = " - "; public string OltcTarget { get => _oltcTarget; set => Set(ref _oltcTarget, value); }
    private string _oltcLive = " - "; public string OltcLive { get => _oltcLive; set => Set(ref _oltcLive, value); }
    private string _oltcRange = " - "; public string OltcRange { get => _oltcRange; set => Set(ref _oltcRange, value); }
    private string _oltcStep = " - "; public string OltcStep { get => _oltcStep; set => Set(ref _oltcStep, value); }
    private string _oltcDeadband = " - "; public string OltcDeadband { get => _oltcDeadband; set => Set(ref _oltcDeadband, value); }
    private string _oltcState = " - "; public string OltcState { get => _oltcState; set => Set(ref _oltcState, value); }
    // Was the WPF OltcBrush (default Brushes.Gray).
    private UiColour _oltcColour = DashboardColours.Gray; public UiColour OltcColour { get => _oltcColour; set => Set(ref _oltcColour, value); }

    // --- schematic data (fed to TransformerSchematic visual) -----------------
    private double _hvKvNom; public double HvKvNom { get => _hvKvNom; set => Set(ref _hvKvNom, value); }
    private double _lvKvNom; public double LvKvNom { get => _lvKvNom; set => Set(ref _lvKvNom, value); }
    private double _hvKvLiveNum; public double HvKvLiveNum { get => _hvKvLiveNum; set => Set(ref _hvKvLiveNum, value); }
    private double _lvKvLiveNum; public double LvKvLiveNum { get => _lvKvLiveNum; set => Set(ref _lvKvLiveNum, value); }
    private double _flowMw; public double FlowMw { get => _flowMw; set => Set(ref _flowMw, value); }
    private double _ratingNum; public double RatingNum { get => _ratingNum; set => Set(ref _ratingNum, value); }
    private double _tapNum; public double TapNum { get => _tapNum; set => Set(ref _tapNum, value); }
    private double _tapMinNum; public double TapMinNum { get => _tapMinNum; set => Set(ref _tapMinNum, value); }
    private double _tapMaxNum; public double TapMaxNum { get => _tapMaxNum; set => Set(ref _tapMaxNum, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    // --- static asset-management sections (UI-first demo content) -----------
    public ObservableCollection<TReading> Thermal { get; } = new();
    public ObservableCollection<TReading> Dga { get; } = new();
    public ObservableCollection<TReading> Protection { get; } = new();
    public ObservableCollection<TEvent> Events { get; } = new();

    private bool _seeded;

    private static UiColour Chip(string status) => status switch
    {
        "ALARM" or "TRIP" => ColourRamps.Loading(1.2),
        "WARN" or "CAUTION" => ColourRamps.Loading(0.75),
        _ => ColourRamps.Loading(0.0),
    };

    /// <summary>A measurement row (was the WPF <c>new TransformerReading(label, value, status, chip)</c>).</summary>
    private static TReading Reading(string label, string value, string status, UiColour chip) =>
        new() { Label = label, Value = value, Status = status, ChipColour = chip };

    /// <summary>An event-log row (was the WPF <c>new TransformerEvent(time, text, chip)</c>).</summary>
    private static TEvent Event(string time, string text, UiColour chip) =>
        new() { Time = time, Text = text, ChipColour = chip };

    /// <summary>Plausible nameplate / monitoring values  -  see class summary; replace with a feed later.</summary>
    private void SeedStatic()
    {
        Thermal.Add(Reading("Top-oil temperature", "58  degC", "OK", Chip("OK")));
        Thermal.Add(Reading("Winding hot-spot", "71  degC", "OK", Chip("OK")));
        Thermal.Add(Reading("Hot-spot alarm / trip", "98 / 110  degC", "OK", Chip("OK")));
        Thermal.Add(Reading("Cooling stage", "ONAN -> ONAF 1", "OK", Chip("OK")));
        Thermal.Add(Reading("Ambient", "16  degC", "OK", Chip("OK")));

        // Dissolved gas analysis  -  ppm vs typical IEEE C57.104 condition-1 caps.
        Dga.Add(Reading("Hydrogen (H2)", "42 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Carbon monoxide (CO)", "310 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Carbon dioxide (CO2)", "2 600 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Methane (CH)", "28 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Ethylene (C2H)", "19 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Ethane (C2H)", "14 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Acetylene (C2H2)", "1 ppm", "OK", Chip("OK")));
        Dga.Add(Reading("Moisture (H2O)", "9 ppm", "CAUTION", Chip("CAUTION")));

        AddDemoProtection();

        Events.Add(Event("06:14", "OLTC tap raise  -  step +1", Chip("OK")));
        Events.Add(Event("04:02", "Cooling stage ONAF 1 engaged", Chip("OK")));
        Events.Add(Event("Yesterday", "Routine DGA sample logged", Chip("OK")));
        Events.Add(Event("3 days ago", "Moisture trend flagged for review", Chip("CAUTION")));
        Events.Add(Event("Last week", "Annual thermographic survey  -  no exceptions", Chip("OK")));
    }

    /// <summary>The PROTECTION panel's static demo rows, exactly as the page has always shown them (the panel then
    /// carries its STATIC DEMO tag). Moved out of <see cref="SeedStatic"/> unchanged so the page can put them back when a
    /// model with no protection for this transformer replaces one that had (ASSETS fix S7).</summary>
    private void AddDemoProtection()
    {
        Protection.Add(Reading("Buchholz relay", "healthy", "OK", Chip("OK")));
        Protection.Add(Reading("Differential (87T)", "healthy", "OK", Chip("OK")));
        Protection.Add(Reading("Overcurrent (51)", "healthy", "OK", Chip("OK")));
        Protection.Add(Reading("Restricted earth fault (64)", "healthy", "OK", Chip("OK")));
        Protection.Add(Reading("Pressure relief (63)", "healthy", "OK", Chip("OK")));
    }

    // --- PROTECTION panel: the case's own settings when it has any for this transformer (ASSETS fix S7) ----------
    // ASSETS fix S7 (2026-10-20, Mark approved; plan section 7 Q4, recommendation (b): "replace with real data where the
    // case has it, keep the tag otherwise"). Before this fix the PROTECTION panel always showed the five fixed demo rows
    // above, tagged STATIC DEMO, even when the case carries authored protections (protections.json, GridModel.Protections).
    // Now, on every refresh:
    //   - when the case places one or more protective devices that apply to THIS transformer (TransformerProtection.Rows
    //     says which: a device whose monitored bus is one of the transformer's two terminal buses, or a recloser whose
    //     target branch is this transformer), the panel lists those devices and their settings exactly as Core holds
    //     them, and ProtectionIsDemo is false, so the front ends hide the STATIC DEMO tag;
    //   - otherwise the panel shows the five demo rows exactly as before, and ProtectionIsDemo is true (the tag shows).
    // The rows are rebuilt only when what they say changes (a model swap), so their objects are kept across frames.
    // While the transformer is missing from the model ("not in the current model ... stale") the panel is left as it is,
    // like every other value on the page.

    private bool _protectionIsDemo = true;

    /// <summary>
    /// True while the PROTECTION panel shows the static demo rows (the front ends show its STATIC DEMO tag); false while
    /// it shows the case's own protection settings for this transformer (no tag: the data is real). ASSETS fix S7.
    /// </summary>
    public bool ProtectionIsDemo { get => _protectionIsDemo; private set => Set(ref _protectionIsDemo, value); }

    /// <summary>The real rows the panel shows now (label, value, status), or null while it shows the demo rows.</summary>
    private List<(string Label, string Value, string Status)>? _protectionShown;

    /// <summary>Show this transformer's protection settings from the model, or the demo rows when it has none
    /// (ASSETS fix S7; see the comment above).</summary>
    private void ShowProtection(GridModel m, Branch b, int hv, int lv)
    {
        List<(string Label, string Value, string Status)> rows = TransformerProtection.Rows(m, b, hv, lv);
        if (rows.Count == 0)
        {
            // No protection applies: the demo rows, exactly as before (put back only if real rows replaced them).
            if (_protectionShown is not null)
            {
                Protection.Clear();
                AddDemoProtection();
                _protectionShown = null;
            }
            ProtectionIsDemo = true;
            return;
        }
        // Real rows: rebuilt only when they differ from what is shown (same text in the same order keeps the objects).
        if (_protectionShown is null || !_protectionShown.SequenceEqual(rows))
        {
            Protection.Clear();
            foreach ((string label, string value, string status) in rows)
                Protection.Add(Reading(label, value, status, TransformerProtection.ChipOf(status)));
            _protectionShown = rows;
        }
        ProtectionIsDemo = false;
    }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_seeded) { SeedStatic(); _seeded = true; }

        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;
        (Branch Branch, int Index)? hit = AssetLookup.FindBranch(m, index, fromBus, toBus, branchName);
        if (hit is not { Branch.IsTransformer: true })
        {
            // Say so plainly rather than keep showing the last frame's numbers as if live.
            _band = "this transformer is not in the current model (model changed?)  -  values below are stale";
            Raise(nameof(Sub));
            return;
        }
        Branch b = hit.Value.Branch;

        _band = TransformerInfo.Band(m, b);
        Raise(nameof(Sub));

        LineFlow? f = TransformerInfo.Flow(r, hit.Value.Index);
        double frac = f is { RatingMva: > 0 } ? f.LoadingPercent / 100.0 : 0;
        Loading = f is { RatingMva: > 0 } ? $"{f.LoadingPercent:F0}%" : " - ";
        LoadingPct = f is { RatingMva: > 0 } ? f.LoadingPercent : 0;
        LoadingColour = ColourRamps.Loading(frac);
        FlowFrom = f is not null ? $"{f.PFromMw:+#,0;-#,0;0}" : " - ";
        FlowTo = f is not null ? $"{f.PToMw:+#,0;-#,0;0}" : " - ";
        Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0}" : " - ";
        RatingNum = b.RateAMva;
        Tap = $"{b.TapRatio:F3}";
        TapNum = b.TapRatio;
        FlowMw = f is not null ? Math.Abs(f.PFromMw) : 0;

        // Endpoint buses  -  HV is the higher-kV end. The flow's From/To may run the
        // other way to HV/LV, so we pick the reactive figure that matches each side.
        int hv = b.From, lv = b.To;
        bool fromIsHv = TransformerInfo.KvOf(m, b.From) >= TransformerInfo.KvOf(m, b.To);
        if (!fromIsHv) (hv, lv) = (lv, hv);
        HvBusId = hv; LvBusId = lv;
        double hvKv = TransformerInfo.KvOf(m, hv), lvKv = TransformerInfo.KvOf(m, lv);
        HvBus = TransformerInfo.NameOf(m, hv);
        HvKv = $"{hvKv:F0} kV";
        HvKvNom = hvKv;
        LvKvNom = lvKv;
        HvVm = TransformerInfo.Vpu(r, hv) is double vh ? $"{vh:F3} pu" : " - ";
        HvKvLive = TransformerInfo.Vpu(r, hv) is double vhk ? $"{vhk * hvKv:F1} kV" : " - ";
        HvKvLiveNum = TransformerInfo.Vpu(r, hv) is double vhk2 ? vhk2 * hvKv : hvKv;
        LvBus = TransformerInfo.NameOf(m, lv);
        LvKv = $"{lvKv:F0} kV";
        LvVm = TransformerInfo.Vpu(r, lv) is double vl ? $"{vl:F3} pu" : " - ";
        LvKvLive = TransformerInfo.Vpu(r, lv) is double vlk ? $"{vlk * lvKv:F1} kV" : " - ";
        LvKvLiveNum = TransformerInfo.Vpu(r, lv) is double vlk2 ? vlk2 * lvKv : lvKv;

        // Reactive flow at each terminal  -  sign convention is "into the unit".
        if (f is not null)
        {
            double qHv = fromIsHv ? f.QFromMvar : f.QToMvar;
            double qLv = fromIsHv ? f.QToMvar : f.QFromMvar;
            Qhv = $"{qHv:+#,0.0;-#,0.0;0} MVAr";
            Qlv = $"{qLv:+#,0.0;-#,0.0;0} MVAr";
        }
        else { Qhv = " - "; Qlv = " - "; }

        // The OLTC the solver applies to THIS branch (by index, through the solver's own mapping). Matching by name put
        // a full OLTC card on every namesake, including a parallel unit the tap changer does not regulate.
        TransformerInfo.OltcByBranch(m).TryGetValue(hit.Value.Index, out Oltc? oltc);
        HasOltc = oltc is not null;
        if (oltc is not null)
        {
            OltcBus = TransformerInfo.NameOf(m, oltc.ControlsBus);
            OltcBusId = oltc.ControlsBus;
            OltcTarget = $"{oltc.TargetVpu:F3} pu";
            OltcRange = $"{oltc.MinTap:F3} - {oltc.MaxTap:F3}";
            TapMinNum = oltc.MinTap;
            TapMaxNum = oltc.MaxTap;
            OltcStep = $"{oltc.StepPu * 100:F2} %/step";
            OltcDeadband = $"+/-{oltc.DeadbandPu:F3} pu";
            double? live = TransformerInfo.Vpu(r, oltc.ControlsBus);
            if (live is double v)
            {
                OltcLive = $"{v:F3} pu";
                double dev = v - oltc.TargetVpu;
                bool inBand = Math.Abs(dev) <= oltc.DeadbandPu;
                OltcState = inBand ? "IN BAND" : (dev > 0 ? "HIGH" : "LOW");
                OltcColour = inBand ? ColourRamps.Loading(0.0) : ColourRamps.Loading(0.75);
            }
            else { OltcLive = " - "; OltcState = " - "; OltcColour = DashboardColours.Gray; }
        }

        // The PROTECTION panel: the case's own settings for this transformer, or the tagged demo rows (ASSETS fix S7).
        ShowProtection(m, b, hv, lv);

        // The trace holds only the samples actually seen since the page opened. It used to be seeded with 95 copies of
        // the first reading, which drew an invented flat day and a half of history (the sparkline draws from 2 samples).
        double load = f is not null ? f.LoadingPercent : 0;
        _history.Add(load);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        Trace = _history.ToArray();
    }
}

// ===========================================================================
// Tap-changer fleet page
// ===========================================================================

/// <summary>One OLTC's row on the tap-changer fleet page.</summary>
public class TapChangerRowModel : ObservableModel
{
    public string Transformer { get; init; } = "";
    public string Bus { get; init; } = "";
    public string Target { get; init; } = "";
    public string Range { get; init; } = "";
    /// <summary>What a click on the row does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };

    private string _live = ""; public string Live { get => _live; set => Set(ref _live, value); }
    private string _deviation = ""; public string Deviation { get => _deviation; set => Set(ref _deviation, value); }
    private string _state = ""; public string State { get => _state; set => Set(ref _state, value); }
    // Was the WPF Chip brush (default Brushes.Gray).
    private UiColour _chipColour = DashboardColours.Gray; public UiColour ChipColour { get => _chipColour; set => Set(ref _chipColour, value); }
}

/// <summary>The tap-changer fleet page: every OLTC and the bus it regulates, with live deviation.</summary>
/// <typeparam name="TRow">The row type the front end shows.</typeparam>
public class TapChangerFleetModel<TRow>(AssetExplorerModel explorer) : AssetPageModel where TRow : TapChangerRowModel, new()
{
    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "TAP CHANGERS";
    public override string Sub => "on-load tap changers and the buses they hold to setpoint  -  live deviation per unit";

    public ObservableCollection<TRow> Rows { get; } = new();
    // Rows keyed by the OLTC record's index (plus its transformer name, so a model swap rebuilds rows): two OLTCs that
    // name the same transformer used to collapse into one row keyed by that name.
    private readonly Dictionary<string, TRow> _byOltc = new();

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _inBand = " - "; public string InBand { get => _inBand; set => Set(ref _inBand, value); }
    private string _meanTarget = " - "; public string MeanTarget { get => _meanTarget; set => Set(ref _meanTarget, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;
        int inBand = 0; double targetSum = 0;
        var seen = new HashSet<string>();

        // Built once for this refresh so each OLTC's controlled-bus name and voltage are O(1) lookups (R112).
        Dictionary<int, Bus> busById = TransformerInfo.BusById(m);
        Dictionary<int, int> resultIdx = TransformerInfo.ResultIndexById(r);

        // The branch each OLTC drives (solver mapping), so click-through opens THAT transformer, not the first namesake.
        int[] branchOfOltc = SteadyStateSolver.MapOltcRecordsToBranches(m);

        for (int k = 0; k < m.Oltcs.Count; k++)
        {
            Oltc o = m.Oltcs[k];
            string key = $"{k}:{o.Transformer}:{o.ControlsBus}";
            seen.Add(key);
            if (!_byOltc.TryGetValue(key, out TRow? row))
            {
                int branchIndex = branchOfOltc[k];
                Branch? driven = branchIndex >= 0 ? m.Branches[branchIndex] : null;
                string txName = o.Transformer;
                row = new TRow
                {
                    Transformer = o.Transformer,
                    Bus = TransformerInfo.NameOf(busById, o.ControlsBus),
                    Target = $"{o.TargetVpu:F3} pu",
                    Range = $"{o.MinTap:F3} - {o.MaxTap:F3}  {o.StepPu * 100:F2} %/step",
                    OnOpen = () =>
                    {
                        if (driven is not null) explorer.OpenTransformer(branchIndex, driven);
                        else explorer.OpenTransformerNamed(txName);   // no branch carries the name: the page says so
                    },
                };
                _byOltc[key] = row;
                Rows.Add(row);
            }

            targetSum += o.TargetVpu;
            double? live = TransformerInfo.Vpu(r, resultIdx, o.ControlsBus);
            if (live is double v)
            {
                double dev = v - o.TargetVpu;
                bool ok = Math.Abs(dev) <= o.DeadbandPu;
                if (ok) inBand++;
                row.Live = $"{v:F3} pu";
                row.Deviation = $"{dev * 1000:+0;-0;0} mpu";
                row.State = ok ? "IN BAND" : (dev > 0 ? "HIGH" : "LOW");
                row.ChipColour = ok ? ColourRamps.Loading(0.0) : ColourRamps.Loading(0.75);
            }
            else
            {
                row.Live = " - "; row.Deviation = " - "; row.State = " - "; row.ChipColour = DashboardColours.Gray;
            }
        }

        // Prune OLTCs absent from the current model (after a source switch while this page is open) so
        // stale rows don't keep showing their last Live/Deviation/state as if live.
        foreach (string stale in _byOltc.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Rows.Remove(_byOltc[stale]);
            _byOltc.Remove(stale);
        }

        Count = m.Oltcs.Count.ToString();
        InBand = $"{inBand} of {m.Oltcs.Count}";
        MeanTarget = m.Oltcs.Count > 0 ? $"{targetSum / m.Oltcs.Count:F3} pu" : " - ";
    }
}
