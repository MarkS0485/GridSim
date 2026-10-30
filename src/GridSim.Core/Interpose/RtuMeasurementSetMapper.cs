using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;

namespace GridSim.Core.Interpose;

/// <summary>
/// Maps a window of decoded <see cref="RtuPoint"/>s to a horizon-guarded <see cref="MeasurementSet"/> the
/// estimator consumes - the RTU analogue of <see cref="Synchrophasor.PmuMeasurementSetMapper"/>, and it
/// follows the same three-step shape: (1) reject a non-historical window before admitting any value
/// (invariant #1), (2) walk the crosswalk turning each native address into a bus/branch measurement,
/// (3) convert to engineering units. Quality is folded into sigma here: an unusable point is dropped, a
/// degraded one has its sigma multiplied (<see cref="QualityFold"/>).
/// </summary>
public static class RtuMeasurementSetMapper
{
    /// <summary>Build a guarded measurement set from a window of points and the crosswalk. The window is first
    /// collapsed to one (the latest) sample per address (<see cref="LatestPerAddress"/>), so a repeated point is
    /// one measurement, not N. Points whose address is not in the map, or whose quality fails
    /// <see cref="QualityFold.ShouldDrop"/>, are skipped. A binding's register data type is applied before
    /// its scale.</summary>
    /// <param name="windowUtc">The window's representative instant; checked against the horizon.</param>
    /// <param name="utcNow">Override for "now" (tests / replay pass <see cref="DateTime.MaxValue"/> to defer
    /// the guard to the consuming engine, which re-checks against the real clock).</param>
    public static MeasurementSet Map(
        IReadOnlyList<RtuPoint> points, RtuPointMap crosswalk, DateTime windowUtc,
        QualityFold? quality = null, string caseName = "rtu", DateTime? utcNow = null)
    {
        // Invariant #1: the window instant must be historical before any RTU value is admitted.
        ForwardInferenceGuard.RejectFuture(windowUtc, utcNow, "RTU window");
        return MapPoints(points, crosswalk, windowUtc, quality, caseName, "mapped from RTU interposer");
    }

    /// <summary>The <see cref="MeasurementSet.Note"/> of a window built by <see cref="MapQuarantineCapture"/>.</summary>
    public const string QuarantineCaptureNote =
        "QUARANTINE CAPTURE from the RTU interposer: newer than the historical horizon when received; raw telemetry for " +
        "the firewalled quarantine store only, never an estimator input";

    /// <summary>
    /// D-DOC-018 (October 2026): map a window that is NEWER than the horizon for the firewalled quarantine store only
    /// (<c>GridSim.Gda.Interpose.QuarantineLiveStore</c>: non-evidential, refuses to write inside the GDA lake). This is
    /// capture, not processing: the result is never handed to an estimator or written as a
    /// <c>gridsim-measurements/1</c> window, and it carries <see cref="QuarantineCaptureNote"/>. Every estimator input
    /// goes through <see cref="Map"/>, whose guard the live <c>interpose</c> path now runs against the REAL clock (it
    /// used to pass <see cref="DateTime.MaxValue"/> and so switched the RTU seam off). Even if such a set were saved and
    /// loaded later, <see cref="MeasurementSet.Load"/> applies the horizon again.
    /// </summary>
    public static MeasurementSet MapQuarantineCapture(
        IReadOnlyList<RtuPoint> points, RtuPointMap crosswalk, DateTime windowUtc,
        QualityFold? quality = null, string caseName = "rtu")
        => MapPoints(points, crosswalk, windowUtc, quality, caseName, QuarantineCaptureNote);

