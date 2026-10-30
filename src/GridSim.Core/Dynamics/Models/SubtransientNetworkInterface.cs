using System;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The <b>salient / subtransient multi-machine network interface</b> - the dq-frame current-injection core
/// that carries the two-axis machine (<see cref="TwoAxisMachine"/> / <see cref="SubtransientMachine"/>) into a
/// coupled network. It is the generalisation of <see cref="MultiMachineFluxDecay.NetworkInjection"/> from a
/// round-rotor internal voltage <c>E'q&#8736;delta</c> to a full two-axis internal voltage with both a d- and
/// a q-axis component, so a machine that is <b>not</b> a simple voltage-behind-one-reactance still enters the
/// network in closed form.
///
/// <para>Each machine presents at its internal node the subtransient EMF <c>(E''q, E''d)</c> in its own rotor
/// frame; rotated into the common network frame by the load angle <c>delta</c> it is the phasor</para>
/// <code>
///   Vr_i = E''q_i*cos(delta_i) + E''d_i*sin(delta_i)
///   Vi_i = E''q_i*sin(delta_i) - E''d_i*cos(delta_i)
/// </code>
/// (so a pure q-axis EMF, <c>E''d = 0</c>, is exactly <c>E''q&#8736;delta</c> - the round-rotor case). With the
/// subtransient reactance <c>X''</c> folded into the reduced admittance <c>Y</c> (as
/// <see cref="Distributed.ClassicalInit"/> folds <c>X'd</c>), the injected current is one matrix-vector
/// product <c>I_i = sum_j Y_ij V_j</c>, the air-gap power is <c>Pe_i = Re(V_i * conj(I_i))</c>, and the dq
/// currents that drive the flux decay are recovered by the inverse rotation
/// <code>
///   Iq_i = Ir_i*cos(delta_i) + Ii_i*sin(delta_i)
///   Id_i = Ir_i*sin(delta_i) - Ii_i*cos(delta_i)
/// </code>
/// so there is <b>no iterative network solve</b> - the whole interface is closed form and deterministic. This
/// interface requires a round <b>subtransient</b> reactance (<c>X''d = X''q</c>, folded into <c>Y</c>); the
/// internal saliency (<c>Xd</c>, <c>Xq</c>, <c>X'd</c>, <c>X'q</c> all distinct) lives in the per-machine flux
/// dynamics, exactly as at SMIB scale.
///
/// <para>It reduces to <see cref="MultiMachineFluxDecay.NetworkInjection"/> field-for-field when every
/// <c>E''d = 0</c>: both <c>Pe</c> and the recovered <c>Id = sum_j E''q_j (G_ij sin(delta_ij) - B_ij
/// cos(delta_ij))</c> agree with the round-rotor closed form. For a lossless reduced network the per-machine
/// air-gap powers sum to zero (the machines only exchange power). These are the oracles it is validated
/// against.</para>
///
/// <para>An optional <c>extraCurrent</c> adds a current to each machine's injection before the power and dq
/// currents are formed - the coupling term <c>Ymc Ycc^-1 Ic</c> through which converter current sources on
/// retained buses (<see cref="ConverterNetwork"/>) act on the machines. Null leaves the closed form as is.</para>
/// </summary>
public static class SubtransientNetworkInterface
{
    /// <summary>The per-machine air-gap power and the recovered dq stator currents for a set of subtransient
    /// internal EMFs and rotor angles, given the reduced admittance (with <c>X''</c> folded in). One
    /// matrix-vector product; closed form and deterministic.</summary>
    public static (double[] Pe, double[] Id, double[] Iq) Inject(
        double[] eqpp, double[] edpp, double[] delta, Complex[,] yReduced, Complex[]? extraCurrent = null)
    {
        (double[] vr, double[] vi, double[] ir, double[] ii) = Solve(eqpp, edpp, delta, yReduced, extraCurrent);
        int n = eqpp.Length;
        var pe = new double[n];
        var id = new double[n];
        var iq = new double[n];
        for (int i = 0; i < n; i++)
        {
            pe[i] = vr[i] * ir[i] + vi[i] * ii[i];            // Re(V_i conj(I_i))
            (double s, double c) = DetMath.SinCos(delta[i]);
            iq[i] = ir[i] * c + ii[i] * s;                    // inverse rotation into the machine frame
            id[i] = ir[i] * s - ii[i] * c;
        }
        return (pe, id, iq);
    }

