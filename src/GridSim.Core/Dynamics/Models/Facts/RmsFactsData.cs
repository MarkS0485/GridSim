using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// WP6 (RC5): the RMS dynamic model of a static var compensator or STATCOM held as a generator of the case (the way PSS/E
/// holds an SVC or a STATCOM that has a machine record). Attach it through <see cref="SubtransientInit.MachineData.Facts"/>:
/// the unit is then converter plant in the RMS study (<see cref="SubtransientInit.IsConverterUnit"/>), it injects reactive
/// power only (its scheduled active power must be 0) and its power-flow reactive output is its initial output. The machine
/// fields of the record are not read for it. The two models are <see cref="Csvgn1Data"/> (PSS/E CSVGN1) and
/// <see cref="Statcom1Data"/> (GridSim STATCOM1).
/// </summary>
public abstract record RmsFactsData
{
    /// <summary>The model name, for traces and errors.</summary>
    public abstract string Model { get; }

    /// <summary>Refuse a value the model cannot integrate, naming <paramref name="who"/>.</summary>
    internal abstract void Validate(string who);
}

/// <summary>
/// WP6 (RC5): PSS/E CSVGN1, the static var compensator, from the block structure of the OpenIPSL
/// <c>Electrical/Banks/PSSE/CSVGN1.mo</c> source with the parameter names of the PowerWorld and NREL PowerSystems.jl pages.
/// The output is the reactor fraction x (pu of MBASE):
/// <code>
///   e  = |V| - Vref
///   u1 = K (1 + s T1) / (1 + s T3) e
///   u2 = (1 + s T2) / (1 + s T4) u1,   limited to [VMIN, VMAX]   (T2 = 0: the lag 1 / (1 + s T4), non-windup)
///   x  = 1 / (1 + s T5) u2,             limited to [RMIN / MBASE, 1] (non-windup)
///   Y  = CBASE / Sb - x MBASE / Sb      the compensator's susceptance (system pu, capacitive positive), Q = Y |V|^2
/// </code>
/// The voltage reference is set so the power-flow susceptance is an equilibrium: x0 = (CBASE / Sb - Y0) / (MBASE / Sb),
/// Vref = V0 - x0 / K. A power-flow point outside the model's range is refused. Inside its limits the steady state is the
/// droop Y = Y0 - K (MBASE / Sb) (|V| - V0).
/// </summary>
/// <param name="K">Steady-state gain (pu of MBASE per pu voltage), above 0.</param>
/// <param name="T1">Lead time constant of the first block (s), 0 or above; above 0 needs T3 above 0.</param>
/// <param name="T2">Lead time constant of the second block (s), 0 or above; above 0 needs T4 above 0.</param>
/// <param name="T3">Lag time constant of the first block (s), 0 or above.</param>
/// <param name="T4">Lag time constant of the second block (s), 0 or above.</param>
/// <param name="T5">Thyristor lag (s), 0 or above.</param>
/// <param name="RminMvar">RMIN, the smallest reactor output (Mvar), 0 to MBASE.</param>
/// <param name="Vmax">VMAX, the upper limit of the second block (pu of MBASE).</param>
/// <param name="Vmin">VMIN, its lower limit.</param>
/// <param name="CbaseMvar">CBASE, the fixed capacitor bank (Mvar), 0 or above.</param>
/// <param name="MbaseMva">MBASE, the reactor rating (MVA); from a DYR file, the machine's MBASE.</param>
public sealed record Csvgn1Data(double K, double T1, double T2, double T3, double T4, double T5, double RminMvar, double Vmax, double Vmin,
    double CbaseMvar, double MbaseMva) : RmsFactsData
{
    /// <inheritdoc />
    public override string Model => "CSVGN1";

    internal override void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("K", K), ("T1", T1), ("T2", T2), ("T3", T3), ("T4", T4), ("T5", T5), ("RMIN", RminMvar),
            ("VMAX", Vmax), ("VMIN", Vmin), ("CBASE", CbaseMvar), ("MBASE", MbaseMva) })
            LoadChecks.Finite(who, n, v);
        if (!(K > 0)) throw LoadChecks.OutOfRange(nameof(K), K, $"{who}: K must be above 0.");
        foreach ((string n, double v) in new[] { ("T1", T1), ("T2", T2), ("T3", T3), ("T4", T4), ("T5", T5) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
        if (T1 > 0 && !(T3 > ControlBlocks.InstantThresholdSeconds))
            throw LoadChecks.OutOfRange(nameof(T3), T3, $"{who}: a lead T1 above 0 needs a lag T3 above 0 (a pure lead is not proper).");
        if (T2 > 0 && !(T4 > ControlBlocks.InstantThresholdSeconds))
            throw LoadChecks.OutOfRange(nameof(T4), T4, $"{who}: a lead T2 above 0 needs a lag T4 above 0 (a pure lead is not proper).");
        if (!(MbaseMva > 0)) throw LoadChecks.OutOfRange(nameof(MbaseMva), MbaseMva, $"{who}: MBASE must be above 0.");
        if (!(CbaseMvar >= 0)) throw LoadChecks.OutOfRange(nameof(CbaseMvar), CbaseMvar, $"{who}: CBASE must be 0 or above.");
        if (!(RminMvar >= 0 && RminMvar <= MbaseMva)) throw LoadChecks.OutOfRange(nameof(RminMvar), RminMvar, $"{who}: RMIN must be from 0 to MBASE.");
        if (!(Vmax >= Vmin)) throw LoadChecks.OutOfRange(nameof(Vmax), Vmax, $"{who}: VMAX must be at least VMIN.");
    }
}

