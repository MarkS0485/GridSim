using System.IO.Compression;
using GridSim.Core.IO.Cim;
using GridSim.Core.IO.Cim.Profiles;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Tests.Substations;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 wiring: <c>convert</c> takes a CIM / CGMES input (a folder of profile files, a .zip exchange or one .xml document) through
/// the Core importers the desktop app uses. The oracle is that Core path run in the test: <see cref="CimImporter"/> with the default
/// options, then <see cref="CimGridModelBridge"/> on 100 MVA, then <see cref="MatpowerWriter"/>. The written file must be those
/// bytes, and the verb's own round-trip proof must pass.
/// </summary>
public sealed class ConvertCimCliProcessTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-wire2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A two-substation exchange written by the exporter with its normal switch states and a solved SV.</summary>
    private static string Bundle(string d, out string zip, out string[] docs)
    {
        string dir = Path.Combine(d, "bundle");
        SubstationModel m = SubstationFixtures.TwoFeed();
        CgmesExportResult e = CgmesSubstationExporter.Export(m, new Dictionary<string, bool>(),
            new CgmesExportOptions(CgmesVersion.V2_4_15, UnspecifiedStatePolicy.UseNormalState, null));
        Directory.CreateDirectory(dir);
        foreach (CgmesProfileFile f in e.Files) File.WriteAllText(Path.Combine(dir, f.FileName), f.Text);
        zip = Path.Combine(d, "bundle.zip");
        ZipFile.CreateFromDirectory(dir, zip);
        docs = CgmesArchiveReader.ReadDirectory(dir).Entries
            .Where(x => x.Kind is not (CgmesProfileKind.Unknown or CgmesProfileKind.DL or CgmesProfileKind.GL or CgmesProfileKind.SC))
            .Select(x => x.Text).ToArray();
        return dir;
    }

    private static string CoreMatpower(string[] docs, string name)
    {
        GridModel m = CimGridModelBridge.ToGridModel(CimImporter.Parse(docs, CimImportOptions.Default), name: name);
        return MatpowerWriter.Write(m).Text;
    }

    [Fact]
    public void A_CGMES_folder_and_zip_convert_to_the_bytes_the_Core_importers_give_and_pass_the_round_trip_proof()
    {
        string d = Dir();
        string dir = Bundle(d, out string zip, out string[] docs);
        string fromDir = Path.Combine(d, "fromdir.m"), fromZip = Path.Combine(d, "fromzip.m");
        CliProcess.Result a = CliProcess.RunBuilt("convert", dir, fromDir);
        CliProcess.Result b = CliProcess.RunBuilt("convert", zip, fromZip);
        Assert.True(a.ExitCode == 0, a.ToString());
        Assert.True(b.ExitCode == 0, b.ToString());
        Assert.Contains("round trip: the written case solves bit-identically", a.StdOut, StringComparison.Ordinal);
        string expected = CoreMatpower(docs, "bundle");
        Assert.Equal(expected, File.ReadAllText(fromDir));
        Assert.Equal(expected, File.ReadAllText(fromZip));
        GridModel core = CimGridModelBridge.ToGridModel(CimImporter.Parse(docs, CimImportOptions.Default), name: "bundle");
        Assert.Contains($": {core.BusCount} buses, {core.AllGenerators.Count} generators", a.StdOut, StringComparison.Ordinal);
        Assert.Equal(0, CliProcess.RunBuilt("pf", fromDir).ExitCode);
    }

    [Fact]
    public void A_CIM_input_also_writes_a_JSON_case_folder_that_loads_back()
    {
        string d = Dir();
        string dir = Bundle(d, out _, out _);
        CliProcess.Result j = CliProcess.RunBuilt("convert", dir, Path.Combine(d, "folder"));
        Assert.True(j.ExitCode == 0, j.ToString());
        Assert.True(File.Exists(Path.Combine(d, "folder", "buses.json")));
        Assert.Equal(0, CliProcess.RunBuilt("pf", Path.Combine(d, "folder")).ExitCode);
    }

    [Fact]
    public void Two_runs_write_identical_bytes()
    {
        string d = Dir();
        string dir = Bundle(d, out _, out _);
        string a = Path.Combine(d, "a.m"), b = Path.Combine(d, "b.m");
        Assert.Equal(0, CliProcess.RunBuilt("convert", dir, a).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("convert", dir, b).ExitCode);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void An_input_that_is_not_CIM_is_refused_by_name_and_an_existing_output_is_never_overwritten()
    {
        string d = Dir();
        string bad = Path.Combine(d, "bad.xml");
        File.WriteAllText(bad, "<not-rdf/>");
        string o1 = Path.Combine(d, "o1.m");
        CliProcess.Result r = CliProcess.RunBuilt("convert", bad, o1);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("error loading case", r.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(o1));

        string emptyZipDir = Path.Combine(d, "noprofiles");
        Directory.CreateDirectory(emptyZipDir);
        File.WriteAllText(Path.Combine(emptyZipDir, "readme.xml"), "<readme/>");
        CliProcess.Result z = CliProcess.RunBuilt("convert", emptyZipDir, Path.Combine(d, "o2.m"));
        Assert.Equal(1, z.ExitCode);

        string dir = Bundle(d, out _, out _);
        string out3 = Path.Combine(d, "o3.m");
        File.WriteAllText(out3, "keep");
        Assert.Equal(2, CliProcess.RunBuilt("convert", dir, out3).ExitCode);
        Assert.Equal("keep", File.ReadAllText(out3));
    }
}
