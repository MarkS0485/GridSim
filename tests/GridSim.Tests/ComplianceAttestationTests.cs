using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Diagnostics.SupplyChain;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The compliance attestation and its manifest (NCSC CAF A4.b). The manifest hash is deterministic over its
/// serialisation; exactly the controls with a named gap are not Achieved (pinned, so a status change is deliberate);
/// a local build never attests compliance, and a release build does only when every in-scope control is Achieved; and
/// the manifest is self-verifying: every test class an in-scope control names as its evidence must exist here.
///
/// Ruling (2026-10-15, the review gap closure): three tests here used to assert the hard-coded Achieved literals back
/// (every control Achieved, the running build compliant, the report saying COMPLIANT), which made the verdict a
/// constant. They are replaced by the tests below, which check the verdict's real inputs.
/// </summary>
public class ComplianceAttestationTests
{
    [Fact]
    public void The_manifest_hash_is_sixty_four_hex_and_equals_a_recomputation_over_serialise()
    {
        string hash = ComplianceManifest.Hash;
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')),
            $"'{hash}' is not 64 upper-case hexadecimal characters");

        string recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ComplianceManifest.Serialise())));
        Assert.Equal(hash, recomputed);
    }

    [Fact]
    public void Exactly_the_controls_with_a_named_gap_are_not_achieved()
    {
        // Each of these is a primitive no shipped path exercises yet; its description names the gap. Closing a gap (or
        // opening one) must change this list on purpose.
        string[] notAchieved = ComplianceManifest.InScope
            .Where(c => c.Status != ComplianceStatus.Achieved).Select(c => c.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "distrusted-source", "rbac", "resilience" }, notAchieved);
        Assert.False(ComplianceManifest.AllInScopeAchieved);
        foreach (ComplianceControl c in ComplianceManifest.InScope.Where(c => c.Status != ComplianceStatus.Achieved))
            Assert.Contains("(", c.Description, StringComparison.Ordinal);   // the gap is stated in the description
    }

    [Fact]
    public void A_local_build_never_attests_compliance()
    {
        ComplianceAttestation attestation = ComplianceAttestation.Current;
        Assert.False(attestation.IsReleaseBuild);   // the test build is a local build: no SBOM hash injected
        Assert.False(attestation.IsCompliant);
        Assert.Equal(ComplianceManifest.Hash, attestation.ManifestHash);
        string report = attestation.Report();
        Assert.Contains("NOT ATTESTED (not a release build", report, StringComparison.Ordinal);
        Assert.Contains("  not achieved   rbac (B2): PartiallyAchieved", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_needs_a_release_build_and_every_in_scope_control_achieved()
    {
        static ComplianceAttestation With(int inScope, int achieved, bool release) =>
            new("1.95.297", "abc", new string('A', 64), release ? new string('B', 64) : BuildProvenance.SbomUnset,
                new string('C', 64), inScope, achieved, release);

        Assert.True(With(18, 18, release: true).IsCompliant);
        Assert.False(With(18, 18, release: false).IsCompliant);   // a local build, whatever the manifest says
        Assert.False(With(18, 15, release: true).IsCompliant);    // a control not achieved
        Assert.False(With(0, 0, release: true).IsCompliant);      // an empty manifest attests nothing
        Assert.Contains("NOT COMPLIANT (in-scope controls not all Achieved)", With(18, 15, release: true).Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The manifest is self-verifying: for every in-scope control, each evidence token that names a test class (ends
    /// with "Tests", and is not a build-artefact phrase containing a bracket or a space) must correspond to a real
    /// type in this test assembly. A named test that does not exist is a manifest error and fails here by design.
    /// </summary>
    [Fact]
    public void Every_named_evidence_test_class_exists_in_the_test_assembly()
    {
        var presentTypeNames = Assembly.GetExecutingAssembly().GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (ComplianceControl control in ComplianceManifest.InScope)
        {
            foreach (string raw in control.EvidenceTest.Split(','))
            {
                string token = raw.Trim();
                // Only tokens that name a test class: skip build-artefact phrases (a bracket or a space-separated phrase).
                if (!token.EndsWith("Tests", StringComparison.Ordinal)) continue;
                if (token.Contains('(') || token.Contains(' ')) continue;
                if (!presentTypeNames.Contains(token))
                    missing.Add($"{control.Id} -> {token}");
            }
        }

        Assert.True(missing.Count == 0,
            "The compliance manifest names evidence test classes that do not exist in the test assembly: " +
            string.Join("; ", missing));
    }
}
