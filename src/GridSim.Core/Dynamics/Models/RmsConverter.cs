using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// Parameters of the RMS grid-following converter model, after the WECC second-generation renewable models:
/// the <b>REGC_A</b> generator/converter interface (current-command lags, low-voltage active current
/// management) under a <b>REEC</b>-style electrical control (constant P/Q in normal operation, K-factor
/// reactive-current boost and active-current freeze during a voltage dip, a current limit with reactive
/// priority, and a ramp-limited active-current recovery). Currents are per-unit on the unit's own rating.
/// Defaults are typical published values for a large grid-following plant.
/// </summary>
/// <param name="Tg">REGC_A current-command lag (s).</param>
/// <param name="Tr">REEC terminal-voltage measurement filter (s).</param>
/// <param name="Vdip">Voltage below which the fault (dip) logic is active (pu).</param>
/// <param name="Kqv">Reactive-current gain on the voltage dip, dIq = Kqv (V0 - V) (pu current / pu voltage).</param>
/// <param name="Imax">Converter current limit, the IEC 60909 <c>k</c> (pu of rated).</param>
/// <param name="LvPnt0">REGC_A LVACM: active current is zero at or below this terminal voltage (pu).</param>
/// <param name="LvPnt1">REGC_A LVACM: active current is unrestricted at or above this terminal voltage (pu).</param>
/// <param name="RampUpPuPerSecond">Active-current recovery rate limit after the dip (pu/s, REGC_A rrpwr).</param>
/// <param name="HoldSeconds">Time the dip logic stays active after the voltage recovers (REEC Thld2-like, s).</param>
/// <param name="BlockInDip">A line-commutated (thyristor) HVDC terminal: it carries its scheduled power in normal
/// operation but feeds no current during a dip (commutation failure / blocking), then ramps back. It has no
/// fault-current capability, so <see cref="Imax"/> is not applied as a normal-operation limit.</param>
/// <param name="GridForming">A grid-forming converter (virtual synchronous machine): a voltage source
/// <c>E&#8736;delta</c> behind <see cref="VirtualReactancePu"/>, with the angle driven by a swing equation
/// (<see cref="VirtualInertiaH"/>, <see cref="DampingPu"/>) and the magnitude by an integral voltage loop
/// (<see cref="VoltageGain"/>), current-limited at <see cref="Imax"/>. The grid-following parameters above do
/// not apply to it.</param>
/// <param name="VirtualInertiaH">Virtual inertia constant (s, on the unit rating).</param>
/// <param name="DampingPu">Damping/droop gain (pu power per pu speed, on the unit rating): 20 is a 5 % droop.</param>
/// <param name="VirtualReactancePu">Virtual (plus filter/transformer) reactance behind the EMF (pu on the rating).</param>
/// <param name="VoltageGain">Integral voltage-loop gain (pu EMF per pu voltage error per second).</param>
/// <param name="NominalHz">Nominal frequency (Hz) - converts the virtual speed into an angle rate.</param>
public sealed record RmsConverterParameters(
    double Tg = 0.02, double Tr = 0.02, double Vdip = 0.9, double Kqv = 2.0, double Imax = global::GridSim.Core.Model.Generator.DefaultConverterFaultCurrentPu,
    double LvPnt0 = 0.4, double LvPnt1 = 0.8, double RampUpPuPerSecond = 10.0, double HoldSeconds = 0.0,
    bool BlockInDip = false,
    bool GridForming = false, double VirtualInertiaH = 2.0, double DampingPu = 20.0, double VirtualReactancePu = 0.15,
    double VoltageGain = 2.0, double NominalHz = FrequencyLimits.NominalHz)
{
    /// <summary>Default parameters for a unit whose fault-current multiple is <paramref name="k"/>: a
    /// current-limited VSC at <c>k &gt; 0</c>, a blocking line-commutated terminal at <c>k = 0</c>.</summary>
    public static RmsConverterParameters WithLimit(double k) =>
        k > 0.0 ? new() { Imax = k } : new() { Imax = 0.0, BlockInDip = true };

    /// <summary>Default grid-forming (virtual synchronous machine) parameters with current limit <paramref name="k"/>.</summary>
    public static RmsConverterParameters GridFormingWithLimit(double k) => new() { GridForming = true, Imax = k > 0 ? k : global::GridSim.Core.Model.Generator.DefaultConverterFaultCurrentPu };
}

