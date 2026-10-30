using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Pages;

// UI programme ASSETS A2: moved here from src/GridSim.Wpf/Views/Assets/CircuitPages.cs (commit 6e729a66), line for line,
// with brushes as colour tokens and commands as actions (see AssetPages.cs for the full list of what changed; nothing
// else did).

/// <summary>Endpoint and voltage-band helpers shared by the cables &amp; lines pages.</summary>
internal static class CircuitInfo
{
    /// <summary>
    /// How close (kV) a base voltage must be to a GB ladder level to be banded AT that level: strictly less than 0.5 kV,
    /// the same "same level" tolerance the substation fleet page uses for its kV chips (SubstationFleetModel.NearKv). It
    /// keeps the old comment's intent ("399.9/400 etc. group together") while a level that is really different (220,
    /// 380, 500 kV) is no longer pulled onto a GB level. ASSETS fix S10.
    /// </summary>
    internal const double LadderToleranceKv = 0.5;

    /// <summary>
    /// The kV band a base voltage falls into: the GB transmission/distribution ladder level when one lies within
    /// <see cref="LadderToleranceKv"/>, otherwise the voltage's own kV (rounded to a whole kV).
    /// </summary>
    /// <remarks>
    /// ASSETS fix S10 (2026-10-20, Mark approved fixing the quirks as their own slices; plan section 7 Q11, recommendation
    /// (b) "GB ladder for GB cases, exact kV elsewhere"). Before this fix EVERY base voltage snapped to the nearest GB
    /// level, so an international 220 kV bus read "275 kV", 300 kV read "275 kV", and 345, 380, 500 and 750 kV all read
    /// "400 kV" (two different real levels then shared one band and one chip). Now:
    ///   - a voltage within 0.5 kV of a ladder level (400, 275, 132, 66, 33, 11) is banded at that level, exactly as
    ///     before (the nearest-level search below is the old code, unchanged, so a GB case whose circuits all sit on the
    ///     ladder bands every circuit exactly as before);
    ///   - any other voltage is banded at its own kV, rounded half away from zero to a whole kV, so the label
    ///     (<see cref="Label"/>, "{band} kV") reads the real level, "220 kV", "380 kV", as the substation page's
    ///     "{kV:F0} kV" chips do for every whole-kV level.
    /// The model carries no explicit "this is a GB case" property (GridModel has a name and a nominal frequency, and
    /// 50 Hz alone does not mean GB), so the rule is by voltage alone: the exact-kV band applies whenever no ladder level
    /// is within the tolerance, which leaves every circuit that sits on the GB ladder, and so every GB case's circuits,
    /// unchanged.
    /// One case is deliberately left as before: a base voltage that would round to less than 1 kV (0 kV, a missing base
    /// kV, a negative or NaN value) still takes the old nearest-level snap. Band 0 is the fleet page's "ALL" chip, so a
    /// "0 kV" band would collide with it; such buses are a data gap, not a voltage level.
    /// </remarks>
    public static int Band(double baseKv)
    {
        // snap to the nearest standard level so 399.9/400 etc. group together
        int[] ladder = { 400, 275, 132, 66, 33, 11 };
        int best = ladder[0];
        double bestErr = double.MaxValue;
        foreach (int lvl in ladder)
        {
            double err = Math.Abs(baseKv - lvl);
            if (err < bestErr) { bestErr = err; best = lvl; }
        }

        // ASSETS fix S10: on the ladder (within the tolerance): the ladder level, as before.
        if (bestErr < LadderToleranceKv) return best;

        // Off the ladder: the voltage's own whole kV. A value that rounds below 1 kV (or is NaN, which fails every
        // comparison) keeps the old snap, so the "ALL" chip's band 0 is never produced; a value too large for an int
        // (no real network) keeps it too, rather than overflowing.
        double exact = Math.Round(baseKv, MidpointRounding.AwayFromZero);
        if (exact >= 1 && exact <= int.MaxValue) return (int)exact;
        return best;
    }

    /// <summary>The higher of a branch's two endpoint bands  -  the band a corridor is filtered/labelled by.</summary>
    public static int BandOf(GridModel m, Branch b)
    {
        double a = m.Buses.FirstOrDefault(x => x.Id == b.From)?.BaseKv ?? 0;
        double c = m.Buses.FirstOrDefault(x => x.Id == b.To)?.BaseKv ?? 0;
        return Band(Math.Max(a, c));
    }

