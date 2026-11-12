using System;
using System.IO;
using System.Security.Cryptography;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-374 (November 2026): a v1 anchor signature (<c>gridsim-audit-anchor-sig/1</c>) binds the entry count and head hash
/// only. The v2 form (<c>gridsim-audit-anchor-sig/2 count= head= first= signed= sig=</c>) also binds the hash of the log's
/// first entry and the signing time (Unix seconds), and the signed bytes are the whole line before <c> sig=</c>. The run
/// recorder signs in v2 from now on; a v1 signature still verifies, with a warning, and is replaced at the run's end.
/// What the first-entry hash adds is an explicit log identity (the head hash already chains back to it, so it is a
/// belt-and-braces check); what the time adds is the means to see that an older log and its older signed anchor were
/// restored: the signature then predates the operator's last signing.
/// </summary>
public sealed class Gr374SignedAnchorV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-gr374-{Guid.NewGuid():N}");

    public Gr374SignedAnchorV2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static readonly DateTimeOffset Signed = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly string First = new('B', 64);
    private static readonly AuditAnchor Anchor = new(3, new string('A', 64));

    [Fact]
    public void A_v2_line_names_every_field_and_verifies_strictly()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string line = SignedAuditAnchor.SignV2(Anchor, First, Signed, key);
        Assert.StartsWith($"{SignedAuditAnchor.FormatTagV2} count=3 head={Anchor.HeadHash} first={First} signed={Signed.ToUnixTimeSeconds()} sig=", line, StringComparison.Ordinal);

        Assert.True(SignedAuditAnchor.VerifyStrict(Anchor, First, line, key, out string reason, out bool legacy, out DateTimeOffset? at), reason);
        Assert.False(legacy);
        Assert.Equal(Signed, at);
        // The count-and-head check verifies a v2 line too (everything but the log identity).
        Assert.True(SignedAuditAnchor.Verify(Anchor, line, key, out string r2), r2);
    }

    [Fact]
    public void A_v2_signature_for_another_log_or_with_an_edited_field_is_refused()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string line = SignedAuditAnchor.SignV2(Anchor, First, Signed, key);

        Assert.False(SignedAuditAnchor.VerifyStrict(Anchor, new string('C', 64), line, key, out string other, out _, out _));
        Assert.Contains("different log", other, StringComparison.Ordinal);

        // Moving the signing time forward (to hide a restore) breaks the signature.
        string later = line.Replace($"signed={Signed.ToUnixTimeSeconds()}", $"signed={Signed.ToUnixTimeSeconds() + 86400}", StringComparison.Ordinal);
        Assert.False(SignedAuditAnchor.VerifyStrict(Anchor, First, later, key, out string edited, out _, out _));
        Assert.Contains("invalid", edited, StringComparison.Ordinal);
        Assert.False(SignedAuditAnchor.Verify(Anchor, later, key, out _));

        Assert.False(SignedAuditAnchor.VerifyStrict(new AuditAnchor(4, Anchor.HeadHash), First, line, key, out string count, out _, out _));
        Assert.Contains("different count or head hash", count, StringComparison.Ordinal);
        Assert.False(SignedAuditAnchor.VerifyStrict(Anchor, First, line.Replace(" signed=", " stamped=", StringComparison.Ordinal), key, out _, out _, out _));
        Assert.False(SignedAuditAnchor.VerifyStrict(Anchor, First, "", key, out _, out _, out _));
    }

    [Fact]
    public void The_first_hash_must_be_genesis_exactly_for_an_empty_log()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => SignedAuditAnchor.SignV2(Anchor, AuditLog.Genesis, Signed, key));
        Assert.Throws<ArgumentException>(() => SignedAuditAnchor.SignV2(new AuditAnchor(0, AuditLog.Genesis), First, Signed, key));
        Assert.Throws<ArgumentException>(() => SignedAuditAnchor.SignV2(Anchor, "not-a-hash", Signed, key));
        string empty = SignedAuditAnchor.SignV2(new AuditAnchor(0, AuditLog.Genesis), AuditLog.Genesis, Signed, key);
        Assert.True(SignedAuditAnchor.VerifyStrict(new AuditAnchor(0, AuditLog.Genesis), AuditLog.Genesis, empty, key, out string r, out _, out _), r);
    }

    [Fact]
    public void A_v1_line_still_verifies_and_is_reported_as_legacy()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string v1 = SignedAuditAnchor.Sign(Anchor, key);
        Assert.True(SignedAuditAnchor.VerifyStrict(Anchor, First, v1, key, out string reason, out bool legacy, out DateTimeOffset? at), reason);
        Assert.True(legacy);
        Assert.Null(at);
    }

    [Fact]
    public void The_writer_knows_its_first_entry_across_reopening()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        string first;
        using (AuditLogWriter w = AuditLogWriter.Open(path))
        {
            Assert.Equal(AuditLog.Genesis, w.FirstHash);
            AuditLogEntry e0 = w.Append(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "one");
            first = e0.Hash;
            Assert.Equal(first, w.FirstHash);
            w.Append(new DateTime(2026, 6, 1, 0, 1, 0, DateTimeKind.Utc), null, ProvenanceTag.DerivedFromRecorded, "two");
            Assert.Equal(first, w.FirstHash);
        }
        using (AuditLogWriter again = AuditLogWriter.Open(path))
            Assert.Equal(first, again.FirstHash);
        Assert.Equal(first, AuditLogFile.Verify(path).FirstHash);
    }

    [Fact]
    public void The_recorder_signs_in_v2_and_replaces_a_v1_signature_with_a_warning()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa pub = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        string log = Path.Combine(_dir, "audit.jsonl");
        var signing = new AuditRecorderOptions { AnchorSigningKey = key, AnchorVerificationKey = pub };

        AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded, null, signing).Finish(0);
        string sigPath = SignedAuditAnchor.DefaultPathFor(log);
        Assert.StartsWith(SignedAuditAnchor.FormatTagV2 + " ", File.ReadAllText(sigPath), StringComparison.Ordinal);

        // A v1 signature of the same anchor (as an older build wrote it): accepted with a warning, then replaced by v2.
        AuditAnchor anchor = AuditAnchorFile.ReadIfExists(AuditAnchorFile.DefaultPathFor(log))!;
        SignedAuditAnchor.Write(sigPath, anchor, key);
        AuditRunRecorder second = AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded, null, signing);
        Assert.NotNull(second.AnchorSignatureWarning);
        Assert.Contains("v1", second.AnchorSignatureWarning!, StringComparison.Ordinal);
        second.Finish(0);
        Assert.StartsWith(SignedAuditAnchor.FormatTagV2 + " ", File.ReadAllText(sigPath), StringComparison.Ordinal);

        AuditRunRecorder third = AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded, null, signing);
        Assert.Null(third.AnchorSignatureWarning);
        third.Finish(0);
        Assert.Equal(3, AuditLogFile.Verify(log).VerifiedCount);
    }

    [Fact]
    public void A_v2_signature_copied_from_another_log_is_refused_at_start()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa pub = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        string log = Path.Combine(_dir, "audit.jsonl");
        var signing = new AuditRecorderOptions { AnchorSigningKey = key, AnchorVerificationKey = pub };
        AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded, null, signing).Finish(0);
        AuditAnchor anchor = AuditAnchorFile.ReadIfExists(AuditAnchorFile.DefaultPathFor(log))!;

        // A signature over the same count and head that names another first entry (a key holder's slip, or a forged
        // field under a stolen key): the strict check at start refuses it.
        SignedAuditAnchor.WriteV2(SignedAuditAnchor.DefaultPathFor(log), anchor, new string('C', 64), DateTimeOffset.UtcNow, key);
        var ex = Assert.Throws<AuditLogTamperException>(() =>
            AuditRunRecorder.Start(log, "analyst@workstation", "estimate", ProvenanceTag.DerivedFromRecorded, null, signing));
        Assert.Contains("different log", ex.Message, StringComparison.Ordinal);
    }
}