/// <summary>A converter-interfaced unit as the RMS engine sees it: where it sits among the retained nodes, its
/// rating, its operating point from the solved power flow, and its control parameters.</summary>
/// <param name="Bus">Model bus id.</param>
/// <param name="Name">Display name.</param>
/// <param name="RatingPu">Rated current (= MVA rating / system base; pu on the system base at 1 pu voltage).</param>
/// <param name="P0">Initial active power (pu, system base).</param>
/// <param name="Q0">Initial reactive power (pu, system base, exporting positive).</param>
/// <param name="V0">Initial terminal voltage phasor (pu).</param>
/// <param name="Parameters">Control parameters (currents on the unit rating).</param>
/// <param name="Store">Optional energy store behind the DC link (<see cref="Assets.IDcSource"/>, MW): its
/// instantaneous limit caps the active current and every step draws from it, so a depleted, cold or hot pack
/// cannot sustain the active power. Reactive current (ride-through support) is not drawn from it.
/// Internal use (F stream, R079, Ruling R1): no case format supplies a converter's DC store and no RMS-timescale study is energy-limited, so nothing populates it; kept, with its tests, for a future storage-aware converter model.
/// (N314, LF5, October 2026: a positional parameter of a public record cannot itself be made internal, so it is marked
/// here; every production construction of <see cref="RmsConverterUnit"/> leaves it null.)</param>
/// <param name="BaseMva">System base (MVA) that converts the store's MW to the network's per-unit.</param>
public sealed record RmsConverterUnit(
    int Bus, string Name, double RatingPu, double P0, double Q0, Complex V0, RmsConverterParameters Parameters,
    Assets.IDcSource? Store = null, double BaseMva = 100.0)
{
    /// <summary>
    /// The unit's WECC second-generation model chain (REGC_A with its electrical control), bound from a PSS/E DYR file
    /// through <see cref="SubtransientInit.MachineData.Renewable"/>; null (the default) for the historical converter
    /// model driven by <see cref="Parameters"/>. When set, the RMS kernel drives the unit with
    /// <see cref="global::GridSim.Core.Dynamics.Models.Wecc.WeccConverterState"/> and <see cref="Parameters"/> is not read (it is the grid-following default,
    /// so <see cref="NortonAdmittance"/> is zero: a WECC REGC_A unit is a current source).
    /// </summary>
    public global::GridSim.Core.Dynamics.Models.Wecc.WeccPlant? Wecc { get; init; }

    /// <summary>WECC: where the unit's plant controller (REPC_A) measures - its regulated bus and its flow branch -
    /// resolved against the case; null for a unit without a plant controller (and for every historical converter).
    /// Its buses are retained as extra converter nodes with no injection (<see cref="NodeBuses"/>).</summary>
    public global::GridSim.Core.Dynamics.Models.Wecc.WeccMeasurement? WeccMeasurement { get; init; }

    /// <summary>The kernel's dynamic model of this unit: the WECC chain when <see cref="Wecc"/> is set, else the
    /// historical converter. <paramref name="nodeBuses"/> is the network's converter-node bus order
    /// (<see cref="NodeBuses"/>), from which a WECC plant controller finds its measurement nodes.</summary>
    public IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses) => Wecc is null
        ? new LegacyRmsConverterModel(new RmsConverterState(this))
        : Wecc.Der is not null
            ? new global::GridSim.Core.Dynamics.Models.Wecc.DerAState(this)
            : Wecc.Gfm is not null
                ? new global::GridSim.Core.Dynamics.Models.Wecc.RegfmA1State(this)
                : new global::GridSim.Core.Dynamics.Models.Wecc.WeccConverterState(this, nodeBuses);

    /// <summary>
    /// The retained converter nodes of a converter list, as bus ids, in the one order the network builder
    /// (<see cref="SubtransientInit"/>) and the kernel (<see cref="MultiMachineSubtransient"/>) both use: every unit's bus
    /// in first-appearance order, then every WECC measurement bus (<see cref="WeccMeasurement"/>) not already a node, in
    /// first-appearance order. Without WECC measurements this is exactly the units' distinct buses in order, as before.
    /// </summary>
    public static List<int> NodeBuses(IReadOnlyList<RmsConverterUnit>? units)
    {
        var nodes = new List<int>();
        if (units is null) return nodes;
        foreach (RmsConverterUnit u in units) if (!nodes.Contains(u.Bus)) nodes.Add(u.Bus);
        foreach (RmsConverterUnit u in units)
            if (u.WeccMeasurement is { } m)
                foreach (int b in m.Buses()) if (!nodes.Contains(b)) nodes.Add(b);
        return nodes;
    }

    /// <summary>The Norton admittance a grid-forming unit's virtual reactance stamps on its bus (system pu):
    /// <c>RatingPu / (j Xv)</c>. Zero for a grid-following unit (a pure current source). WECC: a voltage-source model
    /// (REGC_B, REGFM_A1) stamps <c>RatingPu / Z</c> with its own source impedance
    /// (<see cref="global::GridSim.Core.Dynamics.Models.Wecc.WeccPlant.NortonImpedance"/>); every other WECC unit is a
    /// current source (zero).</summary>
    public Complex NortonAdmittance => Wecc is { NortonImpedance: { } zw } ? RatingPu / zw
        : Wecc is not null ? Complex.Zero
        : Parameters.GridForming
        ? RatingPu / new Complex(0.0, Math.Max(Parameters.VirtualReactancePu, RmsConverterState.MinVirtualReactancePu))
        : Complex.Zero;
}

