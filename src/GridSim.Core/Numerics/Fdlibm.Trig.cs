// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// k_tan.c carries the 2004 form of the same notice:
// ====================================================
// Copyright 2004 Sun Microsystems, Inc.  All Rights Reserved.
//
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// C# port for GridSim (D1, October 2026) of FDLIBM 5.3 (netlib.org/fdlibm): k_sin.c 1.3, k_cos.c 1.3, k_tan.c 1.5,
// e_rem_pio2.c 1.4, k_rem_pio2.c 1.3, s_sin.c 1.3, s_cos.c 1.3, s_tan.c 1.3 and s_scalbn.c 1.3. See
// THIRD-PARTY-NOTICES.md. The port was made from the original C files only.

using System.Runtime.CompilerServices;

namespace GridSim.Core.Numerics;

/// <summary>
/// D1 (October 2026, Mark's determinism rule 2026-10-04): a line-by-line C# port of Sun's FDLIBM 5.3, the
/// "freely distributable libm" that Java's <c>StrictMath</c> is defined by. Every routine is plain IEEE 754 double
/// arithmetic (add, subtract, multiply, divide, the correctly rounded square root) plus integer work on the two 32-bit
/// halves of a double, with no fused multiply-add and no lookup of the operating system's maths library. IEEE 754
/// fixes the result of each of those operations to the bit, and .NET's JIT never fuses or reorders floating-point
/// operations, so the same input gives the same output bits on every CPU (AVX-512, AVX2, no SIMD; x64 or ARM64) and
/// every operating system (Windows, Linux, macOS). That is the property System.Math lacks: its transcendental
/// functions call the C runtime of the host operating system, which rounds some results differently in the last
/// place (N175).
///
/// Structure: each fdlibm C function becomes one C# method with the same name in PascalCase, the same constants
/// (written exactly as the C source writes them, with the source's hexadecimal bit patterns in the comments), the
/// same statement order and the same branch structure, so the port can be read side by side with the C file it cites.
/// Where C relies on a feature C# lacks (writing one 32-bit half of a double in place, unsigned / signed mixing, the
/// <c>goto recompute</c> loop), the comment at that line says what was done. The public front door is
/// <see cref="DetMath"/>; callers never use this class directly.
///
/// Proof: <c>FdlibmStrictMathVectorTests</c> checks every routine bit for bit against outputs of Java 21's
/// <c>StrictMath</c> (Eclipse Temurin 21.0.12.1) over special values and tens of thousands of arguments.
/// </summary>
internal static partial class Fdlibm
{
    // ---- access to the two 32-bit halves of a double (fdlibm.h's __HI(x) and __LO(x)) ----------------------------
    // fdlibm reads and writes the high and low 32-bit words of a double through pointer casts. Here the words are
    // taken from, and put back into, the 64-bit pattern with BitConverter, which is an exact reinterpretation.

    /// <summary>__HI(x): the high 32 bits (sign, exponent, top 20 bits of the significand) as a signed int.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hi(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);

    /// <summary>__LO(x): the low 32 bits of the significand as an unsigned int (fdlibm declares it unsigned).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Lo(double x) => (uint)BitConverter.DoubleToInt64Bits(x);

    /// <summary>The double whose high word is <paramref name="hi"/> and low word is <paramref name="lo"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Make(int hi, uint lo) => BitConverter.Int64BitsToDouble((long)(((ulong)(uint)hi << 32) | lo));

    /// <summary>C's <c>__HI(x) = hi;</c>: <paramref name="x"/> with its high word replaced, low word kept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SetHi(double x, int hi) => Make(hi, Lo(x));

    /// <summary>C's <c>__LO(x) = lo;</c>: <paramref name="x"/> with its low word replaced, high word kept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SetLo(double x, uint lo) => Make(Hi(x), lo);

    // ---- s_scalbn.c: scalbn(x, n) = x * 2^n computed by exponent manipulation -------------------------------------

