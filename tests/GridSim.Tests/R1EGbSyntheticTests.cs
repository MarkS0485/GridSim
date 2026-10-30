using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Synthetic;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N203 / N204 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): the gb-synthetic builder marks the
/// line-commutated links and sizes its radial ladder to its peak demand, and data/gb-synthetic was regenerated from it,
/// so the built-in name and the folder are one network.
/// </summary>
public sealed class R1EGbSyntheticTests
{
    private static GridModel Folder() => JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-synthetic"));

    private static void SameBits(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void N203_moyle_ifa_and_britned_are_line_commutated_and_the_other_links_are_not()
    {
        GridModel built = SyntheticGb.Build();
        var lcc = new[] { "Moyle", "IFA", "BritNed" };
        foreach (Generator g in built.Generators.Where(g => g.Type == "interconnector"))
        {
            if (lcc.Contains(g.Name)) Assert.Equal(0.0, g.ConverterFaultCurrentPu);
            else Assert.Null(g.ConverterFaultCurrentPu);
        }
        Assert.Equal(10, built.Generators.Count(g => g.Type == "interconnector"));
        // The folder carries the same three markings (written by `gridsim synth` since N203, no longer by hand).
        foreach (string name in lcc) Assert.Equal(0.0, Folder().Generators.Single(g => g.Name == name).ConverterFaultCurrentPu);
    }

    [Fact]
    public void The_folder_is_the_builders_output_field_for_field()
    {
        // The data as written: the loader's default dispatch balancing (Dispatcher.Balance) is turned off here, since
        // it re-dispatches the units on load (the slack unit's 5,400 MW becomes 5,105.27... MW) for the folder and
        // would do the same for the built model; the balanced pair is compared at the end.
        GridModel built = SyntheticGb.Build();
        GridModel folder = JsonGridLoader.LoadDirectory(Path.Combine(CliProcess.Root(), "data", "gb-synthetic"), dispatch: false);
        Assert.Equal(built.Name, folder.Name);
        SameBits(built.BaseMva, folder.BaseMva, "BaseMva");

        Assert.Equal(built.Buses.Count, folder.Buses.Count);
        var fb = folder.Buses.ToDictionary(b => b.Id);
        foreach (Bus b in built.Buses)
        {
            Bus f = fb[b.Id];
            Assert.Equal(b.Name, f.Name);
            Assert.Equal(b.Type, f.Type);
            SameBits(b.Pd, f.Pd, $"bus {b.Id} Pd");
            SameBits(b.Qd, f.Qd, $"bus {b.Id} Qd");
            SameBits(b.Gs, f.Gs, $"bus {b.Id} Gs");
            SameBits(b.Bs, f.Bs, $"bus {b.Id} Bs");
            SameBits(b.BaseKv, f.BaseKv, $"bus {b.Id} BaseKv");
        }

        Assert.Equal(built.Generators.Count, folder.Generators.Count);
        for (int i = 0; i < built.Generators.Count; i++)
        {
            Generator b = built.Generators[i], f = folder.Generators[i];
            Assert.Equal(b.Name, f.Name);
            Assert.Equal(b.Bus, f.Bus);
            Assert.Equal(b.Type, f.Type);
            SameBits(b.Pg, f.Pg, $"{b.Name} Pg");
            SameBits(b.Vg, f.Vg, $"{b.Name} Vg");
            SameBits(b.Qmin, f.Qmin, $"{b.Name} Qmin");
            SameBits(b.Qmax, f.Qmax, $"{b.Name} Qmax");
            SameBits(b.Pmax, f.Pmax, $"{b.Name} Pmax");
            SameBits(b.MBase, f.MBase, $"{b.Name} MBase");
            SameBits(b.H, f.H, $"{b.Name} H");
            Assert.Equal(b.ConverterFaultCurrentPu, f.ConverterFaultCurrentPu);
        }

        // Branches: the loader reads the lines before the transformers, so they are matched by name (unique here).
        Assert.Equal(built.Branches.Count, folder.Branches.Count);
        var fbr = folder.Branches.ToDictionary(b => b.Name!);
        foreach (Branch b in built.Branches)
        {
            Branch f = fbr[b.Name!];
            Assert.Equal(b.From, f.From);
            Assert.Equal(b.To, f.To);
            Assert.Equal(b.IsTransformer, f.IsTransformer);
            SameBits(b.R, f.R, $"{b.Name} R");
            SameBits(b.X, f.X, $"{b.Name} X");
            SameBits(b.B, f.B, $"{b.Name} B");
            SameBits(b.TapRatio, f.TapRatio, $"{b.Name} tap");
            SameBits(b.RateAMva, f.RateAMva, $"{b.Name} rating");
        }
        Assert.Equal(built.Oltcs.Count, folder.Oltcs.Count);
        // Balanced on load, the folder and the balanced builder dispatch every unit to the same bits.
        GridModel balancedFolder = Folder(), balancedBuilt = GridSim.Core.Dispatch.Dispatcher.Balance(built);
        for (int i = 0; i < balancedBuilt.Generators.Count; i++)
            SameBits(balancedBuilt.Generators[i].Pg, balancedFolder.Generators[i].Pg, $"{balancedBuilt.Generators[i].Name} balanced Pg");
        // N192: no corridor keeps the en dash of the old copy.
        Assert.DoesNotContain(folder.Branches, b => b.Name!.Contains('\u2013'));
    }

    // The radial ladder's standard unit per layer (the rating every branch had before N204).
    private static readonly (Regex Pattern, double UnitMva, string Layer)[] Layers =
    {
        (new Regex(@"^[A-Z]{3} GSP \d+ SGT$"), 750, "GSP SGT"),
        (new Regex(@"^[A-Z]{3} GSP \d+ industrial spur$"), 240, "industrial spur"),
        (new Regex(@"^[A-Z]{3} G\d+P\d+ 132/33$"), 180, "132/33"),
        (new Regex(@"^[A-Z]{3} G\d+P\d+S\d+ 33/11$"), 30, "33/11"),
        (new Regex(@"^[A-Z]{3} G\d+P\d+S\d+ 11/6\.6$"), 12, "11/6.6"),
    };

    [Fact]
    public void N204_every_radial_branch_carries_its_peak_demand_with_the_fewest_standard_units()
    {
        GridModel m = SyntheticGb.Build();
        var children = new Dictionary<int, List<int>>();
        var radial = new List<(Branch B, double Unit, string Layer)>();
        foreach (Branch b in m.Branches)
            foreach ((Regex rx, double unit, string layer) in Layers)
                if (rx.IsMatch(b.Name!))
                {
                    radial.Add((b, unit, layer));
                    if (!children.TryGetValue(b.From, out List<int>? l)) children[b.From] = l = new List<int>();
                    l.Add(b.To);
                }
        Assert.Equal(2164, radial.Count);
        var demand = m.Buses.ToDictionary(b => b.Id, b => (b.Pd, b.Qd));
        var doubled = new Dictionary<string, int>();
        foreach ((Branch b, double unit, string layer) in radial)
        {
            double p = 0, q = 0;
            var stack = new Stack<int>(new[] { b.To });
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                p += demand[id].Pd; q += demand[id].Qd;
                if (children.TryGetValue(id, out List<int>? kids)) foreach (int c in kids) stack.Push(c);
            }
            double s = Math.Sqrt(p * p + q * q);
            int units = (int)Math.Round(b.RateAMva / unit);
            Assert.Equal(unit * units, b.RateAMva);
            Assert.True(s <= b.RateAMva, $"{b.Name}: peak {s:R} MVA over its {b.RateAMva} MVA");            // enough units
            Assert.True(units == 1 || s > unit * (units - 1), $"{b.Name}: {units} units for {s:R} MVA");  // and no more
            if (units > 1) doubled[layer] = doubled.GetValueOrDefault(layer) + 1;
            Assert.True(units <= 2, $"{b.Name}: {units} units");
        }
        // Before N204 every count was 0 (every branch one unit). 84 in all.
        Assert.Equal(65, doubled["33/11"]);
        Assert.Equal(8, doubled["11/6.6"]);
        Assert.Equal(6, doubled["132/33"]);
        Assert.Equal(4, doubled["GSP SGT"]);
        Assert.Equal(1, doubled["industrial spur"]);

        // A resized branch is two parallel units exactly as the loader builds circuits: 2 (R / 2, X / 2, B x 2).
        Branch sec = m.Branches.Single(b => b.Name == "SSC G1P1S3 33/11");
        SameBits(0.01 / 2, sec.R, "R");
        SameBits(0.22 / 2, sec.X, "X");
        Assert.Equal(60.0, sec.RateAMva);
        // The meshed supergrid transformers are not part of the rule.
        Assert.All(m.Branches.Where(b => b.Name!.Contains("SGT 400/275", StringComparison.Ordinal)), b => Assert.Equal(1000.0, b.RateAMva));
    }

    [Fact]
    public void N204_only_the_two_meshed_supergrid_transformers_are_left_that_no_dispatch_can_relieve()
    {
        // Before N204: 53 (51 radial branches whose own demand exceeded their rating, plus these two).
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(SyntheticGb.Build());
        Assert.True(r.Feasible);
        Assert.Equal(new[] { "MID SGT 400/275 1", "LDN SGT 400/275 1" }, r.UnrelievableLines.Select(u => u.Name));
    }
}
