using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.HumanScale;

/// <summary>
/// THE HUMAN SCALE TAB - the same grid physics the rest of the suite solves, re-expressed in units a
/// person can actually feel. It is deliberately NOT operational: nothing here feeds a decision. It exists
/// because "1.9 MNm of shaft torque" means nothing to most people, but "12 million family cars all flooring
/// it at once" does. Every number is a faithful conversion of a REAL streamed quantity (generation, system
/// inertia, RoCoF, losses) - the maths is honest, only the framing is for fun.
///
/// The anchors (a car's ~200 Nm, ~130 hp; a 60 kWh EV; an 8 kWh home-day; a 3 kW kettle) are stated inline
/// so the comparison is legible rather than magic. They are order-of-magnitude everyday values, not spec
/// sheets - the point is intuition, not precision.
///
/// <para>UI programme Phase 1.6 (third tab batch): this is the logic of the WPF <c>Views/HumanScaleVm.cs</c>, moved here
/// line for line so the Avalonia app shows the same tab from the same code. Every displayed string and number is
/// produced by the same formatting call, in the same culture, in the same order. The only changes are at the framework
/// boundary: each WPF <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes:
/// <c>RegenBrush</c> is now <see cref="RegenColour"/>, <c>TwistBrush</c> is <see cref="TwistColour"/>, and a row's
/// <c>Bar</c> is its <c>BarColour</c>), and the rows are made through <see cref="CreateRegionRow"/> and
/// <see cref="CreateMachineRow"/> so a front end can make its own row type. The WPF <c>HumanScaleVm</c> derives from
/// this class and maps the tokens back to brushes under the old names, so its XAML is unchanged.</para>
/// </summary>
public class HumanScaleModel : ObservableModel
{
    // ---- exact conversion constants ----
    private const double FtLbPerNm = 0.7375621493;      // 1 Nm  = 0.7376 ft.lb
    private const double HpPerMw = 1_000_000.0 / 745.699872;   // 1 MW  = 1341.02 mechanical hp
    private const double KwhPerGj = 1000.0 / 3.6;       // 1 GJ  = 277.78 kWh

    // ---- everyday anchors (order-of-magnitude, stated inline so the comparison is honest) ----
    private const double CarTorqueNm = 200.0;           // typical family car peak crank torque
    private const double CarHp = 130.0;                 // typical family car power
    private const double F1Hp = 1000.0;                 // a Formula 1 power unit
    private const double EvBatteryKwh = 60.0;           // a mid-size EV pack
    // One UK home for a day (~2,900 kWh/yr): the shared Core figure THE MACHINE uses (R205: was a duplicate 8.0).
    private const double HomeDayKwh = GridSim.Core.Presentation.EnergyEconomicsConstants.HomeKwhPerDay;
    private const double KettleBoilKwh = 0.10;          // energy to boil a full kettle (~1.5 L)
    private const double KettleKw = 3.0;                // a kettle on the boil
    private const double HomeMeanKw = 0.5;              // a home's average continuous draw
    private const double ShowerKw = 8.5;                // an electric shower running

    // ---- named physical anchors (R205: were literals; the same names and values as the web PresentationLimits,
    // pinned equal by PresentationConstantsAntiDriftTests) ----
    // (N273, stream B2: the nominal is the frame model's own, read per frame; the GB constant is no longer kept here.)
    /// <summary>A two-pole synchronous machine's speed (rpm) at nominal frequency.</summary>
    public const double TwoPoleSyncRpm = GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpm;   // N023: the one shared copy (was the literal 3000.0)
    /// <summary>Radius (m) of a large steam turbine's last-stage LP blade tip.</summary>
    public const double LpBladeTipRadiusM = GridSim.Core.Presentation.PresentationConstants.LpBladeTipRadiusM;   // N023: the one shared copy (was the literal 0.9)
    /// <summary>Speed of sound in air (m/s) at about 20 C.</summary>
    public const double SpeedOfSoundMs = GridSim.Core.Presentation.PresentationConstants.SpeedOfSoundMs;   // N023: the one shared copy (was the literal 343.0)
    /// <summary>|RoCoF| (Hz/s) below which the stored rotor energy reads as coasting.</summary>
    public const double RegenDeadbandHzPerS = GridSim.Core.Presentation.PresentationConstants.RegenDeadbandHzPerS;   // N023: the one shared copy (was the literal 1e-4)

