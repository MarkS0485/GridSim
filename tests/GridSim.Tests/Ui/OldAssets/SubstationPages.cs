// TEST ORACLE (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/SubstationPages.cs exactly as it was at commit 6e729a66
// TEST ORACLE (2.0.46), before its page logic moved to src/GridSim.Ui/Assets/Pages. Only the using and namespace lines
// TEST ORACLE differ: the WPF types it named are the stand-ins in OldAssetsShims.cs (Brush, Color, Visibility, Palette,
// TEST ORACLE Fuels, Notify, Visuals.SubstationMapData). UiAssetPagesParityTests checks a SHA-256 of the rest against
// TEST ORACLE the original file.
using System.Collections.ObjectModel;
using System.Windows.Input;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldAssets;

/// <summary>One row in a substation's GDA per-asset drill-down (envelope / BESS / tap / consumer).</summary>
public sealed class AssetInsightRow
{
    public string Group { get; init; } = "";    // section header on the first row of each block, else ""
    public string Head { get; init; } = "";      // primary line
    public string Sub { get; init; } = "";        // secondary line
    public bool HasGroup => Group.Length > 0;
}

// The substations register treats every electrical bus as a substation: a node where
// demand sits, plant connects, and the solver lands a voltage. Everything on these
// pages is read straight off the live model + solved result  -  there is no demo data,
// because a substation's reality is already fully described by the network.

/// <summary>One bus's row on the substations fleet page.</summary>
public sealed class SubstationFleetRow : Notify
{
    public int BusId { get; init; }
    public string Name { get; init; } = "";
    public string Kv { get; init; } = "";
    public double SortKv { get; init; }            // BaseKv, for ordering
    public string Connections { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });

    private string _vm = " - "; public string Vm { get => _vm; set => Set(ref _vm, value); }
    private Brush _vmBrush = Brushes.Gray; public Brush VmBrush { get => _vmBrush; set => Set(ref _vmBrush, value); }
    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _gen = " - "; public string Gen { get => _gen; set => Set(ref _gen, value); }
    private double _sortDemand; public double SortDemand { get => _sortDemand; set => Set(ref _sortDemand, value); }
}

/// <summary>One toggle chip in the voltage-level filter row ("ALL", "400 kV", "132 kV", ...).</summary>
public sealed class KvFilterChip : Notify
{
    public string Label { get; init; } = "";
    public double Kv { get; init; }                // NaN = the "ALL" chip
    public ICommand Toggle { get; init; } = new RelayCommand(() => { });
    private bool _active; public bool Active { get => _active; set => Set(ref _active, value); }
}

/// <summary>
/// The substations fleet page: every bus in the running model, ordered by voltage
/// level then demand, with its live solved voltage, demand, generation and connection
/// count. A voltage-level chip row filters the register; the list is capped (GDA models
/// carry thousands of buses) with a "showing X of N" note.
/// </summary>
public sealed class SubstationFleetVm : AssetPageVm
{
    private const int RowCap = 200;

    private readonly AssetExplorerVm _explorer;
    private readonly Dictionary<int, SubstationFleetRow> _byId = new();
    private double _filterKv = double.NaN;          // NaN = no filter (ALL)

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "SUBSTATIONS";
    public override string Sub => "every electrical bus  -  voltage level, demand, connected plant, live solved voltage";

    public ObservableCollection<KvFilterChip> Filters { get; } = new();
    public ObservableCollection<SubstationFleetRow> Rows { get; } = new();

    private string _busCount = " - "; public string BusCount { get => _busCount; set => Set(ref _busCount, value); }
    private string _levels = " - "; public string Levels { get => _levels; set => Set(ref _levels, value); }
    private string _totalDemand = " - "; public string TotalDemand { get => _totalDemand; set => Set(ref _totalDemand, value); }
    private string _outOfBand = " - "; public string OutOfBand { get => _outOfBand; set => Set(ref _outOfBand, value); }
    private string _showing = ""; public string Showing { get => _showing; set => Set(ref _showing, value); }

    public SubstationFleetVm(AssetExplorerVm explorer) => _explorer = explorer;

