using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.PowerElectronics;

namespace GridSim.Core.Dynamics.Emt.Hvdc;

/// <summary>Which sub-module representation an MMC arm uses.</summary>
public enum MmcStackKind
{
    /// <summary>Arm-average model: one equivalent arm capacitor <c>C_arm = C_sm / N</c> (arm sum-capacitor voltage as the state) and a continuous insertion index.</summary>
    ArmAverage,
    /// <summary>Sub-module level: <c>N</c> capacitor voltages, an integer insertion count (nearest level) and sorting-based voltage balancing.</summary>
    Detailed,
}

/// <summary>MMC data. One phase leg has two arms (upper from the positive DC node to the AC node, lower from the AC node to the
/// negative DC node), each <c>N</c> half-bridge sub-modules in series with an arm resistance and an arm inductance.</summary>
/// <param name="SubmodulesPerArm">N, at least 1.</param>
/// <param name="SubmoduleCapF">One sub-module capacitor, farads.</param>
/// <param name="ArmResistanceOhm">Arm resistance (conduction losses), ohms, not negative.</param>
/// <param name="ArmInductanceH">Arm inductance, henries, positive.</param>
/// <param name="NominalDcVolts">Pole-to-pole DC voltage; the sub-module capacitors start at <c>Vdc / N</c> each.</param>
public sealed record MmcParameters(int SubmodulesPerArm, double SubmoduleCapF, double ArmResistanceOhm, double ArmInductanceH, double NominalDcVolts)
{
    /// <summary>The equivalent arm capacitance <c>C_sm / N</c> (the arm sum-capacitor voltage <c>N v_sm</c> obeys <c>C_arm dV/dt = n i</c>).</summary>
    public double ArmCapacitanceF => SubmoduleCapF / SubmodulesPerArm;

    /// <summary>Check the data.</summary>
    public void Validate(string name)
    {
        if (SubmodulesPerArm < 1) throw new ArgumentException($"{name}: at least one sub-module per arm.");
        if (!(SubmoduleCapF > 0) || !double.IsFinite(SubmoduleCapF)) throw new ArgumentException($"{name}: the sub-module capacitance must be finite and positive.");
        if (!(ArmResistanceOhm >= 0) || !double.IsFinite(ArmResistanceOhm)) throw new ArgumentException($"{name}: the arm resistance must be finite and not negative.");
        if (!(ArmInductanceH > 0) || !double.IsFinite(ArmInductanceH)) throw new ArgumentException($"{name}: the arm inductance must be finite and positive.");
        if (!(NominalDcVolts > 0) || !double.IsFinite(NominalDcVolts)) throw new ArgumentException($"{name}: the nominal DC voltage must be finite and positive.");
    }
}

/// <summary>The sub-module stack of one arm: given an insertion index it reports the voltage drop it presents to the arm current,
/// and it integrates its capacitors with the arm current.</summary>
public interface IMmcSubmoduleBank
{
    /// <summary>Back to the initial charge.</summary>
    void Reset();
    /// <summary>The stack voltage at the END of the coming step for insertion index <paramref name="n"/> (0 to 1). <paramref name="iLast"/> is the arm current at the last accepted step (its sign picks the balancing order).</summary>
    double Emf(double n, double iLast, double dt);
    /// <summary>Accept the step: integrate the capacitors with the new arm current <paramref name="i"/>.</summary>
    void Accept(double n, double i, double dt);
    /// <summary>Sum of all sub-module capacitor voltages of the arm (the arm sum-capacitor voltage).</summary>
    double SumCapacitorVoltage { get; }
    /// <summary>Energy stored in the arm's capacitors, joules.</summary>
    double Energy { get; }
}

/// <summary>
/// The arm-average stack. State: the arm sum-capacitor voltage <c>V</c>; <c>C_arm dV/dt = n i</c>. The voltage seen by the arm is
/// <c>n V</c>. <c>V</c> at the end of the step is PREDICTED by a second-order Adams-Bashforth step from the last two derivatives and
/// then corrected with the trapezoidal rule once the step's current is known (the EMF is an explicit input to the linear solve, so
/// one step of lag in <c>V</c> would be first-order). Energy <c>W = C_arm V^2 / 2</c>.
/// </summary>
public sealed class AverageArmBank : IMmcSubmoduleBank
{
    private readonly double _c, _v0;
    private double _v, _f, _fOld;

