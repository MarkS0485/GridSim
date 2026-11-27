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
    /// "r", a backslash and "n"), as a transcript line shows it.</summary>
    internal static string EscapedToCrLf(string jsonLine) =>
        jsonLine.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
}
