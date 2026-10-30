// TEST ORACLE (UI programme ASSETS A2): src/GridSim.Wpf/Views/Assets/AssetPages.cs exactly as it was at commit 6e729a66
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
using GridSim.Ui.Presentation;

namespace GridSim.Tests.Ui.OldAssets;

/// <summary>Minimal ICommand for asset-page navigation.</summary>
public sealed class RelayCommand(Action exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec();
}

/// <summary>ICommand that passes its <c>CommandParameter</c> through to the handler  -  used for
/// cross-links whose target id changes per frame (the parameter binds to a live VM property).</summary>
public sealed class RelayCommand<T>(Action<T> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) { if (p is T t) exec(t); }
}

/// <summary>
/// One page of the asset explorer. Pages form a drill-down (home -> fleet -> unit);
/// <see cref="Refresh"/> is called with every solved frame, so a page that binds the
/// live model stays time-aligned with the running sim (and later, a replay).
/// </summary>
public abstract class AssetPageVm : Notify
{
    public abstract string Eyebrow { get; }
    public abstract string Title { get; }
    public virtual string Sub => "";
    public virtual void Refresh(SimFrame frame) { }
}

/// <summary>
/// The explorer shell: a navigation stack of <see cref="AssetPageVm"/>s under a
/// header with back/home and the sim-time strip. The whole thing is model-driven  - 
/// views are matched to page VMs by DataTemplate.
/// </summary>
public sealed class AssetExplorerVm : Notify
{
    private readonly Stack<AssetPageVm> _stack = new();
    private SimFrame? _frame;
    private bool _active = true;

