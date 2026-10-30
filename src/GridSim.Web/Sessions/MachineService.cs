using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using GridSim.Web.Visuals;

namespace GridSim.Web.Sessions;

/// <summary>
/// Per-session THE MACHINE projection - the whole GB grid as one rotating machine. A faithful port of the WPF
/// <c>MachineVm</c>: rotation (rpm / spinning energy / sync split), the torque story (commanded vs actual, the
/// gap that moves frequency), the banked divergence (accumulated Hz*s -> grid time error, harvestable regen, a
/// BESS leaning against it), and the electrical story (voltage deviation/spread, reactive balance, the shaft's
/// voltage layers and the OLTC fleet). Stateful: integrates over sim time and resets on a timeline break.
/// </summary>
public sealed class MachineService
{
    private const int TraceLen = 120;
    // BESS cap / response, governor droop, stress band and the regen price now live in
    // GridSim.Core.Presentation.EnergyEconomicsConstants - the single source shared with the WPF MachineVm.

    private double _accumHzS;
    private double _regenMwh;
    private double _bessSoc = PresentationLimits.BessInitialSocPct;
    private double _bessMovedMwh;
    private double _lastHour = -1, _lastHz = GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
    private int _lastTick = int.MinValue;
    private readonly List<double> _freqHist = new();
    private readonly List<double> _voltHist = new();

    /// <summary>
    /// Additive instrument-dial fields (regulation strain) not carried by the shared <see cref="MachineDto"/>.
    /// Recomputed every <see cref="Project"/> and read off the frame envelope by THE MACHINE tab. Stateless
    /// (derived from the current solve), so it needs no reset.
    /// </summary>
    public MachineViewDto LastView { get; private set; } = new(0);

    public void Reset()
    {
        _accumHzS = 0; _regenMwh = 0; _bessSoc = PresentationLimits.BessInitialSocPct; _bessMovedMwh = 0;
        _lastHour = -1; _lastTick = int.MinValue; _lastHz = NominalHz;
        _freqHist.Clear(); _voltHist.Clear();
    }

    private static (string name, string color) Bucket(FuelInfo fi) => fi.Name switch
    {
        "CCGT" or "OCGT" or "Gas" or "CHP" or "Coal" => ("Gas & thermal", "#FF8A3D"),
        "Nuclear" => ("Nuclear", "#7E57C2"),
        "Wind (offshore)" or "Wind (onshore)" => ("Wind", "#00B8D4"),
        "Solar" => ("Solar", "#FFD54F"),
        "Hydro" or "Pumped storage" or "Tidal" => ("Hydro", "#29B6F6"),
        "Biomass" => ("Biomass", "#8BC34A"),
        "Interconnector" or "Grid infeed" => ("Imports", "#90A4AE"),
        "Storage" => ("Storage", "#EC407A"),
        _ => ("Other", "#9E9E9E"),
    };

    // Nominal system frequency (Hz) the torque story is told against (R207: was the literal 50.0 throughout).
    private const double NominalHz = GridSim.Core.Dynamics.FrequencyLimits.NominalHz;

    public MachineDto Project(SimFrame f, FuelBreakdown fb)
    {
        GridModel m = f.Model;
        PowerFlowResult r = f.Result;
        double hz = f.FrequencyHz;
        double loss = r.TotalLossMw;
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the torque story is told against the
        // frame's own nominal (the model's FrequencyHz; 50.0, the constant, for every GB case, so every number below is
        // bit-identical there). A fresh or reset history starts its trend at that nominal.
        double nominalHz = m.FrequencyHz;
        if (_lastHour < 0) _lastHz = nominalHz;

        bool discontinuity = _lastHour < 0 || f.Tick < _lastTick;
        double dtHours = discontinuity ? 0 : (f.Hour - _lastHour + 24) % 24;
        _lastHour = f.Hour; _lastTick = f.Tick;
        double freqTrend = hz - _lastHz; _lastHz = hz;

        // Rotation.
        double rpm = hz / nominalHz * GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);
        // N325 (DOC-17): the N289 rotating-mass total, the same sum the inertia read-out (SystemInertiaGvaS) and trip use;
        // it used to be H x MBase over EVERY unit, so a converter-typed unit with H > 0 counted here and nowhere else. For
        // every model without such a unit (every bundled case) the terms and their order are the same, bit for bit.
        double inertiaMwS = m.SystemInertiaMwS;
        double spinMwh = inertiaMwS / 3600.0;

