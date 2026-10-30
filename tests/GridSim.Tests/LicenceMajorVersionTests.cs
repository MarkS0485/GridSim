using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F27 (Ruling R1, 2026-10-04): licence keys are bound to the GridSim major version they were issued for, because
/// LICENSE.md section 1 sells a licence per major version. A key carries its major version inside the signed payload
/// (schema v2); a 1.x key works on every 1.x build and is refused on 2.x with a message naming both; legacy v1 keys
/// (issued before the binding) read as 1.x, so existing keys keep working on the current line. Covered here: issue,
/// accept, refuse, tamper, legacy keys, the install refusal and the gate's own belt-and-braces check.
///
/// Every key here is signed by a throw-away P-256 key made for the test; the production private key is never involved.
/// </summary>
public class LicenceMajorVersionTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static Entitlement Commercial(int major) =>
        new Entitlement("user@example.com", "Example Ltd", LicenceTier.CommercialCode, 0, Today, Today.AddDays(365), "maj01")
        { MajorVersion = major };

    /// <summary>The payload text inside a key (between the 7-byte header and the 64-byte signature).</summary>
    private static string PayloadOf(string token)
    {
        byte[] blob = Base64Url.Decode(token[LicenceKey.Prefix.Length..]);
        int len = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(5, 2));
        return Encoding.UTF8.GetString(blob, 7, len);
    }

    /// <summary>A key with an arbitrary payload, signed exactly as LicenceKey.Encode signs (to build legacy v1 keys).</summary>
    private static string SignRaw(string payload, ECDsa signer)
    {
        byte[] p = Encoding.UTF8.GetBytes(payload);
        byte[] signed = new byte[7 + p.Length];
        "GSK1"u8.CopyTo(signed);
        signed[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(signed.AsSpan(5, 2), (ushort)p.Length);
        p.CopyTo(signed, 7);
        byte[] sig = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return LicenceKey.Prefix + Base64Url.Encode(signed.Concat(sig).ToArray());
    }

    // ------------------------------------------------------------------------------------------------- issue ---

    [Fact]
    public void The_build_major_version_is_the_first_component_of_the_release_number()
    {
        Assert.Equal(BuildInfo.MajorOf(BuildInfo.Version), BuildInfo.MajorVersion);
        Assert.Equal(BuildInfo.MajorVersion, LicenceGate.BuildMajorVersion);
        Assert.Equal(1, BuildInfo.MajorOf("1.95.148"));
        Assert.Equal(1, BuildInfo.MajorOf("1.0.0"));
        Assert.Equal(2, BuildInfo.MajorOf("2.0.0"));
        Assert.Equal(10, BuildInfo.MajorOf("10.3.1"));
        // An unreadable version fails loud rather than binding keys to a guess.
        Assert.Throws<FormatException>(() => BuildInfo.MajorOf("x.1.2"));
        Assert.Throws<FormatException>(() => BuildInfo.MajorOf(""));
        Assert.Throws<FormatException>(() => BuildInfo.MajorOf("-1.0.0"));
    }

    [Fact]
    public void An_issued_key_carries_its_major_version_signed_in_a_v2_payload()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string token = LicenceKey.Encode(Commercial(1), signer);
        string payload = PayloadOf(token);
        Assert.StartsWith("v2|", payload, StringComparison.Ordinal);
        Assert.EndsWith("|maj01|1", payload, StringComparison.Ordinal);
        Assert.Equal(9, payload.Split('|').Length);

        Assert.True(LicenceKey.TryParse(token, signer, out Entitlement ent, out string reason), reason);
        Assert.Equal(1, ent.MajorVersion);
        Assert.Equal(Commercial(1), ent);   // every field, the major version included, round-trips

        Assert.True(LicenceKey.TryParse(LicenceKey.Encode(Commercial(2), signer), signer, out Entitlement two, out reason), reason);
        Assert.Equal(2, two.MajorVersion);
    }

    [Fact]
    public void An_entitlement_built_without_a_major_version_is_for_the_line_of_the_build_that_made_it()
    {
        var e = new Entitlement("a@example.com", "", LicenceTier.Academic, 5000, Today, Today.AddDays(10), "x");
        Assert.Equal(BuildInfo.MajorVersion, e.MajorVersion);
    }

    [Fact]
    public void A_key_for_no_major_version_cannot_be_issued()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => LicenceKey.Encode(Commercial(0), signer));
        Assert.Throws<ArgumentException>(() => LicenceKey.Encode(Commercial(-1), signer));
    }

    // ------------------------------------------------------------------------------------------- accept / refuse ---

    [Fact]
    public void A_1x_key_is_accepted_by_a_1x_build_and_refused_by_a_2x_build_naming_both()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string token = LicenceKey.Encode(Commercial(1), signer);

        // Accept: any 1.x build (the gate compares major versions only, so 1.0.0, 1.95.148 and 1.99.0 all say 1).
        LicenceGate one = LicenceGate.Resolve(null, token, null, signer, buildMajor: 1);
        Assert.Equal(LicenceTier.CommercialCode, one.Entitlement.Tier);
        Assert.Null(one.SourceNote);
        Assert.True(one.Check(50_000, bundled: false).Allowed);
        Assert.Contains("for GridSim 1.x", one.Describe(), StringComparison.Ordinal);

        // Refuse: a 2.x build runs at the free tier, saying why, naming the source, both versions and the contact.
        LicenceGate two = LicenceGate.Resolve(null, token, null, signer, buildMajor: 2);
        Assert.Equal(Entitlement.FreeDefault, two.Entitlement);
        Assert.NotNull(two.SourceNote);
        string note = two.SourceNote!;
        Assert.Contains("licence key from " + LicenceGate.EnvVar + " rejected", note, StringComparison.Ordinal);
        Assert.Contains("issued for GridSim 1.x", note, StringComparison.Ordinal);
        Assert.Contains("major version 2", note, StringComparison.Ordinal);
        Assert.Contains("needs a 2.x key", note, StringComparison.Ordinal);
        Assert.Contains("LICENSE.md section 1", note, StringComparison.Ordinal);
        Assert.Contains(LicenceGate.ContactEmail, note, StringComparison.Ordinal);
        Assert.Contains("rejected", two.Describe(), StringComparison.Ordinal);
        // ... and above the free cap a user network is refused there.
        Assert.False(two.Check(Entitlement.FreeBusLimit + 1, bundled: false).Allowed);
    }

    [Fact]
    public void A_key_for_a_newer_line_is_refused_by_an_older_build_too()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LicenceGate g = LicenceGate.Resolve(LicenceKey.Encode(Commercial(2), signer), null, null, signer, buildMajor: 1);
        Assert.Equal(Entitlement.FreeDefault, g.Entitlement);
        Assert.Contains("issued for GridSim 2.x and this build is major version 1", g.SourceNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_key_does_not_fall_through_to_the_next_source()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string old = LicenceKey.Encode(Commercial(1), signer);
        string current = LicenceKey.Encode(Commercial(2), signer);
        // The environment key (1.x) is the one the user chose for this run; on a 2.x build it is refused and named,
        // and the installed 2.x key is not silently used instead (the existing first-source-decides rule).
        LicenceGate g = LicenceGate.Resolve(null, old, current, signer, buildMajor: 2);
        Assert.Equal(Entitlement.FreeDefault, g.Entitlement);
        Assert.Contains(LicenceGate.EnvVar, g.SourceNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void FitsMajorVersion_is_exact_equality()
    {
        Assert.True(LicenceGate.FitsMajorVersion(Commercial(1), 1, out string ok));
        Assert.Equal(string.Empty, ok);
        Assert.False(LicenceGate.FitsMajorVersion(Commercial(1), 2, out string up));
        Assert.Contains("issued for GridSim 1.x", up, StringComparison.Ordinal);
        Assert.False(LicenceGate.FitsMajorVersion(Commercial(3), 2, out _));
    }

    [Fact]
    public void A_gate_built_directly_from_another_lines_key_applies_the_free_tier()
    {
        // Belt and braces in Check: whatever built the gate, a 1.x entitlement on a 2.x build gets the free rules.
        var g = new LicenceGate(Commercial(1), null, buildMajor: 2);
        Assert.True(g.Check(Entitlement.FreeBusLimit, bundled: false).Allowed);
        GateResult over = g.Check(Entitlement.FreeBusLimit + 1, bundled: false);
        Assert.False(over.Allowed);
        Assert.Contains("does not apply: this key was issued for GridSim 1.x", over.Message, StringComparison.Ordinal);
        Assert.Contains($"{Entitlement.FreeBusLimit:N0}", over.Message, StringComparison.Ordinal);
        Assert.True(g.Check(1_000_000, bundled: true).Allowed);   // bundled networks never count
        // The free default is never version-bound.
        Assert.True(new LicenceGate(Entitlement.FreeDefault, null, buildMajor: 7).Check(Entitlement.FreeBusLimit, bundled: false).Allowed);
        Assert.DoesNotContain("for GridSim", new LicenceGate(Entitlement.FreeDefault).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_a_key_for_another_line_fails_with_the_reason_and_writes_nothing()
    {
        // Install verifies against the embedded production key, so a test key is refused there on its signature
        // before the version check is reached; the version rule Install applies is FitsMajorVersion with this
        // build's major version, pinned above. Here: the refusal is a LicenceException and nothing is written.
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string other = LicenceKey.Encode(Commercial(BuildInfo.MajorVersion + 1), signer);
        // Against a temp licence file, never the real installed one (which a regression that wrote before verifying
        // would have overwritten); the assertion is unchanged: the refusal writes nothing.
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-licence-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "licence" + LicenceGate.FileExtension);
        try
        {
            Assert.Throws<LicenceException>(() => LicenceGate.Install(other, path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------- legacy keys ---

    [Fact]
    public void A_legacy_v1_key_is_a_1x_key_it_works_on_1x_and_is_refused_on_2x()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        int issued = Today.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        string v1 = SignRaw($"v1|user@example.com|Example Ltd|code|0|{issued}|{issued + 365}|old01", signer);

        Assert.True(LicenceKey.TryParse(v1, signer, out Entitlement ent, out string reason), reason);
        Assert.Equal(LicenceKey.LegacyMajorVersion, ent.MajorVersion);
        Assert.Equal(1, LicenceKey.LegacyMajorVersion);
        Assert.Equal("old01", ent.LicenceId);

        Assert.Equal("old01", LicenceGate.Resolve(v1, null, null, signer, buildMajor: 1).Entitlement.LicenceId);
        LicenceGate two = LicenceGate.Resolve(v1, null, null, signer, buildMajor: 2);
        Assert.Equal(Entitlement.FreeDefault, two.Entitlement);
        Assert.Contains("issued for GridSim 1.x", two.SourceNote!, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_schema_and_field_counts_are_refused_naming_why()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // v1 with a ninth field, v2 without one, a v3 schema, and a v2 key with a bad major version.
        Assert.False(LicenceKey.TryParse(SignRaw("v1|a|b|code|0|1|2|id|1", signer), signer, out _, out string r1));
        Assert.Equal("malformed licence payload", r1);
        Assert.False(LicenceKey.TryParse(SignRaw("v2|a|b|code|0|1|2|id", signer), signer, out _, out string r2));
        Assert.Equal("malformed licence payload", r2);
        Assert.False(LicenceKey.TryParse(SignRaw("v3|a|b|code|0|1|2|id|1", signer), signer, out _, out string r3));
        Assert.Equal("unsupported licence schema 'v3'", r3);
        foreach (string bad in new[] { "0", "-1", "x", "", "+1", " 1" })
        {
            Assert.False(LicenceKey.TryParse(SignRaw($"v2|a|b|code|0|1|2|id|{bad}", signer), signer, out _, out string r4));
            Assert.Equal("invalid major version in licence", r4);
        }
    }

    // ------------------------------------------------------------------------------------------------ tamper ---

    [Fact]
    public void Editing_the_major_version_breaks_the_signature()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string token = LicenceKey.Encode(Commercial(1), signer);
        byte[] blob = Base64Url.Decode(token[LicenceKey.Prefix.Length..]);
        int len = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(5, 2));
        int majorAt = 7 + len - 1;                 // the payload's last byte is the major version digit
        Assert.Equal((byte)'1', blob[majorAt]);
        blob[majorAt] = (byte)'2';                 // "upgrade" the key to 2.x
        string forged = LicenceKey.Prefix + Base64Url.Encode(blob);
        Assert.False(LicenceKey.TryParse(forged, signer, out Entitlement ent, out string reason));
        Assert.Contains("signature is invalid", reason, StringComparison.Ordinal);
        Assert.Equal(Entitlement.FreeDefault, ent);
        // ... and a 2.x build given the forged key runs at the free tier.
        Assert.Equal(Entitlement.FreeDefault, LicenceGate.Resolve(forged, null, null, signer, buildMajor: 2).Entitlement);
    }

    [Fact]
    public void Every_single_byte_tamper_of_a_v2_key_is_rejected()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] blob = Base64Url.Decode(LicenceKey.Encode(Commercial(1), signer)[LicenceKey.Prefix.Length..]);
        for (int i = 0; i < blob.Length; i++)
        {
            byte[] t = (byte[])blob.Clone();
            t[i] ^= 0x01;
            Assert.False(LicenceKey.TryParse(LicenceKey.Prefix + Base64Url.Encode(t), signer, out _, out string reason),
                $"flipping one bit of byte {i} of {blob.Length} was accepted");
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }
}
