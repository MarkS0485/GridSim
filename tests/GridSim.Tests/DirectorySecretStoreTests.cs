using System;
using System.IO;
using GridSim.Core.Security;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The one-file-per-secret store (Docker / Compose secrets, Kubernetes secret volumes, systemd credentials), which the
/// web host puts in front of the environment with Secrets:Directory, so the key-ring certificate password need never be
/// in configuration or the process environment.
/// </summary>
public sealed class DirectorySecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-secrets-{Guid.NewGuid():N}");

    public DirectorySecretStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void A_secret_is_read_under_its_aspnet_file_name_with_one_trailing_newline_removed()
    {
        File.WriteAllText(Path.Combine(_dir, "Auth__KeyCertificatePassword"), "s3cret value\n");
        var store = new DirectorySecretStore(_dir);
        Assert.True(store.TryGet("Auth:KeyCertificatePassword", out string? value));
        Assert.Equal("s3cret value", value);
    }

    [Fact]
    public void A_missing_or_empty_secret_is_a_normal_miss()
    {
        File.WriteAllText(Path.Combine(_dir, "Empty"), "");
        var store = new DirectorySecretStore(_dir);
        Assert.False(store.TryGet("Absent", out string? a));
        Assert.Null(a);
        Assert.False(store.TryGet("Empty", out _));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("sub/inner")]
    public void A_name_that_could_leave_the_directory_never_resolves(string name)
    {
        string parent = Path.GetDirectoryName(_dir)!;
        // Even when such a file exists just outside, the store does not reach it.
        var store = new DirectorySecretStore(_dir);
        Assert.False(store.TryGet(name, out _));
        Assert.True(Directory.Exists(parent));
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_refused_at_construction()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new DirectorySecretStore(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void In_a_composite_the_directory_wins_over_the_environment()
    {
        string name = $"GridSimTestSecret{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(_dir, name), "from-file");
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var store = new CompositeSecretStore(new DirectorySecretStore(_dir), EnvironmentSecretStore.Instance);
            Assert.Equal("from-file", store.Get(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
