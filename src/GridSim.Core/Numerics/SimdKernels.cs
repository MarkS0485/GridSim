using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GridSim.Core.Numerics;

/// <summary>
/// The shared vectorised double primitives every dense kernel in the solver and estimator builds
/// on: scaled-row subtract/add (the LU trailing-update and normal-equations rank-1 axpy) and dot
/// products. Written widest-first with explicit <see cref="Vector512{T}"/>/<see cref="Vector256{T}"/>/
/// <see cref="Vector128{T}"/> paths selected once by <see cref="SimdCapabilities.Tier"/>, so the same
/// source runs 8-wide on an AVX-512 EPYC, 4-wide on an AVX2 desktop, and falls back to scalar where
/// neither is present.
///
/// The core kernels take a <c>ref double</c> origin plus an element offset, so they drive both flat
/// <c>double[]</c> vectors AND the contiguous row-major storage of <c>double[,]</c> matrices (pass
/// <c>ref m[0,0]</c>): loads/stores use the safe <c>LoadUnsafe</c>/<c>StoreUnsafe</c> ref APIs, no
/// <c>unsafe</c> block, so the pedantic build stays clean. <c>double[]</c> convenience overloads
/// resolve the array's data reference for callers that hold a vector.
///
/// Rounding contracts (D1, October 2026, Mark's determinism rule 2026-10-04: identical bits on every CPU tier
/// and operating system):
/// <list type="bullet">
/// <item><b>Bit-exact at every width</b>: <see cref="SubtractScaledRow(double[],int,int,int,double)"/>,
/// <see cref="AddScaledRow"/> with <c>fastFma=false</c> and the complex column updates do a separate multiply
/// then add/subtract per lane, matching the scalar <c>x -= f*y</c> to the bit. <see cref="Dot"/> sums in one
/// DEFINED order (four partial sums, then the tail) that every width reproduces exactly, so it gives the same bits
/// on every CPU (it is not the left-to-right order of <see cref="DotExact"/>, which stays available).
/// <see cref="SinCos(double[],double[],double[],int)"/> is the deterministic fdlibm sin/cos per element.</item>
/// <item><b>Not for numerical paths</b>: <see cref="AddScaledRow"/> with <c>fastFma=true</c> fuses multiply and
/// add where the CPU has FMA, so its last bit depends on the CPU. No GridSim numerical path passes it
/// (<c>DeterministicMathGuardTests</c> fails if one does); it remains only as the tested R102 kernel arm.</item>
/// </list>
/// </summary>
internal static class SimdKernels
{
    // ---- scaled-row subtract: dst[t] -= factor*src[t] (bit-exact) --------------------------------

    /// <summary>Bit-exact <c>a[dstOff+t] -= factor*a[srcOff+t]</c>, t=0..count-1, over one flat array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractScaledRow(double[] a, int dstOff, int srcOff, int count, double factor)
    {
        ref double r = ref MemoryMarshal.GetArrayDataReference(a);
        SubtractScaledRow(ref r, dstOff, ref r, srcOff, count, factor);
    }

    /// <summary>Bit-exact <c>dst[dstOff+t] -= factor*src[srcOff+t]</c> across two flat arrays.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractScaledRow(double[] dst, int dstOff, double[] src, int srcOff, int count, double factor)
    {
        ref double dr = ref MemoryMarshal.GetArrayDataReference(dst);
        ref double sr = ref MemoryMarshal.GetArrayDataReference(src);
        SubtractScaledRow(ref dr, dstOff, ref sr, srcOff, count, factor);
    }

    /// <summary>Bit-exact <c>dst[dstOff+t] -= factor*src[srcOff+t]</c> over ref origins (1-D or 2-D storage).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractScaledRow(ref double dst, int dstOff, ref double src, int srcOff, int count, double factor)
        => SubtractScaledRow(ref dst, dstOff, ref src, srcOff, count, factor, SimdCapabilities.Tier);

