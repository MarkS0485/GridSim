using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>APP-25: a v2 case is bundled only when its manifest AND every file it lists are shipped content.</summary>
public sealed class FixKlLicensingTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-fixkl-lic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void App25_tampering_with_a_listed_network_file_alone_makes_the_v2_case_not_bundled()
    {
        string root = Path.Combine(_dir, "install", "data");
        string folder = Path.Combine(root, "uk", "v2case");
        Directory.CreateDirectory(folder);
        string manifest = Path.Combine(folder, "x.case.json");
        string network = Path.Combine(folder, "x.network.json");
        File.WriteAllText(manifest, "{ \"network\": [\"x.network.json\"] }\n");
        File.WriteAllText(network, "{ \"buses\": [] }\n");
        File.WriteAllLines(Path.Combine(root, BundledNetworks.ManifestFileName), new[]
        {
            $"{BundledNetworks.ContentHash(manifest)}  uk/v2case/x.case.json",
            $"{BundledNetworks.ContentHash(network)}  uk/v2case/x.network.json",
        });
        string[] roots = [root];

        Assert.True(BundledNetworks.IsBundledV2Case(manifest, roots));

        // The manifest bytes are untouched; only the listed network file is replaced.
        File.WriteAllText(network, "{ \"buses\": [1, 2, 3] }\n");
        Assert.True(BundledNetworks.IsBundledByContent(manifest, roots));    // the old check: still said bundled
        Assert.False(BundledNetworks.IsBundledV2Case(manifest, roots));

        // A path that is not a v2 manifest is judged by the plain rule.
        Assert.False(BundledNetworks.IsBundledV2Case(Path.Combine(_dir, "elsewhere", "x.case.json"), roots));
    }
}