    // The last frame shown, so a filter change re-filters at once (as the circuits page does) even while paused.
    private SimFrame? _frame;

    public override void Refresh(SimFrame frame)
    {
        _frame = frame;
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // solved voltage by bus id, and the chip set, are derived once per frame
        var vmById = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) vmById[r.BusIds[i]] = r.Vm[i];

        // generation and connection count per bus
        var genByBus = new Dictionary<int, double>();
        foreach (Generator g in m.Generators)
            genByBus[g.Bus] = genByBus.GetValueOrDefault(g.Bus) + g.Pg;
        var connByBus = new Dictionary<int, int>();
        foreach (Branch b in m.Branches)
        {
            connByBus[b.From] = connByBus.GetValueOrDefault(b.From) + 1;
            connByBus[b.To] = connByBus.GetValueOrDefault(b.To) + 1;
        }

        EnsureFilters(m);

        int outOfBand = 0;
        foreach (Bus bus in m.Buses)
        {
            double vm = vmById.GetValueOrDefault(bus.Id, double.NaN);
            // The shared voltage band (ScadaThresholds, the lamp's 0.94 / 1.06 pu), not a local copy of the literals.
            if (!double.IsNaN(vm) && (vm < GridSim.Core.Presentation.ScadaThresholds.VoltageBandLowPu
                                      || vm > GridSim.Core.Presentation.ScadaThresholds.VoltageBandHighPu)) outOfBand++;
        }

        // order the register: voltage level desc, then demand desc  -  biggest grid nodes first
        List<Bus> ordered = m.Buses
            .Where(b => double.IsNaN(_filterKv) || NearKv(b.BaseKv, _filterKv))
            .OrderByDescending(b => b.BaseKv)
            .ThenByDescending(b => b.Pd)
            .ToList();

        int shown = Math.Min(RowCap, ordered.Count);

        // rebuild only when the visible set changes (filter flip / model swap); otherwise
        // reuse the row objects in place so bindings stay live, exactly like the link fleet
        bool sameSet = Rows.Count == shown && Enumerable.Range(0, shown).All(i => Rows[i].BusId == ordered[i].Id);
        if (!sameSet)
        {
            Rows.Clear();
            _byId.Clear();
            for (int i = 0; i < shown; i++)
            {
                Bus bus = ordered[i];
                int id = bus.Id;
                var row = new SubstationFleetRow
                {
                    BusId = id,
                    Name = bus.Name ?? $"bus {id}",
                    Kv = bus.BaseKv > 0 ? $"{bus.BaseKv:F0} kV" : " - ",
                    SortKv = bus.BaseKv,
                    Connections = $"{connByBus.GetValueOrDefault(id)} conn",
                    Open = new RelayCommand(() => _explorer.OpenSubstation(id)),
                };
                _byId[id] = row;
                Rows.Add(row);
            }
        }

        foreach (Bus bus in ordered.Take(shown))
        {
            if (!_byId.TryGetValue(bus.Id, out SubstationFleetRow? row)) continue;
            double vm = vmById.GetValueOrDefault(bus.Id, double.NaN);
            row.Vm = double.IsNaN(vm) ? " - " : $"{vm:F3}";
            row.VmBrush = double.IsNaN(vm) ? Brushes.Gray : Palette.Frozen(Palette.Voltage(vm));
            row.Demand = $"{bus.Pd:N0} MW";
            row.SortDemand = bus.Pd;
            double bg = genByBus.GetValueOrDefault(bus.Id);
            row.Gen = bg > 0.5 ? $"{bg:N0} MW" : " - ";
        }

