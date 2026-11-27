using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

/// <summary>
/// A split DC link: two equal capacitors, positive node to the mid-point (ground) and mid-point to the negative node, each of twice
/// the stated link capacitance so that the series pair is <c>CdcF</c>, charged to half the link voltage each, with an optional
/// Thevenin supply (+Vsup/2 at the positive node and -Vsup/2 at the negative, each behind half the stated supply resistance).
/// With zero supply resistance the supply is ideal and the DC nodes are imposed.
/// </summary>
public sealed class DcLink
{
    private DcLink(string name, int p, int n) { Name = name; Positive = p; Negative = n; }

    /// <summary>The link name.</summary>
    public string Name { get; }
    /// <summary>Node id of the positive rail.</summary>
    public int Positive { get; }
    /// <summary>Node id of the negative rail.</summary>
    public int Negative { get; }
    /// <summary>The positive capacitor's element name.</summary>
    public string PositiveCapacitor => Name + ".cp";
    /// <summary>The negative capacitor's element name.</summary>
    public string NegativeCapacitor => Name + ".cn";

    /// <summary>Add the link. Nodes are <c>name.p</c> and <c>name.n</c>. A zero <paramref name="supplyVolts"/> means no supply.</summary>
    public static DcLink Add(NodalNetwork net, string name, double capacitanceF, double initialVolts, double supplyVolts, double supplyOhm)
    {
        ArgumentNullException.ThrowIfNull(net);
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (!(capacitanceF > 0) || !double.IsFinite(capacitanceF)) throw Refuse($"{name}: the DC link capacitance must be finite and positive.");
        if (!double.IsFinite(initialVolts) || !double.IsFinite(supplyVolts) || !(supplyOhm >= 0) || !double.IsFinite(supplyOhm))
            throw Refuse($"{name}: voltages must be finite and the supply resistance not negative.");
        int p = net.Node(name + ".p"), n = net.Node(name + ".n");
        net.Add(NodalBranch.Capacitor(name + ".cp", p, NodalNetwork.Ground, 2.0 * capacitanceF, 0.5 * initialVolts));
        net.Add(NodalBranch.Capacitor(name + ".cn", NodalNetwork.Ground, n, 2.0 * capacitanceF, 0.5 * initialVolts));
        if (supplyVolts != 0.0)
        {
            net.Add(new NodalVoltageSource(name + ".sp", p, 0.5 * supplyOhm, _ => 0.5 * supplyVolts));
            net.Add(new NodalVoltageSource(name + ".sn", n, 0.5 * supplyOhm, _ => -0.5 * supplyVolts));
        }
        return new DcLink(name, p, n);
    }
}

/// <summary>The result of a power-electronics run: the solver result plus the model class of every converter and the stated switching
/// timing, so a trace is never read without knowing which model produced it.</summary>
/// <param name="Run">The nodal solver result (status, traces, statistics).</param>
/// <param name="Converters">Name, model class and description of every converter in the run.</param>
/// <param name="SwitchingTiming">The switching-instant contract: how and with what error switching events are placed on the step grid.</param>
public sealed record PowerElectronicsResult(NodalResult Run, IReadOnlyList<(string Name, ConverterModelClass Class, string Description)> Converters, string SwitchingTiming)
{
    /// <summary>True when any converter is a switched model.</summary>
    public bool HasSwitchedConverter => Converters.Any(c => c.Class == ConverterModelClass.Switched);
}

/// <summary>Runs a network that holds converters and reports their model class and the switching-timing contract.</summary>
public static class PowerElectronicsRunner
{
    /// <summary>Run <paramref name="net"/> on the nodal solver and name the converter models in the result.</summary>
    public static PowerElectronicsResult Run(NodalNetwork net, double dt, double duration, IReadOnlyList<NodalProbe> probes,
        IReadOnlyList<IConverterModel> converters, IReadOnlyList<NodalEvent>? events = null, NodalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(converters);
        NodalResult run = NodalSolver.Run(net, dt, duration, probes, events, options);
        string timing = "Switching instants (gate edges, diode and thyristor turn-on, current-zero turn-off, scheduled events) are quantised UP to the "
            + FormattableString.Invariant($"{dt:R} s step grid and are never interpolated: the timing error is at most one step ({dt:R} s) and a current-zero turn-off may overshoot by at most ")
            + "one step of current. Every switching decision uses the last accepted state, so the run is bit-identical on every machine and thread count."
            + (options is { EventRule: NodalEventRule.BackwardEulerStep } ? " Each switching step is one backward-Euler step." : "");
        return new PowerElectronicsResult(run, converters.Select(c => (c.Name, c.ModelClass, c.Description)).ToArray(), timing);
    }
}