    // The tab's fixed colours: the WPF frozen brushes of Palette.Voltage(1.0), Palette.Loading(0.85) and #8492A0.
    private static readonly UiColour GreenB = ColourRamps.Voltage(1.0);
    private static readonly UiColour AmberB = ColourRamps.Loading(0.85);
    private static readonly UiColour MutedB = ColourRamps.FromHex("#8492A0");

    private string _clock = "-"; public string Clock { get => _clock; set => Set(ref _clock, value); }

    // ======== THE HEADLINE CLAIM: the whole grid stated as one absurd-but-true fact ========
    private string _heroValue = "-"; public string HeroValue { get => _heroValue; set => Set(ref _heroValue, value); }
    private string _heroLine = "-"; public string HeroLine { get => _heroLine; set => Set(ref _heroLine, value); }

    // ======== ABSURD BUT TRUE: factual things said in ridiculous ways ========
    private string _fact1 = "-"; public string Fact1 { get => _fact1; set => Set(ref _fact1, value); }
    private string _fact2 = "-"; public string Fact2 { get => _fact2; set => Set(ref _fact2, value); }
    private string _fact3 = "-"; public string Fact3 { get => _fact3; set => Set(ref _fact3, value); }
    private string _fact4 = "-"; public string Fact4 { get => _fact4; set => Set(ref _fact4, value); }
    private string _fact5 = "-"; public string Fact5 { get => _fact5; set => Set(ref _fact5, value); }

    // ======== THE GRAND ENGINE: the whole grid as one giant motor ========
    private string _torqueNative = "-"; public string TorqueNative { get => _torqueNative; set => Set(ref _torqueNative, value); }
    private string _torqueFtLb = "-"; public string TorqueFtLb { get => _torqueFtLb; set => Set(ref _torqueFtLb, value); }
    private string _torqueCars = "-"; public string TorqueCars { get => _torqueCars; set => Set(ref _torqueCars, value); }

    private string _shaftHp = "-"; public string ShaftHp { get => _shaftHp; set => Set(ref _shaftHp, value); }
    private string _shaftNative = "-"; public string ShaftNative { get => _shaftNative; set => Set(ref _shaftNative, value); }
    private string _shaftCars = "-"; public string ShaftCars { get => _shaftCars; set => Set(ref _shaftCars, value); }

    private string _gridRpm = "-"; public string GridRpm { get => _gridRpm; set => Set(ref _gridRpm, value); }
    private string _bladeTip = "-"; public string BladeTip { get => _bladeTip; set => Set(ref _bladeTip, value); }

    // ======== REGENERATIVE BRAKING: the spinning mass as an energy bank ========
    private string _spinKwh = "-"; public string SpinKwh { get => _spinKwh; set => Set(ref _spinKwh, value); }
    private string _spinNative = "-"; public string SpinNative { get => _spinNative; set => Set(ref _spinNative, value); }
    private string _spinCars = "-"; public string SpinCars { get => _spinCars; set => Set(ref _spinCars, value); }
    private string _spinHomes = "-"; public string SpinHomes { get => _spinHomes; set => Set(ref _spinHomes, value); }
    private string _spinKettles = "-"; public string SpinKettles { get => _spinKettles; set => Set(ref _spinKettles, value); }