        BusCount = m.BusCount.ToString("N0");
        Levels = DistinctKvs(m).Count.ToString();
        TotalDemand = $"{m.Buses.Sum(b => b.Pd) / 1000.0:F2}";
        OutOfBand = outOfBand.ToString();
        Showing = ordered.Count > shown ? $"showing {shown:N0} of {ordered.Count:N0}" : $"{shown:N0} substations";
    }

    private void EnsureFilters(GridModel m)
    {
        if (Filters.Count > 0) return;
        Filters.Add(MakeChip("ALL", double.NaN, active: true));
        foreach (double kv in DistinctKvs(m))
            Filters.Add(MakeChip($"{kv:F0} kV", kv, active: false));
    }

    private KvFilterChip MakeChip(string label, double kv, bool active)
    {
        KvFilterChip chip = null!;
        chip = new KvFilterChip
        {
            Label = label,
            Kv = kv,
            Active = active,
            Toggle = new RelayCommand(() => SelectFilter(chip)),
        };
        return chip;
    }

    private void SelectFilter(KvFilterChip pick)
    {
        _filterKv = pick.Kv;
        foreach (KvFilterChip c in Filters) c.Active = ReferenceEquals(c, pick);
        // Re-filter now: the chip used to light while the rows waited for the next frame, which on a paused live view
        // or a paused replay never came.
        if (_frame is not null) Refresh(_frame);
    }

    private static List<double> DistinctKvs(GridModel m) =>
        m.Buses.Select(b => b.BaseKv).Where(kv => kv > 0)
            .Distinct().OrderByDescending(kv => kv).ToList();

    private static bool NearKv(double a, double b) => Math.Abs(a - b) < 0.5;
}

/// <summary>One generator's row in a substation's connected-plant list.</summary>
public sealed class PlantRow : Notify
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });   // -> the generator's detail page
    private string _fuel = ""; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    private Brush _fuelBrush = Brushes.Gray; public Brush FuelBrush { get => _fuelBrush; set => Set(ref _fuelBrush, value); }
    private string _output = " - "; public string Output { get => _output; set => Set(ref _output, value); }
    /// <summary>The unit's output (MW) as a number, for the single-line diagram. Not bound.</summary>
    internal double OutputMw;
}

/// <summary>One branch's row in a substation's connections list.</summary>
public sealed class ConnectionRow : Notify
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";        // TRANSFORMER / LINE
    public ICommand Open { get; init; } = new RelayCommand(() => { });   // -> the circuit/transformer detail page
    private string _otherEnd = " - "; public string OtherEnd { get => _otherEnd; set => Set(ref _otherEnd, value); }
    private string _flow = " - "; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _direction = " - "; public string Direction { get => _direction; set => Set(ref _direction, value); }
    private string _loading = " - "; public string Loading { get => _loading; set => Set(ref _loading, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }
    /// <summary>|P| at this end (MW) as a number, and whether power leaves the bus into the branch; for the
    /// single-line diagram. Not bound.</summary>
    internal double FlowMw;
    internal bool Exporting;
}

