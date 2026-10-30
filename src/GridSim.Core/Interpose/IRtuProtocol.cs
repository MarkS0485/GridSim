using GridSim.Core.IO.Telemetry;

namespace GridSim.Core.Interpose;

/// <summary>What a decoded point observes, in protocol-native terms (before the crosswalk assigns it a
/// bus/branch and a <see cref="Estimation.MeasurementKind"/>).</summary>
public enum RtuPointType
{
    /// <summary>An analogue measured value (voltage, power, current, frequency…).</summary>
    Analog,
    /// <summary>An integrated total / accumulator (kWh, pulse counter).</summary>
    Counter,
    /// <summary>A single- or double-point binary status (breaker open/closed…).</summary>
    BinaryStatus,
    /// <summary>A control/command set-point echoed back in monitor direction.</summary>
    Setpoint,
    /// <summary>N337 (October 2026): a step position (IEC 60870-5 types 5 / 32, VTI), typically a transformer tap
    /// position; the value is the signed step, -64 to +63. It is a state of equipment, not an electrical quantity, so
    /// no estimator measurement kind takes it: <see cref="RtuMeasurementSetMapper"/> refuses a crosswalk binding for
    /// one (fail loud) and otherwise leaves it out of the measurement set.</summary>
    StepPosition,
    /// <summary>N337 (October 2026): a bitstring of 32 bits (IEC 60870-5 types 7 / 33, BSI); the value is the 32-bit
    /// word as an unsigned integer (exact in a double). A packed status word, not a measurement: the crosswalk may not
    /// bind it either.</summary>
    Bitstring,
}

/// <summary>
/// Normalised telemetry-quality flags, folded from each protocol's own descriptor (IEC-104 QDS,
/// DNP3 object flags, GOOSE validity, C37.118 STAT). There is no quality field on
/// <see cref="Estimation.Measurement"/> - <see cref="QualityFold"/> turns these into a sigma
/// multiplier or an outright drop, because in the estimator <b>sigma is the weight</b>.
/// </summary>
[Flags]
public enum RtuQuality
{
    /// <summary>No adverse flags: the point is trustworthy.</summary>
    Good = 0,
    /// <summary>Invalid (IEC-104 IV, DNP3 !online, GOOSE !good). Always dropped.</summary>
    Invalid = 1 << 0,
    /// <summary>Not topical / stale (IEC-104 NT): the value has not refreshed. Sigma widened.</summary>
    NonTopical = 1 << 1,
    /// <summary>Substituted by an operator rather than telemetered (IEC-104 SB). Sigma widened.</summary>
    Substituted = 1 << 2,
    /// <summary>Blocked from update (IEC-104 BL). Dropped.</summary>
    Blocked = 1 << 3,
    /// <summary>Over-range / overflow (IEC-104 OV, DNP3 over-range). Dropped.</summary>
    Overflow = 1 << 4,
    /// <summary>Communication to the source RTU is lost (DNP3 comm-lost). Dropped.</summary>
    CommLost = 1 << 5,
    /// <summary>The source device has restarted and not yet re-integrated (DNP3 restart). Sigma widened.</summary>
    Restart = 1 << 6,
    /// <summary>N337 (October 2026): the equipment was in a transient state when reported (the IEC 60870-5 VTI "T"
    /// bit of a step position: the tap changer is moving). Informational only: a step position never enters the
    /// estimator, so it neither drops a point nor widens a sigma.</summary>
    Transient = 1 << 7,
}

/// <summary>
/// One decoded, corrected telemetry point in <b>protocol-native address space</b>: a value plus quality
/// and an optional device timestamp, keyed by an address string the <see cref="RtuPointMap"/> crosswalk
/// resolves to a bus or branch. Address forms are per-protocol, e.g. <c>"IOA:1001"</c> (IEC-104/101),
/// <c>"DNP:AI:12"</c> (DNP3), <c>"MB:HR:40001"</c> (Modbus), a GOOSE dataset-member path, or
/// <c>"PMU:3:V0"</c> (C37.118).
/// </summary>
/// <param name="Address">Protocol-native key resolved by the crosswalk.</param>
/// <param name="Type">What the point observes.</param>
/// <param name="Value">Engineering value after correction (see <see cref="FrameCorrection"/>).</param>
/// <param name="Quality">Folded, protocol-independent quality flags.</param>
/// <param name="StampUtc">The point's device timestamp in UTC, or null if the protocol carried none.</param>
public sealed record RtuPoint(
    string Address,
    RtuPointType Type,
    double Value,
    RtuQuality Quality,
    DateTime? StampUtc)
{
    /// <summary>
    /// Which ingested frame this point came from (stamped by <see cref="InterposePipeline.Ingest"/>, 1-based;
    /// 0 when a caller builds points directly). A 32-bit value spread over two Modbus registers must be
    /// assembled from two registers of the SAME response, never from halves of different polls (R272), so the
    /// mapper pairs registers by this number.
    /// </summary>
    public long FrameSeq { get; init; }
}

/// <summary>How a protocol's frames arrive off the wire.</summary>
public enum RtuTransportKind
{
    /// <summary>A TCP stream (IEC-104, DNP3-TCP, Modbus-TCP, MMS).</summary>
    Tcp,
    /// <summary>A serial line (IEC-101, DNP3-serial, Modbus-RTU).</summary>
    Serial,
    /// <summary>A raw layer-2 Ethernet capture (IEC 61850-GOOSE, Sampled Values).</summary>
    Ethernet,
}

/// <summary>
/// One selectable RTU/SCADA protocol stack. The transport framing is already handled by the codecs in
/// <see cref="GridSim.Core.IO.Telemetry"/>; an implementation adds the <b>application / object-model</b>
/// layer, turning a validated frame into zero-or-more <see cref="RtuPoint"/>s. Control frames that carry
/// no measurement (IEC-104 U-frames, DNP3 link ACKs) decode to an empty list, not an error.
/// </summary>
public interface IRtuProtocol
{
    /// <summary>Lowercase registry key, e.g. <c>"iec104"</c>.</summary>
    string Name { get; }

    /// <summary>Human-readable name, e.g. <c>"IEC 60870-5-104"</c>.</summary>
    string DisplayName { get; }

    /// <summary>How this protocol's frames arrive (selects the transport factory).</summary>
    RtuTransportKind Transport { get; }

    /// <summary>The conventional listen port (0 for serial / layer-2 protocols).</summary>
    int DefaultPort { get; }

    /// <summary>
    /// Decode one framed, already-integrity-checked <paramref name="frame"/> into native points. The
    /// <paramref name="quality"/> table folds this protocol's quality descriptor into <see cref="RtuQuality"/>.
    /// </summary>
    IReadOnlyList<RtuPoint> Decode(ReadOnlySpan<byte> frame, QualityFold quality);
}

/// <summary>
/// GR-329: a protocol whose decoder keeps per-connection state (the Modbus stack remembers outstanding requests by
/// transaction id). The interposer calls <see cref="ResetSession"/> whenever its client goes away, so nothing one client
/// sent can pair with, or be refused because of, what the next client sends. A stateless protocol need not implement it.
/// </summary>
public interface IRtuSessionState
{
    /// <summary>Forget everything learnt from the connection that has just ended.</summary>
    void ResetSession();
}
