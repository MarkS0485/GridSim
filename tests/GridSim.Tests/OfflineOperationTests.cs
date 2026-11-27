using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Offline operation (RC5 package TEL, part two): GridSim starts, runs and stops with no internet connection. The only
/// network code is in a short list of features the user starts explicitly (the listed files below), and the web app's
/// pages load nothing from another origin. Pinned three ways:
/// <list type="bullet">
/// <item>a source scan: no network API outside the documented allowlist;</item>
/// <item>the web app's served HTML, CSS and JavaScript name no external origin to load from;</item>
/// <item>the CLI solves case14 with every proxy variable pointed at an address that answers nothing. This is a
/// proxy-based check: an HTTP request made through the default handler would fail, so a run that needed one would
/// fail. It is not a full network-isolation proof (a raw socket ignores proxy settings); the source scan covers that.</item>
/// </list>
/// </summary>
public sealed class OfflineOperationTests
{
    private static string Root() => CliProcess.Root();

    private static string Rel(string path) => Path.GetRelativePath(Root(), path).Replace('\\', '/');

    /// <summary>
    /// The only source files allowed to use a network API, each a feature the user starts explicitly (category b in
    /// technical chapter 35, section Offline operation). Adding a file here needs the same row in that section.
    /// </summary>
    private static readonly string[] Allowlist =
    [
        "src/GridSim.Cli/InterposeTransport.cs",          // interpose: the RTU interposer's TCP listener
        "src/GridSim.Cli/RpcCommand.cs",                  // rpc: the local RPC listener
        "src/GridSim.Core/Net/GridBridgeClient.cs",       // bridge sessions (desktop and web), to a configured endpoint
        "src/GridSim.Core/Synchrophasor/C37118LiveTransport.cs", // pmu-ingest live capture (TCP or UDP)
        "src/GridSim.Web/Sessions/SimSession.cs",         // a bridge session's /meta request, to the same endpoint
    ];

    // Network-capable .NET APIs. \bSocket\b does not match WebSocket (the web host's own, inbound socket) or the
    // System.Net.Sockets namespace, which is also used for address parsing.
    private static readonly Regex NetworkApi = new(
        @"\b(HttpClient|HttpClientHandler|SocketsHttpHandler|HttpMessageHandler|HttpWebRequest|WebRequest|WebClient|"
        + @"Socket|TcpClient|TcpListener|UdpClient|Dns|ClientWebSocket|SmtpClient|NetworkInformation|HttpListener|"
        + @"NetworkStream|Ping)\b",
        RegexOptions.CultureInvariant);

