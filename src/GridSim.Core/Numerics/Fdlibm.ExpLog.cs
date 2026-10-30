// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// e_exp.c (1.6), s_expm1.c (1.5) and e_pow.c (1.5) carry the 2004 form of the same notice:
// ====================================================
// Copyright (C) 2004 by Sun Microsystems, Inc. All rights reserved.
//
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// C# port for GridSim (D1, October 2026) of FDLIBM 5.3 (netlib.org/fdlibm): e_exp.c 1.6, s_expm1.c 1.5, e_log.c 1.3,
// e_log10.c 1.3, e_pow.c 1.5, e_sinh.c 1.3 and e_cosh.c 1.3. See THIRD-PARTY-NOTICES.md. The port was made from the
// original C files only.

namespace GridSim.Core.Numerics;

internal static partial class Fdlibm
{
    // A runtime zero for the C idioms "-two54/zero" (minus infinity) and "(x-x)/zero" (NaN). fdlibm keeps zero in a
    // non-const static so the C compiler cannot fold the division; the same is done here.
    private static readonly double ZeroVar = 0.0;

    // ---- e_exp.c ----------------------------------------------------------------------------------------------------

    private static readonly double[] HalF = { 0.5, -0.5, };
    private const double Twom1000 = 9.33263618503218878990e-302;      // 2**-1000=0x01700000,0
    private const double O_threshold = 7.09782712893383973096e+02;    // 0x40862E42, 0xFEFA39EF
    private const double U_threshold = -7.45133219101941108420e+02;   // 0xc0874910, 0xD52D3051
    private static readonly double[] Ln2HI =
    {
        6.93147180369123816490e-01,     // 0x3fe62e42, 0xfee00000
        -6.93147180369123816490e-01,    // 0xbfe62e42, 0xfee00000
    };
    private static readonly double[] Ln2LO =
    {
        1.90821492927058770002e-10,     // 0x3dea39ef, 0x35793c76
        -1.90821492927058770002e-10,    // 0xbdea39ef, 0x35793c76
    };
    private const double Invln2 = 1.44269504088896338700e+00;   // 0x3ff71547, 0x652b82fe
    private const double P1 = 1.66666666666666019037e-01;       // 0x3FC55555, 0x5555553E
    private const double P2 = -2.77777777770155933842e-03;      // 0xBF66C16C, 0x16BEBD93
    private const double P3 = 6.61375632143793436117e-05;       // 0x3F11566A, 0xAF25DE2C
    private const double P4 = -1.65339022054652515390e-06;      // 0xBEBBBD41, 0xC5D26BF1
    private const double P5 = 4.13813679705723846039e-08;       // 0x3E663769, 0x72BEA4D0

