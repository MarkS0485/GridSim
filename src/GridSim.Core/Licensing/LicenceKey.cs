using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Licensing;

/// <summary>
/// Encode and verify GridSim licence keys. A key is a single SSH-authorized-keys-style line:
/// <code>GSK1 &lt;base64url-blob&gt;</code>
/// The blob is <c>magic "GSK1" (4) | version (1) | payloadLen u16-BE (2) | payload | signature (64)</c>.
/// The payload is canonical pipe-delimited UTF-8:
/// <c>v2|email|org|type|buslimit|issuedDays|expiresDays|licenseId|majorVersion</c> (dates = days since 1970-01-01 UTC).
///
/// F27 (Ruling R1, 2026-10-04): <c>majorVersion</c> is the GridSim major version the key was issued for (LICENSE.md
/// section 1 sells a licence per major version). It sits inside the signed bytes, so it cannot be edited without
/// breaking the signature. Every key this class encodes is a <c>v2</c> key. Keys issued before the binding are
/// <c>v1</c> keys (the same fields without <c>majorVersion</c>); they are still read, as keys for
/// <see cref="LegacyMajorVersion"/> (1), the line they were issued on, so existing keys keep working on 1.x.
/// Whether a key's major version fits the running build is decided by <see cref="LicenceGate"/>, not here (as with
/// expiry, parsing reports what the key says; the gate applies the policy).
/// GridSim builds from before F27 read only <c>v1</c>: they refuse a <c>v2</c> key ("unsupported licence schema"),
/// failing safe to the free tier.
/// The signature is ECDSA P-256 / SHA-256 (IEEE-P1363, fixed 64 bytes) over every byte before it.
///
/// The key is <b>signed, not encrypted</b>: the fields are inspectable, but cannot be altered or forged
/// without the out-of-repo private key. That is the correct trust model for offline licensing of
/// source-available software - you cannot hide fields in a binary the customer can read anyway.
/// </summary>
public static class LicenceKey
{
    /// <summary>The one-line prefix that makes a key recognisable and paste-safe.</summary>
    public const string Prefix = "GSK1 ";

    private const byte Version = 1;
    private const int MagicLen = 4;
    private const int HeaderLen = MagicLen + 1 + 2; // magic + version + payloadLen
    private const int SignatureLen = 64;            // P-256, IEEE-P1363
    private const string SchemaV1 = "v1";   // legacy: 8 fields, no major version (read as LegacyMajorVersion)
    private const string SchemaV2 = "v2";   // F27: 9 fields, the last one the major version
    private const int FieldCountV1 = 8;
    private const int FieldCountV2 = 9;

    /// <summary>
    /// The major version a legacy <c>v1</c> key (issued before keys carried one) stands for: 1. Every v1 key was issued
    /// on the 1.x line (LICENSE.md version 1.0 of 24 July 2026, during GridSim 1.x), so it keeps working on every 1.x
    /// build and, like any 1.x key, is refused on 2.x.
    /// </summary>
    public const int LegacyMajorVersion = 1;

    private static readonly byte[] Magic = "GSK1"u8.ToArray();
    private static readonly DateOnly Epoch = new(1970, 1, 1);

    /// <summary>Sign an entitlement into a <c>GSK1 …</c> token using the forge's private key.</summary>
    public static string Encode(Entitlement entitlement, ECDsa signer)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(signer);

        // F27: a key must name a real major version (1 or later); 0 or a negative number would bind it to nothing.
        if (entitlement.MajorVersion < 1)
            throw new ArgumentException($"A licence key must be issued for a major version of 1 or later, not {entitlement.MajorVersion}.", nameof(entitlement));

        byte[] payload = Encoding.UTF8.GetBytes(BuildPayload(entitlement));
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentException("Licence payload too large to encode.", nameof(entitlement));

        byte[] signed = new byte[HeaderLen + payload.Length];
        Magic.CopyTo(signed.AsSpan(0, MagicLen));
        signed[MagicLen] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(signed.AsSpan(MagicLen + 1, 2), (ushort)payload.Length);
        payload.CopyTo(signed.AsSpan(HeaderLen));

