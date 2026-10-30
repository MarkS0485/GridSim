using System;
using GridSim.Core.Security;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The secret-store seam (NCSC CAF B3 secrets management): a service resolves a secret through
/// <see cref="ISecretStore"/> rather than reading plain configuration. The environment store reads an environment
/// variable, also in the ASP.NET double-underscore form; the null store always misses; the composite returns the
/// first hit in order; and the Get() extension returns null on a miss.
/// </summary>
public sealed class SecretStoreTests
{
    [Fact]
    public void The_environment_store_reads_a_variable_back()
    {
        const string name = "GridSimTest_SecretA";
        try
        {
            Environment.SetEnvironmentVariable(name, "value-a");
            Assert.True(EnvironmentSecretStore.Instance.TryGet(name, out string? value));
            Assert.Equal("value-a", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void A_logical_name_matches_the_double_underscore_environment_form()
    {
        // Auth:Key is surfaced by the host as Auth__Key (the ASP.NET section separator form).
        const string envForm = "Auth__Key";
        try
        {
            Environment.SetEnvironmentVariable(envForm, "the-key");
            Assert.True(EnvironmentSecretStore.Instance.TryGet("Auth:Key", out string? value));
            Assert.Equal("the-key", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envForm, null);
        }
    }

    [Fact]
    public void A_name_with_an_underscore_matches_the_aspnet_environment_form()
    {
        // GR-719: ASP.NET maps only ':' to "__", so GridSimTest:API_KEY is surfaced as GridSimTest__API_KEY. The
        // store used to double every '_' too and look for GridSimTest__API__KEY instead.
        const string aspNetForm = "GridSimTest__API_KEY";
        try
        {
            Environment.SetEnvironmentVariable(aspNetForm, "aspnet-form");
            Assert.True(EnvironmentSecretStore.Instance.TryGet("GridSimTest:API_KEY", out string? value));
            Assert.Equal("aspnet-form", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(aspNetForm, null);
        }
    }

    [Fact]
    public void The_aspnet_form_wins_over_the_older_doubled_form()
    {
        // GR-719: both spellings set; the ASP.NET one is the documented form and is read first. The older form is
        // still read when it is the only one set, so an existing deployment keeps working.
        const string aspNetForm = "GridSimTest__BOTH_KEY";
        const string legacyForm = "GridSimTest__BOTH__KEY";
        try
        {
            Environment.SetEnvironmentVariable(legacyForm, "legacy-form");
            Assert.True(EnvironmentSecretStore.Instance.TryGet("GridSimTest:BOTH_KEY", out string? legacyOnly));
            Assert.Equal("legacy-form", legacyOnly);

            Environment.SetEnvironmentVariable(aspNetForm, "aspnet-form");
            Assert.True(EnvironmentSecretStore.Instance.TryGet("GridSimTest:BOTH_KEY", out string? both));
            Assert.Equal("aspnet-form", both);
        }
        finally
        {
            Environment.SetEnvironmentVariable(aspNetForm, null);
            Environment.SetEnvironmentVariable(legacyForm, null);
        }
    }

    [Fact]
    public void An_unset_or_whitespace_variable_is_treated_as_absent()
    {
        const string name = "GridSimTest_SecretBlank";
        try
        {
            Environment.SetEnvironmentVariable(name, "   ");
            Assert.False(EnvironmentSecretStore.Instance.TryGet(name, out string? value));
            Assert.Null(value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void The_null_store_always_misses()
    {
        Assert.False(NullSecretStore.Instance.TryGet("anything", out string? value));
        Assert.Null(value);
        Assert.Null(NullSecretStore.Instance.Get("anything"));
    }

    [Fact]
    public void The_composite_store_returns_the_first_hit_in_order()
    {
        const string name = "GridSimTest_SecretComposite";
        try
        {
            Environment.SetEnvironmentVariable(name, "from-environment");
            // Null store first (misses), environment store second (hits).
            var composite = new CompositeSecretStore(NullSecretStore.Instance, EnvironmentSecretStore.Instance);
            Assert.True(composite.TryGet(name, out string? value));
            Assert.Equal("from-environment", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void The_composite_store_misses_when_no_backing_store_holds_the_secret()
    {
        var composite = new CompositeSecretStore(NullSecretStore.Instance, NullSecretStore.Instance);
        Assert.False(composite.TryGet("GridSimTest_NoSuchSecret", out string? value));
        Assert.Null(value);
        Assert.Null(composite.Get("GridSimTest_NoSuchSecret"));
    }

    [Fact]
    public void The_composite_store_requires_at_least_one_backing_store()
    {
        Assert.Throws<ArgumentException>(() => new CompositeSecretStore());
    }

    [Fact]
    public void The_get_extension_returns_the_value_on_a_hit_and_null_on_a_miss()
    {
        const string name = "GridSimTest_SecretGet";
        try
        {
            Environment.SetEnvironmentVariable(name, "got-it");
            Assert.Equal("got-it", EnvironmentSecretStore.Instance.Get(name));
            Assert.Null(EnvironmentSecretStore.Instance.Get("GridSimTest_SecretGetMissing"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
