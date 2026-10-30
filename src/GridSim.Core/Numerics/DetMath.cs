using System.Numerics;
using System.Runtime.CompilerServices;

namespace GridSim.Core.Numerics;

/// <summary>
/// D1 (October 2026, Mark's determinism rule 2026-10-04: "a 1 bit change is enough to break machine epsilon"):
/// GridSim's own deterministic transcendental functions. Every numerical path in Core calls these instead of
/// <see cref="Math"/>'s Sin / Cos / Tan / SinCos / Asin / Acos / Atan / Atan2 / Exp / Log / Log10 / Pow / Sinh / Cosh
/// and instead of <see cref="Complex.Phase"/>, <see cref="Complex.FromPolarCoordinates"/>, <see cref="Complex.Sinh"/>
/// and <see cref="Complex.Tanh"/>.
///
/// Why: .NET does not implement those System.Math functions itself. At run time they call the C runtime maths library
/// of the host operating system (the Microsoft C runtime on Windows, glibc's libm on Linux, Apple's libm on macOS),
/// and those libraries round some results differently in the last binary place. GridSim's iterative solvers carry
/// such a difference forward, so the same case gave the same answer to a few units in the last place, but not the
/// same bits, on Linux and Windows (N175: 33 exact-bit pins differed on ubuntu-latest).
///
/// What: a C# port of Sun's FDLIBM 5.3 (<see cref="Fdlibm"/>), the library Java's <c>StrictMath</c> is defined by.
/// It is plain IEEE 754 double arithmetic and integer bit work, no fused multiply-add, no platform call, so it gives
/// the same bits on every CPU tier (AVX-512, AVX2, no hardware intrinsics), every instruction set and every operating
/// system. Accuracy is fdlibm's: under 1 ulp for every function (not always correctly rounded, which is irrelevant
/// to determinism: what matters is that the rounding is the same everywhere). Proved bit for bit against Java 21
/// <c>StrictMath</c> by <c>FdlibmStrictMathVectorTests</c>.
///
/// <see cref="Math.Sqrt"/>, <see cref="Math.Abs"/>, <see cref="Math.Floor"/>, <see cref="Math.Round"/>,
/// <see cref="Math.CopySign"/>, <see cref="Math.ScaleB"/> and <see cref="Math.IEEERemainder"/> stay on System.Math:
/// IEEE 754 defines each of them exactly (the square root is correctly rounded; the rest are exact), so every
/// conforming implementation gives the same bits. <see cref="Complex"/>'s arithmetic, <see cref="Complex.Magnitude"/>
/// and <see cref="Complex.Sqrt(Complex)"/> are managed .NET code built only from those, so they also stay.
/// <c>DeterministicMathGuardTests</c> fails the build's test run if a System.Math / System.Numerics.Complex
/// transcendental call appears anywhere in GridSim.Core outside this module.
/// </summary>
public static class DetMath
{
    /// <summary>The one NaN bit pattern GridSim reports: the positive quiet NaN 0x7FF8000000000000.</summary>
    public const long CanonicalNaNBits = 0x7FF8000000000000;

    /// <summary>
    /// x unchanged, except that any NaN becomes the one canonical quiet NaN (<see cref="CanonicalNaNBits"/>).
    /// Why (final batch, 2026-10-10): IEEE 754 does not define the sign or payload of a NaN that arithmetic produces,
    /// and on x64 the bits really do vary: with two NaN operands an SSE instruction returns its first operand, and the
    /// JIT may swap the operands of a commutative operation at a higher optimisation tier. Measured: a diverged
    /// frequency run (inertia floored to 1 MW.s, HWIntrinsic off) gave NaN 0x7FF8... before tier-up and 0xFFF8...
    /// after, from the same source. A NaN result is not a number, but its bytes are still output (JSON, CSV, exact
    /// comparisons), so a consumer that outputs or compares a double's BITS passes it through here and so sees the same
    /// bytes on every run, tier, CPU and OS. Finite and infinite values are returned bit for bit.
    /// <para><b>Where it is applied (and where it is not).</b> The frequency result types (FrequencyTypes,
    /// FrequencyDynamics) canonicalise their samples, and the web map payload's bit-exact change test canonicalises both
    /// sides before comparing. Other result types (power flow, estimation, bad-data, cascade, CPF) are NOT canonicalised:
    /// they write <c>double.NaN</c> (bit pattern 0xFFF8000000000000 in .NET) or arithmetic NaNs whose sign can vary, so a
    /// consumer that compares their raw bits must canonicalise first. Text outputs are unaffected (every NaN prints as
    /// <c>NaN</c>).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CanonicalNaN(double x) => double.IsNaN(x) ? BitConverter.Int64BitsToDouble(CanonicalNaNBits) : x;

    /// <summary>sin(x), fdlibm s_sin.c (same bits as Java StrictMath.sin).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Sin(double x) => Fdlibm.Sin(x);

    /// <summary>cos(x), fdlibm s_cos.c (same bits as Java StrictMath.cos).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Cos(double x) => Fdlibm.Cos(x);

