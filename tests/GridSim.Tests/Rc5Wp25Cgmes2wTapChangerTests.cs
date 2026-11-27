using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.Journal;
using GridSim.Core.Model.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.131 (B30): the node-breaker CGMES 3.0 export of two-winding tap changers carries what CGMES 3.0 requires, as the
/// three-winding ends have since 2.1.130: <c>TapChanger.neutralU</c> (end 1's rated voltage, the changer sits on end 1),
/// <c>TapChanger.ltcFlag</c> false and, on a linear phase changer, <c>PhaseTapChangerLinear.xMin = xMax</c> = end 1's
/// reactance in ohms. The 2.4.15 documents are byte for byte what the 2.1.130 build wrote.
/// </summary>
public sealed class Rc5Wp25Cgmes2wTapChangerTests
{
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string All(CgmesExportResult e) => string.Concat(e.Files.Select(f => f.FileName + "\n" + f.Text + "\n--\n"));

    /// <summary>Three two-winding units on 400, 132 and 33 kV nodes: TA a ratio changer only (1.05), TB both (0.975 and
    /// 5 degrees), TC a phase changer only (-3 degrees).</summary>
    internal static SubstationModel TwoWinding() => new()
    {
        Name = "t2", BaseMva = 100,
        Substations = new[]
        {
            new Substation("S", null, new[] { new VoltageLevel("H", 400), new VoltageLevel("M", 132), new VoltageLevel("L", 33) },
                Array.Empty<SubstationBay>(), new[] { new ConnectivityNode("h", "H"), new ConnectivityNode("m", "M"), new ConnectivityNode("l", "L") },
                Array.Empty<NbSwitch>()),
        },
        Transformers = new[]
        {
            new NbTransformer("TA", "h", "m", 0.002, 0.1, 0, 1.05, 0, 0),
            new NbTransformer("TB", "h", "l", 0, 0.2, -0.001, 0.975, 5, 0),
            new NbTransformer("TC", "m", "l", 0, 0.15, 0, 1.0, -3, 0),
        },
        Generators = new[] { new NbGenerator("G", "h", 0, Slack: true), new NbGenerator("G2", "m", 5) },
        Loads = new[] { new NbLoad("D", "l", 10, 2), new NbLoad("D2", "h", 1) },
    };

    private static string EndX(string eq, string end)
        => Regex.Match(eq, "gridsim-id=" + Regex.Escape(end) + "</cim:IdentifiedObject.description>.*?<cim:PowerTransformerEnd.x>([^<]+)<", RegexOptions.Singleline).Groups[1].Value;

    private static string Changer(string eq, string cls, string id)
        => Regex.Match(eq, "<cim:" + cls + " [^>]*>\\s*<cim:IdentifiedObject.name>[^<]*</cim:IdentifiedObject.name>\\s*<cim:IdentifiedObject.description>gridsim-id=" + Regex.Escape(id) + "</cim:IdentifiedObject.description>(.*?)</cim:" + cls + ">", RegexOptions.Singleline).Value;