    private string _regenHp = "-"; public string RegenHp { get => _regenHp; set => Set(ref _regenHp, value); }
    private string _regenDir = "-"; public string RegenDir { get => _regenDir; set => Set(ref _regenDir, value); }
    /// <summary>The live braking power's colour (was the WPF <c>RegenBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _regenColour = DashboardColours.Gray; public UiColour RegenColour { get => _regenColour; set => Set(ref _regenColour, value); }
    private string _regenNote = "-"; public string RegenNote { get => _regenNote; set => Set(ref _regenNote, value); }
    private string _rocofRpmS = "-"; public string RocofRpmS { get => _rocofRpmS; set => Set(ref _rocofRpmS, value); }

    // ======== IN THINGS YOU CAN HOLD: demand + losses as household appliances ========
    private string _demandKettles = "-"; public string DemandKettles { get => _demandKettles; set => Set(ref _demandKettles, value); }
    private string _demandHomes = "-"; public string DemandHomes { get => _demandHomes; set => Set(ref _demandHomes, value); }
    private string _demandShowers = "-"; public string DemandShowers { get => _demandShowers; set => Set(ref _demandShowers, value); }
    private string _demandNative = "-"; public string DemandNative { get => _demandNative; set => Set(ref _demandNative, value); }

    private string _lossHomes = "-"; public string LossHomes { get => _lossHomes; set => Set(ref _lossHomes, value); }
    private string _lossNative = "-"; public string LossNative { get => _lossNative; set => Set(ref _lossNative, value); }
    private string _lossKettles = "-"; public string LossKettles { get => _lossKettles; set => Set(ref _lossKettles, value); }

    // ======== THE TWIST: the crankshaft is 1,000 km of torsion bar. Rotor angle IS the transmission. ========
    // sin-delta redline. GB never nears 90 (transient limits bite first) but the wall is real: past it the
    // twist runs away and the machine pole-slips. This is the only reading on the tab with physics behind it.
    private const double PoleSlipDeg = 90.0;              // sin-delta peaks here; beyond it the shaft lets go
    private const double RegionTrack = 230.0;            // px, region bar scaled 0..90 (so the wall is in shot)

    private string _twistTotal = "-"; public string TwistTotal { get => _twistTotal; set => Set(ref _twistTotal, value); }
    private string _twistToSlip = "-"; public string TwistToSlip { get => _twistToSlip; set => Set(ref _twistToSlip, value); }
    private double _twistPct; public double TwistPct { get => _twistPct; set => Set(ref _twistPct, value); }
    private double _twistBarPx; public double TwistBarPx { get => _twistBarPx; set => Set(ref _twistBarPx, value); }
    /// <summary>The wind-up headline and bar colour (was the WPF <c>TwistBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _twistColour = DashboardColours.Gray; public UiColour TwistColour { get => _twistColour; set => Set(ref _twistColour, value); }
    private string _twistLead = "-"; public string TwistLead { get => _twistLead; set => Set(ref _twistLead, value); }
    private string _twistCorridor = "-"; public string TwistCorridor { get => _twistCorridor; set => Set(ref _twistCorridor, value); }

    /// <summary>Rotor angle by map latitude band, relative to the southern grid - the geographic twist.</summary>
    public ObservableCollection<TwistRegionRowModel> Regions { get; } = new();
    /// <summary>Every synchronous machine, ranked by how far its rotor leads the south. Electrical deg = mechanical deg for the 2-pole sets.</summary>
    public ObservableCollection<TwistMachineRowModel> Machines { get; } = new();
    private readonly Dictionary<string, TwistMachineRowModel> _machineByKey = new();   // "index:bus:name", never the name alone

