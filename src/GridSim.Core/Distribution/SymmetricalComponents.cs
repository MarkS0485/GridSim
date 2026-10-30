using System.Numerics;

namespace GridSim.Core.Distribution;

/// <summary>
/// The Fortescue symmetrical-component transform between phase (abc) and sequence (zero/positive/negative,
/// 012) quantities. V_012 = A^-1 V_abc, V_abc = A V_012, with A the standard 3x3 [1,1,1; 1,a^2,a; 1,a,a^2]
/// and a = 1&lt;120deg. It underpins the positive-sequence collapse of an unbalanced solution and the
/// sequence-network build the earth-fault study needs.
/// </summary>
public static class SymmetricalComponents
{
    /// <summary>The rotation operator a = 1&lt;120 degrees.</summary>
    public static readonly Complex A = DetMath.FromPolar(1.0, 2.0 * Math.PI / 3.0);
    /// <summary>a^2 = 1&lt;240 degrees (= 1&lt;-120 degrees).</summary>
    public static readonly Complex A2 = DetMath.FromPolar(1.0, -2.0 * Math.PI / 3.0);

    /// <summary>Phase (abc) -> sequence (0, +, -): V0 = mean, V1 = positive, V2 = negative.</summary>
    public static Complex[] ToSequence(Complex[] abc)
    {
        Complex va = abc[0], vb = abc[1], vc = abc[2];
        return new[]
        {
            (va + vb + vc) / 3.0,
            (va + A * vb + A2 * vc) / 3.0,
            (va + A2 * vb + A * vc) / 3.0,
        };
    }

    /// <summary>Sequence (0, +, -) -> phase (abc).</summary>
    public static Complex[] ToPhase(Complex[] seq012)
    {
        Complex v0 = seq012[0], v1 = seq012[1], v2 = seq012[2];
        return new[]
        {
            v0 + v1 + v2,
            v0 + A2 * v1 + A * v2,
            v0 + A * v1 + A2 * v2,
        };
    }

    /// <summary>The positive-sequence component of a phase triple - the single-line-equivalent voltage used
    /// to collapse an unbalanced solution onto positive-sequence tooling.</summary>
    public static Complex PositiveSequence(Complex[] abc) => (abc[0] + A * abc[1] + A2 * abc[2]) / 3.0;
}