        byte[] signature = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != SignatureLen)
            throw new InvalidOperationException($"Expected a {SignatureLen}-byte P-256 signature, got {signature.Length}.");

        byte[] blob = new byte[signed.Length + SignatureLen];
        signed.CopyTo(blob.AsSpan());
        signature.CopyTo(blob.AsSpan(signed.Length));

        return Prefix + Base64Url.Encode(blob);
    }

    /// <summary>
    /// Verify and decode a token against an explicit public key. Returns false with a human-readable
    /// <paramref name="reason"/> for any malformed, mis-signed, or unparsable input - never throws on
    /// bad input. Expiry is <b>not</b> checked here (that is a policy decision made by the gate).
    /// </summary>
    public static bool TryParse(string? token, ECDsa verifier, out Entitlement entitlement, out string reason)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        entitlement = Entitlement.FreeDefault;

        if (string.IsNullOrWhiteSpace(token)) { reason = "empty licence key"; return false; }

        string body = token.Trim();
        if (body.StartsWith(Prefix, StringComparison.Ordinal))
            body = body[Prefix.Length..].Trim();
        // Tolerate wrapped/whitespaced pastes.
        body = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());

        byte[] blob;
        try { blob = Base64Url.Decode(body); }
        catch (FormatException) { reason = "licence key is not valid base64"; return false; }

        if (blob.Length < HeaderLen + SignatureLen) { reason = "licence key too short"; return false; }
        if (!blob.AsSpan(0, MagicLen).SequenceEqual(Magic)) { reason = "not a GridSim licence key"; return false; }
        if (blob[MagicLen] != Version) { reason = $"unsupported licence version {blob[MagicLen]}"; return false; }

        int payloadLen = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(MagicLen + 1, 2));
        int signedLen = HeaderLen + payloadLen;
        if (blob.Length != signedLen + SignatureLen) { reason = "licence key length mismatch"; return false; }

        ReadOnlySpan<byte> signed = blob.AsSpan(0, signedLen);
        ReadOnlySpan<byte> signature = blob.AsSpan(signedLen, SignatureLen);

        bool ok;
        try { ok = verifier.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { reason = "licence signature could not be checked"; return false; }
        if (!ok) { reason = "licence signature is invalid (key was altered or not issued by GridSim)"; return false; }

        string payload = Encoding.UTF8.GetString(blob.AsSpan(HeaderLen, payloadLen));
        if (!TryParsePayload(payload, out entitlement, out reason)) return false;

        reason = string.Empty;
        return true;
    }

    /// <summary>Verify and decode a token against the embedded production public key.</summary>
    public static bool TryParse(string? token, out Entitlement entitlement, out string reason)
    {
        using ECDsa? verifier = CreateEmbeddedVerifier();
        if (verifier is null)
        {
            entitlement = Entitlement.FreeDefault;
            reason = "no public key is embedded in this build";
            return false;
        }
        return TryParse(token, verifier, out entitlement, out reason);
    }

    /// <summary>
    /// Build an <see cref="ECDsa"/> verifier from the embedded public key, or null when the build still
    /// carries the placeholder (or the key is malformed). Caller owns disposal.
    /// </summary>
    internal static ECDsa? CreateEmbeddedVerifier()
    {
        string spki = LicencePublicKey.SpkiBase64;
        if (string.IsNullOrWhiteSpace(spki) || spki == LicencePublicKey.Placeholder)
            return null;

        ECDsa? ec = null;
        try
        {
            ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            return ec;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            ec?.Dispose();
            return null;
        }
    }

    private static string BuildPayload(Entitlement e) => string.Join('|',
        SchemaV2,
        Escape(e.Email),
        Escape(e.Org),
        e.Tier.ToWire(),
        e.BusLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DaysSinceEpoch(e.Issued).ToString(System.Globalization.CultureInfo.InvariantCulture),
        DaysSinceEpoch(e.Expires).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Escape(e.LicenceId),
        e.MajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));   // F27: last, so v1 fields keep their places

    private static bool TryParsePayload(string payload, out Entitlement entitlement, out string reason)
    {
        entitlement = Entitlement.FreeDefault;
        List<string> parts = SplitEscaped(payload);
        // F27: the schema decides the field count. v1 (legacy) has 8 fields and stands for major version 1; v2 has a
        // ninth field, the major version the key was issued for. Anything else is refused, naming the schema.
        bool v1 = parts.Count > 0 && parts[0] == SchemaV1;
        bool v2 = parts.Count > 0 && parts[0] == SchemaV2;
        if (!v1 && !v2) { reason = parts.Count > 0 ? $"unsupported licence schema '{parts[0]}'" : "malformed licence payload"; return false; }
        if (parts.Count != (v1 ? FieldCountV1 : FieldCountV2)) { reason = "malformed licence payload"; return false; }
        if (!LicenceTierCodes.TryFromWire(parts[3], out LicenceTier tier)) { reason = $"unknown licence tier '{parts[3]}'"; return false; }

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(parts[4], System.Globalization.NumberStyles.Integer, ci, out int busLimit) || busLimit < 0)
        { reason = "invalid bus limit in licence"; return false; }
        if (!int.TryParse(parts[5], System.Globalization.NumberStyles.Integer, ci, out int issuedDays))
        { reason = "invalid issue date in licence"; return false; }
        if (!int.TryParse(parts[6], System.Globalization.NumberStyles.Integer, ci, out int expiresDays))
        { reason = "invalid expiry date in licence"; return false; }
        int majorVersion = LegacyMajorVersion;
        if (v2 && (!int.TryParse(parts[8], System.Globalization.NumberStyles.None, ci, out majorVersion) || majorVersion < 1))
        { reason = "invalid major version in licence"; return false; }

        entitlement = new Entitlement(
            Email: parts[1],
            Org: parts[2],
            Tier: tier,
            BusLimit: busLimit,
            Issued: FromDays(issuedDays),
            Expires: FromDays(expiresDays),
            LicenceId: parts[7])
        { MajorVersion = majorVersion };
        reason = string.Empty;
        return true;
    }

    private static int DaysSinceEpoch(DateOnly d) => d.DayNumber - Epoch.DayNumber;

    private static DateOnly FromDays(int days)
    {
        long dayNumber = (long)days + Epoch.DayNumber;
        if (dayNumber < DateOnly.MinValue.DayNumber) return DateOnly.MinValue;
        if (dayNumber > DateOnly.MaxValue.DayNumber) return DateOnly.MaxValue;
        return DateOnly.FromDayNumber((int)dayNumber);
    }

    // Fields are pipe-delimited; escape '\' and '|' so an org name containing a pipe round-trips.
    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
                                               .Replace("|", "\\|", StringComparison.Ordinal);

    private static List<string> SplitEscaped(string s)
    {
        var parts = new List<string>(FieldCountV2);
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); }
            else if (c == '|') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }
}