    /// <summary>Set by the shell when the ASSETS tab is the selected/visible tab. While false the
    /// per-frame Refresh work is skipped entirely (the tab is off-screen, so there is nothing to
    /// paint). Defaults true so the explorer still works if the shell never wires it; wire it from
    /// the tab's selection/visibility to skip the solve-time work on the other four tabs.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (_active && _frame is not null) Current.Refresh(_frame);   // catch up on show
        }
    }

    public AssetHomeVm Home { get; }
    public ICommand BackCmd { get; }
    public ICommand HomeCmd { get; }

    public AssetExplorerVm()
    {
        Home = new AssetHomeVm(this);
        _current = Home;
        BackCmd = new RelayCommand(Back);
        HomeCmd = new RelayCommand(GoHome);
    }

    private AssetPageVm _current;
    public AssetPageVm Current { get => _current; private set { Set(ref _current, value); Raise(nameof(BackVis)); Raise(nameof(Crumb)); } }

    public Visibility BackVis => _stack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Crumb => string.Join("    ", _stack.Reverse().Select(p => p.Title).Append(Current.Title));

    private string _timeLabel = " - "; public string TimeLabel { get => _timeLabel; set => Set(ref _timeLabel, value); }

    public void Open(AssetPageVm page)
    {
        _stack.Push(Current);
        Current = page;
        if (_frame is not null) page.Refresh(_frame);
    }

    public void Back() { if (_stack.Count > 0) { Current = _stack.Pop(); if (_frame is not null) Current.Refresh(_frame); } }

    public void GoHome() { _stack.Clear(); Current = Home; if (_frame is not null) Current.Refresh(_frame); }

    // ---- cross-navigation: open any related entity's detail page in this same stack ----
    // This is what turns the explorer into a navigable graph: a cable links to its
    // substations, a substation to its plant and circuits, a generator back to its bus.
    public void OpenSubstation(int busId) => Open(new SubstationDetailVm(busId, this));
    // Generators and transformers are opened by model INDEX plus the identifying fields (bus / ends and model
    // name), never by display name alone: gb-full repeats names, and name lookup always opened the first
    // namesake (R055). Circuits too since D-DOC-146 (they were opened by name, so a repeated circuit name
    // always opened the first namesake, and an unnamed circuit never resolved at all), and interconnectors since N258
    // (gb-full has two links both named "Auchencrosh (interconnector CCT) interconnector"; the second opened as the first).
    public void OpenGenerator(int index, int bus, string? name) => Open(new GeneratorDetailVm(index, bus, name, this));
    public void OpenInterconnector(int index, int bus, string? name) => Open(new InterconnectorDetailVm(index, bus, name, this));
    public void OpenCircuit(int index, GridSim.Core.Model.Branch br) =>
        Open(new CircuitDetailVm(index, br.From, br.To, br.Name, this));
    public void OpenTransformer(int index, GridSim.Core.Model.Branch br) =>
        Open(new TransformerDetailVm(index, br.From, br.To, br.Name, this));

    /// <summary>Open the transformer an OLTC regulates. The model links an OLTC to its transformer BY NAME
    /// (<c>Oltc.Transformer</c>), so this takes the first transformer branch carrying that name in the current
    /// frame, the same one the OLTC control acts on, and opens it by index. With no such branch the page opens
    /// and says the transformer is not in the current model.</summary>
    public void OpenTransformerNamed(string name)
    {
        if (_frame is not null)
        {
            IReadOnlyList<GridSim.Core.Model.Branch> branches = _frame.Model.Branches;
            for (int i = 0; i < branches.Count; i++)
                if (branches[i].IsTransformer && branches[i].Name == name) { OpenTransformer(i, branches[i]); return; }
        }
        Open(new TransformerDetailVm(-1, 0, 0, name, this));
    }

    /// <summary>Open a branch's detail  -  a transformer page if it's a transformer, else a corridor page.
    /// The branch's index is found by reference in the current frame's model; when the frame has moved on
    /// (index -1) the page resolves the branch by its ends and name instead (both page kinds, D-DOC-146).</summary>
    public void OpenBranch(GridSim.Core.Model.Branch br)
    {
        int index = -1;
        if (_frame is not null)
        {
            IReadOnlyList<GridSim.Core.Model.Branch> branches = _frame.Model.Branches;
            for (int i = 0; i < branches.Count; i++)
                if (ReferenceEquals(branches[i], br)) { index = i; break; }
        }
        if (br.IsTransformer) OpenTransformer(index, br);
        else OpenCircuit(index, br);
    }

    /// <summary>Every solved frame lands here; the visible page re-reads the live model.</summary>
    public void Update(SimFrame frame, string clock)
    {
        _frame = frame;
        TimeLabel = $"SIM {clock}  TICK {frame.Tick}";
        if (!_active) return;            // tab off-screen: nothing to paint, skip the solve-time work
        // Only the visible page refreshes. Home used to be refreshed every frame while a detail page
        // was open ("keep counts fresh behind the scenes")  -  but it isn't on screen then, and its
        // Refresh is the heaviest one. Navigation (Open/Back/GoHome) refreshes the page it reveals,
        // so Home is fresh the instant it becomes visible again.
        Current.Refresh(frame);
    }
}

/// <summary>One asset-class summary card on the explorer landing page  -  a live, drill-down tile.</summary>
public sealed class AssetClassCard : Notify
{
    public const double BarTrack = 220.0;

