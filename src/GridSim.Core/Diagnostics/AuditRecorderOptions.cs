using System.Security.Cryptography;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// The optional parts of a product audit run (<see cref="AuditRunRecorder"/>): where the entries are forwarded, and
/// how the anchor is signed and checked. Every product path that opens an audit log (the CLI's <c>--audit-log</c>, the
/// web host's <c>Audit:LogPath</c>) builds one of these from its own flags or settings, so the same controls are
/// reachable from both. The default (<see cref="None"/>) is exactly the plain anchored log.
///
/// <para><b>Forwarding (NCSC CAF C1).</b> <see cref="Sinks"/> are notified after each entry is durably on disk, in
/// order. The on-disk chain stays authoritative: a sink that fails never fails the append (see <see cref="IAuditSink"/>).
/// GridSim ships <see cref="JsonLinesAuditSink"/> (an append-only copy for a SIEM collector or a write-once mount) and
/// the incident sinks in <c>GridSim.Core.Diagnostics.Incident</c>.</para>
///
/// <para><b>Signed anchor (C1, the joint-rewrite limit).</b> With <see cref="AnchorSigningKey"/> (an ECDSA P-256
/// PRIVATE key) the recorder writes a detached signature over the anchor after every append
/// (<see cref="SignedAuditAnchor"/>, file <see cref="SignaturePath"/>). With <see cref="AnchorVerificationKey"/> (the
/// PUBLIC key) it refuses, before the run does any work, a log whose anchor has no signature or a signature that does
/// not verify: a rewrite of the log and its anchor together, without the private key, is then detected. A host that
/// holds the private key only protects against an attacker who does not; the strongest form keeps the private key off
/// the host and signs at checkpoints (the assurance documents say which applies).</para>
/// </summary>
public sealed record AuditRecorderOptions
{
    /// <summary>No forwarding and no signature: the plain anchored log.</summary>
    public static AuditRecorderOptions None { get; } = new();

    /// <summary>Secondary observers notified after each durable append, in order. Empty by default.</summary>
    public IReadOnlyList<IAuditSink> Sinks { get; init; } = Array.Empty<IAuditSink>();

    /// <summary>ECDSA P-256 private key: when set, the anchor is signed after every append.</summary>
    public ECDsa? AnchorSigningKey { get; init; }

    /// <summary>ECDSA P-256 public key: when set, the anchor's signature must exist and verify at start.</summary>
    public ECDsa? AnchorVerificationKey { get; init; }

    /// <summary>The detached signature file; null means <see cref="SignedAuditAnchor.DefaultPathFor"/> of the log.</summary>
    public string? SignaturePath { get; init; }

    /// <summary>The secondary delivery channels (a forwarding file, an incident file) whose failures the run reports
    /// when it ends (<see cref="AuditRunRecorder.DeliveryErrors"/>). A channel never fails an append; this is how its
    /// failure still reaches the operator.</summary>
    public IReadOnlyList<IDeliveryChannel> DeliveryChannels { get; init; } = Array.Empty<IDeliveryChannel>();

    /// <summary>
    /// Load an ECDSA P-256 key from a PEM file (<c>-----BEGIN PRIVATE KEY-----</c> / <c>EC PRIVATE KEY</c> for signing,
    /// <c>-----BEGIN PUBLIC KEY-----</c> for verifying). Fails loud on a missing file, a file that is not PEM, or a key
    /// that is not on the P-256 curve (the curve <see cref="SignedAuditAnchor"/> signs with). The key text is never
    /// echoed in a message.
    /// </summary>
    /// <param name="path">The PEM file.</param>
    /// <param name="requirePrivate">True for a signing key: a public-only key is refused.</param>
    public static ECDsa LoadKey(string path, bool requirePrivate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Audit anchor key file '{path}' does not exist.", path);
        string pem = File.ReadAllText(path);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException($"Audit anchor key file '{path}' is not a PEM ECDSA key ({ex.GetType().Name}).");
        }
        ECParameters p;
        try
        {
            p = key.ExportParameters(includePrivateParameters: requirePrivate);
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException($"Audit anchor key file '{path}' holds no private key; a signing key must be the PRIVATE key.");
        }
        // 1.2.840.10045.3.1.7 is the OID of NIST P-256 (secp256r1, prime256v1).
        if (!string.Equals(p.Curve.Oid?.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal))
        {
            key.Dispose();
            throw new InvalidDataException($"Audit anchor key file '{path}' is not a P-256 key; the anchor signature uses ECDSA P-256.");
        }
        return key;
    }
}
