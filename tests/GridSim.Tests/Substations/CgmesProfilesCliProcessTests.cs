using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model.Substations;
using Xunit;
using static GridSim.Tests.Substations.SubstationFixtures;

namespace GridSim.Tests.Substations;

/// <summary>RC4 R4-11 part B: <c>cgmes-export</c>, <c>cgmes-conform</c> and <c>cgmes-study --archive</c> run as the built CLI.</summary>
public sealed class CgmesProfilesCliProcessTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-r411b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Site(string d)
    {
        string site = Path.Combine(d, "site.json");
        File.WriteAllText(site, SubstationJson.Write(CgmesProfilesTests.Rich()));
        return site;
    }

    [Fact]
    public void Export_writes_the_profile_documents_and_conform_passes_them_as_folder_and_as_zip()
    {
        string d = Dir();
        string site = Site(d), outDir = Path.Combine(d, "out"), result = Path.Combine(d, "r.json");
        CliProcess.Result e = CliProcess.RunBuilt("cgmes-export", site, "--dir", outDir, "--sv", "solve", "--json", result);
        Assert.True(e.ExitCode == 0, e.ToString());
        string[] names = Directory.GetFiles(outDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
        Assert.Equal(new[] { "rich_EQ.xml", "rich_SSH.xml", "rich_SV.xml", "rich_TP.xml" }, names);
        Assert.Contains("omitted [DL]", e.StdOut, StringComparison.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(result)))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal("2.4.15", root.GetProperty("summary").GetProperty("cgmes_version").GetString());
            Assert.True(root.GetProperty("tables").GetProperty("omissions").GetArrayLength() >= 5);
        }

        CliProcess.Result c = CliProcess.RunBuilt("cgmes-conform", outDir);
        Assert.True(c.ExitCode == 0, c.ToString());
        Assert.Contains("S01 NOTRUN", c.StdOut, StringComparison.Ordinal);

        string zip = Path.Combine(d, "bundle.zip");
        ZipFile.CreateFromDirectory(outDir, zip);
        Assert.Equal(0, CliProcess.RunBuilt("cgmes-conform", zip, "--json", Path.Combine(d, "c.json")).ExitCode);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "c.json"))))
            Assert.Equal(13, doc.RootElement.GetProperty("tables").GetProperty("checks").GetArrayLength());

        // Never overwrites: a second export keeps the first files byte for byte and writes numbered copies.
        string before = File.ReadAllText(Path.Combine(outDir, "rich_EQ.xml"));
        Assert.Equal(0, CliProcess.RunBuilt("cgmes-export", site, "--dir", outDir).ExitCode);
        Assert.Equal(before, File.ReadAllText(Path.Combine(outDir, "rich_EQ.xml")));
        Assert.True(File.Exists(Path.Combine(outDir, "rich_EQ-dup1.xml")));
    }

    [Fact]
    public void Export_in_3_0_passes_conform_and_the_failures_use_the_documented_exit_codes()
    {
        string d = Dir();
        string site = Site(d), outDir = Path.Combine(d, "o3");
        Assert.Equal(0, CliProcess.RunBuilt("cgmes-export", site, "--dir", outDir, "--cgmes-version", "3.0").ExitCode);
        CliProcess.Result c = CliProcess.RunBuilt("cgmes-conform", outDir);
        Assert.Contains("CGMES 3.0", c.StdOut, StringComparison.Ordinal);
        Assert.Equal(0, c.ExitCode);

        // A defect fails the verdict: exit 3.
        string eq = Path.Combine(outDir, "rich_EQ.xml");
        File.WriteAllText(eq, File.ReadAllText(eq).Replace("ACLineSegment.r>", "ACLineSegment.rr>"));
        Assert.Equal(3, CliProcess.RunBuilt("cgmes-conform", outDir).ExitCode);

        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", site, "--dir", Path.Combine(d, "x"), "--cgmes-version", "4").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", site).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-export", site, "--dir", Path.Combine(d, "x"), "--sv", "maybe").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("cgmes-export", Path.Combine(d, "none.json"), "--dir", Path.Combine(d, "x")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("cgmes-export", site, "--dir", Path.Combine(d, "x"), "--unspecified", "refuse").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("cgmes-conform", Path.Combine(d, "none.zip")).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("cgmes-conform").ExitCode);
    }

    [Fact]
    public void Conform_refuses_a_zip_with_path_traversal_as_an_input_error()
    {
        string d = Dir();
        string zip = Path.Combine(d, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("../x_EQ.xml").Open())) w.Write("<a/>");
        CliProcess.Result r = CliProcess.RunBuilt("cgmes-conform", zip);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("path traversal", r.StdErr, StringComparison.Ordinal);
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void Study_from_an_archive_gives_the_same_result_as_from_files_and_refuses_a_missing_entry()
    {
        string d = Dir();
        string[] files = ["p3_EQ.xml", "p3_TP.xml", "p3_SSH.xml", "p3_SV.xml"], types = ["EQ", "TP", "SSH", "SV"];
        var manifest = new StringBuilder();
        var profiles = new StringBuilder();
        string zip = Path.Combine(d, "p3.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            for (int i = files.Length - 1; i >= 0; i--)   // written in reverse: the reader sorts
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(CgmesStudyJobTests.FixtureDir, files[i]));
                using (Stream s = z.CreateEntry(files[i]).Open()) s.Write(bytes);
                File.WriteAllBytes(Path.Combine(d, files[i]), bytes);
            }
        for (int i = 0; i < files.Length; i++)
        {
            string sha = Sha(File.ReadAllBytes(Path.Combine(d, files[i])));
            manifest.Append(types[i]).Append("\t2.4.15\t").Append(files[i]).Append('\t').Append(sha).Append('\n');
            if (i > 0) profiles.Append(',');
            profiles.Append($$"""{"profile":"{{types[i]}}","version":"2.4.15","path":"{{files[i]}}","sha256":"{{sha}}"}""");
        }
        string study = Path.Combine(d, "study.json");
        File.WriteAllText(study, "{\"schemaVersion\":1,\"bundle\":{\"sha256\":\"" + Sha(Encoding.UTF8.GetBytes(manifest.ToString())) + "\",\"profiles\":[" + profiles + "]},\"voltage\":{\"minPu\":0.95,\"maxPu\":1.05}}");

        CliProcess.Result fromFiles = CliProcess.RunBuilt("cgmes-study", study);
        CliProcess.Result fromZip = CliProcess.RunBuilt("cgmes-study", study, "--archive", zip);
        Assert.True(fromFiles.ExitCode == 0, fromFiles.ToString());
        Assert.Equal(0, fromZip.ExitCode);
        Assert.Equal(fromFiles.StdOut, fromZip.StdOut);

        string wrong = Path.Combine(d, "wrong.zip");
        using (var z = ZipFile.Open(wrong, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("p3_EQ.xml").Open())) w.Write(File.ReadAllText(Path.Combine(d, "p3_EQ.xml")));
        CliProcess.Result missing = CliProcess.RunBuilt("cgmes-study", study, "--archive", wrong);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("is not an entry of the archive", missing.StdErr, StringComparison.Ordinal);
    }
}
