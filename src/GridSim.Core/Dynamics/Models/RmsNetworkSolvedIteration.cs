using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// One pass of the RMS kernel's converter / network iteration for the units that solve their own network interface
/// (<see cref="IRmsNetworkSolvedConverterModel"/>, the WECC REGC_B voltage source: DECISIONS D36 of the WECC expansion).
///
/// <para>Each such unit u at converter node c sees its node as <c>V = a + Zcc I_u</c>, with <c>a = V_last - Zcc I_u,last</c>
/// the voltage of the last network solution without its own injection, and returns the injection phi_u(a) that solves its
/// own model exactly. With ONE such unit that is the whole pass (the network is linear, so the next solution reproduces it
/// and the iteration ends). With several, each unit's a depends on the others' injections through the network,
/// <c>a_u = a_u,last + sum over w != u of Z_uw dI_w</c>, and holding the others fixed (block Jacobi or Gauss-Seidel) converges
/// only linearly, slowly when two terminal-frame sources are electrically close (measured on WSCC-9 with two units: about 13
/// passes per step, and the 50-pass cap hit at a fault). So the pass takes one Newton step on the injections instead:
/// <c>dI_u - S_u(sum over w != u of Z_uw dI_w) = phi_u - I_u,last</c>, where S_u, the real 2 x 2 sensitivity of phi_u to a,
/// comes from two extra solves with a moved by 1e-7 |a| (real and imaginary). The linear system (2 x units reals) is solved by
/// Gaussian elimination with partial pivoting; a singular system falls back to the Jacobi update. The kernel's convergence
/// test (the change of the network solution) stays the judge, so the Newton step only decides how fast it is met.</para>
/// </summary>
internal sealed class RmsNetworkSolvedIteration
{
    private readonly int[] _unit;      // kernel unit index of each network-solved unit
    private readonly int[] _node;      // its converter node
    private readonly IRmsNetworkSolvedConverterModel[] _model;
    private readonly Complex[] _a, _phi, _dR, _dI;
    private readonly double[,] _m;
    private readonly double[] _x;

    public RmsNetworkSolvedIteration(IRmsNetworkSolvedConverterModel?[] solved, int[] nodeOf)
    {
        var u = new List<int>();
        for (int i = 0; i < solved.Length; i++) if (solved[i] is not null) u.Add(i);
        int m = u.Count;
        if (m == 0) throw new ArgumentException("At least one network-solved unit is needed.", nameof(solved));
        _unit = u.ToArray();
        _node = new int[m];
        _model = new IRmsNetworkSolvedConverterModel[m];
        for (int i = 0; i < m; i++) { _node[i] = nodeOf[_unit[i]]; _model[i] = solved[_unit[i]]!; }
        _a = new Complex[m]; _phi = new Complex[m]; _dR = new Complex[m]; _dI = new Complex[m];
        _m = new double[2 * m, 2 * m];
        _x = new double[2 * m];
    }

    /// <summary>Replace the network-solved units' entries of <paramref name="icUnit"/> (each unit's injection of the pass that
    /// produced <paramref name="vConv"/>) with this pass's injections. <paramref name="columns"/> holds, for every node of a
    /// network-solved unit, the converter-node voltages per unit current injected there.</summary>
    public void Update(Complex[] vConv, Complex[]?[] columns, Complex[] icUnit)
    {
        int m = _unit.Length;
        for (int i = 0; i < m; i++)
        {
            Complex zdd = columns[_node[i]]![_node[i]];
            _a[i] = vConv[_node[i]] - zdd * icUnit[_unit[i]];
        }
        if (m == 1)
        {
            icUnit[_unit[0]] = _model[0].NetworkSolvedCurrent(_a[0], columns[_node[0]]![_node[0]]);
            return;
        }
        for (int i = 0; i < m; i++)
        {
            Complex zdd = columns[_node[i]]![_node[i]];
            double h = 1e-7 * Math.Max(1.0, _a[i].Magnitude);
            Complex pr = _model[i].NetworkSolvedCurrent(_a[i] + h, zdd);
            Complex pi = _model[i].NetworkSolvedCurrent(_a[i] + new Complex(0.0, h), zdd);
            _phi[i] = _model[i].NetworkSolvedCurrent(_a[i], zdd);   // last, so the unit keeps the frame choice of the unmoved a
            _dR[i] = (pr - _phi[i]) / h;
            _dI[i] = (pi - _phi[i]) / h;
        }
        // Rows 2i, 2i+1: real and imaginary parts of dI_i - S_i(sum_j Z_ij dI_j) = phi_i - I_i, unknowns x_2j + j x_2j+1 = dI_j.
        Array.Clear(_m);
        for (int i = 0; i < m; i++)
        {
            _m[2 * i, 2 * i] = 1.0;
            _m[2 * i + 1, 2 * i + 1] = 1.0;
            Complex r = _phi[i] - icUnit[_unit[i]];
            _x[2 * i] = r.Real;
            _x[2 * i + 1] = r.Imaginary;
            for (int j = 0; j < m; j++)
            {
                if (j == i) continue;
                Complex z = columns[_node[j]]![_node[i]];   // voltage at node i per unit current at node j
                // S_i(w) = dR Re(w) + dI Im(w), w = z dI_j: Re(w) = zr x - zi y, Im(w) = zi x + zr y.
                Complex cx = _dR[i] * z.Real + _dI[i] * z.Imaginary;
                Complex cy = -_dR[i] * z.Imaginary + _dI[i] * z.Real;
                _m[2 * i, 2 * j] -= cx.Real; _m[2 * i, 2 * j + 1] -= cy.Real;
                _m[2 * i + 1, 2 * j] -= cx.Imaginary; _m[2 * i + 1, 2 * j + 1] -= cy.Imaginary;
            }
        }
        if (!Solve(_m, _x))
        {
            for (int i = 0; i < m; i++) icUnit[_unit[i]] = _phi[i];   // singular: the Jacobi update
            return;
        }
        for (int i = 0; i < m; i++) icUnit[_unit[i]] += new Complex(_x[2 * i], _x[2 * i + 1]);
    }

    // Gaussian elimination with partial pivoting, in place; false when a pivot vanishes.
    private static bool Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double best = Math.Abs(a[k, k]);
            for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > best) { best = Math.Abs(a[i, k]); p = i; }
            if (!(best > 1e-300)) return false;
            if (p != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
                (b[k], b[p]) = (b[p], b[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                double f = a[i, k] / a[k, k];
                if (f == 0.0) continue;
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        }
        for (int k = n - 1; k >= 0; k--)
        {
            double s = b[k];
            for (int j = k + 1; j < n; j++) s -= a[k, j] * b[j];
            b[k] = s / a[k, k];
        }
        for (int k = 0; k < n; k++) if (!double.IsFinite(b[k])) return false;
        return true;
    }
}
