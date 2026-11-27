using System.Globalization;

namespace GridSim.Core.Dynamics.Models.Iec61400;

// RC5 WP23: generic IEC 61400-27-1 style wind turbine models, Type 4A and Type 4B (full-converter turbines), 2020 edition.
//
// SOURCE BASIS. Built from public sources only; the IEC 61400-27-1 text itself was NOT used and the models are NOT checked
// against it:
//  - Dynawo (RTE, MPL-2.0), commit 43812dfbbe1c5245905efaeb4149addcf0affe11 (github.com/dynawo/dynawo, 2026-11-24), Modelica
//    library Dynawo.Electrical.Wind.IEC.WT (WT4ACurrentSource2020, WT4BCurrentSource2020),
//    Dynawo.Electrical.Controls.IEC.IEC61400 (Control4A2020, Control4B2020, PControl4A2020, PControl4B2020, QControl2020,
//    BaseQControl, CurrentLimiter2020, BaseCurrentLimiter, Mechanical, PLL, GridProtection2020, BaseGridProtection, VDrop,
//    Measurements) and Dynawo.Electrical.Sources.IEC (WT4Injector, GenSystem4, RefFrameRotation). The block diagrams were READ
//    for their structure; no Dynawo file or code was copied. Every block here is written independently as a fixed-step
//    discrete model (see Iec61400Blocks.cs for the discretisation).
//  - ENTSO-E CGMES 2.4.15 Dynamics profile RDFS (DynamicsProfileRDFSAugmented-v2_4_15-16Feb2016.rdf): the parameter names of
//    WindGenType4IEC (dipmax, diqmin, diqmax, tg), WindContPType4aIEC / 4bIEC (dpmax, tpord, tpaero), WindContQIEC (iqh1, iqmax,
//    iqmin, iqpost, kiq, kiu, kpq, kpu, kqv, qmax, qmin, rdroop, xdroop, tpost, tqord, udb1, udb2, umax, umin, uqdip, uref0,
//    windQcontrolModesType, windLVRTQcontrolModesType), WindContCurrLimIEC (imax, imaxdip, mdfslim, mqpri, lookup tables ipvdl
//    and iqvdl), WindMechIEC (cdrt, hgen, hwtr, kdrt) and WindProtectionIEC (uover, uunder, fover, funder). CGMES 2.4.15 follows
//    the 2015 edition; the 2020 parameters it does not name take Dynawo's names, lower case (dprefmax, dprefmin, tpwtref,
//    mpuscale, updip, kpufrt, uqrise, tuss, kpqu, upqumax, tpll, upll1, upll2, the measurement filters).
//
// What was taken from Dynawo, in words: the signal flow of each module, the switching of the reactive control modes and of the
// fault ride-through states, the current-limit priority logic, the two-mass equations (shaft twist integrated with gain 1, no
// angle base factor), the PLL freeze thresholds, the protection timers and the protection's frequency measurement (the
// terminal angle's derivative through Dynawo's Derivative block, T = tffilt / 20, taken as the one-step angle difference and
// then the exact lag of T). The protection tables tuover, tuunder, tfover and tfunder take the CGMES WindProtectionIEC names,
// which CGMES 2.4.15 gives scalar times (the 2015 edition); here they are tables of the level, as in Dynawo's 2020
// GridProtection2020. What differs, on purpose: anti-windup gains (Kpaw,
// Kipaw, Kiqaw) are replaced by non-windup clamping of the limited state (the limit of a large anti-windup gain); the
// reactive-power PI integrator starts at the dropped voltage (an exact equilibrium) where Dynawo starts it at U0 (the two agree
// when rdroop = xdroop = 0); placeholder limits of +-999 are read as "no limit"; lookup tables hold their end values outside
// their range (Dynawo's table default extrapolates linearly through the last two points; give points covering the operating
// range and the two agree); the converter's series and shunt impedance (Dynawo ElecSystem) is not built (the unit injects at
// its terminal); the plant controller (WindPlantIEC) is not built.

