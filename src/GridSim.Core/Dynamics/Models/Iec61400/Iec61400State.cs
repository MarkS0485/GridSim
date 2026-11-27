using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Iec61400;

/// <summary>
/// RC5 WP23: one IEC 61400-27-1 style Type 4A or 4B turbine (<see cref="Iec61400Data"/>) as the RMS kernel drives it: a current
/// source at its terminal, <c>I = (ip + j iq) e^(j theta_pll)</c> times the unit rating (system pu), with ip and iq from the
/// generator system and theta from the phase-locked loop. Built from public sources (Dynawo MPL-2.0, read for structure only, and
/// the CGMES 2.4.15 DY names); not checked against the IEC 61400-27-1 text.
///
/// <para>Each kernel step (<see cref="Step"/>, from the start-of-step terminal voltage and the unit's own terminal power at the
/// last commit) runs, in order: the measurement filters; the protection (a trip zeroes the current from the next solve on); the
/// PLL; the reactive control (and the fault state); the current limiter, from the present power order and the new reactive
/// command; the active control; the drive train (Type 4B); the generator system. A whole run uses one step size (refused
/// otherwise), because the fault and protection timers count whole steps.</para>
///
/// <para>Initialisation reads only the solved power-flow point of the unit (voltage, P, Q): ip0 = P0/U0, iq0 = -Q0/U0, every
/// filter at its input, the references at the initial values. Nothing is taken from the first step, so a disturbance at t = 0
/// does not change the start. A start outside a limit, inside the fault band or beyond a protection threshold is refused.</para>
/// </summary>
internal sealed class Iec61400State : IRmsConverterModel, IRmsDeviceStateRecorder
{
    /// <summary>Below this terminal voltage (pu) the instant PLL keeps its last angle (the angle of a collapsed terminal is
    /// meaningless); the same floor as the historical converter's.</summary>
    internal const double AngleLockMinVoltagePu = 1e-3;

    private static readonly string[] Names =
    {
        "ip", "iq", "pRef", "pOrder", "pAero", "omegaWtr", "omegaGen", "twist", "qIntegral", "vIntegral", "qOrder", "iqOpenLoop",
        "uss", "uFilt", "pFilt", "qFilt", "thetaPllFilt", "frtState", "uProt", "dfProt", "tripped",
    };

    private readonly RmsConverterUnit _unit;
    private readonly Iec61400Data _d;
    private readonly double _rating;
    private readonly double _nominalHz;
    private readonly Iec61400GenSystemModule _gen;
    private readonly Iec61400PControlModule _pc;
    private readonly Iec61400QControlModule _qc;
    private readonly Iec61400MechanicalModule? _mech;
    private readonly Iec61400PllModule _pll;
    private readonly Iec61400ProtectionModule? _prot;
    private double _uf, _pf, _qf;
    private double _lastAngle;          // the angle of the last committed current
    private double _prevTerminalAngle;  // the terminal angle at the previous step (unwrapping; derivative with tffilt = 0)
    private double _frequencyInput;     // the protection's frequency derivative, lagged (Dynawo Derivative, T = tffilt / 20)
    private Complex _lastCurrent;       // the last committed injection (system pu)
    private double _dt = double.NaN;
    private readonly List<double[]> _samples = new();

