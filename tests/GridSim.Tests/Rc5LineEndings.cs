// RC5 programme (package linux-q): one shared line-ending normaliser for the byte-pinned tests.
using System.Text;

namespace GridSim.Tests;

/// <summary>
/// The CLI writers and some reader messages end their lines with <see cref="Environment.NewLine"/>, which is CRLF on Windows
/// (where the byte pins were taken) and LF on Linux. A pinned test passes its output through here before hashing or
/// comparing, so that the same content gives the same pin on both systems. The target is CRLF so that every existing pin
/// stays valid. Only the line ending is normalised: any other change to the content still changes the hash.
/// </summary>
internal static class Rc5LineEndings
{
    /// <summary>Every line ending (LF or CRLF) becomes CRLF. A lone CR is left as it is.</summary>
    internal static string ToCrLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>The same for UTF-8 bytes.</summary>
    internal static byte[] ToCrLf(byte[] bytes) => Encoding.UTF8.GetBytes(ToCrLf(Encoding.UTF8.GetString(bytes)));

    /// <summary>The same for the JSON-escaped form of a line ending inside a string value (a backslash and "n", or a backslash,
    /// "r", a backslash and "n"), as a transcript line shows it. Only a real escape is matched: one preceded by an even
    /// number of backslashes (so an escaped backslash before an "n", as in "C:\\net", is left alone). Before 2.1.174 this
    /// replaced raw line feeds, which a one-line transcript never holds, so an rpc study's escaped stdout kept LF on Linux
    /// and macOS where the Windows pins have CRLF (T8a level 2, first seen in public CI).</summary>
    internal static string EscapedToCrLf(string jsonLine)
    {
        string raw = ToCrLf(jsonLine);
        string lf = EscapedCrLf.Replace(raw, "$1\\n");
        return EscapedLf.Replace(lf, "$1\\r\\n");
    }

    private static readonly System.Text.RegularExpressions.Regex EscapedCrLf =
        new(@"(?<!\\)((?:\\\\)*)\\r\\n", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex EscapedLf =
        new(@"(?<!\\)((?:\\\\)*)\\n", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
