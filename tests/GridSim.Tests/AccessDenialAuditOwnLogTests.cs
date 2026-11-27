using System.Net;
using System.Net.Http.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Security;
using GridSim.Web.Site;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// 2.1.074 RC-4-Fixes: a web host with <c>Audit:LogPath</c> and no resident estimator keeps its own audit log and records
/// each access denial in it (one AccessDenied entry), instead of refusing the setting at start-up.
/// </summary>
public sealed class AccessDenialAuditOwnLogTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-ownaudit-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "audit.log");

    public AccessDenialAuditOwnLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private WebApplicationFactory<Program> Host(string role) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Auth:KeysDirectory", Path.Combine(_dir, "keys"));
        b.UseSetting("GridSim:ReplayOnly", "false");
        b.UseSetting("Cycle:Enabled", "false");
        b.UseSetting("GridSim:AnonymousRole", role);
        b.UseSetting("Audit:LogPath", LogPath);
    });

    [Fact]
    public async Task A_denied_solve_on_a_host_without_a_resident_estimator_writes_one_AccessDenied_entry()
    {
        await using var f = Host("Viewer");
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        Assert.False(File.Exists(LogPath));   // nothing is written until a denial

        using HttpResponseMessage r = await c.PostAsJsonAsync("/api/solve", new { @case = "case9" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        IReadOnlyList<AuditLogEntry> entries = AuditLogFile.Load(LogPath);
        AuditLogEntry only = Assert.Single(entries);
        Assert.True(SecurityAudit.TryRead(only, out SecurityEventKind kind, out _));
        Assert.Equal(SecurityEventKind.AccessDenied, kind);
        Assert.Contains("api.solve", only.Summary, StringComparison.Ordinal);
        Assert.True(AuditLogFile.Verify(LogPath, AuditAnchorFile.ReadIfExists(LogPath + ".anchor")).IsValid);

        using HttpResponseMessage again = await c.PostAsJsonAsync("/api/solve", new { @case = "case9" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(2, AuditLogFile.Load(LogPath).Count);
    }

    [Fact]
    public async Task An_allowed_call_writes_nothing()
    {
        await using var f = Host("Analyst");
        using HttpClient c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using HttpResponseMessage r = await c.PostAsJsonAsync("/api/solve", new { @case = "case9" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void The_own_log_audit_reports_itself_configured()
    {
        Assert.False(AccessDenialAudit.None.IsConfigured);
        Assert.True(new AccessDenialAudit(LogPath, null, AuditRecorderOptions.None).IsConfigured);
    }
}
