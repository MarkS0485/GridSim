using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// FrozenIntroSort is GridSim's copy of the .NET 10 introsort, so the order of TIED elements (equal-degree nodes in the
/// RCM ordering behind every sparse LU, conjugate-pair modes, degenerate eigenvalues, near-equal LAV breakpoints) belongs
/// to GridSim and cannot move with a runtime release. On .NET 10 it must equal Array.Sort exactly (so adopting it moved
/// no result); the pinned permutations below hold on every runtime.
/// </summary>
public class FrozenIntroSortTests
{
    [Fact]
    public void On_dotnet_10_it_reproduces_array_sort_exactly_on_tie_heavy_inputs()
    {
        // The equality with the runtime's sort is the property that kept every result unchanged when GridSim adopted the
        // frozen copy; it is a statement about .NET 10, so it is checked on .NET 10.
        Assert.Equal(10, Environment.Version.Major);
        var rng = new Random(20261015);
        foreach (int n in new[] { 2, 3, 5, 16, 17, 33, 100, 1000 })
            for (int trial = 0; trial < 200; trial++)
            {
                int classes = 1 + rng.Next(Math.Max(1, n / 3));
                var degree = new int[n];
                for (int i = 0; i < n; i++) degree[i] = rng.Next(classes);
                var a = new int[n];
                for (int i = 0; i < n; i++) a[i] = i;
                if (trial % 2 == 1)
                    for (int i = n - 1; i > 0; i--) { int j = rng.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
                var b = (int[])a.Clone();
                Array.Sort(a, (x, y) => degree[x].CompareTo(degree[y]));
                FrozenIntroSort.Sort(b, (x, y) => degree[x].CompareTo(degree[y]));
                Assert.Equal(a, b);

                var la = new List<int>(b);
                var lb = new List<int>(b);
                la.Sort((x, y) => degree[y].CompareTo(degree[x]));
                FrozenIntroSort.Sort(lb, (x, y) => degree[y].CompareTo(degree[x]));
                Assert.Equal(la, lb);
            }
    }

    [Theory]
    [InlineData(12, "1, 6, 11, 4, 9, 2, 7, 0, 5, 10, 3, 8")]
    [InlineData(40, "1, 36, 31, 6, 26, 21, 16, 11, 19, 34, 29, 24, 14, 39, 4, 9, 12, 37, 2, 32, 27, 22, 7, 17, 0, 15, 25, 10, 35, 5, 30, 20, 3, 18, 28, 38, 13, 8, 33, 23")]
    [InlineData(70, "11, 1, 66, 26, 61, 6, 56, 31, 51, 36, 21, 46, 16, 41, 29, 34, 44, 49, 54, 59, 64, 39, 24, 69, 4, 19, 9, 14, 22, 67, 2, 62, 57, 7, 52, 47, 42, 12, 17, 37, 32, 27, 0, 60, 55, 50, 10, 30, 5, 65, 45, 20, 25, 35, 40, 15, 28, 3, 63, 53, 18, 8, 33, 48, 68, 23, 13, 43, 58, 38")]
    public void Tied_elements_land_in_the_pinned_order(int n, string expected)
    {
        // Five degree classes over n nodes: almost every comparison is a tie. The permutation was recorded on .NET 10,
        // where it equals Array.Sort's; it must never change on any runtime.
        var degree = new int[n];
        for (int i = 0; i < n; i++) degree[i] = (i * 7 + 3) % 5;
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        FrozenIntroSort.Sort(a, (x, y) => degree[x].CompareTo(degree[y]));
        Assert.Equal(expected, string.Join(", ", a));
    }

    [Fact]
    public void Range_sort_touches_only_the_range()
    {
        int[] a = { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
        FrozenIntroSort.Sort(a, 2, 5, (x, y) => x.CompareTo(y));
        Assert.Equal(new[] { 9, 8, 3, 4, 5, 6, 7, 2, 1, 0 }, a);
        Assert.Throws<ArgumentException>(() => FrozenIntroSort.Sort(a, 8, 5, (x, y) => x.CompareTo(y)));
    }
}