    /// <summary>Create the stack.</summary>
    public AverageArmBank(double armCapacitanceF, double initialSumVoltage)
    {
        _c = armCapacitanceF; _v0 = initialSumVoltage; Reset();
    }

    /// <inheritdoc />
    public void Reset() { _v = _v0; _f = 0.0; _fOld = 0.0; }
    /// <inheritdoc />
    public double Emf(double n, double iLast, double dt) => n * (_v + dt * (1.5 * _f - 0.5 * _fOld));
    /// <inheritdoc />
    public void Accept(double n, double i, double dt)
    {
        double fNew = n * i / _c;
        _v += 0.5 * dt * (_f + fNew);
        _fOld = _f; _f = fNew;
    }
    /// <inheritdoc />
    public double SumCapacitorVoltage => _v;
    /// <inheritdoc />
    public double Energy => 0.5 * _c * _v * _v;
}

/// <summary>One MMC arm: a series R-L branch from node a to node b (current a to b) carrying a controlled voltage DROP from its
/// <see cref="IMmcSubmoduleBank"/>: <c>v_ab = R i + L di/dt + e_stack</c>. Trapezoidal companion (<see cref="EmtCompanionStamp.SeriesRl"/>);
/// the stack voltage is an explicit source term of the step, so the matrix is constant.</summary>
public sealed class MmcArm : NodalElement
{
    private readonly int _a, _b;
    private readonly double _r, _l;
    private readonly IMmcSubmoduleBank _stack;
    private readonly Func<double, double> _insertion;
    private Companion _comp;
    private double _dt, _vPrev, _iPrev, _hist, _e, _n;

    /// <summary>Create the arm. <paramref name="insertion"/> gives the insertion index (0 to 1) at the end of the step.</summary>
    public MmcArm(string name, int a, int b, double rOhm, double lHenry, IMmcSubmoduleBank stack, Func<double, double> insertion) : base(name)
    {
        if (a == b) throw new ArgumentException($"{name}: both terminals are the same node.");
        if (!(rOhm >= 0) || !(lHenry > 0)) throw new ArgumentException($"{name}: R must not be negative and L must be positive.");
        _a = a; _b = b; _r = rOhm; _l = lHenry;
        _stack = stack ?? throw new ArgumentNullException(nameof(stack));
        _insertion = insertion ?? throw new ArgumentNullException(nameof(insertion));
    }

    /// <summary>The sub-module stack.</summary>
    public IMmcSubmoduleBank Bank => _stack;
    /// <summary>The arm current at the last accepted step, amperes.</summary>
    public double Current => _iPrev;
    /// <summary>The insertion index used at the last accepted step.</summary>
    public double Insertion => _n;

    internal override void Configure(double dt, IntegrationRule rule) { _comp = EmtCompanionStamp.SeriesRl(_r, _l, dt, rule); _dt = dt; }
    internal override void Reset() { _vPrev = 0.0; _iPrev = 0.0; _hist = 0.0; _e = 0.0; _n = 0.0; _stack.Reset(); }
    internal override void StampMatrix(NodalStamper s) => s.AddConductance(_a, _b, _comp.Geq);

    internal override void StampSources(double t, NodalStamper s)
    {
        _n = Math.Clamp(_insertion(t), 0.0, 1.0);
        _e = _stack.Emf(_n, _iPrev, _dt);
        _hist = _comp.History(_vPrev, _iPrev);
        double h = _hist - _comp.Geq * _e;
        s.AddCurrentInto(_a, -h);
        s.AddCurrentInto(_b, h);
    }

    internal override void Accept(double t, double[] v)
    {
        double vRl = V(v, _a) - V(v, _b) - _e;
        _iPrev = _comp.Geq * vRl + _hist;
        _vPrev = vRl;
        _stack.Accept(_n, _iPrev, _dt);
    }

    internal override double ProbeCurrent(int channel) => _iPrev;

    /// <summary>State channels: 0 arm sum-capacitor voltage, 1 stored energy, 2 stack voltage (EMF) of the last step, 3 insertion index.</summary>
    internal override double ProbeStateAt(int channel) => channel switch
    {
        0 => _stack.SumCapacitorVoltage,
        1 => _stack.Energy,
        2 => _e,
        3 => _n,
        _ => double.NaN,
    };
}