        // Fuel-in streams (coarse layman buckets) + CO2.
        var bucketMw = new Dictionary<string, (string color, double mw)>();
        double co2 = 0;
        foreach (Generator g in m.Generators)
        {
            if (g.Pg <= 0) continue;
            FuelInfo fi = Fuels.Classify(g.Type);
            (string name, string color) = Bucket(fi);
            bucketMw[name] = (color, bucketMw.GetValueOrDefault(name).mw + g.Pg);
            co2 += g.Pg * CarbonFactors.CombustionTonnesPerMwh(fi.Name);
        }
        double totalGen = Math.Max(1, fb.TotalGenMw);
        var fuelIn = bucketMw.OrderByDescending(kv => kv.Value.mw)
            .Select(kv => new MachineStreamDto(kv.Key, kv.Value.color, kv.Value.mw / 1000.0, kv.Value.mw / totalGen * 100.0))
            .ToList();

        _freqHist.Add(hz); if (_freqHist.Count > TraceLen) _freqHist.RemoveAt(0);

        // Torque: P = Tomega. Commanded holds exactly 50 Hz (demand + losses); actual adds the droop-implied surplus.
        double cmdMw = f.DemandMw + loss;
        double surplusMw = (hz - nominalHz) / nominalHz / EnergyEconomicsConstants.GovernorDroopFraction * f.DemandMw;
        double omega = 2 * Math.PI * nominalHz * (hz / nominalHz);
        double cmdMnm = cmdMw * 1e6 / (2 * Math.PI * nominalHz) / 1e6;
        double actMnm = omega > PresentationLimits.MinOmegaRadPerS ? (cmdMw + surplusMw) * 1e6 / omega / 1e6 : 0;
        double divMnm = omega > PresentationLimits.MinOmegaRadPerS ? surplusMw * 1e6 / omega / 1e6 : 0;
        double stress = cmdMnm > 1e-6 ? Math.Clamp(Math.Abs(divMnm) / (EnergyEconomicsConstants.StressBand * cmdMnm) * 100.0, 0, 100) : 0;
        string divNote = surplusMw > PresentationLimits.TorqueNarrativeBandMw ? "too much push - the machine speeds up"
            : surplusMw < -PresentationLimits.TorqueNarrativeBandMw ? "not enough push - the machine slows down"
            : "supply and demand in balance";

        // Banked divergence + regen + BESS.
        _accumHzS += (hz - nominalHz) * dtHours * 3600.0;
        _regenMwh += Math.Max(0, surplusMw) * dtHours;
        double timeErr = Math.Abs(_accumHzS) / nominalHz;
        bool fast = _accumHzS >= 0;

        double bessMw = Math.Clamp(surplusMw * EnergyEconomicsConstants.BessResponseFraction,
            -EnergyEconomicsConstants.BessCapMw, EnergyEconomicsConstants.BessCapMw);
        if (_bessSoc >= EnergyEconomicsConstants.BessSocChargeCeilingPct && bessMw > 0) bessMw = 0;
        if (_bessSoc <= EnergyEconomicsConstants.BessSocDischargeFloorPct && bessMw < 0) bessMw = 0;
        _bessSoc = Math.Clamp(_bessSoc + bessMw * dtHours / EnergyEconomicsConstants.BessCapMwh * 100.0, 0, 100);
        _bessMovedMwh += Math.Abs(bessMw) * dtHours;
        string bessMode = bessMw > EnergyEconomicsConstants.BessModeDeadbandMw ? "CHARGING"
            : bessMw < -EnergyEconomicsConstants.BessModeDeadbandMw ? "DISCHARGING" : "STANDBY";

        // Voltage: demand-weighted average pu, spread, trace.
        var busById = m.Buses.ToDictionary(b => b.Id);
        double wSum = 0, wV = 0, vmin = double.PositiveInfinity, vmax = double.NegativeInfinity;
        for (int i = 0; i < r.Vm.Length; i++)
        {
            double v = r.Vm[i];
            if (!busById.TryGetValue(r.BusIds[i], out Bus? b)) continue;
            double w = Math.Max(b.Pd, 0) + PresentationLimits.VoltageWeightEpsilonMw;
            wSum += w; wV += w * v;
            if (v < vmin) vmin = v;
            if (v > vmax) vmax = v;
        }
        double vavg = wSum > 0 ? wV / wSum : 1.0;
        if (double.IsInfinity(vmin)) { vmin = 1; vmax = 1; }
        _voltHist.Add(vavg); if (_voltHist.Count > TraceLen) _voltHist.RemoveAt(0);

        // Reactive balance.
        double qGen = m.Generators.Where(g => g.Qg > 0).Sum(g => g.Qg);
        double qGenAbsorb = m.Generators.Where(g => g.Qg < 0).Sum(g => -g.Qg);
        double qLoad = m.Buses.Sum(b => Math.Max(0, b.Qd));
        double qLineLoss = r.LineFlows.Sum(l => l.QLossMvar);
        double qProduced = qGen + Math.Max(0, -qLineLoss);
        double qAbsorbed = qLoad + qGenAbsorb + Math.Max(0, qLineLoss);