    /// <summary>
    /// e_exp.c <c>__ieee754_exp(x)</c>: x = k*ln2 + r with |r| up to 0.5*ln2 (ln2 split hi + lo so k*ln2hi is
    /// exact), exp(r) from a degree-5 Remez rational form, then k is added to the exponent field.
    /// </summary>
    internal static double Exp(double x)
    {
        double y, hi = 0.0, lo = 0.0, c, t;
        int k = 0, xsb;
        uint hx;

        hx = (uint)Hi(x);                               // high word of x (fdlibm declares hx unsigned here)
        xsb = (int)((hx >> 31) & 1);                    // sign bit of x
        hx &= 0x7fffffff;                               // high word of |x|

        // filter out non-finite argument
        if (hx >= 0x40862E42)
        {                                               // if |x|>=709.78...
            if (hx >= 0x7ff00000)
            {
                if (((hx & 0xfffff) | Lo(x)) != 0)
                    return x + x;                       // NaN
                else return (xsb == 0) ? x : 0.0;       // exp(+-inf)={inf,0}
            }
            if (x > O_threshold) return Huge300 * Huge300;     // overflow
            if (x < U_threshold) return Twom1000 * Twom1000;   // underflow
        }

        // argument reduction
        if (hx > 0x3fd62e42)
        {                                               // if  |x| > 0.5 ln2
            if (hx < 0x3FF0A2B2)
            {                                           // and |x| < 1.5 ln2
                hi = x - Ln2HI[xsb]; lo = Ln2LO[xsb]; k = 1 - xsb - xsb;
            }
            else
            {
                k = (int)(Invln2 * x + HalF[xsb]);
                t = k;
                hi = x - t * Ln2HI[0];                  // t*ln2HI is exact here
                lo = t * Ln2LO[0];
            }
            x = hi - lo;
        }
        else if (hx < 0x3e300000)
        {                                               // when |x|<2**-28
            if (Huge300 + x > One) return One + x;      // trigger inexact
        }
        else k = 0;

        // x is now in primary range
        t = x * x;
        c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        if (k == 0) return One - ((x * c) / (c - 2.0) - x);
        else y = One - ((lo - (x * c) / (2.0 - c)) - hi);
        if (k >= -1021)
        {
            y = SetHi(y, Hi(y) + (k << 20));            // add k to y's exponent
            return y;
        }
        else
        {
            y = SetHi(y, Hi(y) + ((k + 1000) << 20));   // add k to y's exponent
            return y * Twom1000;
        }
    }

    // ---- s_expm1.c --------------------------------------------------------------------------------------------------

    private const double Ln2_hi = 6.93147180369123816490e-01;   // 0x3fe62e42, 0xfee00000
    private const double Ln2_lo = 1.90821492927058770002e-10;   // 0x3dea39ef, 0x35793c76
    // scaled coefficients related to expm1
    private const double Q1 = -3.33333333333331316428e-02;      // BFA11111 111110F4
    private const double Q2 = 1.58730158725481460165e-03;       // 3F5A01A0 19FE5585
    private const double Q3 = -7.93650757867487942473e-05;      // BF14CE19 9EAADBB7
    private const double Q4 = 4.00821782732936239552e-06;       // 3ED0CFCA 86E65239
    private const double Q5 = -2.01099218183624371326e-07;      // BE8AFDB7 6E09C32D