/// <summary>
/// WP6 (RC5): GridSim STATCOM1, a simplified voltage-regulating STATCOM after the STATCOM block of the WECC report
/// <i>Static Var System Modeling for Power Flow and Dynamics</i> (August 2011) and the SVSMO3 structure. The state is the
/// reactive current Iq (pu of MBASE, capacitive positive):
/// <code>
///   e      = Vref - |V| - Xs Iq                  Xs the slope (droop)
///   Iq_cmd = Kp e + Ki / s e                     the integrator non-windup in [-ImaxInd, ImaxCap]
///   Iq     = 1 / (1 + s Tc) Iq_cmd,              limited to [-ImaxInd, ImaxCap] (Tc = 0: algebraic)
///   I      = -j Iq (MBASE / Sb) V / |V|          so Q = |V| Iq MBASE / Sb: at the limit Q falls linearly with |V|
/// </code>
/// Initialisation: Iq0 = Q0 / (|V0| MBASE / Sb); the integrator holds Iq0 and Vref = |V0| + Xs Iq0. With Ki = 0 (a
/// proportional controller, Kp above 0) the integrator holds 0 and the offset is taken by Vref = |V0| + Xs Iq0 + Iq0 / Kp.
/// A power-flow current beyond the limits is refused.
/// </summary>
/// <param name="MbaseMva">Rating (MVA), above 0.</param>
/// <param name="Xs">Slope (pu voltage per pu current on MBASE), 0 or above.</param>
/// <param name="Kp">Proportional gain (pu current per pu voltage), 0 or above.</param>
/// <param name="Ki">Integral gain (pu current per pu voltage per s), 0 or above; Kp and Ki cannot both be 0.</param>
/// <param name="Tc">Current lag (s), 0 or above.</param>
/// <param name="ImaxCap">Capacitive current limit (pu of MBASE), above 0.</param>
/// <param name="ImaxInd">Inductive current limit (pu of MBASE), above 0.</param>
public sealed record Statcom1Data(double MbaseMva, double Xs, double Kp, double Ki, double Tc, double ImaxCap, double ImaxInd) : RmsFactsData
{
    /// <inheritdoc />
    public override string Model => "STATCOM1";

    internal override void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("MBASE", MbaseMva), ("Xs", Xs), ("Kp", Kp), ("Ki", Ki), ("Tc", Tc), ("ImaxCap", ImaxCap), ("ImaxInd", ImaxInd) })
            LoadChecks.Finite(who, n, v);
        if (!(MbaseMva > 0)) throw LoadChecks.OutOfRange(nameof(MbaseMva), MbaseMva, $"{who}: MBASE must be above 0.");
        foreach ((string n, double v) in new[] { ("Xs", Xs), ("Kp", Kp), ("Ki", Ki), ("Tc", Tc) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
        if (Kp == 0 && Ki == 0) throw LoadChecks.OutOfRange(nameof(Ki), Ki, $"{who}: Kp and Ki are both 0, so nothing regulates the voltage.");
        if (!(ImaxCap > 0)) throw LoadChecks.OutOfRange(nameof(ImaxCap), ImaxCap, $"{who}: ImaxCap must be above 0.");
        if (!(ImaxInd > 0)) throw LoadChecks.OutOfRange(nameof(ImaxInd), ImaxInd, $"{who}: ImaxInd must be above 0.");
    }
}

