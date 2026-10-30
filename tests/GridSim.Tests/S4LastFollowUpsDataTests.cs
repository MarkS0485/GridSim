using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 last follow-ups (October 2026), data items: N170 (what passes for ireland-all-island), N171 (gb-synthetic's
/// "NSC 400 ring 1"), N169 (gb-full's duplicated branch names). Each test pins the EVIDENCE the notes and READMEs quote,
/// so a later data or physics change shows up here.
/// </summary>
public class S4LastFollowUpsDataTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N170: ireland-all-island converges and passes the physics-LAW gate, but sits outside the voltage band
    // =============================================================================================================

    [Fact]
    public void N170_ireland_converges_but_46_of_60_buses_are_above_the_1_06_pu_band()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "intl", "ireland-all-island"));
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        Assert.Equal(5, r.Iterations);
        Assert.Equal(60, r.Vm.Length);
        Assert.Equal(46, r.Vm.Count(v => v > 1.06));
        Assert.Equal(45, r.Vm.Count(v => v > 1.10));
        Assert.InRange(r.Vm.Max(), 1.12579, 1.12580);   // 1.1257904307214863 pu at bus 9
        Assert.InRange(r.Vm.Min(), 1.01993, 1.01994);   // 1.0199312362437716 pu at bus 27
        Assert.Equal(9, r.BusIds[Array.IndexOf(r.Vm, r.Vm.Max())]);
        // Only the slack holds a voltage: 50 of the 51 units are fixed injections.
        Assert.Equal(51, m.Generators.Count);
        Assert.Equal(0, m.Buses.Count(b => b.Type == BusType.Pv));
    }

    [Fact]
    public void N170_the_intl_readme_no_longer_calls_ireland_a_bare_physics_gate_pass()
    {
        string readme = File.ReadAllText(Path.Combine(Root(), "data", "intl", "README.md"));
        Assert.DoesNotContain("| converges, physics gate PASS |", readme, StringComparison.Ordinal);
        Assert.Contains("outside the voltage band", readme, StringComparison.Ordinal);
        Assert.Contains("46 of 60 buses above 1.06 pu", readme, StringComparison.Ordinal);
        Assert.Contains("It checks no operating limit.", readme, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // N171: a zone with two 400 kV nodes gets one circuit, now labelled a tie (it was "<tag> 400 ring 1")
    // =============================================================================================================

    /// <summary>How many buses are reachable from the first bus when branch <paramref name="skip"/> is left out.</summary>
    private static int ReachableWithout(GridModel m, int skip)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (Bus b in m.Buses) adj[b.Id] = new List<int>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            if (i == skip || !m.Branches[i].InService) continue;
            adj[m.Branches[i].From].Add(m.Branches[i].To);
            adj[m.Branches[i].To].Add(m.Branches[i].From);
        }
        var seen = new HashSet<int> { m.Buses[0].Id };
        var stack = new Stack<int>(seen);
        while (stack.Count > 0)
            foreach (int v in adj[stack.Pop()])
                if (seen.Add(v)) stack.Push(v);
        return seen.Count;
    }

    [Fact]
    public void N171_the_two_node_zones_have_a_400_tie_and_three_of_them_are_bridges()
    {
        GridModel m = GridSim.Core.Synthetic.SyntheticGb.Build(new());
        Assert.Equal(2227, m.BusCount);
        Assert.Equal(2268, m.Branches.Count);
        Assert.DoesNotContain(m.Branches, b => b.Name is "NSC 400 ring 1" or "SSC 400 ring 1" or "EAN 400 ring 1" or "SWW 400 ring 1");
        // Branch order unchanged: the NSC tie is still branch 0, between the zone's two 400 kV nodes (buses 1 and 2).
        Assert.Equal("NSC 400 tie", m.Branches[0].Name);
        Assert.Equal((1, 2), (m.Branches[0].From, m.Branches[0].To));
        int full = ReachableWithout(m, -1);
        Assert.Equal(m.BusCount, full);
        foreach ((string name, bool bridge) in new[] { ("NSC 400 tie", true), ("SSC 400 tie", false), ("EAN 400 tie", true), ("SWW 400 tie", true) })
        {
            int k = m.Branches.ToList().FindIndex(b => b.Name == name);
            Assert.True(k >= 0, name);
            Assert.True(m.Branches[k].RateAMva == 5500 && m.Branches[k].R == 0.0003 && m.Branches[k].X == 0.003 && m.Branches[k].B == 0.35, name);
            Assert.Equal(bridge, ReachableWithout(m, k) < full);
        }
        // The zones with three or more 400 kV nodes keep real rings, still named "ring".
        Assert.Contains(m.Branches, b => b.Name == "MER 400 ring 3");
        Assert.Contains(m.Branches, b => b.Name == "LDN 400 ring 6");
    }

    // =============================================================================================================
    // N169 / N190: gb-full's 58 duplicated branch names (all supergrid transformers from GDA's spine assembler), renamed
    // uniquely in the bundled data by N190
    // =============================================================================================================

    // Moved 2026-10 (N190, Ruling R1, Mark delegated 2026-10-04): the bundled gb-full data now carries unique names
    // (names only; every other field of every row is unchanged, literal text compared). Before: 58 duplicated names
    // over 133 rows (47 class-A names x 2 identical rows, 11 class-B names over 39 rows). After: 0 duplicated names;
    // the second row of each class-A pair is "<name>#2" (the GDA export's own suffix for a parallel unit) and every
    // class-B row is "<name>@<to bus id>" (as GDA's main coupling pass names its rows). The evidence of the two
    // classes is still pinned below on the new names, so the possible double count (README, [VERIFY]) stays visible.
    [Fact]
    public void N169_N190_gb_full_branch_names_are_unique_and_the_two_former_duplicate_classes_are_still_identifiable()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-full"));
        Assert.Equal(5918, m.Branches.Count);
        var groups = m.Branches.GroupBy(b => b.Name ?? "").Where(g => g.Count() > 1).ToList();
        Assert.Empty(groups);                                        // was 58 names over 133 rows

        var byName = m.Branches.ToDictionary(b => b.Name!, StringComparer.Ordinal);

        // Class A (47 pairs): "SGT <spine key> <licence>:<label> x<n>/<k>@<bus id>" and its twin "...#2", identical in
        // every field except the name (same ends, impedance, rating): two coupling rows of GDA's sgt_couplings (one per
        // NESO GSP) that share the spine site and the DNO label, each fanned out to the same buses.
        var classA = m.Branches.Where(b => b.Name!.StartsWith("SGT ", StringComparison.Ordinal)
                                           && !b.Name.StartsWith("SGT default ", StringComparison.Ordinal)
                                           && b.Name.EndsWith("#2", StringComparison.Ordinal)).ToList();
        Assert.Equal(47, classA.Count);
        Assert.All(classA, twin =>
        {
            Assert.True(twin.IsTransformer, twin.Name);
            string first = twin.Name![..^2];
            Assert.Contains("@", first, StringComparison.Ordinal);
            Assert.True(byName.ContainsKey(first), first);
            Assert.True(byName[first] with { Name = twin.Name } == twin, twin.Name);   // record equality bar the name
        });
        // The seven GSP groups (spine site keys) the pairs fall in: Barking 3, Hackney 5, Tynemouth 8, Upper Boat 8,
        // Uskmouth 8, Wimbledon 7, Willesden 8.
        var sites = classA.GroupBy(b => b.Name!.Split(' ')[1]).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["BARKC1"] = 3, ["HACK_1"] = 5, ["TYNE_1"] = 8, ["UPPB_1"] = 8, ["ALST_3"] = 8, ["WIMBN1"] = 7, ["ACTL_2"] = 8,
        }, sites);
        Assert.Equal(3840.0, classA.Sum(b => b.RateAMva), 9);       // the SGT rating the twins add, MVA (3839.9999999999986 summed)

        // Class B (39 rows, 11 former names): "SGT default <licence>:<gsp> x<n>@<to bus id>" from the leftover coupling
        // pass, each row to a DIFFERENT bus: distinct transformers whose old name omitted the bus.
        var classB = m.Branches.Where(b => b.Name!.StartsWith("SGT default ", StringComparison.Ordinal)
                                           && b.Name.EndsWith("@" + b.To.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
        Assert.Equal(39, classB.Count);
        var formerNames = classB.Select(b => b.Name![..b.Name!.LastIndexOf('@')]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(11, formerNames.Count);
        Assert.DoesNotContain(m.Branches, b => formerNames.Contains(b.Name!));   // no row keeps a former shared name

        // No OLTC and no boundary refers to a renamed row, so no lookup moved.
        var renamed = classA.Select(b => b.Name!).Concat(classB.Select(b => b.Name!)).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(m.Oltcs, o => renamed.Contains(o.Transformer));
        string boundaries = File.ReadAllText(Path.Combine(Root(), "data", "gb-full", "boundaries.json"));
        Assert.DoesNotContain(renamed, n => boundaries.Contains(n, StringComparison.Ordinal));
    }

    [Fact]
    public void N171_the_bundled_gb_synthetic_folder_carries_the_same_tie_names()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-synthetic"));
        foreach (string tag in new[] { "NSC", "SSC", "EAN", "SWW" })
        {
            Assert.Contains(m.Branches, b => b.Name == $"{tag} 400 tie");
            Assert.DoesNotContain(m.Branches, b => b.Name == $"{tag} 400 ring 1");
        }
    }
}
