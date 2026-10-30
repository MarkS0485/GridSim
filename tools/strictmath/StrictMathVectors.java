// D1 (October 2026): generates tests/GridSim.Tests/fixtures/strictmath/strictmath-vectors.txt, the reference
// GridSim's fdlibm port (src/GridSim.Core/Numerics/Fdlibm.*.cs, front door DetMath) is checked against bit for bit.
//
// The reference is java.lang.StrictMath, which the Java specification defines as the results of FDLIBM 5.3 (the
// same library GridSim ports). This program only CALLS StrictMath; no JDK source was used for the port.
//
// Run (any JDK 17 or later; the committed file was made with Eclipse Temurin 21.0.12.1+1, Windows x64):
//   java tools/strictmath/StrictMathVectors.java > tests/GridSim.Tests/fixtures/strictmath/strictmath-vectors.txt
//
// Output lines:
//   v <fn> <x hex> [<y hex>] <result hex>          one explicit vector (special and boundary arguments)
//   d <fn> <classX> [<classY>] <seed> <count> <sha256>   a digest over <count> pseudo-random arguments
// A digest is SHA-256 over the 8-byte little-endian bit pattern of every result in order, with every NaN written as
// 0x7ff8000000000000 (NaN payloads are not part of the contract). The pseudo-random arguments come from SplitMix64
// and plain IEEE arithmetic, which C# reproduces exactly (FdlibmStrictMathVectorTests holds the mirror generator), so
// each digest line stands for <count> vectors without storing them.
//
// Argument classes (the mirror in the C# test must match these definitions exactly):
//   bits             any 64-bit pattern (every exponent, subnormals, infinities, NaNs)
//   sub              a subnormal or zero of random sign
//   r:lo:hi          lo + (hi - lo) * u, u uniform in [0, 1) with 53 random bits
//   lu:e1:e2         +-(1 + m) * 2^e, m a random 52-bit fraction, e uniform in [e1, e2] (normal results only)
//   int:lo:hi        a whole number uniform in [lo, hi]
//   nearpio2:k       n * (pi/2) for n uniform in [-k, k], then moved by -3..3 ulps
//   near1:e          1 + (2u - 1) * 2^-e
//   nearm1:e         -(1 + (2u - 1) * 2^-e)
//   abs:<class>      the absolute value of <class>
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.List;
import java.util.function.DoubleBinaryOperator;
import java.util.function.DoubleUnaryOperator;

public final class StrictMathVectors {
    private static long state;

    // SplitMix64 (Steele, Lea, Flood 2014), the 64-bit output of one step.
    private static long next() {
        long z = (state += 0x9E3779B97F4A7C15L);
        z = (z ^ (z >>> 30)) * 0xBF58476D1CE4E5B9L;
        z = (z ^ (z >>> 27)) * 0x94D049BB133111EBL;
        return z ^ (z >>> 31);
    }

    private static double unit() { return (next() >>> 11) * 0x1.0p-53; }

    private static double gen(String spec) {
        String[] p = spec.split(":", 2);
        switch (p[0]) {
            case "bits": return Double.longBitsToDouble(next());
            case "sub": {
                long a = next();
                double v = Double.longBitsToDouble(a & 0x000fffffffffffffL);
                return (a < 0) ? -v : v;
            }
            case "r": {
                String[] q = p[1].split(":");
                double lo = Double.parseDouble(q[0]), hi = Double.parseDouble(q[1]);
                return lo + (hi - lo) * unit();
            }
            case "lu": {
                String[] q = p[1].split(":");
                int e1 = Integer.parseInt(q[0]), e2 = Integer.parseInt(q[1]);
                long a = next();
                double m = 1.0 + (a >>> 12) * 0x1.0p-52;
                int e = e1 + (int) ((next() >>> 33) % (e2 - e1 + 1));
                double v = Math.scalb(m, e);
                return ((a & 1L) != 0) ? -v : v;
            }
            case "int": {
                String[] q = p[1].split(":");
                long lo = Long.parseLong(q[0]), hi = Long.parseLong(q[1]);
                return (double) (lo + (long) ((next() >>> 11) % (hi - lo + 1)));
            }
            case "nearpio2": {
                long k = Long.parseLong(p[1]);
                long n = (long) ((next() >>> 11) % (2 * k + 1)) - k;
                double x = n * (Math.PI / 2);
                int j = (int) ((next() >>> 11) % 7) - 3;
                for (; j > 0; j--) x = Math.nextUp(x);
                for (; j < 0; j++) x = Math.nextDown(x);
                return x;
            }
            case "near1": {
                int e = Integer.parseInt(p[1]);
                return 1.0 + (unit() * 2.0 - 1.0) * Math.scalb(1.0, -e);
            }
            case "nearm1": {
                int e = Integer.parseInt(p[1]);
                return -(1.0 + (unit() * 2.0 - 1.0) * Math.scalb(1.0, -e));
            }
            case "abs": return Math.abs(gen(p[1]));
            default: throw new IllegalArgumentException(spec);
        }
    }