/// <summary>
/// A three-phase modular multilevel converter wired into a <see cref="NodalNetwork"/>: for leg k an upper arm <c>name.u{k}</c> from
/// the positive DC node to the AC node and a lower arm <c>name.l{k}</c> from the AC node to the negative DC node. Insertion indices
/// (the average-value equations): <c>n_u = 1/2 - m/2 + z</c>, <c>n_l = 1/2 + m/2 + z</c>, with <c>m</c> the modulation signal (the pole
/// voltage is <c>m V_C / 2</c>, V_C the arm sum-capacitor voltage) and <c>z</c> a common-mode term (zero unless a
/// <see cref="MmcCirculatingCurrentControl"/> sets it). The sum of the arm voltages is then <c>V_C (1 + 2 z)</c>, which is what drives the
/// circulating current. Model class: <see cref="ConverterModelClass.AverageValue"/> (no valve-level switching ripple); the
/// sub-module-level form adds capacitor voltages and balancing but still no IGBT switching.
/// </summary>
public sealed class MmcConverter : IConverterModel
{
    private readonly double[] _z = new double[3];

    private MmcConverter(string name, MmcParameters p, MmcStackKind kind) { Name = name; Parameters = p; Kind = kind; }

    /// <inheritdoc />
    public string Name { get; }
    /// <summary>The data.</summary>
    public MmcParameters Parameters { get; }
    /// <summary>The stack representation.</summary>
    public MmcStackKind Kind { get; }
    /// <inheritdoc />
    public ConverterModelClass ModelClass => ConverterModelClass.AverageValue;
    /// <inheritdoc />
    public string Description => Kind == MmcStackKind.ArmAverage
        ? $"modular multilevel converter, arm-average model ({Parameters.SubmodulesPerArm} sub-modules per arm lumped into C_arm = C_sm/N, continuous insertion, no switching ripple)"
        : $"modular multilevel converter, sub-module level ({Parameters.SubmodulesPerArm} half-bridge sub-modules per arm, nearest-level insertion, sorted voltage balancing, no IGBT switching ripple)";

    /// <summary>The arms in order u0, l0, u1, l1, u2, l2.</summary>
    public IReadOnlyList<MmcArm> Arms { get; private set; } = [];

    /// <summary>The upper arm of leg <paramref name="leg"/>.</summary>
    public MmcArm Upper(int leg) => Arms[2 * leg];
    /// <summary>The lower arm of leg <paramref name="leg"/>.</summary>
    public MmcArm Lower(int leg) => Arms[2 * leg + 1];

    /// <summary>The common-mode insertion term of leg <paramref name="leg"/> (set by a circulating-current control).</summary>
    public double CommonMode(int leg) => _z[leg];
    internal void SetCommonMode(int leg, double z) => _z[leg] = z;

    /// <summary>The circulating current of a leg, <c>(i_u + i_l)/2</c>, at the last accepted step (it contains the DC share <c>Idc/3</c>).</summary>
    public double LegCirculatingCurrent(int leg) => 0.5 * (Upper(leg).Current + Lower(leg).Current);

    /// <summary>Add the converter. <paramref name="modulation"/> is called with (time, leg) and gives m in -1..1.</summary>
    public static MmcConverter Add(NodalNetwork net, string name, int dcPositive, int dcNegative, int[] ac, MmcParameters p, MmcStackKind kind,
        Func<double, int, double> modulation)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(modulation);
        p.Validate(name);
        if (ac is null || ac.Length != 3 || ac.Any(a => a < 0)) throw new ArgumentException($"{name}: three non-ground AC nodes are needed.");
        var c = new MmcConverter(name, p, kind);
        var arms = new List<MmcArm>();
        double vsm = p.NominalDcVolts / p.SubmodulesPerArm;
        for (int k = 0; k < 3; k++)
        {
            int leg = k;
            IMmcSubmoduleBank Stack() => kind == MmcStackKind.ArmAverage
                ? new AverageArmBank(p.ArmCapacitanceF, p.NominalDcVolts)
                : new DetailedArmBank(p.SubmodulesPerArm, p.SubmoduleCapF, vsm);
            arms.Add(net.Add(new MmcArm($"{name}.u{k}", dcPositive, ac[k], p.ArmResistanceOhm, p.ArmInductanceH, Stack(),
                t => 0.5 - 0.5 * modulation(t, leg) + c._z[leg])));
            arms.Add(net.Add(new MmcArm($"{name}.l{k}", ac[k], dcNegative, p.ArmResistanceOhm, p.ArmInductanceH, Stack(),
                t => 0.5 + 0.5 * modulation(t, leg) + c._z[leg])));
        }
        c.Arms = arms;
        return c;
    }
}
