using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// An ECDSA signature over an <see cref="AuditAnchor"/>, kept in a detached file beside the log
/// (<c>&lt;log&gt;.anchor.sig</c> by default). This closes the honest limit stated in
/// <c>docs/assurance/caf-mapping.md</c> section 6.2: the plain hash chain and the <see cref="AuditAnchorFile"/>
/// are tamper-EVIDENT, but an actor who can rewrite both the log and its anchor can recompute a consistent
/// pair. Signing the anchor with a key the host does not hold makes that joint rewrite detectable: the
/// attacker can forge the chain and the anchor, but not the signature.
///
/// <para><b>Trust model (the same as licensing).</b> The signature is made with an ECDSA P-256 private key held
/// OFF the host (an offline station, an HSM, or an OS keychain reached through an <c>ISecretStore</c>); the host
/// keeps only the PUBLIC key and verifies. The strongest form signs the anchor offline at a checkpoint or
/// rotation; a host that signs continuously must hold the key and then only gains protection against an
/// attacker without that key. This class provides the primitive; the deployment decides where the key lives,
/// and the assurance document states which protection applies to a given deployment.</para>
///
/// <para><b>Format</b> (one ASCII line, newline-terminated):
/// <c>gridsim-audit-anchor-sig/1 count=&lt;N&gt; head=&lt;hash&gt; sig=&lt;base64&gt;</c>. The signed bytes are the exact
/// <see cref="AuditAnchorFile.Format(AuditAnchor)"/> line, so the signature is bound to that precise anchor.
/// The signature is ECDSA P-256 / SHA-256, IEEE-P1363 fixed 64 bytes, as the licence keys use.</para>
/// </summary>
public static class SignedAuditAnchor
{
    /// <summary>The format tag at the start of the signature line.</summary>
    public const string FormatTag = "gridsim-audit-anchor-sig/1";

    /// <summary>The default signature file for a log: the log's full path plus <c>.anchor.sig</c>.</summary>
    public static string DefaultPathFor(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        return Path.GetFullPath(logPath) + ".anchor.sig";
    }

    /// <summary>Sign <paramref name="anchor"/> with an ECDSA P-256 private key and return the one-line signature
    /// text (without the trailing newline).</summary>
    public static string Sign(AuditAnchor anchor, ECDsa signer)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(signer);
        byte[] signedBytes = Encoding.ASCII.GetBytes(AuditAnchorFile.Format(anchor));
        byte[] signature = signer.SignData(signedBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{FormatTag} count={anchor.Count} head={anchor.HeadHash} sig={Convert.ToBase64String(signature)}";
    }

    /// <summary>
    /// Verify that <paramref name="signatureText"/> is a valid signature over <paramref name="anchor"/> under
    /// <paramref name="verifier"/>'s public key. Returns false with a human-readable <paramref name="reason"/>
    /// for a malformed line, a count/head that does not match the anchor, or a bad signature; it never throws
    /// on bad input.
    /// </summary>
    public static bool Verify(AuditAnchor anchor, string signatureText, ECDsa verifier, out string reason)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(verifier);
        if (string.IsNullOrWhiteSpace(signatureText)) { reason = "empty anchor signature"; return false; }

        string line = signatureText.EndsWith('\n') ? signatureText[..^1] : signatureText;
        if (line.EndsWith('\r')) line = line[..^1];
        string[] parts = line.Split(' ');
        if (parts.Length != 4 || parts[0] != FormatTag
            || !parts[1].StartsWith("count=", StringComparison.Ordinal)
            || !parts[2].StartsWith("head=", StringComparison.Ordinal)
            || !parts[3].StartsWith("sig=", StringComparison.Ordinal))
        {
            reason = $"not a valid anchor signature line (expected '{FormatTag} count=<N> head=<hash> sig=<base64>')";
            return false;
        }

        // The signature must name the same anchor it is presented with, or it is a signature over a different
        // anchor replayed against this one.
        if (!int.TryParse(parts[1].AsSpan(6), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int count)
            || count != anchor.Count
            || !string.Equals(parts[2][5..], anchor.HeadHash, StringComparison.Ordinal))
        {
            reason = "anchor signature is for a different count or head hash than the anchor presented";
            return false;
        }

        byte[] signature;
        try { signature = Convert.FromBase64String(parts[3][4..]); }
        catch (FormatException) { reason = "anchor signature is not valid base64"; return false; }

        byte[] signedBytes = Encoding.ASCII.GetBytes(AuditAnchorFile.Format(anchor));
        bool ok;
        try
        {
            ok = verifier.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { reason = "anchor signature could not be checked"; return false; }

        if (!ok) { reason = "anchor signature is invalid (the log and anchor were rewritten, or the key is wrong)"; return false; }
        reason = string.Empty;
        return true;
    }

    /// <summary>Write the signature of <paramref name="anchor"/> to <paramref name="signaturePath"/> atomically
    /// (temp file, flush, move over the old one), as <see cref="AuditAnchorFile.Write"/> does for the anchor.</summary>
    public static void Write(string signaturePath, AuditAnchor anchor, ECDsa signer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        string line = Sign(anchor, signer) + "\n";
        string full = Path.GetFullPath(signaturePath);
        string temp = full + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.ASCII.GetBytes(line));
            fs.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>Read a signature file, or null when it does not exist.</summary>
    public static string? ReadIfExists(string signaturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        return File.Exists(signaturePath) ? File.ReadAllText(signaturePath, Encoding.ASCII) : null;
    }
}
