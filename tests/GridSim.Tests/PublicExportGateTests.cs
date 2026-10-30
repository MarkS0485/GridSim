using System.Text.Json;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Provenance;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Export pack slice 2 (2026-10): the PUBLIC export gate (<see cref="PublicExportGate"/>, <see cref="ProvenanceManifest"/>)
/// and <c>gridsim convert --public</c>. Every manifest here is SYNTHETIC test data over the bundled IEEE 9-bus case (its
/// branches and units given test names); the sources it lists are made-up test fixtures, marked as such, and assert
/// nothing about any real network's provenance.
/// </summary>
public class PublicExportGateTests
{
    /// <summary>case9 with every branch named L1..L9 and every unit G1..G3 (the MATPOWER file names neither).</summary>
    private static GridModel NamedCase9()
    {
        GridModel m = IeeeCases.Case9();
        return m.CopyWith(
            branches: m.Branches.Select((b, k) => b with { Name = "L" + (k + 1) }).ToList(),
            generators: m.AllGenerators.Select((g, k) => g with { Name = "G" + (k + 1) }).ToList());
    }

    /// <summary>
    /// A synthetic manifest over <paramref name="m"/>: every bus, branch, unit, demand and shunt cites the published test
    /// source "pub", except the elements named in <paramref name="unpublished"/> (keys such as "branch:L5", "gen:G2",
    /// "demand:5"), which cite the unpublished test source "priv".
    /// </summary>
    private static string Manifest(GridModel m, params string[] unpublished)
    {
        var priv = new HashSet<string>(unpublished, StringComparer.Ordinal);
        string[] Cite(string key) => new[] { priv.Contains(key) ? "priv" : "pub" };
        var doc = new Dictionary<string, object>
        {
            ["schema"] = "gridsim-provenance/1",
            ["case"] = m.Name,
            ["sources"] = new object[]
            {
                new Dictionary<string, object> { ["id"] = "pub", ["title"] = "Synthetic test source (published)", ["publisher"] = "test fixture", ["url"] = "https://example.invalid/pub", ["licence"] = "test only", ["published"] = true },
                new Dictionary<string, object> { ["id"] = "priv", ["title"] = "Synthetic test source (not published)", ["publisher"] = "test fixture", ["url"] = "https://example.invalid/priv", ["licence"] = "test only", ["published"] = false },
            },
            ["buses"] = m.Buses.ToDictionary(b => b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), b => Cite("bus:" + b.Id)),
            ["branches"] = m.Branches.ToDictionary(b => b.Name!, b => Cite("branch:" + b.Name)),
            ["generators"] = m.AllGenerators.ToDictionary(g => g.Name!, g => Cite("gen:" + g.Name)),
            ["demand"] = m.Buses.Where(b => b.Pd != 0 || b.Qd != 0).ToDictionary(b => b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), b => Cite("demand:" + b.Id)),
        };
        return JsonSerializer.Serialize(doc);
    }

    [Fact]
    public void A_case_every_element_of_which_is_published_passes_unchanged()
    {
        GridModel m = NamedCase9();
        PublicExportResult r = PublicExportGate.Apply(m, ProvenanceManifest.Parse(Manifest(m)));
        Assert.Empty(r.Stripped);
        Assert.Equal(m.Buses, r.Model.Buses);
        Assert.Equal(m.Branches, r.Model.Branches);
        Assert.Equal(m.AllGenerators, r.Model.AllGenerators);
        Assert.Equal(m.BaseMva, r.Model.BaseMva);
    }

    [Fact]
    public void Unpublished_elements_are_stripped_or_zeroed_and_each_is_listed()
    {
        GridModel m = NamedCase9();
        Generator pvUnit = m.Generators.First(g => m.Buses.Single(b => b.Id == g.Bus).Type == BusType.Pv);
        Bus loaded = m.Buses.First(b => b.Pd != 0);
        PublicExportResult r = PublicExportGate.Apply(m, ProvenanceManifest.Parse(
            Manifest(m, "branch:L5", "gen:" + pvUnit.Name, "demand:" + loaded.Id)));

        Assert.Equal(m.Branches.Count - 1, r.Model.Branches.Count);
        Assert.DoesNotContain(r.Model.Branches, b => b.Name == "L5");
        Assert.DoesNotContain(r.Model.AllGenerators, g => g.Name == pvUnit.Name);
        Assert.Equal(BusType.Pq, r.Model.Buses.Single(b => b.Id == pvUnit.Bus).Type);
        Bus zeroed = r.Model.Buses.Single(b => b.Id == loaded.Id);
        Assert.Equal(0.0, zeroed.Pd);
        Assert.Equal(0.0, zeroed.Qd);
        Assert.Contains(r.Stripped, s => s.StartsWith("1 branch(es) stripped", StringComparison.Ordinal) && s.Contains("L5", StringComparison.Ordinal));
        Assert.Contains(r.Stripped, s => s.StartsWith("1 generator(s) stripped", StringComparison.Ordinal) && s.Contains(pvUnit.Name!, StringComparison.Ordinal));
        Assert.Contains(r.Stripped, s => s.StartsWith("1 bus demand(s) stripped", StringComparison.Ordinal));
        Assert.Contains(r.Stripped, s => s.StartsWith("1 PV bus(es) made PQ", StringComparison.Ordinal));
        // Everything else is the input record for record.
        Assert.Equal(m.Branches.Where(b => b.Name != "L5"), r.Model.Branches);
    }

    [Fact]
    public void An_unpublished_slack_unit_a_foreign_manifest_and_bad_manifests_are_refused()
    {
        GridModel m = NamedCase9();
        Generator slackUnit = m.Generators.First(g => m.Buses.Single(b => b.Id == g.Bus).Type == BusType.Slack);
        var slack = Assert.Throws<InvalidDataException>(() => PublicExportGate.Apply(m, ProvenanceManifest.Parse(Manifest(m, "gen:" + slackUnit.Name))));
        Assert.Contains("slack bus", slack.Message, StringComparison.Ordinal);

        string foreign = Manifest(m).Replace($"\"case\":{JsonSerializer.Serialize(m.Name)}", "\"case\":\"another\"", StringComparison.Ordinal);
        var other = Assert.Throws<InvalidDataException>(() => PublicExportGate.Apply(m, ProvenanceManifest.Parse(foreign)));
        Assert.Contains("is for case 'another'", other.Message, StringComparison.Ordinal);

        string ok = Manifest(m);
        Assert.Contains("unknown key 'extra'", Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(ok.Insert(1, "\"extra\":1,"))).Message, StringComparison.Ordinal);
        Assert.Contains("this reader reads", Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(ok.Replace("gridsim-provenance/1", "gridsim-provenance/9", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        Assert.Contains("does not list", Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(ok.Replace("[\"pub\"]", "[\"nowhere\"]", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        Assert.Contains("cites no source", Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(ok.Replace("[\"pub\"]", "[]", StringComparison.Ordinal))).Message, StringComparison.Ordinal);
        Assert.Contains("'branches' is missing", Assert.Throws<InvalidDataException>(() => ProvenanceManifest.Parse(
            JsonSerializer.Serialize(new { schema = "gridsim-provenance/1", @case = "x", sources = Array.Empty<object>(), buses = new { }, generators = new { } }))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_public_writes_the_gated_model_and_refuses_without_a_manifest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-convert-public-" + Guid.NewGuid().ToString("N"));
        try
        {
            GridModel m = NamedCase9();
            string caseDir = Path.Combine(dir, "case");
            JsonCaseWriter.WriteDirectory(m, caseDir);

            // No manifest: exit 1, nothing written.
            string none = Path.Combine(dir, "none.m");
            CliProcess.Result n = CliProcess.RunBuilt("convert", caseDir, none, "--public");
            Assert.True(n.ExitCode == 1, n.ToString());
            Assert.Contains("no provenance manifest at", n.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(none));

            // --provenance without --public is a usage error.
            CliProcess.Result u = CliProcess.RunBuilt("convert", caseDir, Path.Combine(dir, "u.m"), "--provenance", Path.Combine(caseDir, "provenance.json"));
            Assert.True(u.ExitCode == 2, u.ToString());

            // With the manifest in the case folder (the default place): branch L5 is unpublished and stripped.
            File.WriteAllText(Path.Combine(caseDir, "provenance.json"), Manifest(m, "branch:L5"));
            string output = Path.Combine(dir, "public.m");
            CliProcess.Result r = CliProcess.RunBuilt("convert", caseDir, output, "--public");
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("public export: provenance manifest", r.StdOut, StringComparison.Ordinal);
            Assert.Contains("public: 1 branch(es) stripped", r.StdOut, StringComparison.Ordinal);
            Assert.Contains($", {m.Branches.Count - 1} branches", r.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(output));

            // An explicit --provenance file elsewhere is read instead.
            string elsewhere = Path.Combine(dir, "elsewhere.json");
            File.WriteAllText(elsewhere, Manifest(m));
            CliProcess.Result e = CliProcess.RunBuilt("convert", caseDir, Path.Combine(dir, "all.m"), "--public", "--provenance", elsewhere);
            Assert.True(e.ExitCode == 0, e.ToString());
            Assert.Contains("nothing stripped", e.StdOut, StringComparison.Ordinal);

            // A built-in case with no folder needs --provenance.
            CliProcess.Result b = CliProcess.RunBuilt("convert", "case9", Path.Combine(dir, "b.m"), "--public");
            Assert.True(b.ExitCode == 1, b.ToString());
            Assert.Contains("--provenance", b.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(dir, "b.m")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Gb_full_has_no_provenance_manifest_so_its_public_export_fails_loud()
    {
        // gb-full carries no per-element provenance; until a real manifest exists its public export must refuse.
        Assert.False(File.Exists(Path.Combine(CliProcess.Root(), "data", "gb-full", "provenance.json")));
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-convert-public-gb-" + Guid.NewGuid().ToString("N"));
        try
        {
            string output = Path.Combine(dir, "gb-full.m");
            CliProcess.Result r = CliProcess.RunBuilt("convert", "gb-full", output, "--public");
            Assert.True(r.ExitCode == 1, r.ToString());
            Assert.Contains("no provenance manifest at", r.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
