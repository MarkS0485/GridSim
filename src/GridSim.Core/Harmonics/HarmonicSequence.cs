namespace GridSim.Core.Harmonics;

/// <summary>The symmetrical-component sequence a balanced harmonic order rotates as.</summary>
public enum HarmonicSequenceKind
{
    /// <summary>Positive sequence (h = 1, 4, 7, ... ; h mod 3 == 1).</summary>
    Positive,
    /// <summary>Negative sequence (h = 2, 5, 8, ... ; h mod 3 == 2).</summary>
    Negative,
    /// <summary>Zero sequence - the triplens (h = 3, 6, 9, ... ; h mod 3 == 0), which add in the neutral.</summary>
    Zero,
}

/// <summary>
/// The sequence a balanced harmonic order presents as. In a balanced three-phase set the phase rotation of
/// order h cycles positive / negative / zero with h: the fundamental is positive, the 5th negative, the 3rd
/// (and every triplen) zero-sequence and adds in the neutral. This is why triplens matter in four-wire
/// systems and vanish in a balanced delta - the driver of much of the three-phase harmonic story (Phase 5).
/// </summary>
public static class HarmonicSequence
{
    /// <summary>The sequence order <paramref name="h"/> rotates as in a balanced system.</summary>
    // GR-578: a harmonic order is 1 or above; 0 and negative orders used to be classed as zero sequence (C# % keeps the
    // sign, so -1 % 3 is -1 and fell to the default arm).
    public static HarmonicSequenceKind Of(int h) => h < 1
        ? throw new ArgumentOutOfRangeException(nameof(h), h, "A harmonic order is 1 or above.")
        : (h % 3) switch
    {
        1 => HarmonicSequenceKind.Positive,
        2 => HarmonicSequenceKind.Negative,
        _ => HarmonicSequenceKind.Zero,
    };

    /// <summary>True if <paramref name="h"/> is a triplen (zero-sequence) harmonic.</summary>
    public static bool IsTriplen(int h) => h % 3 == 0;
}
