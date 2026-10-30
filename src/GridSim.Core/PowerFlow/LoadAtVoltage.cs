using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>
/// The single rule for "how much load does this bus actually draw at voltage V" (R065, October 2026).
/// <para>A bus with no <see cref="Bus.Zip"/>, or a ZIP whose coefficients do not vary with voltage, draws exactly
/// its nominal <see cref="Bus.Pd"/> / <see cref="Bus.Qd"/> - returned as the stored doubles, untouched, so every
/// constant-power case stays bit-for-bit on the historical numbers. A voltage-dependent ZIP load draws
/// <c>Pd * PMultiplier(V)</c> and <c>Qd * QMultiplier(V)</c>, the same polynomial the Newton mismatch and Jacobian
/// already use.</para>
/// <para>Before this helper only the Newton mismatch honoured ZIP; the reactive-limit check, the physics audit
/// and the backward/forward sweep all used the nominal load, so an exactly solved ZIP case was reported as
/// violating KCL and its generators' reactive output was mis-measured.</para>
/// </summary>
public static class LoadAtVoltage
{
    /// <summary>The (P, Q) load in MW / MVAr that <paramref name="bus"/> draws at voltage magnitude
    /// <paramref name="vmPu"/> (pu).</summary>
    public static (double PMw, double QMvar) Mw(Bus bus, double vmPu)
    {
        if (bus.Zip is not { VariesWithVoltage: true } zip) return (bus.Pd, bus.Qd);
        return (bus.Pd * zip.PMultiplier(vmPu), bus.Qd * zip.QMultiplier(vmPu));
    }

    /// <summary>The reactive load (MVAr) at voltage <paramref name="vmPu"/> for a bus with nominal reactive load
    /// <paramref name="qdMvar"/> and load model <paramref name="zip"/> (null or non-varying = constant power, the
    /// nominal value returned untouched).</summary>
    public static double QMvar(double qdMvar, ZipLoad? zip, double vmPu)
        => zip is { VariesWithVoltage: true } ? qdMvar * zip.QMultiplier(vmPu) : qdMvar;
}
