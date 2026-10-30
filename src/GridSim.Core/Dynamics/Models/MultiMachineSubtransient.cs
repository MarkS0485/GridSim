using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// A per-machine field controller (AVR, optionally with a stabiliser) for the subtransient multi-machine kernel
/// (R061). The kernel calls <see cref="Initialize"/> once with the machine's initial held field, then, once per
/// step, <see cref="Step"/> with the regulator error <c>Vref - Vt</c> and the machine's speed deviation (pu), and
/// uses the returned field voltage across the step. <see cref="RmsExciter"/> is the standard implementation
/// (SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, each with an optional PSS1A).
/// </summary>
public interface IRmsFieldController
{
    /// <summary>Pre-load every internal state so the controller sits in equilibrium at field voltage
    /// <paramref name="fieldVoltagePu"/> with zero speed deviation. Must fail loud if that field cannot be an
    /// equilibrium of the controller (for example outside its limits).</summary>
    void Initialize(double fieldVoltagePu);

    /// <summary>The steady regulator error <c>Vref - Vt</c> (pu) that holds the field at
    /// <paramref name="fieldVoltagePu"/>. The kernel adds it to the measured initial terminal voltage to form the
    /// voltage reference, so the undisturbed fleet is stationary.</summary>
    double EquilibriumErrorPu(double fieldVoltagePu);

    /// <summary>Advance one step of <paramref name="dtSeconds"/> with regulator error
    /// <paramref name="voltageErrorPu"/> (<c>Vref - Vt</c>, held across the step) and rotor speed deviation
    /// <paramref name="speedDeviationPu"/>; return the field voltage Efd (pu) to hold across the step.</summary>
    double Step(double voltageErrorPu, double speedDeviationPu, double dtSeconds);
}

/// <summary>
/// A field controller that also reads the machine's field current (an alternator-rectifier exciter whose rectifier
/// regulation and armature reaction depend on it, such as <see cref="AcExciter"/>). When
/// <see cref="NeedsFieldCurrent"/> is true the kernel calls the four-argument <see cref="Step"/> with the start-of-step
/// field current in the non-reciprocal Xad base, IFD = E'q + (Xd - X'd) Id + the d-axis saturation increment: the
/// quantity the E'q equation balances against Efd, so IFD = Efd at every equilibrium (which is how such a controller
/// initialises from the field alone). Otherwise the kernel takes the three-argument path, unchanged.
/// </summary>
public interface IRmsFieldCurrentController : IRmsFieldController
{
    /// <summary>Whether the controller needs the field current (the kernel then calls the four-argument step).</summary>
    bool NeedsFieldCurrent { get; }

    /// <summary>As <see cref="IRmsFieldController.Step"/>, with the start-of-step field current
    /// <paramref name="fieldCurrentPu"/> (held across the step).</summary>
    double Step(double voltageErrorPu, double speedDeviationPu, double fieldCurrentPu, double dtSeconds);
}

/// <summary>
/// A field controller that also reads the terminal voltage and current phasors (a static exciter fed from a potential
/// and/or compound source, such as <see cref="Esst4b"/>). When <see cref="NeedsTerminalQuantities"/> is true the kernel
/// calls the six-argument <see cref="Step"/> with the start-of-step field current (as for
/// <see cref="IRmsFieldCurrentController"/>), the terminal voltage V_t = E'' - jX'' I (pu) and the machine current I
/// (pu on the SYSTEM base, generator convention), both in the network frame; the controller converts the current to its
/// machine base itself.
/// </summary>
public interface IRmsTerminalFieldController : IRmsFieldCurrentController
{
    /// <summary>Whether the controller needs the terminal phasors (the kernel then calls the six-argument step).</summary>
    bool NeedsTerminalQuantities { get; }

    /// <summary>As <see cref="IRmsFieldCurrentController.Step"/>, with the terminal voltage
    /// <paramref name="terminalVoltagePu"/> and the machine current <paramref name="terminalCurrentSystemPu"/> (system
    /// base), held across the step.</summary>
    double Step(double voltageErrorPu, double speedDeviationPu, double fieldCurrentPu, Complex terminalVoltagePu,
        Complex terminalCurrentSystemPu, double dtSeconds);
}

/// <summary>
/// R061 rest (CF4, October 2026): machine <see cref="Machine"/> (its index in the kernel's machine list) is
/// disconnected from <see cref="AtSeconds"/> on (a generator trip). The network stage in force from that time must
/// already leave the machine out (no Norton admittance, zero admittance in the machine list, so it carries no current);
/// the kernel then sets its mechanical power to zero (the unit is off the system, its turbine shut), holds its field,
/// and leaves it out of the synchronism verdict (a disconnected rotor cannot lose synchronism with the system). The
/// trip takes effect at the same step a network stage at the same time does (the shared stage tolerance).
/// </summary>
public sealed record RmsMachineTrip(int Machine, double AtSeconds);