/// <summary>WP6 (RC5): a change of a link's order at <paramref name="AtSeconds"/>: the power order (MW, PSS/E's sign: drawn at
/// the end that holds it) or, for an LCC current order, the current order (A).</summary>
public sealed record RmsOrderStep(double AtSeconds, double Value);

/// <summary>
/// WP6 (RC5): GridSim LCC1, a quasi-steady two-terminal LCC HVDC model after PSS/E CDC4T (Kundur, <i>Power System
/// Stability and Control</i>, sections 10.6 and 10.7). The DC current follows its order through the lag TIDC; the order is
/// the power order (through the optional lag Tp) over the measured DC voltage (lag TVDC; at the end that holds the power
/// order), held between C0 and the VDCOL. The converters are the steady-state equations of <c>LccDcLinks</c> at the
/// power-flow taps (frozen in the run), the inverter at the power-flow extinction angle, the rectifier's firing angle
/// following; below ALFDY the rectifier sits at ALFDY and the inverter takes the current at the order less the margin.
/// When the rectifier's AC voltage falls below VBLOCK the link blocks (no current); it unblocks once the voltage is above
/// VUNBL and it has been blocked for TBLOCK, its current order then ramping up at CRAMP and the VDCOL voltage at VRAMP.
/// </summary>
/// <param name="AlphaMinDeg">ALFDY, the minimum firing angle in the run (degrees).</param>
/// <param name="GammaMinDeg">GAMDY, the minimum extinction angle in the run (degrees). The inverter runs at the power-flow
/// extinction angle; one that differs from GAMDY is kept (so the start is an equilibrium) and noted.</param>
/// <param name="Tvdc">TVDC, the DC voltage measurement lag (s), 0 or above.</param>
/// <param name="Tidc">TIDC, the DC current lag (s), 0 or above (0: the current is its order).</param>
/// <param name="VblockPu">VBLOCK, the rectifier AC voltage below which the link blocks (pu); 0 never blocks.</param>
/// <param name="VunblockPu">VUNBL, the rectifier AC voltage above which it unblocks (pu).</param>
/// <param name="TblockSeconds">TBLOCK, the shortest time blocked (s).</param>
/// <param name="VrampPuPerSecond">VRAMP, the rate the VDCOL voltage recovers after unblocking (pu of the initial measured DC
/// voltage per s); 0 or below is a step.</param>
/// <param name="CrampPuPerSecond">CRAMP, the rate the current order recovers after unblocking (pu of the initial DC current
/// per s); 0 or below is a step.</param>
/// <param name="C0Amps">C0, the minimum current order (A).</param>
/// <param name="V1Kv">VDCOL point 1 voltage (kV); the limit is C1 at and below it.</param>
/// <param name="C1Amps">VDCOL point 1 current (A).</param>
/// <param name="V2Kv">VDCOL point 2 voltage (kV).</param>
/// <param name="C2Amps">VDCOL point 2 current (A).</param>
/// <param name="V3Kv">VDCOL point 3 voltage (kV); at and above it there is no VDCOL limit.</param>
/// <param name="C3Amps">VDCOL point 3 current (A).</param>
public sealed record Lcc1Data(double AlphaMinDeg, double GammaMinDeg, double Tvdc, double Tidc, double VblockPu, double VunblockPu,
    double TblockSeconds, double VrampPuPerSecond, double CrampPuPerSecond, double C0Amps,
    double V1Kv, double C1Amps, double V2Kv, double C2Amps, double V3Kv, double C3Amps)
{
    /// <summary>GridSim's own power-order lag Tp (s); 0 (the default) applies an order step at once.</summary>
    public double Tp { get; init; }

    /// <summary>The floor on the measured DC voltage a power order is divided by, as a fraction of its initial value (0.1 by
    /// default), so a collapsed DC voltage cannot drive the current order to infinity.</summary>
    public double PowerOrderVoltageFloorPu { get; init; } = 0.1;

    /// <summary>Order changes during the run, in time order (none by default).</summary>
    public IReadOnlyList<RmsOrderStep> OrderSteps { get; init; } = Array.Empty<RmsOrderStep>();

    internal void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("ALFDY", AlphaMinDeg), ("GAMDY", GammaMinDeg), ("TVDC", Tvdc), ("TIDC", Tidc), ("VBLOCK", VblockPu),
            ("VUNBL", VunblockPu), ("TBLOCK", TblockSeconds), ("VRAMP", VrampPuPerSecond), ("CRAMP", CrampPuPerSecond), ("C0", C0Amps),
            ("V1", V1Kv), ("C1", C1Amps), ("V2", V2Kv), ("C2", C2Amps), ("V3", V3Kv), ("C3", C3Amps), ("Tp", Tp), ("PowerOrderVoltageFloorPu", PowerOrderVoltageFloorPu) })
            LoadChecks.Finite(who, n, v);
        foreach ((string n, double v) in new[] { ("TVDC", Tvdc), ("TIDC", Tidc), ("TBLOCK", TblockSeconds), ("C0", C0Amps), ("Tp", Tp), ("VBLOCK", VblockPu) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
        if (!(AlphaMinDeg >= 0 && AlphaMinDeg < 90)) throw LoadChecks.OutOfRange(nameof(AlphaMinDeg), AlphaMinDeg, $"{who}: ALFDY must be from 0 to below 90 degrees.");
        if (!(VunblockPu >= VblockPu)) throw LoadChecks.OutOfRange(nameof(VunblockPu), VunblockPu, $"{who}: VUNBL must be at least VBLOCK.");
        if (!(V1Kv <= V2Kv && V2Kv <= V3Kv)) throw LoadChecks.OutOfRange(nameof(V2Kv), V2Kv, $"{who}: the VDCOL voltages must rise, V1 <= V2 <= V3.");
        if (!(PowerOrderVoltageFloorPu > 0 && PowerOrderVoltageFloorPu <= 1)) throw LoadChecks.OutOfRange(nameof(PowerOrderVoltageFloorPu), PowerOrderVoltageFloorPu, $"{who}: the power-order voltage floor must be above 0 and at most 1.");
        OrderStepChecks.Check(who, OrderSteps);
    }
}

