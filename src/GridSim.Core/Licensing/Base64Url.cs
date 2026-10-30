namespace GridSim.Core.Licensing;

/// <summary>
/// Minimal URL-safe, unpadded Base64 (RFC 4648 §5). Licence keys use this so the token is one clean
/// line with no <c>+</c>/<c>/</c>/<c>=</c> that a shell, URL, or config file might mangle.
/// </summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decode a URL-safe, unpadded Base64 string. Throws <see cref="FormatException"/> on malformed input,
    /// and on any input that is not the one canonical spelling <see cref="Encode"/> would write for the same bytes.</summary>
    public static byte[] Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // GR-726 (October 2026): only the URL-safe alphabet is accepted. The decoder used to map '-' and '_' and then
        // hand the rest to the standard decoder, so a token written with '+', '/', '=' padding or inner whitespace
        // decoded to the same bytes: one key had several valid spellings.
        foreach (char c in s)
        {
            bool urlSafe = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!urlSafe) throw new FormatException("Invalid base64url character (only A-Z, a-z, 0-9, '-' and '_' are allowed, with no padding).");
        }
        string b64 = s.Replace('-', '+').Replace('_', '/');
        int pad = b64.Length % 4;
        if (pad == 2) b64 += "==";
        else if (pad == 3) b64 += "=";
        else if (pad == 1) throw new FormatException("Invalid base64url length.");
        byte[] bytes = Convert.FromBase64String(b64);
        // GR-726: the last character of an unpadded group carries spare low bits that the standard decoder ignores, so
        // up to sixteen spellings decoded to the same bytes. Only the spelling with those bits zero (the one Encode
        // writes) is accepted.
        if (!string.Equals(Encode(bytes), s, StringComparison.Ordinal))
            throw new FormatException("Non-canonical base64url (the unused low bits of the last character must be zero).");
        return bytes;
    }
}