/// <summary>
/// The <b>salient / subtransient multi-machine</b> transient-stability model - the network generalisation of
/// the SMIB <see cref="SubtransientMachine"/>, built on the validated dq current-injection core
/// <see cref="SubtransientNetworkInterface"/>. Every machine carries the full six-state set (rotor angle and
/// speed, the transient EMFs <c>E'q</c>, <c>E'd</c> and the subtransient EMFs <c>E''q</c>, <c>E''d</c>) with
/// its own d- and q-axis reactances and time constants, and the machines are coupled through the reduced
/// admittance (with the round subtransient reactance <c>X''</c> folded in, as
/// <see cref="ClassicalInit"/> folds <c>X'd</c>).
///
/// <para>Per machine, on the system base (angles in rad, times in seconds):</para>
/// <code>
///   ddelta_i/dt    = omega_s * omega_i
///   domega_i/dt    = (Pm_i - Pe_i - D_i*omega_i) / (2 H_i)
///   T'do_i  dE'q_i/dt  = Efd_i - E'q_i - (Xd_i - X'd_i)*Id_i - E''q_i*Se_i(|E''_i|)
///   T''do_i dE''q_i/dt = E'q_i - E''q_i - (X'd_i - X''d_i)*Id_i
///   T'qo_i  dE'd_i/dt  = -E'd_i + (Xq_i - X'q_i)*Iq_i - E''d_i*Se_i(|E''_i|)*(Xq_i - Xl_i)/(Xd_i - Xl_i)
///   T''qo_i dE''d_i/dt = E'd_i - E''d_i + (X'q_i - X''q_i)*Iq_i
/// </code>
/// Each step, one <see cref="SubtransientNetworkInterface.Inject"/> gives every machine's air-gap power and
/// its dq stator currents from the subtransient EMFs and rotor angles; the four flux states advance on the
/// exact zero-order-hold <see cref="FirstOrderLag"/> (currents and transient EMFs frozen across the step), and
/// the coupled rotor swing advances by RK4 with the subtransient EMFs frozen, the network injection recomputed
/// at each sub-step as the angles move - the same operator splitting the SMIB and round-rotor models use.
///
/// <para>It collapses onto the trusted <see cref="ClassicalMultiMachine"/> as a reduction identity: with every
/// reactance equal to <c>X'd</c> and a matched held field, all four flux drivers vanish, the subtransient EMF
/// stays at its initial <c>E'q&#8736;delta</c>, and the fleet is the constant-<c>E'</c> classical swing - so a
/// fault-free run follows the classical kernel to 8 or 9 decimal places (only the Pe rounding differs). A faulted run
/// does not: the classical kernel reads the fault window at each RK4 stage time, while this one picks its network
/// once at the step start and holds it for all four stages. With no exciters the field is held per machine.
/// Per-machine AVRs are supported (R061): the terminal voltage is recovered each step as
/// <c>V_t = E'' - jX'' I</c> and fed either to a SEXS list (<c>exciters</c>, with an explicit voltage reference)
/// or to a general <see cref="IRmsFieldController"/> list (<c>fieldControllers</c>: SEXS, IEEET1, EXAC1, ESAC1A or ESST4B, optionally
/// with a PSS1A on the rotor speed, see <see cref="RmsExciter"/>), whose voltage reference is set from the
/// measured initial terminal voltage so the fleet starts at equilibrium.</para>
///
/// <para><b>Converter plant.</b> Converter-interfaced units (battery, PV, wind, VSC HVDC) have no rotor and no
/// EMF behind a reactance. When <c>converters</c> are passed, their terminal buses are kept as retained nodes
/// after the machine internal nodes (<see cref="ConverterNetwork"/>), each is an
/// <see cref="RmsConverterState"/> phase-locked current source, and the machines see the effective admittance
/// plus the coupling current the converters drive. With none, the kernel is exactly the machine-only one.</para>
/// </summary>
public static class MultiMachineSubtransient
{
    /// <summary>One machine's reactances and open-circuit time constants, all per-unit on the system base
    /// (so directly comparable to the reduced admittance), with an optional saturation curve. The round
    /// subtransient reactance <c>X''d = X''q</c> that the network interface requires is the value folded into
    /// the reduced admittance; the per-axis <c>Xdpp</c>/<c>Xqpp</c> here drive the damper flux decay.</summary>
    public sealed record Machine(
        double Xd, double Xdp, double Xdpp, double Xq, double Xqp, double Xqpp,
        double Tdop, double Tdopp, double Tqop, double Tqopp,
        SubtransientMachine.SaturationCurve? Saturation = null,
        double? SaturationQAxisScale = null)
    {
        /// <summary>N045: the GENROU q-axis saturation scale <c>(Xq - Xl) / (Xd - Xl)</c> on the machine's OWN reactances.
        /// <see cref="SubtransientInit"/> sets it from the machine data (before a unit transformer is folded into the
        /// reactances here); a machine built directly without it takes the leakage-free <c>Xq / Xd</c> of its own
        /// fields.</summary>
        public double EffectiveSaturationQAxisScale => SaturationQAxisScale ?? Xq / Xd;
    }

    /// <summary>Per-machine trajectory: rotor angle, speed and the four flux states through the run.</summary>
    public sealed record MachineTrace(
        IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double> DeltaRad,
        IReadOnlyList<double> SpeedPu,
        IReadOnlyList<double> EqpPu,
        IReadOnlyList<double> EdpPu,
        IReadOnlyList<double> EqppPu,
        IReadOnlyList<double> EddpPu);

    /// <summary>The run: per-machine traces, the synchronism verdict, and (when the fleet includes converter
    /// plant) per-converter traces.</summary>
    public sealed record Result(IReadOnlyList<MachineTrace> Machines, bool StayedInSynchronism,
        IReadOnlyList<RmsConverterTrace>? Converters = null, int ConverterIterationFailures = 0)
    {
        /// <summary>True when every step's converter algebraic iteration met its tolerance (always true with no
        /// converters). The iteration has a 50-pass cap; a step that hits it carries on with the last iterate, so
        /// <see cref="ConverterIterationFailures"/> counts those steps rather than hiding them (R084).</summary>
        public bool ConverterIterationsConverged => ConverterIterationFailures == 0;

        /// <summary>Steps whose converter iteration hit the cap with plain substitution and were then solved by the exact
        /// rescue (WECC REGC_A units, DECISIONS D38); a rescue that also fails counts in
        /// <see cref="ConverterIterationFailures"/>. Always 0 without WECC units.</summary>
        public int ConverterRescuedSteps { get; init; }
    }

    /// <summary>A temporary reduced admittance applied over a fault window (onset, cleared), as in the
    /// classical kernel.</summary>
    public sealed record Fault(double OnsetSeconds, double ClearSeconds, Complex[,] YReducedFaulted);

    /// <summary>One entry of a piecewise-constant network schedule: from <see cref="AtSeconds"/> the reduced
    /// admittance becomes <see cref="YReduced"/> (until the next stage). This carries the general event
    /// timeline an RMS study needs - a fault applied then cleared, a branch tripped (and left out), a
    /// switching sequence - where the single <see cref="Fault"/> window only reverts to the base network.</summary>
    public sealed record NetworkStage(double AtSeconds, Complex[,] YReduced);

    /// <summary>Fraction of a step within which a network stage time counts as reached (R249): absorbs the
    /// floating-point rounding of a stage time formed as onset + clearing, never a genuine sub-step offset.</summary>
    public const double StageTimeToleranceSteps = 1e-6;

    /// <summary>Cap on the per-step fixed-point iteration between converter currents and network voltages
    /// (R166). A step that does not settle within it is counted in the result's converter-iteration failures.</summary>
    public const int MaxConverterIterations = 50;

    /// <summary>The converter fixed point has settled when no converter terminal voltage moves by this much
    /// (pu, absolute phasor change) between iterations (R166).</summary>
    public const double ConverterVoltageTolerancePu = 1e-10;

