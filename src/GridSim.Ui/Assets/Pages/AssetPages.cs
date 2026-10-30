using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Pages;

// UI programme ASSETS A2: the page logic of the WPF ASSETS tab moved here from src/GridSim.Wpf/Views/Assets/AssetPages.cs
// (commit 6e729a66, 2.0.46), line for line. What changed, and only this:
//   - every WPF Brush became a neutral colour token (UiColour) with the same four bytes: a property "XBrush" is now
//     "XColour" ("Bar" is "BarColour"), Palette.Frozen(Palette.Loading(x)) is ColourRamps.Loading(x), and the WPF
//     Brushes.Gray default is DashboardColours.Gray (#FF808080);
//   - every Visibility became a bool (BackVis is BackVisible: true = Visible, false = Collapsed);
//   - every ICommand became a plain Action or method (a row's Open command is its OnOpen action; the explorer's BackCmd
//     and HomeCmd are its Back and GoHome methods; a page's OpenLanding command is its OpenLandingSubstation method);
//   - the WPF Fuels / FuelClass became the neutral UiFuels / Core FuelCategory (a one-to-one translation, same names,
//     same colours);
//   - a page no longer makes its rows with "new LinkFleetRow": it makes them as its type parameter (TRow), so the WPF
//     front end puts its own row types (which add the brushes and commands under the old names) in the lists, and the
//     explorer makes its pages through the Create... methods below, which the WPF explorer overrides for the same
//     reason.
// Every formatting call (machine culture, as the WPF interpolated strings were), number, branch, ordering, cap and
// message is the old one. UiAssetPagesParityTests pins every displayed value against a verbatim copy of the old WPF code.
// The quirks the ASSETS plan lists (section 2 item 9: the home phasor card's spread over ALL buses, "OLTC FITTED"
// counting OLTC records, the substation kV chips built once) are copied as is, to be fixed later in their own slices.
// Fixed since (2026-10-20, each its own slice, the parity test naming each exact difference): S4, the substation kV
// chips are rebuilt on a model swap as the circuit page's band chips are (SubstationPages.cs); S12, "OLTC FITTED" counts
// transformers with a mapped OLTC record, not OLTC records (TransformerPages.cs); S5, the home GENERATOR PHASORS card's
// angle spread is over the generator buses, by the phasor page's own rule (AssetHomeModel.Refresh below); S10, a
// circuit whose kV lies off the GB ladder (no level within 0.5 kV) is banded at its own kV, not the nearest GB level
// (CircuitInfo.Band in CircuitPages.cs); S3, every page's per-frame bus table is the one shared first-wins lookup
// (FrameLookup.cs), so the substation fleet's voltage table is no longer last-wins (it differs only where two buses
// share an id, which no bundled case has); S7, the transformer page's PROTECTION panel lists the case's own protection
// settings for the transformer when the case has any (TransformerProtection in TransformerPages.cs), its STATIC DEMO tag
// then hidden, and the demo rows, tagged, otherwise.

/// <summary>
/// One page of the asset explorer. Pages form a drill-down (home -> fleet -> unit);
/// <see cref="Refresh"/> is called with every solved frame, so a page that binds the
/// live model stays time-aligned with the running sim (and later, a replay).
/// </summary>
public abstract class AssetPageModel : ObservableModel
{
    public abstract string Eyebrow { get; }
    public abstract string Title { get; }
    public virtual string Sub => "";
    public virtual void Refresh(SimFrame frame) { }
}

/// <summary>
/// The explorer shell: a navigation stack of <see cref="AssetPageModel"/>s under a
/// header with back/home and the sim-time strip. The whole thing is model-driven  -
/// views are matched to page models by their type.
/// </summary>
/// <remarks>
/// The pages are made through the virtual Create... methods, so a front end can put its own page types (each derived
/// from the neutral page) on the stack; the defaults make the neutral pages with the neutral row types. The landing page
/// is made by the function handed to the constructor (not by a virtual call from the constructor).
/// </remarks>
public class AssetExplorerModel : ObservableModel
{
    private readonly Stack<AssetPageModel> _stack = new();
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

    /// <summary>The landing page (the ASSETS button returns here).</summary>
    public AssetPageModel Home { get; }

    /// <summary>An explorer over the neutral pages (the landing page is the neutral home with neutral cards).</summary>
    public AssetExplorerModel() : this(e => new AssetHomeModel<AssetClassCardModel>(e)) { }

