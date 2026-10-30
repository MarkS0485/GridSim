using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GridSim.Core.Diagnostics;
using GridSim.Core.Diagnostics.Incident;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The audit controls as the product paths use them (AuditRunRecorder with AuditRecorderOptions, which the CLI's
/// --audit-forward / --incident-log / --audit-sign-key / --audit-verify-key and the web host's Audit:* settings build):
/// one entry per security event, forwarded to a JSON-lines copy; significant events and bursts raised as incidents; the
/// anchor signed after every entry and checked before the next run, so a rewrite of log and anchor together is refused.
/// </summary>
public sealed class AuditWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-auditwiring-{Guid.NewGuid():N}");

    public AuditWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void A_security_event_is_its_own_entry_and_is_forwarded_and_the_log_still_verifies()
    {
        var forward = new JsonLinesAuditSink(P("forward.jsonl"));
        var options = new AuditRecorderOptions { Sinks = new IAuditSink[] { forward } };
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "serve",
            ProvenanceTag.DerivedFromRecorded, null, options);
        run.RecordSecurityEvent(SecurityEventKind.HorizonViolation, "window w1.json refused:\nnewer than the horizon");
        run.Finish(0);

        AuditLogVerification v = AuditLogFile.Verify(P("audit.jsonl"));
        Assert.True(v.IsValid, v.Reason);
        Assert.Equal(2, v.VerifiedCount);   // the security entry, then the run entry
        Assert.True(SecurityAudit.TryRead(v.Entries[0], out SecurityEventKind kind, out SecuritySeverity sev));
        Assert.Equal(SecurityEventKind.HorizonViolation, kind);
        Assert.Equal(SecuritySeverity.Info, sev);   // the kind's baseline (IncidentPolicy.Classify)
        Assert.DoesNotContain('\n', v.Entries[0].Summary);   // a line break in the reason is folded, never fails the record
        Assert.Equal(1, run.SecurityEventCount);

        // The forwarded copy holds the same two entries, with the same hashes.
        string[] lines = File.ReadAllLines(P("forward.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Equal(JsonLinesAuditSink.Format(v.Entries[0]), lines[0]);
        Assert.Equal(JsonLinesAuditSink.Format(v.Entries[1]), lines[1]);
        Assert.Null(forward.LastError);
    }

    [Fact]
    public void A_significant_event_raises_one_incident_and_a_routine_one_raises_none()
    {
        var incidents = new BufferedIncidentSink();
        var options = new AuditRecorderOptions { Sinks = IncidentWiring.AuditSinksFor(incidents) };
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, options);
        AuditLogEntry fdia = run.RecordSecurityEvent(SecurityEventKind.SuspectedFdia, "FDIA screen: suspected");
        run.RecordSecurityEvent(SecurityEventKind.InfeasibleState, "estimate infeasible");   // Low: no single incident
        run.Finish(3);

        IncidentReport report = Assert.Single(incidents.Reports);
        Assert.Equal(IncidentCategory.SuspectedFdia, report.Category);
        Assert.Equal(SecuritySeverity.Significant, report.Severity);
        Assert.Equal(fdia.Hash, report.AuditHash);   // the incident names the entry that evidences it
        Assert.Equal(fdia.RunTimestampUtc, report.RaisedUtc);   // deterministic: the entry's own stamp
    }

    [Fact]
    public void A_burst_of_routine_events_is_escalated_once()
    {
        var incidents = new BufferedIncidentSink();
        var options = new AuditRecorderOptions { Sinks = IncidentWiring.AuditSinksFor(incidents) };
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "serve",
            ProvenanceTag.DerivedFromRecorded, null, options);
        int burst = IncidentPolicy.Default.HorizonViolationBurst;
        for (int i = 0; i < burst + 5; i++)
            run.RecordSecurityEvent(SecurityEventKind.HorizonViolation, $"window w{i}.json refused");
        run.Finish(0);

        IncidentReport report = Assert.Single(incidents.Reports);
        Assert.Equal(IncidentCategory.HorizonViolation, report.Category);
        Assert.Equal(SecuritySeverity.Significant, report.Severity);
    }

    [Fact]
    public void The_incident_file_sink_writes_one_json_line_per_incident()
    {
        var file = new JsonLinesIncidentSink(P("incidents.jsonl"));
        var options = new AuditRecorderOptions { Sinks = IncidentWiring.AuditSinksFor(file) };
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, options);
        run.RecordSecurityEvent(SecurityEventKind.SuspectedFdia, "FDIA screen: suspected");
        run.Finish(3);

        string line = Assert.Single(File.ReadAllLines(P("incidents.jsonl")));
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal("SuspectedFdia", doc.RootElement.GetProperty("category").GetString());
        Assert.Equal("SuspectedFdia", doc.RootElement.GetProperty("detail").GetProperty("kind").GetString());
        Assert.Equal(1, file.Published);
        Assert.Null(file.LastError);
    }

    [Fact]
    public void A_signed_anchor_is_written_after_every_entry_and_verifies_on_the_next_run()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa pub = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        var signing = new AuditRecorderOptions { AnchorSigningKey = key, AnchorVerificationKey = pub };

        AuditRunRecorder first = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, signing);
        Assert.True(first.SignsAnchor);
        first.Finish(0);
        Assert.True(File.Exists(SignedAuditAnchor.DefaultPathFor(P("audit.jsonl"))));

        // The next run opens the log only after the anchor's signature verified.
        AuditRunRecorder second = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, signing);
        second.Finish(0);
        Assert.Equal(2, AuditLogFile.Verify(P("audit.jsonl")).VerifiedCount);
    }

    [Fact]
    public void A_joint_rewrite_of_log_and_anchor_without_the_key_is_refused_at_start()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa pub = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        string log = P("audit.jsonl");
        AuditRunRecorder run = AuditRunRecorder.Start(log, "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, new AuditRecorderOptions { AnchorSigningKey = key });
        run.Finish(0);

        // An attacker without the key rewrites the log as a fresh, self-consistent chain and rewrites the anchor to
        // match: the plain chain and anchor checks pass, but the signature names the old anchor.
        File.Delete(log);
        File.Delete(AuditAnchorFile.DefaultPathFor(log));
        AuditRunRecorder forged = AuditRunRecorder.Start(log, "intruder@host", "estimate", ProvenanceTag.DerivedFromRecorded);
        forged.Finish(0);

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(log, "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, new AuditRecorderOptions { AnchorVerificationKey = pub }));
        Assert.Contains("different count or head hash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_signature_is_refused_when_a_verification_key_is_configured()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa pub = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        string log = P("audit.jsonl");
        AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded).Finish(0);

        var ex = Assert.Throws<AuditLogTamperException>(() => AuditRunRecorder.Start(log, "analyst@workstation", "estimate",
            ProvenanceTag.DerivedFromRecorded, null, new AuditRecorderOptions { AnchorVerificationKey = pub }));
        Assert.Contains("no signature file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_load_from_pem_and_a_public_key_is_refused_as_a_signing_key()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(P("private.pem"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(P("public.pem"), key.ExportSubjectPublicKeyInfoPem());

        using ECDsa loadedPrivate = AuditRecorderOptions.LoadKey(P("private.pem"), requirePrivate: true);
        using ECDsa loadedPublic = AuditRecorderOptions.LoadKey(P("public.pem"), requirePrivate: false);
        Assert.Throws<InvalidDataException>(() => AuditRecorderOptions.LoadKey(P("public.pem"), requirePrivate: true));

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        File.WriteAllText(P("p384.pem"), p384.ExportPkcs8PrivateKeyPem());
        Assert.Throws<InvalidDataException>(() => AuditRecorderOptions.LoadKey(P("p384.pem"), requirePrivate: true));
        Assert.Throws<FileNotFoundException>(() => AuditRecorderOptions.LoadKey(P("absent.pem"), requirePrivate: false));
    }

    [Fact]
    public void A_security_event_after_the_run_is_recorded_is_refused()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(P("audit.jsonl"), "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded);
        run.Finish(0);
        Assert.Throws<InvalidOperationException>(() => run.RecordSecurityEvent(SecurityEventKind.SuspectedFdia, "late"));
    }
}
