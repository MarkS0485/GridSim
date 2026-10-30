using System;
using System.Threading.Tasks;
using GridSim.Core.Net;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The resilience options (NCSC CAF B4/B5): the bounded reconnect schedule and the resource limits a
/// <see cref="GridBridgeClient"/> applies. The default validates; the backoff is the exact geometric schedule,
/// capped at the ceiling, and is 1-based; a nonsensical schedule fails loud on Validate; and ApplyTo copies the
/// three resource/transport settings onto a client.
/// </summary>
public sealed class ResilienceOptionsTests
{
    [Fact]
    public void The_default_validates()
    {
        ResilienceOptions.Default.Validate();   // does not throw
    }

    [Fact]
    public void The_first_backoff_is_the_initial_backoff()
    {
        Assert.Equal(ResilienceOptions.Default.InitialBackoff, ResilienceOptions.Default.BackoffDelay(1));
    }

    [Fact]
    public void The_backoff_grows_geometrically_and_caps_at_the_ceiling()
    {
        ResilienceOptions o = ResilienceOptions.Default;   // 1 s initial, x2, 30 s ceiling

        Assert.Equal(TimeSpan.FromSeconds(1), o.BackoffDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), o.BackoffDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), o.BackoffDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), o.BackoffDelay(4));

        // Far enough out the schedule saturates at the ceiling and stays there.
        Assert.Equal(o.MaxBackoff, o.BackoffDelay(20));
        Assert.True(o.BackoffDelay(10) <= o.MaxBackoff);
    }

    [Fact]
    public void The_backoff_is_one_based()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResilienceOptions.Default.BackoffDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResilienceOptions.Default.BackoffDelay(-1));
    }

    [Fact]
    public void A_nonsensical_schedule_fails_loud_on_validate()
    {
        // Ceiling below the initial backoff.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (ResilienceOptions.Default with { MaxBackoff = TimeSpan.FromMilliseconds(1) }).Validate());
        // Multiplier below 1 (the delay would shrink).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (ResilienceOptions.Default with { BackoffMultiplier = 0.5 }).Validate());
        // Non-positive initial backoff.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (ResilienceOptions.Default with { InitialBackoff = TimeSpan.Zero }).Validate());
    }

    [Fact]
    public async Task Apply_to_sets_the_resource_and_transport_settings_on_a_client()
    {
        var options = ResilienceOptions.Default with
        {
            MaxMessageBytes = 123_456,
            MaxInFlightQueries = 7,
            AllowUnencryptedEndpoint = true,
        };

        await using var client = new GridBridgeClient();
        options.ApplyTo(client);

        Assert.Equal(123_456, client.MaxMessageBytes);
        Assert.Equal(7, client.MaxInFlightQueries);
        Assert.True(client.AllowUnencryptedEndpoint);
    }
}
