using System;
using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.Security;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The role guard (NCSC CAF B2 identity and access control): a fail-loud role gate at the service seam. A higher
/// band includes every lower one; a caller below the required band is refused with an
/// <see cref="AccessDeniedException"/> carrying the required and actual roles; a denial with an audit log writes
/// exactly one AccessDenied security entry, and an allow writes nothing. It is additive to, never a replacement
/// for, the forward-inference guard: the two gates are independent.
/// </summary>
public sealed class RoleGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-roleguard-{Guid.NewGuid():N}");

    public RoleGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string LogPath(string name = "audit.jsonl") => Path.Combine(_dir, name);

    private static IdentityContext Id(OperationRole role) => new("mark", role, "corr-1");

    [Fact]
    public void Is_allowed_is_true_exactly_when_the_role_meets_or_exceeds_the_requirement()
    {
        foreach (OperationRole actual in Enum.GetValues<OperationRole>())
            foreach (OperationRole required in Enum.GetValues<OperationRole>())
            {
                bool expected = actual >= required;
                Assert.Equal(expected, RoleGuard.IsAllowed(Id(actual), required));
            }
    }

    [Fact]
    public void Require_throws_when_the_actual_role_is_below_the_requirement_and_carries_both_roles()
    {
        AccessDeniedException ex = Assert.Throws<AccessDeniedException>(() =>
            RoleGuard.Require(Id(OperationRole.Viewer), OperationRole.Operator, "stream.start"));

        Assert.Equal(OperationRole.Operator, ex.Required);
        Assert.Equal(OperationRole.Viewer, ex.Actual);
        Assert.Equal("stream.start", ex.Operation);
    }

    [Fact]
    public void Require_does_not_throw_when_the_role_is_sufficient()
    {
        RoleGuard.Require(Id(OperationRole.Administrator), OperationRole.Operator, "stream.start");   // no throw
    }

    [Fact]
    public void Require_with_an_audit_log_writes_exactly_one_access_denied_entry_on_a_denial()
    {
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            Assert.Throws<AccessDeniedException>(() =>
                RoleGuard.Require(Id(OperationRole.Viewer), OperationRole.Administrator, "config.change", writer));
        }

        AuditLogEntry entry = Assert.Single(AuditLogFile.Load(path));
        Assert.True(SecurityAudit.TryRead(entry, out SecurityEventKind kind, out _));
        Assert.Equal(SecurityEventKind.AccessDenied, kind);
    }

    [Fact]
    public void Require_with_an_audit_log_writes_nothing_when_the_role_is_sufficient()
    {
        string path = LogPath();
        using (AuditLogWriter writer = AuditLogWriter.Open(path))
        {
            RoleGuard.Require(Id(OperationRole.Operator), OperationRole.Analyst, "estimate", writer);   // allowed
            Assert.Equal(0, writer.Count);
        }
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public void The_forward_inference_guard_is_independent_of_any_role()
    {
        // No role widens what the engine may process: the guard reads no role and has no bypass, so an input at
        // "now" is refused regardless of the caller's role. The two gates are independent.
        DateTime now = DateTime.UtcNow;

        // Even an Administrator passes the role gate for the operation...
        RoleGuard.Require(Id(OperationRole.Administrator), OperationRole.Administrator, "estimate");

        // ...yet the horizon guard still refuses data inside the horizon, with no API that a role could use to relax it.
        Assert.Throws<ForwardInferenceException>(() => ForwardInferenceGuard.RejectFuture(now, now));
    }
}