    internal Iec61400State(RmsConverterUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _unit = unit;
        _d = unit.Iec61400 ?? throw new ArgumentException("The unit carries no IEC 61400 model.", nameof(unit));
        Iec61400Data.Validate(_d);
        if (!(unit.RatingPu > 0.0) || !double.IsFinite(unit.RatingPu))
            throw new ArgumentException($"{unit.Name}: an IEC 61400 unit needs a positive rating (its MVA base).", nameof(unit));
        _rating = unit.RatingPu;
        _nominalHz = unit.Parameters.NominalHz;
        double u0 = unit.V0.Magnitude;
        if (!(u0 > 0.01))
            throw new ArgumentException($"{unit.Name}: the initial terminal voltage {Num(u0)} pu is too low to start an IEC 61400 unit.", nameof(unit));
        double angle0 = DetMath.Phase(unit.V0);
        double p0 = unit.P0 / _rating, q0 = unit.Q0 / _rating;   // unit pu, generating / exporting positive
        double ip0 = p0 / u0, iq0 = -q0 / u0;                    // IEC frame: Q = -U iq

        _uf = u0; _pf = p0; _qf = q0;
        _lastAngle = angle0; _prevTerminalAngle = angle0;
        _lastCurrent = Current(ip0, iq0, angle0);
        _pll = new Iec61400PllModule(_d.Pll, u0, angle0);
        _qc = new Iec61400QControlModule(_d.QControl, u0, p0, q0);
        _pc = new Iec61400PControlModule(_d.PControl, _d.Type == Iec61400TurbineType.Type4B, p0);
        _mech = _d.Type == Iec61400TurbineType.Type4B ? new Iec61400MechanicalModule(_d.Mechanical!, p0) : null;
        _gen = new Iec61400GenSystemModule(_d.Gen, ip0, iq0);
        _prot = _d.Protection is { } pr ? new Iec61400ProtectionModule(pr, u0) : null;

        // The limits at the start must hold the initial currents (an exact equilibrium, as every standard model, R252).
        (double ipMax0, double iqMin0, double iqMax0) = Iec61400CurrentLimiterModule.Limits(_d.CurrentLimit, u0, 1.0, 0,
            Iec61400PControlModule.Command(p0, u0), iq0);
        ControlBlocks.RequireEquilibriumWithinLimits("IEC 61400", "active current ip", ip0, double.NegativeInfinity, ipMax0, nameof(unit));
        ControlBlocks.RequireEquilibriumWithinLimits("IEC 61400", "reactive current iq", iq0, iqMin0, iqMax0, nameof(unit));
        ControlBlocks.RequireEquilibriumWithinLimits("IEC 61400", "power order", p0, double.NegativeInfinity, ipMax0 * u0, nameof(unit));
    }

    /// <summary>The unit's data.</summary>
    internal Iec61400Data Data => _d;

    /// <summary>Why the protection tripped the unit, or null while connected.</summary>
    internal string? TripReason => _prot?.Trip;

    /// <summary>The protection's frequency input after the last step: the terminal angle's derivative through the lag of
    /// tffilt / 20, per unit of nominal frequency (0 without protection).</summary>
    internal double ProtectionFrequencyInput => _frequencyInput;

    /// <summary>The fault state of the reactive control: 0 normal, 1 fault, 2 post-fault.</summary>
    internal int FrtState => _qc.FrtState;

    /// <summary>Every state at each commit (one row per kernel sample), names in <see cref="Names"/> order.</summary>
    public RmsDeviceTrace? Trace => new(_unit.Name, _d.Description, Names, _samples);

    public double ActiveCurrent { get; private set; }

    public double ReactiveCurrent { get; private set; }

    public bool InDip => _qc.FrtState != 0;

    public Complex TrialCurrent(Complex v)
    {
        if (_prot?.Trip is not null) return Complex.Zero;
        return Current(_gen.Ip, _gen.Iq, Angle(v));
    }

    public Complex Commit(Complex v)
    {
        if (_prot?.Trip is not null)
        {
            ActiveCurrent = 0.0; ReactiveCurrent = 0.0; _lastCurrent = Complex.Zero;
            Record();
            return Complex.Zero;
        }
        _lastAngle = Angle(v);
        _lastCurrent = Current(_gen.Ip, _gen.Iq, _lastAngle);
        ActiveCurrent = _gen.Ip * _rating;
        ReactiveCurrent = -_gen.Iq * _rating;
        Record();
        return _lastCurrent;
    }

    public Complex TerminalCurrent(Complex injection, Complex v) => injection;