        // Shaft voltage layers: group buses by kV tier; per tier avg live kV + deviation + connected generation.
        var vmByBus = new Dictionary<int, double>();
        for (int i = 0; i < r.BusIds.Count; i++) vmByBus[r.BusIds[i]] = r.Vm[i];
        var genByBus = m.Generators.GroupBy(g => g.Bus).ToDictionary(g => g.Key, g => g.Where(x => x.Pg > 0).Sum(x => x.Pg));
        var layerAgg = new Dictionary<int, (double vmSum, int n, double genMw)>();
        foreach (Bus b in m.Buses)
        {
            int tier = TierKv(b.BaseKv);
            if (tier <= 0) continue;
            double vm = vmByBus.GetValueOrDefault(b.Id, b.Vm);
            var cur = layerAgg.GetValueOrDefault(tier);
            layerAgg[tier] = (cur.vmSum + vm, cur.n + 1, cur.genMw + genByBus.GetValueOrDefault(b.Id));
        }
        var layers = layerAgg.OrderByDescending(kv => kv.Key)
            .Select(kv =>
            {
                double avgVm = kv.Value.n > 0 ? kv.Value.vmSum / kv.Value.n : 1.0;
                return new MachineLayerDto($"{kv.Key} kV", kv.Value.genMw / 1000.0,
                    kv.Value.genMw / totalGen * 100.0, avgVm * kv.Key, (avgVm - 1.0) * 100.0, kv.Value.n);
            }).ToList();

        // OLTC fleet.
        var branchByName = new Dictionary<string, Branch>();
        foreach (Branch br in m.Branches) if (br.Name is { Length: > 0 }) branchByName[br.Name] = br;
        double offSum = 0, errSum = 0; int atLimit = 0, tapCount = 0;
        double tapDeadband = m.Oltcs.Count > 0 ? m.Oltcs[0].DeadbandPu : PresentationLimits.DefaultOltcDeadbandPu;
        foreach (Oltc o in m.Oltcs)
        {
            if (!branchByName.TryGetValue(o.Transformer, out Branch? br)) continue;
            tapCount++;
            offSum += (br.TapRatio - 1.0) * 100.0;
            if (br.TapRatio <= o.MinTap + o.StepPu * PresentationLimits.TapAtLimitStepFraction
                || br.TapRatio >= o.MaxTap - o.StepPu * PresentationLimits.TapAtLimitStepFraction) atLimit++;
            // Regulation error: how far the controlled bus sits from its target (mirrors WPF UpdateTaps).
            double vCtrl = vmByBus.GetValueOrDefault(o.ControlsBus, o.TargetVpu);
            errSum += Math.Abs(vCtrl - o.TargetVpu);
        }
        double tapAvgOff = tapCount > 0 ? offSum / tapCount : 0;

        // Regulation stress (the ELECTRICAL bank's fourth dial): the worse of the fraction of taps at travel
        // limit and the average error versus deadband (half-scaled), clamped 0..100. Faithful to WPF MachineVm.
        double avgErr = tapCount > 0 ? errSum / tapCount : 0;
        double limitFrac = tapCount > 0 ? (double)atLimit / tapCount : 0;
        double errStress = tapDeadband > 0 ? avgErr / tapDeadband : 0;
        double regStress = Math.Clamp(Math.Max(limitFrac, Math.Min(1.0, errStress / 2.0)) * 100.0, 0, 100);
        LastView = new MachineViewDto(regStress);

        return new MachineDto(
            rpm, hz, freqTrend, m.SystemInertiaGvaS, spinMwh,
            fb.SyncMw / 1000.0, fb.ConverterMw / 1000.0, fb.SyncFraction * 100.0, co2, _freqHist.ToArray(),
            cmdMnm, actMnm, divMnm, surplusMw, stress, divNote,
            _accumHzS, timeErr, fast, _regenMwh, _regenMwh * EnergyEconomicsConstants.RegenPricePerMwh,
            bessMw, bessMode, _bessSoc, _bessMovedMwh,
            vavg, (vavg - 1.0) * 100.0, vmin, vmax, _voltHist.ToArray(),
            qProduced, qAbsorbed, qProduced - qAbsorbed,
            fuelIn, layers,
            tapAvgOff, tapCount, atLimit,
            f.GenerationMw, f.DemandMw, loss,
            TapsSettled: f.TapsSettled, TapsAtLimitOutOfBand: f.TapsAtLimit?.Count ?? 0);   // N054
    }

    /// <summary>Snap a bus's nominal kV onto a display tier (400/275/132/66/33/11...), else 0 to skip.</summary>
    private static int TierKv(double kv)
    {
        if (kv <= 0) return 0;
        int best = 0; double bestErr = double.MaxValue;
        foreach (int t in PresentationLimits.KvTiers)
        {
            double e = Math.Abs(kv - t) / t;
            if (e < bestErr) { bestErr = e; best = t; }
        }
        return bestErr < PresentationLimits.KvTierSnapTolerance ? best : (int)Math.Round(kv);
    }
}
