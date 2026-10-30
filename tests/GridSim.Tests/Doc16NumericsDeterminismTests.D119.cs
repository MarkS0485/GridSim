using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>DOC-16 D119: see the summary of <see cref="Doc16NumericsDeterminismTests"/>.</summary>
public partial class Doc16NumericsDeterminismTests
{
    // =================================================================================================================
    // D-DOC-119: the RoCoF screening order.
    // =================================================================================================================

    [Theory]
    [MemberData(nameof(EveryBundledCase))]
    public void D119_rocof_screening_order_is_defined_and_every_number_is_the_old_sorts_on_every_bundled_case(string name)
    {
        GridModel m = Load(name);
        // The screen's own inputs (RocofScreening.Screen(GridModel)), one contingency per dispatched unit in model order.
        double total = m.Generators.Sum(g => g.H * g.MBase);
        double demand = m.Buses.Sum(b => b.Pd);
        var conts = m.Generators.Where(g => g.Pg > 0).Select(g => new Contingency(g.Name ?? $"gen@{g.Bus}", g.Pg, g.H * g.MBase)).ToList();
        // Each result depends on its own contingency only, so screening them one at a time gives the unsorted list.
        var unsorted = conts.Select(c => RocofScreening.Screen(total, demand, new[] { c }).Single()).ToList();

        // The old order: List.Sort with the old comparison, on the same input list.
        var old = new List<RocofScreeningResult>(unsorted);
        old.Sort((x, y) => Math.Abs(y.InitialRocofHzPerS).CompareTo(Math.Abs(x.InitialRocofHzPerS)));
        // The defined order: |RoCoF| descending, ties in input order (LINQ's OrderByDescending is stable).
        var stable = unsorted.OrderByDescending(r => Math.Abs(r.InitialRocofHzPerS)).ToList();
        IReadOnlyList<RocofScreeningResult> now = RocofScreening.Screen(m);

        Assert.Equal(stable.Count, now.Count);
        for (int i = 0; i < now.Count; i++) Assert.Equal(stable[i], now[i]);   // the defined order, every field

        int differ = 0, firstAt = -1;
        for (int i = 0; i < now.Count; i++)
            if (!Equals(old[i], now[i])) { differ++; if (firstAt < 0) firstAt = i; }
        int tiedPairs = 0;
        for (int i = 1; i < now.Count; i++)
            if (Math.Abs(now[i].InitialRocofHzPerS) == Math.Abs(now[i - 1].InitialRocofHzPerS)) tiedPairs++;
        _out.WriteLine($"{name}: {now.Count} contingencies, {tiedPairs} adjacent exact ties in |RoCoF|; positions differing from the old "
            + $"unstable sort: {differ}" + (firstAt >= 0 ? $" (first at {firstAt}: old '{old[firstAt].Name}', now '{now[firstAt].Name}')" : ""));

        // The proof of what did NOT change. (1) The sort key at every position is the old one to the bit: the list still
        // runs worst first through exactly the same |RoCoF| values, so a position can differ only inside a run of exactly
        // equal keys. (2) Every result is the old one, every field: the same multiset of records (each computed by the
        // same code from the same contingency). Only the order inside a run of exact ties moved, from whatever the
        // introsort left to the input (model) order, which is the D-DOC-119 fix. Measured at DOC-16 (normal mode): order
        // moved inside ties on 19 of the 34 bundled cases (the MATPOWER cases whose units all carry H = 0, so every loss
        // has the same floored RoCoF, and the GB / intl cases with identical units); see the DOC-16 notes for the list.
        for (int i = 0; i < now.Count; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Abs(old[i].InitialRocofHzPerS)), BitConverter.DoubleToInt64Bits(Math.Abs(now[i].InitialRocofHzPerS)));
        var remaining = new List<RocofScreeningResult>(old);
        foreach (RocofScreeningResult r in now)
            Assert.True(remaining.Remove(r), $"{name}: result '{r.Name}' is not one of the old results");
        Assert.Empty(remaining);
    }
}
