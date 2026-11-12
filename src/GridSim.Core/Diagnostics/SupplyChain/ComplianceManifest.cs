using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics.SupplyChain;

/// <summary>The assurance status of a control, on the CAF's own scale plus the honest fourth band for outcomes a
/// component does not own.</summary>
public enum ComplianceStatus
{
    /// <summary>An implemented control substantially meets the outcome and a named test or artefact evidences it.</summary>
    Achieved = 0,

    /// <summary>A control exists but has a named, material gap.</summary>
    PartiallyAchieved = 1,

    /// <summary>The outcome is in scope and there is no control yet.</summary>
    NotAchieved = 2,

    /// <summary>The outcome belongs to the deploying party's environment, not the engine.</summary>
    OutOfScope = 3,
}

/// <summary>One control GridSim attests it provides, mapped to a CAF v4.0 principle and the test or artefact that
/// evidences it. The machine-readable twin of a row in the <c>caf-mapping.md</c> traceability matrix.</summary>
/// <param name="Id">A short stable id, for example <c>guard</c> or <c>rbac</c>.</param>
/// <param name="Principle">The CAF v4.0 principle/outcome, for example <c>B2</c> or <c>A4.b</c>.</param>
/// <param name="Description">What the control does, in one line.</param>
/// <param name="EvidenceTest">The test class (in GridSim.Tests) or build artefact that evidences it.</param>
/// <param name="Status">Its assurance status.</param>
public sealed record ComplianceControl(
    string Id,
    string Principle,
    string Description,
    string EvidenceTest,
    ComplianceStatus Status);

/// <summary>
/// The compliance manifest: the inventory of controls a GridSim build attests it provides, each bound to the
/// test or artefact that evidences it and to a CAF v4.0 principle (NCSC CAF A4.b, "attest to the authenticity and
/// integrity of software"). It is embedded in the build (it is this code) and hashed into the
/// <see cref="ComplianceAttestation"/>, so a running binary can prove which control set it was built to provide.
///
/// <para>The statuses are maintained BY HAND, here, from what the product paths actually do: a control is Achieved only
/// when a shipped path (a CLI verb, the web host) exercises it and a test evidences that path; a primitive that no host
/// uses yet is PartiallyAchieved with the gap in its description. The gate then refuses to call a build compliant until
/// the gap is closed - the same doctrine the estimator applies when it reports <c>Unobservable</c> rather than a
/// plausible lie. A release build is produced only after the full test matrix passed, so every named evidencing test
/// was green for it (<see cref="ComplianceAttestation"/>); <c>ComplianceAttestationTests</c> checks every named test
/// class exists and pins exactly which controls are not yet Achieved, so a status change is always deliberate.</para>
/// </summary>
public static class ComplianceManifest
{
    /// <summary>The manifest schema tag, bumped if the serialised shape changes.</summary>
    public const string SchemaTag = "gridsim-compliance/1";

