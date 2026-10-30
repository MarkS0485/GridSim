using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.Emt;

/// <summary>
/// Per-unit ↔ physical conversions the EMT engine needs. GridSim stores branch impedance as per-unit R/X/B
/// (no henries/farads), so inductance and capacitance are derived at build time: <c>L = X/ω0</c>,
/// <c>C = B/ω0</c> (per-unit, in seconds), with <c>ω0 = 2π·FrequencyHz</c>. This mirrors the
/// <c>w0 = 2π f0; l = x/w0</c> derivation in <see cref="Dynamics.Impedance.GridImpedanceAssembler"/>.
/// Physical henries/farads are only recoverable where a bus carries a non-zero base voltage
/// (<c>Zbase = kV²/MVA</c>); per-unit EMT is fully self-consistent, so a case with <c>BaseKv = 0</c> runs
/// per-unit-only (physical volts/amps simply aren't reported).
/// </summary>
public static class EmtUnits
{
    /// <summary>System angular frequency, rad/s.</summary>
    public static double Omega(GridModel model) => 2.0 * Math.PI * model.FrequencyHz;

    /// <summary>Series (R, L) of a branch in per-unit; L in seconds via <c>X/ω0</c>.</summary>
    public static (double R, double L) SeriesRl(Branch br, double omega) => (br.R, br.X / omega);

    /// <summary>R078: the series capacitance (per-unit, seconds) of a series-compensated branch with negative
    /// reactance <paramref name="xPu"/>: <c>C = 1/(|X|*w0)</c>, so that <c>-1/(w0*C) = X</c> at the system
    /// frequency. Requires X &lt; 0.</summary>
    public static double SeriesC(double xPu, double omega)
    {
        if (!(xPu < 0)) throw new ArgumentOutOfRangeException(nameof(xPu), xPu, "A series capacitance needs a negative reactance.");
        return 1.0 / (-xPu * omega);
    }

    /// <summary>Per-unit shunt capacitance from a charging susceptance, in seconds via <c>B/ω0</c>.</summary>
    public static double ShuntC(double susceptancePu, double omega) => susceptancePu / omega;

    /// <summary>Base impedance (Ω) of a bus, or 1.0 when the bus has no base voltage (per-unit-only case).</summary>
    public static double Zbase(Bus bus, double baseMva) =>
        bus.BaseKv > 0 && baseMva > 0 ? bus.BaseKv * bus.BaseKv / baseMva : 1.0;

    /// <summary>Surge impedance <c>Zc = √(X/B)</c> and travel time <c>τ = √(X·B)/ω0</c> of a line from its
    /// per-unit series reactance X and total charging susceptance B (both length-cancelling). Requires B &gt; 0
    /// - a branch with no charging data cannot be a travelling-wave line and stays a lumped pi section.</summary>
    public static (double Zc, double Tau) SurgeParams(double xPu, double bPu, double omega) =>
        (Math.Sqrt(xPu / bPu), Math.Sqrt(xPu * bPu) / omega);
}
