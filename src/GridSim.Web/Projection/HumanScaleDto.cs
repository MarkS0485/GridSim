using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Visuals;

namespace GridSim.Web.Projection;

// THE HUMAN SCALE tab - the same solved physics re-expressed in units a person can feel. Port of the WPF
// HumanScaleVm. Every number is a faithful conversion of a REAL streamed quantity (generation, inertia,
// RoCoF, losses, bus voltage angles); the maths is honest, only the framing is for fun. This DTO carries the
// RAW numbers - the razor component does the "12 million cars" phrasing.

/// <summary>One latitude band's mean rotor lead vs the reference (south), for THE TWIST regional gauge.</summary>
public sealed record TwistRegionDto(string Label, double MeanLeadDeg, int BusCount, double MinLeadDeg, double MaxLeadDeg);

/// <summary>One synchronous machine's rotor lead vs the reference, ranked most-advanced first. For the 2-pole
/// 3,000-rpm sets this lead is literally the shaft's mechanical wind-up.</summary>
public sealed record TwistMachineDto(string Name, double LeadDeg, double Mw);

/// <summary>The whole synchronised fleet as one giant motor: shaft torque, power, speed, blade-tip speed.</summary>
public sealed record GrandEngineDto(
    double ShaftHp, double ShaftGw, double TorqueMNm, double TorqueFtLb, double Rpm, double BladeTipMs, double BladeTipMach);

/// <summary>The spinning mass as an energy bank + the live regen exchange arresting the frequency change.</summary>
public sealed record RegenDto(
    double SpinKwh, double SpinGj, double RegenMw, double RegenHp, string Direction, double RocofRpmPerS);

/// <summary>Demand / losses / stored spin restated as everyday appliances (raw counts; the anchors are the
/// same order-of-magnitude values the WPF twin states inline).</summary>
public sealed record RelatableUnitsDto(
    double DemandKettles, double DemandHomes, double DemandShowers,
    double LossHomes, double LossKettles,
    double SpinEvs, double SpinHomeDays, double SpinKettleBoils);

/// <summary>THE HUMAN SCALE rollup: the grand engine, the regen bank, the appliance framing, and THE TWIST
/// (per-region + per-machine rotor lead, total wind-up, and headroom to the 90-degree pole-slip wall).</summary>
public sealed record HumanScaleDto(
    GrandEngineDto Engine,
    RegenDto Regen,
    RelatableUnitsDto Units,
    double TotalTwistDeg,
    double WorstLeadDeg,
    double DistanceTo90Deg,
    double PctToPoleSlip,
    IReadOnlyList<TwistRegionDto> Regions,
    IReadOnlyList<TwistMachineDto> Machines);

/// <summary>Projects a solved frame into THE HUMAN SCALE numbers: engine, regen, relatable units, the machine-lead
/// ranking and the GB latitude banding (THE TWIST regional gauge) are all wired. (R099: this summary used to call the
/// banding a TODO although it is implemented.)</summary>
public static class HumanScaleProjection
{
    // Exact conversion constants (mirror HumanScaleVm).
    private const double FtLbPerNm = 0.7375621493;
    private const double HpPerMw = 1_000_000.0 / 745.699872;   // 1 MW = 1341.02 mechanical hp
    private const double KwhPerGj = 1000.0 / 3.6;              // 1 GJ = 277.78 kWh
    // Everyday anchors (order-of-magnitude).
    private const double KettleKw = 3.0, HomeMeanKw = 0.5, ShowerKw = 8.5;
    private const double EvBatteryKwh = 60.0, KettleBoilKwh = 0.10;
    // A home's daily draw: the shared Core figure THE MACHINE uses too (R205: was a duplicate literal 8.0).
    private const double HomeDayKwh = GridSim.Core.Presentation.EnergyEconomicsConstants.HomeKwhPerDay;
    private const double PoleSlipDeg = 90.0;                   // sin-delta peak; beyond it the shaft lets go

