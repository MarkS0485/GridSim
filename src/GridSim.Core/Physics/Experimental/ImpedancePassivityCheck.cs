using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Dynamics.Impedance;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Physics.Experimental;

/// <summary>
/// EXPERIMENTAL, opt-in frequency-domain passivity audit - the small-signal analogue of the Ampere/passivity
/// law (<see cref="PhysicsLaws"/> law 5). A passive network's driving-point impedance is positive-real:
/// <c>Re(Z_grid(jw)) &gt;= -tol</c> for all w. A converter that closes a loop with the grid may present a
/// NON-passive band (a negative-real admittance around the PLL bandwidth); that is not itself an instability -
/// the Nyquist test adjudicates it - so this is reported, never gated.
///
/// <para>Like <see cref="ReciprocityCheck"/> this is NOT one of the ten headline laws, is not part of
/// <see cref="PhysicsLaws.Audit"/>, and is NEVER in the hard-gate set. It reports
/// <see cref="LawStatus.Constitutive"/> when passivity holds and <see cref="LawStatus.Violated"/> (with the
/// offending frequency) otherwise - a diagnostic, not an acceptance gate.</para>
/// </summary>
public static class ImpedancePassivityCheck
{
    /// <summary>
    /// Audit the driving-point grid impedance at <paramref name="busId"/> over <paramref name="fHz"/> for
    /// positive-realness. Reports the most-negative <c>Re(Z)</c> and the frequency it occurs at.
    /// </summary>
    public static LawResult AuditGridPassivity(
        GridModel model, int busId, IReadOnlyList<double> fHz, double f0Hz,
        HarmonicModelOptions? options = null, IComplexLinearSolver? solver = null, double tol = 1e-9)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        solver ??= new RealExpandedComplexSolver();

        double minRe = double.PositiveInfinity;
        double atF = double.NaN;
        int skipped = 0;
        foreach (double f in fHz)
        {
            Complex z;
            try { z = GridImpedanceAssembler.DrivingPointAtRatio(model, busId, f / f0Hz, opt, solver); }
            catch (InvalidOperationException) { skipped++; continue; }   // singular (resonance/DC): no Z to judge
            // A NaN real part is not passive: it wins the min (as -Infinity), so it can never read positive-real.
            if (!(z.Real >= minRe)) { minRe = double.IsNaN(z.Real) ? double.NegativeInfinity : z.Real; atF = f; }
        }

        // R091 (fail loud): when EVERY frequency was singular nothing was audited, but minRe used to default to
        // 0 and the grid was reported positive-real ("passive"). Refuse a verdict instead; and always disclose
        // how many frequencies were skipped.
        if (double.IsPositiveInfinity(minRe))
            throw new InvalidOperationException(
                $"Grid passivity audit at bus {busId}: all {fHz.Count} frequencies were singular, so nothing was " +
                "audited (no path to ground at any swept frequency?).");
        string skippedNote = skipped == 0 ? "" : $"; {skipped} of {fHz.Count} frequencies singular and skipped";

        bool passive = minRe >= -tol;
        return new LawResult(PhysicsCheckRegistry.GridPassivityLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.GridPassivityLaw],
            "passive network driving-point impedance is positive-real: Re(Z_grid(jw)) >= 0",
            passive ? LawStatus.Constitutive : LawStatus.Violated,
            Math.Max(0.0, -minRe), "pu", tol,
            passive
                ? $"driving-point Re(Z) >= {minRe:E2} pu at bus {busId} over the swept band (positive-real){skippedNote}"
                : $"NON-PASSIVE driving-point: min Re(Z) = {minRe:E2} pu at {atF:F2} Hz, bus {busId}{skippedNote}");
    }

    /// <summary>
    /// Report the converter admittance's non-passive band: the most-negative <c>Re(Y_conv+(jw))</c> and where
    /// it occurs. A grid-following unit is expected to show a negative-real band around its PLL bandwidth - the
    /// physical driver the Nyquist test then adjudicates - so this reports Constitutive with the band flagged,
    /// never Violated.
    /// </summary>
    public static LawResult AuditConverterNonPassiveBand(IConverterImpedanceModel conv, IReadOnlyList<double> fHz)
    {
        double minRe = double.PositiveInfinity;
        double atF = double.NaN;
        foreach (double f in fHz)
        {
            Complex y = conv.PositiveSequenceAdmittance(2.0 * Math.PI * f);
            if (y.Real < minRe) { minRe = y.Real; atF = f; }
        }
        if (double.IsPositiveInfinity(minRe)) minRe = 0.0;
        bool hasBand = minRe < 0.0;
        return new LawResult(PhysicsCheckRegistry.ConverterNonPassiveBandLaw, PhysicsCheckRegistry.ExperimentalLaws[PhysicsCheckRegistry.ConverterNonPassiveBandLaw],
            "a grid-following converter presents Re(Y_conv) < 0 around the PLL bandwidth",
            LawStatus.Constitutive, Math.Max(0.0, -minRe), "pu", 0.0,
            hasBand
                ? $"non-passive band: min Re(Y_conv) = {minRe:E2} pu at {atF:F2} Hz (adjudicated by the Nyquist test)"
                : $"converter is passive over the swept band (min Re(Y_conv) = {minRe:E2} pu)");
    }
}