    // Steps (2) and (3): the crosswalk walk and the unit conversion, shared by Map (after its guard) and by
    // MapQuarantineCapture. Byte-for-byte the body Map always had.
    private static MeasurementSet MapPoints(
        IReadOnlyList<RtuPoint> points, RtuPointMap crosswalk, DateTime windowUtc,
        QualityFold? quality, string caseName, string note)
    {
        quality ??= QualityFold.Default;

        var measurements = new List<Measurement>();
        int nonFinite = 0;
        foreach (RtuPoint p in LatestPerAddress(points))
        {
            // N337 (October 2026): step positions (tap positions) and 32-bit bitstrings are now decoded points, but
            // they are states of equipment, not electrical quantities: no MeasurementKind takes them, so they are left
            // out of the set. A crosswalk that binds one to a measurement is a configuration fault (it would feed a
            // tap step or a status word to the estimator as a voltage or a flow), so it is refused whatever the
            // point's quality, rather than skipped. InvalidDataException is the pipeline's configuration-fault type
            // (counted in MapErrors and rethrown).
            // GR-787: a binary status (a breaker's 0 / 1) and an integrated counter (a kWh total) are not electrical
            // measurements either; bound as one (an address mix-up) a breaker ON read as a plausible 1.0 pu voltage.
            if (p.Type is RtuPointType.StepPosition or RtuPointType.Bitstring or RtuPointType.BinaryStatus or RtuPointType.Counter)
            {
                if (crosswalk.TryResolve(p.Address, out RtuPointBinding wrong))
                    throw new InvalidDataException(
                        $"RTU crosswalk binds {p.Address} as a {wrong.Kind} measurement, but that address carries a " +
                        (p.Type switch
                        {
                            RtuPointType.StepPosition => "step position (a tap position, IEC 60870-5 type 5 / 32)",
                            RtuPointType.Bitstring => "32-bit bitstring (IEC 60870-5 type 7 / 33)",
                            RtuPointType.BinaryStatus => "binary status (a single or double point, such as a breaker state)",
                            _ => "integrated total (a counter, such as an energy meter reading)",
                        }) +
                        ", which is not an electrical measurement. Remove the binding.");
                continue;
            }
            if (QualityFold.ShouldDrop(p.Quality)) continue;
            if (!crosswalk.TryResolve(p.Address, out RtuPointBinding b)) continue;

            double sigma = b.Sigma * quality.SigmaMultiplier(p.Quality);
            double raw = RawValue(p, b, points);   // register data type applied (R272)
            double v = raw * b.Scale;   // raw-to-engineering scale (identity by default)
            // A NaN or infinite value (an F32 register fault, an IEC 104 float overflow) is not a measurement: it is
            // left out and counted in the note, instead of being written into the window (and the quarantine store).
            if (!double.IsFinite(v)) { nonFinite++; continue; }
            switch (b.Kind)
            {
                case MeasurementKind.VmPu:
                    measurements.Add(new Measurement(MeasurementKind.VmPu, v / b.BaseKv, sigma,
                        Bus: b.Bus, Source: "rtu"));
                    break;
                case MeasurementKind.PInjMw:
                case MeasurementKind.QInjMvar:
                    measurements.Add(new Measurement(b.Kind, v, sigma, Bus: b.Bus, Source: "rtu"));
                    break;
                case MeasurementKind.VaDeg:
                    measurements.Add(new Measurement(MeasurementKind.VaDeg, v - b.DatumAngleDeg, sigma,
                        Bus: b.Bus, Source: "rtu"));
                    break;
                case MeasurementKind.PFlowMw:
                case MeasurementKind.QFlowMvar:
                    measurements.Add(new Measurement(b.Kind, v, sigma,
                        From: b.From, To: b.To, Circuit: b.Circuit, End: b.End, Source: "rtu"));
                    break;
                case MeasurementKind.IMagPu:
                    measurements.Add(new Measurement(MeasurementKind.IMagPu, v / b.BaseAmps, sigma,
                        From: b.From, To: b.To, Circuit: b.Circuit, End: b.End, Source: "rtu"));
                    break;
                case MeasurementKind.IAngDeg:
                    measurements.Add(new Measurement(MeasurementKind.IAngDeg, v - b.DatumAngleDeg, sigma,
                        From: b.From, To: b.To, Circuit: b.Circuit, End: b.End, Source: "rtu"));
                    break;
            }
        }

        if (nonFinite > 0) note += $"; left out {nonFinite} non-finite value(s)";
        return new MeasurementSet
        {
            Case = caseName,
            BaseMva = crosswalk.BaseMva,
            TakenUtc = windowUtc,
            Note = note,
            Measurements = measurements,
        };
    }