/// <summary>
/// One substation's dashboard, keyed by bus id: live solved voltage (with a recent-history
/// sparkline), angle, demand, the generation landing on the bus, its bus type and base
/// voltage. "CONNECTED PLANT" lists the generators here; "CONNECTIONS" lists every branch
/// touching the bus with its live loading and signed flow direction. A "SUPPLY CONTEXT"
/// card carries derived facts  -  net injection and the transformer/line mix at the node.
/// Both lists reuse their row objects by key across frames, like the interconnector page.
/// </summary>
public sealed class SubstationDetailVm : AssetPageVm
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps
    private const double BarTrack = 220.0;

    private readonly int _busId;
    private readonly AssetExplorerVm _explorer;

    /// <summary>The bus this page describes  -  lets the shell match a returned asset query to the open page.</summary>
    public int BusId => _busId;

    public override string Eyebrow => "SUBSTATION";
    public override string Title => _title;
    public override string Sub => _sub;

    private string _title; private string _sub;

    public SubstationDetailVm(int busId, AssetExplorerVm explorer)
    {
        _busId = busId;
        _explorer = explorer;
        _title = $"BUS {busId}";
        _sub = "electrical bus  -  live voltage, demand and connected plant";
    }

    private string _voltage = " - "; public string Voltage { get => _voltage; set => Set(ref _voltage, value); }
    private Brush _voltageBrush = Brushes.Gray; public Brush VoltageBrush { get => _voltageBrush; set => Set(ref _voltageBrush, value); }
    private string _angle = " - "; public string Angle { get => _angle; set => Set(ref _angle, value); }
    private string _demand = " - "; public string Demand { get => _demand; set => Set(ref _demand, value); }
    private string _reactive = " - "; public string Reactive { get => _reactive; set => Set(ref _reactive, value); }
    private string _generation = " - "; public string Generation { get => _generation; set => Set(ref _generation, value); }
    private string _busType = " - "; public string BusType { get => _busType; set => Set(ref _busType, value); }
    private string _baseKv = " - "; public string BaseKv { get => _baseKv; set => Set(ref _baseKv, value); }

    // SUPPLY CONTEXT  -  derived facts
    private string _netInjection = " - "; public string NetInjection { get => _netInjection; set => Set(ref _netInjection, value); }
    private string _transformers = " - "; public string Transformers { get => _transformers; set => Set(ref _transformers, value); }
    private string _lines = " - "; public string Lines { get => _lines; set => Set(ref _lines, value); }
    private string _role = " - "; public string Role { get => _role; set => Set(ref _role, value); }

    public ObservableCollection<PlantRow> Plant { get; } = new();
    private readonly Dictionary<string, PlantRow> _plantByKey = new();
    public ObservableCollection<ConnectionRow> Connections { get; } = new();
    private readonly Dictionary<string, ConnectionRow> _connByKey = new();

    private string _plantNote = ""; public string PlantNote { get => _plantNote; set => Set(ref _plantNote, value); }
    private string _connNote = ""; public string ConnNote { get => _connNote; set => Set(ref _connNote, value); }

    // ---- GDA per-asset drill-down (task T-J): real envelope / BESS / tap / consumer
    // data pulled on demand from the bridge's op="asset" and applied by the shell. In
    // live/replay (non-bridge) mode the card stays hidden - there is no per-asset feed. ----
    public ObservableCollection<AssetInsightRow> AssetInsights { get; } = new();
    private Visibility _assetVis = Visibility.Collapsed;
    public Visibility AssetVis { get => _assetVis; private set => Set(ref _assetVis, value); }
    private string _assetStatus = ""; public string AssetStatus { get => _assetStatus; private set => Set(ref _assetStatus, value); }

    /// <summary>
    /// Replace the placeholder-free asset card with the real per-asset blocks returned by the bridge's
    /// <c>op="asset"</c> query for this bus. Called by the shell on the UI thread; blocks the unit lacks are
    /// simply absent. A <c>null</c>/empty <paramref name="asset"/> shows only the status line (the graceful
    /// fallback when a bus has no GDA per-asset data).
    /// </summary>
    public void ApplyAsset(AssetData? asset, string status)
    {
        AssetInsights.Clear();
        AssetStatus = status;
        AssetVis = Visibility.Visible;
        if (asset is null) return;

        // N063: the row text now comes from the shared Core formatter (moved there text for text from this class),
        // so the web substation page shows exactly the same rows.
        foreach (GridSim.Core.Presentation.AssetInsightFormatter.Row r in GridSim.Core.Presentation.AssetInsightFormatter.Rows(asset))
            AssetInsights.Add(new AssetInsightRow { Group = r.Group, Head = r.Head, Sub = r.Sub });
    }

    // --- schematic map data ---
    private Visuals.SubstationMapData? _mapData;
    public Visuals.SubstationMapData? MapData { get => _mapData; private set => Set(ref _mapData, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    public override void Refresh(SimFrame frame)
    {
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == _busId);
        if (bus is null)
        {
            // Say so plainly, as the generator, circuit, transformer and interconnector pages do, rather than keep
            // showing the last model's numbers as if live.
            _sub = "this bus is not in the current model (model changed?)  -  values below are stale";
            Raise(nameof(Sub));
            return;
        }

        _title = bus.Name ?? $"bus {_busId}";
        _sub = bus.BaseKv > 0 ? $"{bus.BaseKv:F0} kV electrical bus" : "electrical bus";
        Raise(nameof(Title));
        Raise(nameof(Sub));

        // solved state at this bus
        double vm = double.NaN, vaDeg = 0, qInj = 0, pInj = 0;
        for (int i = 0; i < r.BusIds.Count; i++)
            if (r.BusIds[i] == _busId)
            {
                vm = r.Vm[i]; vaDeg = r.VaDeg[i]; pInj = r.PInjMw[i]; qInj = r.QInjMvar[i];
                break;
            }

        Voltage = double.IsNaN(vm) ? " - " : $"{vm:F3}";
        VoltageBrush = double.IsNaN(vm) ? Brushes.Gray : Palette.Frozen(Palette.Voltage(vm));
        Angle = $"{vaDeg:+0.0;-0.0;0.0} deg";
        Demand = $"{bus.Pd:N0}";
        Reactive = $"{bus.Qd:N0}";
        BusType = bus.Type switch { GridSim.Core.Model.BusType.Slack => "SLACK", GridSim.Core.Model.BusType.Pv => "PV", _ => "PQ" };
        BaseKv = bus.BaseKv > 0 ? $"{bus.BaseKv:F0}" : " - ";

        double busGen = m.Generators.Where(g => g.Bus == _busId).Sum(g => g.Pg);
        Generation = $"{busGen:N0}";

        // SUPPLY CONTEXT
        NetInjection = $"{pInj:+#,0;-#,0;0} MW";
        Role = pInj > 5 ? "net source" : pInj < -5 ? "net sink" : "balanced";

        // ---- CONNECTED PLANT: generators landing on this bus ----
        // Rows are keyed by generator INDEX plus name: two namesakes on one bus (gb-full has them) used to share
        // one row, and the drill opened the first namesake (R055).
        var plantKeys = new HashSet<string>();
        IReadOnlyList<Generator> allGens = m.Generators;
        for (int gi = 0; gi < allGens.Count; gi++)
        {
            Generator g = allGens[gi];
            if (g.Bus != _busId) continue;
            string key = $"{gi}:{g.Name}";
            plantKeys.Add(key);
            FuelInfo fuel = Fuels.Classify(g.Type);
            if (!_plantByKey.TryGetValue(key, out PlantRow? row))
            {
                string genName = g.Name ?? $"unit @ bus {_busId}";   // matches GeneratorDetailVm's title
                int unitIndex = gi, unitBus = g.Bus;
                string? modelName = g.Name;
                row = new PlantRow
                {
                    Key = key,
                    Name = genName,
                    Open = new RelayCommand(() => _explorer.OpenGenerator(unitIndex, unitBus, modelName)),
                };
                _plantByKey[key] = row;
                Plant.Add(row);
            }
            row.Fuel = fuel.Name;
            row.FuelBrush = Palette.Frozen(fuel.Color);
            row.Output = $"{g.Pg:N0} MW";
            row.OutputMw = g.Pg;   // the number itself, for the single-line diagram (never parsed back from the text)
        }
        Prune(Plant, _plantByKey, plantKeys);
        PlantNote = Plant.Count == 0 ? "no generation connects to this bus" : $"{Plant.Count} unit(s)";

        // ---- CONNECTIONS: every branch touching this bus ----
        int transformers = 0, lines = 0;
        var connKeys = new HashSet<string>();
        for (int bi = 0; bi < m.Branches.Count; bi++)
        {
            Branch br = m.Branches[bi];
            bool fromHere = br.From == _busId, toHere = br.To == _busId;
            if (!fromHere && !toHere) continue;

            if (br.IsTransformer) transformers++; else lines++;
            int otherId = fromHere ? br.To : br.From;
            // N258 (October 2026): one row per BRANCH, keyed by its model index plus its ends and name (as the plant rows
            // are keyed by generator index, R055). Rows were keyed by branch name, so parallel namesakes (two circuits of
            // one double-circuit route, or a GDA zone's repeated names) shared a single row, which showed the last one's
            // flow and opened the first one's page.
            string key = $"{bi}:{br.From}-{br.To}:{br.Name}";
            connKeys.Add(key);

            if (!_connByKey.TryGetValue(key, out ConnectionRow? row))
            {
                Branch branch = br;   // capture the value for the click-through
                row = new ConnectionRow
                {
                    Key = key,
                    Name = br.Name ?? $"{br.From}->{br.To}",
                    Kind = br.IsTransformer ? "TRANSFORMER" : "LINE",
                    Open = new RelayCommand(() => _explorer.OpenBranch(branch)),
                };
                _connByKey[key] = row;
                Connections.Add(row);
            }

            Bus? other = m.Buses.FirstOrDefault(b => b.Id == otherId);
            row.OtherEnd = other?.Name ?? $"bus {otherId}";

            // live flow at this end: LineFlows align with branch index
            LineFlow? lf = bi < r.LineFlows.Count ? r.LineFlows[bi] : null;
            double pHere = lf is null ? 0 : (fromHere ? lf.PFromMw : lf.PToMw);
            bool exporting = pHere > 1;   // power leaving this bus into the branch
            row.Flow = $"{Math.Abs(pHere):N0} MW";
            row.FlowMw = Math.Abs(pHere);   // the number itself, for the single-line diagram
            row.Exporting = exporting;
            row.Direction = exporting ? "OUT ->" : pHere < -1 ? "<- IN" : " - ";

            double frac = lf is { RatingMva: > 0 } ? lf.LoadingPercent / 100.0 : 0;
            row.Loading = lf is { RatingMva: > 0 } ? $"{lf.LoadingPercent:F0}%" : "unrated";
            row.BarWidth = Math.Min(1.0, frac) * BarTrack;
            row.Bar = Palette.Frozen(frac > 0 ? Palette.Loading(frac) : Palette.Voltage(0.9));
        }
        Prune(Connections, _connByKey, connKeys);
        Transformers = transformers.ToString();
        Lines = lines.ToString();
        ConnNote = Connections.Count == 0 ? "islanded  -  no branches touch this bus" : $"{Connections.Count} connection(s)";

        // build the schematic map snapshot from the rows' NUMBERS. Flow and output used to be parsed back out of the
        // display strings ("1,234 MW"), so any change of display format (or culture) would silently draw 0 MW.
        var mapConns = new List<Visuals.MapConnection>();
        foreach (ConnectionRow cr in Connections)
        {
            double lf = cr.BarWidth / BarTrack;
            mapConns.Add(new Visuals.MapConnection(cr.Name, cr.OtherEnd, cr.Kind, cr.FlowMw, cr.Exporting, lf));
        }
        var mapGens = new List<Visuals.MapGenerator>();
        foreach (PlantRow pr in Plant)
        {
            Color fc = pr.FuelBrush is SolidColorBrush scb ? scb.Color : Colors.Gray;
            mapGens.Add(new Visuals.MapGenerator(pr.Name, pr.Fuel, fc, pr.OutputMw));
        }
        MapData = new Visuals.SubstationMapData
        {
            VoltagePu = double.IsNaN(vm) ? 1.0 : vm,
            AngleDeg = vaDeg,
            BaseKv = bus.BaseKv,
            DemandMw = bus.Pd,
            ReactiveMvar = bus.Qd,
            GenerationMw = busGen,
            BusType = BusType,
            NetInjectionMw = pInj,
            Connections = mapConns,
            Generators = mapGens,
        };

        // voltage history sparkline: only the samples actually seen since the page opened. It used to be seeded with 95
        // copies of the first reading, which drew an invented flat day and a half of history.
        if (!double.IsNaN(vm))
        {
            _history.Add(vm);
            if (_history.Count > HistoryLength) _history.RemoveAt(0);
            Trace = _history.ToArray();
        }
    }

    /// <summary>Drop rows whose key no longer appears this frame (the live set shrank).</summary>
    private static void Prune<T>(ObservableCollection<T> rows, Dictionary<string, T> byKey, HashSet<string> live)
        where T : class
    {
        if (rows.Count == live.Count) return;
        foreach (string stale in byKey.Keys.Where(k => !live.Contains(k)).ToList())
        {
            rows.Remove(byKey[stale]);
            byKey.Remove(stale);
        }
    }
}