    /// <summary>The controls this build attests it provides. Ordered; the order is part of the manifest hash.</summary>
    public static IReadOnlyList<ComplianceControl> Controls { get; } = new[]
    {
        new ComplianceControl("guard", "Foundational/D1",
            "Forward-inference 169h data boundary at every dated ingestion seam",
            "ForwardInferenceGuardTests, RestrictionInvariantTests", ComplianceStatus.Achieved),
        new ComplianceControl("restriction-review", "A1/A2",
            "Dynamics-to-estimation fusion gated by a code-resident reviewed register",
            "RestrictionInvariantTests", ComplianceStatus.Achieved),
        new ComplianceControl("physics-audit", "A2/B4/C1",
            "Physics-conformance audit rejects physically impossible converged states",
            "PhysicsAuditTests, FeasibleKclGateTests", ComplianceStatus.Achieved),
        new ComplianceControl("provenance", "A3/B3",
            "Trust-class labelling of every emitted data series",
            "AssetProvenanceTests", ComplianceStatus.Achieved),
        new ComplianceControl("distrusted-source", "D1",
            "A measurement source can be marked distrusted (estimate and serve --distrust, web Realtime:DistrustedSources): its rows are forced to pseudo-measurement treatment and left out of the chi-square redundancy, with one DistrustedSource security event per source per run (held at Partially achieved until the release test matrix has run the wiring tests)",
            "DistrustedSourceTests, DistrustedSourceRedundancyTests, DistrustedSourceSettingTests", ComplianceStatus.PartiallyAchieved),
        new ComplianceControl("audit-chain", "C1",
            "Hash-chained, tamper-evident audit trail, persisted and anchored",
            "AuditLogTests, AuditLogFileTests, Doc3AuditAnchorTests", ComplianceStatus.Achieved),
        new ComplianceControl("audit-per-event", "C1",
            "One audit entry per security event at the detection sites of the audited CLI verbs and the web resident estimator, forwardable to a monitoring sink (--audit-forward, Audit:ForwardPath)",
            "SecurityAuditTests, AuditWiringTests", ComplianceStatus.Achieved),
        new ComplianceControl("audit-signed-anchor", "C1",
            "Detached signature over the audit anchor, written after every entry and checked before a run (--audit-sign-key / --audit-verify-key, audit verify --pubkey, Audit:AnchorSigningKeyPath / Audit:AnchorVerificationKeyPath)",
            "SignedAuditAnchorTests, AuditWiringTests", ComplianceStatus.Achieved),
        new ComplianceControl("security-monitor", "C2",
            "Aggregates the audit trail and escalates bursts of security events to one incident (attached with --incident-log, Audit:IncidentLogPath)",
            "SecurityEventMonitorTests, AuditWiringTests", ComplianceStatus.Achieved),
        new ComplianceControl("incident-router", "C1/D1",
            "Significant detections are raised as structured, restriction-safe incidents to a reporting file (--incident-log, Audit:IncidentLogPath)",
            "IncidentRouterTests, AuditWiringTests", ComplianceStatus.Achieved),
        new ComplianceControl("fdia-screen", "C1/C2",
            "Residual-cloaked FDIA detection, including PMU-anchored feasible-point checks",
            "FdiaScreenTests, FdiaCloakedAttackTests, FdiaProtectedAnchorTests", ComplianceStatus.Achieved),
        new ComplianceControl("rbac", "B2",
            "Role-based access-control gate (RoleGuard) enforced by the web host on POST /api/solve and every /ws/sim control, with denials audited through the host's audit log (partial: the Blazor dashboard's own sessions are not role-gated, and a host without the resident estimator has no audit log, so its denials are refused but not audited; replay-only remains the production control)",
            "RoleGuardTests, CafB2RoleGateWebTests", ComplianceStatus.PartiallyAchieved),
        new ComplianceControl("secrets", "B3",
            "Secret-store seam with a one-file-per-secret directory backend (Secrets:Directory) for the key-ring certificate password; no secret in the audit log",
            "SecretStoreTests, NoSecretInAuditTests, DirectorySecretStoreTests", ComplianceStatus.Achieved),
        new ComplianceControl("resilience", "B4/B5",
            "Resource limits and transport policy on every bridge client, an availability probe whose SLO breach is a security event, and a supervised bridge reconnect on the bounded backoff schedule (off by default)",
            "ResilienceOptionsTests, HealthProbeTests, BridgeEndpointSettingTests, BridgeReconnectSupervisorTests, BridgeReconnectHostWiringTests", ComplianceStatus.PartiallyAchieved),
        new ComplianceControl("build-hardening", "B4",
            "Zero-warning build gate: warnings-as-errors, analysers, nullable",
            "build gate (Directory.Build.props)", ComplianceStatus.Achieved),
        new ComplianceControl("determinism", "D1",
            "Bit-reproducible estimate and replay for forensic re-derivation",
            "EstimatorDeterminismTests, GdaReplayDeterminismTests, EstimationClockIndependenceTests", ComplianceStatus.Achieved),
        // Bug #196: these two were recorded Achieved, but their descriptions claimed things that were not true of the
        // release actually published (2.1.000 RC-1 was built, signed and published by hand, outside release.yml, so it
        // passed neither the CI-matrix gate nor the in-workflow SBOM injection nor the provenance attestation). The
        // manifest is an inventory maintained in source; it must say what holds, so both are PartiallyAchieved, with the
        // gap named in brackets as for every other partial control, until a release is made through release.yml.
        new ComplianceControl("supply-chain", "A3/A4",
            "Per-project lock files restored in locked mode on CI with the SDK pinned by global.json, per-release SBOMs injected into the shipped builds, vulnerable-package gate, signing pinned to the (self-signed) release certificate, signed build-provenance attestation of every release file (release.yml only: no published release has yet been made through it, so no published file carries the attestation or an in-workflow SBOM hash)",
            "WorkflowContractTests, ThirdPartyNoticesTests, SupplyChainManifestTests, WorkflowReleaseGateTests", ComplianceStatus.PartiallyAchieved),
        new ComplianceControl("secure-sdlc-attestation", "A4.b",
            "Compliance attestation bound to the build identity (version, commit, SDK, SBOM); only a release build (a 64-hex SBOM hash, a known commit and a clean source tree) can attest compliance; download integrity via the provenance attestation (a release build does not by itself prove the full test matrix passed: only a release made through release.yml, which gates on the matrix, does, and none has been published yet)",
            "ComplianceAttestationTests", ComplianceStatus.PartiallyAchieved),
    };

    /// <summary>The controls GridSim owns and must therefore have Achieved for a build to be called compliant
    /// (everything that is not deploying-party / out of scope).</summary>
    public static IEnumerable<ComplianceControl> InScope =>
        Controls.Where(c => c.Status != ComplianceStatus.OutOfScope);

    /// <summary>True when every in-scope control is <see cref="ComplianceStatus.Achieved"/>.</summary>
    public static bool AllInScopeAchieved => InScope.All(c => c.Status == ComplianceStatus.Achieved);

    /// <summary>The canonical one-line-per-control serialisation the manifest hash is taken over.</summary>
    public static string Serialise()
    {
        var sb = new StringBuilder();
        sb.Append(SchemaTag).Append('\n');
        foreach (ComplianceControl c in Controls)
            sb.Append(c.Id).Append('|').Append(c.Principle).Append('|').Append(c.Description).Append('|')
              .Append(c.EvidenceTest).Append('|').Append(c.Status).Append('\n');
        return sb.ToString();
    }

    /// <summary>SHA-256 over <see cref="Serialise"/>, the manifest's identity. Deterministic across runs and OSes.</summary>
    public static string Hash { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialise())));
}
