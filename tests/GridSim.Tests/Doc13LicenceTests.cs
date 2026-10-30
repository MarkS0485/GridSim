using System.Security.Cryptography;
using GridSim.Core;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-13 (October 2026), licence items of stream 13. D-DOC-086: a key's issue date is now checked, so a key dated in the
/// future gets the free tier until its day (it applied at once). D-DOC-152: the status line shows the bus limit that
/// actually applies, so an expired (or not yet valid, or other-major) unlimited key no longer prints "unlimited buses".
/// Every test is new.
/// </summary>
public sealed class Doc13LicenceTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static Entitlement Unlimited(DateOnly issued, DateOnly expires) =>
        new("a@example.com", "Example Ltd", LicenceTier.CommercialDeployment, 0, issued, expires, "doc13");

    // ---- D-DOC-086: the issue date ---------------------------------------------------------------------------------

    [Fact]
    public void D086_a_future_dated_key_is_not_yet_valid_and_gets_the_free_tier()
    {
        var gate = new LicenceGate(Unlimited(Today.AddDays(30), Today.AddDays(400)));
        Assert.True(gate.Entitlement.IsNotYetValid);
        Assert.False(gate.Entitlement.IsExpired);

        // Within the free cap: allowed, as for an unlicensed install. Above it: denied, naming the start date.
        Assert.True(gate.Check(Entitlement.FreeBusLimit, bundled: false).Allowed);
        GateResult over = gate.Check(Entitlement.FreeBusLimit + 1, bundled: false);
        Assert.False(over.Allowed);
        Assert.Contains($"starts on {Today.AddDays(30):yyyy-MM-dd} (UTC)", over.Message, StringComparison.Ordinal);
        Assert.Contains($"the free tier allows {Entitlement.FreeBusLimit:N0}", over.Message, StringComparison.Ordinal);
        // Bundled networks never count.
        Assert.True(gate.Check(1_000_000, bundled: true).Allowed);
    }

    [Fact]
    public void D086_the_issue_day_itself_counts_and_the_key_applies_in_full()
    {
        var gate = new LicenceGate(Unlimited(Today, Today.AddDays(30)));
        Assert.False(gate.Entitlement.IsNotYetValid);
        Assert.True(gate.Check(50_000, bundled: false).Allowed);
    }

    [Fact]
    public void D086_the_free_default_is_never_not_yet_valid()
        => Assert.False(Entitlement.FreeDefault.IsNotYetValid);

    [Fact]
    public void D086_a_signed_future_dated_key_round_trips_its_issue_date_and_is_held_back()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string token = LicenceKey.Encode(Unlimited(Today.AddDays(5), Today.AddDays(50)), signer);
        Assert.True(LicenceKey.TryParse(token, signer, out Entitlement e, out string why), why);
        Assert.Equal(Today.AddDays(5), e.Issued);
        Assert.False(new LicenceGate(e).Check(Entitlement.FreeBusLimit + 1, bundled: false).Allowed);
    }

    // ---- D-DOC-152: the status line shows the limit that applies ------------------------------------------------------

    [Fact]
    public void D152_an_expired_unlimited_key_shows_the_free_cap_not_unlimited()
    {
        string line = new LicenceGate(Unlimited(Today.AddYears(-2), Today.AddDays(-1))).Describe();
        Assert.Contains("EXPIRED", line, StringComparison.Ordinal);
        Assert.Contains($"{Entitlement.FreeBusLimit:N0} buses (the free tier applies)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unlimited", line, StringComparison.Ordinal);
    }

    [Fact]
    public void D152_a_not_yet_valid_key_says_so_and_shows_the_free_cap()
    {
        string line = new LicenceGate(Unlimited(Today.AddDays(3), Today.AddDays(300))).Describe();
        Assert.Contains($"NOT YET VALID (starts {Today.AddDays(3):yyyy-MM-dd})", line, StringComparison.Ordinal);
        Assert.Contains($"{Entitlement.FreeBusLimit:N0} buses (the free tier applies)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unlimited", line, StringComparison.Ordinal);
    }

    [Fact]
    public void D152_a_key_for_another_major_version_shows_the_free_cap()
    {
        Entitlement e = Unlimited(Today, Today.AddDays(30)) with { MajorVersion = 1 };
        string line = new LicenceGate(e, null, buildMajor: 2).Describe();
        Assert.Contains($"{Entitlement.FreeBusLimit:N0} buses (the free tier applies)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unlimited", line, StringComparison.Ordinal);
    }

    [Fact]
    public void GR248_FreeRulesApply_is_the_rule_the_status_line_uses()
    {
        // GR-248: the web footer reads this property, so it matches the tooltip (Describe) in every case above.
        Assert.True(new LicenceGate(Unlimited(Today.AddYears(-2), Today.AddDays(-1))).FreeRulesApply);
        Assert.True(new LicenceGate(Unlimited(Today.AddDays(3), Today.AddDays(300))).FreeRulesApply);
        Assert.True(new LicenceGate(Unlimited(Today, Today.AddDays(30)) with { MajorVersion = 1 }, null, buildMajor: 2).FreeRulesApply);
        Assert.False(new LicenceGate(Unlimited(Today, Today.AddDays(30))).FreeRulesApply);
        Assert.False(new LicenceGate(Entitlement.FreeDefault).FreeRulesApply);
    }

    [Fact]
    public void D152_a_current_key_still_shows_its_own_limit()
    {
        Assert.Contains("unlimited buses", new LicenceGate(Unlimited(Today, Today.AddDays(30))).Describe(), StringComparison.Ordinal);
        var capped = new Entitlement("a@example.com", "", LicenceTier.Academic, 5000, Today, Today.AddDays(30), "c");
        Assert.Contains($"{5000:N0} buses", new LicenceGate(capped).Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("free tier applies", new LicenceGate(capped).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void D152_the_free_default_line_is_unchanged()
    {
        // "Personal, unregistered, 1,500 buses, no expiry" (the free default never says the free tier "applies").
        string line = new LicenceGate(Entitlement.FreeDefault).Describe();
        Assert.Contains($"{Entitlement.FreeBusLimit:N0} buses", line, StringComparison.Ordinal);
        Assert.Contains("no expiry", line, StringComparison.Ordinal);
        Assert.DoesNotContain("free tier applies", line, StringComparison.Ordinal);
    }

    // ---- D-DOC-205: the licence file path on every OS (the macOS branch now runs in the release workflow) ------------

    [Fact]
    public void D205_the_licence_file_is_GridSim_licence_gridsim_under_the_per_user_data_folder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(local));
        Assert.Equal(Path.Combine(local, "GridSim", "licence" + LicenceGate.FileExtension), LicenceGate.LicenceFilePath);
        // .NET 8 and later map LocalApplicationData on macOS to ~/Library/Application Support (it was ~/.local/share).
        if (OperatingSystem.IsMacOS())
            Assert.EndsWith(Path.Combine("Library", "Application Support", "GridSim", "licence.gridsim"), LicenceGate.LicenceFilePath, StringComparison.Ordinal);
    }
}
