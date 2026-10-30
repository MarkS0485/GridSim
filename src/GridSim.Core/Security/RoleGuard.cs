using GridSim.Core.Diagnostics;

namespace GridSim.Core.Security;

/// <summary>A least-privilege access band for a GridSim service operation (NCSC CAF B2). Ordered: a higher band
/// includes every lower one.</summary>
public enum OperationRole
{
    /// <summary>Read estimated-state frames and quality metrics. No control.</summary>
    Viewer = 0,

    /// <summary>Additionally run on-demand analysis (estimate, cross-validation, FDIA screen) and read the audit log.</summary>
    Analyst = 1,

    /// <summary>Additionally start, stop and seek the resident estimator and select the stream source.</summary>
    Operator = 2,

    /// <summary>Additionally change configuration (cadence, network model, feeds).</summary>
    Administrator = 3,
}

/// <summary>The authenticated principal a service operation runs as: who, in what role, under which correlation id.
/// Built at the service boundary from the signed-in cookie or the SSO claims, never inside the solver.</summary>
/// <param name="Subject">The principal id (a user or service account name), secret-free.</param>
/// <param name="Role">The principal's role band.</param>
/// <param name="CorrelationId">An id tying this decision to a request, for the audit trail.</param>
public sealed record IdentityContext(string Subject, OperationRole Role, string CorrelationId)
{
    /// <summary>The identity for an unauthenticated caller: a <see cref="OperationRole.Viewer"/> with no subject.
    /// Anonymous visitors may read, nothing more.</summary>
    public static IdentityContext Anonymous { get; } = new("anonymous", OperationRole.Viewer, "");
}

/// <summary>Thrown when a principal lacks the role an operation requires. Carries the required and actual roles so
/// the hosting layer can answer 403 and record the denial.</summary>
public sealed class AccessDeniedException : Exception
{
    /// <summary>Create the exception for a specific operation and the roles involved.</summary>
    public AccessDeniedException(string operation, OperationRole required, OperationRole actual)
        : base($"Access denied: '{operation}' requires role {required} or higher, but the caller has {actual}.")
    {
        Operation = operation;
        Required = required;
        Actual = actual;
    }

    /// <summary>The operation that was refused.</summary>
    public string Operation { get; }

    /// <summary>The role the operation requires.</summary>
    public OperationRole Required { get; }

    /// <summary>The role the caller actually has.</summary>
    public OperationRole Actual { get; }
}

/// <summary>
/// The fail-loud role gate for GridSim's service operations (NCSC CAF B2 identity and access control). It mirrors
/// <c>RestrictionReview.AssertPermitted</c> in spirit: a typed throw at the service seam, never inside the inner
/// solver loop where it would cost per-iteration overhead and pollute the numerics.
///
/// <para><b>Additive to, never a replacement for, the forward-inference guard.</b> The two gates are independent:
/// the guard consults no role and has no bypass, so even an <see cref="OperationRole.Administrator"/> cannot make
/// the engine process data newer than the 169 h horizon. Access control narrows WHO may run the filter; it never
/// widens WHAT the filter may see. Neither gate can weaken the other whichever runs first.</para>
///
/// <para>Every denial is recorded as a security event when an audit log is supplied, tying B2 to C1.</para>
/// </summary>
public static class RoleGuard
{
    /// <summary>True when <paramref name="identity"/> holds at least <paramref name="required"/>.</summary>
    public static bool IsAllowed(IdentityContext identity, OperationRole required)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.Role >= required;
    }

    /// <summary>Require that <paramref name="identity"/> holds at least <paramref name="required"/> for
    /// <paramref name="operation"/>, throwing <see cref="AccessDeniedException"/> otherwise.</summary>
    public static void Require(IdentityContext identity, OperationRole required, string operation)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (identity.Role < required)
            throw new AccessDeniedException(operation, required, identity.Role);
    }

    /// <summary>
    /// Require the role and, on a denial, record one <see cref="SecurityEventKind.AccessDenied"/> audit entry
    /// (with the subject, the operation, and the required and actual roles - never a secret) before throwing. This
    /// is the overload the hosting layer calls, so every B2 denial is C1-observable and D1-recoverable.
    /// </summary>
    public static void Require(IdentityContext identity, OperationRole required, string operation, AuditLogWriter? audit)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (identity.Role >= required)
            return;

        if (audit is not null)
        {
            string actor = string.IsNullOrWhiteSpace(identity.Subject) ? "anonymous" : Sanitise(identity.Subject);
            string reason = $"operation={operation} required={required} actual={identity.Role} subject={Sanitise(identity.Subject)}";
            SecurityAudit.Record(audit, actor, SecurityEventKind.AccessDenied, SecuritySeverity.Low, reason);
        }
        throw new AccessDeniedException(operation, required, identity.Role);
    }

    // Keep the actor/subject within the audit summary's token rule (letters, digits, . _ - @ /).
    private static string Sanitise(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "anonymous";
        char[] chars = raw.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' or '/'))
                chars[i] = '_';
        }
        return new string(chars);
    }
}
