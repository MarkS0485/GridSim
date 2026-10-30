// TEST ORACLE (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/CircuitPages.cs exactly as it was at commit 6e729a66
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

/// <summary>Endpoint and voltage-band helpers shared by the cables &amp; lines pages.</summary>
internal static class CircuitInfo
{
    /// <summary>The kV band a base voltage falls into, snapped to the GB transmission/distribution ladder.</summary>
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

/// <summary>A selectable voltage-band chip on the fleet filter row ("ALL" / "400 kV" / ...).</summary>
public sealed class BandChip : Notify
{
    public string Label { get; init; } = "";
    public int Band { get; init; }            // 0 = the "ALL" chip
    public System.Windows.Input.ICommand Select { get; init; } = new RelayCommand(() => { });
    private bool _on; public bool IsOn { get => _on; set => Set(ref _on, value); }
}

/// <summary>One circuit's row on the cables &amp; lines fleet page.</summary>
public sealed class CircuitFleetRow : Notify
{
    public string Name { get; init; } = "";
    public string Endpoints { get; init; } = "";
    public string BandLabel { get; init; } = "";
    public string Rating { get; init; } = "";
    public int BranchIndex { get; init; }
    public int Band { get; init; }
    public System.Windows.Input.ICommand Open { get; init; } = new RelayCommand(() => { });

    private string _loading = ""; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }

    internal double LoadFrac;   // sort key, not bound
}

/// <summary>
/// The cables &amp; lines fleet page: every non-transformer branch in the model as a
/// transmission corridor, grouped by voltage band, ordered most-loaded first. A band
/// chip row filters the register; click-through opens the corridor dashboard.
/// </summary>
public sealed class CircuitFleetVm : AssetPageVm
{
    public const double BarTrack = 240.0;
    private const int MaxRows = 200;

    private readonly AssetExplorerVm _explorer;

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "CABLES & LINES";
    public override string Sub => "transmission corridors  -  live loading against rating, ordered by stress";

    public ObservableCollection<BandChip> Bands { get; } = new();
    public ObservableCollection<CircuitFleetRow> Circuits { get; } = new();

    /// <summary>The selected band filter; 0 means "ALL". Re-applied on the next <see cref="Refresh"/>.</summary>
    private int _selectedBand;

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }
    private string _transfer = " - "; public string Transfer { get => _transfer; set => Set(ref _transfer, value); }
    private string _hottest = " - "; public string Hottest { get => _hottest; set => Set(ref _hottest, value); }
    private string _stressed = " - "; public string Stressed { get => _stressed; set => Set(ref _stressed, value); }
    private string _note = ""; public string Note { get => _note; set => Set(ref _note, value); }

    public CircuitFleetVm(AssetExplorerVm explorer) => _explorer = explorer;

    private void Pick(int band)
    {
        _selectedBand = band;
        foreach (BandChip c in Bands) c.IsOn = c.Band == band;
        if (_frame is not null) Refresh(_frame);   // re-filter immediately
    }

    private SimFrame? _frame;
    // Row objects kept across refreshes, keyed by the circuit's full identity (index, ends and name), so a row is reused
    // while it stays in the register and a model swap (even to a network with the same counts and name) builds new rows
    // instead of relabelling old ones. The key never relies on a guessed model identity.
    private readonly Dictionary<string, CircuitFleetRow> _rowByKey = new();

    private static string RowKey(int index, Branch b) => $"{index}:{b.From}-{b.To}:{b.Name}";

    public override void Refresh(SimFrame frame)
    {
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
            Bands.Add(new BandChip { Label = "ALL", Band = 0, IsOn = _selectedBand == 0, Select = new RelayCommand(() => Pick(0)) });
            foreach (int band in bandsPresent)
            {
                int captured = band;
                Bands.Add(new BandChip { Label = CircuitInfo.Label(band), Band = band, IsOn = band == _selectedBand, Select = new RelayCommand(() => Pick(captured)) });
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

        var wanted = new List<CircuitFleetRow>(top.Count);
        var keep = new HashSet<string>();
        foreach (int idx in top)
        {
            Branch b = m.Branches[idx];
            string key = RowKey(idx, b);
            keep.Add(key);
            if (!_rowByKey.TryGetValue(key, out CircuitFleetRow? row))
            {
                int band = BandOf(b);
                int captured = idx;
                row = new CircuitFleetRow
                {
                    Name = b.Name ?? $"branch {idx}",
                    Endpoints = $"{CircuitInfo.BusName(m, b.From)} -> {CircuitInfo.BusName(m, b.To)}",
                    BandLabel = CircuitInfo.Label(band),
                    Band = band,
                    Rating = b.RateAMva > 0 ? $"{b.RateAMva:F0} MVA" : " - ",
                    BranchIndex = captured,
                    // D-DOC-146: by index (the row's identity), confirmed by ends and name; not by name.
                    Open = new RelayCommand(() => _explorer.OpenCircuit(captured, b)),
                };
                _rowByKey[key] = row;
            }
            wanted.Add(row);
        }
        // Drop rows that left the register, then put the wanted rows in order (insert or move).
        foreach (string stale in _rowByKey.Keys.Where(k => !keep.Contains(k)).ToList()) _rowByKey.Remove(stale);
        var wantedSet = new HashSet<CircuitFleetRow>(wanted, ReferenceEqualityComparer.Instance);
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

        foreach (CircuitFleetRow row in Circuits)
        {
            Branch b = m.Branches[row.BranchIndex];
            LineFlow? lf = row.BranchIndex < flows.Count ? flows[row.BranchIndex] : null;
            double frac = b.RateAMva > 0 && lf is not null ? lf.LoadingPercent / 100.0 : 0;
            row.LoadFrac = frac;
            row.Loading = b.RateAMva > 0 ? $"{frac * 100:F0}%" : " - ";
            row.Flow = $"{lf?.PFromMw ?? 0:+#,0;-#,0;0} MW";
            row.BarWidth = Math.Min(1.0, frac) * BarTrack;
            row.Bar = Palette.Frozen(Palette.Loading(frac));
        }

        Count = circuitCount.ToString();
        Transfer = $"{totalTransfer / 1000.0:F1}";
        Hottest = hotFrac >= 0 ? $"{hotName}  {hotFrac * 100:F0}%" : " - ";
        Stressed = stressed.ToString();
    }
}

