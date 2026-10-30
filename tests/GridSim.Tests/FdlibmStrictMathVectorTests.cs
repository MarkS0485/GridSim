using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// D1 (October 2026, Mark's determinism rule 2026-10-04): GridSim's fdlibm port (<see cref="DetMath"/>) is checked
/// bit for bit against Java's <c>StrictMath</c>, which the Java specification defines as the results of FDLIBM 5.3.
///
/// Reference: tests/GridSim.Tests/fixtures/strictmath/strictmath-vectors.txt, written by
/// tools/strictmath/StrictMathVectors.java running on Eclipse Temurin 21.0.12.1+1 (Windows x64). It holds explicit
/// vectors for the special and boundary arguments of every function, and SHA-256 digests over 20,000 pseudo-random
/// arguments per argument class (about 1.1 million results across 14 functions). The pseudo-random arguments come
/// from SplitMix64 and plain IEEE arithmetic; <see cref="Gen"/> below is the exact mirror of the Java generator, so
/// a digest line is checked by regenerating the same arguments here, evaluating DetMath, and hashing the results
/// the same way. Any single-bit difference in any one result changes the digest.
///
/// Because fdlibm is plain IEEE double arithmetic with no fused multiply-add and no platform call, these tests pass
/// with the same bits in every SIMD mode (normal, DOTNET_EnableAVX2=0, DOTNET_EnableHWIntrinsic=0) and on every
/// operating system; the full suite runs them in all three modes.
/// </summary>
public class FdlibmStrictMathVectorTests
{
    private static string VectorFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "tests", "GridSim.Tests", "fixtures", "strictmath", "strictmath-vectors.txt");
        Assert.True(File.Exists(path), path);
        return path;
    }

    private static readonly Lazy<string[]> Lines = new(() => File.ReadAllLines(VectorFile()));

    private static Func<double, double> Unary(string fn) => fn switch
    {
        "sin" => DetMath.Sin,
        "cos" => DetMath.Cos,
        "tan" => DetMath.Tan,
        "asin" => DetMath.Asin,
        "acos" => DetMath.Acos,
        "atan" => DetMath.Atan,
        "exp" => DetMath.Exp,
        "expm1" => DetMath.Expm1,
        "log" => DetMath.Log,
        "log10" => DetMath.Log10,
        "sinh" => DetMath.Sinh,
        "cosh" => DetMath.Cosh,
        _ => throw new InvalidOperationException($"unknown unary function '{fn}' in the vector file"),
    };

    private static Func<double, double, double> Binary(string fn) => fn switch
    {
        "pow" => DetMath.Pow,
        "atan2" => DetMath.Atan2,
        _ => throw new InvalidOperationException($"unknown binary function '{fn}' in the vector file"),
    };

    private static bool IsBinary(string fn) => fn is "pow" or "atan2";

    private static double FromHex(string h) => BitConverter.Int64BitsToDouble(long.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Hex(double d) => BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture);

    // Every NaN compares as the one canonical pattern (NaN payloads are not part of the StrictMath contract).
    private static long Canon(double d) => double.IsNaN(d) ? 0x7ff8000000000000L : BitConverter.DoubleToInt64Bits(d);

    // ---- the mirror of StrictMathVectors.java's argument generator ------------------------------------------------

    private sealed class Gen
    {
        private ulong _state;
        public Gen(long seed) { _state = (ulong)seed; }

        // SplitMix64, as Java's next(): same constants, wrapping 64-bit arithmetic.
        private ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private double Unit() => (double)(Next() >> 11) * (1.0 / 9007199254740992.0);   // * 2^-53, exact

        private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        public double Draw(string spec)
        {
            string[] p = spec.Split(':', 2);
            switch (p[0])
            {
                case "bits": return BitConverter.Int64BitsToDouble((long)Next());
                case "sub":
                {
                    ulong a = Next();
                    double v = BitConverter.Int64BitsToDouble((long)(a & 0x000fffffffffffffUL));
                    return ((long)a < 0) ? -v : v;
                }
                case "r":
                {
                    string[] q = p[1].Split(':');
                    double lo = Num(q[0]), hi = Num(q[1]);
                    return lo + (hi - lo) * Unit();
                }
                case "lu":
                {
                    string[] q = p[1].Split(':');
                    int e1 = int.Parse(q[0], CultureInfo.InvariantCulture), e2 = int.Parse(q[1], CultureInfo.InvariantCulture);
                    ulong a = Next();
                    double m = 1.0 + (double)(a >> 12) * (1.0 / 4503599627370496.0);   // * 2^-52, exact
                    int e = e1 + (int)((Next() >> 33) % (ulong)(e2 - e1 + 1));
                    double v = Math.ScaleB(m, e);   // exact: the classes keep the result normal
                    return ((a & 1UL) != 0) ? -v : v;
                }
                case "int":
                {
                    string[] q = p[1].Split(':');
                    long lo = long.Parse(q[0], CultureInfo.InvariantCulture), hi = long.Parse(q[1], CultureInfo.InvariantCulture);
                    return (double)(lo + (long)((Next() >> 11) % (ulong)(hi - lo + 1)));
                }
                case "nearpio2":
                {
                    long k = long.Parse(p[1], CultureInfo.InvariantCulture);
                    long n = (long)((Next() >> 11) % (ulong)(2 * k + 1)) - k;
                    double x = n * (Math.PI / 2);
                    int j = (int)((Next() >> 11) % 7UL) - 3;
                    for (; j > 0; j--) x = Math.BitIncrement(x);
                    for (; j < 0; j++) x = Math.BitDecrement(x);
                    return x;
                }
                case "near1":
                {
                    int e = int.Parse(p[1], CultureInfo.InvariantCulture);
                    return 1.0 + (Unit() * 2.0 - 1.0) * Math.ScaleB(1.0, -e);
                }
                case "nearm1":
                {
                    int e = int.Parse(p[1], CultureInfo.InvariantCulture);
                    return -(1.0 + (Unit() * 2.0 - 1.0) * Math.ScaleB(1.0, -e));
                }
                case "abs": return Math.Abs(Draw(p[1]));
                default: throw new InvalidOperationException($"unknown argument class '{spec}' in the vector file");
            }
        }
    }

    private sealed class Digest : IDisposable
    {
        private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _b = new byte[8];
        public void Add(double r)
        {
            long bits = Canon(r);
            for (int i = 0; i < 8; i++) _b[i] = (byte)(bits >> (8 * i));
            _h.AppendData(_b);
        }
        public string Done() => Convert.ToHexString(_h.GetHashAndReset()).ToLowerInvariant();
        public void Dispose() => _h.Dispose();
    }

    public static TheoryData<string> Functions =>
        new() { "sin", "cos", "tan", "asin", "acos", "atan", "exp", "expm1", "log", "log10", "sinh", "cosh", "pow", "atan2" };

    [Theory]
    [MemberData(nameof(Functions))]
    public void Explicit_vectors_match_Java_StrictMath_bit_for_bit(string fn)
    {
        int checkedCount = 0;
        var failures = new List<string>();
        foreach (string line in Lines.Value)
        {
            if (!line.StartsWith("v ", StringComparison.Ordinal)) continue;
            string[] t = line.Split(' ');
            if (t[1] != fn) continue;
            double got, want;
            string args;
            if (IsBinary(fn))
            {
                double x = FromHex(t[2]), y = FromHex(t[3]);
                got = Binary(fn)(x, y);
                want = FromHex(t[4]);
                args = $"{t[2]}, {t[3]}";
            }
            else
            {
                double x = FromHex(t[2]);
                got = Unary(fn)(x);
                want = FromHex(t[3]);
                args = t[2];
            }
            if (Canon(got) != Canon(want) && failures.Count < 20)
                failures.Add($"{fn}({args}) = {Hex(got)}, StrictMath {Hex(want)}");
            checkedCount++;
        }
        Assert.True(checkedCount > 100, $"only {checkedCount} explicit vectors for {fn}: the vector file is incomplete");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public void Pseudo_random_digests_match_Java_StrictMath_bit_for_bit(string fn)
    {
        int classes = 0;
        var failures = new List<string>();
        foreach (string line in Lines.Value)
        {
            if (!line.StartsWith("d ", StringComparison.Ordinal)) continue;
            string[] t = line.Split(' ');
            if (t[1] != fn) continue;
            bool bin = IsBinary(fn);
            string cx = t[2], cy = bin ? t[3] : "";
            int k = bin ? 4 : 3;
            long seed = long.Parse(t[k], CultureInfo.InvariantCulture);
            int n = int.Parse(t[k + 1], CultureInfo.InvariantCulture);
            string want = t[k + 2];
            var g = new Gen(seed);
            using var d = new Digest();
            for (int i = 0; i < n; i++)
            {
                if (bin)
                {
                    double x = g.Draw(cx);
                    double y = g.Draw(cy);
                    d.Add(Binary(fn)(x, y));
                }
                else d.Add(Unary(fn)(g.Draw(cx)));
            }
            string got = d.Done();
            if (got != want) failures.Add($"{fn} over {n} arguments of class {cx} {cy} (seed {seed}): digest {got}, StrictMath {want}");
            classes++;
        }
        Assert.True(classes >= 6, $"only {classes} digest classes for {fn}: the vector file is incomplete");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void SinCos_halves_are_bit_identical_to_Sin_and_Cos()
    {
        // Over every sin argument in the file (specials and the regenerated classes): SinCos shares one reduction,
        // and must still give exactly the bits of the two separate calls.
        int n = 0;
        foreach (string line in Lines.Value)
        {
            string[] t = line.Split(' ');
            if (t.Length < 3 || t[1] != "sin") continue;
            IEnumerable<double> xs;
            if (t[0] == "v") xs = new[] { FromHex(t[2]) };
            else if (t[0] == "d")
            {
                var g = new Gen(long.Parse(t[3], CultureInfo.InvariantCulture));
                int count = int.Parse(t[4], CultureInfo.InvariantCulture);
                var list = new List<double>(count);
                for (int i = 0; i < count; i++) list.Add(g.Draw(t[2]));
                xs = list;
            }
            else continue;
            foreach (double x in xs)
            {
                (double s, double c) = DetMath.SinCos(x);
                Assert.Equal(Canon(DetMath.Sin(x)), Canon(s));
                Assert.Equal(Canon(DetMath.Cos(x)), Canon(c));
                n++;
            }
        }
        Assert.True(n > 100000, $"only {n} SinCos arguments checked");
    }

    [Fact]
    public void Complex_helpers_are_the_documented_formulas_over_the_deterministic_functions()
    {
        var rng = new Gen(77);
        for (int i = 0; i < 2000; i++)
        {
            double re = rng.Draw("r:-20:20"), im = rng.Draw("r:-20:20");
            var z = new Complex(re, im);

            Assert.Equal(Canon(DetMath.Atan2(im, re)), Canon(DetMath.Phase(z)));

            Complex p = DetMath.FromPolar(re, im);
            Assert.Equal(Canon(re * DetMath.Cos(im)), Canon(p.Real));
            Assert.Equal(Canon(re * DetMath.Sin(im)), Canon(p.Imaginary));

            Complex sh = DetMath.Sinh(z);
            Assert.Equal(Canon(DetMath.Sinh(re) * DetMath.Cos(im)), Canon(sh.Real));
            Assert.Equal(Canon(DetMath.Cosh(re) * DetMath.Sin(im)), Canon(sh.Imaginary));

            Complex th = DetMath.Tanh(z);
            double dd = DetMath.Cosh(2 * re) + DetMath.Cos(2 * im);
            Assert.Equal(Canon(DetMath.Sinh(2 * re) / dd), Canon(th.Real));
            Assert.Equal(Canon(DetMath.Sin(2 * im) / dd), Canon(th.Imaginary));

            // And each agrees with System.Numerics to rounding (the platform library differs only in the last bits).
            Assert.True(Complex.Abs(sh - Complex.Sinh(z)) <= 1e-12 * Math.Max(1.0, Complex.Abs(sh)), $"sinh({z})");
            Assert.True(Complex.Abs(th - Complex.Tanh(z)) <= 1e-12 * Math.Max(1.0, Complex.Abs(th)), $"tanh({z})");
        }

        // Large real part: cosh(2a) overflows, the exact limit is returned instead of inf / inf = NaN.
        Complex big = DetMath.Tanh(new Complex(400.0, 0.3));
        Assert.Equal(1.0, big.Real);
        Assert.Equal(0.0, big.Imaginary);
        Complex bigNeg = DetMath.Tanh(new Complex(-400.0, -0.3));
        Assert.Equal(-1.0, bigNeg.Real);
        Assert.True(double.IsNegative(bigNeg.Imaginary));
    }
}
