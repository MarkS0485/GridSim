namespace GridSim.Core.Interpose;

/// <summary>
/// Turns protocol-native quality descriptors into a uniform <see cref="RtuQuality"/>, then into the one
/// thing the estimator understands: a <b>sigma multiplier</b> (or a decision to drop the point entirely).
/// <see cref="Estimation.Measurement"/> has no quality field - larger sigma simply means lower trust in the
/// gain matrix - so this is the single place adverse flags become weight. The multipliers are deliberately
/// conservative: a stale or substituted value is admitted but down-weighted; an invalid/blocked/comm-lost
/// value never enters the solve.
/// </summary>
public sealed class QualityFold
{
    /// <summary>Sigma multiplier applied to a not-topical (stale) value.</summary>
    public double NonTopicalFactor { get; init; } = 4.0;

    /// <summary>Sigma multiplier applied to an operator-substituted value.</summary>
    public double SubstitutedFactor { get; init; } = 2.0;

    /// <summary>Sigma multiplier applied while a source device is re-integrating after a restart.</summary>
    public double RestartFactor { get; init; } = 4.0;

    /// <summary>The default folder with the standard factors.</summary>
    public static QualityFold Default { get; } = new();

    /// <summary>
    /// Decode an IEC 60870-5-101/104 quality descriptor octet (QDS, or the quality bits of an SIQ/DIQ/BCR).
    /// The high nibble is shared across those descriptors: IV=b7, NT=b6, SB=b5, BL=b4; OV=b0 (QDS only).
    /// </summary>
    public static RtuQuality FromIec104(byte qds)
    {
        RtuQuality q = RtuQuality.Good;
        if ((qds & 0x80) != 0) q |= RtuQuality.Invalid;
        if ((qds & 0x40) != 0) q |= RtuQuality.NonTopical;
        if ((qds & 0x20) != 0) q |= RtuQuality.Substituted;
        if ((qds & 0x10) != 0) q |= RtuQuality.Blocked;
        if ((qds & 0x01) != 0) q |= RtuQuality.Overflow;
        return q;
    }

    /// <summary>Decode a DNP3 object flags octet (bit0 online, bit1 restart, bit2 comm-lost,
    /// bit5 over-range/reference-check). A cleared online bit means the point is not being updated.
    /// SCAFFOLDING (R089): no product caller; there is no DNP3 protocol stack for it to serve (see Dnp3Frame).</summary>
    public static RtuQuality FromDnp3(byte flags)
    {
        RtuQuality q = RtuQuality.Good;
        if ((flags & 0x01) == 0) q |= RtuQuality.Invalid;   // online bit clear = not valid
        if ((flags & 0x02) != 0) q |= RtuQuality.Restart;
        if ((flags & 0x04) != 0) q |= RtuQuality.CommLost;
        if ((flags & 0x20) != 0) q |= RtuQuality.Overflow;  // over-range (AI) / reference-check
        return q;
    }

    /// <summary>Fold GOOSE membership into quality: a non-good validity, a test bit, or needs-commissioning
    /// all render the sample unusable for estimation. SCAFFOLDING (R089): no product caller; there is no GOOSE
    /// protocol stack for it to serve (see Iec61850GooseFrame).</summary>
    public static RtuQuality FromGoose(bool valid, bool test, bool needsCommissioning)
    {
        RtuQuality q = RtuQuality.Good;
        if (!valid) q |= RtuQuality.Invalid;
        if (test) q |= RtuQuality.Invalid;
        if (needsCommissioning) q |= RtuQuality.Invalid;
        return q;
    }

    /// <summary>True if a point carrying this quality must never enter the estimator.</summary>
    public static bool ShouldDrop(RtuQuality q) =>
        (q & (RtuQuality.Invalid | RtuQuality.Blocked | RtuQuality.Overflow | RtuQuality.CommLost)) != 0;

    /// <summary>The sigma multiplier for an admitted (non-dropped) point; 1.0 when <see cref="RtuQuality.Good"/>.
    /// Multiple adverse flags multiply, so a substituted-and-stale value is down-weighted more than either alone.</summary>
    public double SigmaMultiplier(RtuQuality q)
    {
        double f = 1.0;
        if ((q & RtuQuality.NonTopical) != 0) f *= NonTopicalFactor;
        if ((q & RtuQuality.Substituted) != 0) f *= SubstitutedFactor;
        if ((q & RtuQuality.Restart) != 0) f *= RestartFactor;
        return f;
    }
}