/// <summary>A status row in the corridor dashboard's static integrity/thermal/civil panels.</summary>
public sealed class CircuitStatusRow : Notify
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public Brush Dot { get; init; } = Brushes.Gray;
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
public sealed class CircuitDetailVm(int index, int fromBus, int toBus, string? branchName, AssetExplorerVm explorer)
    : AssetPageVm
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps

    // Display name (page title) - the same label the fleet row shows.
    private readonly string name = branchName ?? (index >= 0 ? $"branch {index}" : $"{fromBus}-{toBus}");

    public override string Eyebrow => "TRANSMISSION CORRIDOR";
    public override string Title => name;

    private string _sub = " - "; public override string Sub => _sub;

    // Click-through to the corridor's two endpoint substations (the in/out end swaps with
    // the direction of real power, so the ids are live properties the command reads at click).
    public ICommand OpenIn { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    public ICommand OpenOut { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    private int _vInBusId; public int VInBusId { get => _vInBusId; private set => Set(ref _vInBusId, value); }
    private int _vOutBusId; public int VOutBusId { get => _vOutBusId; private set => Set(ref _vOutBusId, value); }

    // ---- live, from the solve ----
    private string _loading = " - "; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private string _flow = " - "; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _rating = " - "; public string Rating { get => _rating; set => Set(ref _rating, value); }
    private string _losses = " - "; public string Losses { get => _losses; set => Set(ref _losses, value); }
    private Brush _loadingBrush = Brushes.Gray; public Brush LoadingBrush { get => _loadingBrush; set => Set(ref _loadingBrush, value); }

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
    private Brush _qVerdictBrush = Brushes.Gray; public Brush QverdictBrush { get => _qVerdictBrush; set => Set(ref _qVerdictBrush, value); }
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
    public ObservableCollection<CircuitStatusRow> RouteIntegrity { get; } =
    [
        new() { Label = "Sheath / serving", Value = "intact  no fault current", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
        new() { Label = "Joint bays", Value = "4 bays  last surveyed 2025-11", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
        new() { Label = "Cross-bonding", Value = "balanced  circulating < 5 A", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
        new() { Label = "Earth continuity", Value = "verified", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
    ];

    public ObservableCollection<CircuitStatusRow> Thermal { get; } =
    [
        new() { Label = "Conductor (est.)", Value = "62  degC", Dot = Palette.Frozen(Palette.Loading(0.45)) },
        new() { Label = "Soil / ambient", Value = "15  degC  1.0 Km/W", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
        new() { Label = "Thermal headroom", Value = "to 90  degC continuous", Dot = Palette.Frozen(Palette.Loading(0.3)) },
        new() { Label = "Dynamic rating", Value = "static rating in use", Dot = Palette.Frozen(Palette.Voltage(0.97)) },
    ];

    public ObservableCollection<CircuitStatusRow> Access { get; } =
    [
        new() { Label = "Route access", Value = "highway verge  2 of 4 bays trafficable", Dot = Palette.Frozen(Palette.Voltage(0.98)) },
        new() { Label = "Third-party works", Value = "none notified within 10 m", Dot = Palette.Frozen(Palette.Voltage(0.99)) },
    ];

    public ObservableCollection<string> EventLog { get; } =
    [
        "2025-11-18  routine joint-bay survey  -  clear",
        "2025-09-02  partial-discharge scan  -  no activity",
        "2025-06-14  thermographic patrol  -  within envelope",
    ];

    public override void Refresh(SimFrame frame)
    {
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
        LoadingBrush = Palette.Frozen(Palette.Loading(frac));
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
            QverdictBrush = Palette.Frozen(sink ? Palette.Loading(0.7) : Palette.Voltage(1.02));

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
            Qin = Qout = Qloss = " - "; Qverdict = " - "; QverdictBrush = Brushes.Gray;
            Iin = Iout = " - "; Ploss = " - "; PlossPct = " - ";
        }

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
