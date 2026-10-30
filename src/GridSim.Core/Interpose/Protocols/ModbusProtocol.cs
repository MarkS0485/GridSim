using GridSim.Core.IO.Telemetry;

namespace GridSim.Core.Interpose.Protocols;

/// <summary>
/// Modbus TCP. Decodes a read-holding/input-registers response (<see cref="ModbusFrame"/>) into one point per
/// register. Modbus has no native quality or timestamp: points are <see cref="RtuQuality.Good"/> with no device
/// time (the window's receive clock stamps them), and the raw register is turned into engineering units by the
/// crosswalk binding (<see cref="RtuPointBinding.DataType"/>, then <see cref="RtuPointBinding.Scale"/>).
///
/// <para><b>Addressing (R271).</b> A Modbus response carries no start address; only the request that asked for
/// it does. So this decoder remembers every read request it sees (keyed by MBAP transaction id) and pairs each
/// response with its request:</para>
/// <list type="bullet">
/// <item><b>Paired</b> (the tap sees both directions): each register gets its ABSOLUTE address
/// <c>MB:U&lt;unit&gt;:&lt;fc&gt;:@&lt;register&gt;</c>, e.g. <c>MB:U1:3:@100</c>. Two different polled blocks
/// can never collide.</item>
/// <item><b>Unpaired</b> (only responses are seen): the register is addressed by its POSITION in the response,
/// <c>MB:U&lt;unit&gt;:&lt;fc&gt;:&lt;index&gt;</c>, as before. Positional addresses from two different blocks on
/// the same unit and function code would collide in the crosswalk and silently bind one block's register to
/// the other's measurement. That is refused: once a positional block of N registers has been seen for a
/// unit/fc, a positional response with a different register count throws <see cref="InvalidDataException"/>
/// (counted by <see cref="InterposePipeline.DecodeErrors"/>, never mapped). Two blocks of the SAME size cannot
/// be told apart without their requests; a deployment that polls more than one block per unit/fc must let the
/// interposer see the requests.</item>
/// </list>
/// <para>The decoder is stateful (it holds the outstanding requests), so it is thread-safe but one instance
/// should serve one stream. GR-329: <see cref="ResetSession"/> forgets that state; the interposer calls it when its client
/// goes away, so a new connection starts clean.</para>
/// <para><b>Transaction ids (GR-801).</b> A response is paired with the outstanding request of its transaction id. When a
/// second, DIFFERENT read request arrives with an id that is still outstanding (a gateway that reuses or pipelines ids),
/// which request the next response with that id answers cannot be known: that id is marked ambiguous, and its response
/// is refused (InvalidDataException, counted as a decode error, never mapped) rather than paired with the later request.
/// The same request sent again (a retry) is not ambiguous. An exception response (function code with bit 7 set) ends
/// its transaction: the outstanding request is forgotten and no point is produced.</para>
/// </summary>
public sealed class ModbusProtocol : IRtuProtocol, IRtuSessionState
{
    public string Name => "modbus";
    public string DisplayName => "Modbus TCP";
    public RtuTransportKind Transport => RtuTransportKind.Tcp;
    public int DefaultPort => 502;

    private readonly object _gate = new();

    // Outstanding read requests by MBAP transaction id (16-bit, so at most 65,536 entries are ever held).
    private readonly Dictionary<int, ModbusReadRequest> _pending = new();

    // For unpaired (positional) responses: the register count first seen per (unit, function code).
    private readonly Dictionary<(int Unit, byte Fc), int> _positionalBlockSize = new();

    // GR-801: transaction ids that had two different requests outstanding at once; their next response is refused.
    private readonly HashSet<int> _ambiguous = new();

    /// <inheritdoc/>
    public void ResetSession()
    {
        lock (_gate)
        {
            _pending.Clear();
            _positionalBlockSize.Clear();
            _ambiguous.Clear();
        }
    }

