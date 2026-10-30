// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// C# port for GridSim (D1, October 2026) of FDLIBM 5.3 (netlib.org/fdlibm): s_atan.c 1.3, e_atan2.c 1.3,
// e_asin.c 1.3 and e_acos.c 1.3. See THIRD-PARTY-NOTICES.md. The port was made from the original C files only.

namespace GridSim.Core.Numerics;

internal static partial class Fdlibm
{
    // ---- s_atan.c ---------------------------------------------------------------------------------------------------

    private static readonly double[] AtanHi =
    {
        4.63647609000806093515e-01,     // atan(0.5)hi 0x3FDDAC67, 0x0561BB4F
        7.85398163397448278999e-01,     // atan(1.0)hi 0x3FE921FB, 0x54442D18
        9.82793723247329054082e-01,     // atan(1.5)hi 0x3FEF730B, 0xD281F69B
        1.57079632679489655800e+00,     // atan(inf)hi 0x3FF921FB, 0x54442D18
    };

    private static readonly double[] AtanLo =
    {
        2.26987774529616870924e-17,     // atan(0.5)lo 0x3C7A2B7F, 0x222F65E2
        3.06161699786838301793e-17,     // atan(1.0)lo 0x3C81A626, 0x33145C07
        1.39033110312309984516e-17,     // atan(1.5)lo 0x3C700788, 0x7AF0CBBD
        6.12323399573676603587e-17,     // atan(inf)lo 0x3C91A626, 0x33145C07
    };

    private static readonly double[] AT =
    {
        3.33333333333329318027e-01,     // 0x3FD55555, 0x5555550D
        -1.99999999998764832476e-01,    // 0xBFC99999, 0x9998EBC4
        1.42857142725034663711e-01,     // 0x3FC24924, 0x920083FF
        -1.11111104054623557880e-01,    // 0xBFBC71C6, 0xFE231671
        9.09088713343650656196e-02,     // 0x3FB745CD, 0xC54C206E
        -7.69187620504482999495e-02,    // 0xBFB3B0F2, 0xAF749A6D
        6.66107313738753120669e-02,     // 0x3FB10D66, 0xA0D03D51
        -5.83357013379057348645e-02,    // 0xBFADDE2D, 0x52DEFD9A
        4.97687799461593236017e-02,     // 0x3FA97B4B, 0x24760DEB
        -3.65315727442169155270e-02,    // 0xBFA2B444, 0x2C6A6C2F
        1.62858201153657823623e-02,     // 0x3F90AD3A, 0xE322DA11
    };

    private const double Huge300 = 1.0e300;

