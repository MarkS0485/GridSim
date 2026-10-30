// TEST ORACLE (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/TransformerPages.cs exactly as it was at commit 6e729a66
// TEST ORACLE (2.0.46), before its page logic moved to src/GridSim.Ui/Assets/Pages. Only the using and namespace lines
// TEST ORACLE differ: the WPF types it named are the stand-ins in OldAssetsShims.cs (Brush, Color, Visibility, Palette,
// TEST ORACLE Fuels, Notify, Visuals.SubstationMapData). UiAssetPagesParityTests checks a SHA-256 of the rest against
// TEST ORACLE the original file.
using System.Collections.ObjectModel;
using System.Windows.Input;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldAssets;

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
    public static Dictionary<int, int> ResultIndexById(PowerFlowResult r)
    {
        var idx = new Dictionary<int, int>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) idx.TryAdd(r.BusIds[i], i);
        return idx;
    }

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

// ===========================================================================
// Fleet page
// ===========================================================================

/// <summary>One transformer's row on the fleet page.</summary>
public sealed class TransformerFleetRow : Notify
{
    /// <summary>The transformer's branch index in the model: the row's identity (branch names repeat).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Band { get; init; } = "";
    public string Rating { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });

    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _tap = ""; public string Tap { get => _tap; set => Set(ref _tap, value); }
    private string _oltc = ""; public string Oltc { get => _oltc; set => Set(ref _oltc, value); }
    private bool _hasOltc; public bool HasOltc { get => _hasOltc; set => Set(ref _hasOltc, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }
    private double _loadingFrac; public double LoadingFrac { get => _loadingFrac; set => _loadingFrac = value; }
}

/// <summary>The transformer fleet page: every grid/GSP transformer, ordered by live loading.</summary>
public sealed class TransformerFleetVm(AssetExplorerVm explorer) : AssetPageVm
{
    public const double BarTrack = 240.0;
    private const int RowCap = 200;   // big GDA models carry thousands of transformers

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "TRANSFORMERS";
    public override string Sub => "GSP and grid transformers by voltage band  -  loading, rating and OLTC state per unit";

    public ObservableCollection<TransformerFleetRow> Rows { get; } = new();
    private readonly Dictionary<int, TransformerFleetRow> _byIndex = new();   // keyed by branch index (R055)

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _bands = " - "; public string Bands { get => _bands; set => Set(ref _bands, value); }
    private string _meanLoading = " - "; public string MeanLoading { get => _meanLoading; set => Set(ref _meanLoading, value); }
    private string _oltcFitted = " - "; public string OltcFitted { get => _oltcFitted; set => Set(ref _oltcFitted, value); }
    private string _capNote = ""; public string CapNote { get => _capNote; set => Set(ref _capNote, value); }

    public override void Refresh(SimFrame frame)
    {
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

            if (!_byIndex.TryGetValue(idx, out TransformerFleetRow? row))
            {
                Branch branch = b;
                row = new TransformerFleetRow
                {
                    Index = idx,
                    Name = name,
                    Band = TransformerInfo.Band(busById, b),
                    Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0} MVA" : " - ",
                    Open = new RelayCommand(() => explorer.OpenTransformer(idx, branch)),
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
            row.Bar = Palette.Frozen(Palette.Loading(frac));
            row.Flow = f is not null ? $"{f.PFromMw:+#,0;-#,0;0} MW" : " - ";
            row.Tap = $"{b.TapRatio:F3}";
            row.HasOltc = oltc is not null;
            row.Oltc = oltc is not null ? "OLTC" : "";
        }

        Count = txs.Count.ToString();
        Bands = bands.Count > 0 ? string.Join(" / ", bands.Take(3).Select(s => s.Replace("->", "/").Replace(" kV", ""))) + " kV" : " - ";
        MeanLoading = loadN > 0 ? $"{loadSum / loadN:F0}%" : " - ";
        OltcFitted = $"{m.Oltcs.Count} of {txs.Count}";
        CapNote = txs.Count > RowCap ? $"showing {RowCap} of {txs.Count}  -  heaviest loaded first" : "";
    }
}

// ===========================================================================
// Unit page
// ===========================================================================