    /// <summary>
    /// Integrate the fleet. Machine parameters are per-unit on the system base (aligned to the reduced
    /// admittance); <paramref name="h"/>, <paramref name="d"/>, <paramref name="pm"/>, <paramref name="efd"/>
    /// are the inertia, damping, mechanical power and (held) field voltage per machine; the initial states are
    /// the six per machine. <paramref name="yReduced"/> is the reduced admittance with <c>X''</c> folded in (machine
    /// internal nodes, then any retained converter buses). The matrices are wrapped as <see cref="IRmsNetwork"/>s
    /// (<see cref="DenseMachineNetwork"/> / <see cref="ConverterNetwork"/>) and integrated by the network overload;
    /// a machine-only run is bit-for-bit the historical kernel.
    /// <para>N271 (October 2026): the RMS study (<see cref="RmsTransientStability"/>) no longer reaches this overload; it
    /// prepares the sparse full network at every size. This entry stays for a caller that brings its own reduced
    /// admittance, and for the tests' dense Kron oracle (<see cref="SubtransientInit.DenseKronReferenceScope"/>).</para>
    /// </summary>
    public static Result Simulate(
        IReadOnlyList<Machine> machines,
        double[] h, double[] d, double[] pm, double[] efd,
        double[] delta0, double[] eqp0, double[] edp0, double[] eqpp0, double[] edpp0,
        Complex[,] yReduced,
        double f0, double stepSeconds, double durationSeconds,
        Fault? fault = null, double slipLimitRad = 2.0 * Math.PI,
        IReadOnlyList<Sexs>? exciters = null, double[]? voltageReferencePu = null,
        IReadOnlyList<NetworkStage>? admittanceStages = null,
        Func<int, double, double>? mechanicalPower = null,
        IReadOnlyList<RmsConverterUnit>? converters = null,
        int synchronismMachines = -1,
        IReadOnlyList<IRmsFieldController?>? fieldControllers = null)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(yReduced);
        int n = machines.Count;
        int nc = ConverterNodes(converters).Count;
        if (yReduced.GetLength(0) != n + nc || yReduced.GetLength(1) != n + nc)
            throw new ArgumentException("yReduced must be (machines + converters) square: machine internal nodes "
                + "first, then the retained converter buses.", nameof(yReduced));

        var cache = new Dictionary<Complex[,], IRmsNetwork>(ReferenceEqualityComparer.Instance);
        IRmsNetwork Wrap(Complex[,] y)
        {
            if (cache.TryGetValue(y, out IRmsNetwork? net)) return net;
            net = nc > 0 ? new ConverterNetwork(y, n) : new DenseMachineNetwork(y);
            cache[y] = net;
            return net;
        }

        List<RmsNetworkStage>? stages = null;
        if (admittanceStages is not null)
        {
            stages = new List<RmsNetworkStage>(admittanceStages.Count);
            foreach (NetworkStage st in admittanceStages) stages.Add(new RmsNetworkStage(st.AtSeconds, Wrap(st.YReduced)));
        }
        else if (fault is { } fl)
        {
            // The single fault window [onset, clear): faulted from onset, back to the base network at clear.
            stages = new List<RmsNetworkStage>
            {
                new(fl.OnsetSeconds, Wrap(fl.YReducedFaulted)),
                new(fl.ClearSeconds, Wrap(yReduced)),
            };
        }

