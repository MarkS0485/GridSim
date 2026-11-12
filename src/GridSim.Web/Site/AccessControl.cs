using System.Globalization;
using System.Security.Claims;
using GridSim.Core.Diagnostics;
using GridSim.Core.Security;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// NCSC CAF B2 (November 2026): the web host's role-based access control. Before this, <see cref="RoleGuard"/> was
/// built and tested but no host called it, so nothing was authorised by role and no access denial ever reached the
/// audit trail. This class is where the host turns a request into an <see cref="IdentityContext"/> and asks
/// <see cref="RoleGuard"/> whether it may act, before the action runs.
///
/// <para><b>The surfaces and the role each needs</b> (the bands are <see cref="OperationRole"/>'s; no second role model):</para>
/// <list type="bullet">
/// <item><c>POST /api/solve</c> (<see cref="SolveOperation"/>): <see cref="OperationRole.Analyst"/>. A one-shot solve is an
/// on-demand analysis, the Analyst band.</item>
/// <item><c>GET /api/topology</c> (<see cref="TopologyOperation"/>): <see cref="OperationRole.Viewer"/>. It reads a case's map
/// and starts nothing, so every caller may read it; it is still routed through the gate so the band is stated in one
/// place and a later change is one line.</item>
/// <item>Every <c>/ws/sim</c> control frame (<c>start</c>, <c>play</c>, <c>pause</c>, <c>step</c>, <c>seek</c>,
/// <c>speed</c>, <c>trip</c>; operation <c>ws.sim.&lt;action&gt;</c>): <see cref="OperationRole.Operator"/>. A start selects
/// the stream source (live, replay or bridge) and runs a session; the others drive it. That is the Operator band.</item>
/// </list>
/// <para>The resident estimator has no control surface on the web host: it is configured (<c>Realtime:*</c>) and run once
/// at start-up, and <c>GET /api/realtime</c> only reads its output (Viewer), so there is nothing there to authorise.
/// The bridge is reached only through a <c>/ws/sim</c> start (<c>bridge:...</c> case ids), which the Operator band covers.</para>
///
/// <para><b>Who the caller is.</b> A signed-in caller (the estate cookie or the SSO consumer, as they exist) is named by
/// the identity's name (else its name-identifier claim) and holds the highest band among its role claims whose value is
/// <c>GridSim.Viewer</c>, <c>GridSim.Analyst</c>, <c>GridSim.Operator</c> or <c>GridSim.Administrator</c> (case-insensitive;
/// <see cref="RoleClaimPrefix"/>). The <c>GridSim.</c> prefix is deliberate: the role claim is shared across the estate,
/// and an estate-wide role such as "Administrator" given for another application must not silently grant control of
/// GridSim. Any other role claim is ignored. A caller who is not signed in is anonymous and holds
/// <see cref="AnonymousRole"/>; a signed-in caller never holds less than an anonymous one.</para>
///
/// <para><b>The default (a deliberate decision).</b> <c>GridSim:AnonymousRole</c> unset means: <see cref="OperationRole.Operator"/>
/// when the instance answers only loopback names (<see cref="HostAllowList.LoopbackOnly"/>: the default
/// <c>AllowedHosts</c>), and <see cref="OperationRole.Viewer"/> otherwise. Why: the documented single-user use is
/// <c>dotnet run</c> on the user's own machine, reached as localhost, where the one user is the operator and has no
/// sign-in to give; refusing that user their own solver would break the documented use for no protection. An instance
/// that answers any other name (a public or private name, a wildcard or <c>*</c>) has callers the operator does not
/// know, so an anonymous caller there may read but not solve or drive sessions until the deployment says otherwise
/// (role claims through sign-in, or an explicit <c>GridSim:AnonymousRole</c>). That is fail-safe: the wider the
/// exposure, the less an unknown caller may do. A replay-only instance (<c>GridSim:ReplayOnly=true</c>, the production
/// configuration) maps neither <c>/api</c> nor <c>/ws/sim</c>, so this changes nothing there; the anonymous pages,
/// <c>/compliance</c>, <c>/health</c> and the read-only <c>/api</c> calls are not gated.</para>
///
/// <para><b>On a denial.</b> The action does not run: REST answers 403 with the reason, <c>/ws/sim</c> answers an error frame
/// and leaves the connection's session exactly as it was. When the host has an audit log (<c>Audit:LogPath</c>), one
/// <see cref="SecurityEventKind.AccessDenied"/> entry is written through <see cref="AccessDenialAudit"/>, the same path the
/// resident estimator's security events take, so the forwarder and the incident router see it. A host with no audit log
/// still refuses.</para>
///
/// <para><b>Fail loud.</b> A <c>GridSim:AnonymousRole</c> that is not one of the four band names stops start-up naming the
/// setting. Numbers are refused (Enum parsing would otherwise accept "2" or "-1").</para>
/// </summary>
public sealed class AccessControl
{
    /// <summary>The setting that fixes the role of an anonymous caller (environment variable <c>GridSim__AnonymousRole</c>).</summary>
    public const string AnonymousRoleSetting = "GridSim:AnonymousRole";