    /// <summary>s_expm1.c <c>expm1(x)</c> = exp(x) - 1, accurate near zero (used by sinh and cosh).</summary>
    internal static double Expm1(double x)
    {
        double y, hi, lo, c = 0.0, t, e, hxs, hfx, r1;
        int k;
        uint hx, xsb;

        hx = (uint)Hi(x);                               // high word of x
        xsb = hx & 0x80000000;                          // sign bit of x
        if (xsb == 0) y = x; else y = -x;               // y = |x|
        hx &= 0x7fffffff;                               // high word of |x|

        // filter out huge and non-finite argument
        if (hx >= 0x4043687A)
        {                                               // if |x|>=56*ln2
            if (hx >= 0x40862E42)
            {                                           // if |x|>=709.78...
                if (hx >= 0x7ff00000)
                {
                    if (((hx & 0xfffff) | Lo(x)) != 0)
                        return x + x;                   // NaN
                    else return (xsb == 0) ? x : -1.0;  // exp(+-inf)={inf,-1}
                }
                if (x > O_threshold) return Huge300 * Huge300;   // overflow
            }
            if (xsb != 0)
            {                                           // x < -56*ln2, return -1.0 with inexact
                if (x + Tiny300 < 0.0)                  // raise inexact
                    return Tiny300 - One;               // return -1
            }
        }

        // argument reduction
        if (hx > 0x3fd62e42)
        {                                               // if  |x| > 0.5 ln2
            if (hx < 0x3FF0A2B2)
            {                                           // and |x| < 1.5 ln2
                if (xsb == 0)
                { hi = x - Ln2_hi; lo = Ln2_lo; k = 1; }
                else
                { hi = x + Ln2_hi; lo = -Ln2_lo; k = -1; }
            }
            else
            {
                k = (int)(Invln2 * x + ((xsb == 0) ? 0.5 : -0.5));   // C: implicit double-to-int truncation
                t = k;
                hi = x - t * Ln2_hi;                    // t*ln2_hi is exact here
                lo = t * Ln2_lo;
            }
            x = hi - lo;
            c = (hi - x) - lo;
        }
        else if (hx < 0x3c900000)
        {                                               // when |x|<2**-54, return x
            t = Huge300 + x;                            // return x with inexact flags when x!=0
            return x - (t - (Huge300 + x));
        }
        else k = 0;

        // x is now in primary range
        hfx = 0.5 * x;
        hxs = x * hfx;
        r1 = One + hxs * (Q1 + hxs * (Q2 + hxs * (Q3 + hxs * (Q4 + hxs * Q5))));
        t = 3.0 - r1 * hfx;
        e = hxs * ((r1 - t) / (6.0 - x * t));
        if (k == 0) return x - (x * e - hxs);           // c is 0
        else
        {
            e = (x * (e - c) - c);
            e -= hxs;
            if (k == -1) return 0.5 * (x - e) - 0.5;
            if (k == 1)
            {
                if (x < -0.25) return -2.0 * (e - (x + 0.5));
                else return One + 2.0 * (x - e);
            }
            if (k <= -2 || k > 56)
            {                                           // suffice to return exp(x)-1
                y = One - (e - x);
                y = SetHi(y, Hi(y) + (k << 20));        // add k to y's exponent
                return y - One;
            }
            t = One;
            if (k < 20)
            {
                t = SetHi(t, 0x3ff00000 - (0x200000 >> k));   // t=1-2^-k
                y = t - (e - x);
                y = SetHi(y, Hi(y) + (k << 20));        // add k to y's exponent
            }
            else
            {
                t = SetHi(t, ((0x3ff - k) << 20));      // 2^-k
                y = x - (e + t);
                y += One;
                y = SetHi(y, Hi(y) + (k << 20));        // add k to y's exponent
            }
        }
        return y;
    }

    // ---- e_log.c ----------------------------------------------------------------------------------------------------

    private const double Lg1 = 6.666666666666735130e-01;   // 3FE55555 55555593
    private const double Lg2 = 3.999999999940941908e-01;   // 3FD99999 9997FA04
    private const double Lg3 = 2.857142874366239149e-01;   // 3FD24924 94229359
    private const double Lg4 = 2.222219843214978396e-01;   // 3FCC71C5 1D8E78AF
    private const double Lg5 = 1.818357216161805012e-01;   // 3FC74664 96CB03DE
    private const double Lg6 = 1.531383769920937332e-01;   // 3FC39A09 D078C69F
    private const double Lg7 = 1.479819860511658591e-01;   // 3FC2F112 DF3E5244

