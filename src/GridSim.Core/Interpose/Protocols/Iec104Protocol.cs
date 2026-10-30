using GridSim.Core.IO.Telemetry;

namespace GridSim.Core.Interpose.Protocols;

/// <summary>
/// IEC 60870-5-104 over TCP. Wraps the <see cref="Iec104Frame"/> APCI codec (transport framing) and the
/// <see cref="Iec104Asdu"/> object model (application layer) into a selectable <see cref="IRtuProtocol"/>:
/// an I-format APDU's ASDU is decoded to information objects, each becoming an <c>IOA:&lt;n&gt;</c>-addressed
/// <see cref="RtuPoint"/>. S/U-format frames (acknowledgements, STARTDT/STOPDT/TESTFR) carry no
/// measurement and decode to an empty list.
/// </summary>
public sealed class Iec104Protocol : IRtuProtocol
{
    public string Name => "iec104";
    public string DisplayName => "IEC 60870-5-104";
    public RtuTransportKind Transport => RtuTransportKind.Tcp;
    public int DefaultPort => 2404;

    // R089: I-format ASDUs of a type this decoder does not support (commands, ...) used to vanish without trace. They
    // are still not decoded, but every one is counted by type id. N337 (October 2026): step position (5 / 32) and
    // bitstring (7 / 33) ASDUs are now decoded into points (RtuPointType.StepPosition / Bitstring), so they are no
    // longer counted here.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<byte, long> _unsupported = new();

    /// <summary>How many I-format ASDUs of each unsupported type id this instance has received (empty when every
    /// ASDU was a supported monitor type).</summary>
    public IReadOnlyDictionary<byte, long> UnsupportedTypeIds => _unsupported;

    private long _indeterminate, _nonFinite, _invalidTime, _refusedCause;

    /// <summary>Double points received in the indeterminate state (no value; not turned into points).</summary>
    public long IndeterminateDoublePoints => Interlocked.Read(ref _indeterminate);

    /// <summary>Analogue values that were NaN or infinite (a sensor fault or an overflow); not turned into points.</summary>
    public long NonFiniteValues => Interlocked.Read(ref _nonFinite);

    /// <summary>Points of a timed type whose CP56Time2a was invalid (IV set, summer time, or a field out of range);
    /// kept, marked <see cref="RtuQuality.Invalid"/>.</summary>
    public long InvalidTimeTags => Interlocked.Read(ref _invalidTime);

    /// <summary>GR-798: points from an ASDU whose cause of transmission is a negative confirmation (P/N set) or one of the
    /// "unknown" causes 44 to 47 (unknown type identification, cause, common address or object address); kept, marked
    /// <see cref="RtuQuality.Invalid"/>.</summary>
    public long RefusedCausePoints => Interlocked.Read(ref _refusedCause);

    public IReadOnlyList<RtuPoint> Decode(ReadOnlySpan<byte> frame, QualityFold quality)
    {
        Iec104Apdu apdu = Iec104Frame.Parse(frame);
        if (apdu.Format != Iec104Format.I || apdu.Asdu.Length == 0)
            return Array.Empty<RtuPoint>(); // supervisory/unnumbered: no telemetry

        Iec104Asdu asdu = Iec104Asdu.Parse(apdu.Asdu);
        if (!Iec104Asdu.IsSupportedType(asdu.TypeId)) _unsupported.AddOrUpdate(asdu.TypeId, 1, (_, n) => n + 1);
        if (asdu.Objects.Count == 0) return Array.Empty<RtuPoint>();

        bool timedType = Iec104Asdu.IsTimedType(asdu.TypeId);
        // GR-798: an outstation's negative confirmation, or its "unknown ..." answer (COT 44 to 47), may echo objects;
        // they are not live values, so they are marked invalid like a test ASDU (only the T bit used to be read).
        bool refusedCause = asdu.Negative || asdu.Cause is >= 44 and <= 47;
        var points = new List<RtuPoint>(asdu.Objects.Count);
        foreach (Iec104InfoObject o in asdu.Objects)
        {
            // NaN is the decoder's "indeterminate" for a double point; for an analogue float it is a fault value, and an
            // infinity an overflow. Neither is a measurement: both are counted (a NaN float used to vanish uncounted and
            // an infinity reached the mapper with good quality).
            if (!double.IsFinite(o.Value))
            {
                if (o.Type == RtuPointType.BinaryStatus && double.IsNaN(o.Value)) Interlocked.Increment(ref _indeterminate);
                else Interlocked.Increment(ref _nonFinite);
                continue;
            }
            RtuQuality q = QualityFold.FromIec104(o.Quality);
            // A timed type whose time tag is invalid is not an untimed point: the station said its clock is not to be
            // trusted, so the value is marked invalid (and counted) rather than stamped with arrival order.
            if (timedType && o.TimeUtc is null) { q |= RtuQuality.Invalid; Interlocked.Increment(ref _invalidTime); }
            if (asdu.Test) q |= RtuQuality.Invalid; // a test ASDU is never live telemetry
            if (refusedCause) { q |= RtuQuality.Invalid; Interlocked.Increment(ref _refusedCause); }
            if (o.Transient) q |= RtuQuality.Transient; // N337: a step position reported while the tap changer moves
            points.Add(new RtuPoint($"IOA:{o.Ioa}", o.Type, o.Value, q, o.TimeUtc));
        }
        return points;
    }
}
