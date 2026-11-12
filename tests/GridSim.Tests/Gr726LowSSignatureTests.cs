using System.Numerics;
using System.Security.Cryptography;
using GridSim.Core.Diagnostics;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-726 (decided): every ECDSA signature has a high-S twin that verifies too, and .NET signs in either form at random,
/// so a licence key (or a signed audit anchor) had two valid signatures. Signing now always writes the low-S form;
/// verification still accepts both, so keys issued before keep working (refusing high-S waits for a revocation list).
/// </summary>
public class Gr726LowSSignatureTests
{
    private static readonly DateOnly Today = new(2026, 7, 1);

    private static readonly BigInteger N = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    private static Entitlement Commercial() =>
        new("user@example.com", "Example Ltd", LicenceTier.CommercialCode, 0, Today, Today.AddDays(365), "lows1") { MajorVersion = 1 };

    private static byte[] SignatureOf(string token)
    {
        byte[] blob = Base64Url.Decode(token[LicenceKey.Prefix.Length..]);
        return blob[^64..];
    }

    [Fact]
    public void Gr726_every_licence_key_is_signed_in_the_low_s_form_and_verifies()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Forty signatures: with a random S form, about half would have been high-S before.
        for (int i = 0; i < 40; i++)
        {
            string token = LicenceKey.Encode(Commercial(), signer);
            Assert.False(EcdsaLowS.IsHighS(SignatureOf(token)), $"key {i} carries a high-S signature");
            Assert.True(LicenceKey.TryParse(token, signer, out _, out string reason), reason);
        }
    }

    [Fact]
    public void Gr726_the_high_s_twin_of_a_key_still_verifies()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string token = LicenceKey.Encode(Commercial(), signer);
        byte[] blob = Base64Url.Decode(token[LicenceKey.Prefix.Length..]);
        byte[] sig = blob[^64..];
        var s = new BigInteger(sig.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        byte[] high = (N - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Clear(blob, blob.Length - 32, 32);
        high.CopyTo(blob, blob.Length - high.Length);
        Assert.True(EcdsaLowS.IsHighS(blob[^64..]));
        string twin = LicenceKey.Prefix + Base64Url.Encode(blob);
        Assert.True(LicenceKey.TryParse(twin, signer, out _, out string reason), reason);   // decided: both forms accepted
    }

    [Fact]
    public void Gr726_normalising_maps_s_to_n_minus_s_above_half_the_order_only()
    {
        var sig = new byte[64];
        sig[0] = 7;                                     // r is never touched
        byte[] nMinusOne = (N - 1).ToByteArray(isUnsigned: true, isBigEndian: true);
        nMinusOne.CopyTo(sig, 32);                      // s = n - 1, high
        byte[] low = EcdsaLowS.NormaliseP256(sig);
        Assert.Equal(7, low[0]);
        Assert.Equal(BigInteger.One, new BigInteger(low.AsSpan(32, 32), isUnsigned: true, isBigEndian: true));
        Assert.Equal(nMinusOne, sig[32..]);             // the input is unchanged
        byte[] again = EcdsaLowS.NormaliseP256(low);    // already low: unchanged
        Assert.Equal(low, again);
    }

    [Fact]
    public void Gr726_signed_audit_anchors_are_low_s_and_verify()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var anchor = new AuditAnchor(0, AuditLog.Genesis);
        for (int i = 0; i < 20; i++)
        {
            string line = SignedAuditAnchor.Sign(anchor, signer);
            byte[] sig = Convert.FromBase64String(line[(line.IndexOf("sig=", StringComparison.Ordinal) + 4)..]);
            Assert.False(EcdsaLowS.IsHighS(sig), $"anchor signature {i} is high-S");
            Assert.True(SignedAuditAnchor.Verify(anchor, line, signer, out string reason), reason);
        }
    }
}
