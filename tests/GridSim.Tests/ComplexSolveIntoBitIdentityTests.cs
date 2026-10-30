using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Numerics;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N060 / R115 (October 2026 review, S5): the complex sparse factorisation gained a <c>SolveInto</c> that writes the
/// solution into a caller array and reuses its own real 2n-vectors, so the sparse RMS network's per-solve solution
/// vector (and the factorisation's three internal vectors) are no longer allocated. These tests prove the values are
/// bit-identical to <c>Solve</c> (repeated right-hand sides, dirty output buffers, the default interface method, the
/// dense and sparse fault-network paths) and that a warmed-up sparse RMS solve allocates nothing.
/// </summary>
public class ComplexSolveIntoBitIdentityTests
{
    private static void AssertBits(Complex[] expected, Complex[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Real), BitConverter.DoubleToInt64Bits(actual[i].Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Imaginary), BitConverter.DoubleToInt64Bits(actual[i].Imaginary));
        }
    }

    /// <summary>A random diagonally dominant sparse complex system (a network-like pattern: a ring plus chords).</summary>
    private static List<(int Row, int Col, Complex V)> Triplets(int n, int seed)
    {
        var rng = new Random(seed);
        var t = new List<(int, int, Complex)>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int k = rng.Next(n);
            foreach (int c in new[] { j, k })
            {
                if (c == i) continue;
                var y = new Complex(rng.NextDouble(), -5.0 * rng.NextDouble() - 1.0);
                t.Add((i, c, -y)); t.Add((c, i, -y)); t.Add((i, i, y)); t.Add((c, c, y));
            }
            t.Add((i, i, new Complex(0.01 + rng.NextDouble(), 0.1)));   // a shunt: no floating island
        }
        return t;
    }

    private static Complex[] Rhs(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new Complex[n];
        for (int i = 0; i < n; i++) b[i] = rng.Next(7) == 0 ? Complex.Zero : new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        return b;
    }

    private static ComplexCsr Csr(int n, List<(int Row, int Col, Complex V)> triplets)
    {
        var cells = new Dictionary<(int, int), Complex>();
        foreach ((int r, int c, Complex v) in triplets) cells[(r, c)] = cells.TryGetValue((r, c), out Complex cur) ? cur + v : Complex.Zero + v;
        var builder = new ComplexCsr.Builder(n);
        foreach (var kv in cells) builder.Add(kv.Key.Item1, kv.Key.Item2, kv.Value.Real, kv.Value.Imaginary);
        return builder.Build();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(400)]
    public void Real_expanded_SolveInto_is_bit_identical_to_Solve_over_many_right_hand_sides(int n)
    {
        IComplexLinearFactorization lu = new RealExpandedComplexSolver().Factor(Csr(n, Triplets(n, n)))!;
        var x = new Complex[n];
        Array.Fill(x, new Complex(double.NaN, -0.0));   // dirty output: must be fully overwritten
        for (int k = 0; k < 6; k++)
        {
            Complex[] b = Rhs(n, 100 + k);
            Complex[] expected = lu.Solve(b)!;
            Assert.True(lu.SolveInto(b, x));
            AssertBits(expected, x);
        }
    }

    /// <summary>A factorisation that only implements Solve gets the default SolveInto, which copies.</summary>
    private sealed class SolveOnly : IComplexLinearFactorization
    {
        private readonly Complex[]? _answer;
        public SolveOnly(Complex[]? answer) { _answer = answer; }
        public Complex[]? Solve(Complex[] b) => _answer is null ? null : (Complex[])_answer.Clone();
    }

    [Fact]
    public void Default_SolveInto_copies_Solve_and_reports_a_singular_factorisation_as_false()
    {
        var answer = new[] { new Complex(1, -0.0), new Complex(-0.0, 2) };
        IComplexLinearFactorization f = new SolveOnly(answer);
        var x = new Complex[2];
        Assert.True(f.SolveInto(new Complex[2], x));
        AssertBits(answer, x);
        IComplexLinearFactorization singular = new SolveOnly(null);
        Assert.False(singular.SolveInto(new Complex[2], x));
        Assert.Throws<ArgumentException>(() => f.SolveInto(new Complex[2], new Complex[3]));
        var same = new Complex[2];
        Assert.Throws<ArgumentException>(() => f.SolveInto(same, same));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fault_network_SolveInto_matches_Solve_on_the_dense_and_sparse_paths(bool sparse)
    {
        const int n = 80;
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the sparse engine is now the default at every size and
        // the dense LU is a test oracle under DenseReferenceScope (it was forceSparse: sparse). Same two systems, same checks.
        FaultNetworkSystem sys;
        if (sparse) sys = FaultNetworkSystem.FromTriplets(n, Triplets(n, 7));
        else using (FaultNetworkSystem.DenseReferenceScope()) sys = FaultNetworkSystem.FromTriplets(n, Triplets(n, 7));
        Assert.Equal(sparse, sys.IsSparse);
        var x = new Complex[n];
        for (int k = 0; k < 4; k++)
        {
            Complex[] b = Rhs(n, 300 + k);
            Complex[] expected = sys.Solve(b);
            sys.SolveInto(b, x);
            AssertBits(expected, x);
        }
        Assert.Throws<ArgumentException>(() => sys.SolveInto(new Complex[n], new Complex[n - 1]));
    }

    [Fact]
    public void Warm_sparse_rms_network_solve_allocates_nothing_and_matches_Solve()
    {
        const int n = 300;
        List<(int Row, int Col, Complex V)> trip = Triplets(n, 3);
        int[] terminal = { 3, 50, 120, 299 };
        var yGen = terminal.Select(_ => Complex.One / new Complex(0.0, 0.25)).ToArray();
        int[] conv = { 10, 200 };
        foreach (int t in terminal) trip.Add((t, t, Complex.One / new Complex(0.0, 0.25)));
        var net = new SparseRmsNetwork(n, trip, terminal, yGen, conv);

        var emf = terminal.Select((_, i) => Complex.FromPolarCoordinates(1.05, 0.1 * i)).ToArray();
        var ic = new[] { new Complex(0.2, -0.1), new Complex(-0.05, 0.3) };
        var im = new Complex[terminal.Length];
        var vc = new Complex[conv.Length];
        net.SolveInto(emf, ic, im, vc);   // warm-up: allocates the scratch once
        (Complex[] imRef, Complex[] vcRef) = net.Solve(emf, ic);

        // N218 (October 2026): the allocation counter must read only what the solve allocates.
        //
        // What went wrong once: in a heavily loaded full run (DOTNET_EnableAVX2=0, the other test classes running in
        // parallel) a single 20-solve window read 6,608 bytes, and every re-run read 0. The bytes are the .NET
        // runtime's own, not the solve's: tiered compilation counts calls to each freshly compiled method and, when a
        // method crosses its call-count threshold, the thread that made the call does a little bookkeeping (it queues
        // the method for re-compilation and installs new call stubs), which can allocate on THAT thread and so shows
        // in GC.GetAllocatedBytesForCurrentThread. Call counting only starts once the runtime has seen no new method
        // compiled for a short while (100 ms by default), so under load, with other tests compiling code all the time,
        // the crossing can land at any moment, including inside the measured window.
        //
        // What the test proves, unchanged: a warmed-up SolveInto allocates nothing. How it is now measured:
        //  1. Warm up well past the runtime's call-count threshold (30 calls) so that, in a quiet process, every
        //     method on the solve path has already been promoted before measuring starts.
        //  2. Measure consecutive windows of 20 solves each. Pass only when THREE consecutive windows (60 solves in a
        //     row) each read exactly 0 bytes. A solve that allocated anything at all, on every call or on any call in
        //     60, would make a window non-zero and could never pass; only a one-off runtime event can be skipped, by
        //     moving on to the next window. This is stricter than the old single 20-solve window.
        //  3. Give up after MaxWindows and fail, listing every window's reading, so a real allocation fails loud.
        for (int k = 0; k < 64; k++) net.SolveInto(emf, ic, im, vc);

        const int WindowSolves = 20;
        const int RequiredCleanWindows = 3;
        const int MaxWindows = 30;
        var readings = new List<long>();
        int cleanInARow = 0;
        while (cleanInARow < RequiredCleanWindows && readings.Count < MaxWindows)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < WindowSolves; k++) net.SolveInto(emf, ic, im, vc);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - a0;
            readings.Add(allocated);
            cleanInARow = allocated == 0 ? cleanInARow + 1 : 0;
        }
        Assert.True(cleanInARow == RequiredCleanWindows,
            $"a warm SolveInto allocated: no {RequiredCleanWindows} consecutive {WindowSolves}-solve windows read 0 bytes " +
            $"in {readings.Count} windows; bytes per window: {string.Join(", ", readings)}");
        AssertBits(imRef, im);
        AssertBits(vcRef, vc);
    }
}
