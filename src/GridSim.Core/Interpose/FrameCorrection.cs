namespace GridSim.Core.Interpose;

/// <summary>A frame-level validator: true if the bytes are a structurally plausible frame for a protocol.</summary>
public delegate bool FrameValidator(ReadOnlySpan<byte> frame);

/// <summary>The outcome of validating one candidate frame.</summary>
/// <param name="Ok">Whether the frame passed and may be decoded.</param>
/// <param name="Reason">Why it failed (null when <paramref name="Ok"/> is true).</param>
public readonly record struct FrameCorrectionResult(bool Ok, string? Reason);

/// <summary>
/// The frame-correction stage of the interposer pipeline. It gates each candidate frame handed up by the
/// transport reassembler - rejecting mis-framed or CRC-failed bytes so a bad frame never reaches the
/// object-model decoder - and tallies running counts the monitor surfaces. Byte-stream <b>resync</b>
/// (scanning to the next start delimiter after a bad frame) lives in the transport, which owns the buffer;
/// this stage owns the yes/no on a delimited candidate. Construct one per protocol with
/// <see cref="ForProtocol"/>.
/// </summary>
public sealed class FrameCorrection
{
    private readonly FrameValidator _validate;

    /// <summary>Frames that passed validation.</summary>
    public long Accepted { get; private set; }

    /// <summary>Frames rejected as mis-framed / integrity-failed.</summary>
    public long Rejected { get; private set; }

    private FrameCorrection(FrameValidator validate) => _validate = validate;

    /// <summary>Build the validator for a protocol. Protocols whose transport carries its own integrity
    /// (IEC-104 rides TCP) get a framing check; CRC-bearing protocols validate their block check via the
    /// existing codecs as those land.</summary>
    public static FrameCorrection ForProtocol(IRtuProtocol p) => p.Name switch
    {
        // GR-814: one APDU exactly: start octet 0x68, a length octet of 4 to 253 (the IEC 60870-5-104 range) and no
        // byte beyond it. It used to accept trailing bytes (a buffer of two APDUs passed, the second was lost and not
        // counted) and length octets of 254 and 255.
        "iec104" => new FrameCorrection(static f => f.Length >= 6 && f[0] == 0x68 && f[1] >= 4 && f[1] <= 253 && f[1] + 2 == f.Length),
        _ => new FrameCorrection(static f => f.Length > 0),
    };

    /// <summary>Validate a delimited candidate frame, updating the running tallies.</summary>
    public FrameCorrectionResult Validate(ReadOnlySpan<byte> frame)
    {
        if (_validate(frame))
        {
            Accepted++;
            return new FrameCorrectionResult(true, null);
        }
        Rejected++;
        return new FrameCorrectionResult(false, "frame failed framing/integrity validation");
    }
}
