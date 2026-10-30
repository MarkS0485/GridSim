namespace GridSim.Core.Dynamics;

/// <summary>
/// Grid-forming frequency-FORMATION mode. <see cref="None"/> is today's floored centre-of-inertia path
/// and is byte-identical to the pre-forming behaviour.
///
/// <para>Under <see cref="Vsm"/> / <see cref="Droop"/> the grid frequency is BUILT by a voltage-source
/// (grid-forming) converter whose internal angle the network follows, rather than measured off a
/// floored synchronous COI. The forming denominator is <c>E_real + E_virtual</c> with NO
/// <c>Math.Max(1,.)</c> floor: a genuine (droop-damped) trajectory even at zero real inertia, or a
/// fail-loud error when nothing forms the frequency (E_real == 0 AND E_virtual == 0).</para>
///
/// <para><see cref="Droop"/> sets the virtual inertia Ev = 0, so a true power STEP into an islanded
/// (E_real == 0) grid is ill-posed - that specific case fails loud via
/// <see cref="Forming.FormingKernel.EffectiveFormingInertiaMwS"/>. With synchronous mass present
/// (E_real &gt; 0) Droop is perfectly well posed (finite, A-stable). For islanded steps use
/// <see cref="Vsm"/> (Ev &gt; 0).</para>
/// </summary>
public enum FormingMode
{
    /// <summary>Default. The existing Math.Max(1,E) explicit-Euler COI swing runs verbatim.</summary>
    None,

    /// <summary>Second-order virtual synchronous machine: virtual inertia Ev &gt; 0 plus damping Dv.</summary>
    Vsm,

    /// <summary>First-order droop: Ev = 0, frequency bounded by Dv alone (islanded steps need synchronous mass or Vsm).
    /// A pure P-f droop former (the power loop of REGFM_A1) carries no virtual inertia, so a config with
    /// <see cref="DynamicsConfig.VirtualInertiaMwS"/> other than 0 under Droop is refused by name (N311,
    /// <see cref="SwingKernel.RefuseDroopWithVirtualInertia"/>); before N311 it was silently run as a VSM.</summary>
    Droop,
}