/// <summary>Per-converter trajectory: terminal voltage, active and reactive power and current (system pu).</summary>
public sealed record RmsConverterTrace(
    string Name, int Bus,
    IReadOnlyList<double> TerminalVoltagePu,
    IReadOnlyList<double> ActivePowerPu,
    IReadOnlyList<double> ReactivePowerPu,
    IReadOnlyList<double> ActiveCurrentPu,
    IReadOnlyList<double> ReactiveCurrentPu,
    IReadOnlyList<bool> InDipMode)
{
    /// <summary>WECC: the voltage magnitude (pu) of the bus the unit's plant controller (REPC_A) regulates, at each
    /// sample; null for a unit without a plant controller measuring another bus.</summary>
    public IReadOnlyList<double>? RegulatedBusVoltagePu { get; init; }

    /// <summary>WECC: the bus the plant controller regulates, or 0.</summary>
    public int RegulatedBus { get; init; }
}

/// <summary>
/// The dynamic state of one RMS grid-following converter (see <see cref="RmsConverterParameters"/>). Per step,
/// given the terminal voltage phasor from the network solve:
/// <code>
///   Vf        = lag(|Vt|, Tr)                                   measured voltage
///   normal    : Ip* = P0 / Vf,  Iq* = Q0 / Vf                   constant power
///   dip (Vf &lt; Vdip, or within HoldSeconds of one):
///               Iq* = Iq0 + Kqv (V0 - Vf),  Ip* = Ip frozen at dip entry
///   limit     : Iq* clamped to +-Imax; Ip* &lt;= sqrt(Imax^2 - Iq*^2)          reactive priority
///   REGC_A    : Ip = lag(Ip*, Tg) with any rise held to RampUp,  Iq = lag(Iq*, Tg)
///   output    : Ip_out = Ip * LVACM(|Vt|)   (0 at LvPnt0, 1 at LvPnt1, linear between)
///   injected  : I = (Ip_out - j Iq) e^{j angle(Vt)}   (a phase-locked current source; exporting Q positive)
/// </code>
/// All currents are held in system pu (scaled by the rating), so the network sees them directly. The initial
/// state reproduces the power-flow injection exactly, so an undisturbed run is stationary.
/// </summary>
public sealed class RmsConverterState
{
    // ---- Named floors and clamps (October 2026 review, WS13 R167). Each replaced a bare literal of exactly the
    // ---- same value, so no trajectory moves.

    /// <summary>Floor (pu on the rating) on a grid-forming unit's virtual reactance before it is inverted into
    /// the Norton admittance, so an unauthored zero reactance cannot divide by zero.</summary>
    public const double MinVirtualReactancePu = 1e-6;

    /// <summary>Floor (pu) on a voltage magnitude used as a divisor or a direction: the initial terminal
    /// voltage and the terminal-voltage unit vector of a grid-forming unit.</summary>
    public const double MinVoltageMagnitudePu = 1e-6;

    /// <summary>Floor (s) on the terminal-voltage measurement lag Tr, so a zero Tr becomes an (almost) instant lag
    /// rather than a division by zero.</summary>
    public const double MinMeasurementLagSeconds = 1e-6;

