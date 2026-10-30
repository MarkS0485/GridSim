using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Model;

namespace GridSim.Core.Storage;

/// <summary>
/// The storage sites of a sequential study, carried step to step: each <see cref="Advance"/> asks every site's
/// policy for a power, lets its chemistry pack deliver what it can over the step (in substeps, behind its
/// converter), and books the energy; <see cref="Apply"/> then puts the delivered power and the auxiliary load
/// onto the step's network model. Shared by the storage time series (<c>chain</c>), the live loop
/// (<see cref="Simulation.LiveSim"/>) and the historical replay (<see cref="Simulation.GdaReplayLoop"/>), so all
/// three carry SoC, temperature and ageing the same way.
/// </summary>
public sealed class StorageFleet
{
    /// <summary>Default number of pack-model substeps per network step (R198).</summary>
    public const int DefaultSubstepsPerStep = 30;

    private readonly BatteryDcSource[] _sources;
    // Bug hunt 2026-10-29, finding 63: the SoC history is kept as its rainflow TURNING POINTS, not one value per sub-step.
    // Its only reader is Rainflow.Count, whose first act is this same reduction (drop a repeat, extend a run in the same
    // direction), and the reduction of an already-reduced series is itself, so the cycles and every summary are bit for
    // bit what the full trace gave. The full trace grew by one value per sub-step for the life of the fleet (30 per step
    // by default, about 525,000 per site for a year at 30-minute steps); the turning points grow only with reversals.
    private readonly List<double>[] _socTrace;

    /// <summary>Append <paramref name="x"/> to a turning-point history with exactly <see cref="Rainflow.Count"/>'s rule.</summary>
    internal static void AppendTurningPoint(List<double> tp, double x)
    {
        if (tp.Count != 0 && x == tp[^1]) return;
        if (tp.Count >= 2 && (tp[^1] - tp[^2]) * (x - tp[^1]) > 0) tp[^1] = x;   // same direction: extend
        else tp.Add(x);
    }

    /// <summary>The stored SoC history of site <paramref name="k"/> (turning points), for tests.</summary>
    internal IReadOnlyList<double> SocTurningPoints(int k) => _socTrace[k];
    private readonly double[] _startStoredMwh, _eOut, _eIn, _eAux, _revenue;
    private readonly int _substeps;
    private IReadOnlyList<SiteStepRecord> _last = Array.Empty<SiteStepRecord>();

    public StorageFleet(IReadOnlyList<StorageSite> sites, int substepsPerStep = DefaultSubstepsPerStep)
    {
        Sites = sites ?? throw new ArgumentNullException(nameof(sites));
        _substeps = Math.Max(1, substepsPerStep);
        _sources = sites.Select(s => new BatteryDcSource(s.Pack, s.ConverterEfficiency)).ToArray();
        _socTrace = sites.Select(s => new List<double> { s.Pack.Soc }).ToArray();
        _startStoredMwh = sites.Select(s => s.Pack.AvailableEnergyWh / 1e6).ToArray();
        _eOut = new double[sites.Count]; _eIn = new double[sites.Count];
        _eAux = new double[sites.Count]; _revenue = new double[sites.Count];
    }

    public IReadOnlyList<StorageSite> Sites { get; }

    /// <summary>The records of the most recent <see cref="Advance"/>.</summary>
    public IReadOnlyList<SiteStepRecord> Last => _last;

    /// <summary>Check every site is on a bus of <paramref name="model"/>.</summary>
    public void Validate(GridModel model)
    {
        var ids = new HashSet<int>(model.Buses.Select(b => b.Id));
        foreach (StorageSite s in Sites)
            if (!ids.Contains(s.Bus)) throw new ArgumentException($"storage site '{s.Name}' is at unknown bus {s.Bus}.");
    }

