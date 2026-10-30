using GridSim.Core.Identity;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The <c>ref</c> verb (2026-10-24), run as the built CLI: each subcommand gives the library's answer, refusals exit 1
/// with the reason, usage errors exit 2, and the verb needs no case or licence.
/// </summary>
public sealed class RefCliProcessTests
{
    [Fact]
    public void Encode_and_decode_give_the_library_answers()
    {
        CliProcess.Result e = CliProcess.RunBuilt("ref", "encode", "0x3C042C");
        Assert.Equal(0, e.ExitCode);
        Assert.Equal("copper.tesla.kron", e.StdOut.Trim());

        CliProcess.Result c = CliProcess.RunBuilt("ref", "encode", "3933228", "--check");
        Assert.Equal(("copper.tesla.kron.sparse", 0), (c.StdOut.Trim(), c.ExitCode));

        CliProcess.Result d = CliProcess.RunBuilt("ref", "decode", "copper.tesla.kron");
        Assert.Equal(0, d.ExitCode);
        Assert.Equal("3933228  0x3C042C", d.StdOut.Trim());

        CliProcess.Result four = CliProcess.RunBuilt("ref", "encode", "16909060", "--words", "4");
        Assert.Equal("watt.volt.joule.tesla", four.StdOut.Trim());
    }

    [Fact]
    public void Scatter_both_ways_and_from_id_give_the_library_answers()
    {
        var s = new ElectricalScatter(0x0123456789ABCDEFUL, 3);
        CliProcess.Result r = CliProcess.RunBuilt("ref", "scatter", "2", "--seed", "0x0123456789ABCDEF");
        Assert.Equal((0, s.RefOf(2)), (r.ExitCode, r.StdOut.Trim()));
        Assert.Equal("aluminium.copper.admittance", r.StdOut.Trim());
        CliProcess.Result back = CliProcess.RunBuilt("ref", "scatter", "aluminium.copper.admittance", "--seed", "0x0123456789ABCDEF");
        Assert.Equal((0, "2"), (back.ExitCode, back.StdOut.Trim()));

        CliProcess.Result id = CliProcess.RunBuilt("ref", "from-id", "GB-SUB-0001");
        Assert.Equal((0, "diode.fourier.cyclotron"), (id.ExitCode, id.StdOut.Trim()));
    }

    [Fact]
    public void A_refused_reference_exits_1_with_the_reason()
    {
        CliProcess.Result r = CliProcess.RunBuilt("ref", "decode", "coper.tesla.kron");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("'coper' (word 1) is not an electrical word. Nearest: copper", r.StdErr, StringComparison.Ordinal);

        CliProcess.Result bad = CliProcess.RunBuilt("ref", "decode", "copper.tesla.kron.ohm", "--check");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("fails its check word", bad.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "ref" }, "a subcommand is needed")]
    [InlineData(new[] { "ref", "twist", "1" }, "unknown subcommand 'twist'")]
    [InlineData(new[] { "ref", "encode", "1", "--colour" }, "unknown flag --colour")]
    [InlineData(new[] { "ref", "encode", "16777216" }, "does not fit in 3 word(s)")]
    [InlineData(new[] { "ref", "encode", "north" }, "is not a whole number")]
    [InlineData(new[] { "ref", "encode", "1", "--words", "9" }, "1 to 8 words")]
    [InlineData(new[] { "ref", "scatter", "1" }, "scatter needs --seed")]
    public void Usage_errors_exit_2_with_the_reason_and_the_usage_line(string[] args, string reason)
    {
        CliProcess.Result r = CliProcess.RunBuilt(args);
        Assert.Equal(2, r.ExitCode);
        Assert.Contains(reason, r.StdErr, StringComparison.Ordinal);
        Assert.Contains("usage: gridsim ref", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verb_help_prints_its_chapter_19_section()
    {
        CliProcess.Result r = CliProcess.RunBuilt("ref", "--help");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("gridsim ref encode <value> [--words N] [--check]", r.StdOut, StringComparison.Ordinal);
    }
}
