using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GridSim.Core.IO.Cim.Zip;
using GridSim.Core.IO.Psse;
using GridSim.Core.Security;
using GridSim.Web.Sessions;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: the dashboard's live session needs Operator on the interactive pass, the loopback anonymous default holds only
/// for a direct loopback request, the CGMES archive running total, the PSS/E multi-terminal DC counts and the over-long rpc TCP line.
/// </summary>
public sealed class Rc4FixesSecurityTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-rc4fix-" + Guid.NewGuid().ToString("N"));

    public Rc4FixesSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- the dashboard's live session ----

    [Theory]
    [InlineData(false, false, false, null)]                       // bundled, prerender: unchanged, no gate
    [InlineData(false, true, false, OperationRole.Operator)]      // bundled, interactive: the /ws/sim control band
    [InlineData(false, true, true, null)]                         // a bridge feed or raw replay is only watched (the public replay)
    [InlineData(true, false, false, OperationRole.Operator)]      // private: the higher of the two bands
    [InlineData(true, true, false, OperationRole.Operator)]
    public void Dashboard_session_role_is_operator_on_the_interactive_pass(bool isPrivate, bool interactive, bool bridge, OperationRole? expected)
    {
        Assert.Equal(expected, AccessControl.DashboardSessionRole(isPrivate, interactive, bridge));
    }

    [Fact]
    public void A_bridge_id_is_recognised()
    {
        Assert.True(AccessControl.IsBridgeFeedId("bridge:gb-full@raw"));
        Assert.False(AccessControl.IsBridgeFeedId("case9"));
        Assert.False(AccessControl.IsBridgeFeedId(null));
    }

    [Fact]
    public void A_viewer_opening_a_dashboard_creates_no_session_and_an_operator_may()
    {
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", Path.Combine(_dir, "keys"));
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            b.UseSetting("GridSim:AnonymousRole", "Viewer");
        });
        SessionManager sessions = f.Services.GetRequiredService<SessionManager>();
        AccessControl access = f.Services.GetRequiredService<AccessControl>();

        // The page's start on the interactive pass asks DashboardSessionRole, requires it, and only then calls Sessions.Create.
        // The order is pinned in the next test; here the same steps run against the host's own services.
        OperationRole needed = AccessControl.DashboardSessionRole(isPrivate: false, isInteractive: true, isBridgeFeed: false)!.Value;
        IdentityContext viewer = access.IdentityFor(null, null);
        Assert.Equal(OperationRole.Viewer, viewer.Role);
        Assert.Throws<AccessDeniedException>(() => access.Require(viewer, needed, AccessControl.DashboardOperationPrefix + "session"));
        Assert.Equal(0, sessions.ActiveSessions);

        access.Require(new IdentityContext("op", OperationRole.Operator, "t"), needed, AccessControl.DashboardOperationPrefix + "session");
    }

    [Fact]
    public void The_dashboard_requires_the_role_before_it_creates_the_session()
    {
        string razor = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "Components", "Pages", "Dashboard.razor");
        int gate = razor.IndexOf("DashboardSessionRole(isPrivate, RendererInfo.IsInteractive,", StringComparison.Ordinal);
        int require = razor.IndexOf("Access.Require(", StringComparison.Ordinal);
        int create = razor.IndexOf("Sessions.Create(CaseId", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < require && require < create);
    }

    // ---- the loopback anonymous default, per request ----

    [Fact]
    public void Direct_loopback_means_a_loopback_peer_and_no_proxy_header()
    {
        var none = new HeaderDictionary();
        Assert.True(AccessControl.IsDirectLoopback(IPAddress.Loopback, none));
        Assert.True(AccessControl.IsDirectLoopback(IPAddress.IPv6Loopback, none));
        Assert.False(AccessControl.IsDirectLoopback(IPAddress.Parse("203.0.113.5"), none));
        Assert.True(AccessControl.IsDirectLoopback(null, none));   // no network peer (in-process host): not a remote caller
        foreach (string h in new[] { "X-Forwarded-For", "x-forwarded-host", "X-Forwarded-Proto", "Forwarded" })
            Assert.False(AccessControl.IsDirectLoopback(IPAddress.Loopback, new HeaderDictionary { [h] = "198.51.100.1" }), h);
    }

    [Fact]
    public void The_marker_withholds_only_the_default_anonymous_role()
    {
        var byDefault = new AccessControl(OperationRole.Operator, anonymousRoleIsDefault: true, AccessDenialAudit.None);
        var explicitRole = new AccessControl(OperationRole.Operator, anonymousRoleIsDefault: false, AccessDenialAudit.None);
        var marked = AccessControl.MarkNotDirectLoopback(new System.Security.Claims.ClaimsPrincipal());
        Assert.Equal(OperationRole.Operator, byDefault.IdentityFor(null, null).Role);
        Assert.Equal(OperationRole.Viewer, byDefault.IdentityFor(marked, null).Role);
        Assert.Equal(OperationRole.Operator, explicitRole.IdentityFor(marked, null).Role);
    }

    private sealed class PeerFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (ctx, nxt) =>
                {
                    if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var v) && IPAddress.TryParse(v.ToString(), out var ip))
                        ctx.Connection.RemoteIpAddress = ip;
                    await nxt();
                });
                next(app);
            };
    }

    private async Task<HttpStatusCode> Solve(string peer, params (string, string)[] extra)
    {
        // The default AllowedHosts (loopback names) and an unset GridSim:AnonymousRole: the loopback default rule applies.
        await using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:KeysDirectory", Path.Combine(_dir, "keys-" + Guid.NewGuid().ToString("N")));
            b.UseSetting("GridSim:ReplayOnly", "false");
            b.UseSetting("Cycle:Enabled", "false");
            b.ConfigureServices(s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, PeerFilter>());
        });
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/solve", UriKind.Relative))
        {
            Content = JsonContent.Create(new { @case = "case9" }),
        };
        req.Headers.Host = "localhost";
        req.Headers.Add("X-Test-Peer", peer);
        foreach ((string k, string v) in extra) req.Headers.Add(k, v);
        using HttpResponseMessage r = await c.SendAsync(req);
        return r.StatusCode;
    }

    [Fact]
    public async Task Loopback_peer_without_proxy_headers_holds_the_operator_default()
    {
        Assert.Equal(HttpStatusCode.OK, await Solve("127.0.0.1"));
    }

    [Fact]
    public async Task Loopback_peer_with_a_forwarded_header_does_not_hold_operator()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await Solve("127.0.0.1", ("X-Forwarded-For", "203.0.113.5")));
        Assert.Equal(HttpStatusCode.Forbidden, await Solve("127.0.0.1", ("Forwarded", "for=203.0.113.5")));
    }

    [Fact]
    public async Task A_non_loopback_peer_does_not_hold_operator()
    {
        // The loopback-only host list refuses it (400) before any role applies; either way it never reaches the solver.
        HttpStatusCode s = await Solve("203.0.113.5");
        Assert.True(s is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, s.ToString());
    }

    // ---- the CGMES archive running total ----

    private static byte[] Zip(params (string Name, int Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, int n) in entries)
            {
                using Stream s = z.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                s.Write(new byte[n]);   // zeros: a few dozen bytes on disk
            }
        return ms.ToArray();
    }

    [Fact]
    public void A_cgmes_archive_whose_entries_expand_past_the_running_total_is_refused()
    {
        byte[] zip = Zip(("a_EQ.xml", 50_000), ("b_TP.xml", 50_000), ("c_SV.xml", 50_000));
        Assert.True(zip.Length < 2_000);
        var ex = Assert.Throws<InvalidDataException>(() => CgmesArchiveReader.Read(new MemoryStream(zip), 100_000));
        Assert.Contains("expand to more than 100000 bytes in all", ex.Message, StringComparison.Ordinal);
        Assert.Contains("c_SV.xml", ex.Message, StringComparison.Ordinal);   // the entry that crossed the limit
        // With room for all three the running total is not what stops it (zero bytes are not CGMES, which is another refusal).
        Exception? other = Record.Exception(() => CgmesArchiveReader.Read(new MemoryStream(zip), 200_000));
        Assert.DoesNotContain("expand to more than", other?.Message ?? "", StringComparison.Ordinal);
        // IO-06 (2.1.157): the total is 1 GiB and one document at most the input-file limit (the total was 2 GiB and an entry
        // 512 MiB, a bundle that needed well over 10 GB of memory to read).
        // Re-pinned (ruling 17, R2-12): the total is 512 MiB (an accepted document is held about 8 to 12 times over, so the
        // 1 GiB total could need 8 to 12 GiB, more than the target laptop has).
        Assert.Equal(512L * 1024 * 1024, CgmesArchiveReader.MaxTotalBytes);
        Assert.Equal(GridSim.Core.IO.InputFileLimits.DefaultMaxBytes, CgmesArchiveReader.MaxEntryBytes);
    }

    // ---- PSS/E multi-terminal DC counts ----

    [Fact]
    public void A_huge_multi_terminal_dc_count_allocates_nothing_before_the_data_is_read()
    {
        string raw = PsseMtdcTests.Case().Replace("'MT1', 3, 3, 2,", "'MT1', 700000000, 700000000, 700000000,", StringComparison.Ordinal);
        Assert.Contains("700000000", raw, StringComparison.Ordinal);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Exception? ex = Record.Exception(() => PsseRawParser.ParseWithDiagnostics(raw, "huge"));
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.IsType<InvalidDataException>(ex);   // the lines are missing: the error it always was, not OutOfMemory
        Assert.True(allocated < 256L * 1024 * 1024, $"allocated {allocated} bytes");
    }

    // ---- an over-long TCP line ends the connection ----

    [Fact]
    public async Task An_over_long_line_on_the_listen_port_ends_the_connection()
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        psi.ArgumentList.Add("rpc");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add("127.0.0.1:0");
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(_dir, "crash");
        using Process p = Process.Start(psi)!;
        try
        {
            _ = p.StandardError.ReadToEndAsync();
            string banner = (await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120)))!;
            string listening = JsonDocument.Parse(banner).RootElement.GetProperty("listening").GetString()!;
            int port = int.Parse(listening[(listening.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 120_000;
            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            byte[] chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)'x');
            try
            {
                // More than the line limit and no line feed: the server stops reading at the limit and ends the session.
                for (long sent = 0; sent <= RpcLimit + 4096; sent += chunk.Length) stream.Write(chunk);
                stream.Flush();
            }
            catch (IOException) { /* the server may already have closed its end */ }
            string? first = reader.ReadLine();
            Assert.NotNull(first);
            Assert.Equal("malformed-request", JsonDocument.Parse(first!).RootElement.GetProperty("error").GetProperty("kind").GetString());
            // The connection is closed: end of stream, or a reset because the server closed with the rest of the line unread.
            try { Assert.Null(reader.ReadLine()); }
            catch (IOException) { /* closed by the server */ }
            Assert.False(p.HasExited);        // the host itself carries on
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }

    private const long RpcLimit = 16L * 1024 * 1024;   // GridSim.Cli RpcCommand.MaxLineChars
}