    /// <summary>An explorer whose landing page is made by <paramref name="createHome"/> (given this explorer).</summary>
    protected AssetExplorerModel(Func<AssetExplorerModel, AssetPageModel> createHome)
    {
        ArgumentNullException.ThrowIfNull(createHome);
        Home = createHome(this);
        _current = Home;
    }

    private AssetPageModel _current;
    public AssetPageModel Current { get => _current; private set { Set(ref _current, value); Raise(nameof(BackVisible)); Raise(nameof(Crumb)); } }

    /// <summary>True when there is a page to go back to (was the WPF <c>BackVis</c>: Visible when true, else Collapsed).</summary>
    public bool BackVisible => _stack.Count > 0;
    public string Crumb => string.Join("    ", _stack.Reverse().Select(p => p.Title).Append(Current.Title));

    private string _timeLabel = " - "; public string TimeLabel { get => _timeLabel; set => Set(ref _timeLabel, value); }

    public void Open(AssetPageModel page)
    {
        _stack.Push(Current);
        Current = page;
        if (_frame is not null) page.Refresh(_frame);
    }

    public void Back() { if (_stack.Count > 0) { Current = _stack.Pop(); if (_frame is not null) Current.Refresh(_frame); } }

    public void GoHome() { _stack.Clear(); Current = Home; if (_frame is not null) Current.Refresh(_frame); }

    // ---- the page factories: a front end overrides these to put its own page types on the stack ----

    /// <summary>A new GENERATORS fleet page.</summary>
    public virtual AssetPageModel CreateGeneratorFleet() => new GeneratorFleetModel<GenFleetRowModel>(this);

    /// <summary>A new GENERATOR PHASORS page.</summary>
    public virtual AssetPageModel CreatePhasorFleet() => new GeneratorPhasorFleetModel<PhasorTileModel>(this);

    /// <summary>A new INTERCONNECTORS fleet page.</summary>
    public virtual AssetPageModel CreateInterconnectorFleet() => new InterconnectorFleetModel<LinkFleetRowModel>(this);

    /// <summary>A new TRANSFORMERS fleet page.</summary>
    public virtual AssetPageModel CreateTransformerFleet() => new TransformerFleetModel<TransformerFleetRowModel>(this);

    /// <summary>A new CABLES &amp; LINES fleet page.</summary>
    public virtual AssetPageModel CreateCircuitFleet() => new CircuitFleetModel<BandChipModel, CircuitFleetRowModel>(this);

    /// <summary>A new SUBSTATIONS fleet page.</summary>
    public virtual AssetPageModel CreateSubstationFleet() => new SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel>(this);

    /// <summary>A new TAP CHANGERS fleet page.</summary>
    public virtual AssetPageModel CreateTapChangerFleet() => new TapChangerFleetModel<TapChangerRowModel>(this);

    /// <summary>A new substation page for a bus id.</summary>
    public virtual AssetPageModel CreateSubstationDetail(int busId) => new SubstationDetailModel<PlantRowModel, ConnectionRowModel>(busId, this);

    /// <summary>A new generator page (model index, confirmed by bus and model name).</summary>
    public virtual AssetPageModel CreateGeneratorDetail(int index, int bus, string? name) => new GeneratorDetailModel(index, bus, name, this);

    /// <summary>A new interconnector page (model index, confirmed by bus and model name).</summary>
    public virtual AssetPageModel CreateInterconnectorDetail(int index, int bus, string? name) => new InterconnectorDetailModel(index, bus, name, this);

    /// <summary>A new circuit page (branch index, confirmed by its ends and model name).</summary>
    public virtual AssetPageModel CreateCircuitDetail(int index, int fromBus, int toBus, string? name) =>
        new CircuitDetailModel<CircuitStatusRowModel>(index, fromBus, toBus, name, this);

    /// <summary>A new transformer page (branch index, confirmed by its ends and model name).</summary>
    public virtual AssetPageModel CreateTransformerDetail(int index, int fromBus, int toBus, string? name) =>
        new TransformerDetailModel<TransformerReadingModel, TransformerEventModel>(index, fromBus, toBus, name, this);