/// <summary>RC5 WP23: the IEC 61400-27-1 full-converter turbine type.</summary>
public enum Iec61400TurbineType
{
    /// <summary>Type 4A: no mechanical model (the generator speed is 1 pu).</summary>
    Type4A,

    /// <summary>Type 4B: with the two-mass drive train (<see cref="Iec61400Mechanical"/>) and the aerodynamic power lag.</summary>
    Type4B,
}

/// <summary>RC5 WP23: the general reactive control mode (CGMES WindQcontrolModesKind; Dynawo MqG). The integer value is
/// Dynawo's MqG.</summary>
public enum Iec61400QControlMode
{
    /// <summary>Voltage control (MqG 0).</summary>
    Voltage = 0,

    /// <summary>Reactive power control through the voltage controller (MqG 1).</summary>
    ReactivePower = 1,

    /// <summary>Open-loop reactive power (MqG 2).</summary>
    OpenLoopReactivePower = 2,

    /// <summary>Power factor control through the voltage controller (MqG 3).</summary>
    PowerFactor = 3,

    /// <summary>Open-loop power factor (MqG 4; 2020 edition, not in CGMES 2.4.15).</summary>
    OpenLoopPowerFactor = 4,
}

/// <summary>
/// RC5 WP23: a piecewise-linear lookup table (CGMES WindDynamicsLookupTable). <see cref="X"/> must rise strictly; between points
/// the output is interpolated linearly; outside the range the end value is held. One point is a constant.
/// </summary>
/// <param name="X">The input points, strictly rising, finite.</param>
/// <param name="Y">The output at each input point, finite.</param>
public sealed record Iec61400Table(IReadOnlyList<double> X, IReadOnlyList<double> Y)
{
    /// <summary>A table holding <paramref name="value"/> for every input.</summary>
    public static Iec61400Table Constant(double value) => new(ZeroPoint, new[] { value });

    private static readonly double[] ZeroPoint = { 0.0 };

    /// <summary>The output at input <paramref name="x"/>.</summary>
    public double Evaluate(double x)
    {
        int n = X.Count;
        if (x <= X[0]) return Y[0];
        if (x >= X[n - 1]) return Y[n - 1];
        int i = 1;
        while (X[i] < x) i++;
        double x0 = X[i - 1], x1 = X[i];
        return Y[i - 1] + (Y[i] - Y[i - 1]) * ((x - x0) / (x1 - x0));
    }

    internal void Check(string name, List<string> problems)
    {
        if (X is null || Y is null || X.Count == 0 || X.Count != Y.Count)
        {
            problems.Add($"{name}: a table needs at least one point and as many outputs as inputs.");
            return;
        }
        for (int i = 0; i < X.Count; i++)
        {
            if (!double.IsFinite(X[i]) || !double.IsFinite(Y[i])) problems.Add($"{name}: point {i.ToString(CultureInfo.InvariantCulture)} is not finite.");
            else if (i > 0 && !(X[i] > X[i - 1])) problems.Add($"{name}: the inputs must rise strictly (point {i.ToString(CultureInfo.InvariantCulture)}).");
        }
    }
}

/// <summary>RC5 WP23: the Type 4 generator system (CGMES WindGenType4IEC; Dynawo GenSystem4). The active and reactive currents
/// follow their commands through first-order lags <see cref="Tg"/>, with rate limits.</summary>
/// <param name="Dipmax">Maximum rise rate of the active current (pu/s, above 0; a fall is not limited).</param>
/// <param name="Diqmin">Maximum fall rate of the reactive current (pu/s, below 0, signed as Dynawo's DiqMinPu).</param>
/// <param name="Diqmax">Maximum rise rate of the reactive current (pu/s, above 0).</param>
/// <param name="Tg">Current generation time constant (s, 0 or above; 0 follows the command at once, within the rate limits).</param>
public sealed record Iec61400GenSystem(double Dipmax, double Diqmin, double Diqmax, double Tg);