/// <summary>Which current a VSC converter keeps at its current limit.</summary>
public enum VscCurrentPriority
{
    /// <summary>Reactive current first: Iq = clip(Iq_cmd, +-Imax), Id = clip(Id_cmd, +-sqrt(Imax^2 - Iq^2)).</summary>
    Reactive,
    /// <summary>Active current first: Id = clip(Id_cmd, +-Imax), Iq = clip(Iq_cmd, +-sqrt(Imax^2 - Id^2)).</summary>
    Active,
}

/// <summary>
/// WP6 (RC5): GridSim VSC1, a generic P/Q-controlled two-terminal VSC HVDC model, simplified from the WECC <i>Proposed
/// Generic VSC-HVDC Model</i> memo (rev 11). The power converter's active power follows its order through 1 / (1 + s Tp)
/// (the power P_f it injects, system pu; Id_cmd = P_f / |V|). Each converter's reactive side is either a fixed order
/// (Kv = 0: its power-flow Q through 1 / (1 + s Tq), Iq_cmd = Q_f / |V|) or AC voltage control (Kv above 0: Iq_cmd =
/// lag(Iq0 + Kv (|V0| - |V|), Tq)). The current is limited to Imax with the chosen priority. The DC-voltage converter is
/// ideal (algebraic DC voltage): it delivers the power converter's DC power less the line and converter losses,
/// max(MINLOSS, A + B |Idc|) per converter as in <c>VscDcLinks.SolveDc</c>, on the held P_f.
/// </summary>
/// <param name="Tp">Active power lag (s), 0 or above.</param>
/// <param name="Tq">Reactive lag (s), 0 or above.</param>
/// <param name="ImaxPu">Current limit of each converter (pu of <paramref name="MbaseMva"/>), above 0.</param>
/// <param name="MbaseMva">Converter rating (MVA), above 0.</param>
public sealed record Vsc1Data(double Tp, double Tq, double ImaxPu, double MbaseMva)
{
    /// <summary>The current-limit priority (reactive by default).</summary>
    public VscCurrentPriority Priority { get; init; } = VscCurrentPriority.Reactive;

    /// <summary>The first converter's AC voltage gain Kv (pu current per pu voltage); 0 (the default) holds its reactive order.</summary>
    public double FirstKv { get; init; }

    /// <summary>The second converter's AC voltage gain Kv; 0 (the default) holds its reactive order.</summary>
    public double SecondKv { get; init; }

