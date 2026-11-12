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
        // GR-726: the low-S form, as for licence keys (verification accepts both forms).
        byte[] signature = GridSim.Core.Licensing.EcdsaLowS.Normalise(signer, signer.SignData(signedBytes,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
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
        // GR-374: a v2 line is checked here for everything but the log's identity, which only VerifyStrict knows.
        if (line.StartsWith(FormatTagV2 + " ", StringComparison.Ordinal))
            return VerifyV2Line(anchor, null, line, verifier, out reason, out _);
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
        WriteLine(signaturePath, Sign(anchor, signer) + "\n");
    }

    /// <summary>GR-374 (November 2026): the format tag of a v2 signature line, which also binds the log's identity (the hash
    /// of its first entry) and the time it was signed.</summary>
    public const string FormatTagV2 = "gridsim-audit-anchor-sig/2";

    /// <summary>
    /// GR-374: sign <paramref name="anchor"/> in the v2 form,
    /// <c>gridsim-audit-anchor-sig/2 count=&lt;N&gt; head=&lt;hash&gt; first=&lt;hash&gt; signed=&lt;unix seconds&gt; sig=&lt;base64&gt;</c>.
    /// The signed bytes are the whole line before <c> sig=</c>, so the signature binds the count, the head, the log's first
    /// entry (<paramref name="firstHash"/>, <see cref="AuditLog.Genesis"/> for an empty log) and the signing time
    /// (<paramref name="signedAtUtc"/>, whole seconds since 1970-01-01 UTC). A v1 signature binds the count and head only, so
    /// it verified against any log with the same anchor (an empty log's anchor is the same for every log).
    /// </summary>
    public static string SignV2(AuditAnchor anchor, string firstHash, DateTimeOffset signedAtUtc, ECDsa signer)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(signer);
        string payload = PayloadV2(anchor, firstHash, signedAtUtc.ToUnixTimeSeconds());
        byte[] signature = GridSim.Core.Licensing.EcdsaLowS.Normalise(signer, signer.SignData(Encoding.ASCII.GetBytes(payload),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return $"{payload} sig={Convert.ToBase64String(signature)}";
    }

    // The v2 signed bytes: the line up to (not including) " sig=". The anchor and the first hash are checked as the anchor
    // file checks them (GENESIS or 64 upper-case hexadecimal digits).
    private static string PayloadV2(AuditAnchor anchor, string firstHash, long signedUnixSeconds)
    {
        _ = AuditAnchorFile.Format(anchor);   // validates the count and head
        if (!IsHash(firstHash))
            throw new ArgumentException($"The first-entry hash '{firstHash}' is neither {AuditLog.Genesis} nor 64 upper-case hexadecimal digits.", nameof(firstHash));
        if ((anchor.Count == 0) != string.Equals(firstHash, AuditLog.Genesis, StringComparison.Ordinal))
            throw new ArgumentException("The first-entry hash must be GENESIS exactly when the anchor's count is 0.", nameof(firstHash));
        if (signedUnixSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(signedUnixSeconds), signedUnixSeconds, "The signing time must not be before 1970.");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{FormatTagV2} count={anchor.Count} head={anchor.HeadHash} first={firstHash} signed={signedUnixSeconds}");
    }

    private static bool IsHash(string? h) =>
        h is not null && (string.Equals(h, AuditLog.Genesis, StringComparison.Ordinal)
            || (h.Length == 64 && h.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')));

    /// <summary>
    /// GR-374: verify a signature line strictly against the log it is presented with. A v2 line must name the same count,
    /// head AND first entry (<paramref name="firstHash"/>) and carry a valid signature over them; <paramref name="signedAtUtc"/>
    /// is then its signing time. A v1 line (count and head only) is accepted when its signature is valid, with
    /// <paramref name="legacyV1"/> true so the caller can warn that it binds no log identity or time; it is replaced by a v2
    /// signature the next time the anchor is signed. Returns false with a reason otherwise; never throws on bad input.
    /// </summary>
    public static bool VerifyStrict(AuditAnchor anchor, string firstHash, string signatureText, ECDsa verifier,
        out string reason, out bool legacyV1, out DateTimeOffset? signedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(verifier);
        legacyV1 = false;
        signedAtUtc = null;
        if (string.IsNullOrWhiteSpace(signatureText)) { reason = "empty anchor signature"; return false; }
        string line = signatureText.EndsWith('\n') ? signatureText[..^1] : signatureText;
        if (line.EndsWith('\r')) line = line[..^1];
        if (line.StartsWith(FormatTag + " ", StringComparison.Ordinal))
        {
            legacyV1 = true;
            return Verify(anchor, signatureText, verifier, out reason);
        }
        return VerifyV2Line(anchor, firstHash, line, verifier, out reason, out signedAtUtc);
    }

    // A v2 line: the fields in order, the anchor and (when given) the first hash matched, then the signature over the payload.
    private static bool VerifyV2Line(AuditAnchor anchor, string? firstHash, string line, ECDsa verifier,
        out string reason, out DateTimeOffset? signedAtUtc)
    {
        signedAtUtc = null;
        string[] parts = line.Split(' ');
        if (parts.Length != 6 || parts[0] != FormatTagV2
            || !parts[1].StartsWith("count=", StringComparison.Ordinal)
            || !parts[2].StartsWith("head=", StringComparison.Ordinal)
            || !parts[3].StartsWith("first=", StringComparison.Ordinal)
            || !parts[4].StartsWith("signed=", StringComparison.Ordinal)
            || !parts[5].StartsWith("sig=", StringComparison.Ordinal))
        {
            reason = $"not a valid anchor signature line (expected '{FormatTag} count=<N> head=<hash> sig=<base64>' or " +
                $"'{FormatTagV2} count=<N> head=<hash> first=<hash> signed=<unix seconds> sig=<base64>')";
            return false;
        }
        if (!int.TryParse(parts[1].AsSpan(6), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int count)
            || count != anchor.Count
            || !string.Equals(parts[2][5..], anchor.HeadHash, StringComparison.Ordinal))
        {
            reason = "anchor signature is for a different count or head hash than the anchor presented";
            return false;
        }
        string signedFirst = parts[3][6..];
        if (firstHash is not null && !string.Equals(signedFirst, firstHash, StringComparison.Ordinal))
        {
            reason = "anchor signature is for a different log (its first entry differs from this log's)";
            return false;
        }
        if (!long.TryParse(parts[4].AsSpan(7), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            reason = "anchor signature has no valid signing time";
            return false;
        }
        byte[] signature;
        try { signature = Convert.FromBase64String(parts[5][4..]); }
        catch (FormatException) { reason = "anchor signature is not valid base64"; return false; }
        string payload = line[..line.LastIndexOf(" sig=", StringComparison.Ordinal)];
        bool ok;
        try
        {
            ok = verifier.VerifyData(Encoding.ASCII.GetBytes(payload), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { reason = "anchor signature could not be checked"; return false; }
        if (!ok) { reason = "anchor signature is invalid (the log and anchor were rewritten, or the key is wrong)"; return false; }
        signedAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        reason = string.Empty;
        return true;
    }

    /// <summary>GR-374: write a v2 signature of <paramref name="anchor"/> (see <see cref="SignV2"/>) atomically, as
    /// <see cref="Write(string, AuditAnchor, ECDsa)"/> writes a v1 one.</summary>
    public static void WriteV2(string signaturePath, AuditAnchor anchor, string firstHash, DateTimeOffset signedAtUtc, ECDsa signer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        WriteLine(signaturePath, SignV2(anchor, firstHash, signedAtUtc, signer) + "\n");
    }

    // The atomic write both forms share: temporary file, flush, move over the old one, flush the folder (GR-837).
    private static void WriteLine(string signaturePath, string line)
    {
        string full = Path.GetFullPath(signaturePath);
        string temp = full + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.ASCII.GetBytes(line));
            fs.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
        DirectorySync.Flush(Path.GetDirectoryName(full)!);   // GR-837: as for the anchor, the rename's folder too
    }

    /// <summary>Read a signature file, or null when it does not exist.</summary>
    public static string? ReadIfExists(string signaturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        return File.Exists(signaturePath) ? File.ReadAllText(signaturePath, Encoding.ASCII) : null;
    }
}