    private const double Two54 = 1.80143985094819840000e+16;   // 0x43500000, 0x00000000
    private const double Twom54 = 5.55111512312578270212e-17;  // 0x3C900000, 0x00000000
    private const double ScalbnHuge = 1.0e+300;
    private const double ScalbnTiny = 1.0e-300;

    /// <summary>
    /// s_scalbn.c <c>scalbn(x, n)</c>: <paramref name="x"/> times 2 to the <paramref name="n"/>, by adjusting the
    /// exponent field directly (one rounding at most, when the result is subnormal). Ported rather than taken from
    /// <see cref="Math.ScaleB"/> so that the whole module is self-contained fdlibm.
    /// </summary>
    internal static double ScaleBn(double x, int n)
    {
        int k, hx;
        uint lx;
        hx = Hi(x);
        lx = Lo(x);
        k = (hx & 0x7ff00000) >> 20;                     // extract exponent
        if (k == 0)
        {                                               // 0 or subnormal x
            if ((lx | (uint)(hx & 0x7fffffff)) == 0) return x;   // +-0
            x *= Two54;
            hx = Hi(x);
            k = ((hx & 0x7ff00000) >> 20) - 54;
            if (n < -50000) return ScalbnTiny * x;      // underflow
        }
        if (k == 0x7ff) return x + x;                   // NaN or Inf
        k = k + n;
        if (k > 0x7fe) return ScalbnHuge * Math.CopySign(ScalbnHuge, x);   // overflow (copysign is a sign-bit copy)
        if (k > 0)                                      // normal result
            return SetHi(x, (hx & unchecked((int)0x800fffff)) | (k << 20));
        if (k <= -54)
        {
            // C: "if (n > 50000) ... else ..." is the inner if's else (the dangling-else rule).
            if (n > 50000) return ScalbnHuge * Math.CopySign(ScalbnHuge, x);   // in case integer overflow in n+k
            else return ScalbnTiny * Math.CopySign(ScalbnTiny, x);            // underflow
        }
        k += 54;                                        // subnormal result
        x = SetHi(x, (hx & unchecked((int)0x800fffff)) | (k << 20));
        return x * Twom54;
    }

    // ---- k_sin.c: kernel sin on [-pi/4, pi/4] ----------------------------------------------------------------------

    private const double Half = 5.00000000000000000000e-01;   // 0x3FE00000, 0x00000000
    private const double S1 = -1.66666666666666324348e-01;    // 0xBFC55555, 0x55555549
    private const double S2 = 8.33333333332248946124e-03;     // 0x3F811111, 0x1110F8A6
    private const double S3 = -1.98412698298579493134e-04;    // 0xBF2A01A0, 0x19C161D5
    private const double S4 = 2.75573137070700676789e-06;     // 0x3EC71DE3, 0x57B1FE7D
    private const double S5 = -2.50507602534068634195e-08;    // 0xBE5AE5E6, 0x8A2B9CEB
    private const double S6 = 1.58969099521155010221e-10;     // 0x3DE5D93A, 0x5ACFD57C

    /// <summary>
    /// k_sin.c <c>__kernel_sin(x, y, iy)</c>: sin(x + y) for |x| up to about pi/4, where y is the tail of the
    /// reduced argument and <paramref name="iy"/> says whether y is zero (0) or not (1). A degree-13 odd polynomial;
    /// the two return formulas are fdlibm's and are kept apart because they round differently.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double KernelSin(double x, double y, int iy)
    {
        double z, r, v;
        int ix;
        ix = Hi(x) & 0x7fffffff;                        // high word of x
        if (ix < 0x3e400000)                            // |x| < 2**-27
        { if ((int)x == 0) return x; }                  // generate inexact
        z = x * x;
        v = z * x;
        r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        if (iy == 0) return x + v * (S1 + z * r);
        else return x - ((z * (Half * y - v * r) - y) - v * S1);
    }

    // ---- k_cos.c: kernel cos on [-pi/4, pi/4] ----------------------------------------------------------------------

