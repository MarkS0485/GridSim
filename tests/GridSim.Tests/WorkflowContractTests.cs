using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F04, F06, F07 (release register section 1): the GitHub workflows cannot be run from the test suite, but the
/// promises they make can be pinned so an edit cannot quietly drop one: the CI matrix covers both operating systems
/// and all three SIMD modes and still reports the "Build &amp; test" check the Dependabot auto-merge waits for;
/// Dependabot watches every project; the release refuses a tag that disagrees with Directory.Build.props; the
/// vulnerable-package gate and CodeQL exist.
/// </summary>
public class WorkflowContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    [Fact]
    public void Ci_runs_both_operating_systems_and_all_three_simd_modes()
    {
        string ci = Read(".github/workflows/ci.yml");
        Assert.Contains("os: [ ubuntu-latest, windows-latest ]", ci, StringComparison.Ordinal);
        Assert.Contains("simd: [ normal, avx2-off, hwintrinsic-off ]", ci, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_EnableAVX2=0", ci, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_EnableHWIntrinsic=0", ci, StringComparison.Ordinal);
        Assert.Contains("fail-fast: false", ci, StringComparison.Ordinal);
        // GR-399: the 256-bit production tier has a leg of its own, which must prove the tier it ran.
        Assert.Contains("- { os: ubuntu-latest,  simd: avx256 }", ci, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_PreferredVectorBitWidth=256", ci, StringComparison.Ordinal);
        Assert.Contains("want=\"Active vector tier: Avx256 \"", ci, StringComparison.Ordinal);
        // The required status check keeps its exact name and fails when any leg fails.
        Assert.Contains("    name: Build & test\n", ci, StringComparison.Ordinal);
        Assert.Contains("if: always()", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Dependabot_watches_every_project()
    {
        string bot = Read(".github/dependabot.yml");
        foreach (string p in new[] { "src/GridSim.Core", "src/GridSim.Gda", "src/GridSim.Gpu", "src/GridSim.Cli",
                                     "src/GridSim.Web", "src/GridSim.Wpf", "tests/GridSim.Tests" })
            Assert.Contains($"- \"/{p}\"", bot, StringComparison.Ordinal);
        // Test extended 2026-10-20 (UI programme 0.4): the Avalonia projects are shipped projects, so Dependabot watches
        // them too; and, so a later project cannot be missed the way Gpu, Web and Wpf once were (F06), every project in
        // GridSim.slnx that has a PackageReference must be listed.
        foreach (string p in new[] { "src/GridSim.App", "src/GridSim.App.Desktop", "tests/GridSim.App.Tests" })
            Assert.Contains($"- \"/{p}\"", bot, StringComparison.Ordinal);
        var withPackages = System.Text.RegularExpressions.Regex.Matches(Read("GridSim.slnx"), "<Project Path=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(csproj => Read(csproj).Contains("<PackageReference", StringComparison.Ordinal))
            .Select(csproj => Path.GetDirectoryName(csproj)!.Replace('\\', '/'))
            .ToList();
        Assert.Contains("src/GridSim.App", withPackages);   // the scan really found the projects with packages
        foreach (string dir in withPackages)
            Assert.True(bot.Contains($"- \"/{dir}\"", StringComparison.Ordinal),
                $"{dir} has PackageReferences but .github/dependabot.yml does not watch it");
        Assert.Contains("package-ecosystem: \"docker\"", bot, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_is_tag_driven_and_gated_on_the_source_version()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("tags: [ 'v*' ]", rel, StringComparison.Ordinal);
        Assert.Contains("does not match Directory.Build.props version", rel, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md has no", rel, StringComparison.Ordinal);
        foreach (string rid in new[] { "win-x64", "linux-x64", "osx-arm64" })
            Assert.Contains($"rid: {rid}", rel, StringComparison.Ordinal);
        Assert.Contains("[[ \"$out\" == \"gridsim $VERSION+$SHA\" ]]", rel, StringComparison.Ordinal);
        Assert.Contains("dotnet-CycloneDX", rel, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", rel, StringComparison.Ordinal);
        // F11: the published-output tests run against each real publish folder (they are skipped elsewhere).
        Assert.Contains("GRIDSIM_PUBLISHED_DIR: artifacts/publish/gridsim-${{ matrix.rid }}", rel, StringComparison.Ordinal);
        Assert.Contains("--filter \"FullyQualifiedName~PublishedOutputTests\"", rel, StringComparison.Ordinal);
        // The NuGet packages may only carry the source version, whatever triggered them.
        Assert.Contains("does not match Directory.Build.props", Read(".github/workflows/publish.yml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// F25 (Ruling R1, 2026-10-04): the 1.0 ship scope is the CLI as self-contained binaries for three platforms,
    /// GridSim.Core as a NuGet package and the web app as the replay-only container image. The desktop app and Unity
    /// come after 1.0; GridSim.Gda and the GDA data are not release artefacts. The workflows must match exactly.
    /// </summary>
    [Fact]
    public void Release_ships_exactly_the_1_0_scope()
    {
        string rel = Read(".github/workflows/release.yml");
        // The CLI binaries (three platforms, pinned above) and the web image, built, probed for replay-only, pushed.
        Assert.Contains("docker build -t gridsim-web:release", rel, StringComparison.Ordinal);
        Assert.Contains("/api/openapi.json", rel, StringComparison.Ordinal);
        Assert.Contains("the image is not replay-only", rel, StringComparison.Ordinal);
        Assert.Contains("image=\"ghcr.io/${OWNER,,}/gridsim-web:$VERSION\"", rel, StringComparison.Ordinal);
        // Only release-* artefacts reach the Release page, and the page is checked against the exact file list.
        Assert.Contains("pattern: release-*", rel, StringComparison.Ordinal);
        Assert.Contains("the release files differ from the 1.0 ship scope (F25)", rel, StringComparison.Ordinal);
        // Not shipped: no desktop publish, no web zip, nothing from GridSim.Gda.
        Assert.DoesNotContain("dotnet publish src/GridSim.Wpf", rel, StringComparison.Ordinal);
        Assert.DoesNotContain("gridsim-desktop-", rel, StringComparison.Ordinal);
        Assert.DoesNotContain("gridsim-web-$VERSION.zip", rel, StringComparison.Ordinal);
        Assert.DoesNotContain("src/GridSim.Gda", rel, StringComparison.Ordinal);

        // NuGet: GridSim.Core only, and the pack step refuses any other package.
        string pub = Read(".github/workflows/publish.yml");
        Assert.Contains("dotnet pack src/GridSim.Core/GridSim.Core.csproj", pub, StringComparison.Ordinal);
        Assert.Contains("\"GridSim.Core.$VERSION.nupkg\"", pub, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet pack src/GridSim.Gda", pub, StringComparison.Ordinal);
        Assert.Contains("<IsPackable>false</IsPackable>", Read("src/GridSim.Gda/GridSim.Gda.csproj"), StringComparison.Ordinal);

        // The README states the scope.
        string readme = Read("README.md");
        Assert.Contains("## What ships in 1.0", readme, StringComparison.Ordinal);
        Assert.Contains("`GridSim.Gda` and the GDA data are not release artefacts", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// F13 (Ruling R1): the Windows CLI binary is Authenticode-signed and the NuGet package signed, with the certificate
    /// read from repository secrets BY NAME (never a value in the repository). GR-359: with the secrets absent the
    /// release builds unsigned and says so in the log, unless the repository variable GRIDSIM_ALLOW_UNSIGNED_RELEASE is
    /// 'false', when it fails before it builds.
    /// </summary>
    [Fact]
    public void Release_signs_with_the_certificate_from_named_secrets_or_says_it_did_not()
    {
        string rel = Read(".github/workflows/release.yml");
        string pub = Read(".github/workflows/publish.yml");
        foreach (string wf in new[] { rel, pub })
        {
            Assert.Contains("${{ secrets.SIGNING_CERT_PFX_BASE64 }}", wf, StringComparison.Ordinal);
            Assert.Contains("${{ secrets.SIGNING_CERT_PASSWORD }}", wf, StringComparison.Ordinal);
            Assert.Contains("${{ vars.SIGNING_TIMESTAMP_URL || 'http://timestamp.digicert.com' }}", wf, StringComparison.Ordinal);
            Assert.Contains("signing SKIPPED: the repository secret SIGNING_CERT_PFX_BASE64 is not set", wf, StringComparison.Ordinal);
            Assert.Contains("SIGNING_CERT_PFX_BASE64 is set but SIGNING_CERT_PASSWORD is not", wf, StringComparison.Ordinal);
            // No certificate material in a workflow: no inline PFX / PEM blob.
            Assert.DoesNotContain("BEGIN CERTIFICATE", wf, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", wf, StringComparison.Ordinal);
        }
        // Authenticode on the Windows binary, verified, before the smoke test and the archive use it.
        Assert.Contains("signtool.FullName sign /fd SHA256 /td SHA256 /tr", rel, StringComparison.Ordinal);
        Assert.Contains("signtool.FullName verify /pa", rel, StringComparison.Ordinal);
        Assert.True(rel.IndexOf("Sign the Windows binary (Authenticode)", StringComparison.Ordinal)
                    < rel.IndexOf("Smoke test on this OS", StringComparison.Ordinal), "the binary must be signed before it is tested and archived");
        // GR-909: the called package workflow receives the two signing secrets by name, and no others. This used to
        // pin `secrets: inherit`, which passed every repository secret; the assertion is reversed because the narrower
        // grant is the correct one (publish.yml reads only these two, and GITHUB_TOKEN reaches it without passing).
        Assert.DoesNotContain("secrets: inherit", rel, StringComparison.Ordinal);
        Assert.Contains("      SIGNING_CERT_PFX_BASE64: ${{ secrets.SIGNING_CERT_PFX_BASE64 }}\n      SIGNING_CERT_PASSWORD: ${{ secrets.SIGNING_CERT_PASSWORD }}\n", rel, StringComparison.Ordinal);
        // GR-359: whether signing is required is a repository variable, checked in the first job before anything is
        // built, and again in each signing step.
        Assert.Contains("vars.GRIDSIM_ALLOW_UNSIGNED_RELEASE", pub, StringComparison.Ordinal);
        Assert.True(rel.IndexOf("Signing certificate present (or an unsigned release chosen)", StringComparison.Ordinal)
                    < rel.IndexOf("      - name: Build\n", StringComparison.Ordinal), "the signing decision must come before the build");
        Assert.Equal(2, rel.Split("vars.GRIDSIM_ALLOW_UNSIGNED_RELEASE").Length - 1);
        // GR-907: signtool signs from the certificate store; the password is never on its command line.
        Assert.DoesNotContain("/p $env:SIGNING_CERT_PASSWORD", rel, StringComparison.Ordinal);
        Assert.Contains("/s My /sha1 $pinned", rel, StringComparison.Ordinal);
        // Signed and verified NuGet package, before the push.
        Assert.Contains("dotnet nuget sign \"$p\"", pub, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget verify --all \"$p\"", pub, StringComparison.Ordinal);
        Assert.True(pub.IndexOf("Sign the package", StringComparison.Ordinal) < pub.IndexOf("Push to GitHub Packages", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_release_profile_the_workflow_uses_exists()
    {
        string root = Root();
        foreach (string p in new[]
        {
            "src/GridSim.Cli/Properties/PublishProfiles/win-x64.pubxml",
            "src/GridSim.Cli/Properties/PublishProfiles/linux-x64.pubxml",
            "src/GridSim.Cli/Properties/PublishProfiles/osx-arm64.pubxml",
            "src/GridSim.Web/Properties/PublishProfiles/portable.pubxml",
            "src/GridSim.Wpf/Properties/PublishProfiles/win-x64.pubxml",
        })
        {
            string text = File.ReadAllText(Path.Combine(root, p));
            // ReadyToRun stays off: one verified code-generation path for the numerics (see the profile comments).
            Assert.Contains("<PublishReadyToRun>false</PublishReadyToRun>", text, StringComparison.Ordinal);
            Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Security_gates_exist()
    {
        Assert.Contains("--vulnerable --include-transitive", Read(".github/workflows/ci.yml"), StringComparison.Ordinal);
        string codeql = Read(".github/workflows/codeql.yml");
        Assert.Contains("languages: csharp", codeql, StringComparison.Ordinal);
        Assert.Contains("security-events: write", codeql, StringComparison.Ordinal);
    }
}
