using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Frequency;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Dynamics.Placement;

/// <summary>A named place (WGS84 latitude and longitude, degrees).</summary>
public sealed record GeoSite(string Name, double LatDeg, double LonDeg);

/// <summary>A site mapped onto the network: its nearest transmission bus (275 kV and above) and how far away that is.</summary>
public sealed record MappedSite(GeoSite Site, int BusId, string BusName, double BaseKv, double DistanceMiles);

/// <summary>A credible loss: <paramref name="Mw"/> of infeed lost at the transmission bus nearest <paramref name="Site"/>.</summary>
public sealed record PlacementLoss(GeoSite Site, double Mw);

/// <summary>Where the added synchronous inertia goes: a share per bus (the shares sum to 1).</summary>
public sealed record InertiaPlacement(string Name, string Description, IReadOnlyList<(int BusId, double Share)> Shares);

/// <summary>Settings of an inertia placement study (see <see cref="InertiaPlacementStudy"/>).</summary>
public sealed record InertiaPlacementOptions
{
    /// <summary>The total synchronous inertia budgets scanned (GVA.s), ascending.</summary>
    public IReadOnlyList<double> BudgetsGvaS { get; init; } = new[] { 120.0, 180.0, 240.0, 360.0, 480.0, 700.0, 900.0, 1200.0 };

    /// <summary>The share of today's synchronous fleet inertia kept on the fleet itself (its machines, dispatch and
    /// locations unchanged, each H scaled by this factor). The rest of each budget is synchronous condensers placed by the
    /// placement under test, so every placement shares the same residual and the same dispatch.</summary>
    public double ResidualFleetFraction { get; init; } = 0.25;

    /// <summary>Inertia constant of every added synchronous condenser (s, on its own rating): a high-inertia condenser
    /// with a flywheel. Every placement uses the same value, so every placement adds the same total condenser MVA.</summary>
    public double CondenserH { get; init; } = 8.0;

    /// <summary>Time of the loss (s): long enough after t = 0 for a full RoCoF window of steady trace before it.</summary>
    public double EventSeconds { get; init; } = 1.0;

    /// <summary>Simulated time after the loss (s).</summary>
    public double PostEventSeconds { get; init; } = 6.0;

    /// <summary>Integration step (s); the RoCoF window and one nominal cycle must be whole multiples of it.</summary>
    public double StepSeconds { get; init; } = 0.005;

    /// <summary>Criterion A: the full-loss worst bus RoCoF (500 ms window) must stay at or below the limit (Hz/s); one
    /// minimum is found per limit. GB loss-of-mains protection now trips at 1 Hz/s (0.5 Hz/s before the Accelerated
    /// Loss of Mains Change Programme); the stricter limits show how placement matters as the limit tightens.</summary>
    public IReadOnlyList<double> RocofLimitsHzPerS { get; init; } = new[] { 0.125, 0.25, 0.5, 1.0 };

    /// <summary>Criterion B: the centre-of-inertia frequency must take at least these times (s) after the loss to fall to
    /// <see cref="FrequencyFloorHz"/>, with no frequency response (the human-in-the-loop window).</summary>
    public IReadOnlyList<double> TimeToFloorTargetsSeconds { get; init; } = new[] { 3.0, 5.0 };

    /// <summary>The frequency the time-to-floor measure runs to (Hz): the GB operational limit, 49.8 Hz.</summary>
    public double FrequencyFloorHz { get; init; } = 49.8;

    /// <summary>Buses at or above this voltage (kV) are measured.</summary>
    public double MeasuredMinKv { get; init; } = 132.0;

    /// <summary>The latitude at and above which a machine counts as north (Scotland) for the inter-area measure.</summary>
    public double NorthLatDeg { get; init; } = 55.3;

    /// <summary>The latitude at and below which a machine counts as south for the inter-area measure.</summary>
    public double SouthLatDeg { get; init; } = 52.3;
}

/// <summary>One RMS run: one placement, one budget, one loss.</summary>
/// <param name="CoiRocofHzPerS">The centre-of-inertia RoCoF of the RMS run (500 ms window).</param>
/// <param name="WorstBusRocofHzPerS">The worst bus RoCoF of the RMS run (500 ms window, angle jump taken out).</param>
/// <param name="SpatialPenaltyK">Their ratio: how much worse the worst bus is than the system as a whole.</param>
/// <param name="EffectiveLossMw">The loss the machines felt in the RMS run, <c>2 E CoiRocof / f0</c>: less than
/// <paramref name="LossMw"/> because the loads are constant admittances in the RMS engine and draw less as the voltage
/// sags (and the converter plant holds its current, not its power).</param>
/// <param name="ScaledWorstBusRocofHzPerS">The worst bus RoCoF for the full loss: K times the swing-equation
/// centre-of-inertia RoCoF <c>f0 LossMw / (2 E)</c>. Criterion A is judged on this.</param>
/// <param name="TimeToFloorNoResponseSeconds">The time the RMS run's centre-of-inertia frequency took to reach the
/// floor after the loss (NaN when it did not within the run): with the effective loss, so longer than the full loss gives.</param>
/// <param name="InterAreaDampingRatio">The damping ratio of the north-south swing (see
/// <see cref="InertiaPlacementStudy.Oscillation"/>); NaN when fewer than <see cref="InertiaPlacementStudy.MinHalfCycles"/>
/// half-cycles were seen.</param>
/// <param name="InterAreaHalfCycles">How many half-cycles of the north-south swing the damping was fitted over.</param>
/// <param name="SwingTimeToFloorSeconds">The swing-equation time to the floor for the full loss with no response,
/// <c>(f0 - floor) 2 E / (f0 LossMw)</c>; placement-independent. Criterion B is judged on this.</param>
public sealed record PlacementRun(
    string Placement, double BudgetGvaS, string Loss, int LossBusId, double LossMw,
    double CoiRocofHzPerS, double WorstBusRocofHzPerS, int WorstRocofBusId, double WorstRocofBusLatDeg,
    double SpatialPenaltyK, double WorstAngleJumpDeg, int WorstJumpBusId,
    double InterAreaDampingRatio, double InterAreaFrequencyHz,
    double TimeToFloorNoResponseSeconds, double LowestCoiHz, bool StayedInSynchronism, int ConverterIterationFailures,
    double MachineInertiaGvaS, double EffectiveLossMw, double ScaledWorstBusRocofHzPerS, double SwingTimeToFloorSeconds,
    int InterAreaHalfCycles);