    /// <summary>WP16: when true, the DC side also follows the DC voltage converter's own current limit. When that converter
    /// cannot deliver the DC power at its AC bus (a fault there, say), it delivers what its current limit allows and the
    /// power converter's power is cut to match, plus the line and converter losses, so the DC side still balances. False
    /// (the default) keeps the earlier behaviour bit for bit: the voltage converter's limit is not fed back. The power
    /// converter's own current limit is followed either way.</summary>
    public bool DcFollowsLimitedPower { get; init; }

    /// <summary>Power order changes during the run, in time order (MW drawn at the power converter, the sign of <c>VscConverter.DcSetpoint</c>; PSS/E's DCSET is the negative of this).</summary>
    public IReadOnlyList<RmsOrderStep> OrderSteps { get; init; } = Array.Empty<RmsOrderStep>();

    internal void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("Tp", Tp), ("Tq", Tq), ("Imax", ImaxPu), ("MBASE", MbaseMva), ("FirstKv", FirstKv), ("SecondKv", SecondKv) })
            LoadChecks.Finite(who, n, v);
        foreach ((string n, double v) in new[] { ("Tp", Tp), ("Tq", Tq), ("FirstKv", FirstKv), ("SecondKv", SecondKv) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
        if (!(ImaxPu > 0)) throw LoadChecks.OutOfRange(nameof(ImaxPu), ImaxPu, $"{who}: Imax must be above 0.");
        if (!(MbaseMva > 0)) throw LoadChecks.OutOfRange(nameof(MbaseMva), MbaseMva, $"{who}: MBASE must be above 0.");
        OrderStepChecks.Check(who, OrderSteps);
    }
}

/// <summary>
/// WP6 (RC5): a network device of the case (an HVDC link, a static var compensator or a FACTS shunt element) with the RMS
/// model to drive it, bound by its name in the model. Attach a list through <see cref="RmsLoadedMachineData"/>. Without
/// device data the RMS study leaves every such device out, as it always has (its power-flow injection is not in the
/// dynamic network); with it the device is a converter node of the network, initialised on its power-flow state. Kinds:
/// <see cref="SvcDeviceData"/>, <see cref="StatcomDeviceData"/>, <see cref="LccLinkDynamicsData"/>, <see cref="VscLinkDynamicsData"/>, <see cref="MtdcGridDynamicsData"/> (WP16).
/// </summary>
/// <param name="DeviceName">The device's name in the model (<c>StaticVarCompensator.Name</c>, <c>FactsDevice.Name</c>,
/// <c>LccDcLink.Name</c>, <c>VscDcLink.Name</c> or <c>MtdcLink.Name</c>).</param>
public abstract record RmsNetworkDeviceData(string DeviceName)
{
    /// <summary>The kind, for messages ("static var compensator", ...).</summary>
    public abstract string Kind { get; }

    /// <summary>The model name.</summary>
    public abstract string Model { get; }

    internal string Label => $"{Model} on {Kind} '{DeviceName}'";
}

/// <summary>WP6 (RC5): a static var compensator of the model (<c>GridModel.StaticVarCompensators</c>, by name) driven by
/// CSVGN1. Its initial susceptance BINIT is already in the bus shunt, so the model injects only its susceptance above
/// BINIT.</summary>
public sealed record SvcDeviceData(string DeviceName, Csvgn1Data Svc) : RmsNetworkDeviceData(DeviceName)
{
    /// <inheritdoc />
    public override string Kind => "static var compensator";

    /// <inheritdoc />
    public override string Model => Svc.Model;
}

/// <summary>WP6 (RC5): a FACTS device of the model in STATCOM mode (shunt element only; by name) driven by STATCOM1. A
/// UPFC or a series-impedance device is refused: it stays at its power-flow injection.</summary>
public sealed record StatcomDeviceData(string DeviceName, Statcom1Data Statcom) : RmsNetworkDeviceData(DeviceName)
{
    /// <inheritdoc />
    public override string Kind => "FACTS device";

    /// <inheritdoc />
    public override string Model => Statcom.Model;
}

/// <summary>WP6 (RC5): a two-terminal LCC link of the model (by name) driven by LCC1.</summary>
public sealed record LccLinkDynamicsData(string DeviceName, Lcc1Data Lcc) : RmsNetworkDeviceData(DeviceName)
{
    /// <inheritdoc />
    public override string Kind => "LCC link";