    /// <summary>
    /// The machine <b>terminal-voltage</b> magnitudes (pu) - the signal an AVR regulates - recovered from the
    /// same solve as <see cref="Inject"/>. The terminal voltage is the internal EMF minus the drop back across
    /// the (folded) subtransient reactance: <c>V_t = E'' - j X'' I</c>, with <paramref name="subtransientXppSys"/>
    /// the per-machine round subtransient reactance <c>X''</c> (on the system base) folded into
    /// <paramref name="yReduced"/>. This is why the reduced internal-node network need not carry the terminal
    /// buses to regulate them. With <c>X'' = 0</c> it reduces to <c>|E''| = sqrt(E''q^2 + E''d^2)</c>.
    /// </summary>
    public static double[] TerminalVoltageMagnitudes(
        double[] eqpp, double[] edpp, double[] delta, Complex[,] yReduced, double[] subtransientXppSys,
        Complex[]? extraCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(subtransientXppSys);
        int n = eqpp.Length;
        if (subtransientXppSys.Length != n)
            throw new ArgumentException("subtransientXppSys must align to the machines.", nameof(subtransientXppSys));
        (double[] vr, double[] vi, double[] ir, double[] ii) = Solve(eqpp, edpp, delta, yReduced, extraCurrent);
        var vt = new double[n];
        for (int i = 0; i < n; i++)
        {
            // V_t = E'' - jX''I  =>  (Vr + X''*Ii) + j(Vi - X''*Ir).
            double tr = vr[i] + subtransientXppSys[i] * ii[i];
            double ti = vi[i] - subtransientXppSys[i] * ir[i];
            vt[i] = Math.Sqrt(tr * tr + ti * ti);
        }
        return vt;
    }

    /// <summary>The internal EMF phasors (Vr, Vi) and the injected currents (Ir, Ii) in the network frame -
    /// the shared solve behind <see cref="Inject"/> and <see cref="TerminalVoltageMagnitudes"/>.</summary>
    private static (double[] Vr, double[] Vi, double[] Ir, double[] Ii) Solve(
        double[] eqpp, double[] edpp, double[] delta, Complex[,] yReduced, Complex[]? extraCurrent)
    {
        ArgumentNullException.ThrowIfNull(eqpp);
        ArgumentNullException.ThrowIfNull(edpp);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(yReduced);
        int n = eqpp.Length;
        if (edpp.Length != n || delta.Length != n)
            throw new ArgumentException("eqpp, edpp and delta must have the same length.");
        if (yReduced.GetLength(0) != n || yReduced.GetLength(1) != n)
            throw new ArgumentException("yReduced must be an nxn matrix.", nameof(yReduced));

        var vr = new double[n];
        var vi = new double[n];
        for (int i = 0; i < n; i++)
        {
            (double s, double c) = DetMath.SinCos(delta[i]);
            vr[i] = eqpp[i] * c + edpp[i] * s;
            vi[i] = eqpp[i] * s - edpp[i] * c;
        }
        var ir = new double[n];
        var ii = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sr = 0.0, si = 0.0;
            for (int j = 0; j < n; j++)
            {
                double g = yReduced[i, j].Real, b = yReduced[i, j].Imaginary;
                sr += g * vr[j] - b * vi[j];
                si += g * vi[j] + b * vr[j];
            }
            ir[i] = sr; ii[i] = si;
        }
        if (extraCurrent is not null)
            for (int i = 0; i < n; i++) { ir[i] += extraCurrent[i].Real; ii[i] += extraCurrent[i].Imaginary; }
        return (vr, vi, ir, ii);
    }

    /// <summary>The internal EMF phasors <c>E''</c> in the network frame (the rotation <see cref="Inject"/> uses).</summary>
    public static Complex[] EmfPhasors(double[] eqpp, double[] edpp, double[] delta)
    {
        var e = new Complex[eqpp.Length];
        EmfPhasorsInto(eqpp, edpp, delta, e);
        return e;
    }

    /// <summary><see cref="EmfPhasors"/> into a caller array of the same length (R115: same loop, same bits).</summary>
    public static void EmfPhasorsInto(double[] eqpp, double[] edpp, double[] delta, Complex[] e)
    {
        int n = eqpp.Length;
        if (e.Length != n) throw new ArgumentException($"e holds {e.Length} entries; expected {n}.", nameof(e));
        for (int i = 0; i < n; i++)
        {
            (double s, double c) = DetMath.SinCos(delta[i]);
            e[i] = new Complex(eqpp[i] * c + edpp[i] * s, eqpp[i] * s - edpp[i] * c);
        }
    }
}