    /// <summary>The prefix a role claim's value must carry to count as a GridSim band (for example <c>GridSim.Operator</c>).</summary>
    public const string RoleClaimPrefix = "GridSim.";

    /// <summary>The operation name for <c>POST /api/solve</c>, as it appears in the refusal and the audit entry.</summary>
    public const string SolveOperation = "api.solve";

    /// <summary>The band <c>POST /api/solve</c> needs: a one-shot solve is an on-demand analysis.</summary>
    public const OperationRole SolveRole = OperationRole.Analyst;

    /// <summary>The operation name for <c>GET /api/topology</c>.</summary>
    public const string TopologyOperation = "api.topology";

    /// <summary>The band <c>GET /api/topology</c> needs: it reads, so every caller holds it.</summary>
    public const OperationRole TopologyRole = OperationRole.Viewer;

    /// <summary>GR-715: the operation name for <c>GET /api/topology</c> of a model found under <c>results/</c> or
    /// <c>gda-out/</c>. Its band is <see cref="GridSim.Web.Model.CaseCatalog.PrivateListingRole"/>, the band that is shown those models in a
    /// listing, so a caller who may not see a model listed may not read it either.</summary>
    public const string TopologyPrivateOperation = "api.topology.private";

    /// <summary>The operation name for opening a model found under <c>results/</c> or <c>gda-out/</c> on the Blazor dashboard
    /// (<c>/dashboard/&lt;id&gt;</c>). Same band as <see cref="TopologyPrivateOperation"/>: <see cref="GridSim.Web.Model.CaseCatalog.PrivateListingRole"/>.</summary>
    public const string DashboardPrivateOperation = "dashboard.private";

    /// <summary>
    /// The dashboard's rule for a private id on the prerender pass: true means "do nothing yet" (no session, no audit
    /// entry, the neutral loading state), because the real identity is only known on the interactive pass. A bundled id
    /// is never deferred, so its prerender is unchanged. Pure, so it is tested without a circuit.
    /// </summary>
    /// <param name="isPrivate">The case id names a model under <c>results/</c> or <c>gda-out/</c>.</param>
    /// <param name="isInteractive">The component is running in the interactive circuit (not the prerender).</param>
    public static bool MustWaitForInteractive(bool isPrivate, bool isInteractive) => isPrivate && !isInteractive;

    /// <summary>The prefix of a <c>/ws/sim</c> control's operation name (<c>ws.sim.start</c>, <c>ws.sim.trip</c>, ...).</summary>
    public const string SimOperationPrefix = "ws.sim.";

