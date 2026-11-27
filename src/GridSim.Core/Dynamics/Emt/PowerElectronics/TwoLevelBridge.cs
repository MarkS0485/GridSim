using GridSim.Core.Dynamics.Emt.Nodal;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

/// <summary>Valve data shared by every device of a bridge.</summary>
/// <param name="RonOhm">On resistance, ohms (conduction loss is Ron i squared).</param>
/// <param name="RoffOhm">Off resistance, ohms (leakage).</param>
/// <param name="SnubberOhm">Series resistance of an optional R-C snubber across each valve (0 with <paramref name="SnubberF"/> 0 = none).</param>
/// <param name="SnubberF">Snubber capacitance, farads (0 = none).</param>
public sealed record ValveParameters(double RonOhm = 1e-3, double RoffOhm = 1e7, double SnubberOhm = 0.0, double SnubberF = 0.0);

/// <summary>
/// A three-phase two-level bridge (six valves) wired into a <see cref="NodalNetwork"/>: three legs from the DC positive node to the
/// DC negative node, the AC terminal of leg k between its upper and lower valve.
/// <list type="bullet">
/// <item><b>Diode and Thyristor</b> bridges are in the rectifying direction: the upper valve of leg k conducts from the AC node to
/// the DC positive node, the lower from the DC negative node to the AC node. Valve names are <c>name.up0..2</c> and <c>name.lo0..2</c>;
/// gate(t, leg, upper) is the thyristor gate.</item>
/// <item>A <b>GatedDiode</b> bridge is a voltage-source converter: the upper device conducts forward from the DC positive node to the
/// AC node (the antiparallel diode backward), the lower from the AC node to the DC negative node. The gate for the upper device
/// is the PWM state of the leg and the lower device is its complement, both changing in the same step (so a shoot-through is not
/// possible); no dead time is modelled.</item>
/// </list>
/// The model class of the converter is <see cref="ConverterModelClass.Switched"/>; every valve change is quantised up to the step
/// grid (see <see cref="NodalValve"/>).
/// </summary>
public sealed class TwoLevelBridge : IConverterModel
{
    private readonly List<NodalValve> _valves = new();

    private TwoLevelBridge(string name) { Name = name; }

    /// <summary>The bridge name.</summary>
    public string Name { get; }

    /// <summary>The model class (always switched).</summary>
    public ConverterModelClass ModelClass => ConverterModelClass.Switched;

    /// <summary>Human-readable model description, named in the result.</summary>
    public string Description { get; private set; } = "";

    /// <summary>The valves in creation order: up0, lo0, up1, lo1, up2, lo2.</summary>
    public IReadOnlyList<NodalValve> Valves => _valves;

    /// <summary>The name of the upper valve of leg <paramref name="leg"/>.</summary>
    public string Upper(int leg) => $"{Name}.up{leg}";
    /// <summary>The name of the lower valve of leg <paramref name="leg"/>.</summary>
    public string Lower(int leg) => $"{Name}.lo{leg}";

    /// <summary>Add a bridge. <paramref name="gate"/> is called with (time, leg 0..2, upper) and is required for a thyristor or gated bridge.</summary>
    public static TwoLevelBridge Add(NodalNetwork net, string name, int dcPositive, int dcNegative, int[] ac, ValveKind kind, ValveParameters valve,
        Func<double, int, bool, bool>? gate, string description)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(ac);
        ArgumentNullException.ThrowIfNull(valve);
        if (ac.Length != 3) throw new ArgumentException($"{name}: a bridge has three AC terminals.", nameof(ac));
        if (kind != ValveKind.Diode && gate is null) throw new ArgumentException($"{name}: a {kind} bridge needs a gate function.", nameof(gate));
        if (valve.SnubberF < 0 || valve.SnubberOhm < 0 || (valve.SnubberF > 0 && !(valve.SnubberOhm > 0)))
            throw new ArgumentException($"{name}: a snubber needs a positive resistance and a non-negative capacitance.", nameof(valve));
        var b = new TwoLevelBridge(name) { Description = description };
        void Snub(string vname, int x, int y)
        {
            if (valve.SnubberF > 0) net.Add(NodalBranch.SeriesRc($"{vname}.snub", x, y, valve.SnubberOhm, valve.SnubberF));
        }
        for (int k = 0; k < 3; k++)
        {
            int leg = k;
            // anode/cathode: rectifier orientation for diode and thyristor, inverter orientation for the gated diode.
            (int ua, int uc, int la, int lc) = kind == ValveKind.GatedDiode
                ? (dcPositive, ac[k], ac[k], dcNegative)
                : (ac[k], dcPositive, dcNegative, ac[k]);
            Func<double, bool>? gu = gate is null ? null : t => gate(t, leg, true);
            Func<double, bool>? gl = gate is null ? null : t => gate(t, leg, false);
            var up = net.Add(new NodalValve(b.Upper(k), ua, uc, kind, valve.RonOhm, valve.RoffOhm, gu));
            var lo = net.Add(new NodalValve(b.Lower(k), la, lc, kind, valve.RonOhm, valve.RoffOhm, gl));
            b._valves.Add(up); b._valves.Add(lo);
            Snub(b.Upper(k), ua, uc);
            Snub(b.Lower(k), la, lc);
        }
        return b;
    }

    /// <summary>A voltage-source-converter bridge gated by a sine-PWM modulator (upper on while the reference exceeds the carrier).</summary>
    public static TwoLevelBridge AddVsc(NodalNetwork net, string name, int dcPositive, int dcNegative, int[] ac, ValveParameters valve, SinePwm pwm)
    {
        ArgumentNullException.ThrowIfNull(pwm);
        return Add(net, name, dcPositive, dcNegative, ac, ValveKind.GatedDiode, valve,
            (t, leg, upper) => upper ? pwm.UpperOn(t, leg) : !pwm.UpperOn(t, leg),
            FormattableString.Invariant($"two-level voltage-source converter, switched valves (gated diodes), sine PWM carrier {pwm.CarrierHz:R} Hz, carrier phase {pwm.CarrierPhaseFraction:R} of a period, no dead time"));
    }
}

/// <summary>Firing pulses for a thyristor bridge fed by a balanced three-phase source <c>v_k = Vm cos(w t + phi0 - 120 deg k)</c>.</summary>
public static class ThyristorFiring
{
    /// <summary>
    /// The gate function of a six-pulse thyristor bridge in the rectifying direction (<see cref="TwoLevelBridge"/> with
    /// <see cref="ValveKind.Thyristor"/>). The natural commutation instant (alpha = 0) of the upper valve of leg k is where its phase
    /// first exceeds the one before it: electrical angle <c>x = w t + phi0 = -60 deg + 120 deg k</c>; the lower valve of leg k is
    /// 180 degrees later. The valve is gated from <c>alpha</c> after that instant for <paramref name="pulseWidthRad"/> radians
    /// (a gate that outlasts the valve's own conduction does not refire it, because the valve is only on while the anode is positive).
    /// </summary>
    public static Func<double, int, bool, bool> Bridge(double frequencyHz, double phi0Rad, double alphaRad, double pulseWidthRad = 0.5236)
    {
        double w = 2.0 * Math.PI * frequencyHz, twoPi = 2.0 * Math.PI;
        return (t, leg, upper) =>
        {
            double start = -Math.PI / 3.0 + leg * (twoPi / 3.0) + (upper ? 0.0 : Math.PI) + alphaRad;
            double u = (w * t + phi0Rad - start) / twoPi;
            u -= Math.Floor(u);
            return u * twoPi < pulseWidthRad;
        };
    }
}