    [Fact]
    public void The_2_4_15_documents_are_unchanged()
    {
        // Every file, pinned from the 2.1.130 Release Core dll (dev main 0ba32dc4) before this change, by a scratch F#
        // probe on the same model. The 3.0 documents of that build hashed c87cf1b1...; they change by the added fields only.
        CgmesExportResult e24 = CgmesSubstationExporter.Export(TwoWinding(), new Dictionary<string, bool>(), new CgmesExportOptions(CgmesVersion.V2_4_15));
        Assert.Equal("39aaad5bb3549b12b79d4baaefa76730ab5b24c0cddfc60e3b96f8463f752669", Sha(All(e24)));
        Assert.DoesNotContain("TapChanger.neutralU", All(e24), StringComparison.Ordinal);
        Assert.DoesNotContain("TapChanger.ltcFlag", All(e24), StringComparison.Ordinal);
        Assert.DoesNotContain("PhaseTapChangerLinear.xMin", All(e24), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_winding_tap_changers_carry_what_3_0_requires()
    {
        CgmesExportResult e30 = CgmesSubstationExporter.Export(TwoWinding(), new Dictionary<string, bool>(), new CgmesExportOptions(CgmesVersion.V3_0));
        string eq = e30.Files.Single(f => f.Kind == CgmesProfileKind.EQ).Text;
        Assert.Equal(2, Regex.Count(eq, "<cim:RatioTapChanger "));
        Assert.Equal(2, Regex.Count(eq, "<cim:PhaseTapChangerLinear "));

        // Each changer is on end 1, so neutralU is end 1's rated voltage (TA, TB on 400 kV, TC on 132 kV).
        string ta = Changer(eq, "RatioTapChanger", "TA"), tbR = Changer(eq, "RatioTapChanger", "TB");
        string tbP = Changer(eq, "PhaseTapChangerLinear", "TB"), tc = Changer(eq, "PhaseTapChangerLinear", "TC");
        foreach (string c in new[] { ta, tbR, tbP, tc })
        {
            Assert.NotEmpty(c);
            Assert.Contains("<cim:TapChanger.ltcFlag>false</cim:TapChanger.ltcFlag>", c, StringComparison.Ordinal);
        }
        Assert.Contains("<cim:TapChanger.neutralU>400</cim:TapChanger.neutralU>", ta, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.neutralU>400</cim:TapChanger.neutralU>", tbR, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.neutralU>400</cim:TapChanger.neutralU>", tbP, StringComparison.Ordinal);
        Assert.Contains("<cim:TapChanger.neutralU>132</cim:TapChanger.neutralU>", tc, StringComparison.Ordinal);
        Assert.DoesNotContain("xMin", ta, StringComparison.Ordinal);
        Assert.DoesNotContain("xMin", tbR, StringComparison.Ordinal);

        // xMin = xMax = end 1's own reactance in ohms, the value its end already carries: a flat curve. TB: 0.2 pu on
        // 400 kV, 100 MVA is 320 ohms; TC: 0.15 pu on 132 kV is 26.136 ohms.
        Assert.Equal("320", EndX(eq, "TB.1"));
        foreach ((string c, string end) in new[] { (tbP, "TB.1"), (tc, "TC.1") })
        {
            string x = EndX(eq, end);
            Assert.NotEmpty(x);
            Assert.Contains($"<cim:PhaseTapChangerLinear.xMin>{x}</cim:PhaseTapChangerLinear.xMin>", c, StringComparison.Ordinal);
            Assert.Contains($"<cim:PhaseTapChangerLinear.xMax>{x}</cim:PhaseTapChangerLinear.xMax>", c, StringComparison.Ordinal);
        }

        // Every value written before is unchanged: removing the added lines gives the 2.1.130 3.0 documents.
        string stripped = Regex.Replace(All(e30), "[ \\t]*<cim:(TapChanger\\.neutralU|TapChanger\\.ltcFlag|PhaseTapChangerLinear\\.xMin|PhaseTapChangerLinear\\.xMax)>[^<]*</cim:[^>]+>\\r?\\n", "");
        Assert.Equal("c87cf1b1702bbf247fcb4ea5de23095657a2960839c59dfc7061efea6c9a85b2", Sha(stripped));

        // The reader takes the units back with their ratios and shifts (neutralU, ltcFlag, xMin and xMax are not read as
        // anything else).
        var back = CimImporter.Parse(e30.Files.Select(f => f.Text)).Transformers;
        Assert.Equal(3, back.Count);
        // The model's ratios and shifts (TA 1.05 / 0, TB 0.975 / 5, TC 1 / -3): the exporter's exact-value search writes the
        // step increment so the reader's 1 + 1 x du / 100 returns the ratio bit for bit, and the shift is written as is.
        foreach ((string id, double ratio, double shift) in new[] { ("TA", 1.05, 0.0), ("TB", 0.975, 5.0), ("TC", 1.0, -3.0) })
        {
            CimTransformer t = Assert.Single(back, x => x.Name == id);
            Assert.Equal(ratio, t.TapRatio);
            Assert.Equal(shift, t.PhaseShiftDeg);
        }
    }

    [Fact]
    public void The_change_is_in_the_breaking_change_map_as_b30()
    {
        BreakingChange b = BreakingChangeMap.Default.Entries.Single(e => e.Id == "B30");
        Assert.Equal("2.1.131", b.Version);
        Assert.Equal("2.1.131", b.Changelog);
        BreakingChangeClaim c = Assert.Single(b.Claims);
        Assert.Equal("qualitative", c.Kind);
        Assert.Equal("(EQ document)", c.Field);
        Assert.Equal(new[] { "cgmes-export" }, b.Verbs);
        Assert.Contains("B30", BreakingChangeMap.Default.Explain("2.1.130", "2.1.131", "cgmes-export", ["cgmes-export", "s.json", "--cgmes-version", "3.0"], []));
    }
}