    public IReadOnlyList<RtuPoint> Decode(ReadOnlySpan<byte> frame, QualityFold quality)
    {
        // A request carries no values: remember it so its response can be given absolute addresses.
        if (ModbusFrame.TryParseReadRequest(frame, out ModbusReadRequest request))
        {
            lock (_gate)
            {
                // GR-801: a different request under an id still outstanding makes that id's next response ambiguous.
                if (_pending.TryGetValue(request.TransactionId, out ModbusReadRequest? earlier) && earlier != request)
                    _ambiguous.Add(request.TransactionId);
                _pending[request.TransactionId] = request;
            }
            return Array.Empty<RtuPoint>();
        }

        ModbusResponse resp = ModbusFrame.Parse(frame);

        // GR-801: an exception response (function code | 0x80) answers its request with an error code and no registers:
        // the transaction is over, so its request is forgotten (it used to stay outstanding and could later be paired
        // with an unrelated response that reused the id).
        if ((resp.FunctionCode & 0x80) != 0)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(resp.TransactionId, out ModbusReadRequest? asked)
                    && asked.UnitId == resp.UnitId && asked.FunctionCode == (resp.FunctionCode & 0x7F))
                {
                    _pending.Remove(resp.TransactionId);
                    _ambiguous.Remove(resp.TransactionId);
                }
            }
            return Array.Empty<RtuPoint>();
        }
        if (resp.Registers.Count == 0) return Array.Empty<RtuPoint>();

        ModbusReadRequest? paired = null;
        lock (_gate)
        {
            // GR-801: refuse the response to an id that had two different requests outstanding; forget both.
            if (_ambiguous.Remove(resp.TransactionId))
            {
                _pending.Remove(resp.TransactionId);
                throw new InvalidDataException(
                    $"Modbus transaction {resp.TransactionId}: two different read requests were outstanding under this id, so " +
                    "which one this response answers cannot be known; it is refused rather than given the later request's " +
                    "addresses. The gateway reuses or pipelines transaction ids.");
            }
            if (_pending.Remove(resp.TransactionId, out ModbusReadRequest? req)
                && req.UnitId == resp.UnitId && req.FunctionCode == resp.FunctionCode)
            {
                paired = req;
            }
            else
            {
                // Positional fallback: refuse a second, differently sized block for the same unit/fc, because
                // its positional addresses would collide with the first block's in the crosswalk.
                var key = (resp.UnitId, resp.FunctionCode);
                if (_positionalBlockSize.TryGetValue(key, out int seen) && seen != resp.Registers.Count)
                    throw new InvalidDataException(
                        $"Modbus unit {resp.UnitId} FC{resp.FunctionCode}: a {resp.Registers.Count}-register response " +
                        $"arrived after a {seen}-register one, with no request seen to give either block's start " +
                        "address. Their positional addresses (MB:U<unit>:<fc>:<index>) would collide in the crosswalk, " +
                        "so this response is refused. Let the interposer see the poll requests to get absolute " +
                        "addresses (MB:U<unit>:<fc>:@<register>).");
                _positionalBlockSize[key] = resp.Registers.Count;
            }
        }

        if (paired is not null && paired.Quantity != resp.Registers.Count)
            throw new InvalidDataException(
                $"Modbus transaction {resp.TransactionId}: the request asked for {paired.Quantity} register(s) " +
                $"from {paired.StartAddress} but the response carries {resp.Registers.Count}.");

        var points = new List<RtuPoint>(resp.Registers.Count);
        foreach (ModbusRegister r in resp.Registers)
        {
            string address = paired is not null
                ? $"MB:U{resp.UnitId}:{resp.FunctionCode}:@{paired.StartAddress + r.Index}"   // absolute register
                : $"MB:U{resp.UnitId}:{resp.FunctionCode}:{r.Index}";                         // position in response
            points.Add(new RtuPoint(address, RtuPointType.Analog, r.Value, RtuQuality.Good, StampUtc: null));
        }
        return points;
    }
}
