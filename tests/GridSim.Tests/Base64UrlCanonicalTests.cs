using System;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-726 (October 2026): the licence token's base64url decoder accepts only the one canonical spelling that
/// <see cref="Base64Url.Encode"/> writes. It used to accept the standard alphabet ('+', '/'), '=' padding and a last
/// character with non-zero spare bits, so one key had several valid spellings.
/// </summary>
public sealed class Base64UrlCanonicalTests
{
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFB })]
    [InlineData(new byte[] { 0xFB, 0xFF })]
    [InlineData(new byte[] { 0xFB, 0xFF, 0xBF })]
    [InlineData(new byte[] { 0x47, 0x53, 0x4B, 0x31, 0x01, 0x00, 0x02 })]
    public void The_canonical_spelling_round_trips(byte[] bytes)
    {
        string s = Base64Url.Encode(bytes);
        Assert.Equal(bytes, Base64Url.Decode(s));
    }

    [Theory]
    [InlineData("-_8")]     // canonical: 0xFB 0xFF (used as the control)
    public void A_url_safe_spelling_decodes(string s)
    {
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Base64Url.Decode(s));
    }

    [Theory]
    [InlineData("+/8")]     // the standard alphabet for 0xFB 0xFF
    [InlineData("-_8=")]    // padded
    [InlineData("-_ 8")]    // inner whitespace (LicenceKey strips whitespace itself before decoding)
    [InlineData("-_9")]     // 0xFB 0xFF with a spare low bit set in the last character
    [InlineData("AB")]      // 0x00 with spare low bits set (canonical is "AA")
    public void A_non_canonical_spelling_is_refused(string s)
    {
        Assert.Throws<FormatException>(() => Base64Url.Decode(s));
    }

    [Fact]
    public void A_length_of_one_modulo_four_is_still_refused()
    {
        Assert.Throws<FormatException>(() => Base64Url.Decode("AAAAA"));
    }
}
