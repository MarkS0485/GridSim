using GridSim.Web.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Known limitation "Discovery and static files" (chapter 36, Web host): two GDA model folders under gda-out/ whose
/// names slug alike got the same gda-* id, and <see cref="CaseCatalog.Find"/> takes the first entry with an id, so the
/// second model could not be opened at all. The gda-* ids are now made unique as the MATPOWER and replay ids are. The
/// catalogue only checks that system.json exists while it scans, so an empty JSON object stands in for a model.
/// </summary>
public sealed class CaseCatalogGdaIdTests
{
    private static string NewBaseDir(params string[] gdaModels)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "gridsim-gda-ids-" + Guid.NewGuid().ToString("N"));
        foreach (string name in gdaModels)
        {
            string dir = Path.Combine(baseDir, "gda-out", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "system.json"), "{}");
        }
        return baseDir;
    }

    // Only the entries this test made (the scan also walks up the temporary folder's ancestors).
    private static List<CaseEntry> GdaEntriesUnder(CaseCatalog catalog, string baseDir) =>
        catalog.Entries
            .Where(e => e.Kind == CaseKind.JsonDir && e.Path.StartsWith(baseDir, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void Two_gda_folders_that_slug_alike_get_distinct_ids_and_each_id_finds_its_own_folder()
    {
        // "Foo Bar" and "foo-bar" both slug to "foo-bar". The scan lists folders case-insensitively in order, and a
        // space sorts before a dash, so "Foo Bar" is first and keeps the plain id.
        string baseDir = NewBaseDir("Foo Bar", "foo-bar");
        var catalog = new CaseCatalog(baseDir);

        List<CaseEntry> gda = GdaEntriesUnder(catalog, baseDir);
        Assert.Equal(2, gda.Count);
        Assert.Equal(new[] { "gda-foo-bar", "gda-foo-bar-2" }, gda.Select(e => e.Id).ToArray());

        // Before the fix both entries were "gda-foo-bar" and Find returned the first for either; now each id opens
        // its own folder.
        Assert.Equal(Path.Combine(baseDir, "gda-out", "Foo Bar"), catalog.Find("gda-foo-bar")!.Path);
        Assert.Equal(Path.Combine(baseDir, "gda-out", "foo-bar"), catalog.Find("gda-foo-bar-2")!.Path);
        Assert.Equal("GDA * foo-bar", catalog.Find("gda-foo-bar-2")!.Label);
    }

    [Fact]
    public void Gda_folders_that_do_not_collide_keep_their_plain_ids()
    {
        string baseDir = NewBaseDir("North West", "south-east");
        var catalog = new CaseCatalog(baseDir);

        List<CaseEntry> gda = GdaEntriesUnder(catalog, baseDir);
        Assert.Equal(new[] { "gda-north-west", "gda-south-east" }, gda.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Every_catalogue_id_is_unique()
    {
        string baseDir = NewBaseDir("Foo Bar", "foo-bar", "FOO_BAR");
        var catalog = new CaseCatalog(baseDir);

        List<string> duplicated = catalog.Entries
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicated);
        Assert.Equal(3, GdaEntriesUnder(catalog, baseDir).Count);
    }
}