    /// <summary>
    /// e_log.c <c>__ieee754_log(x)</c>: x = 2^k * (1+f) with sqrt(2)/2 &lt; 1+f &lt; sqrt(2), log(1+f) from
    /// s = f/(2+f) and a degree-14 Remez polynomial in s, then k*ln2 (hi + lo) added back.
    /// </summary>
    internal static double Log(double x)
    {
        double hfsq, f, s, z, R, w, t1, t2, dk;
        int k, hx, i, j;
        uint lx;

        hx = Hi(x);                                     // high word of x
        lx = Lo(x);                                     // low  word of x

        k = 0;
        if (hx < 0x00100000)
        {                                               // x < 2**-1022
            if (((uint)(hx & 0x7fffffff) | lx) == 0)
                return -Two54 / ZeroVar;                // log(+-0)=-inf
            if (hx < 0) return (x - x) / ZeroVar;       // log(-#) = NaN
            k -= 54; x *= Two54;                        // subnormal number, scale up x
            hx = Hi(x);                                 // high word of x
        }
        if (hx >= 0x7ff00000) return x + x;
        k += (hx >> 20) - 1023;
        hx &= 0x000fffff;
        i = (hx + 0x95f64) & 0x100000;
        x = SetHi(x, hx | (i ^ 0x3ff00000));            // normalize x or x/2
        k += (i >> 20);
        f = x - 1.0;
        if ((0x000fffff & (2 + hx)) < 3)
        {                                               // |f| < 2**-20
            // C: "if(f==zero) if(k==0) return zero; else {...}": the else belongs to the inner if.
            if (f == 0.0)
            {
                if (k == 0) return 0.0;
                else { dk = (double)k; return dk * Ln2_hi + dk * Ln2_lo; }
            }
            R = f * f * (0.5 - 0.33333333333333333 * f);
            if (k == 0) return f - R;
            else
            {
                dk = (double)k;
                return dk * Ln2_hi - ((R - dk * Ln2_lo) - f);
            }
        }
        s = f / (2.0 + f);
        dk = (double)k;
        z = s * s;
        i = hx - 0x6147a;
        w = z * z;
        j = 0x6b851 - hx;
        t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        i |= j;
        R = t2 + t1;
        if (i > 0)
        {
            hfsq = 0.5 * f * f;
            if (k == 0) return f - (hfsq - s * (hfsq + R));
            else
                return dk * Ln2_hi - ((hfsq - (s * (hfsq + R) + dk * Ln2_lo)) - f);
        }
        else
        {
            if (k == 0) return f - s * (f - R);
            else
                return dk * Ln2_hi - ((s * (f - R) - dk * Ln2_lo) - f);
        }
    }

    // ---- e_log10.c --------------------------------------------------------------------------------------------------

    private const double Ivln10 = 4.34294481903251816668e-01;      // 0x3FDBCB7B, 0x1526E50E
    private const double Log10_2hi = 3.01029995663611771306e-01;   // 0x3FD34413, 0x509F6000
    private const double Log10_2lo = 3.69423907715893078616e-13;   // 0x3D59FEF3, 0x11F12B36

    /// <summary>e_log10.c <c>__ieee754_log10(x)</c> = n*log10(2) + log10(e)*log(x / 2^n), from <see cref="Log"/>.</summary>
    internal static double Log10(double x)
    {
        double y, z;
        int i, k, hx;
        uint lx;

        hx = Hi(x);                                     // high word of x
        lx = Lo(x);                                     // low word of x

        k = 0;
        if (hx < 0x00100000)
        {                                               // x < 2**-1022
            if (((uint)(hx & 0x7fffffff) | lx) == 0)
                return -Two54 / ZeroVar;                // log(+-0)=-inf
            if (hx < 0) return (x - x) / ZeroVar;       // log(-#) = NaN
            k -= 54; x *= Two54;                        // subnormal number, scale up x
            hx = Hi(x);                                 // high word of x
        }
        if (hx >= 0x7ff00000) return x + x;
        k += (hx >> 20) - 1023;
        i = (int)(((uint)k & 0x80000000) >> 31);
        hx = (hx & 0x000fffff) | ((0x3ff - i) << 20);
        y = (double)(k + i);
        x = SetHi(x, hx);
        z = y * Log10_2lo + Ivln10 * Log(x);
        return z + y * Log10_2hi;
    }

    // ---- e_pow.c (1.5, 2004) ----------------------------------------------------------------------------------------

