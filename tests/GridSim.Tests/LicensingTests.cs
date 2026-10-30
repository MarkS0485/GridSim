using System;
using System.Security.Cryptography;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The offline licence engine: sign → verify round-trips, tamper detection, expiry and bus-limit
/// enforcement, the Bundled-Network exemption, and source-precedence resolution.
///
/// These use a dedicated <b>TEST-ONLY</b> keypair generated purely for the suite - it is NOT the
/// production signing key and verifies nothing the shipped app trusts. Signing with it here lets the
/// tests exercise the real <see cref="LicenceKey"/> / <see cref="LicenceGate"/> code paths without the
/// production private key (which never leaves the out-of-repo forge).
/// </summary>
public class LicensingTests
{
    // TEST-ONLY P-256 keypair. Do not reuse for anything real.
    private const string TestPrivatePem =
        "-----BEGIN PRIVATE KEY-----\n" +
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgWLzxmYiRoDozWPWp\n" +
        "sgLNLDAD+Px74rdmMro9iRBSXHqhRANCAAT31Ey0I3YAHyz1+DLhRvOlgg8KNtpi\n" +
        "Siax5EpDsKOBv0XLNZPT4w//PGewl9E//+UtyaIflbr9i+d/rbu49B6Y\n" +
        "-----END PRIVATE KEY-----";

