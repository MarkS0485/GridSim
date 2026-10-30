using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;

namespace GridSim.Core.Storage;

/// <summary>A storage plant in a time-series run: where it connects, its AC rating, the chemistry pack behind
/// its converter, and the dispatch policy that decides what it is asked to do.</summary>
public sealed record StorageSite(
    string Name, int Bus, double RatedMw, PackModel Pack, IStorageDispatchPolicy Policy, double ConverterEfficiency = 0.98);

/// <summary>The time axis of a run: step count and length, the demand multiplier and (optionally) the energy
/// price per step, the clock at step 0, and how many chemistry substeps each network step is split into.</summary>
public sealed record StorageTimeSeriesSpec(
    int Steps, double StepHours, Func<int, double> LoadFactor, Func<int, double>? PriceGbpPerMwh = null,
    double StartHour = 0.0, int SubstepsPerStep = 30);

/// <summary>One site at one step: what it was asked for, what the pack delivered (AC, mean over the step), its
/// balance-of-plant draw, and its state at the end of the step.</summary>
public sealed record SiteStepRecord(
    string Name, double RequestedMw, double DeliveredMw, double AuxiliaryMw, double Soc, double TemperatureC,
    double CapacityFade, bool Limited, string? LimitReason);

/// <summary>The network at one step, with every site's record.</summary>
public sealed record StorageStepRecord(
    int Step, double Hour, double LoadFactor, double DemandMw, double PriceGbpPerMwh, bool Converged,
    double VminPu, double VmaxPu, double LossMw, double SlackMw, IReadOnlyList<SiteStepRecord> Sites);

/// <summary>A site over the whole run: energy out/in and auxiliary energy (MWh, AC), round-trip efficiency
/// (out / in, corrected for the change in stored energy), revenue against the price series, equivalent full
/// cycles, ageing (calendar, throughput-cycle, and the depth-aware rainflow estimate), and the rainflow cycles.</summary>
public sealed record SiteSummary(
    string Name, double EnergyOutMwh, double EnergyInMwh, double AuxiliaryMwh, double StoredChangeMwh,
    double RoundTripEfficiency, double RevenueGbp, double EquivalentFullCycles,
    double CalendarFade, double CycleFade, double RainflowCycleFade, IReadOnlyList<(double Depth, double Count)> Cycles);

public sealed record StorageTimeSeriesResult(IReadOnlyList<StorageStepRecord> Steps, IReadOnlyList<SiteSummary> Sites);

/// <summary>
/// Storage carried through a time series of power flows - the missing link between the chemistry model and the
/// sequential studies (<c>chain</c>, replay, the live loop). Each step:
/// <list type="number">
///   <item>the demand is scaled (<see cref="GridScenario.AtLoad"/>) and the dispatch policy asks each site for a
///   power;</item>
///   <item>the site's pack delivers what its chemistry allows, stepped in substeps (voltage sag, BMS limits,
///   heating, HVAC, ageing), behind its converter efficiency;</item>
///   <item>the delivered power is injected at the site's bus and the auxiliary load added as demand there, and
///   the AC power flow is solved warm-started from the previous step.</item>
/// </list>
/// So SoC, temperature and ageing persist between steps, and the network sees what the battery actually did.
/// </summary>
public static class StorageTimeSeries
{
    /// <summary>Default Newton iteration cap of each storage time-series power-flow step (R198).</summary>
    public const int DefaultMaxIterations = 100;

    public static StorageTimeSeriesResult Run(
        GridModel baseModel, IReadOnlyList<StorageSite> sites, StorageTimeSeriesSpec spec, IPowerFlowSolver solver,
        PowerFlowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseModel);
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(solver);
        PowerFlowOptions opt = options ?? new PowerFlowOptions { MaxIterations = DefaultMaxIterations, EnforceQLimits = true };
        var fleet = new StorageFleet(sites, spec.SubstepsPerStep);
        fleet.Validate(baseModel);

        var records = new List<StorageStepRecord>(spec.Steps);
        PowerFlowResult? prev = null;
        for (int step = 0; step < spec.Steps; step++)
        {
            double hour = (spec.StartHour + step * spec.StepHours) % 24.0;
            double factor = spec.LoadFactor(step);
            double price = spec.PriceGbpPerMwh?.Invoke(step) ?? double.NaN;
            GridModel m = GridScenario.AtLoad(baseModel, factor);
            double demand = m.Buses.Sum(b => b.Pd);

            IReadOnlyList<SiteStepRecord> siteRecords = fleet.Advance(step, hour, spec.StepHours, demand, price);
            GridModel withStorage = fleet.Apply(m);
            if (prev is not null) withStorage = GridScenario.WithSolvedVoltages(withStorage, prev);
            PowerFlowResult pf = solver.Solve(withStorage, opt);
            if (pf.Converged) prev = pf;

            // N028: one id -> bus map per step (first match wins, as First() did) instead of a linear bus search for
            // every result bus (O(n^2) per step). Same slack index, same Pd: bit-identical.
            var busById = new Dictionary<int, Bus>(withStorage.Buses.Count);
            foreach (Bus b in withStorage.Buses) busById.TryAdd(b.Id, b);
            int slackIdx = -1;
            for (int i = 0; i < pf.BusIds.Count; i++)
                if (busById[pf.BusIds[i]].Type == BusType.Slack) { slackIdx = i; break; }
            // D-DOC-137 (October 2026): a step whose power flow did not converge has no state, so its slack output and its
            // losses are NaN, as its voltages already were. They used to carry the non-converged iterate's numbers, which
            // the storage report printed as if they were a solved state. A converged step's values are the same bits.
            double slackMw = pf.Converged && slackIdx >= 0 ? pf.PInjMw[slackIdx] + busById[pf.BusIds[slackIdx]].Pd : double.NaN;
            records.Add(new StorageStepRecord(step, hour, factor, demand, price, pf.Converged,
                pf.Converged ? pf.Vm.Min() : double.NaN, pf.Converged ? pf.Vm.Max() : double.NaN,
                pf.Converged ? pf.TotalLossMw : double.NaN, slackMw, siteRecords));
        }
        return new StorageTimeSeriesResult(records, fleet.Summaries());
    }
}