    public void Update(SimFrame f, string clock)
    {
        Clock = clock;

        double genMw = f.GenerationMw;
        double demandMw = f.DemandMw;
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the frame's own nominal (50.0, the
        // constant, for every GB case).
        double nominalHz = f.Model.FrequencyHz;
        double freq = f.FrequencyHz > 1 ? f.FrequencyHz : nominalHz;
        double omega = 2.0 * Math.PI * freq;                 // electrical rad/s (== 2-pole shaft rad/s)
        double storedGj = f.Model.SystemInertiaGvaS;         // GVA.s ~= GJ of stored kinetic energy
        double rocof = f.RocofHzPerS;
        double lossMw = f.Result.TotalLossMw;

        // ---- THE GRAND ENGINE ----
        // Shaft torque of the whole synchronised fleet: T = P / omega.
        double torqueNm = omega > 0 ? genMw * 1e6 / omega : 0;
        double torqueFtLb = torqueNm * FtLbPerNm;
        TorqueNative = $"{torqueNm / 1e6:F2} MN.m";
        TorqueFtLb = $"{Humanise(torqueFtLb)} ft.lb";
        TorqueCars = $"the shaft twists like {Humanise(torqueNm / CarTorqueNm)} family cars flooring it at once  (~200 Nm each)";

        double shaftHp = genMw * HpPerMw;
        ShaftHp = $"{Humanise(shaftHp)} hp";
        ShaftNative = $"{genMw / 1000.0:F2} GW on the shaft";
        ShaftCars = $"= {Humanise(shaftHp / CarHp)} family cars (130 hp)   or   {Humanise(shaftHp / F1Hp)} Formula 1 cars (1000 hp)";

        double rpm = freq / nominalHz * GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);   // 2-pole: 50 Hz -> 3000 rpm, 60 Hz -> 3600
        GridRpm = $"{rpm:F0} rpm";
        // A last-stage LP turbine blade tip, ~0.9 m radius, at this speed - vs the speed of sound (343 m/s).
        double tipMs = omega * LpBladeTipRadiusM;
        BladeTip = $"blade tips ~{tipMs:F0} m/s  -  about Mach {tipMs / SpeedOfSoundMs:F1}";

        // ---- REGENERATIVE BRAKING ----
        double spinKwh = storedGj * KwhPerGj;
        SpinKwh = $"{Humanise(spinKwh)} kWh";
        SpinNative = $"{storedGj:F0} GJ spinning in every generator";
        SpinCars = $"= {Humanise(spinKwh / EvBatteryKwh)} EVs charged flat-to-full (60 kWh)";
        SpinHomes = $"= {Humanise(spinKwh / HomeDayKwh)} homes powered for a whole day (8 kWh)";
        SpinKettles = $"= {Humanise(spinKwh / KettleBoilKwh)} kettle boils";

        // The real regen number: power the spinning mass exchanges to arrest the frequency change,
        // P = 2 * KE * (df/dt) / f. Falling frequency = the flywheel delivering; rising = absorbing.
        double regenMw = 2.0 * (storedGj * 1000.0) * Math.Abs(rocof) / freq;
        double regenHp = regenMw * HpPerMw;
        RegenHp = $"{Humanise(regenHp)} hp";
        bool delivering = rocof < -RegenDeadbandHzPerS, absorbing = rocof > RegenDeadbandHzPerS;
        RegenDir = delivering ? "DELIVERING  (frequency falling)"
                 : absorbing ? "SOAKING UP  (frequency rising)"
                 : "COASTING  (frequency steady)";
        RegenColour = delivering ? AmberB : absorbing ? GreenB : MutedB;
        RegenNote = Math.Abs(rocof) < RegenDeadbandHzPerS
            ? "the grand flywheel is holding its speed - no net braking right now"
            : $"the flywheel is trading {regenMw:F0} MW with the grid to fight a {Math.Abs(rocof):F3} Hz/s drift";
        RocofRpmS = $"{Math.Abs(rocof) * 60.0:F1} rpm/s";     // Hz/s on the 3000-rpm reference

