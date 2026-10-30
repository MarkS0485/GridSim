using GridSim.Core.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2026-10-20 follow-up (bridge, seen end to end on the real lake): the connect line in both desktop apps printed the
/// bridge endpoint verbatim, so a URL carrying a token in its query string, a password in its user-info or a secret in
/// its fragment would have reached the event log and the status bar. Both apps now write the endpoint through
/// <see cref="BridgeProtocol.DisplayEndpoint"/>. These tests pin its exact output and that both apps use it.
/// </summary>
public class BridgeEndpointRedactionTests
{
    [Theory]
    // Nothing secret: exactly as given (so the ordinary connect line is unchanged byte for byte).
    [InlineData("ws://127.0.0.1:8770/ws/bridge", "ws://127.0.0.1:8770/ws/bridge")]
    [InlineData("wss://simstream.twinscrollgridbalancer.co.uk/ws/bridge", "wss://simstream.twinscrollgridbalancer.co.uk/ws/bridge")]
    [InlineData("wss://example.invalid", "wss://example.invalid")]
    // A query string (the usual place for an access token) is replaced, the server and path kept.
    [InlineData("wss://example.invalid/ws/bridge?token=abc123", "wss://example.invalid/ws/bridge?<redacted>")]
    [InlineData("wss://example.invalid:9443/ws/bridge?a=1&b=2", "wss://example.invalid:9443/ws/bridge?<redacted>")]
    // User-info (user:password@) is replaced; the default port stays implicit.
    [InlineData("wss://user:secret@example.invalid/ws/bridge", "wss://<redacted>@example.invalid/ws/bridge")]
    // A fragment is replaced.
    [InlineData("ws://127.0.0.1:8770/ws/bridge#key=xyz", "ws://127.0.0.1:8770/ws/bridge#<redacted>")]
    // All three at once.
    [InlineData("wss://u:p@example.invalid:8443/b?t=1#f", "wss://<redacted>@example.invalid:8443/b?<redacted>#<redacted>")]
    // IPv6 keeps its brackets.
    [InlineData("ws://[::1]:8770/ws/bridge?token=abc", "ws://[::1]:8770/ws/bridge?<redacted>")]
    // Not an absolute URI: nothing of it is shown.
    [InlineData("not a url", "<redacted>")]
    [InlineData("", "<redacted>")]
    public void The_endpoint_shown_keeps_the_server_and_path_and_hides_any_secret(string endpoint, string shown)
    {
        Assert.Equal(shown, BridgeProtocol.DisplayEndpoint(endpoint));
    }

    [Fact]
    public void A_null_endpoint_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeProtocol.DisplayEndpoint(null!));
    }

    [Fact]
    public void Both_desktop_apps_write_the_connect_line_through_the_redaction()
    {
        string root = RepoRoot();
        string wpf = File.ReadAllText(Path.Combine(root, "src", "GridSim.Wpf", "MainWindow.xaml.cs"));
        string ui = File.ReadAllText(Path.Combine(root, "src", "GridSim.Ui", "Hosting", "BridgeSession.cs"));
        Assert.Contains("$\"Connected to bridge {BridgeProtocol.DisplayEndpoint(s.Bridge!)}  -  streaming", wpf, StringComparison.Ordinal);
        Assert.Contains("$\"Connected to bridge {BridgeProtocol.DisplayEndpoint(endpoint)}  -  streaming", ui, StringComparison.Ordinal);
        // And neither app writes the raw endpoint into that line any more.
        Assert.DoesNotContain("$\"Connected to bridge {s.Bridge}", wpf, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"Connected to bridge {endpoint}", ui, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
