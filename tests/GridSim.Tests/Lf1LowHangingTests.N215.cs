using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

public partial class Lf1LowHangingTests
{
    // =================================================================================================================
    // N215: the licence status line is plain ASCII (the middle-dot separator is now " | ").
    // =================================================================================================================

    [Fact]
    public void N215_the_free_default_status_line_is_ascii_with_the_pipe_separator()
    {
        string line = new LicenceGate(Entitlement.FreeDefault).Describe();
        Assert.All(line, ch => Assert.True(ch < 128, $"non-ASCII U+{(int)ch:X4} in: {line}"));
        Assert.DoesNotContain((char)0x00B7, line);
        // Tier | who | limit | expiry, in the old order.
        Assert.Equal(" | ", LicenceGate.Sep);
        string[] fields = line.Split(LicenceGate.Sep);
        Assert.Equal(4, fields.Length);
        Assert.Equal("unregistered", fields[1]);
        Assert.Equal("no expiry", fields[3]);
    }

    [Fact]
    public void N215_a_key_status_line_is_ascii_too()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        var e = new Entitlement("a@example.com", "Org", LicenceTier.Academic, 5000, today, today.AddDays(30), "lic-42");
        string line = new LicenceGate(e).Describe();
        Assert.All(line, ch => Assert.True(ch < 128, $"non-ASCII U+{(int)ch:X4} in: {line}"));
        Assert.Contains(" | for GridSim ", line, StringComparison.Ordinal);
        Assert.Contains(" | id lic-42", line, StringComparison.Ordinal);
    }

    [Fact]
    public void N215_the_licence_sources_carry_no_middle_dot()
    {
        foreach (string[] rel in new[]
        {
            new[] { "src", "GridSim.Core", "Licensing", "LicenceGate.cs" },
            new[] { "src", "GridSim.Cli", "LicenceCli.cs" },
        })
        {
            string text = File.ReadAllText(Path.Combine(new[] { CliProcess.Root() }.Concat(rel).ToArray()));
            Assert.DoesNotContain((char)0x00B7, text);
        }
    }
}
