using System.IO.Compression;
using System.Text.Json;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.Model.Substations;
using Xunit;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 wiring: <c>substation-state</c> reads a CGMES exchange (a .zip or a folder of EQ/TP/SSH/SV profile files) through Core's
/// <see cref="CgmesSubstationReader"/>, as the built CLI. The oracle is Core itself: the grouping the verb prints must be the
/// grouping <see cref="SubstationProjection"/> finds on the model the exporter was given (the round trip the Core tests prove),
/// with the SSH states as the starting states and --open / --close overriding them.
/// </summary>
public sealed class CgmesSubstationStateCliProcessTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-wire1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Exports the rich model with its normal switch states as a folder of profile documents.</summary>
    private static string Bundle(string d, out string zip)
    {
        string dir = Path.Combine(d, "bundle");
        CgmesExportResult e = CgmesSubstationExporter.Export(CgmesProfilesTests.Rich(), new Dictionary<string, bool>(),
            new CgmesExportOptions(CgmesVersion.V2_4_15, UnspecifiedStatePolicy.UseNormalState, null));
        Directory.CreateDirectory(dir);
        foreach (CgmesProfileFile f in e.Files) File.WriteAllText(Path.Combine(dir, f.FileName), f.Text);
        zip = Path.Combine(d, "bundle.zip");
        ZipFile.CreateFromDirectory(dir, zip);
        return dir;
    }

    private static string Grouping(string resultJson)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultJson));
        return doc.RootElement.GetProperty("summary").GetProperty("grouping").GetString()!;
    }

    private static string CoreGrouping(params (string Id, bool Open)[] states)
    {
        var s = states.ToDictionary(x => x.Id, x => x.Open, StringComparer.Ordinal);
        return SubstationProjection.Project(CgmesProfilesTests.Rich(), s, new SubstationProjectionOptions(UnspecifiedStatePolicy.UseNormalState)).GroupingText();
    }

    [Fact]
    public void A_CGMES_folder_and_zip_project_to_the_grouping_Core_finds_on_the_model_that_was_exported()
    {
        string d = Dir();
        string dir = Bundle(d, out string zip);
        string rf = Path.Combine(d, "f.json"), rz = Path.Combine(d, "z.json");
        CliProcess.Result f = CliProcess.RunBuilt("substation-state", dir, "--json", rf);
        CliProcess.Result z = CliProcess.RunBuilt("substation-state", zip, "--json", rz);
        Assert.True(f.ExitCode == 0, f.ToString());
        Assert.True(z.ExitCode == 0, z.ToString());
        Assert.Equal(CoreGrouping(), Grouping(rf));
        Assert.Equal(Grouping(rf), Grouping(rz));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(rf));
        Assert.Equal("cgmes-node-breaker", doc.RootElement.GetProperty("summary").GetProperty("format").GetString());
        Assert.True(doc.RootElement.GetProperty("tables").TryGetProperty("cgmes_notes", out _));
        Assert.Equal(CgmesProfilesTests.Rich().NormalStates().Count, doc.RootElement.GetProperty("summary").GetProperty("cgmes_ssh_switch_states").GetInt32());
    }

    [Fact]
    public void Open_and_close_override_the_SSH_states_exactly_as_the_projection_does()
    {
        string d = Dir();
        string dir = Bundle(d, out _);
        string r = Path.Combine(d, "o.json");
        // A.CPL is closed in the SSH; opening it splits the busbar, closing A.LBS (open in the SSH) is a second change.
        Assert.Equal(0, CliProcess.RunBuilt("substation-state", dir, "--open", "A.CPL", "--json", r).ExitCode);
        Assert.Equal(CoreGrouping(("A.CPL", true)), Grouping(r));
        Assert.NotEqual(CoreGrouping(), CoreGrouping(("A.CPL", true)));
        string r2 = Path.Combine(d, "o2.json");
        Assert.Equal(0, CliProcess.RunBuilt("substation-state", dir, "--close", "A.LBS", "--base-mva", "50", "--json", r2).ExitCode);
        Assert.Equal(CoreGrouping(("A.LBS", false)), Grouping(r2));
    }

    [Fact]
    public void Two_runs_give_identical_bytes()
    {
        string d = Dir();
        string dir = Bundle(d, out _);
        string a = Path.Combine(d, "a.json");
        CliProcess.Result x = CliProcess.RunBuilt("substation-state", dir, "--json", a);
        byte[] first = File.ReadAllBytes(a);
        File.Delete(a);   // the same result path both times: the file records where it was written
        CliProcess.Result y = CliProcess.RunBuilt("substation-state", dir, "--json", a);
        Assert.Equal(x.StdOut, y.StdOut);
        Assert.Equal(first, File.ReadAllBytes(a));
    }

    [Fact]
    public void Refusals_name_the_problem_and_use_the_documented_exit_codes()
    {
        string d = Dir();
        string empty = Path.Combine(d, "empty");
        Directory.CreateDirectory(empty);
        CliProcess.Result none = CliProcess.RunBuilt("substation-state", empty);
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("error: substation-state", none.StdErr, StringComparison.Ordinal);

        // A bundle without its EQ document is refused by the reader.
        string dir = Bundle(d, out _);
        string noEq = Path.Combine(d, "noeq");
        Directory.CreateDirectory(noEq);
        foreach (string f in Directory.GetFiles(dir).Where(f => !f.EndsWith("_EQ.xml", StringComparison.Ordinal))) File.Copy(f, Path.Combine(noEq, Path.GetFileName(f)));
        Assert.Equal(1, CliProcess.RunBuilt("substation-state", noEq).ExitCode);

        // --base-mva must be a positive number.
        Assert.Equal(2, CliProcess.RunBuilt("substation-state", dir, "--base-mva", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("substation-state", dir, "--base-mva", "x").ExitCode);

        string site = Path.Combine(d, "site.json");
        File.WriteAllText(site, SubstationJson.Write(SubstationFixtures.TwoFeed()));
        CliProcess.Result onJson = CliProcess.RunBuilt("substation-state", site, "--base-mva", "100", "--unspecified", "normal");
        Assert.Equal(2, onJson.ExitCode);
        Assert.Contains("--base-mva is for a CGMES input", onJson.StdErr, StringComparison.Ordinal);
    }
}