    public string Title { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Detail { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }

    // ---- live headline metric (the thing that makes the card earn its space) ----
    private string _metric = " - "; public string Metric { get => _metric; set => Set(ref _metric, value); }
    private string _metricUnit = ""; public string MetricUnit { get => _metricUnit; set => Set(ref _metricUnit, value); }
    private string _metricLabel = ""; public string MetricLabel { get => _metricLabel; set => Set(ref _metricLabel, value); }
    private Brush _metricBrush = Brushes.Gray; public Brush MetricBrush { get => _metricBrush; set => Set(ref _metricBrush, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
}

/// <summary>The landing page: every asset class in the running model, with live counts.</summary>
public sealed class AssetHomeVm : AssetPageVm
{
    public override string Eyebrow => "ASSET EXPLORER";
    public override string Title => "ASSETS";
    public override string Sub => "every asset class in the running model  -  drill down to the unit";

    public ObservableCollection<AssetClassCard> Classes { get; } = new();

    private readonly AssetClassCard _generators, _phasors, _interconnectors, _transformers, _circuits, _substations, _oltcs;

    public AssetHomeVm(AssetExplorerVm explorer)
    {
        Classes.Add(_generators = new AssetClassCard
        {
            Title = "GENERATORS",
            Tag = "FLEET",
            Detail = "thermal  nuclear  hydro  wind  solar  -  by fuel, down to the unit",
            Open = new RelayCommand(() => explorer.Open(new GeneratorFleetVm(explorer))),
        });
        Classes.Add(_phasors = new AssetClassCard
        {
            Title = "GENERATOR PHASORS",
            Tag = "LIVE",
            Detail = "every generator's voltage-current phasor  -  phase angle from the solved power flow",
            Open = new RelayCommand(() => explorer.Open(new GeneratorPhasorFleetVm(explorer))),
        });
        Classes.Add(_interconnectors = new AssetClassCard
        {
            Title = "INTERCONNECTORS",
            Tag = "LIVE",
            Detail = "the GB import links  -  signed flow, capacity, landing site",
            Open = new RelayCommand(() => explorer.Open(new InterconnectorFleetVm(explorer))),
        });
        Classes.Add(_transformers = new AssetClassCard
        {
            Title = "TRANSFORMERS",
            Tag = "FLEET",
            Detail = "GSP and grid transformers by voltage band, OLTC state per unit",
            Open = new RelayCommand(() => explorer.Open(new TransformerFleetVm(explorer))),
        });
        Classes.Add(_circuits = new AssetClassCard
        {
            Title = "CABLES & LINES",
            Tag = "FLEET",
            Detail = "transmission corridors and circuits  -  loading, rating, route",
            Open = new RelayCommand(() => explorer.Open(new CircuitFleetVm(explorer))),
        });
        Classes.Add(_substations = new AssetClassCard
        {
            Title = "SUBSTATIONS",
            Tag = "FLEET",
            Detail = "every electrical bus  -  voltage, demand, connected plant",
            Open = new RelayCommand(() => explorer.Open(new SubstationFleetVm(explorer))),
        });
        Classes.Add(_oltcs = new AssetClassCard
        {
            Title = "TAP CHANGERS",
            Tag = "FLEET",
            Detail = "on-load tap changers and the buses they regulate",
            Open = new RelayCommand(() => explorer.Open(new TapChangerFleetVm(explorer))),
        });
    }

    public override void Refresh(SimFrame frame)
    {
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // O(1) lookups built once per refresh. Previously every OLTC scanned all bus ids  -  O(n^2) across the model
        // each frame. Branch flows are read BY INDEX (AssetLookup.FlowAt: LineFlows is index-aligned with Branches), not
        // through a name dictionary: names repeat (gb-full has 58 duplicated branch names) and unnamed branches have none,
        // so a name lookup read the first namesake's loading for every namesake and skipped unnamed circuits entirely.
        var vpuById = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++)
            vpuById.TryAdd(r.BusIds[i], r.Vm[i]);                              // first match wins, as VpuOf did

        // ---- counts (unchanged) ----
        _generators.Count = m.Generators.Count(g => Fuels.Classify(g.Type).Class != FuelClass.Interconnector).ToString();
        _interconnectors.Count = m.Generators.Count(g => Fuels.Classify(g.Type).Class == FuelClass.Interconnector).ToString();
        _transformers.Count = m.Branches.Count(b => b.IsTransformer).ToString();
        _circuits.Count = m.Branches.Count(b => !b.IsTransformer).ToString();
        _substations.Count = m.BusCount.ToString();
        _oltcs.Count = m.Oltcs.Count.ToString();

        // ---- GENERATORS: total dispatched output, GW (non-interconnector plant) ----
        double genPg = 0, genCap = 0;
        foreach (Generator g in m.Generators)
        {
            if (Fuels.Classify(g.Type).Class == FuelClass.Interconnector) continue;
            genPg += g.Pg;
            if (!double.IsInfinity(g.Pmax)) genCap += g.Pmax;
        }
        _generators.Metric = $"{genPg / 1000.0:F1}";
        _generators.MetricUnit = " GW";
        _generators.MetricLabel = "DISPATCHED OUTPUT";
        _generators.MetricBrush = AccentBrush;
        _generators.BarWidth = genCap > 0 ? Math.Min(1.0, genPg / genCap) * AssetClassCard.BarTrack : 0;

        // ---- GENERATOR PHASORS: angle spread across the fleet ----
        double minVa = double.MaxValue, maxVa = double.MinValue;
        for (int i = 0; i < r.VaDeg.Length; i++)
        {
            if (r.VaDeg[i] < minVa) minVa = r.VaDeg[i];
            if (r.VaDeg[i] > maxVa) maxVa = r.VaDeg[i];
        }
        double spread = minVa < double.MaxValue ? maxVa - minVa : 0;
        _phasors.Count = _generators.Count;
        _phasors.Metric = spread > 0 ? $"{spread:F1}" : " - ";
        _phasors.MetricUnit = spread > 0 ? " deg" : "";
        _phasors.MetricLabel = "ANGLE SPREAD";
        _phasors.MetricBrush = Palette.Frozen(spread > 30 ? Palette.Loading(0.9) : spread > 15 ? Palette.Loading(0.6) : Palette.Voltage(1.0));
        _phasors.BarWidth = Math.Min(1.0, spread / 60.0) * AssetClassCard.BarTrack;

        // ---- INTERCONNECTORS: net flow, GW, signed (import +) ----
        double icCap = 0, icNet = 0;
        foreach (Generator g in m.Generators)
        {
            if (Fuels.Classify(g.Type).Class != FuelClass.Interconnector) continue;
            icCap += g.Pmax;
            icNet += g.Pg;
        }
        // The shared idle cut-off (StatusLampRules, 0.5 MW), the same one the GENERATION tab's import-links panel uses,
        // so the two tabs never disagree on whether the links are importing. This used its own 5 MW literal.
        bool icIdle = StatusLampRules.IsIdleInterconnector(icNet);
        bool importing = !icIdle && icNet > 0, exporting = !icIdle && icNet < 0;
        _interconnectors.Metric = $"{icNet / 1000.0:+0.00;-0.00;0.00}";
        _interconnectors.MetricUnit = " GW";
        _interconnectors.MetricLabel = importing ? "NET IMPORT" : exporting ? "NET EXPORT" : "BALANCED";
        _interconnectors.MetricBrush = Palette.Frozen(importing ? Palette.Voltage(1.0) : exporting ? Palette.Loading(0.8) : Palette.Voltage(0.9));
        _interconnectors.BarWidth = icCap > 0 ? Math.Min(1.0, Math.Abs(icNet) / icCap) * AssetClassCard.BarTrack : 0;

        // ---- TRANSFORMERS: mean loading % across rated transformer branches ----
        double txSum = 0; int txN = 0;
        for (int bi = 0; bi < m.Branches.Count; bi++)
        {
            Branch b = m.Branches[bi];
            if (!b.IsTransformer || b.RateAMva <= 0) continue;
            LineFlow? f = AssetLookup.FlowAt(r, bi);   // by index: every namesake and every unnamed branch counts
            if (f is { RatingMva: > 0 }) { txSum += f.LoadingPercent; txN++; }
        }
        double txMean = txN > 0 ? txSum / txN : 0;
        _transformers.Metric = txN > 0 ? $"{txMean:F0}" : " - ";
        _transformers.MetricUnit = txN > 0 ? "%" : "";
        _transformers.MetricLabel = "MEAN LOADING";
        _transformers.MetricBrush = Palette.Frozen(Palette.Loading(txMean / 100.0));
        _transformers.BarWidth = Math.Min(1.0, txMean / 100.0) * AssetClassCard.BarTrack;

        // ---- CABLES & LINES: highest circuit loading % (non-transformer branches) ----
        double cktMax = 0; bool cktAny = false;
        for (int bi = 0; bi < m.Branches.Count; bi++)
        {
            Branch b = m.Branches[bi];
            if (b.IsTransformer || b.RateAMva <= 0) continue;
            LineFlow? f = AssetLookup.FlowAt(r, bi);   // by index, so an overloaded second circuit of a shared name counts
            if (f is { RatingMva: > 0 }) { cktAny = true; if (f.LoadingPercent > cktMax) cktMax = f.LoadingPercent; }
        }
        _circuits.Metric = cktAny ? $"{cktMax:F0}" : " - ";
        _circuits.MetricUnit = cktAny ? "%" : "";
        _circuits.MetricLabel = "PEAK CIRCUIT LOADING";
        _circuits.MetricBrush = Palette.Frozen(Palette.Loading(cktMax / 100.0));
        _circuits.BarWidth = Math.Min(1.0, cktMax / 100.0) * AssetClassCard.BarTrack;

        // ---- SUBSTATIONS: buses outside the shared 0.94-1.06 pu voltage band (ScadaThresholds, the lamp's band) ----
        int outOfBand = 0;
        for (int i = 0; i < r.Vm.Length; i++)
            if (r.Vm[i] < ScadaThresholds.VoltageBandLowPu || r.Vm[i] > ScadaThresholds.VoltageBandHighPu) outOfBand++;
        bool clean = outOfBand == 0;
        _substations.Metric = outOfBand.ToString();
        _substations.MetricUnit = "";
        _substations.MetricLabel = clean ? "ALL IN VOLTAGE BAND" : "BUSES OUT OF BAND";
        _substations.MetricBrush = Palette.Frozen(clean ? Palette.Voltage(1.0) : Palette.Loading(1.1));
        _substations.BarWidth = m.BusCount > 0 ? Math.Min(1.0, outOfBand / (double)m.BusCount) * AssetClassCard.BarTrack : 0;

        // ---- TAP CHANGERS: how many OLTCs hold their bus within deadband of target ----
        int inBand = 0;
        foreach (Oltc o in m.Oltcs)
        {
            double? live = vpuById.TryGetValue(o.ControlsBus, out double lv) ? lv : null;
            if (live is double v && Math.Abs(v - o.TargetVpu) <= o.DeadbandPu) inBand++;
        }
        int oltcN = m.Oltcs.Count;
        bool allHeld = oltcN > 0 && inBand == oltcN;
        _oltcs.Metric = oltcN > 0 ? $"{inBand} / {oltcN}" : " - ";
        _oltcs.MetricUnit = "";
        _oltcs.MetricLabel = "ON-TARGET (IN DEADBAND)";
        _oltcs.MetricBrush = Palette.Frozen(allHeld ? Palette.Voltage(1.0) : Palette.Loading(0.7));
        _oltcs.BarWidth = oltcN > 0 ? inBand / (double)oltcN * AssetClassCard.BarTrack : 0;
    }

    private static readonly Brush AccentBrush = Palette.Frozen(Palette.FromHex("#21C7E6"));
}

/// <summary>One link's row on the interconnector fleet page.</summary>
public sealed class LinkFleetRow : Notify
{
    public string Name { get; init; } = "";
    public string Route { get; init; } = "";
    public string Capacity { get; init; } = "";
    public ICommand Open { get; init; } = new RelayCommand(() => { });
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _badge = ""; public string Badge { get => _badge; set => Set(ref _badge, value); }
    private string _util = ""; public string Util { get => _util; set => Set(ref _util, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private Brush _bar = Brushes.Gray; public Brush Bar { get => _bar; set => Set(ref _bar, value); }
}

/// <summary>The interconnector fleet page: every link in the model, click-through to the unit.</summary>
public sealed class InterconnectorFleetVm(AssetExplorerVm explorer) : AssetPageVm
{
    public const double BarTrack = 240.0;

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "INTERCONNECTORS";
    public override string Sub => "HVDC links into the GB system  -  signed flow, positive importing";

    public ObservableCollection<LinkFleetRow> Links { get; } = new();
    // N258 (October 2026): rows keyed by the unit's model INDEX plus its bus and name, as the substation's plant rows are
    // (R055). They were keyed by name, so two links sharing a name (gb-full's two Auchencrosh links) shared ONE row, which
    // showed the last one's flow and opened the first one's page.
    private readonly Dictionary<string, LinkFleetRow> _byKey = new();

    private string _totalCap = " - "; public string TotalCap { get => _totalCap; set => Set(ref _totalCap, value); }
    private string _netFlow = " - "; public string NetFlow { get => _netFlow; set => Set(ref _netFlow, value); }
    private string _importing = " - "; public string Importing { get => _importing; set => Set(ref _importing, value); }
    private string _largest = " - "; public string Largest { get => _largest; set => Set(ref _largest, value); }

    public override void Refresh(SimFrame frame)
    {
        double cap = 0, net = 0, biggest = 0;
        int importing = 0, count = 0;
        string biggestName = " - ";
        var seen = new HashSet<string>();

        IReadOnlyList<Generator> fleet = frame.Model.Generators;
        for (int gi = 0; gi < fleet.Count; gi++)
        {
            Generator g = fleet[gi];
            if (Fuels.Classify(g.Type).Class != FuelClass.Interconnector) continue;
            string name = g.Name ?? $"link @ bus {g.Bus}";
            string key = $"{gi}:{g.Bus}:{g.Name}";
            seen.Add(key);
            count++;
            cap += g.Pmax;
            net += g.Pg;
            // The shared idle cut-off (StatusLampRules, 0.5 MW), as the GENERATION tab uses; this was a 5 MW literal.
            bool linkIdle = StatusLampRules.IsIdleInterconnector(g.Pg);
            if (!linkIdle && g.Pg > 0) importing++;
            if (Math.Abs(g.Pg) > Math.Abs(biggest)) { biggest = g.Pg; biggestName = name; }

            if (!_byKey.TryGetValue(key, out LinkFleetRow? row))
            {
                int unitIndex = gi, unitBus = g.Bus;
                string? modelName = g.Name;
                row = new LinkFleetRow
                {
                    Name = name,
                    Route = InterconnectorRoutes.RouteOf(g.Name),
                    Capacity = $"{g.Pmax:F0} MW",
                    Open = new RelayCommand(() => explorer.OpenInterconnector(unitIndex, unitBus, modelName)),
                };
                _byKey[key] = row;
                Links.Add(row);
            }
            double frac = g.Pmax > 0 ? Math.Abs(g.Pg) / g.Pmax : 0;
            bool imp = !linkIdle && g.Pg > 0, exp = !linkIdle && g.Pg < 0;
            row.Flow = $"{g.Pg:+#,0;-#,0;0} MW";
            row.Badge = imp ? "IMPORTING" : exp ? "EXPORTING" : "IDLE";
            row.Util = $"{frac * 100:F0}%";
            row.BarWidth = Math.Min(1.0, frac) * BarTrack;
            row.Bar = Palette.Frozen(imp ? Palette.Voltage(1.0) : exp ? Palette.Loading(0.8) : Palette.Voltage(0.9));
        }

        // Prune links absent from the current model (e.g. after a source/zone switch while this page is
        // open) so a previous model's rows don't linger showing their last flow/badge as live data.
        foreach (string stale in _byKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Links.Remove(_byKey[stale]);
            _byKey.Remove(stale);
        }

        TotalCap = $"{cap / 1000.0:F1}";
        NetFlow = $"{net / 1000.0:+0.00;-0.00;0.00}";
        Importing = $"{importing} of {count}";
        Largest = $"{biggestName}  {Math.Abs(biggest):F0} MW";
    }
}

/// <summary>One interconnector's dashboard: live flow, recent history, landing-zone context.</summary>
/// <remarks>N258 (October 2026): the link is identified by its generator INDEX, confirmed by bus and model name
/// (<see cref="AssetLookup.FindGenerator"/>), as the generator, transformer and circuit pages are (R055, D-DOC-146). It was
/// identified by name alone, so of two links sharing a name (gb-full has two "Auchencrosh (interconnector CCT)
/// interconnector" units) the second always opened, and read the flow of, the first. Its co-located generation now leaves
/// out this unit by index, not every unit of the same name.</remarks>
public sealed class InterconnectorDetailVm(int index, int unitBus, string? unitName, AssetExplorerVm explorer) : AssetPageVm
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps

    // Display name (page title): the same label the fleet row shows.
    private readonly string name = unitName ?? $"link @ bus {unitBus}";

    public override string Eyebrow => "INTERCONNECTOR";
    public override string Title => name;
    private string _sub = InterconnectorRoutes.RouteOf(unitName); public override string Sub => _sub;

    /// <summary>Click-through to the substation the link lands on.</summary>
    public ICommand OpenLanding { get; } = new RelayCommand<int>(id => explorer.OpenSubstation(id));
    private int _landingBusId; public int LandingBusId { get => _landingBusId; private set => Set(ref _landingBusId, value); }

    private string _flow = " - "; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _capacity = " - "; public string Capacity { get => _capacity; set => Set(ref _capacity, value); }
    private string _util = " - "; public string Util { get => _util; set => Set(ref _util, value); }
    private string _direction = " - "; public string Direction { get => _direction; set => Set(ref _direction, value); }
    private Brush _directionBrush = Brushes.Gray; public Brush DirectionBrush { get => _directionBrush; set => Set(ref _directionBrush, value); }

    private string _landing = " - "; public string Landing { get => _landing; set => Set(ref _landing, value); }
    private string _landingKv = " - "; public string LandingKv { get => _landingKv; set => Set(ref _landingKv, value); }
    private string _landingVm = " - "; public string LandingVm { get => _landingVm; set => Set(ref _landingVm, value); }
    private string _colocated = " - "; public string Colocated { get => _colocated; set => Set(ref _colocated, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    public override void Refresh(SimFrame frame)
    {
        GridModel m = frame.Model;
        // N258: the index, confirmed by bus and name; otherwise the one unit with that bus and name; otherwise (gone, or
        // ambiguous) say so rather than show a namesake's numbers.
        (Generator Unit, int Index)? hit = AssetLookup.FindGenerator(m, index, unitBus, unitName);
        if (hit is null)
        {
            _sub = "this link is not in the current model (model changed?)  -  values below are stale";
            Raise(nameof(Sub));
            return;
        }
        Generator g = hit.Value.Unit;
        int self = hit.Value.Index;
        _sub = InterconnectorRoutes.RouteOf(unitName);
        Raise(nameof(Sub));

        // The shared idle cut-off (StatusLampRules, 0.5 MW), as the fleet page and the GENERATION tab use.
        bool idle = StatusLampRules.IsIdleInterconnector(g.Pg);
        bool imp = !idle && g.Pg > 0, exp = !idle && g.Pg < 0;
        Flow =$"{g.Pg:+#,0;-#,0;0}";
        Capacity = $"{g.Pmax:F0}";
        Util = g.Pmax > 0 ? $"{Math.Abs(g.Pg) / g.Pmax * 100:F0}%" : " - ";
        Direction = imp ? "IMPORTING" : exp ? "EXPORTING" : "IDLE";
        DirectionBrush = Palette.Frozen(imp ? Palette.Voltage(1.0) : exp ? Palette.Loading(0.8) : Palette.Voltage(0.9));

        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == g.Bus);
        if (bus is not null)
        {
            Landing = bus.Name ?? $"bus {bus.Id}";
            LandingBusId = bus.Id;
            LandingKv = $"{bus.BaseKv:F0} kV";
            for (int i = 0; i < frame.Result.BusIds.Count; i++)
                if (frame.Result.BusIds[i] == bus.Id) { LandingVm = $"{frame.Result.Vm[i]:F3} pu"; break; }
            double other = m.Generators.Where((x, i) => x.Bus == g.Bus && i != self).Sum(x => x.Pg);
            Colocated = other > 0 ? $"{other:F0} MW co-located generation" : "no co-located generation";
        }

        // The trace holds only the samples actually seen since the page opened. It used to be seeded with 95 copies of
        // the first reading, which drew an invented flat day and a half of history (the sparkline draws from 2 samples).
        _history.Add(g.Pg);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        Trace = _history.ToArray();
    }
}