/// <summary>RC5 WP23: the Type 4 active power control (CGMES WindContPType4aIEC / WindContPType4bIEC; Dynawo PControl4A2020 /
/// PControl4B2020).</summary>
/// <param name="Dpmax">Maximum rise rate of the power order (pu/s, above 0; Dynawo DPMaxP4A / DPMaxP4B).</param>
/// <param name="Tpord">Power order lag (s, 0 or above; Dynawo tPOrdP4A / tPOrdP4B).</param>
/// <param name="Dprefmax">Maximum rise rate of the reference power (pu/s, above 0).</param>
/// <param name="Dprefmin">Maximum fall rate of the reference power (pu/s, below 0, signed).</param>
/// <param name="Tpwtref">Type 4A: the reference power lag (s, 0 or above; Dynawo tPWTRef4A). Not read for Type 4B.</param>
/// <param name="Tpaero">Type 4B: the aerodynamic power lag (s, 0 or above; CGMES tpaero). Not read for Type 4A.</param>
/// <param name="Mpuscale">Scale the reference by the terminal voltage during a dip (Dynawo MpUScale).</param>
/// <param name="Updip">The voltage below which the scaling acts (pu; Dynawo UpDipPu).</param>
public sealed record Iec61400PControl(double Dpmax, double Tpord, double Dprefmax, double Dprefmin, double Tpwtref, double Tpaero,
    bool Mpuscale, double Updip);

/// <summary>RC5 WP23: the reactive power control with its fault ride-through states (CGMES WindContQIEC; Dynawo QControl2020 and
/// BaseQControl). Constant reactive power limits <see cref="Qmax"/> / <see cref="Qmin"/> (Dynawo QlConst true; the
/// voltage- and power-dependent Q limiter tables are not built).</summary>
/// <param name="Mode">The general control mode (CGMES windQcontrolModesType; Dynawo MqG).</param>
/// <param name="FrtMode">The ride-through mode 0, 1 or 2 (CGMES windLVRTQcontrolModesType; Dynawo Mqfrt). Mode 3 (a user hook)
/// is not built.</param>
/// <param name="Iqh1">Upper limit of the reactive current during and after a fault (pu, IEC frame).</param>
/// <param name="Iqmax">Upper limit of the voltage controller's reactive current (pu).</param>
/// <param name="Iqmin">Lower limit of the voltage controller's reactive current, and of the current during and after a fault (pu).</param>
/// <param name="Iqpost">Post-fault reactive current offset (pu; ride-through mode 2).</param>
/// <param name="Kiq">Reactive power PI integral gain (pu/s).</param>
/// <param name="Kiu">Voltage PI integral gain (pu/s).</param>
/// <param name="Kpq">Reactive power PI proportional gain (pu).</param>
/// <param name="Kpu">Voltage PI proportional gain (pu).</param>
/// <param name="Kpufrt">Voltage PI proportional gain during a fault (pu; 2020).</param>
/// <param name="Kqv">Reactive current gain on the voltage change during a fault (pu/pu).</param>
/// <param name="Qmax">Constant maximum reactive power (pu).</param>
/// <param name="Qmin">Constant minimum reactive power (pu).</param>
/// <param name="Rdroop">Resistive part of the voltage drop compensation (pu; Dynawo RDropPu).</param>
/// <param name="Xdroop">Inductive part of the voltage drop compensation (pu; Dynawo XDropPu).</param>
/// <param name="Tpost">Length of the post-fault state (s, 0 or above).</param>
/// <param name="Tqord">Reactive power order lag of the open-loop modes (s, 0 or above).</param>
/// <param name="Tuss">Steady-state voltage filter (s, above 0; 2020).</param>
/// <param name="Udb1">Lower edge of the voltage change dead band (pu, 0 or below).</param>
/// <param name="Udb2">Upper edge of the voltage change dead band (pu, 0 or above).</param>
/// <param name="Umax">Upper limit of the voltage reference (pu).</param>
/// <param name="Umin">Lower limit of the voltage reference (pu).</param>
/// <param name="Uqdip">Voltage below which the fault state starts (pu).</param>
/// <param name="Uqrise">Voltage above which the fault state starts (pu; 2020).</param>
/// <param name="Uref0">Bias of the voltage reference (pu).</param>
public sealed record Iec61400QControl(Iec61400QControlMode Mode, int FrtMode, double Iqh1, double Iqmax, double Iqmin, double Iqpost,
    double Kiq, double Kiu, double Kpq, double Kpu, double Kpufrt, double Kqv, double Qmax, double Qmin, double Rdroop, double Xdroop,
    double Tpost, double Tqord, double Tuss, double Udb1, double Udb2, double Umax, double Umin, double Uqdip, double Uqrise, double Uref0);

