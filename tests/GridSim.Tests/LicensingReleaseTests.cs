using System.Security.Cryptography;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F11 (release register section 6): licensing tests a release depends on, added beside LicensingTests (which signs
/// and verifies with a test-only key pair). These cover what that class does not: the REAL embedded production public
/// key (present, a valid P-256 key, not the placeholder, and refusing anything signed by another private key), every
/// single-byte tamper of a key, the expiry boundary (the expiry day itself is still valid), the per-OS licence file
/// path, and the previously unreferenced Base64Url and LicenceException.
/// No production private key is involved anywhere: it never leaves the out-of-repo forge.
/// </summary>
public class LicensingReleaseTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>A key signed by a throw-away private key that has nothing to do with the production one.</summary>
    private static (string Token, ECDsa Signer) ForeignToken(DateOnly expires, int busLimit = 0)
    {
        ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ent = new Entitlement("user@example.com", "Example Ltd", LicenceTier.CommercialDeployment, busLimit, Today, expires, "rel01");
        return (LicenceKey.Encode(ent, signer), signer);
    }

    [Fact]
    public void Embedded_production_key_is_a_real_p256_public_key()
    {
        Assert.NotEqual(LicencePublicKey.Placeholder, LicencePublicKey.SpkiBase64);
        using ECDsa? verifier = LicenceKey.CreateEmbeddedVerifier();
        Assert.NotNull(verifier);
        Assert.Equal(256, verifier!.KeySize);
        // The imported key exports back to exactly the embedded bytes (nothing was silently altered on import).
        Assert.Equal(Convert.FromBase64String(LicencePublicKey.SpkiBase64), verifier.ExportSubjectPublicKeyInfo());
        // It is a public key only: it cannot sign.
        Assert.ThrowsAny<CryptographicException>(() => verifier.SignData(new byte[] { 1, 2, 3 }, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Production_verifier_refuses_a_key_from_any_other_signer()
    {
        (string token, ECDsa signer) = ForeignToken(Today.AddDays(30));
        using (signer)
        {
            // The foreign key is well formed: its own signer accepts it ...
            Assert.True(LicenceKey.TryParse(token, signer, out _, out _));
            // ... and the shipped verifier (the public overload every front end uses) refuses it.
            Assert.False(LicenceKey.TryParse(token, out Entitlement ent, out string reason));
            Assert.Contains("signature is invalid", reason, StringComparison.Ordinal);
            Assert.Equal(Entitlement.FreeDefault, ent);
        }
    }

    [Fact]
    public void A_foreign_key_from_the_environment_falls_back_to_free_with_a_visible_note()
    {
        (string token, ECDsa signer) = ForeignToken(Today.AddDays(30));
        using (signer)
        using (ECDsa? verifier = LicenceKey.CreateEmbeddedVerifier())
        {
            LicenceGate gate = LicenceGate.Resolve(null, token, null, verifier);
            Assert.Equal(Entitlement.FreeDefault, gate.Entitlement);
            Assert.NotNull(gate.SourceNote);
            Assert.Contains(LicenceGate.EnvVar, gate.SourceNote!, StringComparison.Ordinal);
            Assert.Contains("rejected", gate.Describe(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_single_byte_tamper_is_rejected()
    {
        (string token, ECDsa signer) = ForeignToken(Today.AddDays(30));
        using (signer)
        {
            byte[] blob = Base64Url.Decode(token[LicenceKey.Prefix.Length..]);
            for (int i = 0; i < blob.Length; i++)
            {
                byte[] t = (byte[])blob.Clone();
                t[i] ^= 0x01;
                string tampered = LicenceKey.Prefix + Base64Url.Encode(t);
                Assert.False(LicenceKey.TryParse(tampered, signer, out _, out string reason),
                    $"flipping one bit of byte {i} of {blob.Length} was accepted");
                Assert.False(string.IsNullOrEmpty(reason));
            }
        }
    }

    [Fact]
    public void Expiry_day_itself_is_valid_and_the_day_after_falls_back_to_free()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Entitlement Signed(DateOnly expires)
        {
            string t = LicenceKey.Encode(new Entitlement("a@example.com", "", LicenceTier.CommercialDeployment, 0, Today.AddYears(-1), expires, "x"), signer);
            Assert.True(LicenceKey.TryParse(t, signer, out Entitlement e, out string why), why);
            return e;
        }

        // Expires is inclusive: on the expiry day the paid tier still applies (unlimited buses).
        var today = new LicenceGate(Signed(Today));
        Assert.False(today.Entitlement.IsExpired);
        Assert.True(today.Check(50_000, bundled: false).Allowed);

        // The day after, the free Personal tier applies: within its cap allowed, above it denied, naming the expiry.
        var lapsed = new LicenceGate(Signed(Today.AddDays(-1)));
        Assert.True(lapsed.Entitlement.IsExpired);
        Assert.True(lapsed.Check(Entitlement.FreeBusLimit, bundled: false).Allowed);
        GateResult over = lapsed.Check(Entitlement.FreeBusLimit + 1, bundled: false);
        Assert.False(over.Allowed);
        Assert.Contains("expired on", over.Message, StringComparison.Ordinal);
        Assert.Contains(LicenceGate.ContactEmail, over.Message, StringComparison.Ordinal);
        // Bundled networks never count, expired or not.
        Assert.True(lapsed.Check(1_000_000, bundled: true).Allowed);
    }

    [Fact]
    public void Enforce_throws_a_licence_exception_with_the_user_facing_message()
    {
        var gate = new LicenceGate(Entitlement.FreeDefault);
        LicenceException ex = Assert.Throws<LicenceException>(() => gate.Enforce(Entitlement.FreeBusLimit + 1, bundled: false));
        Assert.Contains($"{Entitlement.FreeBusLimit:N0}", ex.Message, StringComparison.Ordinal);
        Assert.Contains(LicenceGate.ContactEmail, ex.Message, StringComparison.Ordinal);
        gate.Enforce(Entitlement.FreeBusLimit, bundled: false);   // at the cap: no exception

        // The three standard constructors exist and keep message and inner exception.
        Assert.NotNull(new LicenceException().Message);
        Assert.Equal("m", new LicenceException("m").Message);
        var inner = new InvalidOperationException("i");
        Assert.Same(inner, new LicenceException("m", inner).InnerException);
    }

    [Fact]
    public void Licence_file_lives_in_the_per_user_data_folder_on_every_os()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "GridSim", "licence.gridsim"), LicenceGate.LicenceFilePath);
        Assert.EndsWith(Path.Combine("GridSim", "licence.gridsim"), LicenceGate.LicenceFilePath, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
            Assert.Contains(Path.Combine("AppData", "Local"), LicenceGate.LicenceFilePath, StringComparison.OrdinalIgnoreCase);
        else if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME")))
            Assert.Contains(Path.Combine(".local", "share"), LicenceGate.LicenceFilePath, StringComparison.Ordinal);
        else if (OperatingSystem.IsMacOS())
            Assert.Contains(Path.Combine("Library", "Application Support"), LicenceGate.LicenceFilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Base64url_round_trips_every_length_and_never_emits_unsafe_characters()
    {
        var rng = new Random(20261003);   // fixed seed: deterministic
        for (int len = 0; len <= 70; len++)
        {
            byte[] bytes = new byte[len];
            rng.NextBytes(bytes);
            string s = Base64Url.Encode(bytes);
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
            Assert.DoesNotContain('=', s);
            Assert.Equal(bytes, Base64Url.Decode(s));
        }
        // 0xFB 0xFF encodes to "+/8" in standard base64, "-_8" in base64url.
        Assert.Equal("-_8", Base64Url.Encode(new byte[] { 0xFB, 0xFF }));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Base64Url.Decode("-_8"));
    }

    [Theory]
    [InlineData("A")]        // length 1 mod 4 can never be valid
    [InlineData("AAAAA")]
    [InlineData("AA*A")]     // not a base64 character
    public void Base64url_refuses_malformed_input_with_a_format_exception(string bad)
        => Assert.Throws<FormatException>(() => Base64Url.Decode(bad));

    [Fact]
    public void Every_tier_round_trips_through_its_wire_code()
    {
        foreach (LicenceTier tier in Enum.GetValues<LicenceTier>())
        {
            Assert.True(LicenceTierCodes.TryFromWire(tier.ToWire(), out LicenceTier back));
            Assert.Equal(tier, back);
        }
        Assert.False(LicenceTierCodes.TryFromWire("platinum", out _));
    }
}