    /// <inheritdoc />
    public override string Model => "LCC1";
}

/// <summary>WP6 (RC5): a two-terminal VSC link of the model (by name) driven by VSC1.</summary>
public sealed record VscLinkDynamicsData(string DeviceName, Vsc1Data Vsc) : RmsNetworkDeviceData(DeviceName)
{
    /// <inheritdoc />
    public override string Kind => "VSC link";

    /// <inheritdoc />
    public override string Model => "VSC1";
}

/// <summary>WP6: one device's recorded states over a run (one sample per kernel sample), for the traces and the
/// <c>rms-devices.csv</c> export.</summary>
/// <param name="Name">The device name.</param>
/// <param name="Model">The model name.</param>
/// <param name="StateNames">The state names, in column order.</param>
/// <param name="Samples">Per sample, the state values in <paramref name="StateNames"/> order.</param>
public sealed record RmsDeviceTrace(string Name, string Model, IReadOnlyList<string> StateNames, IReadOnlyList<double[]> Samples);

/// <summary>WP6: a converter model that records device states at each commit (one per kernel sample).</summary>
internal interface IRmsDeviceStateRecorder
{
    /// <summary>The recorded trace, or null for a model whose states another terminal records (a link's second end).</summary>
    RmsDeviceTrace? Trace { get; }
}

/// <summary>WP6: the initialised form of a device terminal: a factory of fresh run models (one per kernel run).</summary>
internal abstract class RmsDevicePlan
{
    /// <summary>The kernel's model of this terminal for one run; <paramref name="nodeBuses"/> is that run's converter-node
    /// order (one list per run, so the two ends of a link find each other's shared state through it).</summary>
    internal abstract IRmsConverterModel CreateModel(IReadOnlyList<int> nodeBuses);
}

internal static class OrderStepChecks
{
    internal static void Check(string who, IReadOnlyList<RmsOrderStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            RmsOrderStep s = steps[i] ?? throw new ArgumentException($"{who}: order step {i} is null.");
            if (!double.IsFinite(s.AtSeconds) || s.AtSeconds < 0 || !double.IsFinite(s.Value))
                throw new ArgumentException($"{who}: order step {i} must have a finite time at or after 0 and a finite value.");
            if (i > 0 && s.AtSeconds < steps[i - 1].AtSeconds)
                throw new ArgumentException($"{who}: the order steps must be in time order.");
        }
    }

    /// <summary>The order in force for the step from <paramref name="t"/>: the last step whose time has arrived (the kernel's
    /// stage rule, within a millionth of the step), else <paramref name="initial"/>.</summary>
    internal static double At(IReadOnlyList<RmsOrderStep> steps, double t, double dt, double initial)
    {
        double v = initial;
        double eps = dt * MultiMachineSubtransient.StageTimeToleranceSteps;
        foreach (RmsOrderStep s in steps) if (t + eps >= s.AtSeconds) v = s.Value;
        return v;
    }
}

/// <summary>WP6: a terminal current from an injected complex power at a voltage (system pu): I = conj(S / V), 0 at a
/// collapsed voltage.</summary>
internal static class DeviceCurrents
{
    internal const double MinVoltagePu = 1e-6;

    internal static Complex FromPower(Complex s, Complex v) => v.Magnitude > MinVoltagePu ? Complex.Conjugate(s / v) : Complex.Zero;

    /// <summary>The active and reactive (exporting positive) current of an injection <paramref name="i"/> at <paramref name="v"/>.</summary>
    internal static (double Ip, double Iq) Split(Complex i, Complex v)
    {
        double mag = v.Magnitude;
        if (!(mag > 0.0)) return (0.0, 0.0);
        Complex rel = i * Complex.Conjugate(v / mag);
        return (rel.Real, -rel.Imaginary);
    }
}

/// <summary>WP16 (RC5): how a converter of an MTDC1 grid (<see cref="MtdcGridDynamicsData"/>) sets its DC power.</summary>
public enum MtdcControlMode
{
    /// <summary>A power order (its power-flow power, or the order steps) through the lag Tp.</summary>
    Power,
    /// <summary>Holds its DC bus voltage at the power-flow value (an ideal controller): its power is whatever balances the
    /// DC bus.</summary>
    DcVoltage,
    /// <summary>Voltage droop: P = P0 - Kd (Vdc - Vdc0) through the lag Tp, P0 its power-flow power (or the order steps).</summary>
    Droop,
}