/// <summary>The centre-of-inertia frequency with the case's response, from the lumped model (placement-independent).</summary>
public sealed record LumpedResponseRun(double BudgetGvaS, double LossMw, double TimeToFloorSeconds, double NadirHz);

/// <summary>The smallest scanned budget meeting a criterion, for one placement (NaN when none of the scanned budgets meets
/// it), with the linear estimate between the last budget that fails and the first that passes.</summary>
public sealed record PlacementMinimum(string Placement, string Criterion, double FirstPassingBudgetGvaS,
    double LastFailingBudgetGvaS, double InterpolatedBudgetGvaS);

/// <summary>Everything the study produced.</summary>
public sealed record InertiaPlacementReport(
    InertiaPlacementOptions Options, double FleetInertiaGvaS, double ResidualInertiaGvaS, double DemandMw,
    int MeasuredBuses, IReadOnlyList<MappedSite> Sites, IReadOnlyList<(PlacementLoss Loss, MappedSite At)> Losses,
    IReadOnlyList<InertiaPlacement> Placements, IReadOnlyList<PlacementRun> Runs,
    IReadOnlyList<LumpedResponseRun> Lumped, IReadOnlyList<PlacementMinimum> Minimums,
    IReadOnlyList<string> Notes);

/// <summary>
/// The inertia placement study (October 2026): with the synchronous inertia of GB in ideal places, can the system run on
/// less of it?
///
/// <para><b>What is fixed.</b> The case, its dispatch and its power flow. Today's synchronous fleet keeps
/// <see cref="InertiaPlacementOptions.ResidualFleetFraction"/> of its inertia (each machine's H scaled, nothing moved).
/// The rest of each budget is synchronous condensers of one inertia constant (<see cref="InertiaPlacementOptions.CondenserH"/>)
/// that inject no power and no reactive power at the solved state (so the power flow is that of the case), placed by the
/// placement under test. So between placements only the location of the added inertia (and of the condenser MVA that
/// comes with it) changes.</para>
///
/// <para><b>What is measured.</b> For each loss (a step of constant admittance drawing the loss's MW at the bus's solved
/// voltage, the RMS engine's load-step event, at the transmission bus nearest the named site), the RMS engine
/// (<see cref="RmsTransientStability.StudyEventsMeasured"/>, mechanical power held: no frequency response) gives the
/// centre-of-inertia frequency and every measured bus's voltage angle. From them: the centre-of-inertia RoCoF and the
/// worst bus RoCoF (both over 500 ms, <see cref="BusFrequencyMeter"/>), their ratio K (the spatial penalty), the worst
/// voltage angle jump, the north-south inter-area oscillation, and the time the centre-of-inertia frequency takes to
/// fall to 49.8 Hz. The lumped model (<see cref="FrequencyDynamics"/>, with the default GB response) gives the same
/// time with frequency response, which does not depend on placement.</para>
///
/// <para>Deterministic: every list is walked in a fixed order and every function is the deterministic one, so the same
/// inputs give the same bits on every CPU and operating system.</para>
/// </summary>
public static class InertiaPlacementStudy
{
    /// <summary>Mean Earth radius in statute miles (IUGG mean radius 6371.0088 km).</summary>
    public const double EarthRadiusMiles = 3958.7613;

    /// <summary>The inverted T / ladder sites of the October 2026 discussion: a spine from Inverness down through the
    /// central belt and the Borders, splitting into the east (Yorkshire / Humber) and west (Cheshire) corridors, meeting
    /// at the Midlands, and a crossbar from Cornwall through the South West and the Thames to Kent and East Anglia.</summary>
    public static readonly IReadOnlyList<GeoSite> SpineTSites = new[]
    {
        new GeoSite("Inverness", 57.48, -4.47),
        new GeoSite("Central belt", 56.03, -3.90),
        new GeoSite("Borders", 55.60, -2.80),
        new GeoSite("East corridor (Yorkshire/Humber)", 53.62, -0.90),
        new GeoSite("West corridor (Cheshire)", 53.20, -2.90),
        new GeoSite("Midlands", 52.50, -1.70),
        new GeoSite("Cornwall", 50.40, -4.90),
        new GeoSite("South West (Somerset)", 51.20, -3.00),
        new GeoSite("Thames (London)", 51.50, -0.20),
        new GeoSite("Kent", 51.25, 0.90),
        new GeoSite("East Anglia", 52.10, 1.10),
    };

