using System.Numerics;
using GridSim.Core.Numerics;

namespace GridSim.Core.Lines.Cables;

/// <summary>
/// Schelkunoff's surface impedances of round conductors, per metre, at angular frequency w, with the wave number
/// m = sqrt(j w mu0 mu_r / rho) of the conductor material (Schelkunoff, "The electromagnetic theory of coaxial transmission
/// lines and cylindrical shields", Bell Syst. Tech. J. 13, 1934; in this form in Ametani, IEEE Trans. PAS-99(3), 1980,
/// and in the EMTP Theory Book, Dommel, BPA 1986, cable constants chapter).
/// <list type="bullet">
/// <item>Solid core of radius r: z = rho m I0(m r) / (2 pi r I1(m r)).</item>
/// <item>Tube from radius a to b, with D = I1(m b) K1(m a) - I1(m a) K1(m b):
/// inner surface z_aa = rho m [I0(m a) K1(m b) + K0(m a) I1(m b)] / (2 pi a D);
/// outer surface z_bb = rho m [I0(m b) K1(m a) + K0(m b) I1(m a)] / (2 pi b D);
/// mutual z_ab = rho / (2 pi a b D).</item>
/// <item>Insulation from r_i to r_o: z = j w mu0 mu_r ln(r_o / r_i) / (2 pi).</item>
/// </list>
/// The Bessel functions are taken in scaled form (<see cref="ComplexBessel.Scaled"/>) and the exponentials collected as
/// exp(-m (b - a)) and exp(-2 m (b - a)), so a thick tube at high frequency neither overflows nor divides infinity by
/// infinity. All values are ohm per metre.
/// </summary>
public static class TubularImpedance
{
    /// <summary>The three surface impedances of a tube (ohm/m).</summary>
    public readonly record struct Tube(Complex Inner, Complex Outer, Complex Mutual);

    /// <summary>The wave number m = sqrt(j w mu0 mu_r / rho) (1/m), phase pi/4.</summary>
    public static Complex WaveNumber(double omega, double resistivityOhmM, double relativePermeability) =>
        Complex.Sqrt(new Complex(0.0, omega * EarthReturn.Mu0 * relativePermeability / resistivityOhmM));

    /// <summary>The surface impedance of a solid round conductor of radius <paramref name="radiusM"/>.</summary>
    public static Complex SolidCore(double omega, double resistivityOhmM, double relativePermeability, double radiusM)
    {
        Complex m = WaveNumber(omega, resistivityOhmM, relativePermeability);
        ComplexBessel.Values v = ComplexBessel.Scaled(m * radiusM);
        return resistivityOhmM * m / (2.0 * Math.PI * radiusM) * (v.I0 / v.I1);
    }

    /// <summary>The inner, outer and mutual surface impedances of a tube from <paramref name="innerM"/> to <paramref name="outerM"/>.</summary>
    public static Tube Tubular(double omega, double resistivityOhmM, double relativePermeability, double innerM, double outerM)
    {
        Complex m = WaveNumber(omega, resistivityOhmM, relativePermeability);
        ComplexBessel.Values va = ComplexBessel.Scaled(m * innerM);
        ComplexBessel.Values vb = ComplexBessel.Scaled(m * outerM);
        Complex s = m * (outerM - innerM);
        Complex e1 = ComplexBessel.Exp(-s);
        Complex e2 = ComplexBessel.Exp(-2.0 * s);
        // D = exp(s) * dt; the numerators carry the same exp(s), which cancels.
        Complex dt = vb.I1 * va.K1 - e2 * va.I1 * vb.K1;
        Complex inner = resistivityOhmM * m / (2.0 * Math.PI * innerM) * (va.K0 * vb.I1 + e2 * va.I0 * vb.K1) / dt;
        Complex outer = resistivityOhmM * m / (2.0 * Math.PI * outerM) * (vb.I0 * va.K1 + e2 * vb.K0 * va.I1) / dt;
        Complex mutual = resistivityOhmM / (2.0 * Math.PI * innerM * outerM) * e1 / dt;
        return new Tube(inner, outer, mutual);
    }

    /// <summary>The series impedance of an insulation layer from <paramref name="innerM"/> to <paramref name="outerM"/> (purely inductive).</summary>
    public static Complex Insulation(double omega, double relativePermeability, double innerM, double outerM) =>
        new(0.0, omega * EarthReturn.Mu0 * relativePermeability / (2.0 * Math.PI) * DetMath.Log(outerM / innerM));
}