    private static readonly double[] Bp = { 1.0, 1.5, };
    private static readonly double[] Dp_h = { 0.0, 5.84962487220764160156e-01, };   // 0x3FE2B803, 0x40000000
    private static readonly double[] Dp_l = { 0.0, 1.35003920212974897128e-08, };   // 0x3E4CFDEB, 0x43CFD006
    private const double Two = 2.0;
    private const double Two53 = 9007199254740992.0;            // 0x43400000, 0x00000000
    // poly coefs for (3/2)*(log(x)-2s-2/3*s**3
    private const double L1 = 5.99999999999994648725e-01;       // 0x3FE33333, 0x33333303
    private const double L2 = 4.28571428578550184252e-01;       // 0x3FDB6DB6, 0xDB6FABFF
    private const double L3 = 3.33333329818377432918e-01;       // 0x3FD55555, 0x518F264D
    private const double L4 = 2.72728123808534006489e-01;       // 0x3FD17460, 0xA91D4101
    private const double L5 = 2.30660745775561754067e-01;       // 0x3FCD864A, 0x93C9DB65
    private const double L6 = 2.06975017800338417784e-01;       // 0x3FCA7E28, 0x4A454EEF
    private const double Lg2Pow = 6.93147180559945286227e-01;   // 0x3FE62E42, 0xFEFA39EF (e_pow.c's lg2)
    private const double Lg2_h = 6.93147182464599609375e-01;    // 0x3FE62E43, 0x00000000
    private const double Lg2_l = -1.90465429995776804525e-09;   // 0xBE205C61, 0x0CA86C39
    private const double Ovt = 8.0085662595372944372e-0017;     // -(1024-log2(ovfl+.5ulp))
    private const double Cp = 9.61796693925975554329e-01;       // 0x3FEEC709, 0xDC3A03FD =2/(3ln2)
    private const double Cp_h = 9.61796700954437255859e-01;     // 0x3FEEC709, 0xE0000000 =(float)cp
    private const double Cp_l = -7.02846165095275826516e-09;    // 0xBE3E2FE0, 0x145B01F5 =tail of cp_h
    private const double Ivln2 = 1.44269504088896338700e+00;    // 0x3FF71547, 0x652B82FE =1/ln2
    private const double Ivln2_h = 1.44269502162933349609e+00;  // 0x3FF71547, 0x60000000 =24b 1/ln2
    private const double Ivln2_l = 1.92596299112661746887e-08;  // 0x3E54AE0B, 0xF85DDF44 =1/ln2 tail