    private const double One = 1.00000000000000000000e+00;    // 0x3FF00000, 0x00000000
    private const double C1 = 4.16666666666666019037e-02;     // 0x3FA55555, 0x5555554C
    private const double C2 = -1.38888888888741095749e-03;    // 0xBF56C16C, 0x16C15177
    private const double C3 = 2.48015872894767294178e-05;     // 0x3EFA01A0, 0x19CB1590
    private const double C4 = -2.75573143513906633035e-07;    // 0xBE927E4F, 0x809C52AD
    private const double C5 = 2.08757232129817482790e-09;     // 0x3E21EE9E, 0xBDB4B1C4
    private const double C6 = -1.13596475577881948265e-11;    // 0xBDA8FAE9, 0xBE8838D4

    /// <summary>
    /// k_cos.c <c>__kernel_cos(x, y)</c>: cos(x + y) for |x| up to about pi/4, y the tail of x. Above |x| = 0.3 the
    /// constant qx (x/4 rounded down to 21 bits, or 0.28125) is split off first so 1 - x*x/2 loses no bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double KernelCos(double x, double y)
    {
        double a, hz, z, r, qx;
        int ix;
        ix = Hi(x) & 0x7fffffff;                        // ix = |x|'s high word
        if (ix < 0x3e400000)
        {                                               // if x < 2**27
            if (((int)x) == 0) return One;              // generate inexact
        }
        z = x * x;
        r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3FD33333)                            // if |x| < 0.3
            return One - (0.5 * z - (z * r - x * y));
        else
        {
            if (ix > 0x3fe90000)
            {                                           // x > 0.78125
                qx = 0.28125;
            }
            else
            {
                qx = Make(ix - 0x00200000, 0);          // x/4: C writes __HI(qx) = ix-0x00200000; __LO(qx) = 0;
            }
            hz = 0.5 * z - qx;
            a = One - qx;
            return a - (hz - (z * r - x * y));
        }
    }

    // ---- k_tan.c (1.5, 2004): kernel tan on [-pi/4, pi/4] ----------------------------------------------------------

    // T[0..12] are the xxx[0..12] polynomial coefficients; one, pio4 and pio4lo are xxx[13..15].
    private static readonly double[] T =
    {
        3.33333333333334091986e-01,     // 3FD55555, 55555563
        1.33333333333201242699e-01,     // 3FC11111, 1110FE7A
        5.39682539762260521377e-02,     // 3FABA1BA, 1BB341FE
        2.18694882948595424599e-02,     // 3F9664F4, 8406D637
        8.86323982359930005737e-03,     // 3F8226E3, E96E8493
        3.59207910759131235356e-03,     // 3F6D6D22, C9560328
        1.45620945432529025516e-03,     // 3F57DBC8, FEE08315
        5.88041240820264096874e-04,     // 3F4344D8, F2F26501
        2.46463134818469906812e-04,     // 3F3026F7, 1A8D1068
        7.81794442939557092300e-05,     // 3F147E88, A03792A6
        7.14072491382608190305e-05,     // 3F12B80F, 32F0A7E9
        -1.85586374855275456654e-05,    // BEF375CB, DB605373
        2.59073051863633712884e-05,     // 3EFB2A70, 74BF7AD4
    };
    private const double Pio4 = 7.85398163397448278999e-01;    // 3FE921FB, 54442D18
    private const double Pio4lo = 3.06161699786838301793e-17;  // 3C81A626, 33145C07

    /// <summary>
    /// k_tan.c 1.5 <c>__kernel_tan(x, y, k)</c>: tan(x + y) when <paramref name="iy"/> is 1, -1/tan(x + y) when it is
    /// -1, for |x| up to about pi/4. FDLIBM 5.3's version (the one with the 0.96 ulp bound), as StrictMath uses.
    /// </summary>
    internal static double KernelTan(double x, double y, int iy)
    {
        double z, r, v, w, s;
        int ix, hx;

        hx = Hi(x);                                     // high word of x
        ix = hx & 0x7fffffff;                           // high word of |x|
        if (ix < 0x3e300000)
        {                                               // x < 2**-28
            if ((int)x == 0)
            {                                           // generate inexact
                // C: ((ix | __LO(x)) | (iy + 1)) == 0, evaluated as unsigned because __LO is unsigned.
                if ((((uint)ix | Lo(x)) | (uint)(iy + 1)) == 0)
                    return One / Math.Abs(x);
                else
                {
                    if (iy == 1)
                        return x;
                    else
                    {                                   // compute -1 / (x+y) carefully
                        double a, t;

                        z = w = x + y;
                        z = SetLo(z, 0);
                        v = y - (z - x);
                        t = a = -One / w;
                        t = SetLo(t, 0);
                        s = One + t * z;
                        return t + a * (s + t * v);
                    }
                }
            }
        }
        if (ix >= 0x3FE59428)
        {                                               // |x| >= 0.6744
            if (hx < 0)
            {
                x = -x;
                y = -y;
            }
            z = Pio4 - x;
            w = Pio4lo - y;
            x = z + w;
            y = 0.0;
        }
        z = x * x;
        w = z * z;
        // Break x^5*(T[1]+x^2*T[2]+...) into
        // x^5(T[1]+x^4*T[3]+...+x^20*T[11]) +
        // x^5(x^2*(T[2]+x^4*T[4]+...+x^22*[T12]))
        r = T[1] + w * (T[3] + w * (T[5] + w * (T[7] + w * (T[9] +
            w * T[11]))));
        v = z * (T[2] + w * (T[4] + w * (T[6] + w * (T[8] + w * (T[10] +
            w * T[12])))));
        s = z * x;
        r = y + z * (s * (r + v) + y);
        r += T[0] * s;
        w = x + r;
        if (ix >= 0x3FE59428)
        {
            v = (double)iy;
            return (double)(1 - ((hx >> 30) & 2)) *
                (v - 2.0 * (x - (w * w / (w + v) - r)));
        }
        if (iy == 1)
            return w;
        else
        {
            // if allow error up to 2 ulp, simply return -1.0 / (x+r) here
            // compute -1.0 / (x+r) accurately
            double a, t;
            z = w;
            z = SetLo(z, 0);
            v = r - (z - x);                            // z+v = r+x
            t = a = -1.0 / w;                           // a = -1.0/w
            t = SetLo(t, 0);
            s = 1.0 + t * z;
            return t + a * (s + t * v);
        }
    }

    // ---- e_rem_pio2.c: x reduced modulo pi/2 -----------------------------------------------------------------------

    // Table of constants for 2/pi, 396 hex digits (476 decimal) of 2/pi, 24 bits per entry.
    private static readonly int[] TwoOverPi =
    {
        0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62,
        0x95993C, 0x439041, 0xFE5163, 0xABDEBB, 0xC561B7, 0x246E3A,
        0x424DD2, 0xE00649, 0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129,
        0xA73EE8, 0x8235F5, 0x2EBB44, 0x84E99C, 0x7026B4, 0x5F7E41,
        0x3991D6, 0x398353, 0x39F49C, 0x845F8B, 0xBDF928, 0x3B1FF8,
        0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D, 0x367ECF,
        0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5,
        0xF17B3D, 0x0739F7, 0x8A5292, 0xEA6BFB, 0x5FB11F, 0x8D5D08,
        0x560330, 0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3,
        0x91615E, 0xE61B08, 0x659985, 0x5F14A0, 0x68408D, 0xFFD880,
        0x4D7327, 0x310606, 0x1556CA, 0x73A8C9, 0x60E27B, 0xC08C6B,
    };

    // High words of n*pi/2 for n = 1..32, used to spot arguments that sit close to a multiple of pi/2.
    private static readonly int[] Npio2Hw =
    {
        0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C,
        0x4025FDBB, 0x402921FB, 0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C,
        0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB, 0x403AB41B, 0x403C463A,
        0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
        0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB,
        0x404858EB, 0x404921FB,
    };

    // invpio2: 53 bits of 2/pi; pio2_1: first 33 bits of pi/2; pio2_1t: pi/2 - pio2_1; pio2_2: second 33 bits;
    // pio2_2t: pi/2 - (pio2_1+pio2_2); pio2_3: third 33 bits; pio2_3t: pi/2 - (pio2_1+pio2_2+pio2_3).
    private const double Two24 = 1.67772160000000000000e+07;    // 0x41700000, 0x00000000
    private const double Invpio2 = 6.36619772367581382433e-01;  // 0x3FE45F30, 0x6DC9C883
    private const double Pio2_1 = 1.57079632673412561417e+00;   // 0x3FF921FB, 0x54400000
    private const double Pio2_1t = 6.07710050650619224932e-11;  // 0x3DD0B461, 0x1A626331
    private const double Pio2_2 = 6.07710050630396597660e-11;   // 0x3DD0B461, 0x1A600000
    private const double Pio2_2t = 2.02226624879595063154e-21;  // 0x3BA3198A, 0x2E037073
    private const double Pio2_3 = 2.02226624871116645580e-21;   // 0x3BA3198A, 0x2E000000
    private const double Pio2_3t = 8.47842766036889956997e-32;  // 0x397B839A, 0x252049C1

    /// <summary>
    /// e_rem_pio2.c <c>__ieee754_rem_pio2(x, y)</c>: returns n (the quadrant count) and the reduced argument
    /// y0 + y1 = x - n*pi/2 with |y0 + y1| up to pi/4. Medium arguments (|x| up to 2^19 * pi/2) use Cody-Waite
    /// subtraction of a 33+33+33+53-bit pi/2 with up to three refinement rounds; larger ones go to
    /// <see cref="KernelRemPio2"/> with Payne-Hanek's many-digit 2/pi. C's <c>double y[2]</c> out-array is the pair
    /// of out parameters here.
    /// </summary>
    internal static int RemPio2(double x, out double y0, out double y1)
    {
        double z, w, t, r, fn;
        int i, j, n, ix, hx;

        hx = Hi(x);                                     // high word of x
        ix = hx & 0x7fffffff;
        if (ix <= 0x3fe921fb)                           // |x| ~<= pi/4, no need for reduction
        { y0 = x; y1 = 0; return 0; }
        if (ix < 0x4002d97c)
        {                                               // |x| < 3pi/4, special case with n=+-1
            if (hx > 0)
            {
                z = x - Pio2_1;
                if (ix != 0x3ff921fb)
                {                                       // 33+53 bit pi is good enough
                    y0 = z - Pio2_1t;
                    y1 = (z - y0) - Pio2_1t;
                }
                else
                {                                       // near pi/2, use 33+33+53 bit pi
                    z -= Pio2_2;
                    y0 = z - Pio2_2t;
                    y1 = (z - y0) - Pio2_2t;
                }
                return 1;
            }
            else
            {                                           // negative x
                z = x + Pio2_1;
                if (ix != 0x3ff921fb)
                {                                       // 33+53 bit pi is good enough
                    y0 = z + Pio2_1t;
                    y1 = (z - y0) + Pio2_1t;
                }
                else
                {                                       // near pi/2, use 33+33+53 bit pi
                    z += Pio2_2;
                    y0 = z + Pio2_2t;
                    y1 = (z - y0) + Pio2_2t;
                }
                return -1;
            }
        }
        if (ix <= 0x413921fb)
        {                                               // |x| ~<= 2^19*(pi/2), medium size
            t = Math.Abs(x);                            // fabs is a sign-bit clear: exact
            n = (int)(t * Invpio2 + Half);
            fn = (double)n;
            r = t - fn * Pio2_1;
            w = fn * Pio2_1t;                           // 1st round good to 85 bit
            if (n < 32 && ix != Npio2Hw[n - 1])
            {
                y0 = r - w;                             // quick check no cancellation
            }
            else
            {
                j = ix >> 20;
                y0 = r - w;
                i = j - ((Hi(y0) >> 20) & 0x7ff);
                if (i > 16)
                {                                       // 2nd iteration needed, good to 118
                    t = r;
                    w = fn * Pio2_2;
                    r = t - w;
                    w = fn * Pio2_2t - ((t - r) - w);
                    y0 = r - w;
                    i = j - ((Hi(y0) >> 20) & 0x7ff);
                    if (i > 49)
                    {                                   // 3rd iteration need, 151 bits acc
                        t = r;                          // will cover all possible cases
                        w = fn * Pio2_3;
                        r = t - w;
                        w = fn * Pio2_3t - ((t - r) - w);
                        y0 = r - w;
                    }
                }
            }
            y1 = (r - y0) - w;
            if (hx < 0) { y0 = -y0; y1 = -y1; return -n; }
            else return n;
        }
        // all other (large) arguments
        if (ix >= 0x7ff00000)
        {                                               // x is inf or NaN
            y0 = y1 = x - x; return 0;
        }
        return RemPio2Large(x, hx, ix, out y0, out y1);
    }

    /// <summary>
    /// The huge-argument tail of <see cref="RemPio2"/> (|x| above 2^19 * pi/2, finite), split into its own method only
    /// so the common medium-argument path carries no stack buffer and stays small for the JIT (D1 performance); the
    /// statements are e_rem_pio2.c's, unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemPio2Large(double x, int hx, int ix, out double y0, out double y1)
    {
        double z;
        int e0, i, nx, n;
        // set z = scalbn(|x|,ilogb(x)-23): C writes __LO(z) = __LO(x); __HI(z) = ix - (e0<<20);
        e0 = (ix >> 20) - 1046;                         // e0 = ilogb(z)-23;
        z = Make(ix - (e0 << 20), Lo(x));
        Span<double> tx = stackalloc double[3];
        for (i = 0; i < 2; i++)
        {
            tx[i] = (double)((int)(z));
            z = (z - tx[i]) * Two24;
        }
        tx[2] = z;
        nx = 3;
        while (tx[nx - 1] == 0.0) nx--;                 // skip zero term
        Span<double> ty = stackalloc double[2];
        n = KernelRemPio2(tx, ty, e0, nx);
        if (hx < 0) { y0 = -ty[0]; y1 = -ty[1]; return -n; }
        y0 = ty[0]; y1 = ty[1];
        return n;
    }

    // ---- k_rem_pio2.c: Payne-Hanek reduction for huge arguments ----------------------------------------------------

    private static readonly double[] PIo2 =
    {
        1.57079625129699707031e+00,     // 0x3FF921FB, 0x40000000
        7.54978941586159635335e-08,     // 0x3E74442D, 0x00000000
        5.39030252995776476554e-15,     // 0x3CF84698, 0x80000000
        3.28200341580791294123e-22,     // 0x3B78CC51, 0x60000000
        1.27065575308067607349e-29,     // 0x39F01B83, 0x80000000
        1.22933308981111328932e-36,     // 0x387A2520, 0x40000000
        2.73370053816464559624e-44,     // 0x36E38222, 0x80000000
        2.16741683877804819444e-51,     // 0x3569F31D, 0x00000000
    };
    private const double Twon24 = 5.96046447753906250000e-08;   // 0x3E700000, 0x00000000

    /// <summary>
    /// k_rem_pio2.c <c>__kernel_rem_pio2(x, y, e0, nx, prec, ipio2)</c> for the one precision e_rem_pio2.c asks for
    /// (prec = 2, "double", two output terms) and ipio2 = <see cref="TwoOverPi"/>; init_jk[2] = 4. The 24-bit chunks
    /// of the argument, <paramref name="x"/>, are multiplied by enough bits of 2/pi to leave the fraction of
    /// x*2/pi with 53+ good bits; <paramref name="y"/> receives the reduced argument and the return is n mod 8.
    /// The C <c>goto recompute</c> is kept as a C# goto to the same point.
    /// </summary>
    private static int KernelRemPio2(ReadOnlySpan<double> x, Span<double> y, int e0, int nx)
    {
        int jz, jx, jv, jp, jk, carry, n, i, j, k, m, q0, ih;
        double z, fw;
        Span<int> iq = stackalloc int[20];
        Span<double> f = stackalloc double[20];
        Span<double> fq = stackalloc double[20];
        Span<double> q = stackalloc double[20];
        int[] ipio2 = TwoOverPi;

        // initialize jk (init_jk[] = {2,3,4,6}, indexed by prec)
        jk = 4;
        jp = jk;

        // determine jx,jv,q0, note that 3>q0
        jx = nx - 1;
        jv = (e0 - 3) / 24; if (jv < 0) jv = 0;
        q0 = e0 - 24 * (jv + 1);

        // set up f[0] to f[jx+jk] where f[jx+jk] = ipio2[jv+jk]
        j = jv - jx; m = jx + jk;
        for (i = 0; i <= m; i++, j++) f[i] = (j < 0) ? 0.0 : (double)ipio2[j];

        // compute q[0],q[1],...q[jk]
        for (i = 0; i <= jk; i++)
        {
            for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
            q[i] = fw;
        }

        jz = jk;
    recompute:
        // distill q[] into iq[] reversingly
        for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
        {
            fw = (double)((int)(Twon24 * z));
            iq[i] = (int)(z - Two24 * fw);
            z = q[j - 1] + fw;
        }

        // compute n
        z = ScaleBn(z, q0);                             // actual value of z
        z -= 8.0 * Math.Floor(z * 0.125);               // trim off integer >= 8 (floor is exact in IEEE 754)
        n = (int)z;
        z -= (double)n;
        ih = 0;
        if (q0 > 0)
        {                                               // need iq[jz-1] to determine n
            i = (iq[jz - 1] >> (24 - q0)); n += i;
            iq[jz - 1] -= i << (24 - q0);
            ih = iq[jz - 1] >> (23 - q0);
        }
        else if (q0 == 0) ih = iq[jz - 1] >> 23;
        else if (z >= 0.5) ih = 2;

        if (ih > 0)
        {                                               // q > 0.5
            n += 1; carry = 0;
            for (i = 0; i < jz; i++)
            {                                           // compute 1-q
                j = iq[i];
                if (carry == 0)
                {
                    if (j != 0)
                    {
                        carry = 1; iq[i] = 0x1000000 - j;
                    }
                }
                else iq[i] = 0xffffff - j;
            }
            if (q0 > 0)
            {                                           // rare case: chance is 1 in 12
                switch (q0)
                {
                    case 1:
                        iq[jz - 1] &= 0x7fffff; break;
                    case 2:
                        iq[jz - 1] &= 0x3fffff; break;
                }
            }
            if (ih == 2)
            {
                z = One - z;
                if (carry != 0) z -= ScaleBn(One, q0);
            }
        }

        // check if recomputation is needed
        if (z == 0.0)
        {
            j = 0;
            for (i = jz - 1; i >= jk; i--) j |= iq[i];
            if (j == 0)
            {                                           // need recomputation
                for (k = 1; iq[jk - k] == 0; k++) { }   // k = no. of terms needed

                for (i = jz + 1; i <= jz + k; i++)
                {                                       // add q[jz+1] to q[jz+k]
                    f[jx + i] = (double)ipio2[jv + i];
                    for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                    q[i] = fw;
                }
                jz += k;
                goto recompute;
            }
        }

        // chop off zero terms
        if (z == 0.0)
        {
            jz -= 1; q0 -= 24;
            while (iq[jz] == 0) { jz--; q0 -= 24; }
        }
        else
        {                                               // break z into 24-bit if necessary
            z = ScaleBn(z, -q0);
            if (z >= Two24)
            {
                fw = (double)((int)(Twon24 * z));
                iq[jz] = (int)(z - Two24 * fw);
                jz += 1; q0 += 24;
                iq[jz] = (int)fw;
            }
            else iq[jz] = (int)z;
        }

        // convert integer "bit" chunk to floating-point value
        fw = ScaleBn(One, q0);
        for (i = jz; i >= 0; i--)
        {
            q[i] = fw * (double)iq[i]; fw *= Twon24;
        }

        // compute PIo2[0,...,jp]*q[jz,...,0]
        for (i = jz; i >= 0; i--)
        {
            for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++) fw += PIo2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        // compress fq[] into y[]: the C switch's "case 1: case 2:" arm, the only one prec = 2 reaches.
        fw = 0.0;
        for (i = jz; i >= 0; i--) fw += fq[i];
        y[0] = (ih == 0) ? fw : -fw;
        fw = fq[0] - fw;
        for (i = 1; i <= jz; i++) fw += fq[i];
        y[1] = (ih == 0) ? fw : -fw;
        return n & 7;
    }

    // ---- s_sin.c, s_cos.c, s_tan.c ---------------------------------------------------------------------------------

    /// <summary>s_sin.c <c>sin(x)</c>.</summary>
    internal static double Sin(double x)
    {
        double z = 0.0;
        int n, ix;

        // High word of x.
        ix = Hi(x);

        // |x| ~< pi/4
        ix &= 0x7fffffff;
        if (ix <= 0x3fe921fb) return KernelSin(x, z, 0);

        // sin(Inf or NaN) is NaN
        else if (ix >= 0x7ff00000) return x - x;

        // argument reduction needed
        else
        {
            n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KernelSin(y0, y1, 1);
                case 1: return KernelCos(y0, y1);
                case 2: return -KernelSin(y0, y1, 1);
                default:
                    return -KernelCos(y0, y1);
            }
        }
    }

    /// <summary>s_cos.c <c>cos(x)</c>.</summary>
    internal static double Cos(double x)
    {
        double z = 0.0;
        int n, ix;

        // High word of x.
        ix = Hi(x);

        // |x| ~< pi/4
        ix &= 0x7fffffff;
        if (ix <= 0x3fe921fb) return KernelCos(x, z);

        // cos(Inf or NaN) is NaN
        else if (ix >= 0x7ff00000) return x - x;

        // argument reduction needed
        else
        {
            n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KernelCos(y0, y1);
                case 1: return -KernelSin(y0, y1, 1);
                case 2: return -KernelCos(y0, y1);
                default:
                    return KernelSin(y0, y1, 1);
            }
        }
    }

    /// <summary>
    /// sin(x) and cos(x) together, sharing one argument reduction. Not an fdlibm entry point: it runs exactly the
    /// statements <see cref="Sin"/> and <see cref="Cos"/> run (same branches, same kernel calls on the same reduced
    /// argument), so each half is bit-identical to the separate call. <c>FdlibmStrictMathVectorTests</c> proves that.
    /// </summary>
    internal static (double Sin, double Cos) SinCos(double x)
    {
        int n, ix;
        ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return (KernelSin(x, 0.0, 0), KernelCos(x, 0.0));
        else if (ix >= 0x7ff00000) { double nan = x - x; return (nan, nan); }
        else
        {
            n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return (KernelSin(y0, y1, 1), KernelCos(y0, y1));
                case 1: return (KernelCos(y0, y1), -KernelSin(y0, y1, 1));
                case 2: return (-KernelSin(y0, y1, 1), -KernelCos(y0, y1));
                default:
                    return (-KernelCos(y0, y1), KernelSin(y0, y1, 1));
            }
        }
    }

    /// <summary>s_tan.c <c>tan(x)</c>.</summary>
    internal static double Tan(double x)
    {
        double z = 0.0;
        int n, ix;

        // High word of x.
        ix = Hi(x);

        // |x| ~< pi/4
        ix &= 0x7fffffff;
        if (ix <= 0x3fe921fb) return KernelTan(x, z, 1);

        // tan(Inf or NaN) is NaN
        else if (ix >= 0x7ff00000) return x - x;            // NaN

        // argument reduction needed
        else
        {
            n = RemPio2(x, out double y0, out double y1);
            return KernelTan(y0, y1, 1 - ((n & 1) << 1));   //   1 -- n even, -1 -- n odd
        }
    }
}