/// <summary>RC5 WP23: the current limiter (CGMES WindContCurrLimIEC; Dynawo CurrentLimiter2020 and BaseCurrentLimiter).</summary>
/// <param name="Imax">Maximum current magnitude outside a fault (pu, above 0).</param>
/// <param name="Imaxdip">Maximum current magnitude during a fault (pu, above 0).</param>
/// <param name="Mdfslim">Limit the stator current (the active current divided by the generator speed) instead of the total.</param>
/// <param name="Mqpri">Reactive current priority during and after a fault (false: active priority).</param>
/// <param name="Kpqu">Slope of the reactive current limit against voltage above <see cref="Upqumax"/> (pu/pu; 2020).</param>
/// <param name="Upqumax">Voltage at which no capacitive reactive current can be delivered (pu; 2020).</param>
/// <param name="Ipvdl">Active current limit against the filtered voltage (CGMES lookup table kind ipvdl).</param>
/// <param name="Iqvdl">Reactive current limit against the filtered voltage (CGMES lookup table kind iqvdl).</param>
public sealed record Iec61400CurrentLimit(double Imax, double Imaxdip, bool Mdfslim, bool Mqpri, double Kpqu, double Upqumax,
    Iec61400Table Ipvdl, Iec61400Table Iqvdl);

/// <summary>RC5 WP23: the two-mass drive train of a Type 4B turbine (CGMES WindMechIEC; Dynawo Mechanical).</summary>
/// <param name="Cdrt">Drive train damping (pu, 0 or above).</param>
/// <param name="Hgen">Generator inertia (s, above 0).</param>
/// <param name="Hwtr">Rotor inertia (s, above 0).</param>
/// <param name="Kdrt">Drive train stiffness (pu, above 0).</param>
public sealed record Iec61400Mechanical(double Cdrt, double Hgen, double Hwtr, double Kdrt);

/// <summary>RC5 WP23: the phase-locked loop (Dynawo PLL).</summary>
/// <param name="Tpll">The angle filter (s, 0 or above).</param>
/// <param name="Upll1">Below this voltage the angle is filtered (pu); at or above it the terminal angle is used at once.</param>
/// <param name="Upll2">Below this voltage the filtered angle is frozen (pu; at most <see cref="Upll1"/>).</param>
public sealed record Iec61400Pll(double Tpll, double Upll1, double Upll2);

/// <summary>RC5 WP23: the control measurement filters (Dynawo Measurements, control set: tUcFilt, tPcFilt, tQcFilt).</summary>
/// <param name="Tufilt">Voltage filter (s, 0 or above).</param>
/// <param name="Tpfilt">Active power filter (s, 0 or above).</param>
/// <param name="Tqfilt">Reactive power filter (s, 0 or above).</param>
public sealed record Iec61400Measurement(double Tufilt, double Tpfilt, double Tqfilt);