    private static ECDsa Key()
    {
        ECDsa ec = ECDsa.Create();
        ec.ImportFromPem(TestPrivatePem);
        return ec;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Token(LicenceTier tier, int busLimit, DateOnly expires,
        string email = "user@example.com", string org = "Example Ltd", string id = "test01")
    {
        using ECDsa signer = Key();
        var ent = new Entitlement(email, org, tier, busLimit, Today, expires, id);
        return LicenceKey.Encode(ent, signer);
    }

    [Fact]
    public void Round_trips_every_field_through_sign_and_verify()
    {
        DateOnly expires = new(2027, 1, 1);
        string token = Token(LicenceTier.Academic, 5000, expires, "a@b.com", "Acme Grid Ltd", "abc123");

        using ECDsa verifier = Key();
        Assert.True(LicenceKey.TryParse(token, verifier, out Entitlement ent, out string reason), reason);
        Assert.Equal("a@b.com", ent.Email);
        Assert.Equal("Acme Grid Ltd", ent.Org);
        Assert.Equal(LicenceTier.Academic, ent.Tier);
        Assert.Equal(5000, ent.BusLimit);
        Assert.Equal(expires, ent.Expires);
        Assert.Equal("abc123", ent.LicenceId);
    }

    [Fact]
    public void Key_looks_like_an_ssh_key_line()
    {
        string token = Token(LicenceTier.CommercialCode, 0, new DateOnly(2030, 1, 1));
        Assert.StartsWith("GSK1 ", token);
        Assert.True(token.Length is > 120 and < 400, $"expected an SSH-key-length token, got {token.Length}");
        // Base64url body: no +, /, or = that a shell/URL/config would mangle.
        string body = token["GSK1 ".Length..];
        Assert.DoesNotContain('+', body);
        Assert.DoesNotContain('/', body);
        Assert.DoesNotContain('=', body);
    }

    [Fact]
    public void An_org_name_containing_a_pipe_round_trips()
    {
        string token = Token(LicenceTier.CommercialCode, 0, new DateOnly(2030, 1, 1), org: "A|B Grid | Co");
        using ECDsa verifier = Key();
        Assert.True(LicenceKey.TryParse(token, verifier, out Entitlement ent, out _));
        Assert.Equal("A|B Grid | Co", ent.Org);
    }

    [Fact]
    public void A_tampered_key_fails_verification()
    {
        string token = Token(LicenceTier.Personal, 1500, new DateOnly(2027, 1, 1));
        int i = token.Length - 5;                       // well inside the signature region
        char repl = token[i] == 'A' ? 'B' : 'A';
        string tampered = token[..i] + repl + token[(i + 1)..];

        using ECDsa verifier = Key();
        Assert.False(LicenceKey.TryParse(tampered, verifier, out _, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void A_key_from_a_different_signer_is_rejected()
    {
        string token = Token(LicenceTier.NetworkOperator, 0, new DateOnly(2030, 1, 1));
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);   // a stranger's key
        Assert.False(LicenceKey.TryParse(token, other, out _, out string reason));
        Assert.Contains("signature", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Garbage_input_is_rejected_without_throwing()
    {
        using ECDsa verifier = Key();
        Assert.False(LicenceKey.TryParse("not a key", verifier, out _, out _));
        Assert.False(LicenceKey.TryParse("GSK1 !!!!not-base64!!!!", verifier, out _, out _));
        Assert.False(LicenceKey.TryParse("", verifier, out _, out _));
        Assert.False(LicenceKey.TryParse(null, verifier, out _, out _));
    }

    [Fact]
    public void Expired_licence_is_denied_valid_one_is_allowed()
    {
        var expired = new Entitlement("u@x.com", "X", LicenceTier.Academic, 5000, Today.AddDays(-10), Today.AddDays(-1), "e");
        var live = expired with { Expires = Today.AddDays(365) };

        // Moved 2026-10 (Q7 / R274, authorised by Mark: "always default back to a free tier"). This line asserted
        // that an expired Academic key DENIES a 100-bus model, which left a lapsed customer worse off than an
        // unlicensed user (the free Personal tier allows 1,500 buses and never expires). An expired key now falls
        // back to the free tier: 100 buses is allowed, and a model over the free cap is still denied, naming the expiry.
        Assert.True(new LicenceGate(expired).Check(100, bundled: false).Allowed);
        Assert.True(new LicenceGate(expired).Check(Entitlement.FreeBusLimit, bundled: false).Allowed);
        GateResult over = new LicenceGate(expired).Check(Entitlement.FreeBusLimit + 1, bundled: false);
        Assert.False(over.Allowed);
        Assert.Contains("expired", over.Message);
        Assert.True(new LicenceGate(live).Check(100, bundled: false).Allowed);
        Assert.True(new LicenceGate(live).Check(Entitlement.FreeBusLimit + 1, bundled: false).Allowed);   // Academic 5,000
    }

    [Fact]
    public void Bus_limit_is_enforced_at_the_boundary()
    {
        var ent = new Entitlement("u@x.com", "X", LicenceTier.Personal, 1500, Today, Today.AddDays(365), "p");
        var gate = new LicenceGate(ent);

        Assert.True(gate.Check(1500, bundled: false).Allowed);      // exactly at the cap is fine
        Assert.False(gate.Check(1501, bundled: false).Allowed);     // one over is denied
        Assert.Contains("1,500", gate.Check(1501, bundled: false).Message);
    }

    [Fact]
    public void Unlimited_tier_allows_any_size()
    {
        var ent = new Entitlement("u@x.com", "X", LicenceTier.CommercialCode, 0, Today, Today.AddDays(365), "c");
        Assert.True(new LicenceGate(ent).Check(1_000_000, bundled: false).Allowed);
    }

    [Fact]
    public void Bundled_networks_bypass_the_limit_even_when_over()
    {
        var ent = new Entitlement("u@x.com", "X", LicenceTier.Personal, 1500, Today, Today.AddDays(365), "p");
        var gate = new LicenceGate(ent);
        Assert.True(gate.Check(50_000, bundled: true).Allowed);     // a bundled case never counts
        Assert.False(gate.Check(50_000, bundled: false).Allowed);  // the same size as a user model is denied
    }

    [Fact]
    public void Free_default_is_personal_1500_and_never_expires()
    {
        Entitlement free = Entitlement.FreeDefault;
        Assert.Equal(LicenceTier.Personal, free.Tier);
        Assert.Equal(1500, free.BusLimit);
        Assert.False(free.IsExpired);
        Assert.True(new LicenceGate(free).Check(1500, bundled: false).Allowed);
        Assert.False(new LicenceGate(free).Check(1501, bundled: false).Allowed);
    }

    [Fact]
    public void Resolution_precedence_is_explicit_then_env_then_file()
    {
        string codeToken = Token(LicenceTier.CommercialCode, 0, new DateOnly(2030, 1, 1));
        string academic = Token(LicenceTier.Academic, 5000, new DateOnly(2030, 1, 1));
        string personal = Token(LicenceTier.Personal, 1500, new DateOnly(2030, 1, 1));

        using ECDsa v1 = Key();
        Assert.Equal(LicenceTier.CommercialCode,
            LicenceGate.Resolve(codeToken, academic, personal, v1).Entitlement.Tier);
        using ECDsa v2 = Key();
        Assert.Equal(LicenceTier.Academic,
            LicenceGate.Resolve(null, academic, personal, v2).Entitlement.Tier);
        using ECDsa v3 = Key();
        Assert.Equal(LicenceTier.Personal,
            LicenceGate.Resolve(null, null, personal, v3).Entitlement.Tier);
    }

    [Fact]
    public void With_no_key_anywhere_resolution_falls_back_to_free()
    {
        using ECDsa v = Key();
        LicenceGate gate = LicenceGate.Resolve(null, null, null, v);
        Assert.Equal(Entitlement.FreeDefault, gate.Entitlement);
        Assert.Null(gate.SourceNote);
    }

    [Fact]
    public void A_present_but_rejected_key_falls_back_to_free_with_a_note()
    {
        using ECDsa v = Key();
        LicenceGate gate = LicenceGate.Resolve("GSK1 tampered", null, null, v);
        Assert.Equal(LicenceTier.Personal, gate.Entitlement.Tier);   // free default
        Assert.NotNull(gate.SourceNote);                              // the mistyped key is surfaced, not silent
    }
}
