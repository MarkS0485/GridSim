using System;
using System.Numerics;

namespace GridSim.Core.Protection.ShortCircuit;

/// <summary>The result of a series (open-conductor) fault: sequence and phase currents through the break (pu or any
/// consistent unit) and the phase voltages across it.</summary>
public sealed record SeriesFaultResult(
    int OpenConductors, Complex I0, Complex I1, Complex I2,
    Complex Ia, Complex Ib, Complex Ic, Complex Va, Complex Vb, Complex Vc);

/// <summary>
/// Series faults (one or two conductors open) from the sequence Thevenin equivalents seen across the break.
/// Across the break, with the broken element removed, each sequence network is <c>V_k = E_k - Zs_k I_k</c> with
/// <c>E_1 = Zs_1 I_pre</c> (the prefault positive-sequence current through the element) and <c>E_2 = E_0 = 0</c>.
/// One conductor open (phase a): <c>I_a = 0</c> and <c>V_0 = V_1 = V_2</c>, so the three sequence networks are in
/// parallel: <c>I_1 = E_1 / (Zs_1 + Zs_2 || Zs_0)</c>, <c>I_2 = -I_1 Zs_0 / (Zs_0 + Zs_2)</c>,
/// <c>I_0 = -I_1 Zs_2 / (Zs_0 + Zs_2)</c>. Two conductors open (b and c): <c>I_0 = I_1 = I_2</c> and the networks are in
/// series: <c>I_1 = E_1 / (Zs_0 + Zs_1 + Zs_2)</c>. These are the textbook symmetrical-component results (Grainger and
/// Stevenson; Anderson), not a quotation of ENA ER G74.
/// </summary>
public static class SeriesFault
{
    private static readonly Complex A = new(-0.5, 0.8660254037844386);   // exp(j 120 deg): a constant, no transcendental call

    public static SeriesFaultResult OpenConductors(Complex iPrePositive, Complex zs1, Complex zs2, Complex zs0, int openConductors)
    {
        if (openConductors is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(openConductors), openConductors, "1 or 2 conductors open");
        Complex e1 = zs1 * iPrePositive;
        Complex i1, i2, i0;
        if (openConductors == 1)
        {
            Complex par = (zs2 * zs0) / (zs2 + zs0);
            i1 = e1 / (zs1 + par);
            i2 = -i1 * zs0 / (zs0 + zs2);
            i0 = -i1 * zs2 / (zs0 + zs2);
        }
        else
        {
            i1 = e1 / (zs0 + zs1 + zs2);
            i2 = i1; i0 = i1;
        }
        Complex v1 = e1 - zs1 * i1, v2 = -zs2 * i2, v0 = -zs0 * i0;
        Complex a2 = A * A;
        return new SeriesFaultResult(openConductors, i0, i1, i2,
            i0 + i1 + i2, i0 + a2 * i1 + A * i2, i0 + A * i1 + a2 * i2,
            v0 + v1 + v2, v0 + a2 * v1 + A * v2, v0 + A * v1 + a2 * v2);
    }
}