/// <summary>A labelled measurement row on the unit page  -  name, value, status chip.</summary>
public sealed class TransformerReading(string label, string value, string status, Brush chip) : Notify
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public string Status { get; } = status;
    public Brush Chip { get; } = chip;
}

/// <summary>One line in the unit's event log.</summary>
public sealed class TransformerEvent(string time, string text, Brush chip) : Notify
{
    public string Time { get; } = time;
    public string Text { get; } = text;
    public Brush Chip { get; } = chip;
}

/// <summary>
/// One transformer's dashboard: live electrical state and OLTC mechanics off the
/// solved frame, plus the asset-management sections (thermal, DGA, protection,
/// event log) the v1 deep page carried. Those last four are static demo content
/// today  -  sensible nameplate values with alarm context  -  wired so a monitoring
/// feed can replace them without touching the layout.
/// </summary>
/// <remarks>The transformer is identified by its branch INDEX, confirmed by its ends and model name
/// (<see cref="AssetLookup.FindBranch"/>); name lookup opened, and read the flow of, the first namesake (R055).</remarks>
public sealed class TransformerDetailVm(int index, int fromBus, int toBus, string? branchName, AssetExplorerVm explorer)
    : AssetPageVm
{
    private const int HistoryLength = 96;

    // Display name (page title) - the same label the fleet row shows. The OLTC is found by branch index, not by this.
    private readonly string name = branchName ?? $"TX {fromBus}-{toBus}";

    public override string Eyebrow => "TRANSFORMER";
    public override string Title => name;
    public override string Sub => _band;
    private string _band = "";

    // Click-through to the HV/LV endpoint substations and the bus the OLTC regulates.
    public ICommand OpenHv { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    public ICommand OpenLv { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    public ICommand OpenOltcBus { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    private int _hvBusId; public int HvBusId { get => _hvBusId; private set => Set(ref _hvBusId, value); }
    private int _lvBusId; public int LvBusId { get => _lvBusId; private set => Set(ref _lvBusId, value); }
    private int _oltcBusId; public int OltcBusId { get => _oltcBusId; private set => Set(ref _oltcBusId, value); }

    // --- live electrical ----------------------------------------------------
    private string _loading = " - "; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private Brush _loadingBrush = Brushes.Gray; public Brush LoadingBrush { get => _loadingBrush; set => Set(ref _loadingBrush, value); }
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
    private Brush _oltcBrush = Brushes.Gray; public Brush OltcBrush { get => _oltcBrush; set => Set(ref _oltcBrush, value); }

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
    public ObservableCollection<TransformerReading> Thermal { get; } = new();
    public ObservableCollection<TransformerReading> Dga { get; } = new();
    public ObservableCollection<TransformerReading> Protection { get; } = new();
    public ObservableCollection<TransformerEvent> Events { get; } = new();

    private bool _seeded;

    private static Brush Chip(string status) => status switch
    {
        "ALARM" or "TRIP" => Palette.Frozen(Palette.Loading(1.2)),
        "WARN" or "CAUTION" => Palette.Frozen(Palette.Loading(0.75)),
        _ => Palette.Frozen(Palette.Loading(0.0)),
    };

    /// <summary>Plausible nameplate / monitoring values  -  see class summary; replace with a feed later.</summary>
    private void SeedStatic()
    {
        Thermal.Add(new TransformerReading("Top-oil temperature", "58  degC", "OK", Chip("OK")));
        Thermal.Add(new TransformerReading("Winding hot-spot", "71  degC", "OK", Chip("OK")));
        Thermal.Add(new TransformerReading("Hot-spot alarm / trip", "98 / 110  degC", "OK", Chip("OK")));
        Thermal.Add(new TransformerReading("Cooling stage", "ONAN -> ONAF 1", "OK", Chip("OK")));
        Thermal.Add(new TransformerReading("Ambient", "16  degC", "OK", Chip("OK")));

        // Dissolved gas analysis  -  ppm vs typical IEEE C57.104 condition-1 caps.
        Dga.Add(new TransformerReading("Hydrogen (H2)", "42 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Carbon monoxide (CO)", "310 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Carbon dioxide (CO2)", "2 600 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Methane (CH)", "28 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Ethylene (C2H)", "19 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Ethane (C2H)", "14 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Acetylene (C2H2)", "1 ppm", "OK", Chip("OK")));
        Dga.Add(new TransformerReading("Moisture (H2O)", "9 ppm", "CAUTION", Chip("CAUTION")));

        Protection.Add(new TransformerReading("Buchholz relay", "healthy", "OK", Chip("OK")));
        Protection.Add(new TransformerReading("Differential (87T)", "healthy", "OK", Chip("OK")));
        Protection.Add(new TransformerReading("Overcurrent (51)", "healthy", "OK", Chip("OK")));
        Protection.Add(new TransformerReading("Restricted earth fault (64)", "healthy", "OK", Chip("OK")));
        Protection.Add(new TransformerReading("Pressure relief (63)", "healthy", "OK", Chip("OK")));

        Events.Add(new TransformerEvent("06:14", "OLTC tap raise  -  step +1", Chip("OK")));
        Events.Add(new TransformerEvent("04:02", "Cooling stage ONAF 1 engaged", Chip("OK")));
        Events.Add(new TransformerEvent("Yesterday", "Routine DGA sample logged", Chip("OK")));
        Events.Add(new TransformerEvent("3 days ago", "Moisture trend flagged for review", Chip("CAUTION")));
        Events.Add(new TransformerEvent("Last week", "Annual thermographic survey  -  no exceptions", Chip("OK")));
    }

    public override void Refresh(SimFrame frame)
    {
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
        LoadingBrush = Palette.Frozen(Palette.Loading(frac));
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
                OltcBrush = Palette.Frozen(inBand ? Palette.Loading(0.0) : Palette.Loading(0.75));
            }
            else { OltcLive = " - "; OltcState = " - "; OltcBrush = Brushes.Gray; }
        }

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
public sealed class TapChangerRow : Notify
{
    public string Transformer { get; init; } = "";
    public string Bus { get; init; } = "";
    public string Target { get; init; } = "";
    public string Range { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });

    private string _live = ""; public string Live { get => _live; set => Set(ref _live, value); }
    private string _deviation = ""; public string Deviation { get => _deviation; set => Set(ref _deviation, value); }
    private string _state = ""; public string State { get => _state; set => Set(ref _state, value); }
    private Brush _chip = Brushes.Gray; public Brush Chip { get => _chip; set => Set(ref _chip, value); }
}

/// <summary>The tap-changer fleet page: every OLTC and the bus it regulates, with live deviation.</summary>
public sealed class TapChangerFleetVm(AssetExplorerVm explorer) : AssetPageVm
{
    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "TAP CHANGERS";
    public override string Sub => "on-load tap changers and the buses they hold to setpoint  -  live deviation per unit";

    public ObservableCollection<TapChangerRow> Rows { get; } = new();
    // Rows keyed by the OLTC record's index (plus its transformer name, so a model swap rebuilds rows): two OLTCs that
    // name the same transformer used to collapse into one row keyed by that name.
    private readonly Dictionary<string, TapChangerRow> _byOltc = new();

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _inBand = " - "; public string InBand { get => _inBand; set => Set(ref _inBand, value); }
    private string _meanTarget = " - "; public string MeanTarget { get => _meanTarget; set => Set(ref _meanTarget, value); }

    public override void Refresh(SimFrame frame)
    {
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
            if (!_byOltc.TryGetValue(key, out TapChangerRow? row))
            {
                int branchIndex = branchOfOltc[k];
                Branch? driven = branchIndex >= 0 ? m.Branches[branchIndex] : null;
                string txName = o.Transformer;
                row = new TapChangerRow
                {
                    Transformer = o.Transformer,
                    Bus = TransformerInfo.NameOf(busById, o.ControlsBus),
                    Target = $"{o.TargetVpu:F3} pu",
                    Range = $"{o.MinTap:F3} - {o.MaxTap:F3}  {o.StepPu * 100:F2} %/step",
                    Open = new RelayCommand(() =>
                    {
                        if (driven is not null) explorer.OpenTransformer(branchIndex, driven);
                        else explorer.OpenTransformerNamed(txName);   // no branch carries the name: the page says so
                    }),
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
                row.Chip = Palette.Frozen(ok ? Palette.Loading(0.0) : Palette.Loading(0.75));
            }
            else
            {
                row.Live = " - "; row.Deviation = " - "; row.State = " - "; row.Chip = Brushes.Gray;
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
