using System;
using System.Linq;
using GridSim.Core;
using GridSim.Core.Diagnostics.SupplyChain;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Build provenance (NCSC CAF A3/A4): a running binary attests how it was built. The version matches the single
/// repository version; the build hash is a 64-character upper-case hex digest; a local build carries no injected
/// SBOM hash (so it is not a release build); and the deterministic build hash is stable for the same inputs and
/// changes when the SBOM changes.
/// </summary>
public class SupplyChainManifestTests
{
    [Fact]
    public void The_provenance_version_matches_the_single_repository_version()
    {
        Assert.Equal(BuildInfo.Version, BuildProvenance.Current.Version);
    }

    [Fact]
    public void The_build_hash_is_sixty_four_upper_case_hex_characters()
    {
        AssertUpperHex64(BuildProvenance.Current.BuildHash);
    }

    [Fact]
    public void A_local_build_has_no_injected_sbom_and_is_not_a_release_build()
    {
        Assert.Equal(BuildProvenance.SbomUnset, BuildProvenance.Current.SbomSha256);
        Assert.False(BuildProvenance.Current.IsReleaseBuild);
    }

    [Fact]
    public void Compute_build_hash_is_deterministic_for_the_same_inputs()
    {
        string a = BuildProvenance.ComputeBuildHash("1.95.295", "abc", ".NET 10.0.0", "unset");
        string b = BuildProvenance.ComputeBuildHash("1.95.295", "abc", ".NET 10.0.0", "unset");
        Assert.Equal(a, b);
        AssertUpperHex64(a);
    }

    [Fact]
    public void Compute_build_hash_differs_when_the_sbom_differs()
    {
        string withUnset = BuildProvenance.ComputeBuildHash("1.95.295", "abc", ".NET 10.0.0", "unset");
        string withSbom = BuildProvenance.ComputeBuildHash("1.95.295", "abc", ".NET 10.0.0", new string('A', 64));
        Assert.NotEqual(withUnset, withSbom);
    }

    private static void AssertUpperHex64(string hash)
    {
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')),
            $"'{hash}' is not 64 upper-case hexadecimal characters");
    }
}
