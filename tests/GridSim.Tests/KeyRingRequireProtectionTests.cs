using GridSim.Web.Site;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>2.1.074 RC-4-Fixes: <c>Auth:RequireKeyProtection</c> (opt-in) refuses an unencrypted key ring at start-up; unset keeps the warning only.</summary>
public sealed class KeyRingRequireProtectionTests
{
    private static IConfiguration Config(params (string K, string V)[] s) =>
        new ConfigurationBuilder().AddInMemoryCollection(s.ToDictionary(x => x.K, x => (string?)x.V)).Build();

    [Fact]
    public void Unset_changes_nothing_on_a_linux_ring_without_a_certificate()
    {
        IConfiguration c = Config();
        KeyRingProtection.Plan plan = KeyRingProtection.Resolve(c, isWindows: false);
        Assert.Equal(KeyRingProtection.Protector.None, plan.Protector);
        KeyRingProtection.RequireEncryptedIfAsked(c, plan);   // no throw
        KeyRingProtection.RequireEncryptedIfAsked(Config((KeyRingProtection.RequireProtectionSetting, "false")), plan);
    }

    [Fact]
    public void True_refuses_an_unencrypted_ring_naming_the_setting()
    {
        IConfiguration c = Config((KeyRingProtection.RequireProtectionSetting, "true"));
        KeyRingProtection.Plan plan = KeyRingProtection.Resolve(c, isWindows: false);
        var ex = Assert.Throws<InvalidOperationException>(() => KeyRingProtection.RequireEncryptedIfAsked(c, plan));
        Assert.Contains(KeyRingProtection.RequireProtectionSetting, ex.Message, StringComparison.Ordinal);
        Assert.Contains(KeyRingProtection.CertificatePathSetting, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void True_accepts_a_ring_the_platform_encrypts()
    {
        IConfiguration c = Config((KeyRingProtection.RequireProtectionSetting, "true"));
        KeyRingProtection.Plan windows = KeyRingProtection.Resolve(c, isWindows: true);
        Assert.Equal(KeyRingProtection.Protector.DpapiCurrentUser, windows.Protector);
        KeyRingProtection.RequireEncryptedIfAsked(c, windows);
    }

    [Fact]
    public void A_value_that_is_not_true_or_false_stops_start_up()
    {
        IConfiguration c = Config((KeyRingProtection.RequireProtectionSetting, "yes"));
        KeyRingProtection.Plan plan = KeyRingProtection.Resolve(Config(), isWindows: true);
        Assert.Throws<InvalidOperationException>(() => KeyRingProtection.RequireEncryptedIfAsked(c, plan));
    }
}