        // ---- IN THINGS YOU CAN HOLD ----
        DemandNative = $"{demandMw / 1000.0:F2} GW being drawn right now";
        DemandKettles = $"{Humanise(demandMw * 1000.0 / KettleKw)} kettles on the boil";
        DemandHomes = $"{Humanise(demandMw * 1000.0 / HomeMeanKw)} homes' worth of draw";
        DemandShowers = $"{Humanise(demandMw * 1000.0 / ShowerKw)} electric showers running";

        LossNative = $"{lossMw:F0} MW lost as heat in the wires";
        LossHomes = $"enough to run {Humanise(lossMw * 1000.0 / HomeMeanKw)} homes - just warming the cables";
        LossKettles = $"= {Humanise(lossMw * 1000.0 / KettleKw)} kettles, boiled for nothing";

        // ---- THE HEADLINE CLAIM ----
        // The whole synchronised fleet is, physically, a single engine. So say so.
        HeroValue = $"{Humanise(shaftHp)} hp";
        HeroLine = $"Every synchronous generator in Britain is bolted to one invisible crankshaft turning at "
                 + $"{rpm:F0} rpm. Right now it develops {Humanise(shaftHp)} horsepower - comfortably the most "
                 + "powerful engine in the country, and nobody can see it.";

        // ---- ABSURD BUT TRUE: factual things, stated ridiculously ----
        Fact1 = $"Its crankshaft is wrung with {Humanise(torqueFtLb)} ft.lb of torque - the same as "
              + $"{Humanise(torqueNm / CarTorqueNm)} family cars flooring it in perfect unison.";
        Fact2 = $"Slam the brakes on it and you would claw back {Humanise(spinKwh)} kWh of spin - enough to "
              + $"charge {Humanise(spinKwh / EvBatteryKwh)} electric cars from dead flat.";
        Fact3 = $"The rotor is turning so fast its turbine-blade tips move at Mach {(omega * LpBladeTipRadiusM / SpeedOfSoundMs):F1} - "
              + "give or take, the speed of sound.";
        Fact4 = $"At this exact second Britain is boiling the equivalent of {Humanise(demandMw * 1000.0 / KettleKw)} kettles.";

