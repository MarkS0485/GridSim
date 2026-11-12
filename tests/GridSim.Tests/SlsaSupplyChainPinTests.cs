using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Supply-chain pins added on 2026-11-01, read straight from the workflow files so an edit cannot quietly undo them:
/// <list type="bullet">
/// <item>SLSA-6: every third-party action in every workflow is referenced by a full 40-hex commit SHA (a moved or
/// re-pointed tag cannot change a build step), with the release tag it was taken from in a trailing comment.</item>
/// <item>SLSA-4: the NuGet package gets a signed build-provenance attestation over the exact bytes that are pushed,
/// after signing and before the push, and the job (and the release job that calls it) holds the two permissions the
/// attestation needs.</item>
/// <item>GR-436: the CycloneDX SBOM tool is installed at a named version, recorded as an output of the SBOM job.</item>
/// </list>
/// </summary>
public class SlsaSupplyChainPinTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    // A "uses:" line naming a third-party action: owner/name[/path]@ref. Local reusable workflows ("uses: ./...") are
    // in the repository itself and are not matched.
    private static readonly Regex ThirdPartyUses = new(@"^\s*-?\s*uses:\s*(?<action>[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+)@(?<rest>.*)$",
        RegexOptions.CultureInvariant);

    // What every such line must look like after the action: a full commit SHA, two spaces, "# v" and the tag.
    private static readonly Regex PinnedRef = new(@"^[0-9a-f]{40}  # v[0-9]+(\.[0-9]+)*$", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_third_party_action_in_every_workflow_is_pinned_to_a_commit_sha_with_its_tag_in_a_comment()
    {
        string dir = Path.Combine(Root(), ".github", "workflows");
        string[] files = Directory.GetFiles(dir, "*.yml");
        Assert.NotEmpty(files);
        int checkedLines = 0;
        foreach (string file in files)
        {
            string[] lines = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = ThirdPartyUses.Match(lines[i]);
                if (!m.Success) continue;
                checkedLines++;
                string rest = m.Groups["rest"].Value.TrimEnd();
                Assert.True(PinnedRef.IsMatch(rest),
                    $"{Path.GetFileName(file)}:{i + 1}: '{m.Groups["action"].Value}@{rest}' is not pinned as '<40-hex sha>  # v<tag>'");
            }
        }
        // The workflows hold well over thirty third-party steps; finding none would mean the pattern stopped matching.
        Assert.True(checkedLines >= 30, $"expected at least 30 third-party uses: lines, found {checkedLines}");
    }

    [Fact]
    public void The_nuget_package_is_attested_after_signing_and_before_the_push()
    {
        string pub = Read(".github/workflows/publish.yml");
        int sign = pub.IndexOf("- name: Sign the package", StringComparison.Ordinal);
        int attest = pub.IndexOf("- name: Attest the package's build provenance", StringComparison.Ordinal);
        int push = pub.IndexOf("- name: Push to GitHub Packages", StringComparison.Ordinal);
        Assert.True(sign >= 0 && attest >= 0 && push >= 0, "the sign, attest and push steps must all exist");
        Assert.True(sign < attest && attest < push, "the attestation must cover the signed bytes and come before the push");
        Assert.Contains("uses: actions/attest-build-provenance@", pub, StringComparison.Ordinal);
        Assert.Contains("subject-path: 'artifacts/*.nupkg'", pub, StringComparison.Ordinal);

        // The package job holds the attestation permissions itself (manual and release-event runs) ...
        Assert.Contains("      id-token: write       # SLSA-4: the Sigstore identity for the package's provenance attestation\n", pub,
            StringComparison.Ordinal);
        Assert.Contains("      attestations: write   # SLSA-4: store the attestation with the repository\n", pub, StringComparison.Ordinal);
        // ... and the release job that calls it grants them, since a called workflow cannot exceed its caller.
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("      packages: write\n      id-token: write       # SLSA-4: the called package job attests the package's build provenance\n      attestations: write\n    uses: ./.github/workflows/publish.yml",
            rel, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_shipped_application_has_its_own_sbom_on_the_release_page()
    {
        // B5 (CAF-11 / NIS-8): the NuGet package (GridSim.Core alone) and the two desktop apps get SBOMs of exactly their
        // own project, each hashed into an output of the SBOM job.
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("dotnet-CycloneDX src/GridSim.Core/GridSim.Core.csproj --output artifacts/sbom --filename \"gridsim-core-$VERSION.sbom.json\"",
            rel, StringComparison.Ordinal);
        Assert.Contains("dotnet-CycloneDX src/GridSim.App.Desktop/GridSim.App.Desktop.csproj --output artifacts/sbom --filename \"gridsim-app-$VERSION.sbom.json\"",
            rel, StringComparison.Ordinal);
        Assert.Contains("dotnet-CycloneDX src/GridSim.Wpf/GridSim.Wpf.csproj --output artifacts/sbom --filename \"gridsim-wpf-$VERSION.sbom.json\"",
            rel, StringComparison.Ordinal);
        Assert.Contains("core_sha256: ${{ steps.hash.outputs.core }}", rel, StringComparison.Ordinal);
        Assert.Contains("app_desktop_sha256: ${{ steps.hash.outputs.app_desktop }}", rel, StringComparison.Ordinal);
        Assert.Contains("wpf_sha256: ${{ steps.hash.outputs.wpf }}", rel, StringComparison.Ordinal);

        // All five SBOMs are Release files (GA-5: the desktop apps ship, so their SBOMs ship with them): the exact-files
        // gate lists each one, and they travel in the release-sbom artefact the Release job downloads.
        Assert.Contains("\"gridsim-core-$VERSION.sbom.json\" \"gridsim-app-$VERSION.sbom.json\" \"gridsim-wpf-$VERSION.sbom.json\"", rel, StringComparison.Ordinal);
        Assert.Contains("          name: release-sbom\n          path: artifacts/sbom/*.json\n", rel, StringComparison.Ordinal);
        Assert.DoesNotContain("sbom-desktop", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_linux_cli_is_rebuilt_independently_and_compared_byte_for_byte()
    {
        // B7 (SLSA-7): a separate job rebuilds the linux-x64 CLI on a fresh runner with the cli job's exact command and
        // SBOM hash, and compares every file of the shipped archive with the rebuild by SHA-256; any difference fails.
        string rel = Read(".github/workflows/release.yml");
        int job = rel.IndexOf("\n  reproducible:\n", StringComparison.Ordinal);
        int next = rel.IndexOf("\n  release:\n", StringComparison.Ordinal);
        Assert.True(job >= 0 && next > job, "the reproducible job must exist, before the release job");
        string body = rel[job..next];
        Assert.Contains("    needs: [ verify, sbom, cli ]\n", body, StringComparison.Ordinal);
        Assert.Contains("    permissions:\n      contents: read\n    steps:\n", body, StringComparison.Ordinal);
        Assert.Contains("          name: release-cli-linux-x64\n", body, StringComparison.Ordinal);
        Assert.Contains("[[ -z \"$(git status --porcelain)\" ]]", body, StringComparison.Ordinal);
        Assert.Contains("dotnet publish src/GridSim.Cli -c Release -p:PublishProfile=linux-x64 -p:GridSimSbomSha256=\"$SBOM_SHA256\" --nologo",
            body, StringComparison.Ordinal);
        Assert.Contains("if ! diff -u \"$WORK/shipped.sha256\" \"$WORK/rebuilt.sha256\"", body, StringComparison.Ordinal);
        Assert.Contains("exit 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_has_its_own_sbom_and_the_self_contained_apps_list_their_bundled_runtime()
    {
        // GR-439: a Syft SBOM of the built web image (Ubuntu base packages and the ASP.NET runtime), checked for both and
        // put on the Release page; and the bundled .NET runtime added to the CLI and desktop SBOMs before they are hashed.
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("uses: anchore/sbom-action@", rel, StringComparison.Ordinal);
        Assert.Contains("          image: gridsim-web:release\n          format: cyclonedx-json\n", rel, StringComparison.Ordinal);
        Assert.Contains("          upload-release-assets: false\n", rel, StringComparison.Ordinal);
        Assert.Contains("problems.append(\"no Ubuntu operating-system component\")", rel, StringComparison.Ordinal);
        Assert.Contains("problems.append(\"no ASP.NET runtime component\")", rel, StringComparison.Ordinal);
        Assert.Contains("          name: release-image-sbom   # release-*: attached to the GitHub Release\n", rel, StringComparison.Ordinal);
        Assert.Contains("\"gridsim-web-image-$VERSION.sbom.json\" | sort)", rel, StringComparison.Ordinal);

        int inject = rel.IndexOf("- name: Add the bundled .NET runtime to the self-contained apps' SBOMs", StringComparison.Ordinal);
        int hashes = rel.IndexOf("- name: SBOM hashes", StringComparison.Ordinal);
        Assert.True(inject > 0 && inject < hashes, "the runtime must be added before the SBOMs are hashed");
        Assert.Contains("runtime(\"Microsoft.NETCore.App\",", rel, StringComparison.Ordinal);
        Assert.Contains("runtime(\"Microsoft.WindowsDesktop.App\",", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sbom_tool_is_installed_at_a_named_version_recorded_by_the_sbom_job()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Matches(new Regex(@"CYCLONEDX_VERSION: '[0-9]+\.[0-9]+\.[0-9]+'", RegexOptions.CultureInvariant), rel);
        Assert.Contains("dotnet tool install --global CycloneDX --version \"$CYCLONEDX_VERSION\"", rel, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet tool install --global CycloneDX\n", rel, StringComparison.Ordinal);
        Assert.Contains("cyclonedx_version: ${{ steps.sbomgen.outputs.cyclonedx_version }}", rel, StringComparison.Ordinal);
    }
}