    public static string Label(int band) => $"{band} kV";

    public static string BusName(GridModel m, int id) => m.Buses.FirstOrDefault(b => b.Id == id)?.Name ?? $"bus {id}";
}

/// <summary>A fleet page the explorer tree can open filtered to one voltage band (ASSETS A3 follow-up, 2026-10-24).</summary>
public interface IBandFilteredFleet
{
    /// <summary>Show only the assets of voltage band <paramref name="band"/> (kV); 0 shows all of them.</summary>
    void ApplyBandFilter(int band);
}

/// <summary>A selectable voltage-band chip on the fleet filter row ("ALL" / "400 kV" / ...).</summary>
public class BandChipModel : ObservableModel
{
    public string Label { get; init; } = "";
    public int Band { get; init; }            // 0 = the "ALL" chip
    /// <summary>What a click on the chip does (was the WPF <c>Select</c> command).</summary>
    public Action OnSelect { get; init; } = () => { };
    private bool _on; public bool IsOn { get => _on; set => Set(ref _on, value); }
}

/// <summary>One circuit's row on the cables &amp; lines fleet page.</summary>
public class CircuitFleetRowModel : ObservableModel
{
    public string Name { get; init; } = "";
    public string Endpoints { get; init; } = "";
    public string BandLabel { get; init; } = "";
    public string Rating { get; init; } = "";
    public int BranchIndex { get; init; }
    public int Band { get; init; }
    /// <summary>What a click on the row does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };

    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Was the WPF Bar brush (default Brushes.Gray).
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }

    internal double LoadFrac;   // sort key, not bound
}

