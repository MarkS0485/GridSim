using GridSim.Core.IO.Telemetry;

namespace GridSim.Core.Interpose.Protocols;

/// <summary>
/// IEC 60870-5-104 over TCP. Wraps the <see cref="Iec104Frame"/> APCI codec (transport framing) and the
/// <see cref="Iec104Asdu"/> object model (application layer) into a selectable <see cref="IRtuProtocol"/>:
/// an I-format APDU's ASDU is decoded to information objects, each becoming an <c>IOA:&lt;n&gt;</c>-addressed
/// <see cref="RtuPoint"/>. S/U-format frames (acknowledgements, STARTDT/STOPDT/TESTFR) carry no
/// measurement and decode to an empty list.
/// <para>GR-306 (November 2026): with the opt-in <see cref="Iec104PointKey.CasduIoa"/> (crosswalk
/// <c>"iec104Key": "casdu-ioa"</c>) points are named <c>CA:&lt;casdu&gt;/IOA:&lt;n&gt;</c>, so stations sharing a link never
/// collide. With the default <see cref="Iec104PointKey.Ioa"/> the names are unchanged, but an IOA that arrives with a
/// second common address on one link is a collision: from then on its points are marked <see cref="RtuQuality.Invalid"/>
/// (the crosswalk cannot say which station it means), counted, and the first one is named.</para>
/// </summary>
public sealed class Iec104Protocol : IRtuProtocol, IRtuSessionState
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

    /// <summary>GR-306: how points are named: by IOA alone (the default, the names every crosswalk has used) or by common
    /// address and IOA. Set from the crosswalk's <c>iec104Key</c> (<see cref="RtuPointMap.ResolveIec104Key"/>) before the
    /// first frame.</summary>
    public Iec104PointKey PointKey { get; set; } = Iec104PointKey.Ioa;

    // GR-306: per link (cleared by ResetSession), the common address each IOA first arrived with and the IOAs that have
    // arrived with two. The counters are never cleared.
    private readonly object _addressLock = new();
    private readonly Dictionary<int, int> _casduByIoa = new();
    private readonly HashSet<int> _collidedIoas = new();
    private long _casduCollisions;
    private string? _firstCollision;

    /// <summary>GR-306: points (by IOA alone) whose IOA has arrived with two common addresses on one link; each is marked
    /// <see cref="RtuQuality.Invalid"/>. Always 0 under <see cref="Iec104PointKey.CasduIoa"/>.</summary>
    public long CommonAddressCollisions => Interlocked.Read(ref _casduCollisions);

    /// <summary>GR-306: the first collision, in words ("IOA 10 arrived with common address 2 after common address 1 on one
    /// link"), or null when there has been none. The interposer prints it once on stderr.</summary>
    public string? FirstCommonAddressCollision { get { lock (_addressLock) return _firstCollision; } }

    /// <summary>GR-306: a new client is a new link, so the common address each IOA came with is forgotten (the counts
    /// stay).</summary>
    public void ResetSession()
    {
        lock (_addressLock)
        {
            _casduByIoa.Clear();
            _collidedIoas.Clear();
        }
    }

    /// <summary>GR-306: true when <paramref name="ioa"/> has (now or before, on this link) arrived with a common address
    /// other than <paramref name="casdu"/>; the first such arrival is recorded and named.</summary>
    private bool Collides(int casdu, int ioa)
    {
        lock (_addressLock)
        {
            if (!_casduByIoa.TryGetValue(ioa, out int first)) { _casduByIoa[ioa] = casdu; return _collidedIoas.Contains(ioa); }
            if (first != casdu && _collidedIoas.Add(ioa))
                _firstCollision ??= string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"IOA {ioa} arrived with common address {casdu} after common address {first} on one link");
            return _collidedIoas.Contains(ioa);
        }
    }

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
            string address;
            if (PointKey == Iec104PointKey.CasduIoa)
                address = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"CA:{asdu.CommonAddress}/IOA:{o.Ioa}");
            else
            {
                // GR-306: by IOA alone two stations on one link would feed one crosswalk point; a collided IOA is invalid.
                if (Collides(asdu.CommonAddress, o.Ioa)) { q |= RtuQuality.Invalid; Interlocked.Increment(ref _casduCollisions); }
                address = $"IOA:{o.Ioa}";
            }
            points.Add(new RtuPoint(address, o.Type, o.Value, q, o.TimeUtc));
        }
        return points;
    }
}

/// <summary>GR-306: how IEC 104 points are named, chosen by the crosswalk's optional top-level <c>iec104Key</c>.</summary>
public enum Iec104PointKey
{
    /// <summary><c>IOA:&lt;n&gt;</c> (crosswalk <c>"ioa"</c> or no member): the names every crosswalk has used. An IOA that
    /// arrives with two common addresses on one link is marked invalid.</summary>
    Ioa,

    /// <summary><c>CA:&lt;casdu&gt;/IOA:&lt;n&gt;</c> (crosswalk <c>"casdu-ioa"</c>): one name per station and object.</summary>
    CasduIoa,
}
