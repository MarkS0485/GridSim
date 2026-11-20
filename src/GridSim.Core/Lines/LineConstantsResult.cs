using System.Numerics;

namespace GridSim.Core.Lines;

/// <summary>Sequence-domain constants of a three-phase line (per km).</summary>
public sealed record SequenceConstants(Complex Z1, Complex Z0, double C1FaradPerKm, double C0FaradPerKm);

/// <summary>
/// Every stage of the line-constants calculation is kept, in the order of the defined sequence: primitive Z and P over all
/// conductors, then the Kron-reduced phase Z and C, then the transposition average, then the sequence constants.
/// Units: impedance ohm/km, potential coefficient km/F (P), capacitance F/km.
/// </summary>
public sealed record LineConstantsResult(
    string Method,
    string Schema,
    double FrequencyHz,
    double EarthResistivityOhmM,
    IReadOnlyList<string> AllConductors,
    IReadOnlyList<string> PhaseConductors,
    Complex[,] PrimitiveZ,
    double[,] PrimitiveP,
    Complex[,] PhaseZ,
    double[,] PhaseC,
    Complex[,] TransposedZ,
    double[,] TransposedC,
    SequenceConstants? Sequence,
    string? SequenceNote,
    double IntegralTailBound)
{
    /// <summary>
    /// Positive-sequence R, X and B in per unit on a stated base for a line of <paramref name="lengthKm"/>:
    /// Zbase = kV^2 / MVA; B = w C1 length Zbase. Refuses if there is no sequence result.
    /// </summary>
    public (double R, double X, double B) PositiveSequencePerUnit(double lengthKm, double baseKv, double baseMva)
    {
        if (Sequence is null) throw new InvalidOperationException(SequenceNote ?? "No sequence constants.");
        if (!(lengthKm > 0 && baseKv > 0 && baseMva > 0)) throw new ArgumentOutOfRangeException(nameof(lengthKm), "Length, base voltage and base power must be positive.");
        double zb = baseKv * baseKv / baseMva;
        double w = 2.0 * Math.PI * FrequencyHz;
        return (Sequence.Z1.Real * lengthKm / zb, Sequence.Z1.Imaginary * lengthKm / zb, w * Sequence.C1FaradPerKm * lengthKm * zb);
    }
}