    /// <summary>The phase-locked loop follows the terminal angle only while the terminal voltage exceeds this
    /// (pu); below it (a bolted fault) the angle is meaningless and the last locked angle is held.</summary>
    public const double PllLockMinVoltagePu = 1e-3;

    /// <summary>Lower clamp (pu) on a grid-forming unit's EMF magnitude from its integral voltage loop.</summary>
    public const double GfmEmfMinPu = 0.5;

    /// <summary>Upper clamp (pu) on a grid-forming unit's EMF magnitude from its integral voltage loop.</summary>
    public const double GfmEmfMaxPu = 1.5;

    /// <summary>Floor (pu) on the filtered terminal voltage used to turn a power reference into a current
    /// reference (I = P / V) in a grid-following unit.</summary>
    public const double MinControlVoltagePu = 0.01;

    private readonly RmsConverterUnit _u;
    private readonly FirstOrderLag _vf;
    private readonly double _ip0, _iq0, _v0Mag, _imax;
    private double _ip, _iq, _ipFrozen, _holdUntil = double.NegativeInfinity;
    private double _angle;

    // Grid-forming (virtual synchronous machine) state: EMF magnitude and angle, virtual speed (pu deviation),
    // the Norton admittance of the virtual reactance, inertia and damping on the system base, references.
    private readonly bool _gfm;
    private readonly Complex _y;
    private readonly double _hSys, _dSys, _pRef0, _vRef, _ws;
    private double _eMag, _delta, _omega;
    private bool _limited;

    public RmsConverterState(RmsConverterUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _u = unit;
        _gfm = unit.Parameters.GridForming;
        if (_gfm)
        {
            RmsConverterParameters gp = unit.Parameters;
            _y = unit.NortonAdmittance;
            _hSys = gp.VirtualInertiaH * unit.RatingPu;
            _dSys = gp.DampingPu * unit.RatingPu;
            _ws = 2.0 * Math.PI * gp.NominalHz;
            Complex it0 = Complex.Conjugate(new Complex(unit.P0, unit.Q0) / unit.V0);
            Complex e0 = unit.V0 + it0 / _y;                // E = V + I / y  (terminal current through Xv)
            _eMag = e0.Magnitude; _delta = DetMath.Phase(e0);
            _pRef0 = unit.P0; _vRef = unit.V0.Magnitude;
        }
        _v0Mag = Math.Max(unit.V0.Magnitude, MinVoltageMagnitudePu);
        _vf = new FirstOrderLag(1.0, Math.Max(unit.Parameters.Tr, MinMeasurementLagSeconds));
        _vf.Initialize(_v0Mag);
        _ip0 = unit.P0 / _v0Mag;
        _iq0 = unit.Q0 / _v0Mag;
        _imax = unit.Parameters.BlockInDip ? double.PositiveInfinity : unit.Parameters.Imax * unit.RatingPu;
        _ip = _ip0; _iq = _iq0; _ipFrozen = _ip0;
        _angle = DetMath.Phase(unit.V0);
    }

    public RmsConverterUnit Unit => _u;

    /// <summary>Active current actually injected (after LVACM), system pu.</summary>
    public double ActiveCurrent { get; private set; }

    /// <summary>Reactive current injected (exporting positive), system pu.</summary>
    public double ReactiveCurrent => _iq;

    /// <summary>True while the dip (fault ride-through) logic is active.</summary>
    public bool InDip { get; private set; }

    /// <summary>Grid-forming: true while the terminal current is held at the limit.</summary>
    public bool CurrentLimited => _limited;

    /// <summary>True for a grid-forming (virtual synchronous machine) unit.</summary>
    public bool IsGridForming => _gfm;

    /// <summary>Grid-forming: the virtual speed deviation (pu) and EMF angle (rad); NaN for grid-following.</summary>
    public double VirtualSpeedPu => _gfm ? _omega : double.NaN;
    public double EmfAngleRad => _gfm ? _delta : double.NaN;

    /// <summary>The unit's terminal current (into the network) for a node <paramref name="injection"/> at
    /// terminal voltage <paramref name="v"/>: a grid-forming unit's Norton admittance is part of the network, so
    /// its terminal current is <c>injection - y V</c>; a grid-following unit's injection is its terminal current.</summary>
    public Complex TerminalCurrent(Complex injection, Complex v) => _gfm ? injection - _y * v : injection;

