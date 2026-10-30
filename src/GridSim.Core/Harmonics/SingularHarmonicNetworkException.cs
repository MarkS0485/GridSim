namespace GridSim.Core.Harmonics;

/// <summary>
/// Thrown when the harmonic network admittance <c>Y(h)</c> at some order cannot be solved: it is singular, so
/// there is no unique harmonic voltage for the injected current. Physically this means part of the network
/// has no path to ground at that frequency - typically a radial feeder or island with no line charging, no
/// shunt and no modelled source impedance (synchronous machines are the main harmonic path to ground; see
/// <see cref="HarmonicModelOptions.IncludeGeneratorSources"/> and <see cref="HarmonicModelOptions.Study"/>).
///
/// <para>Fail loud (October 2026 review, R001): every harmonic solver used to substitute a ZERO voltage
/// vector for a singular solve, which the indices then reported as 0% THD and the IEEE-519 check as
/// "Compliant" - a wrong answer that looked like a clean pass. A singular order is now an error that names
/// the order, never a result.</para>
/// </summary>
public sealed class SingularHarmonicNetworkException : InvalidOperationException
{
    public SingularHarmonicNetworkException(string study, double order)
        : base($"{study}: the harmonic network admittance Y(h) is singular at order {order:R}, so no harmonic " +
               "voltage can be computed. Part of the network has no path to ground at this frequency (a radial " +
               "feeder or island with no line charging, shunts or source impedance). Model the machine source " +
               "impedance (HarmonicModelOptions.IncludeGeneratorSources, or HarmonicModelOptions.Study) or the " +
               "loads (IncludeLoadShunts) to ground it.")
    {
        Study = study;
        Order = order;
    }

    /// <summary>Which study hit the singular order (penetration, impedance scan, ...).</summary>
    public string Study { get; }

    /// <summary>The harmonic order whose Y(h) could not be solved.</summary>
    public double Order { get; }
}