    private static String hex(double d) { return String.format("%016x", Double.doubleToRawLongBits(d)); }

    private static long canon(double d) { return Double.isNaN(d) ? 0x7ff8000000000000L : Double.doubleToRawLongBits(d); }

    private static final class Digest {
        final MessageDigest md;
        Digest() throws Exception { md = MessageDigest.getInstance("SHA-256"); }
        void add(double r) {
            long b = canon(r);
            for (int i = 0; i < 8; i++) md.update((byte) (b >>> (8 * i)));
        }
        String done() {
            StringBuilder sb = new StringBuilder();
            for (byte x : md.digest()) sb.append(String.format("%02x", x));
            return sb.toString();
        }
    }

    // Special and boundary arguments: the IEEE specials, the fdlibm branch boundaries (by high word) and their
    // neighbours, and a few classic hard cases.
    private static List<Double> specials() {
        List<Double> s = new ArrayList<>();
        double[] base = {
            0.0, Double.MIN_VALUE, Double.MIN_NORMAL, Math.nextDown(Double.MIN_NORMAL), 0x1p-1074 * 3,
            1e-300, 0x1p-1022 * 1.5, 0x1p-60, 0x1p-57, 0x1p-54, 0x1p-29, 0x1p-28, 0x1p-27, 0x1p-20, 1e-10, 1e-5,
            0.1, 0.25, 0.3, 0.4375, 0.5, 0.6744, 0.6875, 0.75, 0.78125, Math.PI / 4, 0.975, 1.0, 1.1875,
            Math.log(2) / 2, Math.log(2), 1.5 * Math.log(2), Math.sqrt(1.5), Math.sqrt(3), 1.5, Math.PI / 2, 2.0,
            2.4375, 3 * Math.PI / 4, Math.E, 3.0, Math.PI, 4.0, 2 * Math.PI, 10.0, 22.0, 56 * Math.log(2), 100.0,
            355.0, 1000.0, 0x1p19 * Math.PI / 2, 1e6, 1e9, 0x1p31, 0x1p52, 0x1p53, 1e16, 1e22, 0x1p64, 0x1p66,
            1e100, 1e200, 1e300, 709.782712893384, 710.4758600739439, 745.1332191019411, Double.MAX_VALUE,
            Double.POSITIVE_INFINITY, Double.NaN,
        };
        for (double b : base) {
            for (double v : new double[] { b, Math.nextUp(b), Math.nextDown(b) }) {
                s.add(v);
                s.add(-v);
            }
        }
        return s;
    }

    private static void unary(String name, DoubleUnaryOperator f, String... classes) throws Exception {
        for (double x : specials()) System.out.println("v " + name + " " + hex(x) + " " + hex(f.applyAsDouble(x)));
        long seed = 1;
        for (String c : classes) {
            state = seed;
            Digest d = new Digest();
            int n = 20000;
            for (int i = 0; i < n; i++) d.add(f.applyAsDouble(gen(c)));
            System.out.println("d " + name + " " + c + " " + seed + " " + n + " " + d.done());
            seed++;
        }
    }

