using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Pages;

// UI programme ASSETS A2: moved here from src/GridSim.Wpf/Views/Assets/GeneratorPages.cs (commit 6e729a66), line for
// line, with brushes as colour tokens, commands as actions and the WPF Fuels as UiFuels (see AssetPages.cs for the full
// list of what changed; nothing else did).
//
// The generators asset class: a fleet snapshot over every synchronous and converter
// machine in the running model (interconnectors live in their own class), and a unit
// dashboard that pairs the live load-flow numbers with the UI-first subsystem / event
// scaffolding the v1 app sketched out, ready for real plant telemetry to land later.

/// <summary>One generator's row on the fleet page  -  live output, capacity bar and inertia tag.</summary>
public class GenFleetRowModel : ObservableModel
{
    /// <summary>The unit's index in the model's generator list: the row's identity (names repeat on gb-full).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Fuel { get; init; } = "";
    /// <summary>The fuel chip's colour (was the WPF <c>FuelBrush</c>, default <c>Brushes.Gray</c>).</summary>
    public UiColour FuelColour { get; init; } = DashboardColours.Gray;
    public string Inertia { get; init; } = "";
    /// <summary>What a click on the row does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };

    private string _output = ""; public string Output { get => _output; set => Set(ref _output, value); }
    private string _util = ""; public string Util { get => _util; set => Set(ref _util, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    // Was the WPF Bar brush (default Brushes.Gray).
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
}

/// <summary>The generator fleet page: the dispatchable plant in the model, ordered by output.</summary>
/// <typeparam name="TRow">The row type the front end shows.</typeparam>
public class GeneratorFleetModel<TRow>(AssetExplorerModel explorer) : AssetPageModel where TRow : GenFleetRowModel, new()
{
    public const double BarTrack = 240.0;
    private const int MaxRows = 200;   // GDA cases run to ~1000 units  -  cap the register, note the rest

    public override string Eyebrow => "ASSET CLASS";
    public override string Title => "GENERATORS";
    public override string Sub => "the generating fleet by fuel  -  synchronous inertia and converter plant, down to the unit";

    public ObservableCollection<TRow> Units { get; } = new();
    // Rows keyed by generator INDEX, not name: gb-full repeats names ("- chp" and friends), and name keying kept
    // one row per name, updated by every namesake in turn (last writer wins), so the register lost units and
    // showed the wrong MW (R055).
    private readonly Dictionary<int, TRow> _byIndex = new();

    private string _totalOutput = " - "; public string TotalOutput { get => _totalOutput; set => Set(ref _totalOutput, value); }
    private string _online = " - "; public string Online { get => _online; set => Set(ref _online, value); }
    private string _synchronous = " - "; public string Synchronous { get => _synchronous; set => Set(ref _synchronous, value); }
    private string _largest = " - "; public string Largest { get => _largest; set => Set(ref _largest, value); }
    private string _countNote = ""; public string CountNote { get => _countNote; set => Set(ref _countNote, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // Snapshot pass over the whole fleet for the metric strip; the register itself is
        // capped to the top MaxRows by output so the very large GDA models stay responsive.
        // Each unit travels with its model index, which is its identity on this page.
        IReadOnlyList<Generator> all = frame.Model.Generators;
        var indexed = new List<(Generator g, int idx)>();
        for (int i = 0; i < all.Count; i++)
            if (AssetIndex.IsPlant(all[i])) indexed.Add((all[i], i));
        var gens = indexed.Select(x => x.g).ToList();

        double totalMw = 0, syncMw = 0, biggest = 0;
        int online = 0;
        string biggestName = " - ";
        foreach (Generator g in gens)
        {
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            totalMw += g.Pg;
            if (g.Pg > 0) online++;
            if (fi.Synchronous) syncMw += g.Pg;
            if (g.Pg > biggest) { biggest = g.Pg; biggestName = NameOf(g); }
        }

        TotalOutput = $"{totalMw / 1000.0:F2}";
        Online = $"{online} of {gens.Count}";
        Synchronous = totalMw > 0 ? $"{syncMw / totalMw * 100:F0}%" : " - ";
        Largest = $"{biggestName}  {biggest:F0} MW";

        // OrderByDescending is a stable sort, so equal outputs keep model order (deterministic).
        List<(Generator g, int idx)> shown = indexed.OrderByDescending(x => x.g.Pg).Take(MaxRows).ToList();
        CountNote = gens.Count > MaxRows ? $"showing {MaxRows} of {gens.Count}" : "";

        // Prune rows that rotated out of the top-MaxRows (or vanished on a
        // model swap): unrefreshed rows kept displaying their LAST MW as if
        // live, indefinitely (every other fleet page prunes or full-updates).
        // A row whose index now holds a differently named unit (model swap) is dropped and rebuilt too.
        var keep = new Dictionary<int, string>();
        foreach ((Generator g, int idx) in shown) keep[idx] = NameOf(g);
        for (int i = Units.Count - 1; i >= 0; i--)
        {
            if (keep.TryGetValue(Units[i].Index, out string? nm) && nm == Units[i].Name) continue;
            _byIndex.Remove(Units[i].Index);
            Units.RemoveAt(i);
        }

        foreach ((Generator g, int idx) in shown)
        {
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            string name = NameOf(g);

            if (!_byIndex.TryGetValue(idx, out TRow? row))
            {
                int bus = g.Bus;
                string? modelName = g.Name;
                row = new TRow
                {
                    Index = idx,
                    Name = name,
                    Fuel = fi.Name,
                    FuelColour = fi.Colour,
                    Inertia = fi.Synchronous ? $"H {g.H:F1}" : "CONVERTER",
                    OnOpen = () => explorer.OpenGenerator(idx, bus, modelName),
                };
                _byIndex[idx] = row;
                Units.Add(row);
            }

            double frac = g.Pmax > 0 && !double.IsInfinity(g.Pmax) ? g.Pg / g.Pmax : 0;
            row.Output = $"{g.Pg:#,0} MW";
            row.Util = double.IsInfinity(g.Pmax) || g.Pmax <= 0 ? " - " : $"{frac * 100:F0}%";
            row.BarWidth = Math.Clamp(frac, 0, 1) * BarTrack;
            row.BarColour = fi.Colour;
        }
    }

    private static string NameOf(Generator g) => g.Name ?? $"unit @ bus {g.Bus}";
}

/// <summary>A subsystem's live status line on the unit dashboard (static demo data for now).</summary>
public sealed record SubsystemRow(string Name, string Status, string Note)
{
    public bool Ok => Status == "OK";
}

/// <summary>One timestamped entry in the unit's event log (static demo data for now).</summary>
public sealed record EventRow(string Time, string Text);

/// <summary>One generator's dashboard: live electrical state plus the subsystem / event scaffolding.</summary>
/// <remarks>The unit is identified by its model INDEX, confirmed by bus and name (<see cref="AssetLookup.FindGenerator"/>),
/// not by name alone: name lookup always opened the FIRST of gb-full's namesakes (R055).</remarks>
public class GeneratorDetailModel(int index, int unitBus, string? unitName, AssetExplorerModel explorer) : AssetPageModel
{
    private const int HistoryLength = 96;   // ~ a day and a half of 20-min sim steps

    // The display name (the page title). Matches the fleet row's label for the same unit.
    private readonly string name = unitName ?? $"unit @ bus {unitBus}";

    public override string Eyebrow => "GENERATOR";
    public override string Title => name;

    private string _sub = ""; public override string Sub => _sub;

    /// <summary>Click-through to the substation this unit connects to (was the WPF <c>OpenBus</c> command, whose
    /// parameter binds <see cref="BusId"/>).</summary>
    public void OpenBusSubstation(int id) => explorer.OpenSubstation(id);
    private int _busId; public int BusId { get => _busId; private set => Set(ref _busId, value); }

    // --- live electrical state ---
    private string _output = " - "; public string Output { get => _output; set => Set(ref _output, value); }
    private string _capacity = " - "; public string Capacity { get => _capacity; set => Set(ref _capacity, value); }
    private string _util = " - "; public string Util { get => _util; set => Set(ref _util, value); }
    private string _fuel = " - "; public string Fuel { get => _fuel; set => Set(ref _fuel, value); }
    // Was the WPF FuelBrush (default Brushes.Gray).
    private UiColour _fuelColour = DashboardColours.Gray; public UiColour FuelColour { get => _fuelColour; set => Set(ref _fuelColour, value); }

    private string _inertia = " - "; public string Inertia { get => _inertia; set => Set(ref _inertia, value); }
    private string _storedEnergy = " - "; public string StoredEnergy { get => _storedEnergy; set => Set(ref _storedEnergy, value); }
    private string _vSet = " - "; public string VSet { get => _vSet; set => Set(ref _vSet, value); }
    private string _reactive = " - "; public string Reactive { get => _reactive; set => Set(ref _reactive, value); }
    private string _reactiveLimits = " - "; public string ReactiveLimits { get => _reactiveLimits; set => Set(ref _reactiveLimits, value); }

    private string _bus = " - "; public string Bus { get => _bus; set => Set(ref _bus, value); }
    private string _busKv = " - "; public string BusKv { get => _busKv; set => Set(ref _busKv, value); }
    private string _busVm = " - "; public string BusVm { get => _busVm; set => Set(ref _busVm, value); }

    // --- OUTPUT dial: live MW against nameplate ---
    private double _outputMw; public double OutputMw { get => _outputMw; set => Set(ref _outputMw, value); }
    private double _capacityMw = 1; public double CapacityMw { get => _capacityMw; set => Set(ref _capacityMw, value); }
    private string _outputDialText = " - "; public string OutputDialText { get => _outputDialText; set => Set(ref _outputDialText, value); }

    // --- inertia contribution: this unit's share of the grid's spin ---
    private string _systemInertia = " - "; public string SystemInertia { get => _systemInertia; set => Set(ref _systemInertia, value); }
    private string _inertiaShare = " - "; public string InertiaShare { get => _inertiaShare; set => Set(ref _inertiaShare, value); }
    private string _inertiaFraming = " - "; public string InertiaFraming { get => _inertiaFraming; set => Set(ref _inertiaFraming, value); }
    private double _inertiaShareBar; public double InertiaShareBar { get => _inertiaShareBar; set => Set(ref _inertiaShareBar, value); }
    public const double ShareTrack = 300.0;   // px width of the inertia-share bar track

    // --- phasor / operating point (Q estimated from the solved frame) ---
    private double _phasorP; public double PhasorP { get => _phasorP; set => Set(ref _phasorP, value); }
    private double _phasorQ; public double PhasorQ { get => _phasorQ; set => Set(ref _phasorQ, value); }
    private double _phasorSMax; public double PhasorSMax { get => _phasorSMax; set => Set(ref _phasorSMax, value); }
    private string _operatingPoint = " - "; public string OperatingPoint { get => _operatingPoint; set => Set(ref _operatingPoint, value); }
    private string _powerFactor = " - "; public string PowerFactor { get => _powerFactor; set => Set(ref _powerFactor, value); }
    private string _leadLag = " - "; public string LeadLag { get => _leadLag; set => Set(ref _leadLag, value); }
    // Was the WPF LeadLagBrush (default Brushes.Gray).
    private UiColour _leadLagColour = DashboardColours.Gray; public UiColour LeadLagColour { get => _leadLagColour; set => Set(ref _leadLagColour, value); }

    // --- estimated fuel burn (clearly labelled: derived from output, not metered) ---
    private string _fuelBurnTitle = " - "; public string FuelBurnTitle { get => _fuelBurnTitle; set => Set(ref _fuelBurnTitle, value); }
    private string _fuelBurnPrimary = " - "; public string FuelBurnPrimary { get => _fuelBurnPrimary; set => Set(ref _fuelBurnPrimary, value); }
    private string _fuelBurnPrimaryUnit = ""; public string FuelBurnPrimaryUnit { get => _fuelBurnPrimaryUnit; set => Set(ref _fuelBurnPrimaryUnit, value); }
    private string _fuelBurnCo2 = " - "; public string FuelBurnCo2 { get => _fuelBurnCo2; set => Set(ref _fuelBurnCo2, value); }
    private string _fuelBurnNote = " - "; public string FuelBurnNote { get => _fuelBurnNote; set => Set(ref _fuelBurnNote, value); }

    private readonly List<double> _history = new();
    private IReadOnlyList<double> _trace = Array.Empty<double>();
    public IReadOnlyList<double> Trace { get => _trace; private set => Set(ref _trace, value); }

    // --- UI-first scaffolding: plausible static demo data, grouped for later datasets ---
    public ObservableCollection<SubsystemRow> Subsystems { get; } = new();
    public ObservableCollection<EventRow> Events { get; } = new();
    private bool _staticBuilt;

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        Generator? g = AssetLookup.FindGenerator(m, index, unitBus, unitName)?.Unit;
        if (g is null)
        {
            // Say so plainly rather than keep showing the last frame's numbers as if live.
            _sub = "this unit is not in the current model (model changed?)  -  values below are stale";
            Raise(nameof(Sub));
            return;
        }

        UiFuelInfo fi = UiFuels.Classify(g.Type);
        bool capped = g.Pmax > 0 && !double.IsInfinity(g.Pmax);

        _sub = $"{fi.Name}  {(fi.Synchronous ? "synchronous machine" : "converter-connected")}";
        Raise(nameof(Sub));

        Output = $"{g.Pg:#,0}";
        Capacity = capped ? $"{g.Pmax:#,0}" : " - ";
        Util = capped ? $"{g.Pg / g.Pmax * 100:F0}%" : " - ";
        Fuel = fi.Name;
        FuelColour = fi.Colour;

        // OUTPUT dial: live MW against nameplate, amber from 85% red from 95%. Where the
        // unit has no real cap (Pmax = ) fall back to its own output as the dial top.
        OutputMw = g.Pg;
        CapacityMw = capped ? g.Pmax : Math.Max(g.Pg, 1);
        OutputDialText = $"{g.Pg:#,0}";

        // INERTIA: H, the stored energy E = H  MBase, and this unit's share of  HMBase.
        double storedMws = g.H * g.MBase;            // MWs of rotational energy
        double systemGvas = m.SystemInertiaGvaS;     //  HMBase over the fleet, GVAs
        if (fi.Synchronous && g.H > 0)
        {
            Inertia = $"H {g.H:F1} s";
            StoredEnergy = $"{storedMws:#,0} MWs  {storedMws / 1000.0:F2} GVAs";   // E = H  MBase
            double share = systemGvas > 1e-9 ? storedMws / 1000.0 / systemGvas : 0;
            InertiaShare = $"{share * 100:F2}%";
            // A single unit is a small slice of grid spin  -  bar uses a  scale so a 2-3%
            // share still reads, clearly a relative indicator rather than a literal width.
            InertiaShareBar = Math.Sqrt(Math.Clamp(share, 0, 1)) * ShareTrack;
            InertiaFraming = "synchronous machine  -  its spinning mass resists frequency change (RoCoF)";
        }
        else
        {
            Inertia = "converter  -  no inertia";
            StoredEnergy = " - ";
            InertiaShare = "0.00%";
            InertiaShareBar = 0.0;
            InertiaFraming = "converter-connected  -  grid-following, contributes no rotational inertia";
        }
        SystemInertia = systemGvas > 1e-9 ? $"{systemGvas:F1} GVAs system total" : " - ";

        VSet = $"{g.Vg:F3} pu";
        Reactive = $"{g.Qg:+#,0;-#,0;0} MVAr";
        bool qLimited = !double.IsInfinity(g.Qmin) && !double.IsInfinity(g.Qmax);
        ReactiveLimits = qLimited ? $"{g.Qmin:#,0} ... {g.Qmax:#,0} MVAr" : "unconstrained";

        UpdateFuelBurn(fi, g.Pg);

        // Estimate this unit's reactive output from the solved frame. The load flow gives a
        // net Q injection per bus; adding back the bus demand Qd recovers the generation Q at
        // that bus, which we apportion to this unit by its share of the bus's total Pg. With
        // one machine on the bus this is exact; otherwise it's an estimate (it's fine).
        double busQg = g.Qg;   // fall back to the model's setpoint Q if we can't solve a share
        Bus? bus = m.Buses.FirstOrDefault(b => b.Id == g.Bus);
        if (bus is not null)
        {
            Bus = bus.Name ?? $"bus {bus.Id}";
            BusId = bus.Id;
            BusKv = bus.BaseKv > 0 ? $"{bus.BaseKv:F0} kV" : " - ";
            for (int i = 0; i < frame.Result.BusIds.Count; i++)
                if (frame.Result.BusIds[i] == bus.Id)
                {
                    BusVm = $"{frame.Result.Vm[i]:F3} pu";
                    double genQAtBus = frame.Result.QInjMvar[i] + bus.Qd;   // Q generated at this bus
                    double busPg = m.Generators.Where(x => x.Bus == g.Bus).Sum(x => x.Pg);
                    double sharePg = busPg > 1e-6 ? g.Pg / busPg : 1.0;
                    busQg = genQAtBus * sharePg;
                    break;
                }
        }

        // Operating point + phasor. phi = atan2(Q, P): Q>0 lags (exports VArs), Q<0 leads.
        double s = Math.Sqrt(g.Pg * g.Pg + busQg * busQg);
        double pf = s > 1e-6 ? Math.Abs(g.Pg) / s : 1.0;
        bool lead = busQg < -1e-6, lag = busQg > 1e-6;
        PhasorP = g.Pg;
        PhasorQ = busQg;
        PhasorSMax = capped ? Math.Max(g.Pmax, s) : Math.Max(s, 1);
        PowerFactor = $"{pf:F3} {(lead ? "leading" : lag ? "lagging" : "unity")}";
        LeadLag = lead ? "LEADING" : lag ? "LAGGING" : "UNITY";
        LeadLagColour = ColourRamps.FromHex(lead ? "#21C7E6" : lag ? "#F3C13A" : "#8492A0");
        OperatingPoint =
            $"P {g.Pg:#,0} MW      Q {busQg:+#,0;-#,0;0} MVAr      S {s:#,0} MVA      PF {pf:F3} {(lead ? "lead" : lag ? "lag" : "")}".TrimEnd();

        // The trace holds only the samples actually seen since the page opened. It used to be seeded with 95 copies of
        // the first reading, which drew an invented flat day and a half of history (the sparkline draws from 2 samples).
        _history.Add(g.Pg);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);
        Trace = _history.ToArray();

        if (!_staticBuilt) { BuildStatic(fi.Category); _staticBuilt = true; }
    }

    /// <summary>
    /// Estimated fuel burn for the unit, derived from electrical output  -  these are
    /// order-of-magnitude figures from textbook heat rates, NOT metered fuel flow, so
    /// every line is labelled "estimated". Heat rate HR (GJ/MWh) and the gas LHV
    /// (~38.7 MJ/m^3) give the gas volume: m^3/h = MW  HR1000 / 38.7. Only the fuel VOLUME is a
    /// local textbook estimate. The CO2 line reads the ONE carbon table (CarbonIntensityTable via
    /// CarbonFactors.CombustionTonnesPerMwh, the same figure THE MACHINE, MARKET and tracing use), so the same
    /// unit gives the same t CO2/h on every tab. Non-combusting plant reports the CO2 it avoids versus the
    /// same energy from a CCGT at the table's CCGT figure.
    /// </summary>
    private void UpdateFuelBurn(UiFuelInfo fi, double mw)
    {
        const double gasLhvMjM3 = 38.7;          // natural-gas lower heating value, MJ/m^3 (fuel-volume estimate only)
        // The shared table's per-fuel intensity (t CO2/MWh) and the CCGT benchmark for avoided carbon. This page used to
        // carry its own 0.35 / 0.49 / 0.95 and call biomass carbon-neutral, contradicting every other carbon figure.
        double tablePerMwh = CarbonFactors.CombustionTonnesPerMwh(fi.Name);
        const double ccgtCo2PerMwh = CarbonIntensityTable.CcgtTPerMwh;
        string TableText(double t) => t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        string lower = name.ToLowerInvariant();

        // Gas LHV -> m^3/h for a given heat rate (GJ/MWh): MW  HR1000 / 38.7.
        double GasM3PerH(double heatRateGjPerMwh) => mw * heatRateGjPerMwh * 1000.0 / gasLhvMjM3;

        // Renewable covers hydro/wind/solar/biomass  -  split on the unit's fuel name.
        bool biomass = fi.Name.StartsWith("Biomass", StringComparison.OrdinalIgnoreCase);

        if (fi.Name.Equals("CCGT", StringComparison.OrdinalIgnoreCase)
            || fi.Name.Equals("CHP", StringComparison.OrdinalIgnoreCase)
            || (fi.Name.Equals("Gas", StringComparison.OrdinalIgnoreCase)))
        {
            // CCGT ~= 55% efficient, heat rate ~6.5 GJ/MWh -> ~168 m^3 of gas per MWh.
            double m3h = GasM3PerH(6.5);
            FuelBurnTitle = "GAS BURN  CCGT";
            FuelBurnPrimary = $"{m3h:#,0}";
            FuelBurnPrimaryUnit = "m^3/h gas";
            FuelBurnCo2 = $"{mw * tablePerMwh:#,0} t CO2/h";
            FuelBurnNote = $"estimated  heat rate ~6.5 GJ/MWh (~55%), gas LHV 38.7 MJ/m^3; CO2 at {TableText(tablePerMwh)} t/MWh (NESO carbon table)";
        }
        else if (fi.Name.StartsWith("OCGT", StringComparison.OrdinalIgnoreCase))
        {
            // OCGT (peaker) ~= 36% efficient, ~10 GJ/MWh -> ~258 m^3/h; CO2 from the shared table.
            double m3h = GasM3PerH(10.0);
            FuelBurnTitle = "GAS BURN  OCGT";
            FuelBurnPrimary = $"{m3h:#,0}";
            FuelBurnPrimaryUnit = "m^3/h gas";
            FuelBurnCo2 = $"{mw * tablePerMwh:#,0} t CO2/h";
            FuelBurnNote = $"estimated  open-cycle heat rate ~10 GJ/MWh (~36%); CO2 at {TableText(tablePerMwh)} t/MWh (NESO carbon table)";
        }
        else if (fi.Name.StartsWith("Coal", StringComparison.OrdinalIgnoreCase))
        {
            // Coal ~36% efficient, ~0.34 t coal/MWh; CO2 from the shared table.
            FuelBurnTitle = "FUEL BURN  COAL";
            FuelBurnPrimary = $"{mw * 0.34:#,0}";
            FuelBurnPrimaryUnit = "t/h coal";
            FuelBurnCo2 = $"{mw * tablePerMwh:#,0} t CO2/h";
            FuelBurnNote = $"estimated  ~0.34 t coal/MWh; CO2 at {TableText(tablePerMwh)} t/MWh (NESO carbon table)";
        }
        else if (biomass)
        {
            // Biomass ~0.55 t wood pellet/MWh. CO2 from the shared table, whose biomass figure is NESO's
            // supply-chain (consumption-based) value: the stack CO2 itself is biogenic.
            FuelBurnTitle = "FUEL BURN  BIOMASS";
            FuelBurnPrimary = $"{mw * 0.55:#,0.0}";
            FuelBurnPrimaryUnit = "t/h pellets";
            FuelBurnCo2 = $"{mw * tablePerMwh:#,0} t CO2/h  (supply chain; stack CO2 biogenic)";
            FuelBurnNote = $"estimated  ~0.55 t wood pellet/MWh; CO2 at {TableText(tablePerMwh)} t/MWh (NESO carbon table)";
        }
        else if (fi.Category == FuelCategory.Nuclear)
        {
            // Order-of-magnitude: ~3.9 g of U-235 fissioned per MWh of thermal-to-electric output.
            FuelBurnTitle = "FUEL  NUCLEAR";
            FuelBurnPrimary = $"{mw * 3.9:#,0}";
            FuelBurnPrimaryUnit = "g/h fissioned";
            FuelBurnCo2 = "no combustion CO2";
            FuelBurnNote = "estimated, order-of-magnitude  ~3.9 g U-235 fissioned per MWh";
        }
        else if (fi.Category is FuelCategory.Renewable or FuelCategory.Storage)
        {
            // No fuel: report the CO2 avoided vs the same energy from a CCGT.
            string kind = lower.Contains("hydro") || lower.Contains("pumped") ? "hydro"
                        : fi.Name.StartsWith("Wind", StringComparison.OrdinalIgnoreCase) ? "wind"
                        : fi.Name.StartsWith("Solar", StringComparison.OrdinalIgnoreCase) ? "solar"
                        : "this plant";
            FuelBurnTitle = "NO FUEL  CARBON AVOIDED";
            FuelBurnPrimary = $"{mw * ccgtCo2PerMwh:#,0}";
            FuelBurnPrimaryUnit = "t CO2/h avoided";
            FuelBurnCo2 = $"vs the same MWh from a CCGT";
            FuelBurnNote = $"estimated  {kind} burns no fuel; avoided at the CCGT {TableText(ccgtCo2PerMwh)} t CO2/MWh (NESO carbon table)";
        }
        else
        {
            FuelBurnTitle = "FUEL BURN";
            FuelBurnPrimary = " - ";
            FuelBurnPrimaryUnit = "";
            FuelBurnCo2 = " - ";
            FuelBurnNote = "no fuel-burn estimate for this fuel class";
        }
    }

    /// <summary>
    /// The subsystem register and event log are UI-first placeholders: the rows are
    /// shaped per fuel class so the page reads sensibly for the unit, and swapped for
    /// real plant telemetry once those feeds exist. Built once, on the first frame.
    /// </summary>
    private void BuildStatic(FuelCategory cls)
    {
        (string name, string status, string note)[] subs = cls switch
        {
            FuelCategory.Fossil => new[]
            {
                ("Rotor Train", "OK", "Shaft vibration 2.2 mm/s  3,000 rpm"),
                ("Bearing Oil", "OK", "Metal temp 78  degC  lube pressure nominal"),
                ("Exciter / AVR", "OK", "Voltage regulator in auto  field stable"),
                ("Protection", "WATCH", "Negative-sequence relay logging, no trip"),
            },
            FuelCategory.Nuclear => new[]
            {
                ("Main Generator", "OK", "1,008 MW  stator winding temps nominal"),
                ("Excitation", "OK", "Field current steady  PF 0.998"),
                ("Protection", "OK", "No active trip conditions  all zones healthy"),
                ("Operational Controls", "WATCH", "Restricted intervention  -  oversight required"),
            },
            FuelCategory.Renewable => RenewableSubs(),
            FuelCategory.Storage => new[]
            {
                ("PV / DC Blocks", "OK", "String currents balanced  41  degC"),
                ("Battery", "OK", "SoC 62%  cell spread 12 mV"),
                ("Inverter Farm", "OK", "Thermal margin healthy  no derate"),
                ("EMS", "WATCH", "Dispatch arbitrage active  fast-reserve armed"),
            },
            _ => new[]
            {
                ("Machine", "OK", "Operating within nameplate"),
                ("Excitation", "OK", "Field stable"),
                ("Protection", "OK", "No active trips"),
                ("Controls", "WATCH", "Mode under review"),
            },
        };

        foreach ((string n, string s, string note) in subs)
            Subsystems.Add(new SubsystemRow(n, s, note));

        // A short, plausible operational history for the unit.
        string[] log = cls switch
        {
            FuelCategory.Fossil => new[]
            {
                "06:12  Governor response to frequency dip  primary delivered",
                "05:48  AGC setpoint raised 540 -> 610 MW",
                "04:30  Routine bearing-oil sample  within limits",
                "02:15  Synchronised to grid  breaker closed",
                "01:50  Field flashing complete  excitation in auto",
            },
            FuelCategory.Nuclear => new[]
            {
                "06:00  Baseload hold  reactor power 100% FP",
                "04:22  Cooling-water inlet temp logged 14.1  degC",
                "03:10  Constraint-service instruction acknowledged",
                "00:30  AVR self-test passed  no flags",
                "Yesterday 22:05  Operator shift handover signed",
            },
            FuelCategory.Renewable => new[]
            {
                "06:18  PPC clip eased  export ramped to forecast",
                "05:40  Wind gust event  converter rode through",
                "04:05  Reactive setpoint trimmed for local voltage",
                "02:50  String 7 returned to service after inspection",
                "01:20  Curtailment instruction lifted",
            },
            FuelCategory.Storage => new[]
            {
                "06:25  Discharge into evening peak  180 MW",
                "05:10  Inverter farm temp check  all healthy",
                "03:30  SoC reached 62% after overnight charge",
                "02:00  Fast-reserve contract armed",
                "00:40  BMS balancing cycle completed",
            },
            _ => new[]
            {
                "06:00  Unit operating normally",
                "04:00  Routine telemetry check  nominal",
                "02:00  Synchronised to grid",
                "00:30  Controller self-test passed",
            },
        };

        foreach (string e in log)
        {
            int gap = e.IndexOf("  ", StringComparison.Ordinal);
            Events.Add(gap > 0 ? new EventRow(e[..gap], e[(gap + 2)..]) : new EventRow("", e));
        }
    }

    /// <summary>Hydro and wind both classify as Renewable  -  split the subsystem rows on the unit name.</summary>
    private (string, string, string)[] RenewableSubs()
    {
        string lower = name.ToLowerInvariant();
        bool hydro = lower.Contains("hydro") || lower.Contains("dinorwig") || lower.Contains("pumped");
        if (hydro)
            return new[]
            {
                ("Governor", "OK", "Fast reserve enabled  response < 10 s"),
                ("Wicket Gates", "OK", "Position 74%  servo pressure nominal"),
                ("Hydraulic", "WATCH", "Penstock transient logged  within limits"),
                ("Digital Control", "OK", "Unit in auto  setpoint tracking"),
            };
        return new[]
        {
            ("Turbine Strings", "OK", "Weather-led  94% available"),
            ("Converter", "OK", "Thermal margin healthy  no derate"),
            ("Export Control", "WATCH", "PPC clipping at boundary limit"),
            ("Reactive Plant", "OK", "STATCOM holding POI voltage"),
        };
    }
}

/// <summary>One generator tile on the phasor fleet page  -  compact phasor disc with key numbers.</summary>
public class PhasorTileModel : ObservableModel
{
    /// <summary>The unit's index in the model's generator list: the tile's identity (names repeat on gb-full).</summary>
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Fuel { get; init; } = "";
    /// <summary>The fuel chip's colour (was the WPF <c>FuelBrush</c>, default <c>Brushes.Gray</c>).</summary>
    public UiColour FuelColour { get; init; } = DashboardColours.Gray;
    /// <summary>What a click on the tile does (was the WPF <c>Open</c> command).</summary>
    public Action OnOpen { get; init; } = () => { };