    /// <summary>The credible losses studied: the SQSS 1.8 GW infeed loss at four places the largest real infeeds connect.</summary>
    public static readonly IReadOnlyList<PlacementLoss> DefaultLosses = new[]
    {
        new PlacementLoss(new GeoSite("Sizewell (East Anglia)", 52.214, 1.620), 1800.0),
        new PlacementLoss(new GeoSite("Hinkley Point (South West)", 51.209, -3.130), 1800.0),
        new PlacementLoss(new GeoSite("Kent interconnectors (Sellindge)", 51.105, 0.975), 1800.0),
        new PlacementLoss(new GeoSite("Scotland over B6 (Torness)", 55.968, -2.408), 1800.0),
    };

    /// <summary>WGS84 latitude of a bus from GridSim's 0..1 GB layout (the inverse of
    /// <see cref="IO.Geo.OsgbGrid"/>'s layout: y = (lat - 49.8) / 9.2).</summary>
    public static double LatOf(Bus b) => b.Y * 9.2 + 49.8;

    /// <summary>WGS84 longitude of a bus from GridSim's 0..1 GB layout (x = (lon + 8.2) / 10.1).</summary>
    public static double LonOf(Bus b) => b.X * 10.1 - 8.2;

    /// <summary>Great-circle distance in statute miles (haversine, deterministic trigonometry).</summary>
    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        double rad = Math.PI / 180.0;
        double dLat = (lat2 - lat1) * rad, dLon = (lon2 - lon1) * rad;
        double s1 = DetMath.Sin(dLat / 2.0), s2 = DetMath.Sin(dLon / 2.0);
        double a = s1 * s1 + DetMath.Cos(lat1 * rad) * DetMath.Cos(lat2 * rad) * s2 * s2;
        return 2.0 * EarthRadiusMiles * DetMath.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
    }

    /// <summary>
    /// True for a bus a condenser can go on: any bus but a slack bus. Bug #35 (2.0.143): the RMS initialisation splits a
    /// bus's solved generation between its units by scheduled power, and equally when the scheduled total is zero; a slack
    /// bus's units are scheduled at zero but its solved output is not, so a condenser there took an equal share of the
    /// slack's output (half of it, beside the national slack on gb-full) and started with a field its exciter could not
    /// hold. Elsewhere a condenser takes none of a bus's power: zero of a positive scheduled total, or an equal share of
    /// a solved output that is itself zero.
    /// </summary>
    public static bool CanHoldCondenser(Bus b) => b.Type != BusType.Slack;

    /// <summary>The transmission bus (275 kV and above, with a layout position, not a slack bus:
    /// <see cref="CanHoldCondenser"/>) nearest a site; the lower bus id wins a tie. Refused when the model has no such
    /// bus.</summary>
    public static MappedSite MapSite(GridModel model, GeoSite site)
    {
        Bus? best = null;
        double bestD = double.PositiveInfinity;
        foreach (Bus b in model.Buses)
        {
            if (b.BaseKv < 275.0 || (b.X == 0.0 && b.Y == 0.0) || !CanHoldCondenser(b)) continue;
            double d = Miles(site.LatDeg, site.LonDeg, LatOf(b), LonOf(b));
            if (d < bestD || (d == bestD && best is not null && b.Id < best.Id)) { best = b; bestD = d; }
        }
        if (best is null)
            throw new InvalidOperationException($"The model has no bus at 275 kV or above with a map position to put site '{site.Name}' on.");
        return new MappedSite(site, best.Id, best.Name ?? best.Id.ToString(CultureInfo.InvariantCulture), best.BaseKv, bestD);
    }

    /// <summary>True for a unit the RMS engine integrates as a synchronous machine (the engine's own rule).</summary>
    public static bool IsMachine(Generator g) => !g.PowerFlowOnly && !g.IsConverterInterfaced && g.H > 0.0;

    /// <summary>Today's fleet: the added inertia shared by bus in proportion to the synchronous inertia already there
    /// (a slack bus left out, <see cref="CanHoldCondenser"/>).</summary>
    public static InertiaPlacement FleetPlacement(GridModel model)
    {
        var slack = new HashSet<int>(model.Buses.Where(b => !CanHoldCondenser(b)).Select(b => b.Id));
        var byBus = new SortedDictionary<int, double>();
        foreach (Generator g in model.Generators)
            if (IsMachine(g) && !slack.Contains(g.Bus))
                byBus[g.Bus] = byBus.GetValueOrDefault(g.Bus) + g.H * MachineBase.EffectiveMva(g, model.BaseMva);
        return new InertiaPlacement("fleet", "where today's synchronous inertia is, in proportion to it", Normalise(byBus));
    }

    /// <summary>In proportion to demand: each bus's demand moved to its nearest transmission bus (275 kV and above, not a
    /// slack bus: <see cref="CanHoldCondenser"/>).</summary>
    public static InertiaPlacement DemandPlacement(GridModel model)
    {
        List<Bus> tx = model.Buses.Where(b => b.BaseKv >= 275.0 && !(b.X == 0.0 && b.Y == 0.0) && CanHoldCondenser(b))
            .OrderBy(b => b.Id).ToList();
        if (tx.Count == 0) throw new InvalidOperationException("The model has no transmission bus with a map position.");
        var byBus = new SortedDictionary<int, double>();
        foreach (Bus b in model.Buses.OrderBy(b => b.Id))
        {
            if (!(b.Pd > 0.0) || (b.X == 0.0 && b.Y == 0.0)) continue;
            Bus near = tx[0];
            double nd = double.PositiveInfinity;
            foreach (Bus t in tx)
            {
                double d = Miles(LatOf(b), LonOf(b), LatOf(t), LonOf(t));
                if (d < nd) { nd = d; near = t; }
            }
            byBus[near.Id] = byBus.GetValueOrDefault(near.Id) + b.Pd;
        }
        return new InertiaPlacement("demand", "in proportion to demand, gathered to the nearest 275/400 kV bus", Normalise(byBus));
    }

    /// <summary>Named sites with the given weights (equal when null).</summary>
    public static InertiaPlacement SitePlacement(string name, string description, IReadOnlyList<MappedSite> sites, IReadOnlyList<double>? weights = null)
    {
        var byBus = new SortedDictionary<int, double>();
        for (int i = 0; i < sites.Count; i++)
        {
            double w = weights?[i] ?? 1.0;
            if (w > 0.0) byBus[sites[i].BusId] = byBus.GetValueOrDefault(sites[i].BusId) + w;
        }
        return new InertiaPlacement(name, description, Normalise(byBus));
    }

    private static IReadOnlyList<(int, double)> Normalise(SortedDictionary<int, double> byBus)
    {
        double sum = 0.0;
        foreach (double v in byBus.Values) sum += v;
        if (!(sum > 0.0)) throw new InvalidOperationException("A placement needs at least one bus with a positive share.");
        var list = new List<(int, double)>(byBus.Count);
        foreach (KeyValuePair<int, double> kv in byBus) list.Add((kv.Key, kv.Value / sum));
        return list;
    }

    /// <summary>
    /// The case with the residual fleet and the condensers of one placement and budget: every synchronous machine's H
    /// times <paramref name="residualFraction"/>, then one condenser per placement bus (appended after every existing
    /// unit, so every existing generator index is unchanged) of rating <c>share x (budget - residual) / H_c</c>, at no
    /// power, no reactive power (a reactive range of a microvar each way, so it never takes a share of a bus's reactive
    /// output in the RMS initialisation) and a voltage setpoint that leaves the power flow as it was: the setpoint of the
    /// bus's last existing unit where it has one (the power flow takes a bus's setpoint from its last unit, so an appended
    /// condenser's setpoint would replace it: bug #37), else the bus's solved voltage.
    /// </summary>
    public static GridModel WithCondensers(GridModel model, PowerFlowResult solved, InertiaPlacement placement,
        double budgetGvaS, double residualFraction, double condenserH, out double residualGvaS)
    {
        var gens = new List<Generator>(model.Generators.Count + placement.Shares.Count);
        double residualMwS = 0.0;
        foreach (Generator g in model.Generators)
        {
            if (IsMachine(g))
            {
                Generator scaled = g with { H = g.H * residualFraction };
                residualMwS += scaled.H * MachineBase.EffectiveMva(scaled, model.BaseMva);
                gens.Add(scaled);
            }
            else gens.Add(g);
        }
        residualGvaS = residualMwS / 1000.0;
        double addMwS = budgetGvaS * 1000.0 - residualMwS;
        if (!(addMwS > 0.0))
            throw new ArgumentOutOfRangeException(nameof(budgetGvaS), budgetGvaS,
                $"The budget ({budgetGvaS} GVA.s) must exceed the residual fleet inertia ({residualGvaS:R} GVA.s).");
        var vm = new Dictionary<int, double>();
        var byId = new Dictionary<int, Bus>();
        for (int i = 0; i < model.Buses.Count; i++) { vm[model.Buses[i].Id] = solved.Vm[i]; byId[model.Buses[i].Id] = model.Buses[i]; }
        // Bug #37 (2.0.146): the bus setpoint is its last unit's Vg (VoltageSetpoint), so keep that one where there is one.
        foreach (Generator g in model.Generators) vm[g.Bus] = g.Vg;
        foreach ((int busId, double share) in placement.Shares)
        {
            if (!byId.TryGetValue(busId, out Bus? target))
                throw new ArgumentException($"Placement '{placement.Name}' names bus {busId}, which is not in the model.", nameof(placement));
            if (!CanHoldCondenser(target))
                throw new ArgumentException($"Placement '{placement.Name}' puts a condenser on bus {busId}, a slack bus: the RMS "
                    + "initialisation would hand it a share of the slack's output (bug #35).", nameof(placement));
            double mva = share * addMwS / condenserH;
            gens.Add(new Generator(Bus: busId, Pg: 0.0, Qg: 0.0, Vg: vm[busId], Qmin: -1e-6, Qmax: 1e-6, Pmin: 0.0, Pmax: 0.0,
                MBase: mva, H: condenserH, Name: $"SC {placement.Name} @{busId}", Type: "synchronous-condenser",
                ConverterInterfaced: false));
        }
        return model.CopyWith(generators: gens);
    }

    /// <summary>Run the study. <paramref name="machineData"/> gives each generator's RMS machine data (the CLI passes
    /// its synthetic GENROU builder, the same one <c>stability --rms</c> uses); <paramref name="solve"/> solves a power
    /// flow. <paramref name="progress"/> (optional) hears one line per run.</summary>
    public static InertiaPlacementReport Run(
        GridModel model, IReadOnlyList<InertiaPlacement> placements, IReadOnlyList<PlacementLoss> losses,
        InertiaPlacementOptions options, IReadOnlyList<MappedSite> sites,
        Func<Generator, SubtransientInit.MachineData> machineData,
        Func<GridModel, PowerFlowResult> solve,
        Func<Generator, RmsExciter?>? exciter = null,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(losses);
        ArgumentNullException.ThrowIfNull(options);
        if (placements.Count == 0) throw new ArgumentException("Name at least one placement.", nameof(placements));
        if (losses.Count == 0) throw new ArgumentException("Name at least one loss.", nameof(losses));
        CheckBudgets(options.BudgetsGvaS);

        var notes = new List<string>();
        PowerFlowResult basePf = solve(model);
        basePf.RequireConverged("inertia placement study (base case)");
        double fleetMwS = model.Generators.Where(IsMachine).Sum(g => g.H * MachineBase.EffectiveMva(g, model.BaseMva));
        double demand = model.Buses.Sum(b => b.Pd);
        List<int> measured = model.Buses.Where(b => b.BaseKv >= options.MeasuredMinKv).Select(b => b.Id).ToList();
        var lat = new Dictionary<int, double>();
        foreach (Bus b in model.Buses) lat[b.Id] = LatOf(b);
        var mappedLosses = losses.Select(l => (l, MapSite(model, l.Site))).ToList();

        var runs = new List<PlacementRun>();
        double residual = double.NaN;
        foreach (InertiaPlacement pl in placements)
            foreach (double budget in options.BudgetsGvaS)
            {
                GridModel m = WithCondensers(model, basePf, pl, budget, options.ResidualFleetFraction, options.CondenserH, out residual);
                PowerFlowResult pf = solve(m);
                pf.RequireConverged($"inertia placement study ({pl.Name}, {budget} GVA.s)");
                double dv = MaxVoltageChange(basePf, pf);
                if (dv > 1e-9) notes.Add($"{pl.Name} at {budget} GVA.s: adding the condensers moved a bus voltage by {dv:R} pu.");
                var data = m.Generators.Select(machineData).ToList();
                foreach ((PlacementLoss loss, MappedSite at) in mappedLosses)
                {
                    PlacementRun r = OneRun(m, pf, data, Exciters(m, exciter), pl.Name, budget, loss, at, measured, lat, options);
                    runs.Add(r);
                    progress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"{pl.Name,-10} {budget,7:0} GVA.s  {loss.Site.Name,-34} K {r.SpatialPenaltyK:0.000}  RMS worst {r.WorstBusRocofHzPerS:0.0000} COI {r.CoiRocofHzPerS:0.0000} Hz/s (felt {r.EffectiveLossMw:0} MW)  full-loss worst {r.ScaledWorstBusRocofHzPerS:0.0000} Hz/s"));
                }
            }

        var lumped = new List<LumpedResponseRun>();
        foreach (double budget in options.BudgetsGvaS)
            foreach (double mw in losses.Select(l => l.Mw).Distinct().OrderBy(x => x))
                lumped.Add(Lumped(model, budget, demand, mw, options));

        var minimums = new List<PlacementMinimum>();
        foreach (InertiaPlacement pl in placements)
        {
            List<PlacementRun> mine = runs.Where(r => r.Placement == pl.Name).ToList();
            foreach (double limit in options.RocofLimitsHzPerS)
                minimums.Add(Minimum(pl.Name, string.Create(CultureInfo.InvariantCulture, $"A: worst bus RoCoF <= {limit} Hz/s"),
                    options.BudgetsGvaS, b => mine.Where(r => r.BudgetGvaS == b).Max(r => r.ScaledWorstBusRocofHzPerS), limit, lowerIsBetter: true));
            foreach (double target in options.TimeToFloorTargetsSeconds)
                minimums.Add(Minimum(pl.Name, string.Create(CultureInfo.InvariantCulture, $"B: >= {target} s to {options.FrequencyFloorHz} Hz, no response"),
                    options.BudgetsGvaS, b => mine.Where(r => r.BudgetGvaS == b).Min(r => r.SwingTimeToFloorSeconds),
                    target, lowerIsBetter: false));
        }

        return new InertiaPlacementReport(options, fleetMwS / 1000.0, residual, demand, measured.Count,
            sites, mappedLosses, placements, runs, lumped, minimums, notes);
    }

    /// <summary>One RMS run and its measures.</summary>
    public static PlacementRun OneRun(GridModel m, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data,
        IReadOnlyList<RmsExciter?>? exciters,
        string placement, double budget, PlacementLoss loss, MappedSite at, IReadOnlyList<int> measured,
        IReadOnlyDictionary<int, double> lat, InertiaPlacementOptions o)
    {
        var events = new RmsEvent[] { new RmsLoadStep(o.EventSeconds, at.BusId, loss.Mw, 0.0) };
        RmsTransientStability.MeasuredEventStudy st = RmsTransientStability.StudyEventsMeasured(m, pf, data, events, measured,
            stepSeconds: o.StepSeconds, durationSeconds: o.EventSeconds + o.PostEventSeconds, exciters: exciters);
        double f0 = st.NominalHz;
        BusFrequencyResult bf = BusFrequencyMeter.Measure(st.Buses, f0, o.StepSeconds, new[] { o.EventSeconds });
        int nw = BusFrequencyMeter.WholeSamples(BusFrequencyMeter.DefaultRocofWindowSeconds, o.StepSeconds, "the RoCoF window");

        // Centre-of-inertia frequency over every real integrated machine (the residual fleet and the condensers; the island
        // reference sources the RMS preparation adds are left out).
        IReadOnlyList<MultiMachineSubtransient.MachineTrace> tr = st.Result.Machines;
        int samples = tr[0].TimeSeconds.Count;
        int machines = st.RealMachines;
        double hSum = 0.0;
        for (int i = 0; i < machines; i++) hSum += st.H[i];
        var fCoi = new double[samples];
        for (int k = 0; k < samples; k++)
        {
            double acc = 0.0;
            for (int i = 0; i < machines; i++) acc += st.H[i] * tr[i].SpeedPu[k];
            fCoi[k] = f0 * (1.0 + acc / hSum);
        }
        double coiRocof = BusFrequencyMeter.WorstWindowedRocof(fCoi, nw, o.StepSeconds);

        int worstBus = -1; double worst = -1.0;
        int jumpBus = -1; double jump = -1.0;
        for (int b = 0; b < bf.BusIds.Count; b++)
        {
            if (bf.WorstRocofHzPerS[b] > worst) { worst = bf.WorstRocofHzPerS[b]; worstBus = bf.BusIds[b]; }
            if (bf.AngleJumpDeg[b] > jump) { jump = bf.AngleJumpDeg[b]; jumpBus = bf.BusIds[b]; }
        }

        // Time to the floor and the lowest COI frequency after the event.
        int ke = 0;
        while (ke < samples && tr[0].TimeSeconds[ke] + o.StepSeconds * MultiMachineSubtransient.StageTimeToleranceSteps < o.EventSeconds) ke++;
        double tFloor = double.NaN, lowest = fCoi[0];
        for (int k = 0; k < samples; k++) lowest = Math.Min(lowest, fCoi[k]);
        for (int k = Math.Max(1, ke); k < samples; k++)
            if (fCoi[k] <= o.FrequencyFloorHz)
            {
                double t0 = tr[0].TimeSeconds[k - 1], t1 = tr[0].TimeSeconds[k];
                double f0k = fCoi[k - 1], f1k = fCoi[k];
                double tc = f0k == f1k ? t1 : t0 + (f0k - o.FrequencyFloorHz) / (f0k - f1k) * (t1 - t0);
                tFloor = tc - o.EventSeconds;
                break;
            }

        (double zeta, double fOsc, int halfCycles) = InterArea(st, lat, o, ke);
        double penalty = worst / coiRocof;
        double ekMwS = hSum * m.BaseMva;                                   // the integrated machines' stored energy
        double effective = 2.0 * ekMwS * coiRocof / f0;
        double swingRocof = f0 * loss.Mw / (2.0 * budget * 1000.0);
        double swingTime = (f0 - o.FrequencyFloorHz) * 2.0 * budget * 1000.0 / (f0 * loss.Mw);
        return new PlacementRun(placement, budget, loss.Site.Name, at.BusId, loss.Mw, coiRocof, worst, worstBus,
            worstBus >= 0 ? lat[worstBus] : double.NaN, penalty, jump, jumpBus, zeta, fOsc, tFloor, lowest,
            st.Result.StayedInSynchronism, st.Result.ConverterIterationFailures,
            ekMwS / 1000.0, effective, penalty * swingRocof, swingTime, halfCycles);
    }

    /// <summary>One exciter per generator of <paramref name="m"/> (null for every unit that is not an integrated machine,
    /// and for every unit when <paramref name="exciter"/> is null: the field held), each a fresh instance.</summary>
    public static IReadOnlyList<RmsExciter?>? Exciters(GridModel m, Func<Generator, RmsExciter?>? exciter)
    {
        if (exciter is null) return null;
        var list = new List<RmsExciter?>(m.Generators.Count);
        foreach (Generator g in m.Generators) list.Add(IsMachine(g) ? exciter(g) : null);
        return list;
    }

    /// <summary>The result of <see cref="Optimise"/>: the site weights found, the objective (the worst bus RoCoF over
    /// every loss, Hz/s) they give, and the objective of the equal weights it started from.</summary>
    public sealed record OptimisedSites(IReadOnlyList<double> Weights, double Objective, double StartObjective, int Evaluations);

    /// <summary>
    /// A coordinate search over the site weights at one budget: starting from equal weights, each pass tries each site's
    /// weight doubled and halved (the others unchanged, the shares renormalised) and keeps the change that lowers the
    /// objective most, if any; the objective is the full-loss worst bus RoCoF over every loss (criterion A's measure). A simple,
    /// deterministic local search: it finds a better placement than equal weights when one is near, not a proven
    /// optimum.
    /// </summary>
    public static OptimisedSites Optimise(
        GridModel model, IReadOnlyList<MappedSite> sites, double budgetGvaS, IReadOnlyList<PlacementLoss> losses,
        InertiaPlacementOptions options, int passes,
        Func<Generator, SubtransientInit.MachineData> machineData, Func<GridModel, PowerFlowResult> solve,
        Func<Generator, RmsExciter?>? exciter = null,
        Action<string>? progress = null)
    {
        if (passes < 1) throw new ArgumentOutOfRangeException(nameof(passes), passes, "At least one pass.");
        PowerFlowResult basePf = solve(model);
        basePf.RequireConverged("inertia placement optimisation (base case)");
        List<int> measured = model.Buses.Where(b => b.BaseKv >= options.MeasuredMinKv).Select(b => b.Id).ToList();
        var lat = new Dictionary<int, double>();
        foreach (Bus b in model.Buses) lat[b.Id] = LatOf(b);
        var mappedLosses = losses.Select(l => (l, MapSite(model, l.Site))).ToList();
        int evaluations = 0;

        double Objective(IReadOnlyList<double> w)
        {
            evaluations++;
            InertiaPlacement pl = SitePlacement("optimised", "search", sites, w);
            GridModel m = WithCondensers(model, basePf, pl, budgetGvaS, options.ResidualFleetFraction, options.CondenserH, out _);
            PowerFlowResult pf = solve(m);
            pf.RequireConverged("inertia placement optimisation");
            var data = m.Generators.Select(machineData).ToList();
            double worst = 0.0;
            foreach ((PlacementLoss loss, MappedSite at) in mappedLosses)
                worst = Math.Max(worst, OneRun(m, pf, data, Exciters(m, exciter), "optimised", budgetGvaS, loss, at, measured, lat, options).ScaledWorstBusRocofHzPerS);
            return worst;
        }

        var weights = Enumerable.Repeat(1.0, sites.Count).ToArray();
        double best = Objective(weights);
        double start = best;
        progress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"optimise: equal weights, worst bus RoCoF {best:0.00000} Hz/s"));
        for (int pass = 0; pass < passes; pass++)
        {
            bool improved = false;
            for (int s = 0; s < sites.Count; s++)
            {
                double[]? bestTrial = null;
                double bestTrialObj = best;
                foreach (double factor in new[] { 2.0, 0.5 })
                {
                    var trial = (double[])weights.Clone();
                    trial[s] *= factor;
                    double obj = Objective(trial);
                    if (obj < bestTrialObj) { bestTrialObj = obj; bestTrial = trial; }
                }
                if (bestTrial is not null)
                {
                    weights = bestTrial; best = bestTrialObj; improved = true;
                    progress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"optimise: pass {pass + 1}, {sites[s].Site.Name} weight {weights[s]:0.###}, worst bus RoCoF {best:0.00000} Hz/s"));
                }
            }
            if (!improved) break;
        }
        return new OptimisedSites(weights, best, start, evaluations);
    }

    /// <summary>The north-south inter-area oscillation: the difference of the north and south machines' inertia-weighted
    /// speeds after the event, measured by <see cref="Oscillation"/> with a 0.4 s smoothing window.</summary>
    private static (double Zeta, double FreqHz, int HalfCycles) InterArea(RmsTransientStability.MeasuredEventStudy st,
        IReadOnlyDictionary<int, double> lat, InertiaPlacementOptions o, int ke)
    {
        IReadOnlyList<MultiMachineSubtransient.MachineTrace> tr = st.Result.Machines;
        int machines = st.RealMachines, samples = tr[0].TimeSeconds.Count;
        double hn = 0.0, hs = 0.0;
        for (int i = 0; i < machines; i++)
        {
            double la = lat[st.MachineBuses[i]];
            if (la >= o.NorthLatDeg) hn += st.H[i]; else if (la <= o.SouthLatDeg) hs += st.H[i];
        }
        if (!(hn > 0.0) || !(hs > 0.0)) return (double.NaN, double.NaN, 0);
        var x = new double[samples];
        for (int k = 0; k < samples; k++)
        {
            double an = 0.0, aS = 0.0;
            for (int i = 0; i < machines; i++)
            {
                double la = lat[st.MachineBuses[i]];
                if (la >= o.NorthLatDeg) an += st.H[i] * tr[i].SpeedPu[k]; else if (la <= o.SouthLatDeg) aS += st.H[i] * tr[i].SpeedPu[k];
            }
            x[k] = an / hn - aS / hs;
        }
        int smooth = Math.Max(1, (int)Math.Round(InterAreaSmoothingSeconds / o.StepSeconds));
        return Oscillation(x, o.StepSeconds, ke, smooth);
    }

    /// <summary>The smoothing window (s) of the inter-area measure: 0.4 s, one period of a 2.5 Hz local swing, so local
    /// machine-against-machine swings (about 1.5 to 3 Hz) are averaged out of the north-south difference while the
    /// inter-area swing (about 0.2 to 1.3 Hz) keeps its decay rate.</summary>
    public const double InterAreaSmoothingSeconds = 0.4;

    /// <summary>The fewest half-cycles a damping ratio is fitted over (two full cycles).</summary>
    public const int MinHalfCycles = 4;

    /// <summary>
    /// The damping ratio and frequency of the dominant oscillation of <paramref name="x"/> from sample
    /// <paramref name="from"/> on. The series is smoothed by a centred moving average of <paramref name="smoothSamples"/>
    /// samples (only where the whole window fits), its turning points found, and each half-cycle's swing (the change from
    /// one turning point to the next) taken. The decay rate sigma is the least-squares slope of ln(swing) against the time
    /// of each half-cycle's midpoint, the frequency 1 / (2 x mean half-period), and the damping ratio
    /// <c>-sigma / sqrt(sigma^2 + omega^2)</c> (negative for a growing swing). Every half-cycle the run shows is used, not
    /// only the first two peaks (the 2.0.143 to 2.0.149 measure, which on gb-full latched onto a 2.5 to 3.1 Hz local swing
    /// in some runs). NaN with the half-cycle count when fewer than <see cref="MinHalfCycles"/> are seen. Deterministic.
    /// </summary>
    public static (double Zeta, double FreqHz, int HalfCycles) Oscillation(double[] x, double stepSeconds, int from, int smoothSamples)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (smoothSamples < 1) throw new ArgumentOutOfRangeException(nameof(smoothSamples), smoothSamples, "At least one sample.");
        int half = smoothSamples / 2;
        int start = Math.Max(from, half), end = x.Length - (smoothSamples - half);   // the window [k-half, k-half+smooth) fits
        var y = new List<double>();
        var t = new List<double>();
        for (int k = start; k <= end; k++)
        {
            double acc = 0.0;
            for (int j = k - half; j < k - half + smoothSamples; j++) acc += x[j];
            y.Add(acc / smoothSamples);
            t.Add(k * stepSeconds);
        }
        var turns = new List<int>();
        for (int k = 1; k < y.Count - 1; k++)
            if ((y[k] > y[k - 1] && y[k] >= y[k + 1]) || (y[k] < y[k - 1] && y[k] <= y[k + 1])) turns.Add(k);
        int halfCycles = turns.Count - 1;
        if (halfCycles < MinHalfCycles) return (double.NaN, double.NaN, Math.Max(0, halfCycles));
        int n = halfCycles;
        double sumT = 0.0, sumL = 0.0, sumTT = 0.0, sumTL = 0.0, sumHalf = 0.0;
        for (int i = 0; i < n; i++)
        {
            double swing = Math.Abs(y[turns[i + 1]] - y[turns[i]]);
            if (!(swing > 0.0)) return (double.NaN, double.NaN, halfCycles);
            double tm = 0.5 * (t[turns[i]] + t[turns[i + 1]]);
            double l = DetMath.Log(swing);
            sumT += tm; sumL += l; sumTT += tm * tm; sumTL += tm * l;
            sumHalf += t[turns[i + 1]] - t[turns[i]];
        }
        double sigma = (n * sumTL - sumT * sumL) / (n * sumTT - sumT * sumT);
        double freq = 1.0 / (2.0 * sumHalf / n);
        double omega = 2.0 * Math.PI * freq;
        return (-sigma / Math.Sqrt(sigma * sigma + omega * omega), freq, halfCycles);
    }

    /// <summary>The lumped centre-of-inertia run with the default GB response (placement-independent).</summary>
    public static LumpedResponseRun Lumped(GridModel model, double budgetGvaS, double demandMw, double lossMw, InertiaPlacementOptions o)
    {
        var cfg = new DynamicsConfig { NominalHz = model.FrequencyHz, DurationSeconds = FrequencyDynamics.DesignEventSeconds + 60.0 };
        FrequencyResponse fr = FrequencyDynamics.Simulate(budgetGvaS * 1000.0, demandMw,
            new[] { new FrequencyEvent(FrequencyDynamics.DesignEventSeconds, -lossMw) }, cfg);
        double t = double.NaN;
        for (int k = 1; k < fr.Points.Count; k++)
        {
            double fk = fr.Points[k].Hz;
            if (fk <= o.FrequencyFloorHz)
            {
                double fp = fr.Points[k - 1].Hz;
                double tp = (k - 1) * cfg.StepSeconds, tk = k * cfg.StepSeconds;
                double tc = fp == fk ? tk : tp + (fp - o.FrequencyFloorHz) / (fp - fk) * (tk - tp);
                t = tc - FrequencyDynamics.DesignEventSeconds;
                break;
            }
        }
        return new LumpedResponseRun(budgetGvaS, lossMw, t, fr.NadirHz);
    }

    private static PlacementMinimum Minimum(string placement, string criterion, IReadOnlyList<double> budgets,
        Func<double, double> metric, double limit, bool lowerIsBetter)
    {
        bool Pass(double v) => lowerIsBetter ? v <= limit : v >= limit;
        double lastFail = double.NaN, lastFailV = double.NaN;
        for (int i = 0; i < budgets.Count; i++)
        {
            double v = metric(budgets[i]);
            if (Pass(v))
            {
                double interp = double.IsNaN(lastFail) ? double.NaN
                    : lastFail + (limit - lastFailV) / (v - lastFailV) * (budgets[i] - lastFail);
                return new PlacementMinimum(placement, criterion, budgets[i], lastFail, interp);
            }
            lastFail = budgets[i]; lastFailV = v;
        }
        return new PlacementMinimum(placement, criterion, double.NaN, lastFail, double.NaN);
    }

    private static double MaxVoltageChange(PowerFlowResult a, PowerFlowResult b)
    {
        double d = 0.0;
        for (int i = 0; i < a.Vm.Length && i < b.Vm.Length; i++) d = Math.Max(d, Math.Abs(a.Vm[i] - b.Vm[i]));
        return d;
    }

    private static void CheckBudgets(IReadOnlyList<double> budgets)
    {
        if (budgets.Count == 0) throw new ArgumentException("Name at least one budget.", nameof(budgets));
        for (int i = 0; i < budgets.Count; i++)
        {
            if (!(budgets[i] > 0.0) || !double.IsFinite(budgets[i]))
                throw new ArgumentOutOfRangeException(nameof(budgets), budgets[i], "Each budget must be a finite number of GVA.s above 0.");
            if (i > 0 && !(budgets[i] > budgets[i - 1]))
                throw new ArgumentException("Budgets must be in ascending order with no repeats.", nameof(budgets));
        }
    }
}
