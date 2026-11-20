using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// Thread-count-invariant summation and block scheduling. Items are cut into fixed blocks of <see cref="BlockSize"/>
/// (1024) consecutive indices. Each block is summed left to right; the block sums are then combined by a fixed pairwise
/// tree in index order (level by level: block 0 with block 1, block 2 with block 3, an unpaired last block carried up
/// unchanged). Parallelism is across blocks only, so the floating-point operations and their order are the same for one
/// thread and for sixteen: the result is bit-identical.
/// </summary>
public static class FixedOrderReduction
{
    /// <summary>The fixed block size.</summary>
    public const int BlockSize = 1024;

    /// <summary>Run <paramref name="body"/>(lo, hi) over the blocks covering [0, n), on up to <paramref name="maxThreads"/>
    /// threads (0 or less: the runtime default; 1: serial). The lowest failing block's exception is rethrown unwrapped.</summary>
    public static void ForBlocks(int n, int maxThreads, Action<int, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        int blocks = (int)(((long)n + BlockSize - 1) / BlockSize);
        if (blocks == 0) return;
        var errors = new Exception?[blocks];
        void One(int b)
        {
            int lo = b * BlockSize;
            int hi = (int)Math.Min((long)n, (long)lo + BlockSize);
            try { body(lo, hi); } catch (Exception ex) { errors[b] = ex; }
        }
        if (maxThreads == 1 || blocks == 1)
        {
            for (int b = 0; b < blocks; b++) One(b);
        }
        else
        {
            var opt = new ParallelOptions();
            if (maxThreads > 1) opt.MaxDegreeOfParallelism = maxThreads;
            Parallel.For(0, blocks, opt, One);
        }
        foreach (Exception? ex in errors)
            if (ex is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
    }

    /// <summary>The fixed-tree sum of <c>value(i)</c> for i in [0, n). Refuses n &lt;= 0; a non-finite value propagates.</summary>
    public static double Sum(int n, Func<int, double> value, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        Uniform.RequireCount(n);
        int blocks = (int)(((long)n + BlockSize - 1) / BlockSize);
        var sums = new double[blocks];
        ForBlocks(n, maxThreads, (lo, hi) =>
        {
            double s = 0.0;
            for (int i = lo; i < hi; i++) s += value(i);
            sums[lo / BlockSize] = s;
        });
        return TreeSum(sums);
    }

    /// <summary>The fixed-tree sum of an array.</summary>
    public static double Sum(double[] values, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Sum(values.Length, i => values[i], maxThreads);
    }

    /// <summary>The fixed-tree mean (sum divided by n).</summary>
    public static double Mean(double[] values, int maxThreads = 0) => Sum(values, maxThreads) / values.Length;

    /// <summary>The pairwise tree over block sums, in index order.</summary>
    public static double TreeSum(double[] blockSums)
    {
        if (blockSums.Length == 0)
            throw DomainRefusal.Mark(new ArgumentException("Nothing to sum.", nameof(blockSums)));
        double[] cur = (double[])blockSums.Clone();
        int len = cur.Length;
        while (len > 1)
        {
            int next = 0;
            for (int i = 0; i + 1 < len; i += 2) cur[next++] = cur[i] + cur[i + 1];
            if ((len & 1) == 1) cur[next++] = cur[len - 1];
            len = next;
        }
        return cur[0];
    }
}