    public void Step(double t, Complex v, Complex[] nodeVoltages, double dt)
    {
        if (double.IsNaN(_dt))
        {
            _dt = dt;
            if (_mech is not null)
            {
                // The symplectic Euler step of the drive train is stable for dt wn < 2 and its explicit damping for
                // dt c < 2 (c the damping rate of the relative speed); refuse a step that breaks either.
                if (!(dt * _mech.NaturalFrequency < 2.0) || !(dt * _mech.DampingRate < 2.0))
                    throw new InvalidOperationException($"{_unit.Name}: the IEC 61400 drive train needs dt wn < 2 and dt c < 2 (wn = {Num(_mech.NaturalFrequency)} rad/s, "
                        + $"c = {Num(_mech.DampingRate)} 1/s); the step {Num(dt)} s is too long.");
            }
        }
        else if (dt != _dt)
            throw new InvalidOperationException($"{_unit.Name}: an IEC 61400 unit runs at one step size (its timers count whole steps); the step changed from {Num(_dt)} s to {Num(dt)} s.");
        if (_prot?.Trip is not null) return;

        double u = v.Magnitude;
        double angle = u > AngleLockMinVoltagePu ? Iec61400Blocks.Unwrap(DetMath.Phase(v), _prevTerminalAngle) : _prevTerminalAngle;
        Complex s = v * Complex.Conjugate(_lastCurrent) / _rating;   // own terminal power, unit pu, generating positive
        double pg = s.Real, qg = s.Imaginary;

        Iec61400Measurement me = _d.Measurement;
        _uf = Iec61400Blocks.Lag(_uf, u, me.Tufilt, dt);
        _pf = Iec61400Blocks.Lag(_pf, pg, me.Tpfilt, dt);
        _qf = Iec61400Blocks.Lag(_qf, qg, me.Tqfilt, dt);

        if (_prot is not null)
        {
            // The frequency measurement differentiates the terminal angle through Dynawo's Derivative block (k = 1,
            // T = tffilt / 20), whose transfer function s / (1 + s T) is the derivative followed by a lag of T. Here: the
            // angle's change over the step (exact for an angle that moves at a constant rate), per unit of nominal
            // frequency, then the exact zero-order-hold lag of T. With tffilt = 0 the lag is the identity, so the input is
            // the one-step difference itself.
            double difference = (angle - _prevTerminalAngle) / (2.0 * Math.PI * _nominalHz * dt);
            _frequencyInput = Iec61400Blocks.Lag(_frequencyInput, difference, _d.Protection!.Tffilt / 20.0, dt);
            _prot.Step(u, _frequencyInput, dt);
        }
        _prevTerminalAngle = angle;
        if (_prot?.Trip is not null) return;

        _pll.Step(u, angle, dt);
        double iqCmd = _qc.Step(_uf, _pf, _qf, dt);
        double omegaGen = _mech?.OmegaGen ?? 1.0;
        (double ipMax, double iqMin, double iqMax) = Iec61400CurrentLimiterModule.Limits(_d.CurrentLimit, _uf, omegaGen, _qc.FrtState,
            Iec61400PControlModule.Command(_pc.Order, _uf), iqCmd);
        _pc.Step(u, _uf, ipMax, omegaGen, dt);
        _mech?.Step(_pc.Aero, pg, dt);
        _gen.Step(Iec61400PControlModule.Command(_pc.Order, _uf), iqCmd, ipMax, iqMin, iqMax, dt);
    }

    // The angle of the injected current: the terminal angle at once while the previous step's voltage was at or above upll1
    // (held below 1e-3 pu), else the PLL's filtered angle.
    private double Angle(Complex v)
    {
        if (_pll.UsesFiltered) return _pll.Filtered;
        return v.Magnitude > AngleLockMinVoltagePu ? DetMath.Phase(v) : _lastAngle;
    }

    private Complex Current(double ip, double iq, double angle) => new Complex(ip, iq) * _rating * DetMath.FromPolar(1.0, angle);

    private void Record() => _samples.Add(new[]
    {
        _gen.Ip, _gen.Iq, _pc.RefState, _pc.Order, _pc.Aero, _mech?.OmegaWtr ?? 1.0, _mech?.OmegaGen ?? 1.0, _mech?.Twist ?? 0.0,
        _qc.QIntegral, _qc.VoltageIntegral, _qc.QOrder, _qc.OpenLoopCurrent, _qc.Uss, _uf, _pf, _qf, _pll.Filtered, _qc.FrtState,
        _prot?.Voltage ?? 0.0, _prot?.FrequencyDeviation ?? 0.0, _prot?.Trip is null ? 0.0 : 1.0,
    });

    private static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
