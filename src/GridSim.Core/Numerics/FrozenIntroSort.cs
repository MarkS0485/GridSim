namespace GridSim.Core.Numerics;

/// <summary>
/// GridSim's own copy of the comparison sort the .NET 10 base class library uses for
/// <c>Array.Sort(T[], Comparison&lt;T&gt;)</c> and <c>Array.Sort(T[], int, int, IComparer&lt;T&gt;)</c> (the introspective
/// sort in <c>ArraySortHelper&lt;T&gt;</c>: insertion sort up to 16 elements, median-of-three quicksort, heapsort past
/// the depth limit), reproduced step for step.
///
/// <para><b>Why it exists.</b> An introsort is not stable: when the comparer calls two elements equal (two nodes of the
/// same degree in the reverse Cuthill-McKee ordering), which one comes first is decided by the algorithm's internals and
/// the input order. Those internals belong to the runtime, not to GridSim, so a .NET servicing or major release that
/// tuned them (a different insertion-sort threshold, a different pivot rule) would change the RCM permutation, hence the
/// pivot order of every sparse LU, hence the last bits of every sparse result, with no GridSim change at all. Copying the
/// algorithm freezes it: the order is now GridSim's, the same on every runtime and OS, and on .NET 10 it is exactly the
/// order <c>Array.Sort</c> gives today, so no result moves (<c>FrozenIntroSortTests</c> checks it against the runtime's
/// sort over many tie-heavy inputs, and pins fixed permutations against a future change of either).</para>
///
/// <para>Use it wherever a sort with ties feeds numerics. Prefer a total-order key (no ties) for new code; this is for
/// keeping an established order bit-for-bit.</para>
/// </summary>
public static class FrozenIntroSort
{
    // The BCL's threshold below which a partition is insertion-sorted.
    private const int IntrosortSizeThreshold = 16;

    /// <summary>Sort <paramref name="keys"/>[<paramref name="index"/> .. +<paramref name="length"/>) in place by
    /// <paramref name="comparer"/>, exactly as the .NET 10 introsort does.</summary>
    public static void Sort<T>(T[] keys, int index, int length, Comparison<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (keys.Length - index < length) throw new ArgumentException("index + length is past the end of the array.");
        Span<T> span = keys.AsSpan(index, length);
        if (span.Length > 1)
            IntroSort(span, 2 * (System.Numerics.BitOperations.Log2((uint)span.Length) + 1), comparer);
    }

    /// <summary>Sort the whole of <paramref name="keys"/> in place (see <see cref="Sort{T}(T[], int, int, Comparison{T})"/>).</summary>
    public static void Sort<T>(T[] keys, Comparison<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Sort(keys, 0, keys.Length, comparer);
    }

    /// <summary>Sort a list in place, exactly as <c>List&lt;T&gt;.Sort(Comparison&lt;T&gt;)</c> does on .NET 10 (it sorts
    /// its backing array with the same introsort).</summary>
    public static void Sort<T>(List<T> list, Comparison<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(comparer);
        Span<T> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
        if (span.Length > 1)
            IntroSort(span, 2 * (System.Numerics.BitOperations.Log2((uint)span.Length) + 1), comparer);
    }

    private static void IntroSort<T>(Span<T> keys, int depthLimit, Comparison<T> comparer)
    {
        int partitionSize = keys.Length;
        while (partitionSize > 1)
        {
            if (partitionSize <= IntrosortSizeThreshold)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(keys, comparer, 0, 1);
                    return;
                }
                if (partitionSize == 3)
                {
                    SwapIfGreater(keys, comparer, 0, 1);
                    SwapIfGreater(keys, comparer, 0, 2);
                    SwapIfGreater(keys, comparer, 1, 2);
                    return;
                }
                InsertionSort(keys[..partitionSize], comparer);
                return;
            }
            if (depthLimit == 0)
            {
                HeapSort(keys[..partitionSize], comparer);
                return;
            }
            depthLimit--;
            int p = PickPivotAndPartition(keys[..partitionSize], comparer);
            // Sort the right part recursively, then loop on the left part (the BCL's order of work).
            IntroSort(keys[(p + 1)..partitionSize], depthLimit, comparer);
            partitionSize = p;
        }
    }

    private static void SwapIfGreater<T>(Span<T> keys, Comparison<T> comparer, int i, int j)
    {
        if (comparer(keys[i], keys[j]) > 0)
            (keys[i], keys[j]) = (keys[j], keys[i]);
    }

    private static void Swap<T>(Span<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static int PickPivotAndPartition<T>(Span<T> keys, Comparison<T> comparer)
    {
        int hi = keys.Length - 1;
        // Median of three: put the median of keys[0], keys[middle], keys[hi] in the middle.
        int middle = hi >> 1;
        SwapIfGreater(keys, comparer, 0, middle);
        SwapIfGreater(keys, comparer, 0, hi);
        SwapIfGreater(keys, comparer, middle, hi);

        T pivot = keys[middle];
        Swap(keys, middle, hi - 1);
        int left = 0, right = hi - 1;   // keys[0] and keys[hi - 1] bound the scans
        while (left < right)
        {
            while (comparer(keys[++left], pivot) < 0) { }
            while (comparer(pivot, keys[--right]) < 0) { }
            if (left >= right) break;
            Swap(keys, left, right);
        }
        // Put the pivot in its final place.
        if (left != hi - 1) Swap(keys, left, hi - 1);
        return left;
    }

    private static void HeapSort<T>(Span<T> keys, Comparison<T> comparer)
    {
        int n = keys.Length;
        for (int i = n >> 1; i >= 1; i--)
            DownHeap(keys, i, n, comparer);
        for (int i = n; i > 1; i--)
        {
            Swap(keys, 0, i - 1);
            DownHeap(keys, 1, i - 1, comparer);
        }
    }

    private static void DownHeap<T>(Span<T> keys, int i, int n, Comparison<T> comparer)
    {
        T d = keys[i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && comparer(keys[child - 1], keys[child]) < 0) child++;
            if (!(comparer(d, keys[child - 1]) < 0)) break;
            keys[i - 1] = keys[child - 1];
            i = child;
        }
        keys[i - 1] = d;
    }

    private static void InsertionSort<T>(Span<T> keys, Comparison<T> comparer)
    {
        for (int i = 0; i < keys.Length - 1; i++)
        {
            T t = keys[i + 1];
            int j = i;
            while (j >= 0 && comparer(t, keys[j]) < 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }
            keys[j + 1] = t;
        }
    }
}