    /// <summary>Run every site through one step of <paramref name="stepHours"/>.</summary>
    public IReadOnlyList<SiteStepRecord> Advance(int step, double hour, double stepHours, double demandMw, double priceGbpPerMwh)
    {
        var records = new List<SiteStepRecord>(Sites.Count);
        double subDt = stepHours * 3600.0 / _substeps;
        for (int k = 0; k < Sites.Count; k++)
        {
            StorageSite site = Sites[k];
            var ctx = new DispatchContext(step, hour, stepHours, demandMw, priceGbpPerMwh, site.Pack.Soc, site.RatedMw);
            double request = Math.Clamp(site.Policy.Request(ctx), -site.RatedMw, site.RatedMw);

            double delivered = 0, aux = 0;
            bool limited = false;
            string? reason = null;
            for (int j = 0; j < _substeps; j++)
            {
                delivered += _sources[k].Draw(request, subDt);
                PackStepResult r = _sources[k].LastStep!;
                aux += r.AuxiliaryPowerW / 1e6;
                if (r.Limited && !limited) { limited = true; reason = r.LimitReason; }
                AppendTurningPoint(_socTrace[k], site.Pack.Soc);
            }
            delivered /= _substeps; aux /= _substeps;

            _eOut[k] += Math.Max(delivered, 0) * stepHours;
            _eIn[k] += Math.Max(-delivered, 0) * stepHours;
            _eAux[k] += aux * stepHours;
            if (!double.IsNaN(priceGbpPerMwh)) _revenue[k] += priceGbpPerMwh * (delivered - aux) * stepHours;
            records.Add(new SiteStepRecord(site.Name, request, delivered, aux, site.Pack.Soc,
                site.Pack.TemperatureC, site.Pack.Cell.CapacityFade, limited, reason));
        }
        _last = records;
        return records;
    }

    /// <summary><paramref name="model"/> with each site's delivered power injected at its bus (a converter-
    /// interfaced storage generator) and its auxiliary load added to that bus's demand, from the last
    /// <see cref="Advance"/>.</summary>
    public GridModel Apply(GridModel model)
    {
        if (_last.Count == 0) return model;
        var gens = new List<Generator>(_last.Count);
        var auxByBus = new Dictionary<int, double>();
        for (int k = 0; k < Sites.Count; k++)
        {
            StorageSite site = Sites[k];
            SiteStepRecord r = _last[k];
            // The storage unit must not move the bus voltage setpoint (R045, October 2026). It is appended AFTER
            // the existing generators, and the shared setpoint rule (VoltageSetpoint: the last generator at the
            // bus wins) used to take its placeholder Vg = 1.0, so a site on a PV or slack bus regulated at, say,
            // 1.04 pu silently re-regulated it to 1.00 pu. It now carries the setpoint the bus already has: the
            // existing generators' (by the same rule), else the bus's own stored Vm. On a PQ bus Vg is never read.
            Bus bus = model.Buses.FirstOrDefault(b => b.Id == site.Bus)
                ?? throw new InvalidOperationException(
                    $"Storage site '{site.Name}' is placed at bus {site.Bus}, which is not in model '{model.Name}'.");
            double vg = PowerFlow.VoltageSetpoint.ForBus(model, site.Bus) ?? bus.Vm;
            gens.Add(new Generator(site.Bus, Pg: r.DeliveredMw, Qg: 0, Vg: vg, Qmin: 0, Qmax: 0,
                Pmin: -site.RatedMw, Pmax: site.RatedMw, MBase: site.RatedMw, Name: site.Name, Type: "storage",
                ConverterInterfaced: true));
            auxByBus[site.Bus] = auxByBus.GetValueOrDefault(site.Bus) + r.AuxiliaryMw;
        }
        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        return model.CopyWith(
            buses: model.Buses.Select(b => auxByBus.TryGetValue(b.Id, out double a) ? b with { Pd = b.Pd + a } : b).ToList(),
            generators: model.Generators.Concat(gens).ToList());
    }

    /// <summary>Per-site totals so far (see <see cref="SiteSummary"/>).</summary>
    public IReadOnlyList<SiteSummary> Summaries()
    {
        var list = new List<SiteSummary>(Sites.Count);
        for (int k = 0; k < Sites.Count; k++)
        {
            StorageSite site = Sites[k];
            double storedChange = site.Pack.AvailableEnergyWh / 1e6 - _startStoredMwh[k];
            // Round trip: energy out over energy in, crediting any net change in what is still stored.
            double rte = _eIn[k] > 1e-9 ? (_eOut[k] + Math.Max(0, storedChange)) / (_eIn[k] + Math.Max(0, -storedChange)) : double.NaN;
            double nominal = site.Pack.NominalEnergyWh / 1e6;
            List<(double Depth, double Count)> cycles = Rainflow.Count(_socTrace[k]);
            AgeingModel ageing = site.Pack.Ageing ?? new AgeingModel(site.Pack.Chemistry.Ageing);
            list.Add(new SiteSummary(site.Name, _eOut[k], _eIn[k], _eAux[k], storedChange, rte, _revenue[k],
                nominal > 0 ? (_eOut[k] + _eIn[k]) / (2.0 * nominal) : 0.0,
                ageing.CalendarFade, ageing.CycleFade, ageing.CycleFadeFromRainflow(cycles), cycles));
        }
        return list;
    }
}