    /// <summary>The band every <c>/ws/sim</c> control frame needs: selecting a stream source and driving a session.</summary>
    public const OperationRole SimControlRole = OperationRole.Operator;

    private readonly AccessDenialAudit _audit;

    /// <summary>Build the access control with a fixed anonymous role and the denial recorder.</summary>
    /// <param name="anonymousRole">The role of a caller who is not signed in.</param>
    /// <param name="anonymousRoleIsDefault">True when the role came from the default rule, not the setting.</param>
    /// <param name="audit">Where denials are recorded (<see cref="AccessDenialAudit.None"/> for a host with no audit log).</param>
    public AccessControl(OperationRole anonymousRole, bool anonymousRoleIsDefault, AccessDenialAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (!Enum.IsDefined(anonymousRole))
            throw new ArgumentOutOfRangeException(nameof(anonymousRole), anonymousRole, "Not a defined OperationRole value.");
        AnonymousRole = anonymousRole;
        AnonymousRoleIsDefault = anonymousRoleIsDefault;
        _audit = audit;
    }

    /// <summary>The role of a caller who is not signed in.</summary>
    public OperationRole AnonymousRole { get; }

    /// <summary>True when <see cref="AnonymousRole"/> came from the default rule (the setting was not given).</summary>
    public bool AnonymousRoleIsDefault { get; }

    /// <summary>True when denials are written to an audit log.</summary>
    public bool AuditsDenials => _audit.IsConfigured;

    /// <summary>A one-line description for the start-up log.</summary>
    public string Description =>
        "anonymous callers hold " + AnonymousRole.ToString()
        + (AnonymousRoleIsDefault
            ? " (the default for the host names this instance answers; set " + AnonymousRoleSetting + " to change it)"
            : " (" + AnonymousRoleSetting + ")")
        + "; /api/solve needs " + SolveRole.ToString() + ", /ws/sim controls need " + SimControlRole.ToString()
        + "; signed-in callers add " + RoleClaimPrefix + "<role> role claims; "
        + (AuditsDenials ? "denials are written to the audit log" : "denials are refused but not audited (no Audit:LogPath)");

