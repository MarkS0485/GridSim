using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;

namespace GridSim.Core.Interpose;

/// <summary>
/// The interposer pipeline for one protocol stream: raw frame -> <see cref="FrameCorrection"/> ->
/// <see cref="IRtuProtocol.Decode"/> -> window accumulation -> a horizon-guarded
/// <see cref="MeasurementSet"/> per window. It holds no transport or I/O - the CLI drives it, calling
/// <see cref="Ingest"/> as frames arrive and <see cref="FlushWindow"/> when a window elapses - so it is
/// fully unit-testable by feeding frames directly.
/// </summary>
public sealed class InterposePipeline
{
    private readonly IRtuProtocol _protocol;
    private readonly RtuPointMap _crosswalk;
    private readonly QualityFold _quality;
    private readonly string _caseName;
    private readonly List<RtuPoint> _window = new();
    private long _frameSeq;   // sequence number of the last decoded frame (stamped onto its points)

    /// <summary>Frames that failed decode after passing framing validation.</summary>
    public long DecodeErrors { get; private set; }

    /// <summary>Windows that could not be mapped to a measurement set because a point did not fit its crosswalk
    /// binding (for example a 32-bit Modbus binding whose second register was not in the same response). Each such
    /// window is discarded whole and counted here; <see cref="FlushWindow"/> still throws so the caller can log why.</summary>
    public long MapErrors { get; private set; }

    /// <summary>Raw points accumulated in the current, not-yet-flushed window (repeats of one address included;
    /// the mapper collapses them to one measurement per address at flush, see
    /// <see cref="RtuMeasurementSetMapper.Map"/>).</summary>
    public int PendingPoints => _window.Count;

    /// <summary>The frame-correction stage (exposes accept/reject tallies for the monitor).</summary>
    public FrameCorrection Correction { get; }

    public InterposePipeline(IRtuProtocol protocol, RtuPointMap crosswalk,
        QualityFold? quality = null, string caseName = "rtu")
    {
        _protocol = protocol;
        _crosswalk = crosswalk;
        _quality = quality ?? QualityFold.Default;
        _caseName = caseName;
        Correction = FrameCorrection.ForProtocol(protocol);
    }

    /// <summary>Validate, decode, and accumulate one raw frame. Returns the points decoded from it (for
    /// monitoring); an empty list for control frames, a rejected frame, or a decode failure.</summary>
    public IReadOnlyList<RtuPoint> Ingest(ReadOnlySpan<byte> frame)
    {
        if (!Correction.Validate(frame).Ok) return Array.Empty<RtuPoint>();

        IReadOnlyList<RtuPoint> points;
        try
        {
            points = _protocol.Decode(frame, _quality);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FormatException)
        {
            DecodeErrors++;
            return Array.Empty<RtuPoint>();
        }

        // Stamp every point with this frame's sequence number, so the mapper can assemble a multi-register
        // value from registers of the same response (R272).
        long seq = ++_frameSeq;
        var stamped = new List<RtuPoint>(points.Count);
        foreach (RtuPoint p in points) stamped.Add(p with { FrameSeq = seq });

        _window.AddRange(stamped);
        return stamped;
    }

    /// <summary>Emit the accumulated window as a guarded <see cref="MeasurementSet"/> and start a new one.
    /// Returns null if the window is empty. The window instant is the latest device timestamp present, or
    /// <paramref name="fallbackUtc"/> when no point carried a time tag. <paramref name="utcNow"/> is passed
    /// to the guard (null is the real clock); a window newer than the horizon throws
    /// <see cref="ForwardInferenceException"/>. Tests pass <see cref="DateTime.MaxValue"/> to look at the mapping alone.
    /// The live CLI path no longer uses this overload (D-DOC-018): it calls
    /// <see cref="FlushWindow(DateTime, InterposeFreshPolicy, DateTime?)"/>, which judges against the real clock.</summary>
    public MeasurementSet? FlushWindow(DateTime fallbackUtc, DateTime? utcNow = null)
    {
        if (_window.Count == 0) return null;
        DateTime windowUtc = WindowInstant(fallbackUtc);

        try
        {
            return RtuMeasurementSetMapper.Map(_window, _crosswalk, windowUtc, _quality, _caseName, utcNow);
        }
        catch (InvalidDataException)
        {
            // A point that does not fit its binding is a configuration fault the caller must see (it is rethrown),
            // but it must not poison the stream: before October 2026 the window was not cleared on this path, so
            // every later flush re-mapped the same bad points and failed again. Count it and discard the window.
            MapErrors++;
            throw;
        }
        finally
        {
            _window.Clear();   // a window is flushed exactly once, mapped or not
        }
    }

