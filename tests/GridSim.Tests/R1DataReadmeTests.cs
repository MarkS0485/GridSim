using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synthetic;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Rulings R1 (Mark delegated 2026-10-04), data READMEs: N147 / N148 / N190 (gb-full) and N191 / N192 (gb-synthetic).
/// Each test pins the figures the README quotes against the data, so a data change shows up here.
/// </summary>
public sealed class R1DataReadmeTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N147 / N148 / N190 (gb-full) and N191 / N192 (gb-synthetic): the README records match the data
    // =============================================================================================================

    [Fact]
    public void N147_N148_gb_full_open_line_ends_and_zero_resistance_sgts_are_as_the_readme_says()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-full"));
        var degree = new Dictionary<int, int>();
        foreach (Branch b in m.Branches.Where(b => b.InService))
        {
            degree[b.From] = degree.GetValueOrDefault(b.From) + 1;
            degree[b.To] = degree.GetValueOrDefault(b.To) + 1;
        }
        var genBuses = m.Generators.Select(g => g.Bus).ToHashSet();
        var openEnds = m.Buses.Where(b => degree.GetValueOrDefault(b.Id) == 1 && b.Pd == 0 && b.Qd == 0 && b.Gs == 0 && b.Bs == 0
                                          && !genBuses.Contains(b.Id) && b.BaseKv >= 66).ToList();
        Assert.Equal(142, openEnds.Count);
        Assert.Equal(97, openEnds.Count(b => b.BaseKv == 132));
        Assert.Equal(45, openEnds.Count(b => b.BaseKv == 66));
        Assert.Contains(openEnds, b => b.Id == 4992 && b.Name == "SILSDEN T2");

        var sgts = m.Branches.Where(b => b.IsTransformer && b.Name!.StartsWith("SGT", StringComparison.Ordinal)).ToList();
        Assert.Equal(1207, sgts.Count);
        Assert.All(sgts, b => Assert.Equal(0.0, b.R));
        Assert.Equal(22, m.Branches.Count(b => b.IsTransformer && b.R == 0.0 && !b.Name!.StartsWith("SGT", StringComparison.Ordinal)));
        Assert.Equal(248, sgts.Count(b => b.X == 0.2222222222222222));

        string readme = File.ReadAllText(Path.Combine(Root(), "data", "gb-full", "README.md"));
        foreach (string s in new[] { "142 buses at 66 kV and above (97 at 132 kV, 45 at 66 kV)", "All 1,207 synthesised supergrid transformers have r = 0",
                                     "[VERIFY: NESO GSP GIS table.]", "**3,840**", "emit unique names at source" })
            Assert.Contains(s, readme, StringComparison.Ordinal);
    }

    [Fact]
    public void N191_N192_gb_synthetic_readme_records_the_design_properties_and_the_folder_differences()
    {
        // Moved 2026-10 (N203 / N204, Ruling R1 follow-up, Mark delegated 2026-10-04): the builder now marks the three
        // line-commutated links and data/gb-synthetic was regenerated from it, so the README no longer records a
        // folder-vs-builder difference. Before -> after: README strings "The folder is NOT regenerated" and
        // "`\"converterFaultCurrentPu\": 0.0`" -> "This folder is the builder's output" and
        // "`\"converterFaultCurrentPu\": 0`"; the builder's Moyle / IFA / BritNed ConverterFaultCurrentPu null -> 0.0
        // (the folder's 0.0 unchanged); the folder's corridor names with an en dash 14 -> 0, the 14 hyphen names now in
        // both. The design-property strings (N191) are unchanged.
        string readme = File.ReadAllText(Path.Combine(Root(), "data", "gb-synthetic", "README.md"));
        foreach (string s in new[] { "Scotland is radial", "`NSC 400 tie`, `EAN 400 tie` and `SWW 400 tie` are bridges",
                                     "2,169 of the 2,268 branches are bridges", "This folder is the builder's output",
                                     "`\"converterFaultCurrentPu\": 0`" })
            Assert.Contains(s, readme, StringComparison.Ordinal);

        // The three line-commutated markings, now in the folder and in the builder alike.
        GridModel folder = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-synthetic"));
        GridModel built = SyntheticGb.Build();
        foreach (string name in new[] { "Moyle", "IFA", "BritNed" })
        {
            Assert.Equal(0.0, folder.Generators.Single(g => g.Name == name).ConverterFaultCurrentPu);
            Assert.Equal(0.0, built.Generators.Single(g => g.Name == name).ConverterFaultCurrentPu);
        }
        // The 14 corridor names are the builder's hyphen names in both; no en dash is left in the folder.
        Assert.DoesNotContain(folder.Branches, b => b.Name!.Contains('\u2013'));
        var corridors = built.Branches.Where(b => Regex.IsMatch(b.Name!, @"^[A-Z]{3}-[A-Z]{3} 400$")).Select(b => b.Name!).ToList();
        Assert.Equal(14, corridors.Count);
        Assert.All(corridors, n => Assert.Contains(folder.Branches, b => b.Name == n));
    }
}
