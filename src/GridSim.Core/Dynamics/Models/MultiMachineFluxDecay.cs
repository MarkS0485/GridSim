using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The <b>multi-machine</b> varying-<c>E'q</c> (flux-decay) model - the network generalisation of the SMIB
/// <see cref="OneAxisMachine"/>. Every machine's transient EMF <c>E'q</c> is a state driven by its field
/// voltage through the field flux-linkage decay, and the machines are coupled through the reduced network
/// rather than a single infinite bus. It is the varying-<c>E'q</c> step up from the constant-<c>E'</c>
/// <see cref="ClassicalMultiMachine"/>, and reuses that trusted swing kernel through its flux-decay hook -
/// there is no second integrator (the same "no second kernel" discipline as <see cref="GovernedMultiMachine"/>).
///
/// <para>Per machine (per-unit on the system base, angles in rad, times in seconds):</para>
/// <code>
///   ddelta_i/dt   = omega_s * omega_i                                     (rotor angle)
///   domega_i/dt   = (Pm_i - Pe_i - D_i*omega_i) / (2 H_i)                 (swing)
///   T'do_i dE'q_i/dt = Efd_i - E'q_i - (Xd_i - X'd_i) * Id_i              (field flux-linkage decay)
/// </code>
/// This first multi-machine slice is <b>round-rotor</b> at the network interface (<c>Xq = X'd</c>), so each
/// machine presents to the network as a simple internal voltage <c>E'q_i&#8736;delta_i</c> behind its
/// transient reactance <c>X'd</c> - exactly the classical interface, with the reduction reactance already
/// folded into the reduced admittance <c>Y</c> by <see cref="ClassicalInit"/>. The electrical power is then
/// the classical form <c>Pe_i = sum_j |E'|_i |E'|_j (G_ij cos(delta_ij) + B_ij sin(delta_ij))</c> (no
/// saliency reluctance term), and the d-axis current that drives the flux decay is recovered from the
/// network <b>reactive</b> injection at the internal node,
/// <code>
///   Qe_i = sum_j |E'|_i |E'|_j (G_ij sin(delta_ij) - B_ij cos(delta_ij)),   Id_i = Qe_i / |E'|_i
/// </code>
/// so there is <b>no iterative network solve</b>: given the rotor angles and internal voltages, one reduced
/// matrix-vector product gives both <c>Pe</c> and <c>Id</c> in closed form. The field flux state advances on
/// the same <b>exact zero-order-hold</b> <see cref="FirstOrderLag"/> as the rest of the library (input
/// <c>Efd_i - (Xd_i - X'd_i) Id_i</c> held across the step), and the rotor swing advances by the classical
/// kernel's RK4 with <c>E'q</c> frozen across the sub-steps - operator splitting, matching both the SMIB
/// machine and the governor.
///
/// <para><b>Deliberately out of this slice.</b> Saliency in the network interface (<c>Xq != X'd</c>), the
/// subtransient two-axis reactances of GENROU/GENSAL/GENTPJ and magnetic saturation need a dq-frame solve
/// that carries a d- and q-axis reactance separately - the same oracle-backed care at network scale that the
/// SMIB slice established, taken up in the next slice. Multi-machine AVR is likewise deferred: the reduced
/// internal-node network does not expose terminal voltages (Kron reduction eliminated the terminal buses),
/// which an AVR regulates, so the field is held constant per machine here; AVR-in-the-loop is already
/// validated at SMIB scale on <see cref="OneAxisMachine"/> and returns to the multi-machine model with the
/// terminal-voltage-preserving solve.</para>
///
/// <para>It collapses onto the classical model as a reduction identity - with <c>Xd_i = X'd_i</c> the
/// flux-decay driver term vanishes and a matched held field keeps every <c>E'q_i</c> at its initial value, so
/// the run is the constant-<c>E'</c> classical swing - and an open (disconnected) machine's field response is
/// the analytic flux-decay exponential to machine epsilon. These, with equilibrium stationarity and
/// bit-for-bit determinism, are the oracles it is validated against
/// (<c>tests/GridSim.Tests/MultiMachineFluxDecayTests.cs</c>).</para>
/// </summary>
public static class MultiMachineFluxDecay
{
    /// <summary>Per-machine trajectory: rotor angle delta(t) (rad), per-unit speed deviation omega(t), and
    /// the varying transient EMF magnitude E'q(t) (pu).</summary>
    public sealed record MachineTrace(
        IReadOnlyList<double> TimeSeconds,
        IReadOnlyList<double> DeltaRad,
        IReadOnlyList<double> SpeedPu,
        IReadOnlyList<double> EqpPu);

    public sealed record Result(IReadOnlyList<MachineTrace> Machines, bool StayedInSynchronism);

    /// <summary>
    /// Network injection at each machine's internal node from the rotor angles and internal voltages: the
    /// air-gap electrical power <c>Pe_i</c> (pu) and the d-axis current <c>Id_i = Qe_i / |E'|_i</c> (pu) that
    /// drives its flux decay. A single reduced matrix-vector product - the same admittance the swing uses -
    /// so it is closed-form and deterministic, no iteration.
    /// </summary>
    public static (double[] Pe, double[] Id) NetworkInjection(double[] eq, double[] delta, Complex[,] yReduced)
    {
        int n = eq.Length;
        if (delta.Length != n) throw new ArgumentException("eq and delta must have the same length.");
        if (yReduced.GetLength(0) != n || yReduced.GetLength(1) != n)
            throw new ArgumentException("yReduced must be an nxn matrix.", nameof(yReduced));

        var pe = new double[n];
        var id = new double[n];
        for (int i = 0; i < n; i++)
        {
            double p = 0.0, q = 0.0, di = delta[i];
            for (int j = 0; j < n; j++)
            {
                (double s, double c) = DetMath.SinCos(di - delta[j]);
                double gij = yReduced[i, j].Real, bij = yReduced[i, j].Imaginary;
                p += eq[j] * (gij * c + bij * s);
                q += eq[j] * (gij * s - bij * c);
            }
            pe[i] = eq[i] * p;
            id[i] = q;                    // Qe_i = eq_i * q, so Id_i = Qe_i / eq_i = q
        }
        return (pe, id);
    }