        UpdateTwist(f);
        Fact5 = Math.Abs(rocof) < RegenDeadbandHzPerS
            ? $"{lossMw:F0} MW is leaking away as pure heat in the wires - enough to run "
              + $"{Humanise(lossMw * 1000.0 / HomeMeanKw)} homes on waste alone."
            : $"The flywheel is trading {regenMw:F0} MW with the grid to hold its speed - a {Humanise(regenHp)} hp "
              + (delivering ? "shove to stop it slowing." : "brake to stop it running away.");
    }

    /// <summary>
    /// Format a bare count as a rounded, comma-grouped whole number (N0): 49,876,321. The full run of
    /// digits is the joke - a precise-sounding absurd number reads better than "~50 million".
    /// </summary>
    private static string Humanise(double v) => Math.Round(Math.Abs(v)).ToString("N0");

    /// <summary>
    /// THE TWIST. Every bus already carries a solved voltage angle (degrees). Referenced to the southern
    /// grid, that angle is the physical wind-up of the 1,000 km torsion bar - and for the 2-pole 3,000-rpm
    /// sets it is literally the rotor's mechanical lead. Power south = (V1.V2/X).sin(delta): the angle IS
    /// the transmission. We bucket buses by map latitude for the regional gauge, and rank the synchronous
    /// machines individually. The redline is 90 degrees - the sin-delta peak, where the shaft lets go.
    /// </summary>
    private void UpdateTwist(SimFrame f)
    {
        GridModel model = f.Model;
        PowerFlowResult r = f.Result;
        // 2026-10-24: the key index is cleared with the rows. It used to survive, so after a frame with no bus angles every
        // machine of the next frame was "already known" and never re-added: the ranked list stayed empty until a new case.
        if (r.VaDeg is null || r.VaDeg.Length == 0 || r.BusIds.Count != r.VaDeg.Length) { Regions.Clear(); Machines.Clear(); _machineByKey.Clear(); return; }

        var angleByBus = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) angleByBus[r.BusIds[i]] = r.VaDeg[i];

        // Bucket buses into latitude bands. GB models name their buses ("North Scotland", "Midlands",
        // "London & South East ..."), so key off the NAME - unambiguous, and immune to the north-up vs
        // north-down coordinate question. Abstract cases (IEEE) fall back to Y-terciles with neutral labels.
        var bandOfBus = new Dictionary<int, int>();
        foreach (Bus b in model.Buses)
        {
            int band = GbBand(b.Name);
            if (band >= 0) bandOfBus[b.Id] = band;
        }
        bool gb = bandOfBus.ContainsValue(0) && bandOfBus.ContainsValue(GbBands.Length - 1);
        string[] labels;
        if (gb) labels = GbBands.Select(x => x.Label).ToArray();
        else
        {
            // Y-tercile fallback: orientation is arbitrary for a synthetic layout, so label neutrally.
            labels = new[] { "TOP OF THE NETWORK", "THE MIDDLE", "BOTTOM OF THE NETWORK" };
            bandOfBus.Clear();
            var ys = model.Buses.Where(b => angleByBus.ContainsKey(b.Id)).ToList();
            if (ys.Count > 0)
            {
                double ymin = ys.Min(b => b.Y), ymax = ys.Max(b => b.Y), span = Math.Max(1e-9, ymax - ymin);
                foreach (Bus b in ys)
                {
                    double t = 1.0 - (b.Y - ymin) / span;       // 0 = top band, 1 = bottom band
                    bandOfBus[b.Id] = Math.Min(2, (int)(t * 3));
                }
            }
        }

        // Reference to the SOUTH (last band): its mean angle is the gauge zero, so the north reads as a
        // positive lead - the physical picture (power flows south, so northern rotors run ahead). Stated on the panel.
        int southBand = labels.Length - 1;
        var southDegs = bandOfBus.Where(kv => kv.Value == southBand).Select(kv => angleByBus[kv.Key]).ToList();
        double refDeg = southDegs.Count > 0 ? southDegs.Average()
                      : angleByBus.Count > 0 ? angleByBus.Values.Average() : 0.0;

        // ---- regional gauge: mean rotor lead per band, vs the south ----
        Regions.Clear();
        for (int band = 0; band < labels.Length; band++)
        {
            var degs = bandOfBus.Where(kv => kv.Value == band).Select(kv => angleByBus[kv.Key] - refDeg).ToList();
            if (degs.Count == 0) continue;
            double mean = degs.Average();
            // The WPF code built the row with an object initialiser and then added it; the same values are set here, in
            // the same order, on the row the front end makes (CreateRegionRow), before it is added.
            TwistRegionRowModel region = CreateRegionRow();
            region.Label = labels[band];
            region.DegText = $"{mean:+0.0;-0.0;0.0} deg";
            region.Detail = $"{degs.Count} buses  *  spans {degs.Min():+0;-0;0} to {degs.Max():+0;-0;0} deg";
            region.BarWidth = Math.Min(1.0, Math.Abs(mean) / PoleSlipDeg) * RegionTrack;
            region.BarColour = ColourRamps.Loading(Math.Abs(mean) / 60.0);
            Regions.Add(region);
        }

        // ---- individual synchronous machines, ranked by lead. For 2-pole sets, this is real rotor twist. ----
        // Rows are keyed by the generator's INDEX (with its bus and name), as GeneratorFleetVm does: names repeat (gb-full
        // has many "- chp" units), and keying by name collapsed namesakes into one row carrying the last one's numbers.
        var mach = new List<(string Key, string Name, double Deg, double Mw)>();
        for (int gi = 0; gi < model.Generators.Count; gi++)
        {
            Generator g = model.Generators[gi];
            if (!UiFuels.Classify(g.Type).Synchronous || g.Pg < GridSim.Core.Presentation.MechanicalThresholds.MinSyncMachineMw) continue;
            if (!angleByBus.TryGetValue(g.Bus, out double a)) continue;
            mach.Add(($"{gi}:{g.Bus}:{g.Name}", g.Name ?? $"{UiFuels.Classify(g.Type).Name}@{g.Bus}", a - refDeg, g.Pg));
        }
        mach.Sort((a, b) => b.Deg.CompareTo(a.Deg));   // most-advanced first (List.Sort is not stable; ties are equal leads)
        double machMax = Math.Max(30.0, mach.Count > 0 ? mach.Max(m => Math.Abs(m.Deg)) : 30.0);

        var seen = new HashSet<string>();
        foreach ((string key, string name, double deg, double mw) in mach)
        {
            seen.Add(key);
            if (!_machineByKey.TryGetValue(key, out TwistMachineRowModel? row))
            {
                row = CreateMachineRow(key, name);
                _machineByKey[key] = row;
                Machines.Add(row);
            }
            row.DegText = $"{deg:+0.0;-0.0;0.0} deg";
            row.Detail = $"{mw:N0} MW";
            row.BarWidth = Math.Min(1.0, Math.Abs(deg) / machMax) * 150.0;
            row.BarColour = ColourRamps.Loading(Math.Abs(deg) / 60.0);
        }
        for (int i = Machines.Count - 1; i >= 0; i--)
            if (!seen.Contains(Machines[i].Key)) { _machineByKey.Remove(Machines[i].Key); Machines.RemoveAt(i); }
        // keep most-advanced first
        var ordered = Machines.OrderByDescending(m => ParseDeg(m.DegText)).ToList();
        for (int i = 0; i < ordered.Count; i++)
            if (!ReferenceEquals(Machines[i], ordered[i])) Machines.Move(Machines.IndexOf(ordered[i]), i);

        // ---- headline: total end-to-end wind-up, and the widest angle across ONE branch against the 90-degree peak ----
        double lo2 = angleByBus.Values.Min() - refDeg, hi2 = angleByBus.Values.Max() - refDeg;
        double endToEnd = hi2 - lo2;
        TwistTotal = $"{endToEnd:F1} deg";
        // The sin(delta) transfer peak at 90 degrees applies to the angle across ONE series reactance, not to a bus's
        // angle against the southern mean through many branches: a meshed case with 60 degrees end to end can have every
        // branch far from its limit. So the read-out takes the largest angle across a single in-service branch (from-end
        // angle, less any phase shift, minus to-end angle), and names that branch. It used to compare the worst bus's
        // angle relative to the south with 90 degrees. This is still a bus-angle (steady-state) margin, not a rotor or
        // small-signal one, and the panel says "across one branch".
        double worst = 0; string worstBranch = "-";
        foreach (Branch br in model.Branches)
        {
            if (!br.InService) continue;
            if (!angleByBus.TryGetValue(br.From, out double af) || !angleByBus.TryGetValue(br.To, out double at)) continue;
            double across = Math.Abs(af - br.PhaseShiftDeg - at);
            if (across > worst) { worst = across; worstBranch = br.Name ?? $"{br.From}-{br.To}"; }
        }
        double pct = worst / PoleSlipDeg;
        TwistPct = Math.Min(1.0, pct) * 100.0;
        TwistBarPx = Math.Min(1.0, pct) * RegionTrack;   // fills toward the 90 deg redline on a 230px track
        TwistToSlip = $"widest angle across one branch {worst:F1} deg ({worstBranch}): {pct * 100:F0}% of the 90 deg sin(delta) peak  *  {PoleSlipDeg - worst:F0} deg of headroom";
        TwistColour = ColourRamps.Loading(worst / 60.0);

        if (mach.Count > 0)
        {
            (string Key, string Name, double Deg, double Mw) top = mach[0];
            TwistLead = $"{top.Name} is furthest ahead, its rotor held {top.Deg:+0.0;-0.0} deg in front of the south while it pushes {top.Mw:N0} MW down the shaft.";
        }
        else TwistLead = "No synchronous machines online to twist the shaft.";

        // biggest adjacent-band gap = the working corridor carrying power south
        if (Regions.Count >= 2)
        {
            double best = 0; string a = "", b = "";
            for (int i = 0; i < Regions.Count - 1; i++)
            {
                double gap = ParseDeg(Regions[i].DegText) - ParseDeg(Regions[i + 1].DegText);
                if (Math.Abs(gap) > Math.Abs(best)) { best = gap; a = Regions[i].Label; b = Regions[i + 1].Label; }
            }
            TwistCorridor = $"The working twist sits between {a} and {b}: {Math.Abs(best):F1} deg of wind-up, and every megawatt crossing it is carried by that angle.";
        }
        else TwistCorridor = "-";
    }

    private static double ParseDeg(string s) =>
        double.TryParse(s.EndsWith(" deg", StringComparison.Ordinal) ? s[..^4] : s, out double d) ? d : 0.0;   // N310: the text is "+12.3 deg" (it was "+12.3" and a degree sign)

    // GB latitude bands, north -> south, matched on the bus name keywords the GB models actually use.
    private static readonly (string Label, string[] Keys)[] GbBands =
    {
        ("SCOTLAND",           new[] { "scotland" }),
        ("NORTHERN ENGLAND",   new[] { "north england", "yorkshire", "humber", "mersey", "north wales" }),
        ("THE MIDLANDS",       new[] { "midlands", "east anglia" }),
        ("LONDON & THE SOUTH", new[] { "london", "south" }),   // South East / South West / S Wales / South
    };

    /// <summary>Classify a bus name into a GB latitude band (0 = Scotland .. last = South), or -1 if it isn't a GB name.</summary>
    private static int GbBand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        string n = name.ToLowerInvariant();
        for (int i = 0; i < GbBands.Length; i++)
            foreach (string key in GbBands[i].Keys)
                if (n.Contains(key)) return i;
        return -1;
    }

    /// <summary>A new, empty latitude-band row (a front end overrides this to make its own row type).</summary>
    protected virtual TwistRegionRowModel CreateRegionRow() => new();

    /// <summary>A new machine row with its fixed identity (a front end overrides this to make its own row type).</summary>
    protected virtual TwistMachineRowModel CreateMachineRow(string key, string name) => new() { Key = key, Name = name };
}