    // Code without comment lines and trailing line comments, so prose may name an API.
    private static string Code(string file) => string.Join('\n', File.ReadAllLines(file)
        .Select(l => l.TrimStart())
        .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith('*') && !l.StartsWith("@*", StringComparison.Ordinal))
        .Select(l => Regex.Replace(l, @"\s//\s.*$", "")));

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !Rel(f).Contains("/obj/", StringComparison.Ordinal) && !Rel(f).Contains("/bin/", StringComparison.Ordinal));

    [Fact]
    public void No_network_api_appears_outside_the_allowlist_of_user_started_features()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string f in Sources())
        {
            Match m = NetworkApi.Match(Code(f));
            if (!m.Success) continue;
            string rel = Rel(f);
            found.Add(rel);
            Assert.True(Allowlist.Contains(rel), $"{rel} uses the network API {m.Value}; it is not on the offline allowlist");
        }
        // Every allowlisted file still uses the network (a stale entry would hide a moved call site).
        foreach (string a in Allowlist) Assert.Contains(a, found);
    }

    [Fact]
    public void The_allowlist_is_documented_in_technical_chapter_35()
    {
        string ch35 = File.ReadAllText(Path.Combine(Root(), "docs", "technical", "0035 Deployment and security.md"));
        int at = ch35.IndexOf("### Offline operation", StringComparison.Ordinal);
        Assert.True(at > 0, "technical chapter 35 has no Offline operation section");
        string section = ch35[at..ch35.IndexOf("\n## ", at, StringComparison.Ordinal)];
        foreach (string a in Allowlist) Assert.Contains("`" + a + "`", section, StringComparison.Ordinal);
        Assert.Contains("OfflineOperationTests", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_licence_check_is_offline()
    {
        foreach (string f in Directory.GetFiles(Path.Combine(Root(), "src", "GridSim.Core", "Licensing"), "*.cs"))
        {
            string code = Code(f);
            Assert.DoesNotMatch(NetworkApi, code);
            Assert.DoesNotMatch(new Regex(@"https?://"), code);
        }
    }

    // ---- The web app's pages ----------------------------------------------------------------------------------------

    private static WebApplicationFactory<Program> Factory()
    {
        string keys = TestTemp.Under("gridsim-offline-keys-" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Auth:KeysDirectory", keys));
    }

    // Absolute URLs that may appear inside the framework's own script as text, never loaded: the SVG namespace, and
    // documentation links in error messages.
    private static readonly string[] TextOnlyHosts = ["www.w3.org", "aka.ms", "learn.microsoft.com"];

    private static IEnumerable<string> AbsoluteUrls(string text) =>
        Regex.Matches(text, @"(?<![A-Za-z0-9])(https?:)?//[A-Za-z0-9.-]+\.[A-Za-z]{2,}").Select(m => m.Value);

    private static string HostOf(string url) => url[(url.IndexOf("//", StringComparison.Ordinal) + 2)..];

    [Fact]
    public async Task The_served_html_css_and_javascript_name_no_external_origin()
    {
        await using WebApplicationFactory<Program> f = Factory();
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage home = await c.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        string html = await home.Content.ReadAsStringAsync();

        // Every src and href on the page is relative to the app.
        var assets = new List<string>();
        foreach (Match m in Regex.Matches(html, @"\b(src|href)\s*=\s*""([^""]*)"""))
        {
            string v = m.Groups[2].Value;
            Assert.False(v.StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(v, "^[A-Za-z][A-Za-z0-9+.-]*:"),
                $"the home page loads or links {v}");
            if (v.EndsWith(".js", StringComparison.Ordinal) || v.EndsWith(".css", StringComparison.Ordinal)) assets.Add(v);
        }
        foreach (string url in AbsoluteUrls(html))
            Assert.True(HostOf(url) == "www.w3.org", $"the home page names the external origin {url}");
        Assert.Contains("_framework/blazor.web.js", assets);
        Assert.Contains("css/app.css", assets);

        // Every script and stylesheet the page loads, plus every file under wwwroot/js and wwwroot/css.
        foreach (string f2 in Directory.GetFiles(Path.Combine(Root(), "src", "GridSim.Web", "wwwroot"), "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".js", StringComparison.Ordinal) || p.EndsWith(".css", StringComparison.Ordinal)))
            assets.Add(Rel(f2)["src/GridSim.Web/wwwroot/".Length..]);
        foreach (string asset in assets.Distinct(StringComparer.Ordinal))
        {
            using HttpResponseMessage r = await c.GetAsync(new Uri("/" + asset.TrimStart('/'), UriKind.Relative));
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{asset} answered {(int)r.StatusCode}");
            string body = await r.Content.ReadAsStringAsync();
            bool framework = asset.StartsWith("_framework/", StringComparison.Ordinal);
            foreach (string url in AbsoluteUrls(body))
            {
                string host = HostOf(url);
                Assert.True(host == "www.w3.org" || (framework && TextOnlyHosts.Contains(host)),
                    $"{asset} names the external origin {url}");
            }
            if (asset.EndsWith(".css", StringComparison.Ordinal))
                Assert.DoesNotMatch(new Regex(@"@import\s+(url\()?\s*['""]?(https?:)?//"), body);
        }
    }

    // ---- The CLI with every proxy pointed nowhere --------------------------------------------------------------------

    [Fact]
    public void The_cli_solves_case14_with_every_proxy_pointed_at_an_address_that_answers_nothing()
    {
        // Port 9 (discard) on the loopback address: nothing listens there, so any request sent through a proxy fails at
        // once. Both spellings, as .NET and other runtimes read either.
        const string Nowhere = "http://127.0.0.1:9";
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = Nowhere, ["HTTPS_PROXY"] = Nowhere, ["ALL_PROXY"] = Nowhere, ["NO_PROXY"] = "",
            ["http_proxy"] = Nowhere, ["https_proxy"] = Nowhere, ["all_proxy"] = Nowhere, ["no_proxy"] = "",
        };
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "pf", "case14");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.False(string.IsNullOrWhiteSpace(r.StdOut), r.ToString());
    }
}