        return Simulate(machines, h, d, pm, efd, delta0, eqp0, edp0, eqpp0, edpp0, Wrap(yReduced),
            f0, stepSeconds, durationSeconds, stages, slipLimitRad, exciters, voltageReferencePu, mechanicalPower,
            converters, synchronismMachines, fieldControllers);
    }

    /// <summary>
    /// Integrate the fleet against a network (<see cref="IRmsNetwork"/>) and an optional schedule of network
    /// stages - the general form, which takes a dense reduced admittance or a sparse full network
    /// (<see cref="SparseRmsNetwork"/>, the production network at every size since N271) alike. Converter units, when
    /// given, align to the network's converter nodes.
    ///
    /// <para><b>Field controllers (R061).</b> <paramref name="fieldControllers"/>, when given, holds one entry per
    /// machine (a null entry keeps that machine's field held). It cannot be combined with the SEXS-only
    /// <paramref name="exciters"/> list. If <paramref name="voltageReferencePu"/> is null, each controlled
    /// machine's reference is set at t = 0 to the measured terminal voltage plus the controller's equilibrium
    /// error, so the undisturbed run is stationary; that needs the base network in force at t = 0. With
    /// <paramref name="fieldControllers"/> null every line of the historical kernel runs unchanged.</para>
    /// <para><b>Machine trips (R061 rest, CF4).</b> <paramref name="machineTrips"/>, when given, disconnects machines
    /// at their times (see <see cref="RmsMachineTrip"/>). With it null (the default) every line of the historical
    /// kernel runs unchanged: each new branch below is guarded by it.</para>
    /// <para><b>Step observer (inertia placement study, October 2026).</b> <paramref name="stepObserver"/>, when given, is
    /// called once per step after the step's committed network solve, with the step time, the network in force, the
    /// machine EMF phasors and the converter injection currents of that solve (an empty array with no converters). It
    /// must only read them (it is handed the kernel's own arrays): it lets a caller record bus voltages from the same
    /// solve (<see cref="SparseRmsNetwork.SolveBusVoltagesInto"/>). It feeds nothing back, so with or without it every
    /// number of the run is the same bits.</para>
    /// </summary>
    public static Result Simulate(
        IReadOnlyList<Machine> machines,
        double[] h, double[] d, double[] pm, double[] efd,
        double[] delta0, double[] eqp0, double[] edp0, double[] eqpp0, double[] edpp0,
        IRmsNetwork network,
        double f0, double stepSeconds, double durationSeconds,
        IReadOnlyList<RmsNetworkStage>? stages = null, double slipLimitRad = 2.0 * Math.PI,
        IReadOnlyList<Sexs>? exciters = null, double[]? voltageReferencePu = null,
        Func<int, double, double>? mechanicalPower = null,
        IReadOnlyList<RmsConverterUnit>? converters = null,
        int synchronismMachines = -1,
        IReadOnlyList<IRmsFieldController?>? fieldControllers = null,
        IReadOnlyList<RmsMachineTrip>? machineTrips = null,
        Action<double, IRmsNetwork, Complex[], Complex[]>? stepObserver = null)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(network);
        int n = machines.Count;
        int nu = converters?.Count ?? 0;                 // converter units
        List<int> nodeBuses = ConverterNodes(converters);
        int nc = nodeBuses.Count;                        // converter network nodes (units on one bus share one)
        var nodeOf = new int[nu];
        for (int u = 0; u < nu; u++) nodeOf[u] = nodeBuses.IndexOf(converters![u].Bus);
        if (h.Length != n || d.Length != n || pm.Length != n || efd.Length != n ||
            delta0.Length != n || eqp0.Length != n || edp0.Length != n || eqpp0.Length != n || edpp0.Length != n)
            throw new ArgumentException("All per-machine arrays must have the same length as machines.");
        if (network.Machines != n || network.Converters != nc)
            throw new ArgumentException("The network must carry one EMF node per machine and one node per converter.", nameof(network));
        if (!(stepSeconds > 0.0) || !double.IsFinite(stepSeconds)) throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "the step must be a finite number of seconds above 0 (NaN used to pass this check).");
        if (!(durationSeconds >= 0.0) || !double.IsFinite(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "the duration must be a finite number of seconds, 0 or more.");
        if (exciters is not null && (exciters.Count != n || voltageReferencePu is null || voltageReferencePu.Length != n))
            throw new ArgumentException("With exciters, one per machine plus a matching voltageReference is required.", nameof(exciters));
        // R061: the general field-controller list. Every check below is skipped when it is null, so the
        // historical (held-field or SEXS) path is untouched.
        if (fieldControllers is not null)
        {
            if (exciters is not null)
                throw new ArgumentException("Pass either exciters (SEXS) or fieldControllers, not both.", nameof(fieldControllers));
            if (fieldControllers.Count != n)
                throw new ArgumentException($"fieldControllers must hold one entry per machine ({n}), got "
                    + $"{fieldControllers.Count}; use a null entry to hold a machine's field.", nameof(fieldControllers));
            if (voltageReferencePu is not null && voltageReferencePu.Length != n)
                throw new ArgumentException($"voltageReferencePu must hold one value per machine ({n}).", nameof(voltageReferencePu));
            // A controller is stateful: the same instance on two machines would mix their states. Fail loud.
            var seen = new HashSet<IRmsFieldController>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < n; i++)
                if (fieldControllers[i] is { } fc && !seen.Add(fc))
                    throw new ArgumentException($"fieldControllers[{i}] is the same instance as an earlier entry; "
                        + "each machine needs its own controller.", nameof(fieldControllers));
        }
        for (int i = 0; i < n; i++)
        {
            if (!(h[i] > 0.0)) throw new ArgumentOutOfRangeException(nameof(h), $"H[{i}] must be > 0.");
            Machine mc = machines[i];
            if (!(mc.Tdop > 0.0 && mc.Tdopp > 0.0 && mc.Tqop > 0.0 && mc.Tqopp > 0.0))
                throw new ArgumentOutOfRangeException(nameof(machines), $"Machine {i} needs all time constants > 0.");
        }

        double ws = 2.0 * Math.PI * f0;
        int steps = (int)Math.Round(durationSeconds / stepSeconds);

        // R061 rest (CF4): the trip time of each machine (null when no trips: the historical path). Fail loud on a
        // malformed list: a machine out of range, a trip time that is not finite or negative, a machine tripped twice,
        // or every machine of the synchronism set tripped (the verdict would have no machine left to judge).
        double[]? tripAt = null;
        if (machineTrips is not null)
        {
            tripAt = new double[n];
            Array.Fill(tripAt, double.PositiveInfinity);
            foreach (RmsMachineTrip tr in machineTrips)
            {
                if (tr is null) throw new ArgumentException("machineTrips holds a null entry.", nameof(machineTrips));
                if (tr.Machine < 0 || tr.Machine >= n)
                    throw new ArgumentOutOfRangeException(nameof(machineTrips), tr.Machine,
                        $"machine trip names machine {tr.Machine}, but the fleet has {n} machines (0 to {n - 1}).");
                if (!double.IsFinite(tr.AtSeconds) || tr.AtSeconds < 0.0)
                    throw new ArgumentOutOfRangeException(nameof(machineTrips), tr.AtSeconds,
                        $"machine {tr.Machine} trip time must be finite and at or after t = 0 (got {tr.AtSeconds} s).");
                if (!double.IsPositiveInfinity(tripAt[tr.Machine]))
                    throw new ArgumentException($"machine {tr.Machine} is tripped twice.", nameof(machineTrips));
                tripAt[tr.Machine] = tr.AtSeconds;
            }
            int judged = synchronismMachines >= 0 ? Math.Min(synchronismMachines, n) : n;
            bool anyLeft = false;
            for (int i = 0; i < judged; i++) if (double.IsPositiveInfinity(tripAt[i])) anyLeft = true;
            if (!anyLeft)
                throw new ArgumentException("every machine of the synchronism set is tripped; at least one machine must "
                    + "stay connected for the run to have a synchronism verdict.", nameof(machineTrips));
        }
        double tripEps = stepSeconds * StageTimeToleranceSteps;   // the same "reached" rule as a network stage
        bool Tripped(int i, double atTime) => tripAt is not null && atTime + tripEps >= tripAt[i];
        double[]? tripPmBuffer = tripAt is not null ? new double[n] : null;

        // The (held or AVR-driven) field per machine. Exciters start from the held field so a matched
        // reference is stationary; the round subtransient reactance X'' feeds the terminal-voltage recovery.
        var field = (double[])efd.Clone();
        var xppSys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xppSys[i] = machines[i].Xdpp;
            exciters?[i].Initialize(efd[i]);
            fieldControllers?[i]?.Initialize(efd[i]);   // R061: null list or null entry = held field
        }
        // R061: the field controllers' voltage references: the caller's, or (when null) filled at t = 0 from the
        // measured initial terminal voltage. Stays null on the historical path.
        double[]? fieldReference = fieldControllers is null ? null
            : voltageReferencePu is not null ? (double[])voltageReferencePu.Clone() : null;

        // Four exact-ZOH flux lags per machine.
        var fEqp = new FirstOrderLag[n];
        var fEqpp = new FirstOrderLag[n];
        var fEdp = new FirstOrderLag[n];
        var fEdpp = new FirstOrderLag[n];
        for (int i = 0; i < n; i++)
        {
            Machine mc = machines[i];
            fEqp[i] = new FirstOrderLag(1.0, mc.Tdop); fEqp[i].Initialize(eqp0[i]);
            fEqpp[i] = new FirstOrderLag(1.0, mc.Tdopp); fEqpp[i].Initialize(eqpp0[i]);
            fEdp[i] = new FirstOrderLag(1.0, mc.Tqop); fEdp[i].Initialize(edp0[i]);
            fEdpp[i] = new FirstOrderLag(1.0, mc.Tqopp); fEdpp[i].Initialize(edpp0[i]);
        }

        var delta = (double[])delta0.Clone();
        var omega = new double[n];

        var tS = new List<double>(steps + 1);
        var dS = NewSeries(n, steps);
        var wS = NewSeries(n, steps);
        var eqS = NewSeries(n, steps);
        var edS = NewSeries(n, steps);
        var eqppS = NewSeries(n, steps);
        var eddpS = NewSeries(n, steps);

        int converterIterationFailures = 0;   // R084
        int rescuedSteps = 0;                 // D38: steps solved by the exact rescue after the substitution hit the cap
        bool inSync = true;

        // Converter plant: phase-locked current sources; each unit sits on a network node (units on one bus
        // share it and their currents add).
        // WECC (expansion): each unit's dynamic model behind IRmsConverterModel; a unit without a WECC chain gets the
        // historical RmsConverterState through an adapter that forwards the same calls with the same arguments.
        var convState = new IRmsConverterModel[nu];
        var vConv = new Complex[nc];
        for (int u = 0; u < nu; u++) { convState[u] = converters![u].CreateModel(nodeBuses); vConv[nodeOf[u]] = converters[u].V0; }
        // WECC: a plant controller's measurement node (no injection) starts the iteration from its power-flow voltage.
        for (int u = 0; u < nu; u++)
            if (converters![u].WeccMeasurement is { } wm)
            {
                if (wm.RegulatedBus != 0) vConv[nodeBuses.IndexOf(wm.RegulatedBus)] = wm.RegulatedV0;
                if (wm.HasBranch) { vConv[nodeBuses.IndexOf(wm.FromBus)] = wm.FromV0; vConv[nodeBuses.IndexOf(wm.ToBus)] = wm.ToV0; }
            }
        for (int u = 0; u < nu; u++) vConv[nodeOf[u]] = converters![u].V0;   // a unit's own terminal wins over a measurement V0
        var cV = NewSeries(nu, steps); var cP = NewSeries(nu, steps); var cQ = NewSeries(nu, steps);
        var cIp = NewSeries(nu, steps); var cIq = NewSeries(nu, steps);
        var cDip = new List<bool>[nu];
        for (int u = 0; u < nu; u++) cDip[u] = new List<bool>(steps + 1);
        // WECC: the regulated-bus voltage of a unit whose plant controller measures another bus (read only; -1 otherwise).
        var regNode = new int[nu];
        var cReg = new List<double>?[nu];
        for (int u = 0; u < nu; u++)
        {
            int rb = converters![u].WeccMeasurement?.RegulatedBus ?? 0;
            regNode[u] = rb != 0 ? nodeBuses.IndexOf(rb) : -1;
            if (regNode[u] >= 0) cReg[u] = new List<double>(steps + 1);
        }
        var ic = new Complex[nc];

        // R249: a stage time such as onset + clearing (0.1 + 0.14) is formed in floating point and can land one
        // ulp ABOVE the step grid point it means (0.24000000000000002 vs 48 * 0.005 = 0.24), so the plain
        // t >= AtSeconds test missed the intended step and the fault persisted one full step longer (145 ms
        // instead of 140 ms). A stage now takes effect at the first step whose time is within a millionth of a
        // step of its AtSeconds - the same convention the EMT engine uses for its events. A stage genuinely
        // between grid points still waits for the next step.
        double stageEps = stepSeconds * StageTimeToleranceSteps;
        // GR-564: NetAt keeps the LAST stage whose time has arrived, which is only right for a list in time order; an
        // unsorted list (or a NaN time) used to integrate against the wrong network without a word. Checked here.
        if (stages is not null)
            for (int s = 0; s < stages.Count; s++)
                if (!double.IsFinite(stages[s].AtSeconds) || (s > 0 && stages[s].AtSeconds < stages[s - 1].AtSeconds))
                    throw new ArgumentException(
                        $"RMS network stage {s} is at {stages[s].AtSeconds} s; stages must be finite times in time order.", nameof(stages));
        IRmsNetwork NetAt(double t)
        {
            IRmsNetwork net = network;
            if (stages is not null)
                foreach (RmsNetworkStage st in stages) if (t + stageEps >= st.AtSeconds) net = st.Network;
            return net;   // the latest stage whose time has arrived; the base network before any
        }

        // R115: per-step buffers hoisted out of the time loop (they were allocated afresh every step). Each is fully
        // overwritten before it is read in a step and never retained past it (the traces copy values), so reusing
        // them leaves every number bit-identical.
        var eqp = new double[n];
        var edp = new double[n];
        var eqpp = new double[n];
        var edpp = new double[n];
        double[]? pmBuffer = mechanicalPower is not null ? new double[n] : null;
        var swing = new SwingWorkspace(n, nc);
        // R115 (1.95.074): the network solve outputs and the EMF phasors are written into these instead of new
        // arrays on every solve. vSpare is the converter-voltage buffer the next solve writes into; the iteration
        // compares it with vConv (the previous iterate) and then swaps the two, so the comparison never reads an
        // array the solve has just overwritten. icFrozen replaces the per-step clone of ic handed to the swing.
        var eStep = new Complex[n];
        var imStep = new Complex[n];
        var vSpare = new Complex[nc];
        var icFrozen = new Complex[nc];

        // WECC expansion (D36): units that solve their own network interface exactly (REGC_B). Null when there are none, and
        // then every line of the converter iteration below is the historical one. icUnit keeps each unit's injection of the
        // pass that produced vConv; RmsNetworkSolvedIteration turns it into this pass's injections (exact for one unit, a
        // Newton step on the injections for several) from the network columns of their nodes (the converter-node voltages per
        // unit current injected there, from one solve per such node, once per network stage).
        IRmsNetworkSolvedConverterModel?[]? solved = null;
        for (int u = 0; u < nu; u++)
            if (convState[u] is IRmsNetworkSolvedConverterModel { SolvesWithNetwork: true } sm) { solved ??= new IRmsNetworkSolvedConverterModel?[nu]; solved[u] = sm; }
        Complex[]? icUnit = solved is null ? null : new Complex[nu];
        RmsNetworkSolvedIteration? solvedPass = solved is null ? null : new RmsNetworkSolvedIteration(solved, nodeOf);
        var zCache = solved is null ? null : new Dictionary<IRmsNetwork, Complex[]?[]>(ReferenceEqualityComparer.Instance);
        // D38: units that CAN solve their interface exactly but do not by default (REGC_A). When a step's iteration hits the
        // cap, it is restarted from the step's starting voltages with every such unit solved exactly as well; a step that
        // converges never takes this path, so its numbers are the historical ones. Null when no unit qualifies.
        IRmsNetworkSolvedConverterModel?[]? rescue = null;
        for (int u = 0; u < nu; u++)
            if (convState[u] is IRmsNetworkSolvedConverterModel { SolvesWithNetwork: false, CanSolveWithNetwork: true })
            {
                rescue = new IRmsNetworkSolvedConverterModel?[nu];
                for (int w = 0; w < nu; w++)
                    if (convState[w] is IRmsNetworkSolvedConverterModel cm && (cm.SolvesWithNetwork || cm.CanSolveWithNetwork)) rescue[w] = cm;
                break;
            }
        RmsNetworkSolvedIteration? rescuePass = rescue is null ? null : new RmsNetworkSolvedIteration(rescue, nodeOf);
        var rescueCache = rescue is null ? null : new Dictionary<IRmsNetwork, Complex[]?[]>(ReferenceEqualityComparer.Instance);
        Complex[]? vStart = rescue is null ? null : new Complex[nc];
        if (rescue is not null) icUnit ??= new Complex[nu];
        Complex[]?[] NetworkColumns(IRmsNetwork net) => Columns(net, solved!, zCache!);
        Complex[]?[] Columns(IRmsNetwork net, IRmsNetworkSolvedConverterModel?[] set, Dictionary<IRmsNetwork, Complex[]?[]> cache)
        {
            if (cache.TryGetValue(net, out Complex[]?[]? z)) return z;
            z = new Complex[]?[nc];
            var e0 = new Complex[n];
            var unitI = new Complex[nc];
            var imZ = new Complex[n];
            for (int u = 0; u < nu; u++)
            {
                if (set[u] is null) continue;
                int c = nodeOf[u];
                if (z[c] is not null) continue;
                Array.Clear(unitI);
                unitI[c] = Complex.One;
                var col = new Complex[nc];
                net.SolveInto(e0, unitI, imZ, col);
                if (!(col[c].Magnitude > 0.0) || !double.IsFinite(col[c].Magnitude))
                    throw new InvalidOperationException($"Converter node {nodeBuses[c]}: its driving-point impedance is {col[c]}; the unit cannot be solved with the network.");
                z[c] = col;
            }
            cache[net] = z;
            return z;
        }

        for (int k = 0; k <= steps; k++)
        {
            double t = k * stepSeconds;
            for (int i = 0; i < n; i++)
            {
                eqp[i] = fEqp[i].Output; edp[i] = fEdp[i].Output;
                eqpp[i] = fEqpp[i].Output; edpp[i] = fEdpp[i].Output;
            }

            // The network this step. With converters, their phase-locked currents and terminal voltages are
            // iterated to the algebraic fixed point, then held (like the EMFs) across the step.
            IRmsNetwork net = NetAt(t);
            SubtransientNetworkInterface.EmfPhasorsInto(eqpp, edpp, delta, eStep);
            Complex[] e = eStep;
            Complex[] im;
            if (nc > 0)
            {
                Complex[] vNew = vConv;
                im = Array.Empty<Complex>();
                bool convSettled = false;
                IRmsNetworkSolvedConverterModel?[]? set = solved;
                RmsNetworkSolvedIteration? pass = solvedPass;
                Complex[]?[]? zcol = solved is null ? null : NetworkColumns(net);
                if (vStart is not null) Array.Copy(vConv, vStart, nc);
                for (int attempt = 0; ; attempt++)
                {
                    for (int it = 0; it < MaxConverterIterations; it++)
                    {
                        Array.Clear(ic);
                        if (set is null)
                        {
                            for (int u = 0; u < nu; u++)
                                ic[nodeOf[u]] += convState[u].TrialCurrent(vConv[nodeOf[u]]);
                        }
                        else
                        {
                            // D36: the first pass is the plain trial (vConv is last step's voltage, not this network's solve
                            // of a known injection); from the second on, the network-solved units' injections come from their
                            // exact interface solutions (RmsNetworkSolvedIteration) and every other unit takes its trial current.
                            if (it > 0) pass!.Update(vConv, zcol!, icUnit!);
                            for (int u = 0; u < nu; u++)
                            {
                                int c = nodeOf[u];
                                if (it == 0 || set[u] is null) icUnit![u] = convState[u].TrialCurrent(vConv[c]);
                                ic[c] += icUnit![u];
                            }
                        }
                        net.SolveInto(e, ic, imStep, vSpare);
                        im = imStep;
                        vNew = vSpare;
                        double change = 0.0;
                        for (int c = 0; c < nc; c++) change = Math.Max(change, (vNew[c] - vConv[c]).Magnitude);
                        vSpare = vConv;   // the previous iterate's array is the next solve's output
                        vConv = vNew;
                        if (change < ConverterVoltageTolerancePu) { convSettled = true; break; }
                    }
                    if (convSettled || attempt > 0 || rescue is null) break;
                    // D38 rescue: restart this step's iteration from its starting voltages with every unit that can solve its
                    // interface exactly doing so.
                    Array.Copy(vStart!, vConv, nc);
                    set = rescue;
                    pass = rescuePass;
                    zcol = Columns(net, rescue, rescueCache!);
                    rescuedSteps++;
                }
                if (!convSettled) converterIterationFailures++;   // R084: reported on the Result, not hidden
                Array.Clear(ic);
                for (int u = 0; u < nu; u++)
                {
                    Complex vu = vConv[nodeOf[u]];
                    Complex iu = convState[u].Commit(vu);
                    ic[nodeOf[u]] += iu;
                    Complex sC = vu * Complex.Conjugate(convState[u].TerminalCurrent(iu, vu));
                    cV[u].Add(vu.Magnitude); cP[u].Add(sC.Real); cQ[u].Add(sC.Imaginary);
                    cIp[u].Add(convState[u].ActiveCurrent); cIq[u].Add(convState[u].ReactiveCurrent);
                    cDip[u].Add(convState[u].InDip);
                    if (regNode[u] >= 0) cReg[u]!.Add(vConv[regNode[u]].Magnitude);
                }
                net.SolveInto(e, ic, imStep, vSpare);   // machine currents consistent with the committed converter currents
                im = imStep;                            // (the voltages land in the spare, unused, as before)
            }
            else
            {
                net.SolveInto(e, ic, imStep, vSpare);
                im = imStep;
            }

            stepObserver?.Invoke(t, net, e, ic);   // read-only (see the summary); null on every historical path
            tS.Add(t);
            int syncCount = synchronismMachines >= 0 ? Math.Min(synchronismMachines, n) : n;
            // R247: loss of synchronism is judged exactly as the classical kernel judges it - any machine more than
            // slipLimitRad from the inertia-weighted centre-of-inertia angle - so the subtransient engine genuinely
            // reduces to the classical CCT. It used to test the max-min angle SPREAD against the same limit, which
            // for two equal machines fires at a 2 pi spread where the COI test fires at 4 pi. The COI is taken over
            // the machines in the synchronism set (syncCount), weighted by their system-base H.
            double hSum = 0.0, hDelta = 0.0;
            for (int i = 0; i < n; i++)
            {
                dS[i].Add(delta[i]); wS[i].Add(omega[i]);
                eqS[i].Add(eqp[i]); edS[i].Add(edp[i]);
                eqppS[i].Add(eqpp[i]); eddpS[i].Add(edpp[i]);
                if (i < syncCount && !Tripped(i, t))   // R061 rest (CF4): a tripped machine is not judged
                {
                    // R027: a non-finite angle or speed is a diverged run, never "in synchronism". Same guard as
                    // the classical kernel.
                    if (!double.IsFinite(delta[i]) || !double.IsFinite(omega[i])) inSync = false;
                    hSum += h[i]; hDelta += h[i] * delta[i];
                }
            }
            double coi = hDelta / hSum;
            for (int i = 0; i < syncCount; i++)
            {
                if (Tripped(i, t)) continue;   // R061 rest (CF4): disconnected, not judged (never true without trips)
                if (!(Math.Abs(delta[i] - coi) <= slipLimitRad)) inSync = false;   // NaN-safe: a NaN deviation fails
            }

            if (k == steps) break;

            // The field current the E'q equation balances against Efd, from the same start-of-step dq current and saturation
            // increment the flux step below uses (a read-only computation, called only for a controller that asks).
            double FieldCurrent(int j)
            {
                (double sn, double cs) = DetMath.SinCos(delta[j]);
                double idNow = im[j].Real * sn - im[j].Imaginary * cs;
                Machine mf = machines[j];
                double satNow = 0.0;
                if (mf.Saturation is { } sf) (satNow, _) = sf.GenrouIncrements(eqpp[j], edpp[j], mf.EffectiveSaturationQAxisScale);
                return eqp[j] + (mf.Xd - mf.Xdp) * idNow + satNow;
            }

            // AVR: read the terminal voltage V_t = E'' - jX'' I at the start of the step (field held across it).
            if (exciters is not null)
            {
                for (int i = 0; i < n; i++)
                {
                    // Bug hunt 2026-10-29, finding 27: a tripped machine's field is held, as on the fieldControllers path
                    // below. It has no terminal (its current is 0, so the "terminal voltage" here would read |E''|) and
                    // stepping its SEXS drove the field from a meaningless error. Never true without machineTrips, so
                    // every run without trips is unchanged.
                    if (Tripped(i, t)) continue;
                    double tr = e[i].Real + xppSys[i] * im[i].Imaginary;
                    double ti = e[i].Imaginary - xppSys[i] * im[i].Real;
                    field[i] = exciters[i].Step(voltageReferencePu![i] - Math.Sqrt(tr * tr + ti * ti), stepSeconds);
                }
            }
            else if (fieldControllers is not null)
            {
                // R061: the general controllers. The terminal voltage is recovered exactly as for SEXS above.
                if (fieldReference is null)
                {
                    // First step: set each reference so the controller's equilibrium error holds the initial
                    // field. The measurement must come from the undisturbed base network, or the reference
                    // would bake the disturbance in; fail loud rather than start off equilibrium.
                    if (!ReferenceEquals(net, network))
                        throw new InvalidOperationException("Automatic field-controller voltage references need the "
                            + "base network in force at t = 0; schedule the disturbance after t = 0 or pass "
                            + "voltageReferencePu.");
                    fieldReference = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        if (fieldControllers[i] is not { } fc0) continue;
                        double tr0 = e[i].Real + xppSys[i] * im[i].Imaginary;
                        double ti0 = e[i].Imaginary - xppSys[i] * im[i].Real;
                        fieldReference[i] = Math.Sqrt(tr0 * tr0 + ti0 * ti0) + fc0.EquilibriumErrorPu(efd[i]);
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    if (fieldControllers[i] is not { } fc) continue;   // held field for this machine
                    if (Tripped(i, t)) continue;   // R061 rest (CF4): a tripped machine's field is held (it has no terminal)
                    double tr = e[i].Real + xppSys[i] * im[i].Imaginary;
                    double ti = e[i].Imaginary - xppSys[i] * im[i].Real;
                    if (fc is IRmsTerminalFieldController { NeedsTerminalQuantities: true } ftc)
                        field[i] = ftc.Step(fieldReference[i] - Math.Sqrt(tr * tr + ti * ti), omega[i], FieldCurrent(i),
                            new Complex(tr, ti), im[i], stepSeconds);
                    else if (fc is IRmsFieldCurrentController { NeedsFieldCurrent: true } fcc)
                        field[i] = fcc.Step(fieldReference[i] - Math.Sqrt(tr * tr + ti * ti), omega[i], FieldCurrent(i), stepSeconds);
                    else
                        field[i] = fc.Step(fieldReference[i] - Math.Sqrt(tr * tr + ti * ti), omega[i], stepSeconds);
                }
            }

            // Start-of-step dq currents (subtransient EMFs and angles held) -> flux decay inputs.
            for (int i = 0; i < n; i++)
            {
                (double s, double c) = DetMath.SinCos(delta[i]);
                double iq0 = im[i].Real * c + im[i].Imaginary * s;
                double id0 = im[i].Real * s - im[i].Imaginary * c;
                Machine mc = machines[i];
                if (mc.Saturation is not { } sat)
                {
                    // Unsaturated: the historical arithmetic, operand for operand (the increment is an exact 0.0).
                    double se = 0.0;
                    fEqp[i].Step(field[i] - (mc.Xd - mc.Xdp) * id0 - se, stepSeconds);
                    fEdp[i].Step((mc.Xq - mc.Xqp) * iq0, stepSeconds);
                }
                else
                {
                    // N045 (October 2026, stream B3): GENROU saturation on |E''| on both axes (the PSS/E convention, see
                    // SubtransientMachine.SaturationCurve.GenrouIncrements), frozen at the start-of-step subtransient EMFs.
                    // SubtransientInit seeds the field and the rotor angle with exactly these terms, so a saturated fleet
                    // starts at equilibrium. (It used to be E'q*Se(E'q) on the d-axis only.)
                    (double satD, double satQ) = sat.GenrouIncrements(eqpp[i], edpp[i], mc.EffectiveSaturationQAxisScale);
                    fEqp[i].Step(field[i] - (mc.Xd - mc.Xdp) * id0 - satD, stepSeconds);
                    fEdp[i].Step((mc.Xq - mc.Xqp) * iq0 - satQ, stepSeconds);
                }
                fEqpp[i].Step(eqp[i] - (mc.Xdp - mc.Xdpp) * id0, stepSeconds);
                fEdpp[i].Step(edp[i] + (mc.Xqp - mc.Xqpp) * iq0, stepSeconds);
            }

            // Governor: mechanical power read from the start-of-step speed and held across the step's RK4.
            double[] pmStep = pm;
            if (mechanicalPower is not null)
            {
                pmStep = pmBuffer!;
                for (int i = 0; i < n; i++) pmStep[i] = mechanicalPower(i, omega[i]);
            }
            if (tripAt is not null)
            {
                // R061 rest (CF4): a tripped machine's mechanical power is zero from its trip on (the governor, if any,
                // was still stepped above, so its state stays in step with the run). The caller's pm is never written.
                if (ReferenceEquals(pmStep, pm)) { Array.Copy(pm, tripPmBuffer!, n); pmStep = tripPmBuffer!; }
                for (int i = 0; i < n; i++) if (Tripped(i, t)) pmStep[i] = 0.0;
            }

            // Converter controls advance from the start-of-step terminal voltage.
            for (int u = 0; u < nu; u++) convState[u].Step(t, vConv[nodeOf[u]], vConv, stepSeconds);

            // Coupled rotor swing by RK4, subtransient EMFs and converter currents frozen; Pe recomputed each
            // sub-step from the network.
            Array.Copy(ic, icFrozen, nc);
            SwingStep(n, delta, omega, eqpp, edpp, pmStep, d, h, net, icFrozen, ws, stepSeconds, swing);
        }

        var traces = new List<MachineTrace>(n);
        for (int i = 0; i < n; i++)
            traces.Add(new MachineTrace(tS, dS[i], wS[i], eqS[i], edS[i], eqppS[i], eddpS[i]));
        List<RmsConverterTrace>? convTraces = null;
        if (nu > 0)
        {
            convTraces = new List<RmsConverterTrace>(nu);
            for (int c = 0; c < nu; c++)
                convTraces.Add(new RmsConverterTrace(converters![c].Name, converters[c].Bus,
                    cV[c], cP[c], cQ[c], cIp[c], cIq[c], cDip[c])
                    { RegulatedBusVoltagePu = cReg[c], RegulatedBus = regNode[c] >= 0 ? converters[c].WeccMeasurement!.RegulatedBus : 0 });
        }
        return new Result(traces, inSync, convTraces, converterIterationFailures) { ConverterRescuedSteps = rescuedSteps };
    }

    /// <summary>The network's converter nodes: the distinct converter buses in first-appearance order, then any WECC
    /// measurement buses (<see cref="RmsConverterUnit.NodeBuses"/>, the order the network builder uses).</summary>
    private static List<int> ConverterNodes(IReadOnlyList<RmsConverterUnit>? converters) => RmsConverterUnit.NodeBuses(converters);

    private static List<double>[] NewSeries(int n, int steps)
    {
        var s = new List<double>[n];
        for (int i = 0; i < n; i++) s[i] = new List<double>(steps + 1);
        return s;
    }

    /// <summary>The RK4 stage buffers of <see cref="SwingStep"/>, allocated once per run (R115: SwingStep used to
    /// allocate fourteen n-vectors plus four acceleration arrays every step).</summary>
    private sealed class SwingWorkspace(int n, int nc)
    {
        // R115 (1.95.074): the stage EMF phasors and network solve outputs (Accel consumes them before the next stage).
        public readonly Complex[] E = new Complex[n], Im = new Complex[n], Vc = new Complex[nc];
        public readonly double[] K1d = new double[n], K1w = new double[n], K2d = new double[n], K2w = new double[n];
        public readonly double[] K3d = new double[n], K3w = new double[n], K4d = new double[n], K4w = new double[n];
        public readonly double[] D2 = new double[n], W2 = new double[n], D3 = new double[n], W3 = new double[n];
        public readonly double[] D4 = new double[n], W4 = new double[n];
    }

    /// <summary>One RK4 step of the coupled (delta, omega) swing, mutating them in place. The subtransient
    /// EMFs and converter currents are frozen; the network (and hence Pe) is re-solved at each stage from the
    /// stage angles. Stage results are written into <paramref name="w"/>'s buffers; every expression and its
    /// evaluation order is unchanged, so the step is bit-identical to the allocating version.</summary>
    private static void SwingStep(
        int n, double[] delta, double[] omega, double[] eqpp, double[] edpp,
        double[] pm, double[] d, double[] h, IRmsNetwork net, Complex[] ic, double ws, double dt, SwingWorkspace w)
    {
        void Accel(double[] dlt, double[] om, double[] acc)
        {
            Complex[] e = w.E, im = w.Im;
            SubtransientNetworkInterface.EmfPhasorsInto(eqpp, edpp, dlt, e);
            net.SolveInto(e, ic, im, w.Vc);
            for (int i = 0; i < n; i++)
            {
                double pe = e[i].Real * im[i].Real + e[i].Imaginary * im[i].Imaginary;   // Re(E conj(I))
                acc[i] = (pm[i] - pe - d[i] * om[i]) / (2.0 * h[i]);
            }
        }
        void Step(double[] baseArr, double[] rate, double f, double[] o)
        {
            for (int i = 0; i < n; i++) o[i] = baseArr[i] + f * rate[i];
        }
        void Dd(double[] om, double[] o)
        {
            for (int i = 0; i < n; i++) o[i] = ws * om[i];
        }

        double[] k1d = w.K1d, k1w = w.K1w, k2d = w.K2d, k2w = w.K2w, k3d = w.K3d, k3w = w.K3w, k4d = w.K4d, k4w = w.K4w;
        double[] d2 = w.D2, w2 = w.W2, d3 = w.D3, w3 = w.W3, d4 = w.D4, w4 = w.W4;
        Dd(omega, k1d); Accel(delta, omega, k1w);
        Step(delta, k1d, 0.5 * dt, d2); Step(omega, k1w, 0.5 * dt, w2);
        Dd(w2, k2d); Accel(d2, w2, k2w);
        Step(delta, k2d, 0.5 * dt, d3); Step(omega, k2w, 0.5 * dt, w3);
        Dd(w3, k3d); Accel(d3, w3, k3w);
        Step(delta, k3d, dt, d4); Step(omega, k3w, dt, w4);
        Dd(w4, k4d); Accel(d4, w4, k4w);

        for (int i = 0; i < n; i++)
        {
            delta[i] += dt / 6.0 * (k1d[i] + 2 * k2d[i] + 2 * k3d[i] + k4d[i]);
            omega[i] += dt / 6.0 * (k1w[i] + 2 * k2w[i] + 2 * k3w[i] + k4w[i]);
        }
    }
}