    private static void binary(String name, DoubleBinaryOperator f, String[][] pairs) throws Exception {
        List<Double> sp = specials();
        // A thinned grid of specials x specials (every 9th x, every 9th y) keeps the file small; the digests below
        // carry the bulk of the coverage.
        for (int i = 0; i < sp.size(); i += 9)
            for (int j = 0; j < sp.size(); j += 9) {
                double x = sp.get(i), y = sp.get(j);
                System.out.println("v " + name + " " + hex(x) + " " + hex(y) + " " + hex(f.applyAsDouble(x, y)));
            }
        long seed = 1;
        for (String[] pr : pairs) {
            state = seed;
            Digest d = new Digest();
            int n = 20000;
            for (int i = 0; i < n; i++) {
                double x = gen(pr[0]);
                double y = gen(pr[1]);
                d.add(f.applyAsDouble(x, y));
            }
            System.out.println("d " + name + " " + pr[0] + " " + pr[1] + " " + seed + " " + n + " " + d.done());
            seed++;
        }
    }

    public static void main(String[] args) throws Exception {
        System.out.println("# Java StrictMath reference vectors for GridSim's fdlibm port (D1). Generated by");
        System.out.println("# tools/strictmath/StrictMathVectors.java with " + System.getProperty("java.vendor") + " "
            + System.getProperty("java.runtime.version") + " on " + System.getProperty("os.name") + " "
            + System.getProperty("os.arch") + ". Do not edit by hand.");
        String[] trig = { "bits", "sub", "r:-4:4", "r:-100:100", "r:-1000000:1000000", "lu:-40:1023",
            "nearpio2:40", "nearpio2:1000000", "lu:-1022:-20" };
        unary("sin", StrictMath::sin, trig);
        unary("cos", StrictMath::cos, trig);
        unary("tan", StrictMath::tan, trig);
        String[] inv = { "bits", "sub", "r:-1:1", "r:0.4:0.6", "r:0.97:1", "near1:30", "nearm1:30", "lu:-60:0" };
        unary("asin", StrictMath::asin, inv);
        unary("acos", StrictMath::acos, inv);
        unary("atan", StrictMath::atan, "bits", "sub", "r:-3:3", "r:-0.5:0.5", "lu:-60:80", "lu:-1022:1023");
        unary("exp", StrictMath::exp, "bits", "sub", "r:-750:710", "r:-1:1", "r:-0.5:0.5", "lu:-60:10");
        unary("expm1", StrictMath::expm1, "bits", "sub", "r:-50:710", "r:-1:1", "r:-2:2", "lu:-60:10");
        unary("log", StrictMath::log, "abs:bits", "sub", "r:0.5:2", "near1:20", "near1:45", "lu:-1022:1023");
        unary("log10", StrictMath::log10, "abs:bits", "sub", "r:0.5:2", "near1:20", "lu:-1022:1023", "int:1:100000");
        unary("sinh", StrictMath::sinh, "bits", "sub", "r:-720:720", "r:-25:25", "r:-1:1", "lu:-60:5");
        unary("cosh", StrictMath::cosh, "bits", "sub", "r:-720:720", "r:-25:25", "r:-1:1", "lu:-60:5");
        binary("pow", StrictMath::pow, new String[][] {
            { "bits", "bits" }, { "r:0:10", "r:-30:30" }, { "lu:-1022:1023", "r:-5:5" }, { "r:-10:0", "int:-60:60" },
            { "near1:25", "lu:20:70" }, { "nearm1:25", "int:-1000000000:1000000000" }, { "r:0:2", "r:-2000:2000" },
            { "abs:bits", "r:-1:1" }, { "sub", "r:-2:2" }, { "r:0.5:2", "int:-1100:1100" },
        });
        binary("atan2", StrictMath::atan2, new String[][] {
            { "bits", "bits" }, { "r:-10:10", "r:-10:10" }, { "lu:-1022:1023", "lu:-1022:1023" },
            { "r:-1:1", "lu:-80:80" }, { "lu:-80:80", "r:-1:1" }, { "sub", "r:-1:1" },
        });
    }
}