    /// <summary>Grid-forming: the terminal current for terminal voltage <paramref name="v"/> - the EMF behind the
    /// virtual reactance, or, when that would exceed the limit, the limit in the same direction.</summary>
    private Complex GfmTerminalCurrent(Complex v, out bool limited)
    {
        Complex iu = (DetMath.FromPolar(_eMag, _delta) - v) * _y;
        double imax = _u.Parameters.Imax * _u.RatingPu;
        limited = iu.Magnitude > imax;
        return limited ? iu * (imax / iu.Magnitude) : iu;
    }

    private Complex GfmInjection(Complex v, bool commit)
    {
        Complex it = GfmTerminalCurrent(v, out bool limited);
        if (commit)
        {
            _limited = limited;
            double vm = v.Magnitude;
            Complex vHat = vm > MinVoltageMagnitudePu ? v / vm : Complex.One;
            Complex rel = it * Complex.Conjugate(vHat);   // current on the terminal-voltage axis
            ActiveCurrent = rel.Real;
            _iq = -rel.Imaginary;                         // exporting reactive current positive
            InDip = limited || vm < _u.Parameters.Vdip;
        }
        return it + _y * v;                               // node injection: terminal current plus the Norton part
    }

    /// <summary>The injected current phasor (network frame) at terminal angle <paramref name="terminalAngle"/>
    /// and magnitude <paramref name="terminalVoltage"/> (used for LVACM). A collapsed terminal keeps its last
    /// phase-locked angle.</summary>
    public Complex Current(double terminalVoltage, double terminalAngle, bool lockAngle)
    {
        if (_gfm) return GfmInjection(DetMath.FromPolar(terminalVoltage, terminalAngle), commit: true);
        if (lockAngle && terminalVoltage > PllLockMinVoltagePu) _angle = terminalAngle;
        ActiveCurrent = _ip * Lvacm(terminalVoltage);
        return new Complex(ActiveCurrent, -_iq) * DetMath.FromPolar(1.0, _angle);
    }

    /// <summary>The current phasor for a trial angle, without committing the phase lock (for the algebraic
    /// network iteration).</summary>
    public Complex TrialCurrent(double terminalVoltage, double terminalAngle)
    {
        if (_gfm) return GfmInjection(DetMath.FromPolar(terminalVoltage, terminalAngle), commit: false);
        double ang = terminalVoltage > PllLockMinVoltagePu ? terminalAngle : _angle;
        return new Complex(_ip * Lvacm(terminalVoltage), -_iq) * DetMath.FromPolar(1.0, ang);
    }

    /// <summary>Advance the controls one step from the start-of-step terminal voltage phasor. A grid-following
    /// unit needs only the magnitude; a grid-forming unit integrates its swing and voltage loop from the phasor.</summary>
    public void Step(double t, Complex terminalVoltage, double dt)
    {
        if (!_gfm) { Step(t, terminalVoltage.Magnitude, dt); return; }

        Complex it = GfmTerminalCurrent(terminalVoltage, out bool limited);
        double pe = (terminalVoltage * Complex.Conjugate(it)).Real;
        AdvanceGridForming(pe, terminalVoltage.Magnitude, limited, dt);
    }

    /// <summary>
    /// Grid-forming: advance the swing and voltage loop from a <b>measured</b> electrical power
    /// <paramref name="pe"/> and terminal-voltage magnitude, with the current-limit state decided by the caller.
    /// The EMT model uses this with the power measured from the actual instantaneous terminal current (a
    /// phasor estimate through the large virtual admittance would turn small PLL phase errors into large false
    /// power); the RMS model feeds its algebraic values.
    /// </summary>
    public void AdvanceGridForming(double pe, double terminalVoltageMagnitude, bool limited, double dt)
    {
        if (!_gfm) throw new InvalidOperationException("AdvanceGridForming is for grid-forming units.");
        RmsConverterParameters p = _u.Parameters;

        // Power reference, capped by the energy store's instantaneous limit (MW -> system pu).
        double pRef = _pRef0;
        if (_u.Store is { } store)
        {
            (double upMw, double downMw) = store.Headroom();
            pRef = Math.Clamp(pRef, -downMw / _u.BaseMva, upMw / _u.BaseMva);
            store.Draw(pe * _u.BaseMva, dt);
        }

        // Swing (semi-implicit Euler): 2H domega/dt = Pref - Pe - D omega;  ddelta/dt = ws omega.
        _omega = (_omega + dt * (pRef - pe) / (2.0 * _hSys)) / (1.0 + dt * _dSys / (2.0 * _hSys));
        _delta += _ws * _omega * dt;

        // Integral voltage loop, frozen while current-limited (anti-windup).
        if (!limited) _eMag = Math.Clamp(_eMag + p.VoltageGain * (_vRef - terminalVoltageMagnitude) * dt, GfmEmfMinPu, GfmEmfMaxPu);
        _limited = limited;
    }

