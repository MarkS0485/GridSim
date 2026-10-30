using System.Text.RegularExpressions;

namespace GridSim.Web.Api;

/// <summary>
/// GR-721 (October 2026): the text of an error frame that reaches an anonymous <c>/ws/sim</c> client. A dispatch
/// failure used to send <c>ex.Message</c> as it was, and a message from a case load or a run folder (an IOException,
/// an InvalidDataException, a loader error) can name a path on the server (C:\srv\gridsim\results\..., /home/...),
/// which tells an anonymous visitor how the host is laid out. The message is still sent, because the refusals the
/// session code raises on purpose (unknown case, unknown action, no session yet, session limit) are what the client
/// needs to correct its frame, but every absolute path in it is replaced with <c>&lt;path&gt;</c>. The full message is
/// logged on the server.
/// <para>
/// What counts as a path: a drive-letter path (<c>C:\x</c>, <c>C:/x</c>), a UNC path (<c>\\server\share</c>), and a
/// rooted Unix path of three or more segments (<c>/home/user/x</c>); a short route such as <c>/ws/sim</c> or
/// <c>/api/solve</c>, which the refusal texts quote, is two segments and is kept. Deterministic: the same message always
/// gives the same text.
/// </para>
/// </summary>
internal static class ClientErrorText
{
    // A drive-letter or UNC path, up to the next white space, quote or angle bracket.
    private static readonly Regex WindowsPath =
        new(@"(?<![\w])(?:[A-Za-z]:[\\/]|\\\\)[^\s'""<>|]*", RegexOptions.CultureInvariant);

    // A rooted Unix path with at least three segments ("/a/b/c", "/a/b/"), not preceded by a word character, a dot, a
    // colon or a slash (so a URL's "//host/x/y" and a relative "a/b/c" are not touched).
    private static readonly Regex UnixPath =
        new(@"(?<![\w.:/])/(?:[\w.-]+/){2,}[\w.-]*", RegexOptions.CultureInvariant);

    /// <summary>The message of <paramref name="ex"/> with every absolute path replaced by <c>&lt;path&gt;</c>.</summary>
    public static string For(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return Scrub(ex.Message);
    }

    /// <summary><paramref name="message"/> with every absolute path replaced by <c>&lt;path&gt;</c>.</summary>
    public static string Scrub(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string s = WindowsPath.Replace(message, "<path>");
        return UnixPath.Replace(s, "<path>");
    }
}
