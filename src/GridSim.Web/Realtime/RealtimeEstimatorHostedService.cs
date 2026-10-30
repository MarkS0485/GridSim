using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Microsoft.Extensions.Hosting;

namespace GridSim.Web.Realtime;

/// <summary>Supplies the resident estimator its network model and its stream of dated measurement windows.</summary>
public interface IRealtimeWindowFeed
{
    /// <summary>The (fixed) network the estimator runs on.</summary>
    GridModel Model { get; }

    /// <summary>The stream of measurement windows to estimate (each dated; the engine guards every one).</summary>
    IEnumerable<MeasurementSet> Windows();

    /// <summary>Inputs the feed could not read and skipped (R100: surfaced through /api/realtime). Empty by default.</summary>
    IReadOnlyList<string> SkippedFiles => Array.Empty<string>();
}

/// <summary>
/// The resident real-time state-estimation service: a hosted background worker that drives the
/// <see cref="RealtimeEstimatorEngine"/> ONCE over its feed of dated measurement windows at start-up (it does not
/// watch the directory; R100 corrected "for the lifetime of the process") - the first DI-hosted background estimator
/// in the app (as opposed to per-connection sessions). Its output is read through <c>GET /api/realtime</c>. It
/// is real-time <b>engineering</b> on guard-permitted-age / replayed data: every window's timestamp passes the
/// horizon guard inside the engine, so the resident service is structurally incapable of nowcasting.
/// </summary>
public sealed class RealtimeEstimatorHostedService : BackgroundService
{
    private readonly IRealtimeWindowFeed _feed;
    private readonly RealtimeEstimatorOptions _options;

    /// <summary>Raised for each emitted estimate frame.</summary>
    public event Action<RealtimeEstimateFrame>? FrameEmitted;

    /// <summary>The summary of the most recent (or in-progress) run.</summary>
    public RealtimeRunSummary? LastSummary { get; private set; }

    /// <summary>The most recent estimate frame emitted (null before the first). R100: the output used to have no
    /// consumer at all; it is now read by <c>GET /api/realtime</c>.</summary>
    public RealtimeEstimateFrame? LatestFrame { get; private set; }

    /// <summary>The feed's skipped inputs (unreadable window files).</summary>
    public IReadOnlyList<string> SkippedFiles => _feed.SkippedFiles;

    public RealtimeEstimatorHostedService(IRealtimeWindowFeed feed, RealtimeEstimatorOptions? options = null)
        : this(feed, options, audit: null)
    {
    }

    /// <summary>
    /// D-DOC-060 (October 2026): the resident estimator with an audit log. The web host wrote no audit entries, while
    /// the CLI's evidence verbs (estimate, crossval, interpose, and now serve) each record their run. With
    /// <c>Audit:LogPath</c> set, Program.cs opens the log (verified against its anchor, D-DOC-062) at start-up, before
    /// the host serves anything, adds the case and window-directory inputs, and passes the recorder here; the run's
    /// single entry is written when the run ends (<see cref="RunToCompletion"/>).
    /// </summary>
    public RealtimeEstimatorHostedService(IRealtimeWindowFeed feed, RealtimeEstimatorOptions? options, AuditRunRecorder? audit)
    {
        _feed = feed;
        _options = options ?? new RealtimeEstimatorOptions();
        _audit = audit;
    }

    private readonly AuditRunRecorder? _audit;

    /// <summary>D-DOC-060: the audit log's anchor after this run's entry, or null when the run is not audited or has not
    /// finished. Also written to the anchor file (D-DOC-062).</summary>
    public AuditAnchor? AuditAnchor { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => RunToCompletion(stoppingToken), stoppingToken);

    /// <summary>Drive the streaming engine over the feed to completion (or cancellation), recording the summary.
    /// The directly-callable core the hosted <see cref="ExecuteAsync"/> wraps.</summary>
    public RealtimeRunSummary RunToCompletion(CancellationToken ct = default)
    {
        var engine = new RealtimeEstimatorEngine(_feed.Model);
        RealtimeRunSummary summary;
        try
        {
            summary = engine.Run(_feed.Windows(), _options, f =>
            {
                LatestFrame = f;
                FrameEmitted?.Invoke(f);
                // CAF C1: one audit entry per security event, at the detection site.
                if (f.Result.Status == GridSim.Core.Estimation.EstimationStatus.Infeasible)
                    _audit?.RecordSecurityEvent(SecurityEventKind.InfeasibleState,
                        $"resident estimator window #{f.Sequence} converged to a physically infeasible state and was rejected");
            }, ct);
        }
        catch (Exception ex) when (_audit is not null)
        {
            // A refused or failed run is an audit event too (C1): record why, then let the failure surface as before.
            // The code follows the CLI: 2 for a forward-inference refusal, 1 otherwise.
            if (ex is ForwardInferenceException fie)
                _audit.RecordSecurityEvent(SecurityEventKind.HorizonViolation, $"resident estimator stopped: {fie.Message}");
            _audit.SetOutcome($"rejected={ex.GetType().Name}");
            AuditAnchor = _audit.Finish(ex is ForwardInferenceException ? 2 : 1);
            // GR-340: a failed run reports a failed audit delivery or anchor write as the completed run does below.
            foreach (string error in _audit.DeliveryErrors)
                Console.Error.WriteLine($"GridSim.Web: error: audit delivery: {error}");
            if (_audit.AnchorWriteError is string failedAnchor)
                Console.Error.WriteLine($"GridSim.Web: error: audit anchor '{_audit.AnchorPath}' could not be updated ({failedAnchor}).");
            throw;
        }
        LastSummary = summary;
        if (_audit is not null)
        {
            // CAF C1 / B5: the windows the engine refused as too fresh, and a breached service-level objective, are
            // security events in their own right (the incident wiring, when configured, raises the SLO breach).
            if (summary.WindowsRejectedFresh > 0)
                _audit.RecordSecurityEvent(SecurityEventKind.HorizonViolation, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{summary.WindowsRejectedFresh} resident-estimator window(s) refused as newer than the horizon"));
            GridSim.Core.Simulation.Health.HealthReport health = new GridSim.Core.Simulation.Health.HealthProbe().Check(summary);
            if (!health.Healthy)
                _audit.RecordSecurityEvent(SecurityEventKind.AvailabilityDegraded,
                    "resident estimator breached its service-level objective: " + string.Join("; ", health.BreachedObjectives));
            // The same summary and the same exit-code rule as `gridsim serve`: 1 when a window file could not be read
            // (skipped), else 3 when a window failed or none converged, else 0.
            int skipped = _feed.SkippedFiles.Count;
            int code = skipped > 0 ? 1 : summary.WindowsFailed > 0 || summary.WindowsConverged == 0 ? 3 : 0;
            _audit.SetOutcome(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"method=wls processed={summary.WindowsProcessed} converged={summary.WindowsConverged} rejected-fresh={summary.WindowsRejectedFresh} failed={summary.WindowsFailed} skipped-files={skipped} cancelled={ct.IsCancellationRequested}"));
            _audit.StateTimeUtc = summary.LastStateTimeUtc;
            AuditAnchor = _audit.Finish(code);
            // A forwarding or incident channel that failed never stopped the run, but the operator must hear of it.
            foreach (string error in _audit.DeliveryErrors)
                Console.Error.WriteLine($"GridSim.Web: error: audit delivery: {error}");
            if (_audit.AnchorWriteError is string anchorError)
                Console.Error.WriteLine($"GridSim.Web: error: audit anchor '{_audit.AnchorPath}' could not be updated ({anchorError}).");
        }
        return summary;
    }
}
