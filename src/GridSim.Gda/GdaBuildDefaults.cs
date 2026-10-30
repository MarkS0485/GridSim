namespace GridSim.Gda;

/// <summary>
/// Named defaults and guards the GDA (DNO LTDS) model build falls back on when a published table leaves a value out
/// (R199 / R200, October 2026). Each used to be a bare literal inside <see cref="GdaModelBuilder"/>,
/// <see cref="GdaModelWriter"/> or a reader; every value here is exactly the literal it replaced, so no built model
/// changes. They are shared by every DNO reader (NGED, UKPN, NPG ...) so a default cannot differ between operators by
/// accident.
/// <para>Provenance notes: these are engineering defaults for missing data, not measured values. Where a DNO publishes
/// the real figure (power factor, tap range, rating) the published figure is always used instead.</para>
/// </summary>
public static class GdaBuildDefaults
{
    /// <summary>Demand power factor assumed when the LTDS demand row's power factor is impossible (&lt;= 0 or &gt; 1).
    /// 0.98 lagging is a typical GB primary-substation figure. A BLANK power-factor cell is not "none": the NGED and UKPN
    /// readers read it as 1.0 (unity, Qd = 0), the same rule as GDA's own normaliser (<c>fillna(1.0)</c>), so the two
    /// builds agree; this default applies only to a published value that cannot be a power factor.</summary>
    public const double DemandPowerFactor = 0.98;

    /// <summary>Reactive capability of an embedded generator as a fraction of its capacity, either way
    /// (Qmin = -0.4 x cap, Qmax = +0.4 x cap): roughly a 0.93 power-factor capability.</summary>
    public const double EmbeddedGenQRatio = 0.4;

    /// <summary>OLTC tap step (pu per step) given to a grid / primary transformer: 1.25 %, the common GB step.</summary>
    public const double OltcStepPu = 0.0125;

    /// <summary>OLTC voltage deadband (pu) either side of target before a tap moves.</summary>
    public const double OltcDeadbandPu = 0.01;

    /// <summary>A transformer whose computed reactance magnitude is below this (pu) is degenerate (a missing or
    /// zero LTDS entry) ...</summary>
    public const double DegenerateTransformerXPu = 1e-6;

    /// <summary>... and is given this small reactance (pu) instead, so the admittance matrix stays finite.</summary>
    public const double MinTransformerXPu = 1e-4;

    /// <summary>A three-winding transformer's star-leg reactance that comes out zero or negative from the
    /// short-circuit triangle is floored at this fraction of |X_HL| ...</summary>
    public const double StarLegFloorFraction = 0.01;

    /// <summary>... plus this small absolute reactance (in the same units), so a zero |X_HL| still gives a finite leg.</summary>
    public const double StarLegFloorAbs = 1e-6;

    /// <summary>MVA base a percent-on-rating transformer impedance is taken on when the transformer has no rating.</summary>
    public const double TransformerRatingFallbackMva = 100.0;

    /// <summary>GSP reference voltage (kV) assumed when a GSP-group name carries no recognisable system voltage.</summary>
    public const double DefaultGspKv = 132.0;

    /// <summary>System frequency (Hz) of a built GB distribution model.</summary>
    public const double SystemFrequencyHz = 50.0;

    /// <summary>A circuit with |R1| + |X1| at or below this (ohm) is a busbar / disconnector tie, not a series branch.</summary>
    public const double TieImpedanceThresholdOhm = 1e-9;

    // ---- grid infeed written by GdaModelWriter (R200) ----

    /// <summary>Smallest total demand (MW) the grid-infeed sizing is based on, so a tiny extract still gets a stiff
    /// source.</summary>
    public const double GridInfeedFloorMw = 1000.0;

    /// <summary>Each grid infeed's capacity and +/- reactive limit = this factor x max(floor, demand of the island the
    /// infeed feeds). It is a deliberately oversized slack (it must never bind). R200, ruled 2026-10-02 (Q10): sized
    /// per island (<c>GdaModelWriter.IslandDemandBySlack</c>), no longer on the whole-network demand.</summary>
    public const double GridInfeedHeadroomFactor = 5.0;
}