    // ---- cross-navigation: open any related entity's detail page in this same stack ----
    // This is what turns the explorer into a navigable graph: a cable links to its
    // substations, a substation to its plant and circuits, a generator back to its bus.
    public void OpenSubstation(int busId) => Open(CreateSubstationDetail(busId));
    // Generators and transformers are opened by model INDEX plus the identifying fields (bus / ends and model
    // name), never by display name alone: gb-full repeats names, and name lookup always opened the first
    // namesake (R055). Circuits too since D-DOC-146 (they were opened by name, so a repeated circuit name
    // always opened the first namesake, and an unnamed circuit never resolved at all), and interconnectors since N258
    // (gb-full has two links both named "Auchencrosh (interconnector CCT) interconnector"; the second opened as the first).
    public void OpenGenerator(int index, int bus, string? name) => Open(CreateGeneratorDetail(index, bus, name));
    public void OpenInterconnector(int index, int bus, string? name) => Open(CreateInterconnectorDetail(index, bus, name));
    public void OpenCircuit(int index, GridSim.Core.Model.Branch br) =>
        Open(CreateCircuitDetail(index, br.From, br.To, br.Name));
    public void OpenTransformer(int index, GridSim.Core.Model.Branch br) =>
        Open(CreateTransformerDetail(index, br.From, br.To, br.Name));

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
        Open(CreateTransformerDetail(-1, 0, 0, name));
    }

    /// <summary>Open a branch's detail  -  a transformer page if it's a transformer, else a corridor page.
    /// The branch's index is found by reference in the current frame's model; when the frame has moved on
    /// (index -1) the page resolves the branch by its ends and name instead (both page kinds, D-DOC-146).</summary>
    public void OpenBranch(GridSim.Core.Model.Branch br)
    {
        ArgumentNullException.ThrowIfNull(br);
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
        ArgumentNullException.ThrowIfNull(frame);
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
public class AssetClassCardModel : ObservableModel
{
    public const double BarTrack = 220.0;

    public string Title { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>What a click on the card does (was the WPF <c>Open</c> command; the WPF card keeps an <c>Open</c>
    /// command under its old name, which runs this).</summary>
    public Action OnOpen { get; init; } = () => { };

    private string _count = " - "; public string Count { get => _count; set => Set(ref _count, value); }

    // ---- live headline metric (the thing that makes the card earn its space) ----
    private string _metric = " - "; public string Metric { get => _metric; set => Set(ref _metric, value); }
    private string _metricUnit = ""; public string MetricUnit { get => _metricUnit; set => Set(ref _metricUnit, value); }
    private string _metricLabel = ""; public string MetricLabel { get => _metricLabel; set => Set(ref _metricLabel, value); }
    // Was the WPF MetricBrush (default Brushes.Gray).
    private UiColour _metricColour = DashboardColours.Gray; public UiColour MetricColour { get => _metricColour; set => Set(ref _metricColour, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
}

/// <summary>The landing page: every asset class in the running model, with live counts.</summary>
/// <typeparam name="TCard">The card type the front end shows (the neutral card, or one derived from it).</typeparam>
public class AssetHomeModel<TCard> : AssetPageModel where TCard : AssetClassCardModel, new()
{
    public override string Eyebrow => "ASSET EXPLORER";
    public override string Title => "ASSETS";
    public override string Sub => "every asset class in the running model  -  drill down to the unit";

    public ObservableCollection<TCard> Classes { get; } = new();

    private readonly TCard _generators, _phasors, _interconnectors, _transformers, _circuits, _substations, _oltcs;

    public AssetHomeModel(AssetExplorerModel explorer)
    {
        ArgumentNullException.ThrowIfNull(explorer);
        Classes.Add(_generators = new TCard
        {
            Title = "GENERATORS",
            Tag = "FLEET",
            Detail = "thermal  nuclear  hydro  wind  solar  -  by fuel, down to the unit",
            OnOpen = () => explorer.Open(explorer.CreateGeneratorFleet()),
        });
        Classes.Add(_phasors = new TCard
        {
            Title = "GENERATOR PHASORS",
            Tag = "LIVE",
            Detail = "every generator's voltage-current phasor  -  phase angle from the solved power flow",
            OnOpen = () => explorer.Open(explorer.CreatePhasorFleet()),
        });
        Classes.Add(_interconnectors = new TCard
        {
            Title = "INTERCONNECTORS",
            Tag = "LIVE",
            Detail = "the GB import links  -  signed flow, capacity, landing site",
            OnOpen = () => explorer.Open(explorer.CreateInterconnectorFleet()),
        });
        Classes.Add(_transformers = new TCard
        {
            Title = "TRANSFORMERS",
            Tag = "FLEET",
            Detail = "GSP and grid transformers by voltage band, OLTC state per unit",
            OnOpen = () => explorer.Open(explorer.CreateTransformerFleet()),
        });
        Classes.Add(_circuits = new TCard
        {
            Title = "CABLES & LINES",
            Tag = "FLEET",
            Detail = "transmission corridors and circuits  -  loading, rating, route",
            OnOpen = () => explorer.Open(explorer.CreateCircuitFleet()),
        });
        Classes.Add(_substations = new TCard
        {
            Title = "SUBSTATIONS",
            Tag = "FLEET",
            Detail = "every electrical bus  -  voltage, demand, connected plant",
            OnOpen = () => explorer.Open(explorer.CreateSubstationFleet()),
        });
        Classes.Add(_oltcs = new TCard
        {
            Title = "TAP CHANGERS",
            Tag = "FLEET",
            Detail = "on-load tap changers and the buses they regulate",
            OnOpen = () => explorer.Open(explorer.CreateTapChangerFleet()),
        });
    }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        PowerFlowResult r = frame.Result;

        // O(1) lookups built once per refresh. Previously every OLTC scanned all bus ids  -  O(n^2) across the model
        // each frame. Branch flows are read BY INDEX (AssetLookup.FlowAt: LineFlows is index-aligned with Branches), not
        // through a name dictionary: names repeat (gb-full has 58 duplicated branch names) and unnamed branches have none,
        // so a name lookup read the first namesake's loading for every namesake and skipped unnamed circuits entirely.
        // The shared per-frame lookup (ASSETS fix S3: the same first-wins TryAdd loop, moved to FrameLookup unchanged).
        Dictionary<int, double> vpuById = FrameLookup.ByBusId(r.BusIds, r.Vm);   // first match wins, as VpuOf did

        // ---- counts (unchanged) ----
        _generators.Count = m.Generators.Count(AssetIndex.IsPlant).ToString();
        _interconnectors.Count = m.Generators.Count(AssetIndex.IsLink).ToString();
        _transformers.Count = m.Branches.Count(b => b.IsTransformer).ToString();
        _circuits.Count = m.Branches.Count(b => !b.IsTransformer).ToString();
        _substations.Count = m.BusCount.ToString();
        _oltcs.Count = m.Oltcs.Count.ToString();

        // ---- GENERATORS: total dispatched output, GW (non-interconnector plant) ----
        double genPg = 0, genCap = 0;
        foreach (Generator g in m.Generators)
        {
            if (!AssetIndex.IsPlant(g)) continue;
            genPg += g.Pg;
            if (!double.IsInfinity(g.Pmax)) genCap += g.Pmax;
        }
        _generators.Metric = $"{genPg / 1000.0:F1}";
        _generators.MetricUnit = " GW";
        _generators.MetricLabel = "DISPATCHED OUTPUT";
        _generators.MetricColour = AccentColour;
        _generators.BarWidth = genCap > 0 ? Math.Min(1.0, genPg / genCap) * AssetClassCardModel.BarTrack : 0;

        // ---- GENERATOR PHASORS: angle spread across the fleet ----
        // ASSETS fix S5 (2026-10-20, Mark approved fixing the quirks as their own slices): the spread is taken over the
        // GENERATOR buses, by exactly the rule the GENERATOR PHASORS page uses for its own "deg spread" figure
        // (GeneratorPhasorFleetModel.Refresh in GeneratorPages.cs), so the card and the page it opens show the same
        // number. Before this fix the card took the spread over ALL buses (every entry of r.VaDeg), so on any case whose
        // most leading or most lagging bus has no generator the card read wider than the page.
        //
        // The page's rule, step for step:
        //   - each bus id's solved angle, first match wins for a repeated id (vaById.TryAdd, as the page builds it);
        //   - the generators are the model's in-service generators that are not interconnectors (UiFuels.Classify, the
        //     same test the page and the GENERATORS card use), whatever their output (an idle unit's bus counts too);
        //   - each such generator's bus angle, when its bus is in the solved result, widens the min / max (a generator
        //     whose bus is missing from the result is skipped, as on the page);
        //   - no generator bus found means no spread (0, shown " - " below, exactly as an empty result was before).
        // The formatting, the colour thresholds and the bar scale below are unchanged; only the figure they read moved.
        // (The shared first-wins lookup, ASSETS fix S3: the same TryAdd loop, moved to FrameLookup unchanged.)
        Dictionary<int, double> vaById = FrameLookup.ByBusId(r.BusIds, r.VaDeg);   // first match wins, as the page does
        double minVa = double.MaxValue, maxVa = double.MinValue;
        foreach (Generator g in m.Generators)
        {
            if (!AssetIndex.IsPlant(g)) continue;
            if (vaById.TryGetValue(g.Bus, out double va))
            {
                if (va < minVa) minVa = va;
                if (va > maxVa) maxVa = va;
            }
        }
        double spread = minVa < double.MaxValue ? maxVa - minVa : 0;
        _phasors.Count = _generators.Count;
        _phasors.Metric = spread > 0 ? $"{spread:F1}" : " - ";
        _phasors.MetricUnit = spread > 0 ? " deg" : "";
        _phasors.MetricLabel = "ANGLE SPREAD";
        _phasors.MetricColour = spread > 30 ? ColourRamps.Loading(0.9) : spread > 15 ? ColourRamps.Loading(0.6) : ColourRamps.Voltage(1.0);
        _phasors.BarWidth = Math.Min(1.0, spread / 60.0) * AssetClassCardModel.BarTrack;

        // ---- INTERCONNECTORS: net flow, GW, signed (import +) ----
        double icCap = 0, icNet = 0;
        foreach (Generator g in m.Generators)
        {
            if (!AssetIndex.IsLink(g)) continue;
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
        _interconnectors.MetricColour = importing ? ColourRamps.Voltage(1.0) : exporting ? ColourRamps.Loading(0.8) : ColourRamps.Voltage(0.9);
        _interconnectors.BarWidth = icCap > 0 ? Math.Min(1.0, Math.Abs(icNet) / icCap) * AssetClassCardModel.BarTrack : 0;

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
        _transformers.MetricColour = ColourRamps.Loading(txMean / 100.0);
        _transformers.BarWidth = Math.Min(1.0, txMean / 100.0) * AssetClassCardModel.BarTrack;

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
        _circuits.MetricColour = ColourRamps.Loading(cktMax / 100.0);
        _circuits.BarWidth = Math.Min(1.0, cktMax / 100.0) * AssetClassCardModel.BarTrack;

        // ---- SUBSTATIONS: buses outside the shared 0.94-1.06 pu voltage band (ScadaThresholds, the lamp's band) ----
        int outOfBand = 0;
        for (int i = 0; i < r.Vm.Length; i++)
            if (r.Vm[i] < ScadaThresholds.VoltageBandLowPu || r.Vm[i] > ScadaThresholds.VoltageBandHighPu) outOfBand++;
        bool clean = outOfBand == 0;
        _substations.Metric = outOfBand.ToString();
        _substations.MetricUnit = "";
        _substations.MetricLabel = clean ? "ALL IN VOLTAGE BAND" : "BUSES OUT OF BAND";
        _substations.MetricColour = clean ? ColourRamps.Voltage(1.0) : ColourRamps.Loading(1.1);
        _substations.BarWidth = m.BusCount > 0 ? Math.Min(1.0, outOfBand / (double)m.BusCount) * AssetClassCardModel.BarTrack : 0;

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
        _oltcs.MetricColour = allHeld ? ColourRamps.Voltage(1.0) : ColourRamps.Loading(0.7);
        _oltcs.BarWidth = oltcN > 0 ? inBand / (double)oltcN * AssetClassCardModel.BarTrack : 0;
    }

    // Was the WPF AccentBrush.
    private static readonly UiColour AccentColour = ColourRamps.FromHex("#21C7E6");
}

/// <summary>One link's row on the interconnector fleet page.</summary>
public class LinkFleetRowModel : ObservableModel
{
    public string Name { get; init; } = "";
    public string Route { get; init; } = "";
    public string Capacity { get; init; } = "";

    /// <summary>What a click on the row does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };
    private string _flow = ""; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _badge = ""; public string Badge { get => _badge; set => Set(ref _badge, value); }
    private string _util = ""; public string Util { get => _util; set => Set(ref _util, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Was the WPF Bar brush (default Brushes.Gray).
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
}

/// <summary>The interconnector fleet page: every link in the model, click-through to the unit.</summary>
/// <typeparam name="TRow">The row type the front end shows.</typeparam>
public class InterconnectorFleetModel<TRow>(AssetExplorerModel explorer) : AssetPageModel where TRow : LinkFleetRowModel, new()
{
    public const double BarTrack = 240.0;

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "INTERCONNECTORS";
    public override string Sub => "HVDC links into the GB system  -  signed flow, positive importing";

    public ObservableCollection<TRow> Links { get; } = new();
    // N258 (October 2026): rows keyed by the unit's model INDEX plus its bus and name, as the substation's plant rows are
    // (R055). They were keyed by name, so two links sharing a name (gb-full's two Auchencrosh links) shared ONE row, which
    // showed the last one's flow and opened the first one's page.
    private readonly Dictionary<string, TRow> _byKey = new();

    private string _totalCap = " - "; public string TotalCap { get => _totalCap; set => Set(ref _totalCap, value); }
    private string _netFlow = " - "; public string NetFlow { get => _netFlow; set => Set(ref _netFlow, value); }
    private string _importing = " - "; public string Importing { get => _importing; set => Set(ref _importing, value); }
    private string _largest = " - "; public string Largest { get => _largest; set => Set(ref _largest, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        double cap = 0, net = 0, biggest = 0;
        int importing = 0, count = 0;
        string biggestName = " - ";
        var seen = new HashSet<string>();

        IReadOnlyList<Generator> fleet = frame.Model.Generators;
        for (int gi = 0; gi < fleet.Count; gi++)
        {
            Generator g = fleet[gi];
            if (!AssetIndex.IsLink(g)) continue;
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

            if (!_byKey.TryGetValue(key, out TRow? row))
            {
                int unitIndex = gi, unitBus = g.Bus;
                string? modelName = g.Name;
                row = new TRow
                {
                    Name = name,
                    Route = InterconnectorRoutes.RouteOf(g.Name),
                    Capacity = $"{g.Pmax:F0} MW",
                    OnOpen = () => explorer.OpenInterconnector(unitIndex, unitBus, modelName),
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
            row.BarColour = imp ? ColourRamps.Voltage(1.0) : exp ? ColourRamps.Loading(0.8) : ColourRamps.Voltage(0.9);
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
public class InterconnectorDetailModel(int index, int unitBus, string? unitName, AssetExplorerModel explorer) : AssetPageModel
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps

    // Display name (page title): the same label the fleet row shows.
    private readonly string name = unitName ?? $"link @ bus {unitBus}";

    public override string Eyebrow => "INTERCONNECTOR";
    public override string Title => name;
    private string _sub = InterconnectorRoutes.RouteOf(unitName); public override string Sub => _sub;

    /// <summary>Click-through to the substation the link lands on (was the WPF <c>OpenLanding</c> command, whose
    /// parameter binds <see cref="LandingBusId"/>).</summary>
    public void OpenLandingSubstation(int id) => explorer.OpenSubstation(id);
    private int _landingBusId; public int LandingBusId { get => _landingBusId; private set => Set(ref _landingBusId, value); }

    private string _flow = " - "; public string Flow { get => _flow; set => Set(ref _flow, value); }
    private string _capacity = " - "; public string Capacity { get => _capacity; set => Set(ref _capacity, value); }
    private string _util = " - "; public string Util { get => _util; set => Set(ref _util, value); }
    private string _direction = " - "; public string Direction { get => _direction; set => Set(ref _direction, value); }
    // Was the WPF DirectionBrush (default Brushes.Gray).
    private UiColour _directionColour = DashboardColours.Gray; public UiColour DirectionColour { get => _directionColour; set => Set(ref _directionColour, value); }

    private string _landing = " - "; public string Landing { get => _landing; set => Set(ref _landing, value); }
    private string _landingKv = " - "; public string LandingKv { get => _landingKv; set => Set(ref _landingKv, value); }
    private string _landingVm = " - "; public string LandingVm { get => _landingVm; set => Set(ref _landingVm, value); }
    private string _colocated = " - "; public string Colocated { get => _colocated; set => Set(ref _colocated, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
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
        DirectionColour = imp ? ColourRamps.Voltage(1.0) : exp ? ColourRamps.Loading(0.8) : ColourRamps.Voltage(0.9);

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