/// <summary>RC5 WP23: the grid protection (CGMES WindProtectionIEC; Dynawo GridProtection2020 and BaseGridProtection). Each table
/// gives the time (s) the unit may stay beyond its threshold as a function of the filtered level; a trip disconnects the unit
/// for the rest of the run.</summary>
/// <param name="Uover">Over-voltage threshold (pu).</param>
/// <param name="Uunder">Under-voltage threshold (pu).</param>
/// <param name="Fover">Over-frequency threshold (pu of nominal).</param>
/// <param name="Funder">Under-frequency threshold (pu of nominal).</param>
/// <param name="TUover">Allowed time against the voltage above <see cref="Uover"/>. This and the three tables below follow
/// Dynawo's 2020 tables under the CGMES WindProtectionIEC names (CGMES 2.4.15 gives these names scalar times).</param>
/// <param name="TUunder">Allowed time against the voltage below <see cref="Uunder"/>.</param>
/// <param name="TFover">Allowed time against the frequency above <see cref="Fover"/>.</param>
/// <param name="TFunder">Allowed time against the frequency below <see cref="Funder"/>.</param>
/// <param name="Tufilt">Protection voltage filter (s, 0 or above; Dynawo tUpFilt).</param>
/// <param name="Tffilt">Protection frequency filter (s, 0 or above; Dynawo tfpFilt). The angle's derivative is lagged by
/// tffilt / 20 first, as Dynawo's Derivative block.</param>
/// <param name="Dfmax">Rate limit of the measured frequency (pu/s, above 0; Dynawo DfpMaxPu).</param>
public sealed record Iec61400Protection(double Uover, double Uunder, double Fover, double Funder,
    Iec61400Table TUover, Iec61400Table TUunder, Iec61400Table TFover, Iec61400Table TFunder,
    double Tufilt, double Tffilt, double Dfmax);