    /// <summary><paramref name="inertiaGvaSOverride"/> supplies the REAL system inertia for the raw replay (where
    /// the frame's model is a static topology whose baked inertia isn't the live figure); null falls back to the
    /// model's inertia for solved/live frames.</summary>
    public static HumanScaleDto Project(SimFrame f, double? inertiaGvaSOverride = null)
    {
        double genMw = f.GenerationMw;
        double demandMw = f.DemandMw;
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the frame model's own nominal (50.0, the
        // constant, for every GB case).
        double nominalHz = f.Model.FrequencyHz;
        double freq = f.FrequencyHz > 1 ? f.FrequencyHz : nominalHz;
        double omega = 2.0 * Math.PI * freq;
        double storedGj = inertiaGvaSOverride is double io && io > 0 ? io : f.Model.SystemInertiaGvaS;   // GVA.s ~= GJ of stored kinetic energy
        double rocof = f.RocofHzPerS;
        double lossMw = f.Result.TotalLossMw;

        // ---- THE GRAND ENGINE ----
        double torqueNm = omega > 0 ? genMw * 1e6 / omega : 0;
        double rpm = freq / nominalHz * GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);
        double tipMs = omega * PresentationLimits.LpBladeTipRadiusM;   // ~0.9 m LP blade radius
        var engine = new GrandEngineDto(
            ShaftHp: genMw * HpPerMw, ShaftGw: genMw / 1000.0,
            TorqueMNm: torqueNm / 1e6, TorqueFtLb: torqueNm * FtLbPerNm,
            Rpm: rpm, BladeTipMs: tipMs, BladeTipMach: tipMs / PresentationLimits.SpeedOfSoundMs);

        // ---- REGENERATIVE BRAKING ----
        double spinKwh = storedGj * KwhPerGj;
        double regenMw = freq > 0 ? 2.0 * (storedGj * 1000.0) * Math.Abs(rocof) / freq : 0;
        string dir = rocof < -PresentationLimits.RegenDeadbandHzPerS ? "DELIVERING"
            : rocof > PresentationLimits.RegenDeadbandHzPerS ? "SOAKING UP" : "COASTING";
        var regen = new RegenDto(spinKwh, storedGj, regenMw, regenMw * HpPerMw, dir, Math.Abs(rocof) * 60.0);

        // ---- IN THINGS YOU CAN HOLD ----
        var units = new RelatableUnitsDto(
            DemandKettles: demandMw * 1000.0 / KettleKw,
            DemandHomes: demandMw * 1000.0 / HomeMeanKw,
            DemandShowers: demandMw * 1000.0 / ShowerKw,
            LossHomes: lossMw * 1000.0 / HomeMeanKw,
            LossKettles: lossMw * 1000.0 / KettleKw,
            SpinEvs: spinKwh / EvBatteryKwh,
            SpinHomeDays: spinKwh / HomeDayKwh,
            SpinKettleBoils: spinKwh / KettleBoilKwh);

        // ---- THE TWIST (rotor lead from the solved bus angles) ----
        (double total, double worst, IReadOnlyList<TwistMachineDto> machines, IReadOnlyList<TwistRegionDto> regions)
            = Twist(f.Model, f.Result);
        double pct = worst / PoleSlipDeg;