    /// <summary>Windows the live flush found newer than the horizon (<see cref="FlushWindow(DateTime, InterposeFreshPolicy, DateTime?)"/>).
    /// None of them became an estimator input.</summary>
    public long HeldFresh { get; private set; }

    /// <summary>
    /// D-DOC-018 (October 2026): the LIVE flush. The window is judged against the REAL horizon (the real clock, or a
    /// test's <paramref name="utcNow"/>, which may not be later than the real clock). Before, the CLI flushed every live
    /// window with <see cref="DateTime.MaxValue"/>, which switched the RTU guard seam off and wrote present-time
    /// <c>gridsim-measurements/1</c> windows that only a later consumer re-checked.
    /// <list type="bullet">
    /// <item>A window older than the horizon (a replayed, recorded RTU capture) maps through the guarded
    /// <see cref="RtuMeasurementSetMapper.Map"/> and is returned as <see cref="InterposeFlush.Estimable"/>.</item>
    /// <item>A window newer than the horizon (live telemetry) is never an estimator input. It is counted in
    /// <see cref="HeldFresh"/>; under <see cref="InterposeFreshPolicy.QuarantineCapture"/> it is returned as
    /// <see cref="InterposeFlush.QuarantineCapture"/> (built by <see cref="RtuMeasurementSetMapper.MapQuarantineCapture"/>)
    /// for the firewalled quarantine store; under <see cref="InterposeFreshPolicy.Refuse"/> it is discarded.</item>
    /// </list>
    /// Returns null if the window is empty. A crosswalk mismatch throws <see cref="InvalidDataException"/> and counts in
    /// <see cref="MapErrors"/>, as the other overload does. The window is cleared exactly once whatever happens.
    /// </summary>
    public InterposeFlush? FlushWindow(DateTime fallbackUtc, InterposeFreshPolicy freshPolicy, DateTime? utcNow = null)
    {
        if (_window.Count == 0) return null;
        try
        {
            DateTime now = ForwardInferenceGuard.RequireNotAfterRealClock(utcNow, "the interpose flush 'now'");
            DateTime windowUtc = WindowInstant(fallbackUtc);
            // The guard's own rule (RejectFuture: refused when stamp > horizon, Unspecified read as UTC), asked here as
            // a question so a fresh window can be routed instead of thrown; Map below re-runs the guard itself.
            if (UtcTime.Normalise(windowUtc) <= ForwardInferenceGuard.Horizon(now))
                return new InterposeFlush(
                    RtuMeasurementSetMapper.Map(_window, _crosswalk, windowUtc, _quality, _caseName, now), null, windowUtc);

            HeldFresh++;
            return freshPolicy == InterposeFreshPolicy.QuarantineCapture
                ? new InterposeFlush(null, RtuMeasurementSetMapper.MapQuarantineCapture(_window, _crosswalk, windowUtc, _quality, _caseName), windowUtc)
                : new InterposeFlush(null, null, windowUtc);
        }
        catch (InvalidDataException)
        {
            MapErrors++;   // a configuration fault the caller must see; counted and rethrown as in the other overload
            throw;
        }
        finally
        {
            _window.Clear();   // a window is flushed exactly once, mapped or not
        }
    }

    // The window instant: the latest device time tag present (even if historical); the receive clock only when no
    // point in the window carried a timestamp.
    private DateTime WindowInstant(DateTime fallbackUtc)
    {
        DateTime? latestStamp = null;
        foreach (RtuPoint p in _window)
            if (p.StampUtc is DateTime s && (latestStamp is null || s > latestStamp)) latestStamp = s;
        return latestStamp ?? fallbackUtc;
    }
}

/// <summary>What the live <see cref="InterposePipeline.FlushWindow(DateTime, InterposeFreshPolicy, DateTime?)"/> does with a
/// window newer than the horizon (D-DOC-018).</summary>
public enum InterposeFreshPolicy
{
    /// <summary>Discard it (counted in <see cref="InterposePipeline.HeldFresh"/>).</summary>
    Refuse = 0,

    /// <summary>Return it as a quarantine capture for the firewalled quarantine store (still counted as held).</summary>
    QuarantineCapture = 1,
}

/// <summary>One live flush (D-DOC-018). At most one of the two sets is non-null.</summary>
/// <param name="Estimable">The guarded window, when it is older than the horizon: an estimator input.</param>
/// <param name="QuarantineCapture">The window as a quarantine capture, when it is newer than the horizon and the policy
/// keeps it: for the firewalled quarantine store only.</param>
/// <param name="WindowUtc">The window instant judged against the horizon.</param>
public sealed record InterposeFlush(MeasurementSet? Estimable, MeasurementSet? QuarantineCapture, DateTime WindowUtc);
