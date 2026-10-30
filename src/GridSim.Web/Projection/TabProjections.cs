using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Visuals;

namespace GridSim.Web.Projection;

/// <summary>
/// The stateless per-tab projections (GEN&amp;INERTIA, OPERATOR, MARKET, NESO RP1) - pure functions of a
/// solved <see cref="SimFrame"/> (plus a shared <see cref="FuelBreakdown"/>). The NESO RP1 numbers are the
/// thesis quantities, computed directly from Core's <see cref="StabilityMetrics"/> - no lake feed required.
/// The stateful tabs (MECHANICAL, SCADA, THE MACHINE) live in per-session services.
/// </summary>
public static class TabProjections
{
    /// <summary>SPEED DOMAINS - decompose the network into speed domains + GVad dominance (mirrors the WPF tab).</summary>
    public static DomainsDto Domains(SimFrame f)
    {
        (IReadOnlyList<SpeedDomain> domains, DominanceReport dom) = DomainAssignment.Assign(f.Model);
        double maxGvad = domains.Count > 0 ? System.Math.Max(1.0, domains.Max(d => d.Gvad)) : 1.0;
        double maxInertia = domains.Count > 0 ? System.Math.Max(1.0, domains.Max(d => d.AggregateInertiaMwS)) : 1.0;

        var rows = domains.OrderByDescending(d => d.Gvad).Select(d => new DomainRowDto(
            d.Id, $"Domain {d.Id}",
            d.Poles > 0 ? $"{d.Poles}-pole * {d.Rpm:F0} rpm" : "inverter (virtual)",
            d.Kind.ToString(), d.MemberGenIndices.Count,
            d.AggregateInertiaGvaS, d.Gvad / 1000.0,
            d.Gvad / maxGvad, d.AggregateInertiaMwS / maxInertia,
            dom.DominantDomainId == d.Id, d.Kind == DomainKind.Synchronous)).ToList();

        double deltaPct = double.IsInfinity(dom.DeltaGVa) ? 100.0 : dom.DeltaGVa * 100.0;
        // R206: the ">= 10%" / "< 10%" wording is formatted from the threshold DomainAssignment actually used (the
        // default DistributedConfig, as Assign is called with no config), not hard-coded beside it.
        string thresholdPct = DominanceThresholdText();
        string dominantText = dom.HasDominant
            ? $"Domain {dom.DominantDomainId} carries waveform authority"
            : "none - inertia too evenly spread";
        string gapText = domains.Count < 2
            ? "Single speed domain - equivalent to the lumped model."
            : dom.HasDominant
                ? $"Top two differ by deltaGVa = {deltaPct:F0}% >= {thresholdPct} - a dominant domain can anchor recovery."
                : $"Top two differ by deltaGVa = {deltaPct:F0}% < {thresholdPct} - inertia too evenly spread (LOS fragmentation mode).";
        return new DomainsDto(domains.Count, deltaPct, dom.HasDominant, dominantText, gapText, rows,
            DomainsStatusText(domains.Count));
    }

    /// <summary>The SPEED DOMAINS one-line status sentence, word for word the WPF <c>DomainsVm.Status</c> (R099
    /// parity): more than one domain names the count and the ranking rule; one (or none) says the decomposition
    /// collapses to the lumped model.</summary>
    public static string DomainsStatusText(int domainCount) =>
        domainCount > 1
            ? $"{domainCount} speed domains, ranked by apparent-power stiffness GVad = sum(S*C)."
            : "Single speed domain - equivalent to the lumped model.";

    /// <summary>How many imbalance prices the MARKET sparkline keeps: 96 (about two days of half-hourly settlement
    /// periods). N144 (October 2026): the rule now lives in Core's <see cref="ImbalancePriceTrace"/>, shared with the WPF
    /// <c>MarketVm</c>; these members are thin wrappers, so the web's behaviour is unchanged.</summary>
    public const int ImbalancePriceHistoryLength = ImbalancePriceTrace.HistoryLength;