    /// <summary>Grid-forming: the EMF phasor (network frame) and the Norton admittance of the virtual reactance.</summary>
    public Complex Emf => _gfm ? DetMath.FromPolar(_eMag, _delta) : Complex.Zero;
    public Complex NortonAdmittance => _y;

    /// <summary>Grid-forming: record externally measured terminal quantities for the traces (EMT).</summary>
    public void RecordMeasured(double activeCurrent, double reactiveCurrent, bool limited)
    {
        ActiveCurrent = activeCurrent; _iq = reactiveCurrent; _limited = limited; InDip = limited;
    }

    /// <summary>Advance the controls one step from the start-of-step terminal voltage magnitude (grid-following).</summary>
    public void Step(double t, double terminalVoltage, double dt)
    {
        if (_gfm) throw new InvalidOperationException("A grid-forming unit needs the terminal voltage phasor: use Step(t, Complex, dt).");
        RmsConverterParameters p = _u.Parameters;
        double vf = _vf.Step(terminalVoltage, dt);
        double vDen = Math.Max(vf, MinControlVoltagePu);

        bool dip = vf < p.Vdip;
        if (dip) _holdUntil = t + p.HoldSeconds;
        bool inDip = dip || t < _holdUntil;
        if (inDip && !InDip) _ipFrozen = _ip;       // entering the dip: freeze the active current
        InDip = inDip;

        double ipRef, iqRef;
        if (inDip && p.BlockInDip)
        {
            ipRef = 0.0; iqRef = 0.0;                // line-commutated: blocked through the dip
        }
        else if (inDip)
        {
            iqRef = _iq0 + p.Kqv * (_v0Mag - vf) * _u.RatingPu;
            ipRef = _ipFrozen;
        }
        else
        {
            ipRef = _ip0 * _v0Mag / vDen;            // P0 / Vf
            iqRef = _iq0 * _v0Mag / vDen;            // Q0 / Vf
        }

        // Current limit with reactive priority.
        iqRef = Math.Clamp(iqRef, -_imax, _imax);
        double ipMax = Math.Sqrt(Math.Max(0.0, _imax * _imax - iqRef * iqRef));
        ipRef = Math.Clamp(ipRef, -ipMax, ipMax);

        // The energy store's instantaneous limit (MW -> system pu -> current at the measured voltage).
        if (_u.Store is { } store)
        {
            (double upMw, double downMw) = store.Headroom();
            ipRef = Math.Clamp(ipRef, -downMw / _u.BaseMva / vDen, upMw / _u.BaseMva / vDen);
        }

        // REGC_A lags (exact ZOH); any rise in active-current magnitude is held to the recovery ramp (rrpwr),
        // including while the dip logic is still latched as the voltage comes back. Falls are not limited.
        double a = p.Tg > 0 ? DetMath.Exp(-dt / p.Tg) : 0.0;
        double ipNext = ipRef + (_ip - ipRef) * a;
        double maxRise = p.RampUpPuPerSecond * _u.RatingPu * dt;
        if (Math.Abs(ipNext) > Math.Abs(_ip)) ipNext = _ip + Math.Clamp(ipNext - _ip, -maxRise, maxRise);
        _ip = ipNext;
        _iq = iqRef + (_iq - iqRef) * a;

        // Draw the active power from the store; what it cannot deliver it does not inject.
        // R248: the draw is the power ACTUALLY injected - |V| * Ip * Lvacm(|V|) at the start-of-step terminal
        // magnitude, the same current Current() injects - not the filtered voltage times the pre-LVACM current.
        // In a deep dip Lvacm is 0 below LvPnt0 while Ip is frozen at its pre-dip value, so the old draw debited
        // the store for power that was never delivered. A shortfall scales Ip so the injection equals what the
        // store delivered (pMw, hence the divisor, is non-zero whenever there is a shortfall).
        if (_u.Store is { } st)
        {
            double pMw = terminalVoltage * _ip * Lvacm(terminalVoltage) * _u.BaseMva;
            double delivered = st.Draw(pMw, dt);
            if (Math.Abs(delivered) < Math.Abs(pMw)) _ip = delivered / (_u.BaseMva * terminalVoltage * Lvacm(terminalVoltage));
        }
    }

