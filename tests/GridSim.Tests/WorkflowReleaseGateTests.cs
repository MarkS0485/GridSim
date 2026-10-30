using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The release gates the workflows promise, pinned so an edit cannot quietly drop one: the release waits for the whole
/// CI matrix (Linux, Windows and Apple-silicon macOS, every SIMD mode including AVX-512 forced and FMA off, the latter
/// through the avx2-off leg) and no leg
/// is advisory; the SBOMs are made first and their hashes injected into every shipped build, which must then attest a
/// release build; the release files carry a signed provenance attestation; and signing trusts exactly the pinned
/// self-signed certificate.
/// </summary>
public class WorkflowReleaseGateTests
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
    public void Ci_is_callable_has_no_tag_trigger_of_its_own_and_no_advisory_leg()
    {
        string ci = Read(".github/workflows/ci.yml");
        Assert.Contains("on:\n  workflow_call:\n  workflow_dispatch:\n", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("tags: [ 'v*' ]", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", ci, StringComparison.Ordinal);
    }

    // Bug #47 (2026-10-26). This test used to pin a separate `fma-off` leg (`{ os: ubuntu-latest,  simd: fma-off }`,
    // `{ os: windows-latest, simd: fma-off }`, `export DOTNET_EnableFMA=0`). That leg verified nothing: the .NET 10
    // runtime ignores DOTNET_EnableFMA=0 (simdcheck still reports Fma=True and the normal tier; measured on 10.0.12 and
    // 10.0.13), so it re-ran the normal leg, and its own preflight (want "Fma=False") could never pass. On .NET 10 FMA is
    // only turned off with the x86-64-v3 group, by DOTNET_EnableAVX2=0 (simdcheck: Avx2=False Fma=False, tier Sse128).
    // So the non-FMA axis is held by the avx2-off leg, and its preflight now REQUIRES Fma=False, which nothing asserted
    // before. The pins below change from "a leg named fma-off exists" to "the avx2-off leg exists on both operating
    // systems, exports DOTNET_EnableAVX2=0 and must report Fma=False", and the dead switch must not come back. This
    // strengthens what is verified: the non-FMA kernels were only ever exercised by avx2-off, and now that is asserted.
    [Fact]
    public void Ci_matrix_covers_macos_avx512_and_the_non_fma_axis()
    {
        string ci = Read(".github/workflows/ci.yml");
        Assert.Contains("{ os: macos-latest,   simd: normal }", ci, StringComparison.Ordinal);
        Assert.Contains("{ os: macos-latest,   simd: hwintrinsic-off }", ci, StringComparison.Ordinal);
        Assert.Contains("{ os: ubuntu-latest,  simd: avx512 }", ci, StringComparison.Ordinal);
        Assert.Contains("{ os: windows-latest, simd: avx512 }", ci, StringComparison.Ordinal);
        Assert.Contains("os: [ ubuntu-latest, windows-latest ]", ci, StringComparison.Ordinal);
        Assert.Contains("simd: [ normal, avx2-off, hwintrinsic-off ]", ci, StringComparison.Ordinal);
        Assert.Contains("export DOTNET_PreferredVectorBitWidth=512", ci, StringComparison.Ordinal);
        Assert.Contains("[[ \"$SIMD\" == \"avx2-off\" ]] && export DOTNET_EnableAVX2=0", ci, StringComparison.Ordinal);
        Assert.Contains("[[ \"$SIMD\" == \"avx2-off\" ]] && want=\"Avx2=False\" && want2=\"Fma=False\"", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("DOTNET_EnableFMA", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("simd: fma-off", ci, StringComparison.Ordinal);
        Assert.Contains("simdcheck --expect Avx512", ci, StringComparison.Ordinal);
        Assert.Contains("export GRIDSIM_TEST_MAX_BUSES=all", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_publishing_job_waits_for_the_ci_matrix()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("  ci:\n    name: CI matrix (release gate)\n    needs: verify\n    uses: ./.github/workflows/ci.yml\n", rel, StringComparison.Ordinal);
        Assert.Contains("    name: CLI ${{ matrix.rid }}\n    needs: [ verify, ci, sbom ]\n", rel, StringComparison.Ordinal);
        Assert.Contains("    name: Web container image (replay-only)\n    needs: [ verify, ci, sbom ]\n", rel, StringComparison.Ordinal);
        Assert.Contains("    needs: [ verify, ci, cli, web-image, sbom ]\n", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sbom_hash_is_injected_into_every_shipped_build_and_checked()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("-p:GridSimSbomSha256=\"$SBOM_SHA256\"", rel, StringComparison.Ordinal);
        Assert.Contains("--build-arg SBOM_SHA256=\"$SBOM_SHA256\"", rel, StringComparison.Ordinal);
        Assert.Contains("sbom-sha256: ${{ needs.sbom.outputs.cli_sha256 }}", rel, StringComparison.Ordinal);
        Assert.Contains("compliance verify", rel, StringComparison.Ordinal);
        Assert.Contains("^  release build  yes", rel, StringComparison.Ordinal);
        Assert.Contains("-p:GridSimSbomSha256=\"${SBOM_SHA256}\"", Read("Dockerfile"), StringComparison.Ordinal);
        string pub = Read(".github/workflows/publish.yml");
        Assert.Contains("sbom_arg=(-p:GridSimSbomSha256=\"$SBOM_SHA256\")", pub, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_files_get_a_signed_provenance_attestation()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("uses: actions/attest-build-provenance@", rel, StringComparison.Ordinal);
        Assert.Contains("subject-path: 'dist/*'", rel, StringComparison.Ordinal);
        Assert.Contains("id-token: write", rel, StringComparison.Ordinal);
        Assert.Contains("attestations: write", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_trusts_exactly_the_pinned_release_certificate()
    {
        const string thumb = "86DD43B731D2B8A5133972F7B7C91C0FDA8EB721";
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains($"$pinned = '{thumb}'", rel, StringComparison.Ordinal);
        Assert.Contains("Import-Certificate -FilePath signing/gridsim-signing.cer -CertStoreLocation Cert:\\LocalMachine\\Root", rel, StringComparison.Ordinal);
        Assert.Contains("$sig.SignerCertificate.Thumbprint -ne $pinned", rel, StringComparison.Ordinal);
        string pub = Read(".github/workflows/publish.yml");
        Assert.Contains($"[[ \"$thumb\" == \"{thumb}\" ]]", pub, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget verify --all \"$p\" --certificate-fingerprint \"$fp\"", pub, StringComparison.Ordinal);
        Assert.Contains(thumb, Read("signing/README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_attests_its_sbom_and_gets_a_provenance_attestation_in_the_registry()
    {
        string rel = Read(".github/workflows/release.yml");
        Assert.Contains("curl -fsS http://127.0.0.1:5540/compliance -o compliance.json", rel, StringComparison.Ordinal);
        Assert.Contains("c.get(\"releaseBuild\") is not True or c.get(\"sbomSha256\") != sys.argv[1]", rel, StringComparison.Ordinal);
        Assert.Contains("subject-digest: ${{ steps.push.outputs.digest }}", rel, StringComparison.Ordinal);
        Assert.Contains("push-to-registry: true", rel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_build_sdk_is_stamped_for_the_build_identity()
    {
        Assert.Contains("<AssemblyMetadata Include=\"GridSimBuildSdk\" Value=\"$(NETCoreSdkVersion)\" />",
            Read("Directory.Build.props"), StringComparison.Ordinal);
    }
}
