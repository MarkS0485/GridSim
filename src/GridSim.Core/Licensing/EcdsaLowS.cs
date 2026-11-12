using System.Numerics;
using System.Security.Cryptography;

namespace GridSim.Core.Licensing;

/// <summary>
/// GR-726 (November 2026): every ECDSA signature (r, s) has a twin (r, n - s) that verifies as well, and .NET's SignData
/// does not pick one, so about half the signatures it makes are in the "high-S" form (s greater than n / 2). Signing
/// now always writes the low-S form, so each key or anchor GridSim issues from here on has one canonical signature.
/// Verification still accepts both forms (decision: keys already issued in the high-S form stay valid; refusing high-S
/// waits for a revocation list and a reissue). Only P-256 signatures in the fixed-field (IEEE P1363, r || s) layout are
/// normalised; anything else is returned unchanged.
/// </summary>
internal static class EcdsaLowS
{
    // The order n of the NIST P-256 group, and n / 2.
    private static readonly BigInteger P256Order = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger P256HalfOrder = P256Order >> 1;

    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>The same signature in its low-S form when <paramref name="signer"/> is a P-256 key and
    /// <paramref name="signature"/> is a 64-byte P1363 signature; otherwise <paramref name="signature"/> itself.</summary>
    public static byte[] Normalise(ECDsa signer, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Length != 64 || !IsP256(signer)) return signature;
        return NormaliseP256(signature);
    }

    /// <summary>The low-S form of a 64-byte P-256 P1363 signature (r || s, big-endian): s is replaced by n - s when it
    /// is above n / 2. The input is not modified.</summary>
    internal static byte[] NormaliseP256(byte[] signature)
    {
        if (signature.Length != 64)
            throw new ArgumentException("A P-256 P1363 signature is 64 bytes.", nameof(signature));
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (s <= P256HalfOrder) return (byte[])signature.Clone();
        BigInteger low = P256Order - s;
        var result = (byte[])signature.Clone();
        Array.Clear(result, 32, 32);
        byte[] lowBytes = low.ToByteArray(isUnsigned: true, isBigEndian: true);
        lowBytes.CopyTo(result, 64 - lowBytes.Length);   // left-padded with zeros to 32 bytes
        return result;
    }

    /// <summary>True when s (the second half of a 64-byte P1363 signature) is above n / 2.</summary>
    internal static bool IsHighS(byte[] signature) =>
        signature.Length == 64 && new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: true) > P256HalfOrder;

    private static bool IsP256(ECDsa key)
    {
        if (key.KeySize != 256) return false;
        ECCurve curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed && (curve.Oid.Value == P256Oid
            || string.Equals(curve.Oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase)
            || string.Equals(curve.Oid.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase));
    }
}