    /// <summary>State of charge of the energy store, or NaN without one.</summary>
    public double StoreSoc => _u.Store?.SocFraction ?? double.NaN;

    private double Lvacm(double v)
    {
        RmsConverterParameters p = _u.Parameters;
        if (v >= p.LvPnt1) return 1.0;
        if (v <= p.LvPnt0) return 0.0;
        return (v - p.LvPnt0) / (p.LvPnt1 - p.LvPnt0);
    }
}

/// <summary>
/// The reduced network with converter terminal buses retained next to the machine internal nodes. Partitioning
/// the retained admittance <c>[Ymm Ymc; Ycm Ycc]</c> (machines first), the converter bus voltages and the
/// machine currents follow in closed form from the machine EMFs <c>E</c> and the converter currents <c>Ic</c>:
/// <code>
///   Vc  = Ycc^-1 (Ic - Ycm E)
///   I_m = Ymm E + Ymc Vc  =  Yeff E + Ymc Ycc^-1 Ic,   Yeff = Ymm - Ymc Ycc^-1 Ycm
/// </code>
/// <c>Yeff</c> is the usual machine-only reduced admittance (converter nodes eliminated at zero injection), so
/// with no converter current the machines see exactly the network they always did.
/// </summary>
public sealed class ConverterNetwork : IRmsNetwork
{
    public int Machines { get; }
    public int Converters { get; }

    /// <summary>Machine-only effective admittance (converters eliminated at zero current).</summary>
    public Complex[,] YEffective { get; }

    private readonly Complex[,] _zcc;     // Ycc^-1 (c x c)
    private readonly Complex[,] _gcm;     // -Ycc^-1 Ycm (c x n)
    private readonly Complex[,] _kmc;     // Ymc Ycc^-1 (n x c)
    private readonly MachineAdmittanceColumns _yEffColumns;   // R116: YEffective for the vector product (same bits)

