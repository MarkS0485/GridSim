using System.Security.Cryptography;
using System.Text;
using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.134 (B31, security): CSV formula injection (CWE-1236). A STRING cell that starts with =, +, -, @, a tab or a
/// carriage return is written with a leading apostrophe; numeric cells are written by their type and never change.
/// </summary>
public sealed class CsvFormulaInjectionTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("csvinj");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@HYPERLINK(\"x\")", "'@HYPERLINK(\"x\")")]
    [InlineData("\tTAB", "'\tTAB")]
    [InlineData("\rCR", "'\rCR")]
    [InlineData("Bus 1", "Bus 1")]
    [InlineData("1-2", "1-2")]
    [InlineData("", "")]
    public void The_guard_prefixes_exactly_the_trigger_characters(string text, string expected) =>
        Assert.Equal(expected, CsvFormulaGuard.Neutralise(text));

    // The two-bus case of Rc5Wp26RasTests: losing branch 1-2 overloads 1-2#2, so every scheme below operates at level 1.
    private const string TwoBusM = """
        function mpc = two
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        	1	2	0	0	0	0	1	1	0	132	1	1.1	0.9;
        	2	3	200	0	0	0	1	1	0	132	1	1.1	0.9;
        ];
        mpc.gen = [
        	1	150	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        	1	50	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        	2	0	0	999	-999	1	100	1	999	0	0	0	0	0	0	0	0	0	0	0	0;
        ];
        mpc.branch = [
        	1	2	0	0.2	0	120	120	120	0	0	1	-360	360;
        	1	2	0	0.2	0	120	120	120	0	0	1	-360	360;
        ];
        """;

    [Fact]
    public void A_user_name_starting_with_each_trigger_character_is_neutralised_in_the_csv()
    {
        // One scheme per trigger character; the scheme name is user text that the ras_actions table writes as a string.
        string[] names = { "=cmd", "+one", "-two", "@three", "\tfour", "\rfive" };
        string schemes = string.Join(",", names.Select(n =>
            "{ \"name\": " + System.Text.Json.JsonSerializer.Serialize(n) + ", \"arming\": [ { \"kind\": \"always\" } ], " +
            "\"triggers\": [ { \"kind\": \"loadingAbove\", \"branch\": \"1-2#2\", \"percent\": 100 } ], " +
            "\"actions\": [ { \"kind\": \"setGeneratorP\", \"unit\": \"G1\", \"mw\": 100 } ] }"));
        File.WriteAllText(Path.Combine(_dir, "two.m"), TwoBusM);
        File.WriteAllText(Path.Combine(_dir, "r.json"), "{ \"schema\": \"gridsim-ras/1\", \"schemes\": [" + schemes + "] }");
        CliProcess.Result r = JournalTestSupport.Run(_dir, "nminus1", "two.m", "--ras", "r.json", "--csv", "o.csv");
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        string csv = File.ReadAllText(Path.Combine(_dir, "o-ras_actions.csv"));
        foreach (string n in names)
        {
            // The apostrophe, then the usual quoting (a carriage return forces quotes).
            string cell = n.Contains('\r') ? "\"'" + n + "\"" : "'" + n;
            Assert.Contains("," + cell + ",", csv, StringComparison.Ordinal);
        }
        // No cell starts with a trigger character any more (outside the quotes of the CR name).
        foreach (string line in csv.Split('\n'))
            foreach (string field in line.TrimEnd('\r').Split(','))
                Assert.False(field.Length > 0 && "=+@".Contains(field[0], StringComparison.Ordinal), field);
    }

    // Pinned from the 2.1.134 build BEFORE the guard was added (the same CLI with CsvField as it was): "pf case14 --csv
    // p.csv" and "nminus1 case14 --csv n.csv" in a fresh folder, each file's SHA-256 with line ends as LF. p.csv holds
    // fourteen negative numbers (angles and injections): they are written by type and are byte for byte unchanged.
    [Theory]
    [InlineData("pf", "p.csv", "p.csv", "e714008c036a7336569b81f9152573b1c1203c87d750166627ed67c2c58dcc09")]
    [InlineData("pf", "p.csv", "p-summary.csv", "870f89dd9989427846b36177dfd316151a4c7437b2d4d026f7e944f767d4189a")]
    [InlineData("nminus1", "n.csv", "n.csv", "437dfa4917607b73bfc5563a411888d99fa7f1385be7a8eb9eef78244c3f2dbe")]
    [InlineData("nminus1", "n.csv", "n-summary.csv", "b1c1249b9f038de4159c9785ef3a7a39b4534c3124050ec3e3b8b9f1b595024d")]
    [InlineData("nminus1", "n.csv", "n-excluded_single_outages.csv", "d4139af517637871c1948d6cbce50562775a51abce5e07fcb3cca788f2aaf0ea")]
    public void An_ordinary_case_csv_and_its_negative_numbers_are_byte_for_byte_unchanged(string verb, string csv, string file, string sha)
    {
        string w = Path.Combine(_dir, verb + "-" + file);
        Directory.CreateDirectory(w);
        CliProcess.Result r = JournalTestSupport.Run(w, verb, "case14", "--csv", csv);
        Assert.True(r.ExitCode is 0 or 3, r.ToString());
        string text = File.ReadAllText(Path.Combine(w, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (file == "p.csv") Assert.Equal(14, text.Split('\n').Count(l => l.Contains(",-", StringComparison.Ordinal)));
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }
}
