using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Simulation;

/// <summary>
/// Derives a point-in-time operating case from a base grid: scale demand (and the
/// non-slack generation that tracks it) by a load factor, then re-balance so the
/// slack plant carries the remainder. The result is ready to hand to the power
/// flow. Used by both the live CLI loop and the visualiser exporter.
/// </summary>
public static class GridScenario
{
    public static GridModel AtLoad(GridModel baseModel, double factor)
    {
        int slackBus = baseModel.Buses.First(b => b.Type == BusType.Slack).Id;
        var buses = baseModel.Buses.Select(b => b with { Pd = b.Pd * factor, Qd = b.Qd * factor }).ToList();
        var gens = baseModel.Generators.Select(g => g.Bus == slackBus ? g : g with { Pg = g.Pg * factor }).ToList();

        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        GridModel scaled = baseModel.CopyWith(buses: buses, generators: gens);
        return Dispatcher.Balance(scaled);
    }

    /// <summary>
    /// Returns a copy of <paramref name="baseModel"/> with each bus's initial voltage
    /// (<see cref="Bus.Vm"/>, <see cref="Bus.VaDeg"/>) set to the solved values from a previous
    /// power flow, mapped by bus id. Feeding this into the next solve warm-starts it from the last
    /// operating point (used by the consecutive-solve <c>chain</c> tests). Buses not present in the
    /// result keep their existing initial values. The model is otherwise unchanged (immutable copy).
    /// </summary>
    public static GridModel WithSolvedVoltages(GridModel baseModel, PowerFlowResult result)
    {
        var vm = new Dictionary<int, double>(result.BusIds.Count);
        var va = new Dictionary<int, double>(result.BusIds.Count);
        for (int i = 0; i < result.BusIds.Count; i++) { vm[result.BusIds[i]] = result.Vm[i]; va[result.BusIds[i]] = result.VaDeg[i]; }

        var buses = baseModel.Buses
            .Select(b => vm.TryGetValue(b.Id, out double v) ? b with { Vm = v, VaDeg = va[b.Id] } : b)
            .ToList();

        // N094 (October 2026): through GridModel.CopyWith, which carries the out-of-service generators (F16) and every
        // other model property. The hand-built copy kept only the in-service list, so a writer given this model lost
        // the out-of-service units. Engines read Generators, which is the same list as before, so no number changes.
        return baseModel.CopyWith(buses: buses);
    }
}