    /// <summary>
    /// (sin x, cos x) with one shared argument reduction. Each half is bit-identical to <see cref="Sin"/> and
    /// <see cref="Cos"/> called separately (the same fdlibm statements run), so a caller may use either form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double Sin, double Cos) SinCos(double x) => Fdlibm.SinCos(x);

    /// <summary>tan(x), fdlibm s_tan.c.</summary>
    public static double Tan(double x) => Fdlibm.Tan(x);

    /// <summary>asin(x), fdlibm e_asin.c.</summary>
    public static double Asin(double x) => Fdlibm.Asin(x);

    /// <summary>acos(x), fdlibm e_acos.c.</summary>
    public static double Acos(double x) => Fdlibm.Acos(x);

    /// <summary>atan(x), fdlibm s_atan.c.</summary>
    public static double Atan(double x) => Fdlibm.Atan(x);

    /// <summary>atan2(y, x), fdlibm e_atan2.c: the angle of (x, y) in (-pi, pi], argument order as <see cref="Math.Atan2"/>.</summary>
    public static double Atan2(double y, double x) => Fdlibm.Atan2(y, x);

    /// <summary>e^x, fdlibm e_exp.c.</summary>
    public static double Exp(double x) => Fdlibm.Exp(x);

    /// <summary>e^x - 1, fdlibm s_expm1.c (accurate near zero).</summary>
    public static double Expm1(double x) => Fdlibm.Expm1(x);

    /// <summary>Natural logarithm, fdlibm e_log.c.</summary>
    public static double Log(double x) => Fdlibm.Log(x);

    /// <summary>Base-10 logarithm, fdlibm e_log10.c.</summary>
    public static double Log10(double x) => Fdlibm.Log10(x);

    /// <summary>
    /// x^y, fdlibm e_pow.c. For a small whole-number power written in the source (x^2, x^3, ...) callers multiply
    /// instead (exact for x^2, which fdlibm also returns as x*x, and cheaper for the rest).
    /// </summary>
    public static double Pow(double x, double y) => Fdlibm.Pow(x, y);

    /// <summary>sinh(x), fdlibm e_sinh.c.</summary>
    public static double Sinh(double x) => Fdlibm.Sinh(x);

    /// <summary>cosh(x), fdlibm e_cosh.c.</summary>
    public static double Cosh(double x) => Fdlibm.Cosh(x);

    // ---- System.Numerics.Complex replacements -------------------------------------------------------------------

    /// <summary>
    /// The phase (argument) of <paramref name="z"/> in (-pi, pi]: <c>Atan2(z.Imaginary, z.Real)</c>, which is exactly
    /// what <see cref="Complex.Phase"/> computes, but through <see cref="Atan2"/> instead of the platform's atan2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Phase(Complex z) => Fdlibm.Atan2(z.Imaginary, z.Real);

    /// <summary>
    /// The complex number with the given magnitude and phase: <c>(m cos p, m sin p)</c>, the same formula as
    /// <see cref="Complex.FromPolarCoordinates"/> (which forms it from Math.SinCos), through <see cref="SinCos"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex FromPolar(double magnitude, double phase)
    {
        (double s, double c) = Fdlibm.SinCos(phase);
        return new Complex(magnitude * c, magnitude * s);
    }

    /// <summary>
    /// Complex hyperbolic sine, sinh(a + ib) = sinh(a) cos(b) + i cosh(a) sin(b): the identity
    /// <see cref="Complex.Sinh"/> evaluates, through the deterministic real functions.
    /// </summary>
    public static Complex Sinh(Complex z)
    {
        double a = z.Real, b = z.Imaginary;
        (double sb, double cb) = Fdlibm.SinCos(b);
        return new Complex(Fdlibm.Sinh(a) * cb, Fdlibm.Cosh(a) * sb);
    }

    /// <summary>
    /// Complex hyperbolic tangent, tanh(a + ib) = (sinh 2a + i sin 2b) / (cosh 2a + cos 2b): the identity
    /// <see cref="Complex.Tanh"/> evaluates, through the deterministic real functions. It returns the exact limit rather
    /// than inf / inf (GR-453: this is not a refusal): when |2a| is so large that cosh 2a overflows (|a| above about 355),
    /// the identity gives inf / inf, and the exact limit is used instead, tanh -> sign(a) + i 0 (the imaginary part is sin 2b / cosh 2a, which
    /// underflows to a zero carrying the sign of sin 2b).
    /// </summary>
    public static Complex Tanh(Complex z)
    {
        double x = 2.0 * z.Real;
        double y = 2.0 * z.Imaginary;
        double coshX = Fdlibm.Cosh(x);
        (double sinY, double cosY) = Fdlibm.SinCos(y);
        if (double.IsInfinity(coshX) && double.IsFinite(y))
            return new Complex(Math.CopySign(1.0, x), Math.CopySign(0.0, sinY));
        double d = coshX + cosY;
        return new Complex(Fdlibm.Sinh(x) / d, sinY / d);
    }
}
