using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R071: the CPU batched solver factors each system in place on its own segment of the batch arrays (the dense LU's
/// shared core on a span slice) instead of building a double[n,n], a solver and a flat copy per system. Every
/// solution must equal <see cref="DenseLuSolver.SolveInPlace"/>'s to the bit, at every size (including sizes that
/// leave SIMD tails and segments at odd offsets), and a singular system must still read as NaN without disturbing
/// its neighbours.
/// </summary>
public class BatchedInPlaceLuBitIdentityTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 4)]
    [InlineData(3, 9)]
    [InlineData(5, 7)]
    [InlineData(17, 6)]
    [InlineData(33, 3)]
    [InlineData(64, 2)]
    public void Every_system_equals_the_dense_solver_bit_for_bit(int n, int count)
    {
        var r = new Random(1000 * n + count);
        var a = new double[count * n * n];
        var b = new double[count * n];
        for (int i = 0; i < a.Length; i++) a[i] = r.NextDouble() * 2.0 - 1.0;
        for (int s = 0; s < count; s++)
            for (int i = 0; i < n; i++) a[s * n * n + i * n + i] += n;   // diagonally dominant: nonsingular
        for (int i = 0; i < b.Length; i++) b[i] = r.NextDouble() * 10.0 - 5.0;

        // The singular system (if the batch has at least three): an all-zero matrix in the middle.
        int singular = count >= 3 ? count / 2 : -1;
        if (singular >= 0) Array.Clear(a, singular * n * n, n * n);

        var expected = new double[count * n];
        var expectedOk = new bool[count];
        for (int s = 0; s < count; s++)
        {
            var m = new double[n, n];
            var x = new double[n];
            for (int i = 0; i < n; i++) { x[i] = b[s * n + i]; for (int j = 0; j < n; j++) m[i, j] = a[s * n * n + i * n + j]; }
            try { new DenseLuSolver().SolveInPlace(m, x); expectedOk[s] = true; }
            catch (InvalidOperationException) { Array.Fill(x, double.NaN); }
            Array.Copy(x, 0, expected, s * n, n);
        }

        bool[] ok = new CpuBatchedLuSolver().SolveBatch(count, n, (double[])a.Clone(), b);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), b.Select(BitConverter.DoubleToInt64Bits));
        if (singular >= 0) Assert.False(ok[singular]);
    }
}
