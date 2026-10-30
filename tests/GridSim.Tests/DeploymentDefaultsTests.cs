using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F05 (release register section 1): the deployment files carry neutral defaults. Before F05, compose.yml mounted
/// a personal home folder and pointed at a private 10.x bridge address, appsettings.json enabled single sign-on
/// against one specific estate, the desktop app defaulted to one specific bridge server, nginx proxied to port 5217
/// while compose published 5540, and compose.yml said `build: .` with no Dockerfile in the repository.
/// These tests keep it that way.
/// </summary>
public class DeploymentDefaultsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    /// <summary>The deployment files a fresh clone uses as-is.</summary>
    private static readonly string[] DeploymentFiles =
    {
        "compose.yml", "Dockerfile", ".dockerignore",
        "src/GridSim.Web/appsettings.json", "src/GridSim.Web/appsettings.Production.json",
        "docs/developer/deploy/README.md", "docs/developer/deploy/nginx-gridsim.example.conf",
        "docs/developer/deploy/gridsim-web.example.service", "docs/developer/deploy/compose.override.example.yml",
    };

    [Fact]
    public void Deployment_files_name_no_personal_path_private_address_or_specific_estate()
    {
        var bad = new List<string>();
        foreach (string f in DeploymentFiles)
        {
            string text = Read(f);
            if (text.Contains("/home/", StringComparison.Ordinal)) bad.Add($"{f}: a /home/ path");
            if (text.Contains("twinscrollgridbalancer", StringComparison.OrdinalIgnoreCase)) bad.Add($"{f}: a specific estate host");
            // RFC 1918 private IPv4 literals (10/8, 172.16/12, 192.168/16) are somebody's network, never a default.
            if (Regex.IsMatch(text, @"\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b"))
                bad.Add($"{f}: a private IPv4 address");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
        Assert.False(File.Exists(Path.Combine(Root(), "docs", "developer", "deploy", "nginx-gridsim.twinscrollgridbalancer.co.uk.conf")));
    }

    [Fact]
    public void Web_sources_carry_no_site_identity_of_their_own()
    {
        // N086 / N087: analytics, share-card metadata, security.txt, footer links and the data-protection application
        // name are Site:* / Auth:* settings; the committed web sources name no estate host, person or estate app name.
        string[] sources =
        {
            "src/GridSim.Web/Components/App.razor", "src/GridSim.Web/Components/Layout/MainLayout.razor",
            "src/GridSim.Web/Program.cs", "src/GridSim.Web/Sso/EstateSso.cs", "src/GridSim.Web/Sso/EstateSsoOptions.cs",
        };
        var bad = new List<string>();
        foreach (string f in sources)
        {
            string text = Read(f);
            foreach (string needle in new[] { "twinscrollgridbalancer", "TSGBWebsite", "googletagmanager.com/gtag/js?id=G-", "x.com/", "github.com/" })
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) bad.Add($"{f}: '{needle}'");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
        // The old static copy would be served by the static-file middleware regardless of configuration.
        Assert.False(File.Exists(Path.Combine(Root(), "src", "GridSim.Web", "wwwroot", ".well-known", "security.txt")));
    }

    [Fact]
    public void One_port_everywhere_compose_systemd_and_nginx()
    {
        Assert.Contains("127.0.0.1:${GRIDSIM_PORT:-5540}:8080", Read("compose.yml"), StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS=http://127.0.0.1:5540", Read("docs/developer/deploy/gridsim-web.example.service"), StringComparison.Ordinal);
        string nginx = Read("docs/developer/deploy/nginx-gridsim.example.conf");
        var ports = Regex.Matches(nginx, @"proxy_pass http://127\.0\.0\.1:(\d+);").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(new[] { "5540" }, ports);
    }

    /// <summary>appsettings files allow // comments (ASP.NET Core configuration skips them); parse them the same way.</summary>
    private static JsonElement Settings(string relative)
    {
        using JsonDocument doc = JsonDocument.Parse(Read(relative), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Single_sign_on_is_off_unless_a_deployment_turns_it_on()
    {
        foreach (string f in new[] { "src/GridSim.Web/appsettings.json", "src/GridSim.Web/appsettings.Production.json" })
        {
            JsonElement sso = Settings(f).GetProperty("Sso");
            Assert.False(sso.GetProperty("Enabled").GetBoolean(), $"{f}: Sso:Enabled must default to false");
            Assert.Equal("", sso.GetProperty("Authority").GetString());
        }
        Assert.Contains("Sso__Enabled: \"${GRIDSIM_SSO_ENABLED:-false}\"", Read("compose.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_defaults_to_replay_only()
    {
        // The solve API and /ws/sim have no authorisation yet (F14), so Production must not expose them by default.
        Assert.True(Settings("src/GridSim.Web/appsettings.Production.json").GetProperty("GridSim").GetProperty("ReplayOnly").GetBoolean());
        Assert.Contains("GridSim__ReplayOnly: \"${GRIDSIM_REPLAY_ONLY:-true}\"", Read("compose.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_app_has_no_default_bridge_server()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): AppSettings.cs moved to src/GridSim.Ui/Settings/AppSettings.cs
        // (same class, same settings file); assertions unchanged. Strengthened: the old WPF copy must be gone, so the
        // desktop app cannot carry a second settings class with a different default.
        string src = Read("src/GridSim.Ui/Settings/AppSettings.cs");
        Assert.Contains("public string BridgeEndpoint { get; set; } = \"\";", src, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root(), "src", "GridSim.Wpf", "AppSettings.cs")));
    }

    [Fact]
    public void Compose_builds_from_a_dockerfile_that_exists_and_builds_strictly()
    {
        string compose = Read("compose.yml");
        Assert.Contains("context: .", compose, StringComparison.Ordinal);
        string docker = Read("Dockerfile");
        // The analyzer severities live in .editorconfig: without it the warnings-as-errors build fails in the image.
        Assert.Contains("COPY Directory.Build.props GridSim.slnx .editorconfig ./", docker, StringComparison.Ordinal);
        Assert.Contains("-p:PublishProfile=portable", docker, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", docker, StringComparison.Ordinal);
        Assert.Contains("compose.override.yml", Read(".gitignore"), StringComparison.Ordinal);
    }
}
