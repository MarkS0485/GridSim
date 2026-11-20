using GridSim.Core.Licensing;
using GridSim.Tests.V2;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Index;
using GridSim.Ui.Geography;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>Post-RC3 audit APP-22: Save As of a v2 case carries the case's geography.json beside the manifest.</summary>
public sealed class PostRc3AppSaveAsGeographyTests
{
    private const string Geo =
        "{\"format\": \"gda-geography/1\", \"projection\": {\"kind\": \"lonlat-affine\", \"south\": 0, \"west\": 0, \"north\": 1, \"east\": 1, \"margin\": 0}, " +
        "\"point_layout\": [\"lon\", \"lat\", \"x\", \"y\"], \"land\": [{\"name\": \"Testland\", \"rings\": [[[0.2, 0.2, 0.2, 0.2], [0.8, 0.2, 0.8, 0.2], [0.8, 0.8, 0.8, 0.8], [0.2, 0.8, 0.2, 0.8]]]}], \"sources\": []}";

    [Fact]
    public void Save_As_of_a_v2_case_copies_geography_json_unchanged_and_says_so()
    {
        using var f = new UiOverlayFixture(withOverlay: false);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Geo);
        File.WriteAllBytes(Path.Combine(f.CaseDir, CaseGeography.FileName), bytes);
        Assert.True(CaseGeography.TryLoad(f.CaseDir, out _, out string? p0), p0);

        string target = Path.Combine(Path.GetTempPath(), "gridsim-saveas-geo-" + Guid.NewGuid().ToString("N"));
        try
        {
            CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(f.ManifestPath), GridSim.Tests.Editor.CaseSessionTests.Unlimited(), Array.Empty<string>());
            AuthoringReport r = a.SaveAs(target);
            Assert.True(r.Saved, string.Join(Environment.NewLine, r.Lines()));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, CaseGeography.FileName)));
            Assert.True(CaseGeography.TryLoad(target, out CaseGeography? g, out string? problem), problem);
            Assert.NotNull(g);
            Assert.Contains(r.Notes, n => n.Contains(CaseGeography.FileName, StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    [Fact]
    public void Save_As_of_a_v2_case_without_geography_json_writes_none()
    {
        using var f = new UiOverlayFixture(withOverlay: false);
        string target = Path.Combine(Path.GetTempPath(), "gridsim-saveas-nogeo-" + Guid.NewGuid().ToString("N"));
        try
        {
            CaseAuthoring a = CaseAuthoring.Open(AssetCaseSource.V2(f.ManifestPath), GridSim.Tests.Editor.CaseSessionTests.Unlimited(), Array.Empty<string>());
            Assert.True(a.SaveAs(target).Saved);
            Assert.False(File.Exists(Path.Combine(target, CaseGeography.FileName)));
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }
}
