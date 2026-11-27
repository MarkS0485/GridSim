using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Web.Site;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Offline operation, the source scan widened to what technical chapter 35 claims (<see cref="OfflineOperationTests"/>
/// scans the C# and Razor files): every network API the chapter lists is searched for in every source file type the
/// apps are built from (.cs, .razor, .cshtml, .js, .css, .html, .axaml), the browser's own network calls are searched for
/// in the files the browser runs, and every setting that puts an operator-configured URL in a page is in the chapter's
/// inventory.
/// </summary>
public sealed class OfflineOperationCoverageTests
{
    private static string Root() => CliProcess.Root();

    private static string Rel(string path) => Path.GetRelativePath(Root(), path).Replace('\\', '/');

    /// <summary>The file types scanned, as the chapter lists them.</summary>
    private static readonly string[] Extensions = [".cs", ".razor", ".cshtml", ".js", ".css", ".html", ".axaml"];

    /// <summary>The (b) table of the chapter: the only files allowed to use a .NET network API (as in OfflineOperationTests).</summary>
    private static readonly string[] Allowlist =
    [
        "src/GridSim.Cli/InterposeTransport.cs",
        "src/GridSim.Cli/RpcCommand.cs",
        "src/GridSim.Core/Net/GridBridgeClient.cs",
        "src/GridSim.Core/Synchrophasor/C37118LiveTransport.cs",
        "src/GridSim.Web/Sessions/SimSession.cs",
    ];

    /// <summary>The network APIs the chapter lists, word for word, and the further .NET types that open a connection or
    /// fetch a remote resource (TLS and QUIC streams, the negotiate stream, the XML URL resolver).</summary>
    private static readonly string[] DocumentedApis =
    [
        "HttpClient", "WebRequest", "HttpWebRequest", "WebClient", "Socket", "TcpClient", "TcpListener", "UdpClient", "Dns",
        "ClientWebSocket", "SmtpClient", "NetworkInformation", "HttpListener", "NetworkStream", "Ping",
    ];

    private static readonly Regex NetworkApi = new(
        @"\b(" + string.Join('|', DocumentedApis.Concat(new[] { "HttpClientHandler", "SocketsHttpHandler", "HttpMessageHandler",
            "SslStream", "NegotiateStream", "QuicConnection", "QuicListener", "XmlUrlResolver" })) + @")\b",
        RegexOptions.CultureInvariant);

    /// <summary>The browser's network calls: fetch, XMLHttpRequest, WebSocket, EventSource, sendBeacon, importScripts.</summary>
    private static readonly Regex BrowserNetworkApi = new(
        @"\b(fetch\s*\(|XMLHttpRequest\b|WebSocket\s*\(|new\s+WebSocket\b|EventSource\b|sendBeacon\b|importScripts\b)",
        RegexOptions.CultureInvariant);

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
            .Where(f => !Rel(f).Contains("/obj/", StringComparison.Ordinal) && !Rel(f).Contains("/bin/", StringComparison.Ordinal));

    /// <summary>
    /// The code of a file, comments removed so prose may name an API: line comments (a line starting with //, *, @*, or
    /// a trailing " // ") in every type, and block comments (/* */, &lt;!-- --&gt;, @* *@) in the types where they cannot
    /// sit inside a C# string (.js, .css, .html, .axaml, .cshtml).
    /// </summary>
    private static string Code(string file)
    {
        string text = File.ReadAllText(file);
        string ext = Path.GetExtension(file);
        if (ext is ".js" or ".css" or ".html" or ".axaml" or ".cshtml")
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        }
        return string.Join('\n', text.Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith('*') && !l.StartsWith("@*", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l, @"\s//\s.*$", "")));
    }

    [Fact]
    public void Every_scanned_file_type_is_present_in_src_or_named_as_absent()
    {
        // The scan really reads each type the apps are built from: .cs, .razor, .js, .css and .axaml files exist today.
        var present = Sources().Select(f => Path.GetExtension(f)).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (string ext in new[] { ".cs", ".razor", ".js", ".css", ".axaml" })
            Assert.Contains(ext, present);
    }

    [Fact]
    public void No_dotnet_network_api_appears_in_any_source_file_type_outside_the_allowlist()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        int scanned = 0;
        foreach (string f in Sources())
        {
            scanned++;
            Match m = NetworkApi.Match(Code(f));
            if (!m.Success) continue;
            string rel = Rel(f);
            found.Add(rel);
            Assert.True(Allowlist.Contains(rel), $"{rel} uses the network API {m.Value}; it is not on the offline allowlist");
        }
        Assert.True(scanned > 1000, $"only {scanned} source files were scanned");
        foreach (string a in Allowlist) Assert.Contains(a, found);
    }

    [Fact]
    public void The_browser_makes_no_network_call_but_same_origin_fetches_of_the_map_overlays()
    {
        // The only browser network call is map-geo.js's fetch of the map overlays, under the relative folder geo/, which the
        // Content-Security-Policy (connect-src 'self') also keeps to this app.
        const string MapGeo = "src/GridSim.Web/wwwroot/js/map-geo.js";
        foreach (string f in Sources().Where(f => Path.GetExtension(f) is ".js" or ".html" or ".razor" or ".cshtml"))
        {
            string rel = Rel(f);
            string code = Code(f);
            foreach (Match m in BrowserNetworkApi.Matches(code))
                Assert.True(rel == MapGeo && m.Value.StartsWith("fetch", StringComparison.Ordinal),
                    $"{rel} makes the browser network call {m.Value}");
        }

        string geo = Code(Path.Combine(Root(), MapGeo));
        Assert.Single(Regex.Matches(geo, @"\bfetch\s*\("));
        Assert.Contains("resp = await fetch(url, { cache: 'no-cache' });", geo, StringComparison.Ordinal);
        Assert.Contains("const BASE = 'geo/';", geo, StringComparison.Ordinal);
        // Every call of the one fetch helper passes BASE plus a file name, so every request is relative to the app.
        MatchCollection calls = Regex.Matches(geo, @"fetchJsonOrNull\(([^)]*)\)");
        Assert.NotEmpty(calls);
        foreach (Match c in calls)
            Assert.True(c.Groups[1].Value == "url" || c.Groups[1].Value.StartsWith("BASE + ", StringComparison.Ordinal),
                $"map-geo.js fetches {c.Groups[1].Value}");
    }

    [Fact]
    public void No_browser_side_file_names_an_absolute_url()
    {
        foreach (string f in Sources().Where(f => Path.GetExtension(f) is ".js" or ".css" or ".html"))
            Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z0-9])(https?:)?//[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), Code(f));
    }

    // ---- Operator-configured URLs ----------------------------------------------------------------------------------

    private static string OfflineSection()
    {
        string ch35 = File.ReadAllText(Path.Combine(Root(), "docs", "technical", "0035 Deployment and security.md")).Replace("\r\n", "\n");
        int at = ch35.IndexOf("### Offline operation", StringComparison.Ordinal);
        Assert.True(at > 0, "technical chapter 35 has no Offline operation section");
        return ch35[at..ch35.IndexOf("\n## ", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void Every_setting_that_puts_a_configured_url_in_a_page_is_in_the_inventory()
    {
        string section = OfflineSection();
        // Every URL-valued site setting, found by reflection, so a new one must be added to the inventory.
        string[] urlSettings = typeof(SiteSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.EndsWith("Url", StringComparison.Ordinal) || p.Name.EndsWith("Links", StringComparison.Ordinal))
            .Select(p => SiteSettings.Section + ":" + p.Name).ToArray();
        Assert.Contains("Site:ShareImageUrl", urlSettings);
        Assert.Contains("Site:FooterLinks", urlSettings);
        foreach (string s in urlSettings.Concat(new[] { "Sso:Authority", "Sso:PublicOrigin", "GRIDSIM_LICENCE_FILE" }))
            Assert.Contains("`" + s + "`", section, StringComparison.Ordinal);
        Assert.Equal("Sso", WebSite.Data.Sso.EstateSsoOptions.Section);
        Assert.NotNull(typeof(WebSite.Data.Sso.EstateSsoOptions).GetProperty("Authority"));
        Assert.NotNull(typeof(WebSite.Data.Sso.EstateSsoOptions).GetProperty("PublicOrigin"));
    }

    [Fact]
    public void The_chapter_names_every_scanned_file_type_and_every_api_it_lists()
    {
        string section = OfflineSection();
        foreach (string ext in Extensions) Assert.Contains("`" + ext + "`", section, StringComparison.Ordinal);
        foreach (string api in DocumentedApis) Assert.Contains("`" + api + "`", section, StringComparison.Ordinal);
        Assert.Contains("OfflineOperationCoverageTests", section, StringComparison.Ordinal);
    }
}
