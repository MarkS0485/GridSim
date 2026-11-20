using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Protection.ShortCircuit;

/// <summary>A solid fault type at one port of a simultaneous-fault study.</summary>
public enum PortFault { None, LineToGroundA, LineToLineBC, DoubleLineToGroundBC, ThreePhase }

/// <summary>Symmetric N-port sequence impedance data: each matrix is Z[p, q] for ports p, q.</summary>
public sealed record SequencePortImpedances(Complex[,] Positive, Complex[,] Negative, Complex[,] Zero)
{
    public int Ports => Positive.GetLength(0);
}

/// <summary>Per-port result: phase currents into the fault (a, b, c), the sequence currents and the phase voltages at the
/// port after the faults.</summary>
public sealed record PortFaultResult(Complex[] PhaseCurrents, Complex I0, Complex I1, Complex I2, Complex[] PhaseVoltages);

/// <summary>
/// Simultaneous faults at several ports (buses) over the sequence networks, solved in the phase domain:
/// <c>V = E - Z_abc I</c> with <c>Z_abc</c> the 3N x 3N phase-impedance matrix assembled from the sequence port matrices
/// (<c>T diag(Z1, Z2, Z0) T^-1</c> per port pair), the per-port boundary conditions of <see cref="PortFault"/> (rows over
/// the phase voltages and currents) and a dense complex Gaussian elimination with partial pivoting. The prefault voltage E
/// is the positive-sequence source voltage at each port (phase a reference).
/// </summary>
public static class SimultaneousFault
{
    private static readonly Complex A = new(-0.5, 0.8660254037844386);

    public static PortFaultResult[] Solve(SequencePortImpedances z, IReadOnlyList<Complex> prefault, IReadOnlyList<PortFault> faults)
    {
        int n = z.Ports;
        if (prefault.Count != n || faults.Count != n) throw new ArgumentException("one prefault voltage and one fault type per port");
        Complex a2 = A * A;
        // Phase-component transform; columns are the positive (1, a2, a), negative (1, a, a2) and zero (1, 1, 1) sets.
        Complex[,] t = { { 1, 1, 1 }, { a2, A, 1 }, { A, a2, 1 } };
        var tInv = new Complex[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                tInv[r, c] = Complex.Conjugate(t[c, r]) / 3.0;   // T^-1 = T^H / 3

        int m = 3 * n;
        var zabc = new Complex[m, m];
        for (int p = 0; p < n; p++)
            for (int q = 0; q < n; q++)
            {
                Complex[] d = { z.Positive[p, q], z.Negative[p, q], z.Zero[p, q] };
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        Complex s = Complex.Zero;
                        for (int k = 0; k < 3; k++) s += t[r, k] * d[k] * tInv[k, c];
                        zabc[3 * p + r, 3 * q + c] = s;
                    }
            }

        var vpre = new Complex[m];
        for (int p = 0; p < n; p++)
        {
            vpre[3 * p] = prefault[p]; vpre[3 * p + 1] = a2 * prefault[p]; vpre[3 * p + 2] = A * prefault[p];
        }

        // Each row is a condition cv . V + ci . I = 0 over the port's phases; V is replaced by vpre - Z I.
        var mat = new Complex[m, m + 1];
        for (int p = 0; p < n; p++)
            for (int row = 0; row < 3; row++)
            {
                (Complex[] cv, Complex[] ci) = Condition(faults[p], row);
                int r = 3 * p + row;
                Complex rhs = Complex.Zero;
                for (int ph = 0; ph < 3; ph++)
                {
                    if (cv[ph] != Complex.Zero)
                    {
                        rhs -= cv[ph] * vpre[3 * p + ph];
                        for (int j = 0; j < m; j++) mat[r, j] -= cv[ph] * zabc[3 * p + ph, j];
                    }
                    mat[r, 3 * p + ph] += ci[ph];
                }
                mat[r, m] = rhs;
            }
        Complex[] i = Gauss(mat, m);
        var res = new PortFaultResult[n];
        for (int p = 0; p < n; p++)
        {
            var ip = new[] { i[3 * p], i[3 * p + 1], i[3 * p + 2] };
            var vp = new Complex[3];
            for (int ph = 0; ph < 3; ph++)
            {
                Complex s = vpre[3 * p + ph];
                for (int j = 0; j < m; j++) s -= zabc[3 * p + ph, j] * i[j];
                vp[ph] = s;
            }
            Complex i1 = (ip[0] + A * ip[1] + a2 * ip[2]) / 3.0, i2 = (ip[0] + a2 * ip[1] + A * ip[2]) / 3.0, i0 = (ip[0] + ip[1] + ip[2]) / 3.0;
            res[p] = new PortFaultResult(ip, i0, i1, i2, vp);
        }
        return res;
    }

    private static (Complex[] V, Complex[] I) Condition(PortFault f, int row)
    {
        var v = new Complex[3];
        var i = new Complex[3];
        Complex one = Complex.One;
        switch (f)
        {
            case PortFault.None: i[row] = one; break;                                         // each phase current = 0
            case PortFault.LineToGroundA:
                if (row == 0) v[0] = one; else i[row] = one;                                  // Va = 0, Ib = 0, Ic = 0
                break;
            case PortFault.LineToLineBC:
                if (row == 0) i[0] = one;                                                     // Ia = 0
                else if (row == 1) { i[1] = one; i[2] = one; }                                // Ib + Ic = 0
                else { v[1] = one; v[2] = -one; }                                             // Vb = Vc
                break;
            case PortFault.DoubleLineToGroundBC:
                if (row == 0) i[0] = one; else v[row] = one;                                  // Ia = 0, Vb = 0, Vc = 0
                break;
            case PortFault.ThreePhase: v[row] = one; break;                                   // Va = Vb = Vc = 0
        }
        return (v, i);
    }

    private static Complex[] Gauss(Complex[,] a, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (a[r, c].Magnitude > a[piv, c].Magnitude) piv = r;
            if (a[piv, c].Magnitude < 1e-300) throw new InvalidOperationException("the fault boundary conditions are singular");
            if (piv != c) for (int j = c; j <= n; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
            for (int r = c + 1; r < n; r++)
            {
                Complex f = a[r, c] / a[c, c];
                if (f == Complex.Zero) continue;
                for (int j = c; j <= n; j++) a[r, j] -= f * a[c, j];
            }
        }
        var x = new Complex[n];
        for (int r = n - 1; r >= 0; r--)
        {
            Complex s = a[r, n];
            for (int j = r + 1; j < n; j++) s -= a[r, j] * x[j];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