    /// <summary>
    /// Collapse a window to ONE sample per address (R032). A point reported several times in a window (periodic
    /// plus spontaneous IEC-104 updates, repeated Modbus polls) used to become several independent measurements
    /// with the same sigma, so the estimator weighted it N times over, suppressed its residual and inflated the
    /// chi-square degrees of freedom by N-1. A window is a snapshot, so the point's CURRENT value is what counts:
    /// the sample with the latest device time stamp wins; when the stamps tie, or either sample has none (Modbus
    /// carries no time tag), the later-arriving sample wins. Output order is the order in which each address
    /// first appeared, so a window with no repeats maps exactly as it always did.
    /// </summary>
    internal static List<RtuPoint> LatestPerAddress(IReadOnlyList<RtuPoint> points)
    {
        var slot = new Dictionary<string, int>(StringComparer.Ordinal);   // address -> index in 'latest'
        var latest = new List<RtuPoint>(points.Count);
        foreach (RtuPoint p in points)
        {
            if (!slot.TryGetValue(p.Address, out int i))
            {
                slot[p.Address] = latest.Count;
                latest.Add(p);
                continue;
            }
            RtuPoint current = latest[i];
            bool newer = p.StampUtc is DateTime ps && current.StampUtc is DateTime cs
                ? ps >= cs      // both stamped: the later device time wins (a tie goes to the later arrival)
                : true;         // otherwise arrival order decides: the later arrival wins
            if (newer) latest[i] = p;
        }
        return latest;
    }

    /// <summary>
    /// The point's raw value under its binding's register data type (R272). <see cref="RtuRegisterType.AsDecoded"/>
    /// is the value unchanged. The 16-bit types check the value really is one 16-bit register. The 32-bit types
    /// combine this register with the next-numbered one FROM THE SAME FRAME (never halves of two different polls)
    /// in the binding's word order. Any mismatch is a configuration error and throws
    /// <see cref="InvalidDataException"/> rather than feeding the estimator a wrong number.
    /// </summary>
    private static double RawValue(RtuPoint p, RtuPointBinding b, IReadOnlyList<RtuPoint> window)
    {
        if (b.DataType == RtuRegisterType.AsDecoded) return p.Value;
        RtuPointMap.ValidateDataType(b);
        ushort first = AsRegister(p);

        switch (b.DataType)
        {
            case RtuRegisterType.U16: return first;
            case RtuRegisterType.S16: return unchecked((short)first);
        }

        // 32-bit: find the companion register (register number + 1) in the same frame.
        // ValidateDataType above already proved the binding address is a Modbus register; the point's address
        // is the binding's, so this cannot fail - but check anyway rather than assume.
        if (!RtuPointMap.TryParseModbusRegister(p.Address, out string prefix, out int register))
            throw new InvalidDataException($"Point '{p.Address}' is not a Modbus register address.");
        string nextAddress = prefix + (register + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        RtuPoint? next = null;
        foreach (RtuPoint q in window)
            if (q.FrameSeq == p.FrameSeq && string.Equals(q.Address, nextAddress, StringComparison.Ordinal))
                next = q;   // the same frame holds each register once; the last match is that one
        if (next is null)
            throw new InvalidDataException(
                $"Binding '{b.Address}' is {b.DataType} (two registers) but register '{nextAddress}' is not in the " +
                "same response. Poll a block that includes both registers.");
        ushort second = AsRegister(next);

        (ushort hi, ushort lo) = b.WordOrder == RtuWordOrder.HighWordFirst ? (first, second) : (second, first);
        uint bits = ((uint)hi << 16) | lo;
        return b.DataType switch
        {
            RtuRegisterType.U32 => bits,
            RtuRegisterType.S32 => unchecked((int)bits),
            RtuRegisterType.F32 => BitConverter.Int32BitsToSingle(unchecked((int)bits)),
            _ => throw new InvalidDataException($"Binding '{b.Address}': unknown dataType {b.DataType}."),
        };
    }

    // A raw Modbus register is an integer 0..65535; anything else means the binding's data type does not fit.
    private static ushort AsRegister(RtuPoint p)
    {
        if (p.Value < 0 || p.Value > ushort.MaxValue || p.Value != Math.Floor(p.Value))
            throw new InvalidDataException(
                $"Point '{p.Address}' value {p.Value} is not a raw 16-bit register, so a register data type cannot apply.");
        return (ushort)p.Value;
    }
}