/// <summary>
/// The cables &amp; lines fleet page: every non-transformer branch in the model as a
/// transmission corridor, grouped by voltage band, ordered most-loaded first. A band
/// chip row filters the register; click-through opens the corridor dashboard.
/// </summary>
/// <typeparam name="TChip">The band chip type the front end shows.</typeparam>
/// <typeparam name="TRow">The row type the front end shows.</typeparam>
public class CircuitFleetModel<TChip, TRow> : AssetPageModel, IBandFilteredFleet
    where TChip : BandChipModel, new()
    where TRow : CircuitFleetRowModel, new()
{
    public const double BarTrack = 240.0;
    private const int MaxRows = 200;

    private readonly AssetExplorerModel _explorer;

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "CABLES & LINES";
    public override string Sub => "transmission corridors  -  live loading against rating, ordered by stress";

    public ObservableCollection<TChip> Bands { get; } = new();
    public ObservableCollection<TRow> Circuits { get; } = new();

    /// <summary>The selected band filter; 0 means "ALL". Re-applied on the next <see cref="Refresh"/>.</summary>
    private int _selectedBand;

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _transfer = " - "; public string Transfer { get => _transfer; set => Set(ref _transfer, value); }
    private string _hottest = " - "; public string Hottest { get => _hottest; set => Set(ref _hottest, value); }
    private string _stressed = " - "; public string Stressed { get => _stressed; set => Set(ref _stressed, value); }
    private string _note = ""; public string Note { get => _note; set => Set(ref _note, value); }

    public CircuitFleetModel(AssetExplorerModel explorer) => _explorer = explorer;

    /// <summary>
    /// ASSETS A3 follow-up (2026-10-24): show only the circuits of voltage band <paramref name="band"/> (0: all of them),
    /// as a click on that band's chip does. Used when the explorer tree opens this page from a band group (or, with 0,
    /// from the CABLES &amp; LINES class node). Before the first frame there are no chips yet: the band is kept and lit
    /// when they are built (a band the model does not have falls back to ALL there).
    /// </summary>
    public void ApplyBandFilter(int band)
    {
        if (Bands.Count == 0) { _selectedBand = band; return; }
        Pick(Bands.Any(c => c.Band == band) ? band : 0);
    }

    private void Pick(int band)
    {
        _selectedBand = band;
        foreach (TChip c in Bands) c.IsOn = c.Band == band;
        if (_frame is not null) Refresh(_frame);   // re-filter immediately
    }

    private SimFrame? _frame;
    // Row objects kept across refreshes, keyed by the circuit's full identity (index, ends and name), so a row is reused
    // while it stays in the register and a model swap (even to a network with the same counts and name) builds new rows
    // instead of relabelling old ones. The key never relies on a guessed model identity.
    private readonly Dictionary<string, TRow> _rowByKey = new();

    private static string RowKey(int index, Branch b) => $"{index}:{b.From}-{b.To}:{b.Name}";

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        GridModel m = frame.Model;
        IReadOnlyList<LineFlow> flows = frame.Result.LineFlows;

        // One bus lookup per refresh, so each branch's band is O(1) (CircuitInfo.BandOf scans the bus list per call).
        var kvById = new Dictionary<int, double>(m.BusCount);
        foreach (Bus bus in m.Buses) kvById.TryAdd(bus.Id, bus.BaseKv);
        int BandOf(Branch b) => CircuitInfo.Band(Math.Max(kvById.GetValueOrDefault(b.From), kvById.GetValueOrDefault(b.To)));

        // Discover the bands present. The chip row is rebuilt whenever the set of bands changes (a model swap), so it
        // never offers a previous model's bands; a vanished selected band falls back to ALL.
        int[] bandsPresent = m.Branches.Where(b => !b.IsTransformer).Select(BandOf).Distinct().OrderByDescending(x => x).ToArray();
        if (Bands.Count == 0 || !Bands.Skip(1).Select(c => c.Band).SequenceEqual(bandsPresent))
        {
            if (_selectedBand != 0 && !bandsPresent.Contains(_selectedBand)) _selectedBand = 0;
            Bands.Clear();
            Bands.Add(new TChip { Label = "ALL", Band = 0, IsOn = _selectedBand == 0, OnSelect = () => Pick(0) });
            foreach (int band in bandsPresent)
            {
                int captured = band;
                Bands.Add(new TChip { Label = CircuitInfo.Label(band), Band = band, IsOn = band == _selectedBand, OnSelect = () => Pick(captured) });
            }
        }

        // The register (which circuits, in what order) is recomputed on EVERY refresh, so it stays ordered by the live
        // loading and a circuit that becomes heavily loaded later enters a capped register. It used to be built only on
        // a filter or model change, after which only the values moved: the "heaviest loaded first" note went stale as
        // the day swept and a circuit rising from 20 % to 110 % never appeared. Rows are reused and moved, not
        // recreated, so the list does not flicker.
        var members = new List<(int idx, double frac)>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch b = m.Branches[i];
            if (b.IsTransformer) continue;
            if (_selectedBand != 0 && BandOf(b) != _selectedBand) continue;
            double frac = b.RateAMva > 0 && i < flows.Count ? flows[i].LoadingPercent / 100.0 : 0;
            members.Add((i, frac));
        }
        int total = members.Count;
        // Stable order: equal loadings keep model order (OrderByDescending is stable), so ties do not shuffle per frame.
        List<int> top = members.OrderByDescending(x => x.frac).Take(MaxRows).Select(x => x.idx).ToList();

        var wanted = new List<TRow>(top.Count);
        var keep = new HashSet<string>();
        foreach (int idx in top)
        {
            Branch b = m.Branches[idx];
            string key = RowKey(idx, b);
            keep.Add(key);
            if (!_rowByKey.TryGetValue(key, out TRow? row))
            {
                int band = BandOf(b);
                int captured = idx;
                row = new TRow
                {
                    Name = b.Name ?? $"branch {idx}",
                    Endpoints = $"{CircuitInfo.BusName(m, b.From)} -> {CircuitInfo.BusName(m, b.To)}",
                    BandLabel = CircuitInfo.Label(band),
                    Band = band,
                    Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0} MVA" : " - ",
                    BranchIndex = captured,
                    // D-DOC-146: by index (the row's identity), confirmed by ends and name; not by name.
                    OnOpen = () => _explorer.OpenCircuit(captured, b),
                };
                _rowByKey[key] = row;
            }
            wanted.Add(row);
        }
        // Drop rows that left the register, then put the wanted rows in order (insert or move).
        foreach (string stale in _rowByKey.Keys.Where(k => !keep.Contains(k)).ToList()) _rowByKey.Remove(stale);
        var wantedSet = new HashSet<TRow>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = Circuits.Count - 1; i >= 0; i--)
            if (!wantedSet.Contains(Circuits[i])) Circuits.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Circuits.IndexOf(wanted[i]);
            if (at < 0) Circuits.Insert(i, wanted[i]);
            else if (at != i) Circuits.Move(at, i);
        }
        Note = total > MaxRows ? $"showing {MaxRows} of {total} circuits  -  heaviest loaded first" : $"{total} circuits";

        // Per-frame: live values on the existing rows, fleet-wide stats over every circuit.
        double totalTransfer = 0;
        int circuitCount = 0, stressed = 0;
        double hotFrac = -1; string hotName = " - ";
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Branch b = m.Branches[i];
            if (b.IsTransformer) continue;
            circuitCount++;
            LineFlow? lf = i < flows.Count ? flows[i] : null;
            totalTransfer += Math.Abs(lf?.PFromMw ?? 0);
            double f = b.RateAMva > 0 && lf is not null ? lf.LoadingPercent / 100.0 : 0;
            if (f > 0.8) stressed++;
            if (f > hotFrac) { hotFrac = f; hotName = b.Name ?? $"branch {i}"; }
        }

        foreach (TRow row in Circuits)
        {
            Branch b = m.Branches[row.BranchIndex];
            LineFlow? lf = row.BranchIndex < flows.Count ? flows[row.BranchIndex] : null;
            double frac = b.RateAMva > 0 && lf is not null ? lf.LoadingPercent / 100.0 : 0;
            row.LoadFrac = frac;
            row.Loading = b.RateAMva > 0 ? $"{frac * 100:F0}%" : " - ";
            row.Flow = $"{lf?.PFromMw ?? 0:+#,0;-#,0;0} MW";
            row.BarWidth = Math.Min(1.0, frac) * BarTrack;
            row.BarColour = ColourRamps.Loading(frac);
        }

        Count = circuitCount.ToString();
        Transfer = $"{totalTransfer / 1000.0:F1}";
        Hottest = hotFrac >= 0 ? $"{hotName}  {hotFrac * 100:F0}%" : " - ";
        Stressed = stressed.ToString();
    }
}

