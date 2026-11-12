using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using GridSim.Core.Diagnostics.SupplyChain;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01 (GridSim 2.1.005): build, release and supply-chain findings (#161, #176, #195, #196,
/// #197). The web, security, licensing and I/O findings of the same hunt are in the other BugHunt1101Web*.cs files.
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepo(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------------------------------
    // #161: the SDK is pinned exactly, every workflow installs that SDK, CI restores in locked mode, and the CLI's
    // normal restore graph equals its release-publish graph so one lock file serves both.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B161_global_json_pins_one_exact_sdk_with_no_roll_forward()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadRepo("global.json"));
        JsonElement sdk = doc.RootElement.GetProperty("sdk");
        string version = sdk.GetProperty("version").GetString()!;
        // An exact three-part SDK version (feature band and patch), never a floating form.
        Assert.Matches(@"^10\.0\.[1-9][0-9]{2}$", version);
        // "disable": not even a newer patch of the same band may be used (10.0.403 over 10.0.401 is what drifted the
        // ILLink / AspNetCore Internal.Assets lock entries). latestPatch would let exactly that happen.
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());

        // The Dockerfile's SDK image carries the same SDK, and its runtime image is pinned too (no floating 10.0 tag).
        string docker = ReadRepo("Dockerfile");
        // Test correction (2026-11-01, GR-423, owner-approved; recorded in the commit and CHANGELOG): both images are now
        // pinned by digest as well as by version, and the digest is REQUIRED. The version pins are unchanged; a digest
        // between the tag and "AS" only tightens them (a re-pushed tag can no longer change the image). These asserted
        // "FROM mcr.microsoft.com/dotnet/sdk:{version} AS build" and "aspnet:10.0.N AS runtime" with no digest.
        Assert.Matches($@"FROM mcr\.microsoft\.com/dotnet/sdk:{System.Text.RegularExpressions.Regex.Escape(version)}@sha256:[0-9a-f]{{64}} AS build", docker);
        Assert.DoesNotContain("dotnet/sdk:10.0 ", docker, StringComparison.Ordinal);
        Assert.Matches(@"FROM mcr\.microsoft\.com/dotnet/aspnet:10\.0\.[0-9]+@sha256:[0-9a-f]{64} AS runtime", docker);
        Assert.Contains("RUN dotnet restore src/GridSim.Web/GridSim.Web.csproj --locked-mode", docker, StringComparison.Ordinal);
        Assert.Contains("COPY Directory.Build.props GridSim.slnx .editorconfig ./\nCOPY global.json ./\n", docker, StringComparison.Ordinal);
    }

    [Fact]
    public void B161_every_workflow_installs_the_sdk_from_global_json()
    {
        string dir = Path.Combine(RepoRoot(), ".github", "workflows");
        int setups = 0;
        foreach (string file in Directory.GetFiles(dir, "*.yml"))
        {
            string[] lines = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Assert.DoesNotContain("dotnet-version:", lines[i], StringComparison.Ordinal);
                if (!lines[i].Contains("uses: actions/setup-dotnet@", StringComparison.Ordinal)) continue;
                setups++;
                // The step's "with:" block follows on the next two lines.
                Assert.True(i + 2 < lines.Length, $"{Path.GetFileName(file)}:{i + 1}: setup-dotnet has no with-block");
                Assert.Equal("with:", lines[i + 1].Trim());
                Assert.Equal("global-json-file: global.json", lines[i + 2].Trim());
            }
        }
        Assert.True(setups >= 8, $"expected at least the 8 known setup-dotnet steps, found {setups}");
    }

    [Fact]
    public void B161_ci_restores_in_locked_mode()
    {
        string props = ReadRepo("Directory.Build.props");
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode Condition=\"'$(GITHUB_ACTIONS)' == 'true'\">true</RestoreLockedMode>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void B161_the_cli_lock_covers_every_release_runtime_and_the_single_file_graph()
    {
        // The publish profiles name the release runtimes; the csproj must list exactly those, and the committed lock
        // file must hold a section for each plus the implicit ILLink reference single-file publishing adds. Without
        // this a locked restore refuses the release publish (NU1004), and an unlocked one rewrites the lock.
        string root = RepoRoot();
        string[] profiles = Directory.GetFiles(Path.Combine(root, "src", "GridSim.Cli", "Properties", "PublishProfiles"), "*.pubxml")
            .Select(Path.GetFileNameWithoutExtension).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "linux-x64", "osx-arm64", "win-x64" }, profiles);

        string csproj = ReadRepo("src/GridSim.Cli/GridSim.Cli.csproj");
        Assert.Contains("<RuntimeIdentifiers>" + string.Join(';', new[] { "win-x64", "linux-x64", "osx-arm64" }) + "</RuntimeIdentifiers>", csproj, StringComparison.Ordinal);
        Assert.Contains("<EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>", csproj, StringComparison.Ordinal);

        using JsonDocument lockDoc = JsonDocument.Parse(ReadRepo("src/GridSim.Cli/packages.lock.json"));
        JsonElement deps = lockDoc.RootElement.GetProperty("dependencies");
        foreach (string rid in profiles)
            Assert.True(deps.TryGetProperty("net10.0/" + rid, out _), $"the CLI lock file has no net10.0/{rid} section");
        Assert.Equal("Direct", deps.GetProperty("net10.0").GetProperty("Microsoft.NET.ILLink.Tasks").GetProperty("type").GetString());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #176: a release build needs a 64-hex SBOM hash, a known commit and a clean source tree.
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly string Hex64 = new('a', 64);

    [Fact]
    public void B176_only_a_64_hex_sbom_a_known_commit_and_a_clean_tree_make_a_release_build()
    {
        Assert.Null(BuildProvenance.ReleaseRefusal(Hex64, "0123abcd", BuildProvenance.SourceTreeClean));
        Assert.Null(BuildProvenance.ReleaseRefusal(new string('F', 64), "0123abcd", BuildProvenance.SourceTreeClean));

        // The bug: any non-blank value used to count. "x" on a modified tree attested a release build.
        Assert.Equal("the injected SBOM hash is not 64 hexadecimal digits",
            BuildProvenance.ReleaseRefusal("x", "0123abcd", BuildProvenance.SourceTreeClean));
        Assert.NotNull(BuildProvenance.ReleaseRefusal(new string('a', 63), "0123abcd", BuildProvenance.SourceTreeClean));
        Assert.NotNull(BuildProvenance.ReleaseRefusal(new string('a', 65), "0123abcd", BuildProvenance.SourceTreeClean));
        Assert.NotNull(BuildProvenance.ReleaseRefusal(new string('g', 64), "0123abcd", BuildProvenance.SourceTreeClean));
        Assert.Equal("local/dev build; SBOM hash not injected",
            BuildProvenance.ReleaseRefusal(BuildProvenance.SbomUnset, "0123abcd", BuildProvenance.SourceTreeClean));

        Assert.Equal("the commit the build was made from is unknown",
            BuildProvenance.ReleaseRefusal(Hex64, "unknown", BuildProvenance.SourceTreeClean));
        Assert.Equal("the source tree had uncommitted changes when it was built",
            BuildProvenance.ReleaseRefusal(Hex64, "0123abcd", BuildProvenance.SourceTreeDirty));
        Assert.Equal("the state of the source tree was not recorded at build time",
            BuildProvenance.ReleaseRefusal(Hex64, "0123abcd", BuildProvenance.SourceTreeUnknown));
    }

    // A throwaway in-memory assembly that carries exactly the attributes a build would stamp, so ForAssembly can be
    // driven end to end without building anything.
    private static Assembly StampedAssembly(string informationalVersion, params (string Key, string Value)[] metadata)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("B176Probe" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { informationalVersion }));
        ConstructorInfo meta = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        foreach ((string key, string value) in metadata)
            builder.SetCustomAttribute(new CustomAttributeBuilder(meta, new object[] { key, value }));
        return builder;
    }

    [Fact]
    public void B176_a_dirty_tree_with_an_sbom_hash_is_not_a_release_build()
    {
        Assembly dirty = StampedAssembly("2.1.006+0123456789abcdef",
            (BuildProvenance.SbomMetadataKey, Hex64), (BuildProvenance.SourceTreeMetadataKey, "dirty"));
        BuildProvenance p = BuildProvenance.ForAssembly(dirty);
        Assert.Equal(BuildProvenance.SourceTreeDirty, p.SourceTree);
        Assert.False(p.IsReleaseBuild);
        Assert.Equal("the source tree had uncommitted changes when it was built", p.NotReleaseReason);

        Assembly clean = StampedAssembly("2.1.006+0123456789abcdef",
            (BuildProvenance.SbomMetadataKey, Hex64), (BuildProvenance.SourceTreeMetadataKey, "clean"));
        BuildProvenance c = BuildProvenance.ForAssembly(clean);
        Assert.True(c.IsReleaseBuild);
        Assert.Null(c.NotReleaseReason);
        // The build hash is the same identity label as before: the source-tree state does not enter it.
        Assert.Equal(BuildProvenance.ComputeBuildHash(c.Version, c.Commit, c.Toolchain, c.SbomSha256), c.BuildHash);
    }

    [Fact]
    public void B176_an_unrecognised_or_missing_tree_stamp_reads_unknown_and_never_release()
    {
        foreach (string odd in new[] { "Clean", "yes", "clean ", "true" })
        {
            Assembly a = StampedAssembly("2.1.006+0123456789abcdef",
                (BuildProvenance.SbomMetadataKey, Hex64), (BuildProvenance.SourceTreeMetadataKey, odd));
            BuildProvenance p = BuildProvenance.ForAssembly(a);
            // "clean " is trimmed by the metadata reader, so it is the one spelling that legitimately reads clean.
            if (odd == "clean ") { Assert.True(p.IsReleaseBuild); continue; }
            Assert.Equal(BuildProvenance.SourceTreeUnknown, p.SourceTree);
            Assert.False(p.IsReleaseBuild);
        }
        BuildProvenance none = BuildProvenance.ForAssembly(StampedAssembly("2.1.006+0123456789abcdef", (BuildProvenance.SbomMetadataKey, Hex64)));
        Assert.Equal(BuildProvenance.SourceTreeUnknown, none.SourceTree);
        Assert.False(none.IsReleaseBuild);

        // The bug's exact case: a non-hex SBOM value on a clean tree.
        BuildProvenance x = BuildProvenance.ForAssembly(StampedAssembly("2.1.006+0123456789abcdef",
            (BuildProvenance.SbomMetadataKey, "x"), (BuildProvenance.SourceTreeMetadataKey, "clean")));
        Assert.False(x.IsReleaseBuild);
    }

    [Fact]
    public void B176_the_report_says_why_a_build_is_not_a_release_and_shows_the_tree()
    {
        var a = new ComplianceAttestation("2.1.006", "abc", new string('A', 64), Hex64, new string('C', 64), 18, 18, false)
        {
            NotReleaseReason = "the source tree had uncommitted changes when it was built",
            SourceTree = BuildProvenance.SourceTreeDirty,
        };
        string report = a.Report();
        Assert.Contains("  release build  no (the source tree had uncommitted changes when it was built)", report, StringComparison.Ordinal);
        Assert.Contains("  source tree    dirty", report, StringComparison.Ordinal);
        Assert.False(a.IsCompliant);
        // The running test build is a local build: it says so, and the tree state was not stamped.
        Assert.Equal(BuildProvenance.SourceTreeUnknown, ComplianceAttestation.Current.SourceTree);
    }

    [Fact]
    public void B176_the_build_stamps_the_tree_and_the_image_build_is_handed_it()
    {
        string targets = ReadRepo("Directory.Build.targets");
        Assert.Contains("<Target Name=\"StampGridSimSourceTree\"", targets, StringComparison.Ordinal);
        Assert.Contains("Condition=\"'$(GridSimSbomSha256)' != ''\"", targets, StringComparison.Ordinal);
        Assert.Contains("<Exec Command=\"git status --porcelain\"", targets, StringComparison.Ordinal);
        Assert.Contains("<AssemblyMetadata Include=\"GridSimSourceTree\" Value=\"$(_GridSimSourceTreeValue)\" />", targets, StringComparison.Ordinal);

        string docker = ReadRepo("Dockerfile");
        Assert.Contains("COPY Directory.Build.targets ./", docker, StringComparison.Ordinal);
        Assert.Contains("ARG SOURCE_TREE=", docker, StringComparison.Ordinal);
        Assert.Contains("-p:GridSimSourceTree=\"${SOURCE_TREE}\"", docker, StringComparison.Ordinal);
        string rel = ReadRepo(".github/workflows/release.yml");
        Assert.Contains("--build-arg SOURCE_TREE=\"$tree\"", rel, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #196: the manifest no longer records as Achieved two controls whose descriptions the published release did not meet.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void B196_supply_chain_and_secure_sdlc_attestation_are_recorded_partially_achieved_with_the_gap_named()
    {
        foreach (string id in new[] { "supply-chain", "secure-sdlc-attestation" })
        {
            ComplianceControl c = ComplianceManifest.InScope.Single(x => x.Id == id);
            Assert.Equal(ComplianceStatus.PartiallyAchieved, c.Status);
            Assert.Contains("release.yml", c.Description, StringComparison.Ordinal);
            Assert.Contains("(", c.Description, StringComparison.Ordinal);
        }
        // The old premise, "only a release build, made after the full test matrix passed", must not come back.
        Assert.DoesNotContain(ComplianceManifest.InScope,
            c => c.Description.Contains("made after the full test matrix passed", StringComparison.Ordinal));
        // So no build, release or not, can read COMPLIANT while they are partial.
        Assert.False(ComplianceManifest.AllInScopeAchieved);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // #195: release.yml accepts the release-candidate tag forms, keeps the numeric part tied to <Version>, marks the
    // Release a pre-release, and pushes no package or image for a candidate.
    // ---------------------------------------------------------------------------------------------------------------

    // The tag pattern exactly as release.yml's verify step tests it (bash ERE; this pattern means the same in .NET).
    private static System.Text.RegularExpressions.Regex ReleaseTagPattern()
    {
        string line = ReadRepo(".github/workflows/release.yml").Split('\n')
            .Single(l => l.Contains("if [[ ! \"$TAG\" =~ ", StringComparison.Ordinal));
        int from = line.IndexOf("=~ ", StringComparison.Ordinal) + 3;
        int to = line.LastIndexOf(" ]]", StringComparison.Ordinal);
        return new System.Text.RegularExpressions.Regex(line[from..to], System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    [Theory]
    [InlineData("v2.1.000", "2.1.000", "")]
    [InlineData("v1.95.112", "1.95.112", "")]
    [InlineData("v2.1.000-RC-1", "2.1.000", "RC-1")]     // the RC-1 tag whose push failed in 5 s
    [InlineData("v2.0.195-rc1.0", "2.0.195", "rc1.0")]   // the RC 1.0 tag whose push failed in 5 s
    public void B195_release_and_candidate_tags_are_accepted_with_the_numeric_part_as_the_version(string tag, string version, string label)
    {
        System.Text.RegularExpressions.Match m = ReleaseTagPattern().Match(tag);
        Assert.True(m.Success, $"release.yml refuses the tag {tag}");
        Assert.Equal(version, m.Groups[1].Value);   // BASH_REMATCH[1]: compared with <Version>
        Assert.Equal(label, m.Groups[3].Value);     // BASH_REMATCH[3]: the label, empty for a final release
    }

    [Theory]
    [InlineData("2.1.000")]                 // no v
    [InlineData("v2.1")]                    // not three parts
    [InlineData("v2.1.000-")]               // empty label
    [InlineData("v2.1.000-1")]              // a label must start with a letter
    [InlineData("v2.1.000-RC 1")]           // whitespace
    [InlineData("v2.1.000-RC;rm")]          // shell metacharacters never pass
    [InlineData("v2.1.000-RC--1")]          // an empty label part
    [InlineData("v2.1.000+abc")]
    public void B195_malformed_tags_are_still_refused(string tag)
    {
        Assert.False(ReleaseTagPattern().IsMatch(tag), $"release.yml accepts the malformed tag {tag}");
    }

    [Fact]
    public void B195_a_candidate_is_a_prerelease_and_pushes_no_package_or_image()
    {
        string rel = ReadRepo(".github/workflows/release.yml");
        Assert.Contains("prerelease: ${{ steps.version.outputs.prerelease }}", rel, StringComparison.Ordinal);
        Assert.Contains("--title \"GridSim $VERSION $LABEL\" --notes-file notes.md --verify-tag --prerelease", rel, StringComparison.Ordinal);
        // Both the image push and the NuGet call are skipped for a candidate.
        Assert.Contains("    needs: [ verify, release ]\n    # Bug #195: a release candidate does not take the final version's image tag in the registry.\n    if: needs.verify.outputs.prerelease == 'false'\n", rel, StringComparison.Ordinal);
        Assert.Contains("    needs: [ verify, release, sbom ]\n    # Bug #195: a release candidate does not take the final version's package version in the feed.\n    if: needs.verify.outputs.prerelease == 'false'\n", rel, StringComparison.Ordinal);
        // The tag-equals-version gate is still there, on the numeric part.
        Assert.Contains("tag_version=\"${BASH_REMATCH[1]}\"", rel, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$tag_version\" != \"$props_version\" ]]; then", rel, StringComparison.Ordinal);

        string pub = ReadRepo(".github/workflows/publish.yml");
        Assert.Contains("if: (github.event_name == 'release' && github.event.release.prerelease != true) || github.event_name == 'workflow_dispatch'", pub, StringComparison.Ordinal);
        Assert.Contains("is a release-candidate tag: mark the GitHub Release as a pre-release", pub, StringComparison.Ordinal);
    }
}