/// <summary>
/// WP16 (RC5): the averaged MMC data of one MTDC1 converter (MMC1), after Saad et al., "Dynamic averaged and simplified
/// models for MMC-based HVDC transmission systems", IEEE Trans. Power Delivery 28(3), 2013. The six arms of N submodules
/// of capacitance C_SM store the energy of one equivalent DC capacitor C_eq = 6 C_SM / N. With the energy controller off
/// (Kp_W = Ki_W = 0, the default) that capacitor is lumped onto the converter's DC bus. With it on (the paper's energy
/// loop), the converter keeps its stored energy E apart: dE/dt = P_ac - P_dc, and the DC power is
/// P_dc = lag(P_ac + Kp_W (E - E0) + Ki_W integral(E - E0), Tw), so the energy returns to E0. The arm inductance adds
/// w L_arm / 2 to the converter's coupling reactance; the converter is a current source at its AC bus, so that reactance
/// changes its internal voltage (traced) and not its injected current.
/// </summary>
/// <param name="Submodules">N, submodules per arm, at least 1.</param>
/// <param name="SubmoduleCapacitanceFarads">C_SM (F), above 0.</param>
/// <param name="ArmInductanceHenries">L_arm (H), 0 or above.</param>
/// <param name="KpW">Energy controller proportional gain (per s), 0 or above.</param>
/// <param name="KiW">Energy controller integral gain (per s squared), 0 or above.</param>
/// <param name="Tw">Energy controller output lag (s), 0 or above.</param>
public sealed record Mmc1Data(int Submodules, double SubmoduleCapacitanceFarads, double ArmInductanceHenries, double KpW, double KiW, double Tw)
{
    /// <summary>C_eq = 6 C_SM / N (F).</summary>
    public double EquivalentCapacitanceFarads => 6.0 * SubmoduleCapacitanceFarads / Submodules;

    /// <summary>True when the energy controller is on (Kp_W or Ki_W above 0).</summary>
    public bool EnergyControl => KpW > 0 || KiW > 0;

    internal void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("C_SM", SubmoduleCapacitanceFarads), ("L_arm", ArmInductanceHenries), ("Kp_W", KpW), ("Ki_W", KiW), ("Tw", Tw) })
            LoadChecks.Finite(who, n, v);
        if (Submodules < 1) throw new ArgumentException($"{who}: Submodules (N, submodules per arm) is {Submodules.ToString(System.Globalization.CultureInfo.InvariantCulture)}; it must be at least 1.");
        if (!(SubmoduleCapacitanceFarads > 0)) throw LoadChecks.OutOfRange("C_SM", SubmoduleCapacitanceFarads, $"{who}: C_SM must be above 0.");
        foreach ((string n, double v) in new[] { ("L_arm", ArmInductanceHenries), ("Kp_W", KpW), ("Ki_W", KiW), ("Tw", Tw) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
    }
}

/// <summary>
/// WP16 (RC5): one converter of an MTDC1 grid, named by its AC bus. The AC side is VSC1's: a phase-locked current source
/// with a current limit of <paramref name="ImaxPu"/> on <paramref name="MbaseMva"/> and the chosen priority, its active
/// current from the DC power, its reactive side a fixed order (Kv = 0: the power-flow Q through Tq) or AC voltage control
/// (Kv above 0).
/// </summary>
/// <param name="ConverterBus">The converter's AC bus (<c>MtdcConverter.Bus</c>).</param>
/// <param name="Mode">How it sets its DC power.</param>
/// <param name="ImaxPu">Current limit (pu of <paramref name="MbaseMva"/>), above 0.</param>
/// <param name="MbaseMva">Converter rating (MVA), above 0.</param>
public sealed record MtdcTerminalControl(int ConverterBus, MtdcControlMode Mode, double ImaxPu, double MbaseMva)
{
    /// <summary>Active power lag Tp (s), 0 or above (Power and Droop).</summary>
    public double Tp { get; init; }

    /// <summary>Reactive lag Tq (s), 0 or above.</summary>
    public double Tq { get; init; }

    /// <summary>AC voltage gain Kv (pu current per pu voltage); 0 (the default) holds the reactive order.</summary>
    public double Kv { get; init; }

    /// <summary>Droop gain Kd (pu power per pu DC voltage), 0 or above (Droop only).</summary>
    public double DroopGain { get; init; }

