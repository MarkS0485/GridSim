using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-713: a key wrapped across lines in a .gridsim file is read whole (its lines are joined up to the next blank or
/// comment line), as the same text is from GRIDSIM_LICENCE; only the first line used to be kept.
/// </summary>
public sealed class LicenceFileTokenTests
{
    [Fact]
    public void A_one_line_key_with_a_comment_header_is_read_as_before()
        => Assert.Equal("GSK1.abc.def", LicenceGate.ExtractToken("# GridSim licence\n# issued to a tester\n\nGSK1.abc.def\n"));

    [Fact]
    public void A_wrapped_key_is_joined_up_to_the_next_blank_or_comment_line()
    {
        Assert.Equal("GSK1.abcdef.ghijkl", LicenceGate.ExtractToken("# header\r\nGSK1.abc\r\ndef.ghi\r\njkl\r\n\r\nnot part of the key\r\n"));
        Assert.Equal("GSK1.abcdef", LicenceGate.ExtractToken("GSK1.abc\ndef\n# trailing comment\nignored"));
    }

    [Fact]
    public void Content_with_no_key_line_is_returned_trimmed()
        => Assert.Equal("# only a comment", LicenceGate.ExtractToken("  # only a comment  \n"));
}