    private double _p; public double P { get => _p; set => Set(ref _p, value); }
    private double _q; public double Q { get => _q; set => Set(ref _q, value); }
    private double _sMax; public double SMax { get => _sMax; set => Set(ref _sMax, value); }
    private string _output = ""; public string Output { get => _output; set => Set(ref _output, value); }
    private string _angle = ""; public string Angle { get => _angle; set => Set(ref _angle, value); }
    private string _voltage = ""; public string Voltage { get => _voltage; set => Set(ref _voltage, value); }
    private string _pf = ""; public string Pf { get => _pf; set => Set(ref _pf, value); }
    private string _leadLag = ""; public string LeadLag { get => _leadLag; set => Set(ref _leadLag, value); }
    // Was the WPF LeadLagBrush (default Brushes.Gray).
    private UiColour _leadLagColour = DashboardColours.Gray; public UiColour LeadLagColour { get => _leadLagColour; set => Set(ref _leadLagColour, value); }
}

/// <summary>The phasor fleet page: every generator's phasor side by side in a grid.</summary>
/// <typeparam name="TTile">The tile type the front end shows.</typeparam>
public class GeneratorPhasorFleetModel<TTile>(AssetExplorerModel explorer) : AssetPageModel where TTile : PhasorTileModel, new()
{
    private const int MaxTiles = 64;