        return new HumanScaleDto(
            engine, regen, units,
            TotalTwistDeg: total, WorstLeadDeg: worst,
            DistanceTo90Deg: PoleSlipDeg - worst, PctToPoleSlip: Math.Min(1.0, pct) * 100.0,
            Regions: regions,
            Machines: machines);
    }

    /// <summary>Machine-lead ranking, per-region rotor lead, and end-to-end wind-up from the solved bus angles.
    /// All leads are referenced to the SOUTH band's mean angle so northern rotors read as a positive lead -
    /// the physical picture (power flows south, so northern rotors run ahead).</summary>
    private static (double Total, double Worst, IReadOnlyList<TwistMachineDto> Machines, IReadOnlyList<TwistRegionDto> Regions)
        Twist(GridModel model, PowerFlowResult r)
    {
        if (r.VaDeg is null || r.VaDeg.Length == 0 || r.BusIds.Count != r.VaDeg.Length)
            return (0, 0, Array.Empty<TwistMachineDto>(), Array.Empty<TwistRegionDto>());

        var angleByBus = new Dictionary<int, double>(r.BusIds.Count);
        for (int i = 0; i < r.BusIds.Count; i++) angleByBus[r.BusIds[i]] = r.VaDeg[i];

        // ---- latitude banding: GB models name their buses ("North Scotland", "Midlands", "London & South
        // East ..."), so key off the NAME - unambiguous, and immune to the north-up vs north-down coordinate
        // question. Abstract layouts (IEEE) fall back to Y-terciles with neutral labels. Ports UpdateTwist. ----
        var bandOfBus = new Dictionary<int, int>();
        foreach (Bus b in model.Buses)
        {
            int band = GbBand(b.Name);
            if (band >= 0 && angleByBus.ContainsKey(b.Id)) bandOfBus[b.Id] = band;
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
                    double t = 1.0 - (b.Y - ymin) / span;      // 0 = top band, 1 = bottom band
                    bandOfBus[b.Id] = Math.Min(2, (int)(t * 3));
                }
            }
        }

        // Reference to the SOUTH (last band): its mean angle is the gauge zero. Falls back to the whole-system
        // mean when the south band is empty, so the number is always meaningful.
        int southBand = labels.Length - 1;
        var southDegs = bandOfBus.Where(kv => kv.Value == southBand).Select(kv => angleByBus[kv.Key]).ToList();
        double refDeg = southDegs.Count > 0 ? southDegs.Average()
                      : angleByBus.Count > 0 ? angleByBus.Values.Average() : 0.0;

        // ---- regional gauge: mean rotor lead per band, vs the south ----
        var regions = new List<TwistRegionDto>();
        for (int band = 0; band < labels.Length; band++)
        {
            var degs = bandOfBus.Where(kv => kv.Value == band).Select(kv => angleByBus[kv.Key] - refDeg).ToList();
            if (degs.Count == 0) continue;
            regions.Add(new TwistRegionDto(labels[band], degs.Average(), degs.Count, degs.Min(), degs.Max()));
        }

        // ---- individual synchronous machines, ranked by lead. For 2-pole sets, this is real rotor twist. ----
        var machines = new List<TwistMachineDto>();
        foreach (Generator g in model.Generators)
        {
            if (!Fuels.Classify(g.Type).Synchronous || g.Pg < GridSim.Core.Presentation.MechanicalThresholds.MinSyncMachineMw) continue;
            if (!angleByBus.TryGetValue(g.Bus, out double a)) continue;
            machines.Add(new TwistMachineDto(g.Name ?? $"{Fuels.Classify(g.Type).Name}@{g.Bus}", a - refDeg, g.Pg));
        }
        machines.Sort((x, y) => y.LeadDeg.CompareTo(x.LeadDeg));   // most-advanced first

        double lo = angleByBus.Values.Min() - refDeg, hi = angleByBus.Values.Max() - refDeg;
        double total = hi - lo;
        double worst = Math.Max(Math.Abs(hi), Math.Abs(lo));
        return (total, worst, machines, regions);
    }

    // GB latitude bands, north -> south, matched on the bus-name keywords the GB models actually use.
    private static readonly (string Label, string[] Keys)[] GbBands =
    {
        ("SCOTLAND",           new[] { "scotland" }),
        ("NORTHERN ENGLAND",   new[] { "north england", "yorkshire", "humber", "mersey", "north wales" }),
        ("THE MIDLANDS",       new[] { "midlands", "east anglia" }),
        ("LONDON & THE SOUTH", new[] { "london", "south" }),   // South East / South West / S Wales / South
    };

    /// <summary>Classify a bus name into a GB latitude band (0 = Scotland .. last = South), or -1 if not a GB name.</summary>
    private static int GbBand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        string n = name.ToLowerInvariant();
        for (int i = 0; i < GbBands.Length; i++)
            foreach (string key in GbBands[i].Keys)
                if (n.Contains(key)) return i;
        return -1;
    }
}