/// <summary>
/// One latitude band's mean rotor lead, for THE TWIST regional gauge.
/// <para>UI programme Phase 1.6: the WPF <c>TwistRegionRow</c>, with <c>Bar</c> now the colour token
/// <see cref="BarColour"/> (default <c>Brushes.Gray</c>).</para>
/// </summary>
public class TwistRegionRowModel : ObservableModel
{
    private string _label = ""; public string Label { get => _label; set => Set(ref _label, value); }
    private string _degText = ""; public string DegText { get => _degText; set => Set(ref _degText, value); }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
}

/// <summary>
/// One synchronous machine's rotor lead vs the south, ranked in THE TWIST.
/// <para>UI programme Phase 1.6: the WPF <c>TwistMachineRow</c>, with <c>Bar</c> now the colour token
/// <see cref="BarColour"/> (default <c>Brushes.Gray</c>).</para>
/// </summary>
public class TwistMachineRowModel : ObservableModel
{
    /// <summary>The row's identity: "index:bus:name" of the generator (names repeat, so never the name alone).</summary>
    public string Key { get; init; } = "";
    private string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    private string _degText = ""; public string DegText { get => _degText; set => Set(ref _degText, value); }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private double _barWidth; public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    private UiColour _barColour = DashboardColours.Gray; public UiColour BarColour { get => _barColour; set => Set(ref _barColour, value); }
}