    /// <summary>
    /// The field voltages that make <c>(delta0, omega = 0, E'q = eq0)</c> an exact equilibrium of the flux
    /// decay: <c>Efd_i = E'q_i + (Xd_i - X'd_i) * Id_i</c> (so <c>dE'q_i/dt = 0</c>), with <c>Id</c> from
    /// <see cref="NetworkInjection"/>. Pair with mechanical powers set to the electrical output at
    /// <c>delta0</c> (the <c>Pe</c> from the same call) and an undisturbed run is stationary.
    /// </summary>
    public static double[] EquilibriumField(double[] eq0, double[] delta0, Complex[,] yReduced, double[] xdMinusXdpSys)
    {
        int n = eq0.Length;
        if (xdMinusXdpSys.Length != n) throw new ArgumentException("xdMinusXdpSys must align to eq0.", nameof(xdMinusXdpSys));
        (_, double[] id) = NetworkInjection(eq0, delta0, yReduced);
        var efd = new double[n];
        for (int i = 0; i < n; i++) efd[i] = eq0[i] + xdMinusXdpSys[i] * id[i];
        return efd;
    }

    /// <summary>
    /// Integrate the flux-decay fleet. Inputs are the reduced classical set (<paramref name="h"/>,
    /// <paramref name="d"/>, <paramref name="pm"/>, <paramref name="eq0"/> = initial <c>|E'q|</c>,
    /// <paramref name="delta0"/>, <paramref name="yReduced"/>) plus the flux-decay data: <paramref
    /// name="xdMinusXdpSys"/> = <c>(Xd - X'd)</c> per machine on the system base, <paramref name="tdop"/> =
    /// open-circuit field time constant <c>T'do</c> (s), and <paramref name="efd"/> = the (held) field
    /// voltage per machine (pu). The rotor swing is <see cref="ClassicalMultiMachine.Simulate"/> driven
    /// through its flux-decay hook, so a case with <c>Xd = X'd</c> and a matched field is the classical
    /// swing. <paramref name="fault"/> applies a temporary reduced admittance over a window as in the
    /// classical kernel.
    /// </summary>
    public static Result Simulate(
        double[] h, double[] d, double[] pm, double[] eq0, double[] delta0,
        double[] xdMinusXdpSys, double[] tdop, double[] efd,
        Complex[,] yReduced,
        double f0, double stepSeconds, double durationSeconds,
        ClassicalMultiMachine.Fault? fault = null,
        double slipLimitRad = 2.0 * Math.PI)
    {
        int n = h.Length;
        if (eq0.Length != n || delta0.Length != n || xdMinusXdpSys.Length != n || tdop.Length != n || efd.Length != n)
            throw new ArgumentException("h, eq0, delta0, xdMinusXdpSys, tdop and efd must all have the same length.");
        for (int i = 0; i < n; i++)
            if (!(tdop[i] > 0.0))
                throw new ArgumentOutOfRangeException(nameof(tdop), $"T'do for machine {i} must be > 0 (got {tdop[i]}).");

        // One exact-ZOH field flux-linkage lag per machine, preset to the initial EMF.
        var flux = new FirstOrderLag[n];
        for (int i = 0; i < n; i++) { flux[i] = new FirstOrderLag(gain: 1.0, timeConstantSeconds: tdop[i]); flux[i].Initialize(eq0[i]); }

        // Record each machine's start-of-step E'q as the kernel calls the hook (steps entries); the final
        // sample (k = steps) is appended from the lag output after the run, aligning with delta/omega.
        var eqSeries = new List<double>[n];
        for (int i = 0; i < n; i++) eqSeries[i] = new List<double>();

        // The flux-decay hook: advance machine i's field flux from its start-of-step value and recovered
        // d-axis current. T'do dE'q/dt = (Efd - (Xd - X'd)*Id) - E'q, the exact zero-order hold.
        double NextEqp(int i, double currentEqp, double id)
        {
            eqSeries[i].Add(currentEqp);
            return flux[i].Step(efd[i] - xdMinusXdpSys[i] * id, stepSeconds);
        }

        ClassicalMultiMachine.Result swing = ClassicalMultiMachine.Simulate(
            h, d, pm, eq0, delta0, yReduced, f0, stepSeconds, durationSeconds, fault, slipLimitRad,
            mechanicalPower: null, internalVoltage: NextEqp);

        var machines = new List<MachineTrace>(n);
        for (int i = 0; i < n; i++)
        {
            eqSeries[i].Add(flux[i].Output);   // E'q at the final sample (k = steps)
            ClassicalMultiMachine.MachineTrace m = swing.Machines[i];
            machines.Add(new MachineTrace(m.TimeSeconds, m.DeltaRad, m.SpeedPu, eqSeries[i]));
        }
        return new Result(machines, swing.StayedInSynchronism);
    }
}
