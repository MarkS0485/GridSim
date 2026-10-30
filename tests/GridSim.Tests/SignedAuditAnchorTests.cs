using System;
using System.IO;
using System.Security.Cryptography;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The detached ECDSA signature over an audit anchor (NCSC CAF C1, section 6.2 of the CAF mapping): the plain
/// hash chain and its anchor file are tamper-EVIDENT, but an actor who can rewrite both can recompute a
/// consistent pair. Signing the anchor with a key the host does not hold makes that joint rewrite detectable.
/// A signature verifies under the same key over the same anchor, and fails for a changed count or head, for a
/// different key, and for a malformed line - and it never throws on bad input. It round-trips through a file.
/// </summary>
public sealed class SignedAuditAnchorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-signanchor-{Guid.NewGuid():N}");

    public SignedAuditAnchorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string LogPath(string name = "audit.jsonl") => Path.Combine(_dir, name);

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void A_signature_verifies_under_the_same_key_over_the_same_anchor()
    {
        using ECDsa key = NewKey();
        var anchor = new AuditAnchor(1, new string('A', 64));

        string signature = SignedAuditAnchor.Sign(anchor, key);
        Assert.StartsWith(SignedAuditAnchor.FormatTag, signature, StringComparison.Ordinal);

        Assert.True(SignedAuditAnchor.Verify(anchor, signature, key, out string reason), reason);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void An_empty_logs_genesis_anchor_signs_and_verifies()
    {
        using ECDsa key = NewKey();
        var genesis = new AuditAnchor(0, AuditLog.Genesis);

        string signature = SignedAuditAnchor.Sign(genesis, key);
        Assert.True(SignedAuditAnchor.Verify(genesis, signature, key, out string reason), reason);
    }

    [Fact]
    public void A_signature_fails_when_the_anchor_count_or_head_differs_from_the_signed_one()
    {
        using ECDsa key = NewKey();
        var signed = new AuditAnchor(5, new string('B', 64));
        string signature = SignedAuditAnchor.Sign(signed, key);

        // A different count presented against the same signature.
        var wrongCount = new AuditAnchor(6, new string('B', 64));
        Assert.False(SignedAuditAnchor.Verify(wrongCount, signature, key, out string reason1));
        Assert.False(string.IsNullOrEmpty(reason1));

        // A different head hash presented against the same signature.
        var wrongHead = new AuditAnchor(5, new string('C', 64));
        Assert.False(SignedAuditAnchor.Verify(wrongHead, signature, key, out string reason2));
        Assert.False(string.IsNullOrEmpty(reason2));
    }

    [Fact]
    public void A_signature_from_a_different_key_fails_to_verify()
    {
        using ECDsa signer = NewKey();
        using ECDsa other = NewKey();
        var anchor = new AuditAnchor(3, new string('D', 64));
        string signature = SignedAuditAnchor.Sign(anchor, signer);

        Assert.False(SignedAuditAnchor.Verify(anchor, signature, other, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void A_malformed_signature_line_is_refused_without_throwing()
    {
        using ECDsa key = NewKey();
        var anchor = new AuditAnchor(1, new string('E', 64));

        Assert.False(SignedAuditAnchor.Verify(anchor, "not a signature line", key, out string r1));
        Assert.False(string.IsNullOrEmpty(r1));
        Assert.False(SignedAuditAnchor.Verify(anchor, "", key, out string r2));
        Assert.False(string.IsNullOrEmpty(r2));
    }

    [Fact]
    public void A_signature_over_a_writers_real_anchor_round_trips_through_a_file()
    {
        string path = LogPath();
        AuditAnchor anchor;
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            writer.Append(T0, null, ProvenanceTag.Recorded, "first");
            writer.Append(T0.AddSeconds(1), null, ProvenanceTag.Recorded, "second");
            anchor = writer.Anchor;
        }
        Assert.Equal(2, anchor.Count);

        using ECDsa key = NewKey();
        string sigPath = SignedAuditAnchor.DefaultPathFor(path);
        SignedAuditAnchor.Write(sigPath, anchor, key);

        string? read = SignedAuditAnchor.ReadIfExists(sigPath);
        Assert.NotNull(read);
        Assert.True(SignedAuditAnchor.Verify(anchor, read!, key, out string reason), reason);

        // A missing signature file reads back as null, not an error.
        Assert.Null(SignedAuditAnchor.ReadIfExists(Path.Combine(_dir, "no-such.anchor.sig")));
    }
}