/// <summary>A status row in the corridor dashboard's static integrity/thermal/civil panels.</summary>
public class CircuitStatusRowModel : ObservableModel
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>The status dot's colour (was the WPF <c>Dot</c> brush, default <c>Brushes.Gray</c>).</summary>
    public UiColour DotColour { get; init; } = DashboardColours.Gray;
}

/// <summary>
/// One corridor's dashboard: live loading, signed From->To flow, rating, endpoint
/// voltages and electrical parameters drawn from the running solve, with a recent-
/// history sparkline. The route-integrity, thermal-envelope and access panels are
/// UI-first static demos  -  placeholders, clearly grouped, for the asset datasets
/// (sheath/joint/bonding records, DTS conductor temperatures, civil access) that will
/// back them later.
/// </summary>
/// <remarks>D-DOC-146: the circuit is identified by its branch INDEX, confirmed by its ends and model name
/// (<see cref="AssetLookup.FindBranch"/>), as the transformer and generator pages are since R055. It was identified by
/// name alone, so on a model that repeats a circuit name (a GDA zone or a user case; gb-full's names were made unique
/// by N169) a repeated name always opened, and read the flow of, the first namesake, and a circuit with no name (shown
/// as "branch i" or "from-to") never resolved, leaving the page empty.</remarks>
/// <typeparam name="TStatus">The status row type the front end shows.</typeparam>
public class CircuitDetailModel<TStatus>(int index, int fromBus, int toBus, string? branchName, AssetExplorerModel explorer)
    : AssetPageModel
    where TStatus : CircuitStatusRowModel, new()
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps

    // Display name (page title) - the same label the fleet row shows.
    private readonly string name = branchName ?? (index >= 0 ? $"branch {index}" : $"{fromBus}-{toBus}");

    public override string Eyebrow => "TRANSMISSION CORRIDOR";
    public override string Title => name;

    private string _sub = " - "; public override string Sub => _sub;

    // Click-through to the corridor's two endpoint substations (the in/out end swaps with
    // the direction of real power, so the ids are live properties the command reads at click).
    // These were the WPF OpenIn and OpenOut commands, whose parameters bind VInBusId and VOutBusId.
    public void OpenInSubstation(int id) => explorer.OpenSubstation(id);
    public void OpenOutSubstation(int id) => explorer.OpenSubstation(id);
    private int _vInBusId; public int VInBusId { get => _vInBusId; private set => Set(ref _vInBusId, value); }
    private int _vOutBusId; public int VOutBusId { get => _vOutBusId; private set => Set(ref _vOutBusId, value); }

    // ---- live, from the solve ----
    private string _loading = " - "; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = " - "; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _rating = " - "; public string Rating { get => _rating; set => Set(ref _rating, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    // Was the WPF LoadingBrush (default Brushes.Gray).
    private UiColour _loadingColour = DashboardColours.Gray; public UiColour LoadingColour { get => _loadingColour; set => Set(ref _loadingColour, value); }

    // ---- dials (doubles) ----
    // Loading is live; conductor temp is the static demo estimate carried below,
    // bound as a number so a DTS feed can replace it without touching the layout.
    private double _loadingPct; public double LoadingPct { get => _loadingPct; set => Set(ref _loadingPct, value); }
    private double _conductorC = 62; public double ConductorC { get => _conductorC; set => Set(ref _conductorC, value); }

    // ---- voltage in / out ----
    // The two endpoint voltages presented as the cable's in/out, oriented by the
    // direction of real power (the "in" end is wherever P flows away from).
    private string _vInBus = " - "; public string VInBus { get => _vInBus; set => Set(ref _vInBus, value); }
    private string _vOutBus = " - "; public string VOutBus { get => _vOutBus; set => Set(ref _vOutBus, value); }
    private string _vIn = " - "; public string VIn { get => _vIn; set => Set(ref _vIn, value); }
    private string _vOut = " - "; public string VOut { get => _vOut; set => Set(ref _vOut, value); }
    private string _vInKv = " - "; public string VInKv { get => _vInKv; set => Set(ref _vInKv, value); }
    private string _vOutKv = " - "; public string VOutKv { get => _vOutKv; set => Set(ref _vOutKv, value); }
    private string _deltaV = " - "; public string DeltaV { get => _deltaV; set => Set(ref _deltaV, value); }
    private string _deltaKv = " - "; public string DeltaKv { get => _deltaKv; set => Set(ref _deltaKv, value); }
    private string _angleSpread = " - "; public string AngleSpread { get => _angleSpread; set => Set(ref _angleSpread, value); }

    // ---- reactive balance ----
    private string _qIn = " - "; public string Qin { get => _qIn; set => Set(ref _qIn, value); }
    private string _qOut = " - "; public string Qout { get => _qOut; set => Set(ref _qOut, value); }
    private string _qLoss = " - "; public string Qloss { get => _qLoss; set => Set(ref _qLoss, value); }
    private string _qVerdict = " - "; public string Qverdict { get => _qVerdict; set => Set(ref _qVerdict, value); }
    // Was the WPF QverdictBrush (default Brushes.Gray).
    private UiColour _qVerdictColour = DashboardColours.Gray; public UiColour QverdictColour { get => _qVerdictColour; set => Set(ref _qVerdictColour, value); }
    private string _iIn = " - "; public string Iin { get => _iIn; set => Set(ref _iIn, value); }
    private string _iOut = " - "; public string Iout { get => _iOut; set => Set(ref _iOut, value); }
    private string _pLoss = " - "; public string Ploss { get => _pLoss; set => Set(ref _pLoss, value); }
    private string _pLossPct = " - "; public string PlossPct { get => _pLossPct; set => Set(ref _pLossPct, value); }

    private string _r = " - "; public string Rpu { get => _r; set => Set(ref _r, value); }
    private string _x = " - "; public string Xpu { get => _x; set => Set(ref _x, value); }
    private string _b = " - "; public string Bpu { get => _b; set => Set(ref _b, value); }
    private string _xr = " - "; public string XoverR { get => _xr; set => Set(ref _xr, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    // ---- static, UI-first demo registers (await real datasets) ----
    public ObservableCollection<TStatus> RouteIntegrity { get; } =
    [
        new() { Label = "Sheath / serving", Value = "intact  no fault current", DotColour = ColourRamps.Voltage(0.99) },
        new() { Label = "Joint bays", Value = "4 bays  last surveyed 2025-11", DotColour = ColourRamps.Voltage(0.99) },
        new() { Label = "Cross-bonding", Value = "balanced  circulating < 5 A", DotColour = ColourRamps.Voltage(0.99) },
        new() { Label = "Earth continuity", Value = "verified", DotColour = ColourRamps.Voltage(0.99) },
    ];

    public ObservableCollection<TStatus> Thermal { get; } =
    [
        new() { Label = "Conductor (est.)", Value = "62  degC", DotColour = ColourRamps.Loading(0.45) },
        new() { Label = "Soil / ambient", Value = "15  degC  1.0 Km/W", DotColour = ColourRamps.Voltage(0.99) },
        new() { Label = "Thermal headroom", Value = "to 90  degC continuous", DotColour = ColourRamps.Loading(0.3) },
        new() { Label = "Dynamic rating", Value = "static rating in use", DotColour = ColourRamps.Voltage(0.97) },
    ];

    public ObservableCollection<TStatus> Access { get; } =
    [
        new() { Label = "Route access", Value = "highway verge  2 of 4 bays trafficable", DotColour = ColourRamps.Voltage(0.98) },
        new() { Label = "Third-party works", Value = "none notified within 10 m", DotColour = ColourRamps.Voltage(0.99) },
    ];

    public ObservableCollection<string> EventLog { get; } =
    [
        "2025-11-18  routine joint-bay survey  -  clear",
        "2025-09-02  partial-discharge scan  -  no activity",
        "2025-06-14  thermographic patrol  -  within envelope",
    ];

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        // D-DOC-146: the index, confirmed by ends and name; otherwise the one branch with those ends and name;
        // otherwise (gone, or ambiguous) say so rather than show a namesake's numbers.
        (Branch Branch, int Index)? hit = AssetLookup.FindBranch(m, index, fromBus, toBus, branchName);
        if (hit is not { Branch.IsTransformer: false })
        {
            _sub = "this circuit is not in the current model (model changed?)  -  values below are stale";
            Raise(nameof(Sub));
            return;
        }
        Branch b = hit.Value.Branch;
        int i = hit.Value.Index;

        _sub = $"{CircuitInfo.BusName(m, b.From)} -> {CircuitInfo.BusName(m, b.To)}  {CircuitInfo.Label(CircuitInfo.BandOf(m, b))}";
        Raise(nameof(Sub));

        LineFlow? lf = i >= 0 && i < frame.Result.LineFlows.Count ? frame.Result.LineFlows[i] : null;
        double pFrom = lf?.PFromMw ?? 0, pTo = lf?.PToMw ?? 0;
        double frac = b.RateAMva > 0 && lf is not null ? lf.LoadingPercent / 100.0 : 0;

        Loading = b.RateAMva > 0 ? $"{frac * 100:F0}%" : " - ";
        LoadingPct = frac * 100;
        LoadingColour = ColourRamps.Loading(frac);
        Flow = $"{pFrom:+#,0;-#,0;0}";
        Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0}" : " - ";
        Losses = lf is not null ? $"{Math.Abs(pFrom + pTo):F1}" : " - ";

        // Voltage in / out  -  orient by the sign of real power: power leaves the "in"
        // end. PFromMw >= 0 means From->To, so From is the inlet; otherwise it's To.
        bool fromIsIn = pFrom >= 0;
        int inId = fromIsIn ? b.From : b.To, outId = fromIsIn ? b.To : b.From;
        VInBusId = inId; VOutBusId = outId;
        double inKv = m.Buses.FirstOrDefault(x => x.Id == inId)?.BaseKv ?? 0;
        double outKv = m.Buses.FirstOrDefault(x => x.Id == outId)?.BaseKv ?? 0;
        VInBus = CircuitInfo.BusName(m, inId);
        VOutBus = CircuitInfo.BusName(m, outId);
        double? vInPu = VmPu(frame.Result, inId), vOutPu = VmPu(frame.Result, outId);
        VIn = vInPu is double vi ? $"{vi:F3} pu" : " - ";
        VOut = vOutPu is double vo ? $"{vo:F3} pu" : " - ";
        VInKv = vInPu is double vik ? $"{vik * inKv:F1} kV" : " - ";
        VOutKv = vOutPu is double vok ? $"{vok * outKv:F1} kV" : " - ";
        if (vInPu is double a && vOutPu is double bb)
        {
            double dPu = a - bb;                         // pu drop across the cable
            DeltaV = $"{dPu:+0.000;-0.000;0.000} pu";
            DeltaKv = $"{dPu * inKv:+0.0;-0.0;0.0} kV";   // referenced to the inlet base
        }
        else { DeltaV = " - "; DeltaKv = " - "; }
        double? angIn = VaOf(frame.Result, inId), angOut = VaOf(frame.Result, outId);
        AngleSpread = angIn is double ai && angOut is double ao ? $"{ai - ao:+0.0;-0.0;0.0} deg" : " - ";

        // Reactive balance: Q into each end and the net consumed in the series
        // reactance (QLossMvar = XI^2 absorbed minus BV^2 generated by the line's
        // charging). Sink => heavily loaded; source => light, charging dominates.
        if (lf is not null)
        {
            double qIn = fromIsIn ? lf.QFromMvar : lf.QToMvar;
            double qOut = fromIsIn ? lf.QToMvar : lf.QFromMvar;
            Qin = $"{qIn:+#,0.0;-#,0.0;0.0} MVAr";
            Qout = $"{qOut:+#,0.0;-#,0.0;0.0} MVAr";
            Qloss = $"{lf.QLossMvar:+#,0.0;-#,0.0;0.0} MVAr";
            bool sink = lf.QLossMvar >= 0;
            Qverdict = sink ? "net MVAr SINK  -  XI^2 dominates" : "net MVAr SOURCE  -  BV^2 charging dominates";
            QverdictColour = sink ? ColourRamps.Loading(0.7) : ColourRamps.Voltage(1.02);

            // Current per end: I ~= S / (3VkV), S = (P^2+Q^2), V = pubaseKv.
            // Swap BOTH the S side and the base kV by flow orientation: the old
            // code always used the From end's P/Q for "in", so with reversed
            // flow the labelled inlet current was the outlet end's S over the
            // inlet end's voltage - a hybrid of the two ends.
            Iin = AmpsText(fromIsIn ? pFrom : pTo, fromIsIn ? lf.QFromMvar : lf.QToMvar, vInPu, inKv);
            Iout = AmpsText(fromIsIn ? pTo : pFrom, fromIsIn ? lf.QToMvar : lf.QFromMvar, vOutPu, outKv);

            // Real loss and its share of the transfer.
            Ploss = $"{lf.PLossMw:F2}";
            double transfer = Math.Max(Math.Abs(pFrom), Math.Abs(pTo));
            PlossPct = transfer > 1e-6 ? $"{lf.PLossMw / transfer * 100:F2}% of transfer" : " - ";
        }
        else
        {
            Qin = Qout = Qloss = " - "; Qverdict = " - "; QverdictColour = DashboardColours.Gray;
            Iin = Iout = " - "; Ploss = " - "; PlossPct = " - ";
        }

        // Quirk copied as is (ASSETS plan section 2 item 3): these are the solver's per-circuit equivalent values, not
        // the authored record's.
        Rpu = $"{b.R:F4}";
        Xpu = $"{b.X:F4}";
        Bpu = $"{b.B:F4}";
        XoverR = b.R > 1e-9 ? $"{b.X / b.R:F1}" : " - ";

        // The trace holds only the samples actually seen since the page opened. It used to be seeded with 95 copies of
        // the first reading, which drew an invented flat day and a half of history (the sparkline draws from 2 samples).
        _history.Add(frac * 100);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        Trace = _history.ToArray();
    }

    /// <summary>Per-end current estimate "I ~= S/(3VkV)" rendered in amps, V in pu x base kV.</summary>
    private static string AmpsText(double pMw, double qMvar, double? vPu, double baseKv)
    {
        if (vPu is not double v || baseKv <= 0 || v <= 0) return " - ";
        double sMva = Math.Sqrt(pMw * pMw + qMvar * qMvar);
        double amps = sMva * 1_000_000 / (Math.Sqrt(3) * v * baseKv * 1000);
        return $"{amps:#,0} A";
    }

    private static double? VmPu(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++)
            if (r.BusIds[i] == busId) return r.Vm[i];
        return null;
    }

    private static double? VaOf(PowerFlowResult r, int busId)
    {
        for (int i = 0; i < r.BusIds.Count; i++)
            if (r.BusIds[i] == busId) return r.VaDeg[i];
        return null;
    }
}