    /// <summary>The converter's DC capacitor (microfarads), 0 or above; it adds to its DC bus capacitance.</summary>
    public double CapacitanceMicroFarads { get; init; }

    /// <summary>The current-limit priority (reactive by default).</summary>
    public VscCurrentPriority Priority { get; init; } = VscCurrentPriority.Reactive;

    /// <summary>Averaged MMC data; null (the default) makes the converter a plain two-level VSC terminal.</summary>
    public Mmc1Data? Mmc { get; init; }

    /// <summary>Changes of its power order during the run (MW sent into the DC grid, the sign of the power flow's
    /// <c>MtdcConverterResult.DrawnMw</c>), in time order. For a droop converter they move P0.</summary>
    public IReadOnlyList<RmsOrderStep> OrderSteps { get; init; } = Array.Empty<RmsOrderStep>();

    internal void Validate(string who)
    {
        foreach ((string n, double v) in new[] { ("Imax", ImaxPu), ("MBASE", MbaseMva), ("Tp", Tp), ("Tq", Tq), ("Kv", Kv), ("Kd", DroopGain), ("C", CapacitanceMicroFarads) })
            LoadChecks.Finite(who, n, v);
        if (!(ImaxPu > 0)) throw LoadChecks.OutOfRange(nameof(ImaxPu), ImaxPu, $"{who}: Imax must be above 0.");
        if (!(MbaseMva > 0)) throw LoadChecks.OutOfRange(nameof(MbaseMva), MbaseMva, $"{who}: MBASE must be above 0.");
        foreach ((string n, double v) in new[] { ("Tp", Tp), ("Tq", Tq), ("Kv", Kv), ("Kd", DroopGain), ("C", CapacitanceMicroFarads) })
            if (!(v >= 0)) throw LoadChecks.OutOfRange(n, v, $"{who}: {n} must be 0 or above.");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException($"{who}: Mode {(int)Mode} is not a known control mode (Power, DcVoltage or Droop).");
        if (Mode != MtdcControlMode.Droop && DroopGain != 0) throw LoadChecks.OutOfRange("Kd", DroopGain, $"{who}: a droop gain needs the Droop mode.");
        ArgumentNullException.ThrowIfNull(OrderSteps);
        if (Mode == MtdcControlMode.DcVoltage && OrderSteps.Count > 0) throw new ArgumentException($"{who}: a DC voltage converter takes no power order steps.");
        Mmc?.Validate(who);
        if (Mode == MtdcControlMode.DcVoltage && Mmc is { EnergyControl: true })
            throw new ArgumentException($"{who}: the MMC energy controller needs a Power or Droop converter; an ideal DC voltage converter takes its power from the DC balance.");
        OrderStepChecks.Check(who, OrderSteps);
    }
}

/// <summary>WP16 (RC5): extra capacitance on one DC bus of an MTDC1 grid (microfarads), such as half the capacitance of
/// each cable that ends there, or the capacitance of a DC bus with no converter.</summary>
public sealed record MtdcDcBusCapacitance(int DcBus, double MicroFarads);

/// <summary>
/// WP16 (RC5): a multi-terminal DC link of the model (<c>GridModel.MtdcLinks</c>, by name) driven by GridSim MTDC1, an RMS
/// DC grid: each DC bus integrates its stored energy, the DC lines and grounds are the link's resistances, and each
/// converter is a VSC1-style terminal set by <see cref="MtdcTerminalControl"/> (one per converter). Starts on the power
/// flow's DC voltages and converter powers.
/// </summary>
/// <param name="DeviceName">The link's name (<c>MtdcLink.Name</c>).</param>
/// <param name="Controls">One control per converter of the link, by AC bus.</param>
public sealed record MtdcGridDynamicsData(string DeviceName, IReadOnlyList<MtdcTerminalControl> Controls) : RmsNetworkDeviceData(DeviceName)
{
    /// <summary>The DC voltage base (kV); 0 (the default) takes the power-flow DC voltage at the voltage converter's DC bus.</summary>
    public double DcBaseKv { get; init; }

    /// <summary>Extra DC bus capacitances (none by default).</summary>
    public IReadOnlyList<MtdcDcBusCapacitance> BusCapacitances { get; init; } = Array.Empty<MtdcDcBusCapacitance>();

    /// <inheritdoc />
    public override string Kind => "multi-terminal DC link";

    /// <inheritdoc />
    public override string Model => "MTDC1";
}
