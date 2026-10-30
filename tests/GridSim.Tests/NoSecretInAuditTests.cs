using System;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A secret recorded the correct way never lands in the audit summary (NCSC CAF B3/C1). An input is recorded by
/// its SHA-256, not its content: <see cref="AuditInput.OfText"/> stores only the hash, so a summary built from it
/// carries the hash and not the secret substring.
/// </summary>
public sealed class NoSecretInAuditTests
{
    [Fact]
    public void A_hashed_input_puts_the_hash_in_the_summary_and_never_the_secret()
    {
        // A distinctive secret so a substring search is conclusive.
        string secret = "S3cr3t-" + Guid.NewGuid().ToString("N");

        AuditInput input = AuditInput.OfText("password", secret);

        // The recorded form is the hash, not the content.
        Assert.DoesNotContain(secret, input.Sha256Hex, StringComparison.Ordinal);
        Assert.Equal(64, input.Sha256Hex.Length);

        string summary = AuditEvent.FormatSummary("actor", "estimate", new[] { input }, "status=ok");

        Assert.DoesNotContain(secret, summary, StringComparison.Ordinal);
        Assert.Contains(input.Sha256Hex, summary, StringComparison.Ordinal);
        Assert.Contains("password:" + input.Sha256Hex, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_secret_hashes_identically_every_time()
    {
        string secret = "S3cr3t-" + Guid.NewGuid().ToString("N");
        Assert.Equal(AuditInput.OfText("password", secret).Sha256Hex, AuditInput.OfText("password", secret).Sha256Hex);
    }
}
