namespace GridSim.Core.IO.Telemetry;

/// <summary>One 16-bit register in a Modbus read response, keyed by its position in the response block.</summary>
/// <param name="Index">Zero-based position of the register within the response.</param>
/// <param name="Value">The raw 16-bit register value (big-endian on the wire).</param>
public sealed record ModbusRegister(int Index, ushort Value);

/// <summary>A decoded Modbus read-registers response.</summary>
/// <param name="TransactionId">MBAP transaction identifier.</param>
/// <param name="UnitId">Unit / slave identifier.</param>
/// <param name="FunctionCode">3 (read holding) or 4 (read input); other codes yield no registers.</param>
/// <param name="Registers">The register block, empty for requests / exceptions / unsupported codes.</param>
public sealed record ModbusResponse(int TransactionId, int UnitId, byte FunctionCode, IReadOnlyList<ModbusRegister> Registers);

/// <summary>A decoded Modbus read-registers REQUEST (function 3 or 4): the poll that names which register
/// block the matching response carries. A response carries no start address of its own, so pairing it with
/// its request (by MBAP transaction id) is the only way to know the absolute register numbers.</summary>
/// <param name="TransactionId">MBAP transaction identifier (echoed by the response).</param>
/// <param name="UnitId">Unit / slave identifier.</param>
/// <param name="FunctionCode">3 (read holding) or 4 (read input).</param>
/// <param name="StartAddress">The first register requested (0-based protocol address).</param>
/// <param name="Quantity">How many registers were requested.</param>
public sealed record ModbusReadRequest(int TransactionId, int UnitId, byte FunctionCode, int StartAddress, int Quantity);

/// <summary>
/// A codec for the Modbus TCP (MBAP) framing and the read-holding/input-registers <b>response</b> PDU - the
/// application layer for the register-oriented half of the interposer. It decodes the 7-octet MBAP header
/// (transaction id, protocol id, length, unit id) and, for a function-3/4 response, the byte-count + big-endian
/// 16-bit register block. Modbus carries no native quality or time tag; the register scale to engineering
/// units and the bus/branch binding are supplied by the crosswalk. Responses are address-less on the wire, so
/// registers are keyed by their position in the response (the poll layout the crosswalk author configured).
/// </summary>
public static class ModbusFrame
{
    /// <summary>Decode one MBAP frame. A request (or an exception/unsupported response) decodes to an empty
    /// register list, not an error. A frame whose size does not match its MBAP length, or a function-3/4 response
    /// whose byte count is inconsistent, throws <see cref="ArgumentException"/> (the pipeline counts it as a decode
    /// error).</summary>
    public static ModbusResponse Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8) throw new ArgumentException("Modbus TCP frame shorter than its MBAP header + function code.");
        int txn = (frame[0] << 8) | frame[1];
        int proto = (frame[2] << 8) | frame[3];
        if (proto != 0) throw new ArgumentException($"Modbus protocol id is {proto}, expected 0.");
        int length = (frame[4] << 8) | frame[5];       // bytes following the length field (unit id + PDU)
        int unit = frame[6];
        byte fc = frame[7];

        // A read-registers RESPONSE has: unit(1) + fc(1) + byteCount(1) + data(byteCount) => length = 3 + byteCount.
        // A read-registers REQUEST has length 6 (unit+fc+start+qty); distinguish by the length relation so a
        // passively-observed request isn't mis-parsed as a response.
        // The MBAP length counts the bytes after the length field, so a whole frame is exactly 6 + length bytes. A
        // frame of any other size is truncated or has trailing bytes; it is refused, not read as "no registers".
        if (frame.Length != 6 + length)
            throw new ArgumentException($"Modbus TCP frame is {frame.Length} bytes but its MBAP length {length} needs {6 + length}.");

        // A function-3/4 frame that is not a request (length 6) must be a well-formed response; a malformed one
        // (byte count not matching the MBAP length, or odd) is refused rather than decoded as an empty register list.
        if ((fc == 3 || fc == 4) && length != 6)
        {
            if (frame.Length < 9)
                throw new ArgumentException("Modbus TCP function-3/4 response has no byte count.");
            int byteCount = frame[8];
            if (length != 3 + byteCount || byteCount % 2 != 0)
                throw new ArgumentException(
                    $"Modbus TCP function-{fc} response has byte count {byteCount} and MBAP length {length}; a response needs " +
                    "an even byte count and length = 3 + byte count.");
            {
                int nReg = byteCount / 2;
                var regs = new List<ModbusRegister>(nReg);
                for (int i = 0; i < nReg; i++)
                {
                    int off = 9 + i * 2;
                    regs.Add(new ModbusRegister(i, (ushort)((frame[off] << 8) | frame[off + 1])));
                }
                return new ModbusResponse(txn, unit, fc, regs);
            }
        }

        return new ModbusResponse(txn, unit, fc, Array.Empty<ModbusRegister>());
    }

    /// <summary>
    /// Recognise a function-3/4 read-registers REQUEST (MBAP length 6: unit + fc + start(2) + quantity(2)) and
    /// return its start address and quantity. Returns false for anything else (a response, an exception, an
    /// unsupported function). A response always has an even byte count, so its length (3 + byteCount) can never
    /// be 6: the two shapes cannot be confused.
    /// </summary>
    public static bool TryParseReadRequest(ReadOnlySpan<byte> frame, out ModbusReadRequest request)
    {
        request = null!;
        if (frame.Length < 12) return false;
        if (((frame[2] << 8) | frame[3]) != 0) return false;           // protocol id must be 0
        int length = (frame[4] << 8) | frame[5];
        byte fc = frame[7];
        if (length != 6 || (fc != 3 && fc != 4)) return false;
        request = new ModbusReadRequest(
            TransactionId: (frame[0] << 8) | frame[1],
            UnitId: frame[6],
            FunctionCode: fc,
            StartAddress: (frame[8] << 8) | frame[9],
            Quantity: (frame[10] << 8) | frame[11]);
        return true;
    }
}