    /// <summary>
    /// s_atan.c <c>atan(x)</c>: the argument is reduced to |x| &lt; 7/16 by one of four identities around
    /// atan(0.5), atan(1), atan(1.5) and atan(inf), then a degree-22 odd polynomial split into odd and even parts.
    /// </summary>
    internal static double Atan(double x)
    {
        double w, s1, s2, z;
        int ix, hx, id;

        hx = Hi(x);
        ix = hx & 0x7fffffff;
        if (ix >= 0x44100000)
        {                                               // if |x| >= 2^66
            if (ix > 0x7ff00000 ||
                (ix == 0x7ff00000 && (Lo(x) != 0)))
                return x + x;                           // NaN
            if (hx > 0) return AtanHi[3] + AtanLo[3];
            else return -AtanHi[3] - AtanLo[3];
        }
        if (ix < 0x3fdc0000)
        {                                               // |x| < 0.4375
            if (ix < 0x3e200000)
            {                                           // |x| < 2^-29
                if (Huge300 + x > One) return x;        // raise inexact
            }
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {                                           // |x| < 1.1875
                if (ix < 0x3fe60000)
                {                                       // 7/16 <=|x|<11/16
                    id = 0; x = (2.0 * x - One) / (2.0 + x);
                }
                else
                {                                       // 11/16<=|x|< 19/16
                    id = 1; x = (x - One) / (x + One);
                }
            }
            else
            {
                if (ix < 0x40038000)
                {                                       // |x| < 2.4375
                    id = 2; x = (x - 1.5) / (One + 1.5 * x);
                }
                else
                {                                       // 2.4375 <= |x| < 2^66
                    id = 3; x = -1.0 / x;
                }
            }
        }
        // end of argument reduction
        z = x * x;
        w = z * z;
        // break sum from i=0 to 10 aT[i]z**(i+1) into odd and even poly
        s1 = z * (AT[0] + w * (AT[2] + w * (AT[4] + w * (AT[6] + w * (AT[8] + w * AT[10])))));
        s2 = w * (AT[1] + w * (AT[3] + w * (AT[5] + w * (AT[7] + w * AT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        else
        {
            z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
            return (hx < 0) ? -z : z;
        }
    }

    // ---- e_atan2.c --------------------------------------------------------------------------------------------------

    private const double Tiny300 = 1.0e-300;
    private const double Pi_o_4 = 7.8539816339744827900E-01;  // 0x3FE921FB, 0x54442D18
    private const double Pi_o_2 = 1.5707963267948965580E+00;  // 0x3FF921FB, 0x54442D18
    private const double Pi = 3.1415926535897931160E+00;      // 0x400921FB, 0x54442D18
    private const double Pi_lo = 1.2246467991473531772E-16;   // 0x3CA1A626, 0x33145C07

    /// <summary>
    /// e_atan2.c <c>__ieee754_atan2(y, x)</c>: the angle of the point (x, y), in (-pi, pi], from atan(|y/x|) and the
    /// signs of both arguments, with the IEEE special cases for zeros and infinities. The "+ tiny" terms are kept: they
    /// only raise the inexact flag in C and round away to the same double here.
    /// </summary>
    internal static double Atan2(double y, double x)
    {
        double z;
        int k, m, hx, hy, ix, iy;
        uint lx, ly;

        hx = Hi(x); ix = hx & 0x7fffffff;
        lx = Lo(x);
        hy = Hi(y); iy = hy & 0x7fffffff;
        ly = Lo(y);
        // C: (ix|((lx|-lx)>>31)) > 0x7ff00000 on unsigned lx: the shifted term is 1 exactly when lx != 0.
        if ((((uint)ix | ((lx | (0u - lx)) >> 31)) > 0x7ff00000) ||
            (((uint)iy | ((ly | (0u - ly)) >> 31)) > 0x7ff00000))   // x or y is NaN
            return x + y;
        if (((uint)(hx - 0x3ff00000) | lx) == 0) return Atan(y);   // x=1.0
        m = ((hy >> 31) & 1) | ((hx >> 30) & 2);        // 2*sign(x)+sign(y)

        // when y = 0
        if (((uint)iy | ly) == 0)
        {
            switch (m)
            {
                case 0:
                case 1: return y;                       // atan(+-0,+anything)=+-0
                case 2: return Pi + Tiny300;            // atan(+0,-anything) = pi
                case 3: return -Pi - Tiny300;           // atan(-0,-anything) =-pi
            }
        }
        // when x = 0
        if (((uint)ix | lx) == 0) return (hy < 0) ? -Pi_o_2 - Tiny300 : Pi_o_2 + Tiny300;

        // when x is INF
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
            {
                switch (m)
                {
                    case 0: return Pi_o_4 + Tiny300;        // atan(+INF,+INF)
                    case 1: return -Pi_o_4 - Tiny300;       // atan(-INF,+INF)
                    case 2: return 3.0 * Pi_o_4 + Tiny300;  // atan(+INF,-INF)
                    case 3: return -3.0 * Pi_o_4 - Tiny300; // atan(-INF,-INF)
                }
            }
            else
            {
                switch (m)
                {
                    case 0: return 0.0;                 // atan(+...,+INF)
                    case 1: return -0.0;                // atan(-...,+INF)
                    case 2: return Pi + Tiny300;        // atan(+...,-INF)
                    case 3: return -Pi - Tiny300;       // atan(-...,-INF)
                }
            }
        }
        // when y is INF
        if (iy == 0x7ff00000) return (hy < 0) ? -Pi_o_2 - Tiny300 : Pi_o_2 + Tiny300;

        // compute y/x
        k = (iy - ix) >> 20;
        if (k > 60) z = Pi_o_2 + 0.5 * Pi_lo;           // |y/x| >  2**60
        else if (hx < 0 && k < -60) z = 0.0;            // |y|/x < -2**60
        else z = Atan(Math.Abs(y / x));                 // safe to do y/x
        switch (m)
        {
            case 0: return z;                           // atan(+,+)
            case 1:
                z = SetHi(z, Hi(z) ^ unchecked((int)0x80000000));   // C: __HI(z) ^= 0x80000000;
                return z;                               // atan(-,+)
            case 2: return Pi - (z - Pi_lo);            // atan(+,-)
            default:                                    // case 3
                return (z - Pi_lo) - Pi;                // atan(-,-)
        }
    }

    // ---- e_asin.c and e_acos.c (shared rational approximation of (asin(x)-x)/x^3) ------------------------------------

    private const double Pio2_hi = 1.57079632679489655800e+00;    // 0x3FF921FB, 0x54442D18
    private const double Pio2_lo = 6.12323399573676603587e-17;    // 0x3C91A626, 0x33145C07
    private const double Pio4_hi = 7.85398163397448278999e-01;    // 0x3FE921FB, 0x54442D18
    private const double PS0 = 1.66666666666666657415e-01;        // 0x3FC55555, 0x55555555
    private const double PS1 = -3.25565818622400915405e-01;       // 0xBFD4D612, 0x03EB6F7D
    private const double PS2 = 2.01212532134862925881e-01;        // 0x3FC9C155, 0x0E884455
    private const double PS3 = -4.00555345006794114027e-02;       // 0xBFA48228, 0xB5688F3B
    private const double PS4 = 7.91534994289814532176e-04;        // 0x3F49EFE0, 0x7501B288
    private const double PS5 = 3.47933107596021167570e-05;        // 0x3F023DE1, 0x0DFDF709
    private const double QS1 = -2.40339491173441421878e+00;       // 0xC0033A27, 0x1C8A2D4B
    private const double QS2 = 2.02094576023350569471e+00;        // 0x40002AE5, 0x9C598AC8
    private const double QS3 = -6.88283971605453293030e-01;       // 0xBFE6066C, 0x1B8D0159
    private const double QS4 = 7.70381505559019352791e-02;        // 0x3FB3B8C5, 0xB12E9282

    /// <summary>
    /// e_asin.c <c>__ieee754_asin(x)</c>. sqrt is <see cref="Math.Sqrt"/>: IEEE 754 requires the square root to be
    /// correctly rounded, so it is the same bits as fdlibm's own e_sqrt.c everywhere.
    /// </summary>
    internal static double Asin(double x)
    {
        double t, w, p, q, c, r, s;
        int hx, ix;
        hx = Hi(x);
        ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)
        {                                               // |x|>= 1
            if (((uint)(ix - 0x3ff00000) | Lo(x)) == 0)
                // asin(1)=+-pi/2 with inexact
                return x * Pio2_hi + x * Pio2_lo;
            return (x - x) / (x - x);                   // asin(|x|>1) is NaN
        }
        else if (ix < 0x3fe00000)
        {                                               // |x|<0.5
            if (ix < 0x3e400000)
            {                                           // if |x| < 2**-27
                if (Huge300 + x > One) return x;        // return x with inexact if x!=0
            }
            // C writes "else t = x*x;" here, so t is only assigned for the non-tiny arm; the tiny arm always returns
            // above (huge + x > one is true for every tiny x), so assigning t unconditionally is the same program.
            t = x * x;
            p = t * (PS0 + t * (PS1 + t * (PS2 + t * (PS3 + t * (PS4 + t * PS5)))));
            q = One + t * (QS1 + t * (QS2 + t * (QS3 + t * QS4)));
            w = p / q;
            return x + x * w;
        }
        // 1> |x|>= 0.5
        w = One - Math.Abs(x);
        t = w * 0.5;
        p = t * (PS0 + t * (PS1 + t * (PS2 + t * (PS3 + t * (PS4 + t * PS5)))));
        q = One + t * (QS1 + t * (QS2 + t * (QS3 + t * QS4)));
        s = Math.Sqrt(t);
        if (ix >= 0x3FEF3333)
        {                                               // if |x| > 0.975
            w = p / q;
            t = Pio2_hi - (2.0 * (s + s * w) - Pio2_lo);
        }
        else
        {
            w = s;
            w = SetLo(w, 0);
            c = (t - w * w) / (s + w);
            r = p / q;
            p = 2.0 * s * r - (Pio2_lo - 2.0 * c);
            q = Pio4_hi - 2.0 * w;
            t = Pio4_hi - (p - q);
        }
        if (hx > 0) return t; else return -t;
    }

    /// <summary>e_acos.c <c>__ieee754_acos(x)</c>. sqrt is <see cref="Math.Sqrt"/> (correctly rounded, see Asin).</summary>
    internal static double Acos(double x)
    {
        double z, p, q, r, w, s, c, df;
        int hx, ix;
        hx = Hi(x);
        ix = hx & 0x7fffffff;
        if (ix >= 0x3ff00000)
        {                                               // |x| >= 1
            if (((uint)(ix - 0x3ff00000) | Lo(x)) == 0)
            {                                           // |x|==1
                if (hx > 0) return 0.0;                 // acos(1) = 0
                else return Pi + 2.0 * Pio2_lo;         // acos(-1)= pi
            }
            return (x - x) / (x - x);                   // acos(|x|>1) is NaN
        }
        if (ix < 0x3fe00000)
        {                                               // |x| < 0.5
            if (ix <= 0x3c600000) return Pio2_hi + Pio2_lo;   // if|x|<2**-57
            z = x * x;
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            r = p / q;
            return Pio2_hi - (x - (Pio2_lo - x * r));
        }
        else if (hx < 0)
        {                                               // x < -0.5
            z = (One + x) * 0.5;
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            s = Math.Sqrt(z);
            r = p / q;
            w = r * s - Pio2_lo;
            return Pi - 2.0 * (s + w);
        }
        else
        {                                               // x > 0.5
            z = (One - x) * 0.5;
            s = Math.Sqrt(z);
            df = s;
            df = SetLo(df, 0);
            c = (z - df * df) / (s + df);
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            r = p / q;
            w = r * s + c;
            return 2.0 * (df + w);
        }
    }
}
