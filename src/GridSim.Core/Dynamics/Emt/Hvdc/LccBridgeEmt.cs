using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.Hvdc;

/// <summary>An EMF with a series resistance between two NODES (neither needs to be ground), stamped as its Norton equivalent: a conductance
/// <c>1/Rs</c> between a and b and the current <c>e(t)/Rs</c> injected into a and drawn from b. Used for a galvanically isolated (floating-neutral)
/// source such as a converter transformer secondary. <c>Rs</c> must be positive.</summary>
public sealed class NodalFloatingVoltageSource : NodalElement
{
    private readonly int _a, _b;
    private readonly double _rs;
    private readonly Func<double, double> _emf;
    private double _eNow, _vab;

    /// <summary>Create the source (EMF positive at a with respect to b).</summary>
    public NodalFloatingVoltageSource(string name, int a, int b, double rs, Func<double, double> emf) : base(name)
    {
        if (a == b) throw new ArgumentException($"{name}: both terminals are the same node.");
        if (!(rs > 0) || !double.IsFinite(rs)) throw new ArgumentException($"{name}: the series resistance must be finite and positive.");
        _a = a; _b = b; _rs = rs; _emf = emf ?? throw new ArgumentNullException(nameof(emf));
    }

    internal override void Configure(double dt, IntegrationRule rule) { }
    internal override void Reset() { _eNow = 0.0; _vab = 0.0; }
    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_a, _b, 1.0 / _rs);
    internal override void StampSources(double t, NodalStamper s)
    {
        _eNow = _emf(t);
        s.AddCurrentInto(_a, _eNow / _rs);
        s.AddCurrentInto(_b, -_eNow / _rs);
    }
    internal override void Accept(double t, double[] v) => _vab = V(v, _a) - V(v, _b);
    /// <summary>The current the source delivers into node a (and takes from b), amperes.</summary>
    internal override double ProbeCurrent(int channel) => (_eNow - _vab) / _rs;
}

/// <summary>Data of a line-commutated converter (LCC) bridge group: the converter transformer is represented by its secondary
/// sources behind their leakage reactance (an ideal ratio transformer is a scaling, so only the secondary matters).</summary>
public sealed record LccBridgeSpec
{
    /// <summary>Secondary line-to-line RMS voltage, volts.</summary>
    public double LineToLineRmsVolts { get; init; }
    /// <summary>Frequency, hertz.</summary>
    public double FrequencyHz { get; init; } = 50.0;
    /// <summary>Per-phase leakage (commutation) inductance, henries.</summary>
    public double CommutationLHenry { get; init; }
    /// <summary>Per-phase source resistance, ohms.</summary>
    public double SourceROhm { get; init; } = 1e-3;
    /// <summary>Six or twelve pulses. Twelve is two six-pulse bridges in series on the DC side fed 30 degrees apart (a star and a delta secondary).</summary>
    public int Pulses { get; init; } = 6;
    /// <summary>Valve data. No snubber by default: with a floating DC side the snubber capacitors couple the DC nodes to the AC nodes and let the common-mode
    /// potential swing until a valve conducts, so give the DC side a solid reference before adding one.</summary>
    public ValveParameters Valve { get; init; } = new(1e-3, 1e7);
    /// <summary>Phase of the first source: <c>v_k = Vm cos(w t + phi0 - 120 deg k)</c>. The default -90 degrees starts the sources at zero volts for phase a.</summary>
    public double Phi0Rad { get; init; } = -Math.PI / 2.0;
    /// <summary>Resistance tying each floating transformer neutral to ground (a numerical reference only), ohms.</summary>
    public double NeutralReferenceOhm { get; init; } = 1e6;
    /// <summary>Gate pulse width, radians.</summary>
    public double PulseWidthRad { get; init; } = 0.5236;
}

/// <summary>
/// A six- or twelve-pulse LCC bridge group on the nodal solver (built from the R4-17 thyristor <see cref="TwoLevelBridge"/>): ideal
/// secondary sources behind <c>R_s + L_c</c>, thyristor valves with optional R-C snubbers, and a firing delay <c>alpha(t)</c> read at every
/// step (an equidistant pulse scheme on a stated reference angle: no PLL). Closed forms (the tests derive them independently):
/// <c>V_d0 = (3 / pi) sqrt(2) V_LL</c> per six-pulse bridge, <c>V_d = V_d0 cos(alpha) - (3 w L_c / pi) I_d - 2 (R_on + R_s) I_d</c>, and for
/// twelve pulses twice that with the same current; the overlap angle follows <c>cos(alpha) - cos(alpha + mu) = 2 w L_c I_d / (sqrt(2) V_LL)</c>.
/// Model class: <see cref="ConverterModelClass.Switched"/> (valve level, with the one-step switching quantisation of R4-17).
/// </summary>
public sealed class LccBridgeEmt : IConverterModel
{
    private readonly List<TwoLevelBridge> _bridges = new();

    private LccBridgeEmt(string name, LccBridgeSpec spec) { Name = name; Spec = spec; }