/// <summary>
/// RC5 WP23: one IEC 61400-27-1 style Type 4A or Type 4B wind turbine, 2020 edition, built from public sources (Dynawo MPL-2.0
/// and the CGMES 2.4.15 DY names; the IEC text was not used). Per unit on the unit's own rating (the generator's MVA base).
/// Set on a unit's <see cref="SubtransientInit.MachineData.Iec61400"/>; the RMS study then drives the unit with
/// <see cref="Iec61400State"/>, a current source at its terminal. Opt-in: a unit without it is untouched.
/// </summary>
/// <param name="Type">Type 4A or Type 4B.</param>
/// <param name="Edition">The edition of the model structure: 2020 only (the 2015 structure is not built).</param>
/// <param name="Gen">The generator system.</param>
/// <param name="PControl">The active power control.</param>
/// <param name="QControl">The reactive power control.</param>
/// <param name="CurrentLimit">The current limiter.</param>
/// <param name="Mechanical">The two-mass drive train: required for Type 4B, refused for Type 4A.</param>
/// <param name="Pll">The phase-locked loop.</param>
/// <param name="Measurement">The control measurement filters.</param>
/// <param name="Protection">The grid protection, or null for none.</param>
public sealed record Iec61400Data(Iec61400TurbineType Type, int Edition, Iec61400GenSystem Gen, Iec61400PControl PControl,
    Iec61400QControl QControl, Iec61400CurrentLimit CurrentLimit, Iec61400Mechanical? Mechanical, Iec61400Pll Pll,
    Iec61400Measurement Measurement, Iec61400Protection? Protection)
{
    /// <summary>The one edition built.</summary>
    public const int SupportedEdition = 2020;

    /// <summary>The source basis, as results state it.</summary>
    public const string SourceNote = "implemented from public open-source implementations (Dynawo, MPL-2.0) and the CGMES DY definitions; "
        + "not checked against the IEC 61400-27-1 text";

    /// <summary>A short name for reports, such as "IEC 61400-27-1 Type 4B (2020 edition, public-source build)".</summary>
    public string Description => $"IEC 61400-27-1 Type {(Type == Iec61400TurbineType.Type4A ? "4A" : "4B")} ({Edition.ToString(CultureInfo.InvariantCulture)} edition, public-source build)";

    /// <summary>Check every value; throws <see cref="ArgumentException"/> listing every problem.</summary>
    public static void Validate(Iec61400Data data)
    {
        IReadOnlyList<string> problems = Problems(data);
        if (problems.Count > 0)
            throw new ArgumentException("IEC 61400 unit: " + string.Join(" ", problems), nameof(data));
    }

    /// <summary>Every problem with the values, in a fixed order; empty when the data is valid.</summary>
    public static IReadOnlyList<string> Problems(Iec61400Data data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var p = new List<string>();
        void Finite(string n, double v) { if (!double.IsFinite(v)) p.Add($"{n} = {Num(v)} is not finite."); }
        void NonNeg(string n, double v) { if (!(v >= 0.0) || !double.IsFinite(v)) p.Add($"{n} = {Num(v)} must be a finite number, 0 or above."); }
        void Pos(string n, double v) { if (!(v > 0.0) || double.IsNaN(v)) p.Add($"{n} = {Num(v)} must be above 0."); }
        void Neg(string n, double v) { if (!(v < 0.0) || double.IsNaN(v)) p.Add($"{n} = {Num(v)} must be below 0."); }

        if (data.Edition != SupportedEdition)
            p.Add($"edition {data.Edition.ToString(CultureInfo.InvariantCulture)}: only the 2020 edition structure is built (the 2015 structure is not).");
        if (data.Gen is null || data.PControl is null || data.QControl is null || data.CurrentLimit is null || data.Pll is null || data.Measurement is null)
        {
            p.Add("the generator system, P control, Q control, current limiter, PLL and measurement are all required.");
            return p;
        }
        if (data.Type == Iec61400TurbineType.Type4B && data.Mechanical is null) p.Add("a Type 4B unit needs the two-mass drive train (mechanical).");
        if (data.Type == Iec61400TurbineType.Type4A && data.Mechanical is not null) p.Add("a Type 4A unit has no drive train model; remove mechanical.");

        Iec61400GenSystem g = data.Gen;
        Pos("dipmax", g.Dipmax); Neg("diqmin", g.Diqmin); Pos("diqmax", g.Diqmax); NonNeg("tg", g.Tg);

        Iec61400PControl pc = data.PControl;
        Pos("dpmax", pc.Dpmax); NonNeg("tpord", pc.Tpord); Pos("dprefmax", pc.Dprefmax); Neg("dprefmin", pc.Dprefmin);
        NonNeg("tpwtref", pc.Tpwtref); NonNeg("tpaero", pc.Tpaero); Finite("updip", pc.Updip);

        Iec61400QControl q = data.QControl;
        if (!Enum.IsDefined(q.Mode)) p.Add($"windQcontrolModesType {((int)q.Mode).ToString(CultureInfo.InvariantCulture)} is not 0 to 4.");
        if (q.FrtMode is < 0 or > 2) p.Add($"windLVRTQcontrolModesType {q.FrtMode.ToString(CultureInfo.InvariantCulture)}: modes 0, 1 and 2 are built (mode 3 is a user hook, not built).");
        foreach ((string n, double v) in new[] { ("iqh1", q.Iqh1), ("iqmax", q.Iqmax), ("iqmin", q.Iqmin), ("iqpost", q.Iqpost), ("kpq", q.Kpq), ("kpu", q.Kpu),
            ("kpufrt", q.Kpufrt), ("kqv", q.Kqv), ("qmax", q.Qmax), ("qmin", q.Qmin), ("rdroop", q.Rdroop), ("xdroop", q.Xdroop), ("umax", q.Umax),
            ("umin", q.Umin), ("uqdip", q.Uqdip), ("uqrise", q.Uqrise), ("uref0", q.Uref0) })
            Finite(n, v);
        NonNeg("kiq", q.Kiq); NonNeg("kiu", q.Kiu); NonNeg("tpost", q.Tpost); NonNeg("tqord", q.Tqord); Pos("tuss", q.Tuss);
        if (!(q.Udb1 <= 0.0)) p.Add($"udb1 = {Num(q.Udb1)} must be 0 or below.");
        if (!(q.Udb2 >= 0.0) || !double.IsFinite(q.Udb2)) p.Add($"udb2 = {Num(q.Udb2)} must be 0 or above.");
        if (!(q.Iqmin < q.Iqmax)) p.Add($"iqmin = {Num(q.Iqmin)} must be below iqmax = {Num(q.Iqmax)}.");
        if (!(q.Iqmin < q.Iqh1)) p.Add($"iqmin = {Num(q.Iqmin)} must be below iqh1 = {Num(q.Iqh1)}.");
        if (!(q.Qmin < q.Qmax)) p.Add($"qmin = {Num(q.Qmin)} must be below qmax = {Num(q.Qmax)}.");
        if (!(q.Umin < q.Umax)) p.Add($"umin = {Num(q.Umin)} must be below umax = {Num(q.Umax)}.");
        if (!(q.Uqdip < q.Uqrise)) p.Add($"uqdip = {Num(q.Uqdip)} must be below uqrise = {Num(q.Uqrise)}.");

        Iec61400CurrentLimit c = data.CurrentLimit;
        Pos("imax", c.Imax); Pos("imaxdip", c.Imaxdip); Finite("kpqu", c.Kpqu); Finite("upqumax", c.Upqumax);
        if (c.Ipvdl is null || c.Iqvdl is null) p.Add("the current limiter needs both tables (ipvdl and iqvdl).");
        else { c.Ipvdl.Check("ipvdl", p); c.Iqvdl.Check("iqvdl", p); }

        if (data.Mechanical is { } m)
        {
            NonNeg("cdrt", m.Cdrt); Pos("hgen", m.Hgen); Pos("hwtr", m.Hwtr); Pos("kdrt", m.Kdrt);
            Finite("hgen", m.Hgen); Finite("hwtr", m.Hwtr); Finite("kdrt", m.Kdrt);
        }

        Iec61400Pll pll = data.Pll;
        NonNeg("tpll", pll.Tpll); Finite("upll1", pll.Upll1); Finite("upll2", pll.Upll2);
        if (!(pll.Upll2 <= pll.Upll1)) p.Add($"upll2 = {Num(pll.Upll2)} must be at most upll1 = {Num(pll.Upll1)}.");

        Iec61400Measurement me = data.Measurement;
        NonNeg("tufilt", me.Tufilt); NonNeg("tpfilt", me.Tpfilt); NonNeg("tqfilt", me.Tqfilt);

        if (data.Protection is { } pr)
        {
            Finite("uover", pr.Uover); Finite("uunder", pr.Uunder); Finite("fover", pr.Fover); Finite("funder", pr.Funder);
            if (!(pr.Uunder < pr.Uover)) p.Add($"uunder = {Num(pr.Uunder)} must be below uover = {Num(pr.Uover)}.");
            if (!(pr.Funder < pr.Fover)) p.Add($"funder = {Num(pr.Funder)} must be below fover = {Num(pr.Fover)}.");
            if (pr.TUover is null || pr.TUunder is null || pr.TFover is null || pr.TFunder is null) p.Add("the protection needs all four time tables.");
            else
            {
                pr.TUover.Check("tuover", p); pr.TUunder.Check("tuunder", p); pr.TFover.Check("tfover", p); pr.TFunder.Check("tfunder", p);
            }
            NonNeg("protection tufilt", pr.Tufilt); NonNeg("protection tffilt", pr.Tffilt); Pos("dfmax", pr.Dfmax);
        }
        return p;
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
