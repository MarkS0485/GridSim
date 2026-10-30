using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Models;

/// <summary>
/// The machine-only reduced admittance of a dense RMS network, held for a fast and BIT-IDENTICAL product
/// <c>I_m = Y E</c> (R116, October 2026 review, S5).
///
/// <para>The historical product (<see cref="DenseMachineNetwork.MultiplyMachineInto"/>) walks <c>Complex[,]</c> one
/// element at a time and accumulates each machine current over the columns in ascending order:
/// <c>sr += g*Er - b*Ei; si += g*Ei + b*Er</c>, starting from +0. The summation order of a row is what the bits
/// depend on, so the usual vectorisation (splitting a row's sum across lanes) would change them. This class instead
/// vectorises ACROSS ROWS: the real parts G and imaginary parts B are copied once, column-major, into two flat arrays,
/// so the entries of column j for consecutive rows sit side by side, and each vector lane carries one row's own
/// running sums through the columns in the same ascending order. Every lane does, per column, exactly the scalar
/// operations in the scalar order - multiply, multiply, subtract (or add), then add to the running sum - with a
/// separate rounding at each step (never a fused multiply-add), so every machine current is the scalar result to the
/// bit at every vector width (AVX-512, AVX2, SSE, and the scalar loop for the rows left over and on CPUs without
/// vector units). The tiers are selected once by <see cref="SimdCapabilities.Tier"/>; each arm is exercised on any
/// machine through the explicit-tier overload (a test seam, as for <see cref="SimdKernels"/>).</para>
///
/// <para>The copy is taken at construction: the network multiplies by the admittance it was built with, as every
/// caller assumes (the RMS integrator builds one network per admittance stage and never edits the matrix).</para>
/// </summary>
internal sealed class MachineAdmittanceColumns
{
    private readonly int _n;
    private readonly double[] _gT;   // _gT[j * n + i] = Re Y[i, j]
    private readonly double[] _bT;   // _bT[j * n + i] = Im Y[i, j]

    public MachineAdmittanceColumns(Complex[,] y)
    {
        ArgumentNullException.ThrowIfNull(y);
        int n = y.GetLength(0);
        if (y.GetLength(1) != n) throw new ArgumentException("The admittance must be square.", nameof(y));
        _n = n;
        _gT = new double[n * n];
        _bT = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                _gT[j * n + i] = y[i, j].Real;
                _bT[j * n + i] = y[i, j].Imaginary;
            }
    }

    /// <summary>Number of machines (rows and columns).</summary>
    public int Size => _n;

    /// <summary><c>im = Y e</c>, bit-identical to <see cref="DenseMachineNetwork.MultiplyMachineInto"/>, at the
    /// process's vector tier. <paramref name="e"/> and <paramref name="im"/> hold <see cref="Size"/> entries and are
    /// different arrays (checked by the callers).</summary>
    public void MultiplyInto(Complex[] e, Complex[] im) => MultiplyInto(e, im, SimdCapabilities.Tier);

    /// <summary><see cref="MultiplyInto(Complex[], Complex[])"/> at an explicit <paramref name="tier"/> (test seam:
    /// runs every arm on any CPU; a Vector512 arm without AVX-512 runs in software, slowly but correctly).</summary>
    internal void MultiplyInto(Complex[] e, Complex[] im, SimdTier tier)
    {
        int n = _n;
        ref double g0 = ref MemoryMarshal.GetArrayDataReference(_gT);
        ref double b0 = ref MemoryMarshal.GetArrayDataReference(_bT);
        int i = 0;
        switch (tier)
        {
            case SimdTier.Avx512:
                for (; i <= n - 8; i += 8)
                {
                    Vector512<double> sr = Vector512<double>.Zero, si = Vector512<double>.Zero;   // +0, as the scalar 0.0
                    for (int j = 0; j < n; j++)
                    {
                        var er = Vector512.Create(e[j].Real);
                        var ei = Vector512.Create(e[j].Imaginary);
                        var g = Vector512.LoadUnsafe(ref g0, (nuint)(j * n + i));
                        var b = Vector512.LoadUnsafe(ref b0, (nuint)(j * n + i));
                        sr += g * er - b * ei;   // per lane: sr + ((g*er) - (b*ei)), each step rounded
                        si += g * ei + b * er;   // per lane: si + ((g*ei) + (b*er))
                    }
                    for (int l = 0; l < 8; l++) im[i + l] = new Complex(sr.GetElement(l), si.GetElement(l));
                }
                break;
            case SimdTier.Avx256:
                for (; i <= n - 4; i += 4)
                {
                    Vector256<double> sr = Vector256<double>.Zero, si = Vector256<double>.Zero;
                    for (int j = 0; j < n; j++)
                    {
                        var er = Vector256.Create(e[j].Real);
                        var ei = Vector256.Create(e[j].Imaginary);
                        var g = Vector256.LoadUnsafe(ref g0, (nuint)(j * n + i));
                        var b = Vector256.LoadUnsafe(ref b0, (nuint)(j * n + i));
                        sr += g * er - b * ei;
                        si += g * ei + b * er;
                    }
                    for (int l = 0; l < 4; l++) im[i + l] = new Complex(sr.GetElement(l), si.GetElement(l));
                }
                break;
            case SimdTier.Sse128:
                for (; i <= n - 2; i += 2)
                {
                    Vector128<double> sr = Vector128<double>.Zero, si = Vector128<double>.Zero;
                    for (int j = 0; j < n; j++)
                    {
                        var er = Vector128.Create(e[j].Real);
                        var ei = Vector128.Create(e[j].Imaginary);
                        var g = Vector128.LoadUnsafe(ref g0, (nuint)(j * n + i));
                        var b = Vector128.LoadUnsafe(ref b0, (nuint)(j * n + i));
                        sr += g * er - b * ei;
                        si += g * ei + b * er;
                    }
                    for (int l = 0; l < 2; l++) im[i + l] = new Complex(sr.GetElement(l), si.GetElement(l));
                }
                break;
        }

        // Remaining rows (all rows on the scalar tier): the historical loop's arithmetic, from the column-major copy.
        for (; i < n; i++)
        {
            double sr = 0.0, si = 0.0;
            for (int j = 0; j < n; j++)
            {
                double g = Unsafe.Add(ref g0, j * n + i), b = Unsafe.Add(ref b0, j * n + i);
                sr += g * e[j].Real - b * e[j].Imaginary;
                si += g * e[j].Imaginary + b * e[j].Real;
            }
            im[i] = new Complex(sr, si);
        }
    }
}