    /// <summary>Append one streamed imbalance price with no settlement-period rule (see
    /// <see cref="ImbalancePriceTrace.Append(Queue{double}, double?)"/>). Returns true when a sample was added.</summary>
    public static bool AppendImbalancePrice(Queue<double> history, double? priceGbpMwh)
        => ImbalancePriceTrace.Append(history, priceGbpMwh);

    /// <summary>The length of one GB settlement period: 30 minutes (BSC section X-1, "Settlement Period").</summary>
    public static readonly TimeSpan SettlementPeriodLength = ImbalancePriceTrace.SettlementPeriodLength;

    /// <summary>The settlement period an instant falls in (see <see cref="ImbalancePriceTrace.SettlementPeriodKey"/>).</summary>
    public static long SettlementPeriodKey(DateTime tickUtc) => ImbalancePriceTrace.SettlementPeriodKey(tickUtc);

    /// <summary>N037: ONE sample per settlement period (see
    /// <see cref="ImbalancePriceTrace.Append(Queue{double}, double?, DateTime, ref long?)"/> for the rules). Returns true
    /// when the history changed.</summary>
    public static bool AppendImbalancePrice(Queue<double> history, double? priceGbpMwh, DateTime tickUtc, ref long? lastPeriodKey)
        => ImbalancePriceTrace.Append(history, priceGbpMwh, tickUtc, ref lastPeriodKey);