    /// <summary>
    /// <see cref="SubtractScaledRow(ref double,int,ref double,int,int,double)"/> at an explicit vector
    /// <paramref name="tier"/>. Production code always passes <see cref="SimdCapabilities.Tier"/> (the overload
    /// above); the explicit form is the R102 test seam that runs EVERY tier's arm, including the AVX-512 arm on a
    /// CPU without AVX-512 (Vector512 then runs in software, slowly but correctly), so a broken or deleted arm
    /// fails a test on any machine instead of only on the hardware that would select it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractScaledRow(ref double dst, int dstOff, ref double src, int srcOff, int count, double factor, SimdTier tier)
    {
        int t = 0;
        switch (tier)
        {
            case SimdTier.Avx512:
            {
                var fv = Vector512.Create(factor);
                for (int limit = count - 8; t <= limit; t += 8)
                {
                    var dv = Vector512.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector512.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (dv - fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
            case SimdTier.Avx256:
            {
                var fv = Vector256.Create(factor);
                for (int limit = count - 4; t <= limit; t += 4)
                {
                    var dv = Vector256.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector256.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (dv - fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
            case SimdTier.Sse128:
            {
                var fv = Vector128.Create(factor);
                for (int limit = count - 2; t <= limit; t += 2)
                {
                    var dv = Vector128.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector128.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (dv - fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
        }
        for (; t < count; t++) Unsafe.Add(ref dst, dstOff + t) -= factor * Unsafe.Add(ref src, srcOff + t);
    }

    // ---- scaled-row add: dst[t] += factor*src[t] (bit-exact or FMA) ------------------------------

    /// <summary>Scaled-row add over one flat array pair.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddScaledRow(double[] dst, int dstOff, double[] src, int srcOff, int count, double factor, bool fastFma = false)
    {
        ref double dr = ref MemoryMarshal.GetArrayDataReference(dst);
        ref double sr = ref MemoryMarshal.GetArrayDataReference(src);
        AddScaledRow(ref dr, dstOff, ref sr, srcOff, count, factor, fastFma);
    }

    /// <summary>
    /// <c>dst[dstOff+t] += factor*src[srcOff+t]</c>, t=0..count-1, over ref origins. The rank-1
    /// accumulation dense <c>H^TWH</c> is built from. FMA when <paramref name="fastFma"/> and hardware allow, which makes
    /// the last bit CPU-dependent: numerical paths always pass <c>fastFma: false</c> (D1; guarded by
    /// <c>DeterministicMathGuardTests</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddScaledRow(ref double dst, int dstOff, ref double src, int srcOff, int count, double factor, bool fastFma = false)
        => AddScaledRow(ref dst, dstOff, ref src, srcOff, count, factor, fastFma, SimdCapabilities.Tier);

    /// <summary><see cref="AddScaledRow(ref double,int,ref double,int,int,double,bool)"/> at an explicit vector
    /// <paramref name="tier"/> (the R102 test seam; production passes <see cref="SimdCapabilities.Tier"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddScaledRow(ref double dst, int dstOff, ref double src, int srcOff, int count, double factor, bool fastFma, SimdTier tier)
    {
        bool fma = fastFma && SimdCapabilities.FmaAvailable;
        int t = 0;
        switch (tier)
        {
            case SimdTier.Avx512:
            {
                var fv = Vector512.Create(factor);
                for (int limit = count - 8; t <= limit; t += 8)
                {
                    var dv = Vector512.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector512.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (fma ? Vector512.FusedMultiplyAdd(fv, sv, dv) : dv + fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
            case SimdTier.Avx256:
            {
                var fv = Vector256.Create(factor);
                for (int limit = count - 4; t <= limit; t += 4)
                {
                    var dv = Vector256.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector256.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (fma ? Vector256.FusedMultiplyAdd(fv, sv, dv) : dv + fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
            case SimdTier.Sse128:
            {
                var fv = Vector128.Create(factor);
                for (int limit = count - 2; t <= limit; t += 2)
                {
                    var dv = Vector128.LoadUnsafe(ref dst, (nuint)(dstOff + t));
                    var sv = Vector128.LoadUnsafe(ref src, (nuint)(srcOff + t));
                    (fma ? Vector128.FusedMultiplyAdd(fv, sv, dv) : dv + fv * sv).StoreUnsafe(ref dst, (nuint)(dstOff + t));
                }
                break;
            }
        }
        if (fma)
            for (; t < count; t++) Unsafe.Add(ref dst, dstOff + t) = Math.FusedMultiplyAdd(factor, Unsafe.Add(ref src, srcOff + t), Unsafe.Add(ref dst, dstOff + t));
        else
            for (; t < count; t++) Unsafe.Add(ref dst, dstOff + t) += factor * Unsafe.Add(ref src, srcOff + t);
    }

    // ---- complex column update: dst[t] -= l[t]*u, skipping l[t] == 0 (bit-exact) -------------------

    /// <summary>
    /// N186 (October 2026): the inner update of the dense complex LU (<see cref="DenseComplexLuSolver.FactorDense"/>):
    /// <c>c[dstOff+t] -= c[lOff+t] * u</c> for t = 0..count-1, over ONE flat <see cref="System.Numerics.Complex"/> array,
    /// skipping every t whose multiplier <c>c[lOff+t]</c> equals zero (as the scalar loop's
    /// <c>if (factor == Complex.Zero) continue;</c> does; skipping is not the same as subtracting 0 * u, which can turn
    /// a -0 into +0). Bit-exact against the scalar
    /// <c>c[dst] -= c[l] * u</c> at every tier, because each lane does exactly the scalar's IEEE operations:
    /// <see cref="System.Numerics.Complex"/>'s product <c>l * u</c> is
    /// <c>(l.Re*u.Re - l.Im*u.Im, l.Im*u.Re + l.Re*u.Im)</c>, each product rounded on its own (no fused multiply-add;
    /// the JIT never contracts separate vector multiply and add), and the vector form computes the same four products,
    /// then the real part as <c>l.Re*u.Re + (-(l.Im*u.Im))</c> (IEEE defines x - y as x + (-y), and multiplying by -1 is
    /// exact) and the imaginary part as the same two-term sum (addition is commutative bit for bit), then
    /// <c>dst - product</c> per lane as the scalar does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SubtractScaledComplexColumn(System.Numerics.Complex[] c, int dstOff, int lOff, int count, System.Numerics.Complex u)
        => SubtractScaledComplexColumn(c, dstOff, lOff, count, u, SimdCapabilities.Tier);

    /// <summary><see cref="SubtractScaledComplexColumn(System.Numerics.Complex[],int,int,int,System.Numerics.Complex)"/> at an
    /// explicit vector <paramref name="tier"/> (the R102 test seam; production passes <see cref="SimdCapabilities.Tier"/>).</summary>
    internal static void SubtractScaledComplexColumn(System.Numerics.Complex[] c, int dstOff, int lOff, int count, System.Numerics.Complex u, SimdTier tier)
    {
        // The complex array viewed as interleaved doubles: element k is (Re, Im) at doubles 2k and 2k + 1.
        ref double d0 = ref Unsafe.As<System.Numerics.Complex, double>(ref MemoryMarshal.GetArrayDataReference(c));
        double ur = u.Real, ui = u.Imaginary;
        int t = 0;
        switch (tier)
        {
            case SimdTier.Avx512:
            {
                // 4 complex numbers per vector.
                var urv = Vector512.Create(ur);
                var uiv = Vector512.Create(ui);
                var sign = Vector512.Create(-1.0, 1.0, -1.0, 1.0, -1.0, 1.0, -1.0, 1.0);
                var swap = Vector512.Create(1L, 0L, 3L, 2L, 5L, 4L, 7L, 6L);
                for (int limit = count - 4; t <= limit; t += 4)
                {
                    var lv = Vector512.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var av = Vector512.LoadUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                    var ls = Vector512.Shuffle(lv, swap);                       // (Im, Re) per element
                    var prod = lv * urv + (ls * uiv) * sign;                   // (lr*ur - li*ui, li*ur + lr*ui)
                    var zero = Vector512.Equals(lv, Vector512<double>.Zero);
                    var skip = zero & Vector512.Shuffle(zero.AsInt64(), swap).AsDouble();   // both parts zero
                    Vector512.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                }
                break;
            }
            case SimdTier.Avx256:
            {
                // 2 complex numbers per vector.
                var urv = Vector256.Create(ur);
                var uiv = Vector256.Create(ui);
                var sign = Vector256.Create(-1.0, 1.0, -1.0, 1.0);
                var swap = Vector256.Create(1L, 0L, 3L, 2L);
                for (int limit = count - 2; t <= limit; t += 2)
                {
                    var lv = Vector256.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var av = Vector256.LoadUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                    var ls = Vector256.Shuffle(lv, swap);
                    var prod = lv * urv + (ls * uiv) * sign;
                    var zero = Vector256.Equals(lv, Vector256<double>.Zero);
                    var skip = zero & Vector256.Shuffle(zero.AsInt64(), swap).AsDouble();
                    Vector256.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                }
                break;
            }
            case SimdTier.Sse128:
            {
                // 1 complex number per vector.
                var urv = Vector128.Create(ur);
                var uiv = Vector128.Create(ui);
                var sign = Vector128.Create(-1.0, 1.0);
                var swap = Vector128.Create(1L, 0L);
                for (; t < count; t++)
                {
                    var lv = Vector128.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var av = Vector128.LoadUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                    var ls = Vector128.Shuffle(lv, swap);
                    var prod = lv * urv + (ls * uiv) * sign;
                    var zero = Vector128.Equals(lv, Vector128<double>.Zero);
                    var skip = zero & Vector128.Shuffle(zero.AsInt64(), swap).AsDouble();
                    Vector128.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * (dstOff + t)));
                }
                break;
            }
        }
        // Scalar tail (and the whole loop on the scalar tier): the original statement, verbatim.
        for (; t < count; t++)
        {
            System.Numerics.Complex factor = c[lOff + t];
            if (factor == System.Numerics.Complex.Zero) continue;
            c[dstOff + t] -= factor * u;
        }
    }

    /// <summary>
    /// N186: <see cref="SubtractScaledComplexColumn(System.Numerics.Complex[],int,int,int,System.Numerics.Complex)"/> for
    /// FOUR destination columns that share one multiplier column: for each column q = 0..3,
    /// <c>c[dstOff_q+t] -= c[lOff+t] * u_q</c>, skipping zero multipliers. Each multiplier vector is loaded, swapped and
    /// tested for zero once and used for all four columns (the LU's block update spends its time here). Every lane does
    /// the single-column kernel's operations, so the result is bit-for-bit four calls of the single-column kernel (and
    /// so of the scalar statement). The four destination ranges must not overlap one another or the multipliers.
    /// </summary>
    internal static void SubtractScaledComplexColumns4(System.Numerics.Complex[] c, int dst0, int dst1, int dst2, int dst3,
        int lOff, int count, System.Numerics.Complex u0, System.Numerics.Complex u1, System.Numerics.Complex u2,
        System.Numerics.Complex u3, SimdTier tier)
    {
        ref double d0 = ref Unsafe.As<System.Numerics.Complex, double>(ref MemoryMarshal.GetArrayDataReference(c));
        int t = 0;
        switch (tier)
        {
            case SimdTier.Avx512:
            {
                var swap = Vector512.Create(1L, 0L, 3L, 2L, 5L, 4L, 7L, 6L);
                var r0 = Vector512.Create(u0.Real); var i0 = SignedImag512(u0.Imaginary);
                var r1 = Vector512.Create(u1.Real); var i1 = SignedImag512(u1.Imaginary);
                var r2 = Vector512.Create(u2.Real); var i2 = SignedImag512(u2.Imaginary);
                var r3 = Vector512.Create(u3.Real); var i3 = SignedImag512(u3.Imaginary);
                for (int limit = count - 4; t <= limit; t += 4)
                {
                    var lv = Vector512.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var ls = Vector512.Shuffle(lv, swap);
                    var zero = Vector512.Equals(lv, Vector512<double>.Zero);
                    var skip = zero & Vector512.Shuffle(zero.AsInt64(), swap).AsDouble();
                    Step512(ref d0, dst0 + t, lv, ls, skip, r0, i0);
                    Step512(ref d0, dst1 + t, lv, ls, skip, r1, i1);
                    Step512(ref d0, dst2 + t, lv, ls, skip, r2, i2);
                    Step512(ref d0, dst3 + t, lv, ls, skip, r3, i3);
                }
                break;
            }
            case SimdTier.Avx256:
            {
                var swap = Vector256.Create(1L, 0L, 3L, 2L);
                var r0 = Vector256.Create(u0.Real); var i0 = SignedImag256(u0.Imaginary);
                var r1 = Vector256.Create(u1.Real); var i1 = SignedImag256(u1.Imaginary);
                var r2 = Vector256.Create(u2.Real); var i2 = SignedImag256(u2.Imaginary);
                var r3 = Vector256.Create(u3.Real); var i3 = SignedImag256(u3.Imaginary);
                for (int limit = count - 2; t <= limit; t += 2)
                {
                    var lv = Vector256.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var ls = Vector256.Shuffle(lv, swap);
                    var zero = Vector256.Equals(lv, Vector256<double>.Zero);
                    var skip = zero & Vector256.Shuffle(zero.AsInt64(), swap).AsDouble();
                    Step256(ref d0, dst0 + t, lv, ls, skip, r0, i0);
                    Step256(ref d0, dst1 + t, lv, ls, skip, r1, i1);
                    Step256(ref d0, dst2 + t, lv, ls, skip, r2, i2);
                    Step256(ref d0, dst3 + t, lv, ls, skip, r3, i3);
                }
                break;
            }
            case SimdTier.Sse128:
            {
                var swap = Vector128.Create(1L, 0L);
                var r0 = Vector128.Create(u0.Real); var i0 = SignedImag128(u0.Imaginary);
                var r1 = Vector128.Create(u1.Real); var i1 = SignedImag128(u1.Imaginary);
                var r2 = Vector128.Create(u2.Real); var i2 = SignedImag128(u2.Imaginary);
                var r3 = Vector128.Create(u3.Real); var i3 = SignedImag128(u3.Imaginary);
                for (; t < count; t++)
                {
                    var lv = Vector128.LoadUnsafe(ref d0, (nuint)(2 * (lOff + t)));
                    var ls = Vector128.Shuffle(lv, swap);
                    var zero = Vector128.Equals(lv, Vector128<double>.Zero);
                    var skip = zero & Vector128.Shuffle(zero.AsInt64(), swap).AsDouble();
                    Step128(ref d0, dst0 + t, lv, ls, skip, r0, i0);
                    Step128(ref d0, dst1 + t, lv, ls, skip, r1, i1);
                    Step128(ref d0, dst2 + t, lv, ls, skip, r2, i2);
                    Step128(ref d0, dst3 + t, lv, ls, skip, r3, i3);
                }
                break;
            }
        }
        // Scalar tail (and the whole loop on the scalar tier): the original statement, verbatim, per column.
        for (; t < count; t++)
        {
            System.Numerics.Complex factor = c[lOff + t];
            if (factor == System.Numerics.Complex.Zero) continue;
            c[dst0 + t] -= factor * u0;
            c[dst1 + t] -= factor * u1;
            c[dst2 + t] -= factor * u2;
            c[dst3 + t] -= factor * u3;
        }
    }

    // (-Im u, +Im u) per complex lane, made by MULTIPLYING by -1 (as the single-column kernel's "* sign" does) rather
    // than by unary minus, so even a NaN keeps the bits the multiply gives. l.Im * (Im u * -1) equals
    // (l.Im * Im u) * -1 bit for bit: round-to-nearest is symmetric in sign, and x86 returns a NaN operand unchanged
    // through a multiply. GR-874: that NaN argument is the x86 rule; arm64 (which is shipped) may produce the default NaN
    // instead, and NaN payload identity on arm64 is not tested. Finite values are bit-identical on both.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> SignedImag512(double ui) { double n = ui * -1.0; return Vector512.Create(n, ui, n, ui, n, ui, n, ui); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> SignedImag256(double ui) { double n = ui * -1.0; return Vector256.Create(n, ui, n, ui); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> SignedImag128(double ui) { double n = ui * -1.0; return Vector128.Create(n, ui); }

    // One destination column's lanes: (Re, Im) -= l * u, keeping lanes whose multiplier is zero.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step512(ref double d0, int dst, Vector512<double> lv, Vector512<double> ls, Vector512<double> skip,
        Vector512<double> ur, Vector512<double> uiSigned)
    {
        var av = Vector512.LoadUnsafe(ref d0, (nuint)(2 * dst));
        var prod = lv * ur + ls * uiSigned;
        Vector512.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * dst));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step256(ref double d0, int dst, Vector256<double> lv, Vector256<double> ls, Vector256<double> skip,
        Vector256<double> ur, Vector256<double> uiSigned)
    {
        var av = Vector256.LoadUnsafe(ref d0, (nuint)(2 * dst));
        var prod = lv * ur + ls * uiSigned;
        Vector256.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * dst));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Step128(ref double d0, int dst, Vector128<double> lv, Vector128<double> ls, Vector128<double> skip,
        Vector128<double> ur, Vector128<double> uiSigned)
    {
        var av = Vector128.LoadUnsafe(ref d0, (nuint)(2 * dst));
        var prod = lv * ur + ls * uiSigned;
        Vector128.ConditionalSelect(skip, av, av - prod).StoreUnsafe(ref d0, (nuint)(2 * dst));
    }

    // ---- dot products ---------------------------------------------------------------------------

    /// <summary>
    /// Dot product of <c>x[xOff+t]*y[yOff+t]</c>, t = 0..count-1, in GridSim's ONE fixed summation order (see
    /// <see cref="Dot(ref double,int,ref double,int,int,SimdTier)"/>): the same bits at every vector width and on
    /// every CPU.
    /// </summary>
    internal static double Dot(double[] x, int xOff, double[] y, int yOff, int count)
    {
        ref double xr = ref MemoryMarshal.GetArrayDataReference(x);
        ref double yr = ref MemoryMarshal.GetArrayDataReference(y);
        return Dot(ref xr, xOff, ref yr, yOff, count);
    }

    /// <summary>Fixed-order dot product over ref origins (see the tier overload for the order).</summary>
    internal static double Dot(ref double xr, int xOff, ref double yr, int yOff, int count)
        => Dot(ref xr, xOff, ref yr, yOff, count, SimdCapabilities.Tier);

    /// <summary>
    /// <see cref="Dot(ref double,int,ref double,int,int)"/> at an explicit vector <paramref name="tier"/> (the R102
    /// test seam; production passes <see cref="SimdCapabilities.Tier"/>).
    ///
    /// D1 (October 2026, Mark's determinism rule 2026-10-04; fixes R279 / R113's Dot half): the old kernel summed
    /// into as many partial sums as the vector had lanes (8, 4 or 2), used a fused multiply-add where the CPU had
    /// one, reduced the lanes in a width-specific order and left the tail to a scalar loop, so an AVX-512, an AVX2
    /// and a no-SIMD machine gave different last bits. Now the order is DEFINED, independent of the hardware:
    /// <list type="number">
    /// <item>Four partial sums s0..s3 start at +0. For t = 0 .. 4*floor(count/4) - 1, in increasing t, the product
    /// <c>x[t]*y[t]</c> (rounded on its own; never fused with the add) is added to s[t mod 4].</item>
    /// <item>The partials combine as <c>(s0 + s1) + (s2 + s3)</c>.</item>
    /// <item>The remaining count mod 4 products are added to that, left to right.</item>
    /// </list>
    /// Every tier performs exactly these IEEE operations: the 256-bit arm holds s0..s3 in one vector; the 512-bit arm
    /// multiplies eight elements at once and adds the low then the high four products into the same four partials
    /// (the same sequence of additions per partial); the 128-bit arm holds s0, s1 in one vector and s2, s3 in
    /// another; the scalar arm holds four doubles. Lane-wise vector add and multiply round exactly as the scalar
    /// operations do, so all four arms, on every CPU, return the same bits.
    /// <c>D1CrossTierTests.Dot_is_bit_identical_at_every_tier_and_equals_the_defined_order</c> proves it.
    /// </summary>
    internal static double Dot(ref double xr, int xOff, ref double yr, int yOff, int count, SimdTier tier)
    {
        int t = 0;
        int body = count & ~3;   // elements handled by the four partial sums
        double s0 = 0.0, s1 = 0.0, s2 = 0.0, s3 = 0.0;
        switch (tier)
        {
            case SimdTier.Avx512:
            {
                var acc = Vector256<double>.Zero;
                for (int limit = body - 8; t <= limit; t += 8)
                {
                    Vector512<double> p = Vector512.LoadUnsafe(ref xr, (nuint)(xOff + t)) * Vector512.LoadUnsafe(ref yr, (nuint)(yOff + t));
                    acc += p.GetLower();   // products t..t+3 into s0..s3
                    acc += p.GetUpper();   // then products t+4..t+7 into s0..s3
                }
                for (; t < body; t += 4)   // a last group of four when body is not a multiple of eight
                    acc += Vector256.LoadUnsafe(ref xr, (nuint)(xOff + t)) * Vector256.LoadUnsafe(ref yr, (nuint)(yOff + t));
                s0 = acc.GetElement(0); s1 = acc.GetElement(1); s2 = acc.GetElement(2); s3 = acc.GetElement(3);
                break;
            }
            case SimdTier.Avx256:
            {
                var acc = Vector256<double>.Zero;
                for (; t < body; t += 4)
                    acc += Vector256.LoadUnsafe(ref xr, (nuint)(xOff + t)) * Vector256.LoadUnsafe(ref yr, (nuint)(yOff + t));
                s0 = acc.GetElement(0); s1 = acc.GetElement(1); s2 = acc.GetElement(2); s3 = acc.GetElement(3);
                break;
            }
            case SimdTier.Sse128:
            {
                var lo = Vector128<double>.Zero;   // s0, s1
                var hi = Vector128<double>.Zero;   // s2, s3
                for (; t < body; t += 4)
                {
                    lo += Vector128.LoadUnsafe(ref xr, (nuint)(xOff + t)) * Vector128.LoadUnsafe(ref yr, (nuint)(yOff + t));
                    hi += Vector128.LoadUnsafe(ref xr, (nuint)(xOff + t + 2)) * Vector128.LoadUnsafe(ref yr, (nuint)(yOff + t + 2));
                }
                s0 = lo.GetElement(0); s1 = lo.GetElement(1); s2 = hi.GetElement(0); s3 = hi.GetElement(1);
                break;
            }
            default:
            {
                for (; t < body; t += 4)
                {
                    s0 += Unsafe.Add(ref xr, xOff + t) * Unsafe.Add(ref yr, yOff + t);
                    s1 += Unsafe.Add(ref xr, xOff + t + 1) * Unsafe.Add(ref yr, yOff + t + 1);
                    s2 += Unsafe.Add(ref xr, xOff + t + 2) * Unsafe.Add(ref yr, yOff + t + 2);
                    s3 += Unsafe.Add(ref xr, xOff + t + 3) * Unsafe.Add(ref yr, yOff + t + 3);
                }
                break;
            }
        }
        double s = (s0 + s1) + (s2 + s3);
        for (; t < count; t++) s += Unsafe.Add(ref xr, xOff + t) * Unsafe.Add(ref yr, yOff + t);
        return s;
    }

    /// <summary>Left-to-right scalar dot - the reference summation order.</summary>
    internal static double DotExact(double[] x, int xOff, double[] y, int yOff, int count)
    {
        double s = 0;
        for (int t = 0; t < count; t++) s += x[xOff + t] * y[yOff + t];
        return s;
    }

    // ---- batch sin/cos ------------------------------------------------------------------------------
    //
    // D1 (October 2026, Mark's determinism rule 2026-10-04; fixes R125 and the SinCos half of R279 / R113): the old
    // batch kernel ran a Cephes minimax polynomial on the first floor(n/w)*w angles (w = Vector<double>.Count, a
    // property of the machine) and handed the rest to Math.SinCos, the operating system's libm, so which formula an
    // angle got depended on the vector width and its bits on the OS. Every angle now goes through the same
    // deterministic fdlibm sin/cos as every other path in GridSim (DetMath.SinCos), whatever the tier, the batch
    // length or the angle's position in it.

    /// <summary>
    /// <c>(sinOut[t], cosOut[t]) = DetMath.SinCos(angles[t])</c> for t = 0..count-1: bit-identical to the scalar
    /// deterministic call for every element, on every CPU tier and operating system.
    /// </summary>
    internal static void SinCos(double[] angles, double[] sinOut, double[] cosOut, int count)
        => SinCos(angles, sinOut, cosOut, count, SimdCapabilities.Tier);

    /// <summary><see cref="SinCos(double[],double[],double[],int)"/> at an explicit vector <paramref name="tier"/>
    /// (the R102 test seam; production passes <see cref="SimdCapabilities.Tier"/>). Every tier gives the same bits.</summary>
    internal static void SinCos(double[] angles, double[] sinOut, double[] cosOut, int count, SimdTier tier)
    {
        _ = tier;   // one formula at every width (see the section comment)
        for (int t = 0; t < count; t++) (sinOut[t], cosOut[t]) = Fdlibm.SinCos(angles[t]);
    }
}