    /// <inheritdoc />
    public string Name { get; }
    /// <summary>The data.</summary>
    public LccBridgeSpec Spec { get; }
    /// <inheritdoc />
    public ConverterModelClass ModelClass => ConverterModelClass.Switched;
    /// <inheritdoc />
    public string Description => $"line-commutated converter, {Spec.Pulses}-pulse thyristor bridge group (valve level, secondary sources behind leakage reactance, firing delay from a stated reference angle, no PLL)";
    /// <summary>The six-pulse bridges (one or two).</summary>
    public IReadOnlyList<TwoLevelBridge> Bridges => _bridges;
    /// <summary>The 12-pulse mid-point node id (-1 for six pulses).</summary>
    public int MidNode { get; private set; } = -1;

    /// <summary>The DC open-circuit voltage of the group at zero firing delay, volts: <c>Pulses/6 * (3 sqrt(2) / pi) V_LL</c>.</summary>
    public static double OpenCircuitDcVolts(LccBridgeSpec s) => (s.Pulses / 6) * 3.0 * Math.Sqrt(2.0) / Math.PI * s.LineToLineRmsVolts;

    /// <summary>The equivalent commutation resistance of the group, ohms: <c>(Pulses/6) 3 w L_c / pi</c>.</summary>
    public static double CommutationResistanceOhm(LccBridgeSpec s) => (s.Pulses / 6) * 3.0 * 2.0 * Math.PI * s.FrequencyHz * s.CommutationLHenry / Math.PI;

    /// <summary>The valve and source resistance in the DC path, ohms: <c>(Pulses/6) 2 (R_on + R_s)</c> (two phases conduct in series).</summary>
    public static double ConductionResistanceOhm(LccBridgeSpec s) => (s.Pulses / 6) * 2.0 * (s.Valve.RonOhm + s.SourceROhm);

    /// <summary>Add the group between <paramref name="dcPositive"/> and <paramref name="dcNegative"/> (either may be ground, not both).
    /// <paramref name="alpha"/> gives the firing delay in radians at time t. Nodes and elements are named <c>prefix + name...</c>.</summary>
    public static LccBridgeEmt Add(NodalNetwork net, string name, string nodePrefix, int dcPositive, int dcNegative, LccBridgeSpec s, Func<double, double> alpha)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(alpha);
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (s.Pulses is not (6 or 12)) throw Refuse($"{name}: a bridge group has 6 or 12 pulses.");
        if (!(s.LineToLineRmsVolts > 0) || !(s.FrequencyHz > 0) || !(s.CommutationLHenry > 0) || !(s.SourceROhm >= 0))
            throw Refuse($"{name}: voltage, frequency and commutation inductance must be positive and the source resistance not negative.");
        if (dcPositive == dcNegative) throw Refuse($"{name}: the DC terminals must differ.");
        var g = new LccBridgeEmt(name, s);
        double w = 2.0 * Math.PI * s.FrequencyHz, vm = s.LineToLineRmsVolts * Math.Sqrt(2.0) / Math.Sqrt(3.0), twoPi = 2.0 * Math.PI;
        int bridges = s.Pulses / 6;
        int mid = bridges == 2 ? net.Node(nodePrefix + name + ".mid") : -1;
        g.MidNode = mid;
        for (int b = 0; b < bridges; b++)
        {
            double phi0 = s.Phi0Rad - b * (Math.PI / 6.0);
            int p = bridges == 1 ? dcPositive : (b == 0 ? dcPositive : mid);
            int n = bridges == 1 ? dcNegative : (b == 0 ? mid : dcNegative);
            var ac = new int[3];
            // The converter transformer secondary is galvanically isolated, so its neutral FLOATS: a grounded neutral would clamp the lower
            // valve group to ground and break the six-pulse relations. The neutral is tied to ground only by a large resistor (a reference).
            int nn = net.Node($"{nodePrefix}{name}.b{b}.n");
            net.Add(NodalBranch.Resistor($"{name}.b{b}.nref", nn, NodalNetwork.Ground, s.NeutralReferenceOhm));
            for (int k = 0; k < 3; k++)
            {
                int gen = net.Node($"{nodePrefix}{name}.b{b}.gen{k}");
                ac[k] = net.Node($"{nodePrefix}{name}.b{b}.ac{k}");
                double ph = phi0 - k * (twoPi / 3.0);
                net.Add(new NodalFloatingVoltageSource($"{name}.b{b}.e{k}", gen, nn, s.SourceROhm, t => vm * DetMath.Cos(w * t + ph)));
                net.Add(NodalBranch.SeriesRl($"{name}.b{b}.l{k}", gen, ac[k], 0.0, s.CommutationLHenry));
            }
            double ph0 = phi0, pw = s.PulseWidthRad;
            Func<double, int, bool, bool> gate = (t, leg, upper) =>
            {
                double start = -Math.PI / 3.0 + leg * (twoPi / 3.0) + (upper ? 0.0 : Math.PI) + alpha(t);
                double u = (w * t + ph0 - start) / twoPi;
                u -= Math.Floor(u);
                return u * twoPi < pw;
            };
            g._bridges.Add(TwoLevelBridge.Add(net, $"{name}.b{b}", p, n, ac, ValveKind.Thyristor, s.Valve, gate, g.Description));
        }
        return g;
    }
}
