using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The ONE rule for a voltage-controlled bus's setpoint when several generators share the bus (R037, October
/// 2026): <b>the last generator listed at the bus sets the voltage</b>.
/// <para>Why "last": it is what MATPOWER does. <c>runpf</c> seeds the controlled voltages with
/// <c>V0(gbus) = gen(on, VG)</c>, a MATLAB indexed assignment in which a bus index that repeats keeps the LAST
/// value written - so a GridSim solve cross-validated against MATPOWER must use the same rule, and the dense and
/// sparse Newton engines (<see cref="NewtonShell.BuildBusSpec"/>, which overwrites the per-bus value generator by
/// generator) always have. Before this the backward/forward sweep took the HIGHEST Vg at a PV bus and the FIRST
/// generator's Vg at the slack, so the three engines held the same model at three different voltages.</para>
/// <para>A unit that should not regulate (a storage site, for instance) must therefore carry the bus's existing
/// setpoint rather than a placeholder; <see cref="Storage.StorageFleet.Apply"/> does exactly that.</para>
/// </summary>
public static class VoltageSetpoint
{
    /// <summary>The voltage setpoint (pu) for bus <paramref name="busId"/>: the Vg of the last generator in
    /// <paramref name="model"/>'s list that sits on the bus, or null when no generator does.</summary>
    public static double? ForBus(GridModel model, int busId)
    {
        double? vg = null;
        foreach (Generator g in model.Generators)
            if (g.Bus == busId) vg = g.Vg;   // later units overwrite earlier ones: last wins
        return vg;
    }
}