    /// <summary>Attach the rolling imbalance-price history to a MARKET DTO's lake block. Unchanged when there is no
    /// lake block (no market feed) or no history yet. The history is copied, so a later append never mutates a
    /// DTO already handed to the UI.</summary>
    public static MarketDto WithImbalancePriceHistory(MarketDto market, Queue<double> history)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(history);
        if (market.Lake is null || history.Count == 0) return market;
        return market with { Lake = market.Lake with { ImbalancePriceHistory = history.ToArray() } };
    }

    /// <summary>The speed-domain dominance threshold as display text ("10%" for the default 0.10), read from the same
    /// <see cref="DistributedConfig"/> default that <see cref="DomainAssignment.Assign"/> uses when given no config.</summary>
    public static string DominanceThresholdText() =>
        $"{new DistributedConfig().DominanceThreshold * 100.0:F0}%";

    public static GenInertiaDto GenInertia(SimFrame f, FuelBreakdown fb)
    {
        var rows = fb.Fuels.Select(a => new FuelRowDto(
            a.Fuel, a.Color, a.Mw,
            fb.TotalGenMw > 0 ? a.Mw / fb.TotalGenMw * 100.0 : 0,
            a.InertiaGvaS, a.Synchronous)).ToList();

        // Interconnector links + net import (import +ve).
        var links = new List<ImportLinkDto>();
        double netImport = 0;
        foreach (Generator g in f.Model.Generators)
        {
            if (!FuelTaxonomy.IsInterconnectorLink(g.Type)) continue;   // the one link rule (bug #28)
            links.Add(new ImportLinkDto(g.Name ?? "Interconnector", g.Pg, g.Pg > 0));
            netImport += g.Pg;
        }

        // Stability note keyed off synchronous share - the inertia thesis in one line.
        double sync = fb.SyncFraction;
        string note = sync >= PresentationLimits.SyncShareHealthyFraction ? "Healthy synchronous base - strong intrinsic inertia."
            : sync >= PresentationLimits.SyncShareModerateFraction ? "Moderate converter penetration - inertia adequate but watch the largest loss."
            : "Converter-dominated - low intrinsic inertia; RoCoF exposure is high.";

        return new GenInertiaDto(
            fb.TotalGenMw, fb.SyncMw, fb.ConverterMw, fb.SyncFraction,
            fb.InertiaGvaS, fb.FossilMw, fb.RenewableMw, fb.RenewableFraction,
            fb.CarbonKgPerKwh, netImport, note, rows, links);
    }

    public static OperatorDto Operator(SimFrame f, FuelBreakdown fb, BridgeProtocol.OperatorBlock? lake = null)
    {
        // D-DOC-087: no boundary rows for a frame whose power flow did not converge (the analysis refuses it).
        var boundaries = (f.Result.Converged ? BoundaryAnalysis.Evaluate(f.Model, f.Result) : Array.Empty<BoundaryFlow>())
            .Select(b => new OpBoundaryRowDto(b.Name, b.FlowMw, b.LimitMw, b.LimitMw - Math.Abs(b.FlowMw), b.LoadingPercent, b.Overloaded))
            .ToList();

        var fuel = fb.Fuels.Select(a => new OpFuelRowDto(
            a.Fuel, a.Color, a.Mw, fb.TotalGenMw > 0 ? a.Mw / fb.TotalGenMw * 100.0 : 0)).ToList();

        // The largest credible loss: the largest single secured unit (FRCR 2025 "BMU-only", any technology since N039), via the ONE Core presenter
        // the WPF OperatorVm also uses (Q1, 2026-10). No correlated group here, so it is passed as none.
        FrequencyRiskPresenter.CredibleLossView loss = FrequencyRiskPresenter.CredibleLoss(
            fb.LargestUnitMw, fb.LargestUnitName, new StabilityMetrics.GroupLoss("", 0.0));
        double largestLoss = loss.SecuredLossMw;
        string largestName = loss.SecuredLossName;

        return new OperatorDto(
            f.FrequencyHz, f.RocofHzPerS, fb.InertiaGvaS,
            f.DemandMw, f.GenerationMw, f.Result.TotalLossMw,
            fb.SyncMw / 1000.0, fb.ConverterMw / 1000.0, fb.SyncFraction,
            largestLoss, largestName,
            boundaries, fuel,
            lake is null ? null : OperatorLake(lake));
    }

    private static OperatorLakeDto OperatorLake(BridgeProtocol.OperatorBlock b)
    {
        var actions = (b.BalancingActions ?? new())
            .Select(a => new OpBmActionDto(a.BmUnit ?? "-", a.Kind ?? "-", a.LevelMw ?? 0, a.PriceGbpMwh ?? 0, a.FuelType ?? "-"))
            .ToList();
        var cons = (b.Constraints ?? new())
            .Select(c => new OpConstraintDto(c.Name ?? "-", c.FlowMw ?? 0, c.LimitMw ?? 0, c.CostGbp ?? 0, c.Binding ?? false))
            .ToList();
        return new OperatorLakeDto(
            b.ResponseMw, b.ReserveMw, b.DeratedMarginMw, b.NetInterconnectorMw,
            b.OutturnInertiaGvaS, b.MarketInertiaGvaS, b.Warning, actions, cons);
    }

    // The canonical GB interconnector set (mirrors BridgeProtocol.RawInterconnectorsBlock).
    // Anything not on this list (e.g. GSP rows the feed may include) is not an interconnector.
    private static readonly HashSet<string> KnownInterconnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "IFA", "IFA2", "BritNed", "Moyle", "EastWest", "EWIC", "Nemo",
        "NSL", "ElecLink", "Viking", "Greenlink",
    };

    private static bool IsRealInterconnector(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // Normalise punctuation/spacing so "East-West" == "EastWest", "IFA-2" == "IFA2".
        string n = name.Replace("-", "").Replace(" ", "").Replace("_", "").Trim();
        return KnownInterconnectors.Contains(n) || n.StartsWith("IFA", StringComparison.OrdinalIgnoreCase);
    }

    public static MarketDto Market(SimFrame f, FuelBreakdown fb, BridgeProtocol.MarketBlock? lake = null)
    {
        // Interconnectors: prefer the lake list (named links with price), but keep ONLY real
        // interconnectors - the wire feed can carry GSP (grid-supply-point) rows too, and those
        // are demand connection points, not cross-border links, so they must not appear here.
        // If filtering empties the lake list, fall back to converter gens classified as such.
        List<MktInterconnectorRowDto> ics = new();
        if (lake?.Interconnectors is { Count: > 0 } lk)
        {
            ics = lk.Where(i => IsRealInterconnector(i.Name))
                    .Select(i => new MktInterconnectorRowDto(i.Name ?? "Interconnector", i.FlowMw ?? 0, (i.FlowMw ?? 0) > 0))
                    .ToList();
        }
        if (ics.Count == 0)
        {
            foreach (Generator g in f.Model.Generators)
            {
                FuelInfo fi = Fuels.Classify(g.Type);
                if (!FuelTaxonomy.IsInterconnectorLink(g.Type)) continue;   // the one link rule (bug #28)
                ics.Add(new MktInterconnectorRowDto(g.Name ?? "Interconnector", g.Pg, g.Pg > 0));
            }
        }

        MarketLakeDto? lakeDto = lake is null ? null : new MarketLakeDto(
            lake.ImbalancePriceGbpMwh, lake.SystemSellPriceGbpMwh, lake.MarketIndexGbpMwh,
            lake.NetImbalanceVolumeMwh, lake.DayAheadGbpMwh, lake.BsuosGbpMwh,
            lake.ConstraintCostGbp, lake.CarbonIntensityGco2PerKwh, lake.DemandForecastMw,
            lake.BalancingCostGbp, lake.BalancingVolumeMwh, lake.BalancingActionsN,
            lake.BmCostGbp, lake.BmVolumeMwh, lake.BmUnitsN);

        var actions = lake?.Actions?
            .Select(a => new BalancingActionRowDto(a.T ?? "", a.Unit ?? "", a.From ?? 0, a.To ?? 0, a.So))
            .ToList();

        return new MarketDto(
            fb.RenewableFraction * 100.0, fb.CarbonKgPerKwh, f.DemandMw,
            fb.FossilMw, fb.RenewableMw, ics, lakeDto, actions);
    }

    /// <summary>
    /// RAW-replay MACHINE projection: the grid-as-one-machine view with NO solve. Rotation (rpm), spinning energy
    /// (from the MEASURED system inertia) and torque (T = P / omega; delivered adds the governor-droop surplus) are
    /// derived from the real frequency + demand + published mix. Solve-only fields (per-bus voltage, reactive, taps,
    /// shaft layers) and the stateful banked-divergence / regen / BESS economics are left at neutral defaults and
    /// hidden by MachineTab's Raw guard.
    /// </summary>
    public static MachineDto MachineRaw(SimFrame f, FuelBreakdown fb, IReadOnlyList<double> freqTrace, double freqTrendHzPerS)
    {
        double hz = f.FrequencyHz;
        double dem = f.DemandMw;
        double gen = fb.TotalGenMw;
        // N273 (October 2026, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04): the frame model's nominal (50.0 for the
        // GB raw replay, so every number is unchanged there).
        double nominalHz = f.Model.FrequencyHz;
        double rpm = hz / nominalHz * GridSim.Core.Presentation.PresentationConstants.TwoPoleSyncRpmAt(nominalHz);
        double spinMwh = fb.InertiaGvaS * 1000.0 / 3600.0;                          // GVA.s -> MW.s -> MWh

        double droop = GridSim.Core.Presentation.EnergyEconomicsConstants.GovernorDroopFraction;
        double band = GridSim.Core.Presentation.EnergyEconomicsConstants.StressBand;
        double cmdMw = dem;                                                          // loss = 0 (no solve)
        double surplusMw = (hz - nominalHz) / nominalHz / droop * dem;
        double omega = 2 * Math.PI * hz;
        double cmdMnm = cmdMw * 1e6 / (2 * Math.PI * nominalHz) / 1e6;
        double actMnm = omega > PresentationLimits.MinOmegaRadPerS ? (cmdMw + surplusMw) * 1e6 / omega / 1e6 : 0;
        double divMnm = omega > PresentationLimits.MinOmegaRadPerS ? surplusMw * 1e6 / omega / 1e6 : 0;
        double stress = cmdMnm > 1e-6 ? Math.Clamp(Math.Abs(divMnm) / (band * cmdMnm) * 100.0, 0, 100) : 0;
        string divNote = surplusMw > PresentationLimits.TorqueNarrativeBandMw ? "too much push - the machine speeds up"
            : surplusMw < -PresentationLimits.TorqueNarrativeBandMw ? "not enough push - the machine slows down"
            : "supply and demand in balance";

        double co2 = fb.CarbonKgPerKwh * gen;                                        // t/h  (kg/kWh x MW)
        var fuelIn = fb.Fuels.Where(x => x.Mw > 0)
            .Select(x => new MachineStreamDto(x.Fuel, x.Color, x.Mw / 1000.0, gen > 0 ? x.Mw / gen * 100.0 : 0))
            .ToList();

        return new MachineDto(
            rpm, hz, freqTrendHzPerS, fb.InertiaGvaS, spinMwh,
            fb.SyncMw / 1000.0, fb.ConverterMw / 1000.0, fb.SyncFraction * 100.0, co2, freqTrace,
            cmdMnm, actMnm, divMnm, surplusMw, stress, divNote,
            0, 0, false, 0, 0, 0, "STANDBY", 0, 0,                                   // banked/BESS (hidden in raw)
            1.0, 0, 1.0, 1.0, System.Array.Empty<double>(), 0, 0, 0,                 // electrical (solve-only; hidden)
            fuelIn, System.Array.Empty<MachineLayerDto>(), 0, 0, 0,                  // streams / layers / taps
            gen, dem, 0);                                                            // energy (loss 0)
    }

    // The correlated-trip corridor map (infeeds that can be lost together, Policy 0021 s.4) and the matching of
    // link names onto corridor members live in Core (InterconnectorCorridors), shared with the WPF NesoRp1Vm.
    // Sellindge lands IFA + ElecLink; the North Sea corridor is North Sea Link (NSL) + Viking Link. Members are
    // matched by canonical id, so the model's long names ("IFA Interconnector", "NS Link interconnector",
    // "Viking Link Denmark Interconnector") are recognised as well as the bridge raw block's short ids (R053).
    private static IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Rp1Corridors => InterconnectorCorridors.All;

    /// <summary>
    /// NESO RP1 - purchased time, intrinsic vs total inertia, the largest credible loss (today's SQSS secured
    /// event), the live correlated HVDC group as context, dynamic MIF, RoCoF and the LFDD grade.
    /// Pure <see cref="StabilityMetrics"/> physics; the credible loss and the grade come from the ONE Core presenter
    /// <see cref="FrequencyRiskPresenter"/> the WPF <c>NesoRp1Vm</c> also calls (Q1, 2026-10).
    /// <paramref name="trace"/> is the session's rolling <see cref="FrequencyRiskPresenter.PurchasedTimeTrace"/>: when
    /// given, this frame's purchased time is added to it and its falling-trend flag escalates the grade, exactly as the
    /// WPF tab does. Null (a one-off call, a test) means no history, so no trend escalation.
    /// </summary>
    public static Rp1Dto Rp1(SimFrame f, FuelBreakdown fb, IReadOnlyList<(string Name, double Mw)>? rawIcs = null,
        FrequencyRiskPresenter.PurchasedTimeTrace? trace = null)
    {
        double intrinsic = fb.IntrinsicInertiaGvaS;
        double total = fb.InertiaGvaS;
        double market = Math.Max(0, total - intrinsic);   // the non-synchronous "water" proxy (0 without a lake feed)

        // Interconnector fleet: name -> live flow (import +ve). Only importing links are a credible infeed loss.
        // In the raw replay we take the REAL published flows (rawIcs, the same source as GEN & INERTIA / MARKET)
        // so RP1's import figures match the other tabs instead of the static topology dispatch.
        var activeFlows = new List<(string Name, double Mw)>();
        var ics = new List<Rp1IcRowDto>();
        IEnumerable<(string Name, double Mw)> icSource = rawIcs is { Count: > 0 }
            ? rawIcs
            : f.Model.Generators.Where(g => FuelTaxonomy.IsInterconnectorLink(g.Type))
                  .Select(g => (Name: DedupeWords(g.Name ?? Fuels.Classify(g.Type).Name), Mw: g.Pg));
        foreach ((string name, double mw) in icSource)
        {
            if (StatusLampRules.IsIdleInterconnector(mw)) continue;   // skip idle (~0 MW) links - no infeed loss to show (shared Q10 rule)
            activeFlows.Add((name, mw));
            // Corridor membership by canonical id (Core), not exact display-name equality.
            string? corridor = InterconnectorCorridors.CorridorOf(name);
            ics.Add(new Rp1IcRowDto(name, mw, mw > 0, corridor is not null, corridor ?? ""));
        }
        ics = ics.OrderByDescending(r => Math.Abs(r.FlowMw)).ToList();

        // Largest correlated HVDC group: sum each corridor's importing members and take the worst
        // (StabilityMetrics.LargestCorrelatedGroup) - surfaced as live multi-infeed context. The flows are keyed by
        // canonical member id, so a model name like "NS Link interconnector" counts as NSL.
        StabilityMetrics.GroupLoss grp = StabilityMetrics.LargestCorrelatedGroup(
            Rp1Corridors, InterconnectorCorridors.MemberFlows(activeFlows));
        var groupMembers = grp.Mw > 0
            ? ics.Where(r => r.CorridorName == grp.Name).ToList()
            : new List<Rp1IcRowDto>();
        var corrGroup = new Rp1CorrGroupDto(grp.Name, grp.Mw, groupMembers);

        // Largest credible loss under today's rules: the single largest secured unit (FRCR 2025, NESO, March 2025:
        // Executive summary p.4, s.6.2 and s.7.1 - secure all BMU-only events, do NOT secure simultaneous events;
        // 1,800 MW in the raw aggregate view). Any technology counts (N039, B4 2026-10: SQSS v2.10 para 5.1.2, FRCR
        // Methodology v3 Table 2), so a single interconnector import or wind farm in the model can be it. The correlated HVDC group is a simultaneous event, so it is surfaced
        // as live unsecured context but never redefines the credible loss. One Core presenter, shared with WPF.
        FrequencyRiskPresenter.CredibleLossView lossView =
            FrequencyRiskPresenter.CredibleLoss(fb.LargestUnitMw, fb.LargestUnitName, grp);
        double credibleLoss = lossView.SecuredLossMw;

        // Live frequency, not the 50.0 default: purchased time is DEFINED as (f_now - 49.2)/RoCoF.
        double purchased = StabilityMetrics.PurchasedTimeSeconds(intrinsic, credibleLoss, f.FrequencyHz);
        double rocof = StabilityMetrics.RocofFor(intrinsic, credibleLoss);
        double mif = StabilityMetrics.DynamicMifGvaS(credibleLoss, FrequencyLimits.RocofLimitLegacyHzPerS);

        // Per-unit constituents for the fuel drill-down (H / stored kinetic energy).
        var units = new List<Rp1UnitDto>();
        foreach (Generator g in f.Model.Generators)
        {
            if (g.Pg <= 0) continue;
            FuelInfo fiU = Fuels.Classify(g.Type);
            double mw = Math.Max(0, g.Pg);
            double cap = double.IsInfinity(g.Pmax) ? mw : Math.Max(g.Pmax, mw);
            double ke = fiU.Synchronous ? g.H * g.MBase / 1000.0 : 0;   // GVA.s (0 for converters)
            units.Add(new Rp1UnitDto(g.Name ?? fiU.Name, fiU.Name, fiU.Color, mw, cap, g.H, ke, fiU.Synchronous));
        }

        // LFDD grade off purchased time to the 49.2 Hz working set-point: the ONE Core ladder (NORMAL / WATCH /
        // ADVISORY / WARNING, escalated by a falling trend), shared with the WPF NesoRp1Vm (Q1, 2026-10). Its colour
        // level keeps the web's old GREEN / AMBER / RED cuts exactly (5 s and 2 s).
        trace?.Add(purchased);
        bool tightening = trace?.IsTightening ?? false;
        FrequencyRiskPresenter.Grade grade = FrequencyRiskPresenter.GradePurchasedTime(purchased, tightening);

        var bySource = fb.Fuels.Where(a => a.InertiaGvaS > 0)
            .OrderByDescending(a => a.InertiaGvaS)
            .Select(a => new Rp1InertiaRowDto(a.Fuel, a.Color, a.InertiaGvaS,
                intrinsic > 0 ? a.InertiaGvaS / intrinsic * 100.0 : 0))
            .ToList();

        var stack = fb.Fuels.OrderByDescending(a => a.Mw)
            .Select(a => new Rp1GenRowDto(a.Fuel, a.Color, a.Mw,
                fb.TotalGenMw > 0 ? a.Mw / fb.TotalGenMw * 100.0 : 0,
                a.InertiaGvaS, a.Synchronous ? "Synchronous" : "Converter"))
            .ToList();

        // N-1 RoCoF screen: each online generator (and interconnector import) as a single credible loss,
        // ranked by the initial RoCoF it would cause against the current intrinsic energy (the CLI `screen`).
        var screen = new List<Rp1ScreenRowDto>();
        foreach (Generator g in f.Model.Generators)
        {
            if (g.Pg <= 0) continue;
            FuelInfo fi = Fuels.Classify(g.Type);
            double ro = StabilityMetrics.RocofFor(intrinsic, g.Pg);
            screen.Add(new Rp1ScreenRowDto(
                g.Name ?? fi.Name, fi.Name, fi.Color, g.Pg, ro,
                ro > FrequencyLimits.RocofLimitLegacyHzPerS, ro > FrequencyLimits.RocofLimitModernHzPerS));
        }
        var screening = screen.OrderByDescending(s => s.RocofHzPerS).Take(10).ToList();

        string narrative =
            $"{intrinsic:0} GVA.s intrinsic holds the {credibleLoss:0} MW largest credible loss (today's SQSS secured event) at " +
            $"{rocof:0.00} Hz/s - {(double.IsPositiveInfinity(purchased) ? "unlimited" : purchased.ToString("0.0") + " s")} of purchased time to 49.2 Hz.";

        return new Rp1Dto(
            purchased, total, intrinsic,
            fb.LargestUnitMw, fb.LargestUnitName,
            credibleLoss,
            mif, rocof,
            fb.SyncMw / 1000.0, fb.ConverterMw / 1000.0, fb.SyncFraction,
            grade.Text, narrative, bySource, stack, screening,
            market,
            corrGroup, ics, units,
            LfddLevel: grade.Level.ToString().ToLowerInvariant());
    }

    /// <summary>Collapse consecutive duplicate words (case-insensitive): some gb-full interconnector generators are
    /// named "IFA Interconnector Interconnector" in the topology export; this renders them "IFA Interconnector".</summary>
    private static string DedupeWords(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>(parts.Length);
        foreach (string p in parts)
            if (outp.Count == 0 || !string.Equals(outp[^1], p, StringComparison.OrdinalIgnoreCase))
                outp.Add(p);
        return string.Join(' ', outp);
    }
}