    public ConverterNetwork(Complex[,] yRetained, int machines)
    {
        ArgumentNullException.ThrowIfNull(yRetained);
        int total = yRetained.GetLength(0);
        int n = machines, c = total - machines;
        if (c <= 0) throw new ArgumentException("No converter nodes retained.", nameof(yRetained));
        Machines = n; Converters = c;

        var ycc = new Complex[c, c];
        for (int i = 0; i < c; i++)
            for (int j = 0; j < c; j++) ycc[i, j] = yRetained[n + i, n + j];
        Numerics.DenseComplexLu lu = Numerics.DenseComplexLuSolver.FactorDense(ycc, c);
        // N019 (October 2026): a converter-node admittance block that is singular to working precision (a retained
        // converter bus with no admittance to anything, for example) has no inverse. FactorDense clamps the vanishing
        // pivot and its solves then return finite numbers that are not Ycc^-1; the converter coupling built on them
        // would be fabricated. Refuse it, naming the problem, instead of simulating on it.
        if (lu.ClampedPivots > 0)
            throw new ArgumentException(
                $"RMS converter network: the converter-node admittance block ({c} x {c}) is singular to working precision "
                + $"({lu.ClampedPivots} pivot(s) at or below the relative floor), so it has no inverse; check that every "
                + "retained converter bus is connected.", nameof(yRetained));
        _zcc = new Complex[c, c];
        var e = new Complex[c];
        for (int j = 0; j < c; j++)
        {
            Array.Clear(e);
            e[j] = Complex.One;
            Complex[] col = lu.Solve(e);
            for (int i = 0; i < c; i++) _zcc[i, j] = col[i];
        }

        _gcm = new Complex[c, n];
        for (int i = 0; i < c; i++)
            for (int j = 0; j < n; j++)
            {
                Complex s = Complex.Zero;
                for (int q = 0; q < c; q++) s += _zcc[i, q] * yRetained[n + q, j];
                _gcm[i, j] = -s;
            }
        _kmc = new Complex[n, c];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < c; j++)
            {
                Complex s = Complex.Zero;
                for (int q = 0; q < c; q++) s += yRetained[i, n + q] * _zcc[q, j];
                _kmc[i, j] = s;
            }
        YEffective = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex s = yRetained[i, j];
                for (int q = 0; q < c; q++) s += yRetained[i, n + q] * _gcm[q, j];
                YEffective[i, j] = s;
            }
        _yEffColumns = new MachineAdmittanceColumns(YEffective);
    }

    /// <summary><see cref="IRmsNetwork"/>: <c>I_m = Yeff E + Ymc Ycc^-1 Ic</c> and the converter bus voltages.</summary>
    public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents)
    {
        ArgumentNullException.ThrowIfNull(emf);
        if (emf.Length != Machines)
            throw new ArgumentException($"emf holds {emf.Length} entries; the network has {Machines} machines.", nameof(emf));
        var im = new Complex[emf.Length];
        _yEffColumns.MultiplyInto(emf, im);   // R116: Yeff E, bit-identical to DenseMachineNetwork.MultiplyMachine
        Complex[] extra = MachineCurrentsFromConverters(converterCurrents);
        for (int i = 0; i < im.Length; i++) im[i] += extra[i];
        return (im, ConverterVoltages(emf, converterCurrents));
    }

    /// <inheritdoc />
    /// <remarks>R115: <see cref="Solve"/>'s arithmetic written in place. Each machine current is <c>Yeff E</c> (the
    /// same loop) plus <c>Ymc Ycc^-1 Ic</c> accumulated in the same order into a local, then added, exactly as the
    /// allocating version adds its <c>extra</c> array; the converter voltages are the same loop as
    /// <see cref="ConverterVoltages"/>.</remarks>
    public void SolveInto(Complex[] emf, Complex[] converterCurrents, Complex[] machineCurrents, Complex[] converterVoltages)
    {
        ArgumentNullException.ThrowIfNull(emf);
        ArgumentNullException.ThrowIfNull(converterCurrents);
        ArgumentNullException.ThrowIfNull(machineCurrents);
        ArgumentNullException.ThrowIfNull(converterVoltages);
        RmsNetworkBuffers.Check(Machines, Converters, machineCurrents, converterVoltages);
        if (ReferenceEquals(emf, machineCurrents))
            throw new ArgumentException("machineCurrents must not be the emf array.", nameof(machineCurrents));
        int n = Machines, c = Converters;
        if (emf.Length != n)
            throw new ArgumentException($"emf holds {emf.Length} entries; the network has {n} machines.", nameof(emf));
        _yEffColumns.MultiplyInto(emf, machineCurrents);   // R116: Yeff E, bit-identical to MultiplyMachineInto
        for (int i = 0; i < n; i++)
        {
            Complex s = Complex.Zero;
            for (int q = 0; q < c; q++) s += _kmc[i, q] * converterCurrents[q];
            machineCurrents[i] += s;
        }
        for (int i = 0; i < c; i++)
        {
            Complex s = Complex.Zero;
            for (int q = 0; q < c; q++) s += _zcc[i, q] * converterCurrents[q];
            for (int j = 0; j < n; j++) s += _gcm[i, j] * emf[j];
            converterVoltages[i] = s;
        }
    }

    /// <summary>Converter bus voltages for machine EMF phasors <paramref name="e"/> and converter currents.</summary>
    public Complex[] ConverterVoltages(Complex[] e, Complex[] ic)
    {
        int n = Machines, c = Converters;
        var v = new Complex[c];
        for (int i = 0; i < c; i++)
        {
            Complex s = Complex.Zero;
            for (int q = 0; q < c; q++) s += _zcc[i, q] * ic[q];
            for (int j = 0; j < n; j++) s += _gcm[i, j] * e[j];
            v[i] = s;
        }
        return v;
    }

    /// <summary>The extra machine currents <c>Ymc Ycc^-1 Ic</c> the converter currents drive.</summary>
    public Complex[] MachineCurrentsFromConverters(Complex[] ic)
    {
        int n = Machines, c = Converters;
        var im = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex s = Complex.Zero;
            for (int q = 0; q < c; q++) s += _kmc[i, q] * ic[q];
            im[i] = s;
        }
        return im;
    }
}