    public override string Eyebrow => "GENERATOR PHASORS";
    public override string Title => "PHASOR MAP";
    public override string Sub => "every generator's voltage-current phasor  -  phase angle from the solved power flow";

    public ObservableCollection<TTile> Tiles { get; } = new();
    private readonly Dictionary<int, TTile> _byIndex = new();   // keyed by generator index, not name (R055)

    private string _totalOutput = " - "; public string TotalOutput { get => _totalOutput; set => Set(ref _totalOutput, value); }
    private string _online = " - "; public string Online { get => _online; set => Set(ref _online, value); }
    private string _angleSpread = " - "; public string AngleSpread { get => _angleSpread; set => Set(ref _angleSpread, value); }
    private string _countNote = ""; public string CountNote { get => _countNote; set => Set(ref _countNote, value); }

    public override void Refresh(SimFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GridModel m = frame.Model;
        var result = frame.Result;

        // The shared per-frame lookup, first-wins for a repeated bus id (ASSETS fix S3: the same TryAdd loop this page
        // built inline, moved to FrameLookup unchanged, so every value here is the same).
        Dictionary<int, double> vaById = FrameLookup.ByBusId(result.BusIds, result.VaDeg);
        Dictionary<int, double> vmById = FrameLookup.ByBusId(result.BusIds, result.Vm);

        IReadOnlyList<Generator> all = m.Generators;
        var indexed = new List<(Generator g, int idx)>();
        for (int i = 0; i < all.Count; i++)
            if (AssetIndex.IsPlant(all[i])) indexed.Add((all[i], i));
        var gens = indexed.Select(x => x.g).ToList();

        double totalMw = 0;
        int online = 0;
        double minAngle = double.MaxValue, maxAngle = double.MinValue;

        foreach (Generator g in gens)
        {
            totalMw += g.Pg;
            if (g.Pg > 0) online++;
            if (vaById.TryGetValue(g.Bus, out double va))
            {
                if (va < minAngle) minAngle = va;
                if (va > maxAngle) maxAngle = va;
            }
        }

        TotalOutput = $"{totalMw / 1000.0:F2} GW";
        Online = $"{online} of {gens.Count}";
        AngleSpread = minAngle < double.MaxValue ? $"{maxAngle - minAngle:F1} deg spread" : " - ";

        List<(Generator g, int idx)> shown = indexed.OrderByDescending(x => x.g.Pg).Take(MaxTiles).ToList();
        CountNote = gens.Count > MaxTiles ? $"showing top {MaxTiles} of {gens.Count} by output  -  8 x 8 grid" : "";

        // Prune tiles that left the top set, or whose index now holds a differently named unit (model swap).
        var keep = new Dictionary<int, string>();
        foreach ((Generator g, int idx) in shown) keep[idx] = NameOf(g);
        for (int i = Tiles.Count - 1; i >= 0; i--)
        {
            if (keep.TryGetValue(Tiles[i].Index, out string? nm) && nm == Tiles[i].Name) continue;
            _byIndex.Remove(Tiles[i].Index);
            Tiles.RemoveAt(i);
        }

        foreach ((Generator g, int idx) in shown)
        {
            UiFuelInfo fi = UiFuels.Classify(g.Type);
            string name = NameOf(g);
            bool capped = g.Pmax > 0 && !double.IsInfinity(g.Pmax);

            if (!_byIndex.TryGetValue(idx, out TTile? tile))
            {
                int unitBus = g.Bus;
                string? modelName = g.Name;
                tile = new TTile
                {
                    Index = idx,
                    Name = name,
                    Fuel = fi.Name,
                    FuelColour = fi.Colour,
                    OnOpen = () => explorer.OpenGenerator(idx, unitBus, modelName),
                };
                _byIndex[idx] = tile;
                Tiles.Add(tile);
            }

            // Quirk copied as is: the Q-apportioning block is the generator page's, duplicated (ASSETS plan section 2).
            double busQg = g.Qg;
            Bus? bus = m.Buses.FirstOrDefault(b => b.Id == g.Bus);
            if (bus is not null)
            {
                for (int i = 0; i < result.BusIds.Count; i++)
                    if (result.BusIds[i] == bus.Id)
                    {
                        double genQAtBus = result.QInjMvar[i] + bus.Qd;
                        double busPg = m.Generators.Where(x => x.Bus == g.Bus).Sum(x => x.Pg);
                        double sharePg = busPg > 1e-6 ? g.Pg / busPg : 1.0;
                        busQg = genQAtBus * sharePg;
                        break;
                    }
            }

            double s = Math.Sqrt(g.Pg * g.Pg + busQg * busQg);
            double pf = s > 1e-6 ? Math.Abs(g.Pg) / s : 1.0;
            bool lead = busQg < -1e-6, lag = busQg > 1e-6;

            tile.P = g.Pg;
            tile.Q = busQg;
            tile.SMax = capped ? Math.Max(g.Pmax, s) : Math.Max(s, 1);
            tile.Output = $"{g.Pg:#,0} MW";
            tile.Angle = vaById.TryGetValue(g.Bus, out double angle) ? $"{angle:F1} deg" : " - ";
            tile.Voltage = vmById.TryGetValue(g.Bus, out double vm) ? $"{vm:F3} pu" : " - ";
            tile.Pf = $"PF {pf:F3}";
            tile.LeadLag = lead ? "LEAD" : lag ? "LAG" : "UNITY";
            tile.LeadLagColour = ColourRamps.FromHex(lead ? "#21C7E6" : lag ? "#F3C13A" : "#8492A0");
        }
    }

    private static string NameOf(Generator g) => g.Name ?? $"unit @ bus {g.Bus}";
}