    /// <summary>
    /// Read <see cref="AnonymousRoleSetting"/> once, at start-up, and build the access control. Unset or blank means the
    /// default rule (see the class summary): <see cref="OperationRole.Operator"/> when <paramref name="hosts"/> answers
    /// only loopback names, else <see cref="OperationRole.Viewer"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The setting is not one of the four band names; the message names it.</exception>
    public static AccessControl FromConfiguration(IConfiguration configuration, HostAllowList hosts, AccessDenialAudit audit)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(audit);
        string? raw = configuration[AnonymousRoleSetting];
        if (string.IsNullOrWhiteSpace(raw))
            return new AccessControl(hosts.LoopbackOnly ? OperationRole.Operator : OperationRole.Viewer, anonymousRoleIsDefault: true, audit);
        OperationRole role = ParseRole(raw.Trim()) ?? throw new InvalidOperationException(
            $"GridSim.Web: configuration setting {AnonymousRoleSetting} ('{raw}') is not a role. Use one of "
            + string.Join(", ", Enum.GetNames<OperationRole>()) + ", or leave it unset for the default ("
            + "Operator when the instance answers only loopback names, Viewer otherwise).");
        return new AccessControl(role, anonymousRoleIsDefault: false, audit);
    }

    /// <summary>
    /// A band by its exact name, case-insensitively (<c>Viewer</c>, <c>Analyst</c>, <c>Operator</c>, <c>Administrator</c>),
    /// or null for anything else. Numbers, combined names and white space inside are refused.
    /// </summary>
    public static OperationRole? ParseRole(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (OperationRole r in Enum.GetValues<OperationRole>())
            if (string.Equals(r.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    /// <summary>
    /// The band a role claim's value names (<c>GridSim.Operator</c> gives <see cref="OperationRole.Operator"/>), or null
    /// for a value without the <see cref="RoleClaimPrefix"/> or with an unknown band.
    /// </summary>
    public static OperationRole? RoleFromClaim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (!v.StartsWith(RoleClaimPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        return ParseRole(v[RoleClaimPrefix.Length..]);
    }

    /// <summary>
    /// The identity a request runs as. A signed-in caller (an authenticated identity on <paramref name="user"/>) is named
    /// by its name or name-identifier claim and holds the highest of <see cref="AnonymousRole"/> and its GridSim role
    /// claims; anyone else is <c>anonymous</c> with <see cref="AnonymousRole"/>.
    /// </summary>
    /// <param name="user">The request's user (HttpContext.User), or null.</param>
    /// <param name="correlationId">The request's id (HttpContext.TraceIdentifier), for the audit trail.</param>
    public IdentityContext IdentityFor(ClaimsPrincipal? user, string? correlationId)
    {
        string subject = "anonymous";
        OperationRole role = AnonymousRole;
        bool named = false;
        if (user is not null)
        {
            foreach (ClaimsIdentity identity in user.Identities)
            {
                if (!identity.IsAuthenticated) continue;
                if (!named)
                {
                    string? name = identity.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    subject = string.IsNullOrWhiteSpace(name) ? "signed-in" : name;
                    named = true;
                }
                foreach (Claim claim in identity.FindAll(identity.RoleClaimType))
                    if (RoleFromClaim(claim.Value) is OperationRole claimed && claimed > role)
                        role = claimed;
            }
        }
        return new IdentityContext(subject, role, correlationId ?? "");
    }

    /// <summary>
    /// GR-235: who a session is counted against for the per-client quota (<c>GridSim:MaxSessionsPerClient</c>). A
    /// signed-in caller is <c>user:&lt;subject&gt;</c>, the same subject <see cref="IdentityFor"/> names; an anonymous
    /// caller is <c>ip:&lt;remote address&gt;</c> (an IPv4-mapped IPv6 address is written as its IPv4 form, so one client
    /// is one key). Null when the caller is anonymous and its address is not known: such a session is bounded by the
    /// global cap only, never refused on a guess.
    /// </summary>
    public string? ClientKeyFor(ClaimsPrincipal? user, System.Net.IPAddress? remote)
    {
        if (user is not null && user.Identities.Any(i => i.IsAuthenticated))
            return "user:" + IdentityFor(user, null).Subject;
        if (remote is null) return null;
        return "ip:" + (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);
    }

    /// <summary>
    /// Require <paramref name="required"/> for <paramref name="operation"/> (<see cref="RoleGuard.Require(IdentityContext, OperationRole, string)"/>).
    /// On a denial, record it (one <see cref="SecurityEventKind.AccessDenied"/> entry when the host has an audit log) and
    /// then throw the <see cref="AccessDeniedException"/>, so the caller refuses before the action runs. An allowed call
    /// writes nothing.
    /// </summary>
    /// <exception cref="AccessDeniedException">The identity's band is below <paramref name="required"/>.</exception>
    public void Require(IdentityContext identity, OperationRole required, string operation)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            RoleGuard.Require(identity, required, operation);
        }
        catch (AccessDeniedException denial)
        {
            // The refusal stands whatever happens to the record: Record never throws (a failure to write is reported
            // on standard error), so the caller always gets the denial, never a different error in its place.
            _audit.Record(identity, denial);
            throw;
        }
    }
}

/// <summary>
/// B2 / C1 (November 2026): writes one <see cref="SecurityEventKind.AccessDenied"/> entry per access denial into the web
/// host's audit log, through <see cref="AuditRunRecorder.RecordSecurityEvent"/>, the path the resident estimator's own
/// security events take (so the forwarder and the incident router attached by <c>Audit:ForwardPath</c> and
/// <c>Audit:IncidentLogPath</c> see it).
///
/// <para><b>The run's log closes before the host stops.</b> The web host's audit log belongs to the resident estimator's
/// run, which runs once at start-up and then finishes, closing the log. A denial while the run is open is written
/// through the run's recorder; a denial after it has finished is written by reopening the same log, with the same
/// anchor, keys and sinks, for that one entry (<see cref="AuditRunRecorder.AppendSecurityEvent"/>), then closing it.
/// Both paths write exactly one entry; one lock serialises them, so two denials never contend for the log.</para>
///
/// <para><b>Never in the way of the refusal.</b> A failure to record (a log another process holds, a log that no longer
/// verifies against its anchor, a full disk) is reported on standard error, naming the log and the reason, and the
/// request is still refused. <see cref="None"/> (no <c>Audit:LogPath</c>) records nothing.</para>
/// </summary>
public sealed class AccessDenialAudit
{
    private readonly Lock _gate = new();
    private readonly AuditRunRecorder? _recorder;
    private readonly AuditRecorderOptions _options;

    /// <summary>The recorder for a host with no audit log: denials are refused and not recorded.</summary>
    public static AccessDenialAudit None { get; } = new(null, null);

    /// <summary>Record denials in the log <paramref name="recorder"/> holds, reopening it with <paramref name="options"/>
    /// once the run has finished.</summary>
    /// <param name="recorder">The host's run recorder, or null for no audit log.</param>
    /// <param name="options">The sinks and anchor keys the recorder was started with (null means none).</param>
    public AccessDenialAudit(AuditRunRecorder? recorder, AuditRecorderOptions? options)
    {
        _recorder = recorder;
        _options = options ?? AuditRecorderOptions.None;
    }

    /// <summary>True when denials are recorded (the host has an audit log).</summary>
    public bool IsConfigured => _recorder is not null;

    /// <summary>The one-line, secret-free reason recorded for a denial: the operation, the required and actual bands,
    /// the subject and the request's correlation id (both reduced to letters, digits and <c>. _ - @ /</c>).</summary>
    public static string ReasonFor(IdentityContext identity, AccessDeniedException denial)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(denial);
        return string.Create(CultureInfo.InvariantCulture,
            $"operation={Token(denial.Operation)} required={denial.Required} actual={denial.Actual} subject={Token(identity.Subject)} correlation={Token(identity.CorrelationId)}");
    }

    /// <summary>
    /// Record one denial. Never throws for a write failure: the caller is about to refuse the request, and that refusal
    /// must not be replaced by an audit error. The failure is reported on standard error instead.
    /// </summary>
    public void Record(IdentityContext identity, AccessDeniedException denial)
    {
        string reason = ReasonFor(identity, denial);
        AuditRunRecorder? recorder = _recorder;
        if (recorder is null)
            return;   // no audit log configured: the refusal is the whole answer
        lock (_gate)
        {
            try
            {
                if (!recorder.IsFinished)
                {
                    try
                    {
                        recorder.RecordSecurityEvent(SecurityEventKind.AccessDenied, reason);
                        return;
                    }
                    catch (InvalidOperationException) when (recorder.IsFinished)
                    {
                        // The run finished between the check and the write: nothing was written; reopen the log below.
                    }
                }
                AuditRunRecorder.AppendSecurityEvent(recorder.FilePath, recorder.Actor, recorder.Provenance,
                    recorder.AnchorPath, _options, SecurityEventKind.AccessDenied, reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                // AuditLogTamperException is an IOException, so a log that no longer verifies lands here too.
                Console.Error.WriteLine(
                    $"GridSim.Web: error: an access denial ({reason}) could not be recorded in the audit log '{recorder.FilePath}': {ex.Message}");
            }
        }
    }

    // Reduce free text to the audit token alphabet (letters, digits, . _ - @ /), so a claim value cannot carry a line
    // separator or forge fields into the entry. Empty becomes "-".
    private static string Token(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "-";
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