    /// <summary>
    /// e_pow.c 1.5 <c>__ieee754_pow(x, y)</c>: x^y = 2^(y*log2(x)), with log2(x) carried in extra precision as
    /// t1 + t2 and y split into y1 + y2 so the product is exact enough, then 2^(p_h + p_l) as in exp. FDLIBM 5.3's
    /// version, with the fix for x near -1 and huge y.
    /// </summary>
    internal static double Pow(double x, double y)
    {
        double z, ax, z_h, z_l, p_h, p_l;
        double y1, t1, t2, r, s, t, u, v, w;
        int i, j, k, yisint, n;
        int hx, hy, ix, iy;
        uint lx, ly;

        // (C computes i0/i1 here to find the word order of a double; the word access above makes that unnecessary.)
        hx = Hi(x); lx = Lo(x);
        hy = Hi(y); ly = Lo(y);
        ix = hx & 0x7fffffff; iy = hy & 0x7fffffff;

        // y==zero: x**0 = 1
        if (((uint)iy | ly) == 0) return One;

        // +-NaN return x+y
        if (ix > 0x7ff00000 || ((ix == 0x7ff00000) && (lx != 0)) ||
           iy > 0x7ff00000 || ((iy == 0x7ff00000) && (ly != 0)))
            return x + y;

        // determine if y is an odd int when x < 0
        // yisint = 0 ... y is not an integer
        // yisint = 1 ... y is an odd int
        // yisint = 2 ... y is an even int
        yisint = 0;
        if (hx < 0)
        {
            if (iy >= 0x43400000) yisint = 2;           // even integer y
            else if (iy >= 0x3ff00000)
            {
                k = (iy >> 20) - 0x3ff;                 // exponent
                if (k > 20)
                {
                    // C: j = ly>>(52-k); if((j<<(52-k))==ly) ... with int j compared against unsigned ly, which is
                    // the same as doing the shift and the comparison in unsigned arithmetic, as here.
                    uint jj = ly >> (52 - k);
                    if ((jj << (52 - k)) == ly) yisint = 2 - (int)(jj & 1);
                }
                else if (ly == 0)
                {
                    j = iy >> (20 - k);
                    if ((j << (20 - k)) == iy) yisint = 2 - (j & 1);
                }
            }
        }

        // special value of y
        if (ly == 0)
        {
            if (iy == 0x7ff00000)
            {                                           // y is +-inf
                if (((uint)(ix - 0x3ff00000) | lx) == 0)
                    return y - y;                       // inf**+-1 is NaN
                else if (ix >= 0x3ff00000)              // (|x|>1)**+-inf = inf,0
                    return (hy >= 0) ? y : 0.0;
                else                                    // (|x|<1)**-,+inf = inf,0
                    return (hy < 0) ? -y : 0.0;
            }
            if (iy == 0x3ff00000)
            {                                           // y is  +-1
                if (hy < 0) return One / x; else return x;
            }
            if (hy == 0x40000000) return x * x;         // y is  2
            if (hy == 0x3fe00000)
            {                                           // y is  0.5
                if (hx >= 0)                            // x >= +0
                    return Math.Sqrt(x);                // correctly rounded, the same bits as fdlibm's e_sqrt.c
            }
        }

        ax = Math.Abs(x);
        // special value of x
        if (lx == 0)
        {
            if (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000)
            {
                z = ax;                                 // x is +-0,+-inf,+-1
                if (hy < 0) z = One / z;                // z = (1/|x|)
                if (hx < 0)
                {
                    if (((ix - 0x3ff00000) | yisint) == 0)
                    {
                        z = (z - z) / (z - z);          // (-1)**non-int is NaN
                    }
                    else if (yisint == 1)
                        z = -z;                         // (x<0)**odd = -(|x|**odd)
                }
                return z;
            }
        }

        n = (hx >> 31) + 1;

        // (x<0)**(non-int) is NaN
        if ((n | yisint) == 0) return (x - x) / (x - x);

        s = One;                                        // s (sign of result -ve**odd) = -1 else = 1
        if ((n | (yisint - 1)) == 0) s = -One;          // (-ve)**(odd int)

        // |y| is huge
        if (iy > 0x41e00000)
        {                                               // if |y| > 2**31
            if (iy > 0x43f00000)
            {                                           // if |y| > 2**64, must o/uflow
                if (ix <= 0x3fefffff) return (hy < 0) ? Huge300 * Huge300 : Tiny300 * Tiny300;
                if (ix >= 0x3ff00000) return (hy > 0) ? Huge300 * Huge300 : Tiny300 * Tiny300;
            }
            // over/underflow if x is not close to one
            if (ix < 0x3fefffff) return (hy < 0) ? s * Huge300 * Huge300 : s * Tiny300 * Tiny300;
            if (ix > 0x3ff00000) return (hy > 0) ? s * Huge300 * Huge300 : s * Tiny300 * Tiny300;
            // now |1-x| is tiny <= 2**-20, suffice to compute log(x) by x-x^2/2+x^3/3-x^4/4
            t = ax - One;                               // t has 20 trailing zeros
            w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
            u = Ivln2_h * t;                            // ivln2_h has 21 sig. bits
            v = t * Ivln2_l - w * Ivln2;
            t1 = u + v;
            t1 = SetLo(t1, 0);
            t2 = v - (t1 - u);
        }
        else
        {
            double ss, s2, s_h, s_l, t_h, t_l;
            n = 0;
            // take care subnormal number
            if (ix < 0x00100000)
            { ax *= Two53; n -= 53; ix = Hi(ax); }
            n += ((ix) >> 20) - 0x3ff;
            j = ix & 0x000fffff;
            // determine interval
            ix = j | 0x3ff00000;                        // normalize ix
            if (j <= 0x3988E) k = 0;                    // |x|<sqrt(3/2)
            else if (j < 0xBB67A) k = 1;                // |x|<sqrt(3)
            else { k = 0; n += 1; ix -= 0x00100000; }
            ax = SetHi(ax, ix);

            // compute ss = s_h+s_l = (x-1)/(x+1) or (x-1.5)/(x+1.5)
            u = ax - Bp[k];                             // bp[0]=1.0, bp[1]=1.5
            v = One / (ax + Bp[k]);
            ss = u * v;
            s_h = ss;
            s_h = SetLo(s_h, 0);
            // t_h=ax+bp[k] High: C writes t_h = zero; __HI(t_h) = ...; (so the low word is 0)
            t_h = Make(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
            t_l = ax - (t_h - Bp[k]);
            s_l = v * ((u - s_h * t_h) - s_h * t_l);
            // compute log(ax)
            s2 = ss * ss;
            r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
            r += s_l * (s_h + ss);
            s2 = s_h * s_h;
            t_h = 3.0 + s2 + r;
            t_h = SetLo(t_h, 0);
            t_l = r - ((t_h - 3.0) - s2);
            // u+v = ss*(1+...)
            u = s_h * t_h;
            v = s_l * t_h + t_l * ss;
            // 2/(3log2)*(ss+...)
            p_h = u + v;
            p_h = SetLo(p_h, 0);
            p_l = v - (p_h - u);
            z_h = Cp_h * p_h;                           // cp_h+cp_l = 2/(3*log2)
            z_l = Cp_l * p_h + p_l * Cp + Dp_l[k];
            // log2(ax) = (ss+..)*2/(3*log2) = n + dp_h + z_h + z_l
            t = (double)n;
            t1 = (((z_h + z_l) + Dp_h[k]) + t);
            t1 = SetLo(t1, 0);
            t2 = z_l - (((t1 - t) - Dp_h[k]) - z_h);
        }

        // split up y into y1+y2 and compute (y1+y2)*(t1+t2)
        y1 = y;
        y1 = SetLo(y1, 0);
        p_l = (y - y1) * t1 + y * t2;
        p_h = y1 * t1;
        z = p_l + p_h;
        j = Hi(z);
        i = (int)Lo(z);
        if (j >= 0x40900000)
        {                                               // z >= 1024
            if (((j - 0x40900000) | i) != 0)            // if z > 1024
                return s * Huge300 * Huge300;           // overflow
            else
            {
                if (p_l + Ovt > z - p_h) return s * Huge300 * Huge300;   // overflow
            }
        }
        else if ((j & 0x7fffffff) >= 0x4090cc00)
        {                                               // z <= -1075
            // C: ((j-0xc090cc00)|i) with an unsigned literal; zero exactly when j == (int)0xc090cc00.
            if (((j - unchecked((int)0xc090cc00)) | i) != 0)   // z < -1075
                return s * Tiny300 * Tiny300;           // underflow
            else
            {
                if (p_l <= z - p_h) return s * Tiny300 * Tiny300;   // underflow
            }
        }

        // compute 2**(p_h+p_l)
        i = j & 0x7fffffff;
        k = (i >> 20) - 0x3ff;
        n = 0;
        if (i > 0x3fe00000)
        {                                               // if |z| > 0.5, set n = [z+0.5]
            n = j + (0x00100000 >> (k + 1));
            k = ((n & 0x7fffffff) >> 20) - 0x3ff;       // new k for n
            t = Make(n & ~(0x000fffff >> k), 0);        // C: t = zero; __HI(t) = (n&~(0x000fffff>>k));
            n = ((n & 0x000fffff) | 0x00100000) >> (20 - k);
            if (j < 0) n = -n;
            p_h -= t;
        }
        t = p_l + p_h;
        t = SetLo(t, 0);
        u = t * Lg2_h;
        v = (p_l - (t - p_h)) * Lg2Pow + t * Lg2_l;
        z = u + v;
        w = v - (z - u);
        t = z * z;
        t1 = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        r = (z * t1) / (t1 - Two) - (w + z * w);
        z = One - (r - z);
        j = Hi(z);
        j += (n << 20);
        if ((j >> 20) <= 0) z = ScaleBn(z, n);          // subnormal output
        else z = SetHi(z, Hi(z) + (n << 20));
        return s * z;
    }

    // ---- e_sinh.c and e_cosh.c --------------------------------------------------------------------------------------

    private const double Shuge = 1.0e307;

    /// <summary>e_sinh.c <c>__ieee754_sinh(x)</c>, from <see cref="Expm1"/> for |x| &lt; 22 and <see cref="Exp"/> above.</summary>
    internal static double Sinh(double x)
    {
        double t, w, h;
        int ix, jx;
        uint lx;

        // High word of |x|.
        jx = Hi(x);
        ix = jx & 0x7fffffff;

        // x is INF or NaN
        if (ix >= 0x7ff00000) return x + x;

        h = 0.5;
        if (jx < 0) h = -h;
        // |x| in [0,22], return sign(x)*0.5*(E+E/(E+1)))
        if (ix < 0x40360000)
        {                                               // |x|<22
            if (ix < 0x3e300000)                        // |x|<2**-28
                if (Shuge + x > One) return x;          // sinh(tiny) = tiny with inexact
            t = Expm1(Math.Abs(x));
            if (ix < 0x3ff00000) return h * (2.0 * t - t * t / (t + One));
            return h * (t + t / (t + One));
        }

        // |x| in [22, log(maxdouble)] return 0.5*exp(|x|)
        if (ix < 0x40862E42) return h * Exp(Math.Abs(x));

        // |x| in [log(maxdouble), overflowthresold]: C reads the low word through a pointer, i.e. __LO(x).
        lx = Lo(x);
        if (ix < 0x408633CE || (ix == 0x408633ce) && (lx <= 0x8fb9f87du))
        {
            w = Exp(0.5 * Math.Abs(x));
            t = h * w;
            return t * w;
        }

        // |x| > overflowthresold, sinh(x) overflow
        return x * Shuge;
    }

    /// <summary>e_cosh.c <c>__ieee754_cosh(x)</c>.</summary>
    internal static double Cosh(double x)
    {
        double t, w;
        int ix;
        uint lx;

        // High word of |x|.
        ix = Hi(x);
        ix &= 0x7fffffff;

        // x is INF or NaN
        if (ix >= 0x7ff00000) return x * x;

        // |x| in [0,0.5*ln2], return 1+expm1(|x|)^2/(2*exp(|x|))
        if (ix < 0x3fd62e43)
        {
            t = Expm1(Math.Abs(x));
            w = One + t;
            if (ix < 0x3c800000) return w;              // cosh(tiny) = 1
            return One + (t * t) / (w + w);
        }

        // |x| in [0.5*ln2,22], return (exp(|x|)+1/exp(|x|)/2;
        if (ix < 0x40360000)
        {
            t = Exp(Math.Abs(x));
            return Half * t + Half / t;
        }

        // |x| in [22, log(maxdouble)] return half*exp(|x|)
        if (ix < 0x40862E42) return Half * Exp(Math.Abs(x));

        // |x| in [log(maxdouble), overflowthresold]
        lx = Lo(x);
        if (ix < 0x408633CE ||
              (ix == 0x408633ce) && (lx <= 0x8fb9f87du))
        {
            w = Exp(Half * Math.Abs(x));
            t = Half * w;
            return t * w;
        }

        // |x| > overflowthresold, cosh(x) overflow
        return Huge300 * Huge300;
    }
}
