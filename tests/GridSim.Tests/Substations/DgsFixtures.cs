using System.Text;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 R4-11 part C: SYNTHETIC DGS fixtures. Every text below was written by hand for these tests to the dialect that
/// IO/Dgs/DgsTableReader.cs documents (built from public descriptions; no real PowerFactory export was used or is claimed:
/// acceptance on real files is AWAITING FILES). Each carries a General row saying so.
/// </summary>
internal static class DgsFixtures
{
    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    public const string Header = "$$General;ID(a:40);Descr(a:40);Val(a:40)\ng1;Version;5.0\ng2;Origin;SYNTHETIC hand-written test fixture (not a PowerFactory export)\n";

    /// <summary>Two 110 kV terminals, one line (type TL), an external grid (slack, 1.02 pu) on T1 and a load (40 MW, 10 MVAr) on T2.</summary>
    public static string TwoBus(string rline = "0.1", string xline = "0.4", string bline = "2.5") => Header + Join(
        "$$ElmTerm;ID(a:40);loc_name(a:40);uknom(r:20)",
        "T1;Bus one;110",
        "T2;Bus two;110",
        "$$TypLne;ID(a:40);loc_name(a:40);uline(r:20);sline(r:20);rline(r:20);xline(r:20);bline(r:20)",
        $"TL;Line type;110;0.5;{rline};{xline};{bline}",
        "$$ElmLne;ID(a:40);loc_name(a:40);typ_id(p:40);dline(r:20);outserv(i:2)",
        "L1;Line one;TL;50;0",
        "$$ElmXnet;ID(a:40);loc_name(a:40);bustp(a:2);usetp(r:20)",
        "X1;Grid;SL;1.02",
        "$$ElmLod;ID(a:40);loc_name(a:40);plini(r:20);qlini(r:20);scale0(r:20)",
        "LD1;Load;40;10;1",
        "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)",
        "C1;T1;X1;0",
        "C2;T1;L1;0",
        "C3;T2;L1;1",
        "C4;T2;LD1;0");

    /// <summary>
    /// A 132 kV substation S1 with busbars T_B1 and T_B2 and a coupler CP (on_off given), a slack grid X1 on T_B1 and a machine G2
    /// (30 MW) on T_B2, each behind a cubicle switch; lines L1 (from T_B1) and L2 (from T_B2) to the remote terminal T_R (not in
    /// the substation) with an 80 MW load. Lines are lossless (X = 0.4 ohm/km x 50 km).
    /// </summary>
    public static string TwoFeed(int coupler = 1, int sw1 = 1, int sw2 = 1) => Header + Join(
        "$$ElmSubstat;ID(a:40);loc_name(a:40)",
        "S1;Substation one",
        "$$ElmTerm;ID(a:40);loc_name(a:40);uknom(r:20);fold_id(p:40)",
        "T_B1;Busbar 1;132;S1",
        "T_B2;Busbar 2;132;S1",
        "T_R;Remote;132;",
        "$$TypLne;ID(a:40);loc_name(a:40);rline(r:20);xline(r:20)",
        "TL;Line type;0;0.4",
        "$$ElmLne;ID(a:40);loc_name(a:40);typ_id(p:40);dline(r:20)",
        "L1;Line one;TL;50",
        "L2;Line two;TL;50",
        "$$ElmXnet;ID(a:40);loc_name(a:40);bustp(a:2);usetp(r:20)",
        "X1;Grid;SL;1.0",
        "$$ElmSym;ID(a:40);loc_name(a:40);pgini(r:20);qgini(r:20);usetp(r:20);ip_ctrl(i:2)",
        "G2;Machine two;30;0;1.0;0",
        "$$ElmLod;ID(a:40);loc_name(a:40);plini(r:20);qlini(r:20)",
        "LD;Load;80;0",
        "$$ElmCoup;ID(a:40);loc_name(a:40);on_off(i:2);aUsage(a:3)",
        $"CP;Coupler;{coupler};cbk",
        "$$StaSwitch;ID(a:40);fold_id(p:40);on_off(i:2);aUsage(a:3)",
        $"SW_X1;C_X1;{sw1};cbk",
        $"SW_G2;C_G2;{sw2};cbk",
        "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)",
        "C_X1;T_B1;X1;0",
        "C_G2;T_B2;G2;0",
        "C_L1a;T_B1;L1;0",
        "C_L1b;T_R;L1;1",
        "C_L2a;T_B2;L2;0",
        "C_L2b;T_R;L2;1",
        "C_LD;T_R;LD;0",
        "C_CPa;T_B1;CP;0",
        "C_CPb;T_B2;CP;1");

    /// <summary>The same file with its blocks and each block's rows in reverse order (the General block last).</summary>
    public static string Reversed(string text)
    {
        var blocks = new List<List<string>>();
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("$$", StringComparison.Ordinal)) blocks.Add(new List<string> { line });
            else blocks[^1].Add(line);
        }
        var sb = new StringBuilder();
        foreach (List<string> b in Enumerable.Reverse(blocks))
        {
            sb.Append(b[0]).Append('\n');
            for (int i = b.Count - 1; i >= 1; i--) sb.Append(b[i]).Append('\n');
        }
        return sb.ToString();
    }
}
