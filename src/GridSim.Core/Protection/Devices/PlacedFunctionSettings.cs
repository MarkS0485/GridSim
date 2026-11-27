using System.Collections.Generic;

namespace GridSim.Core.Protection.Devices;

/// <summary>The shape of a distance zone: a mho circle through the origin, or a quadrilateral.</summary>
public enum DistanceCharacteristic { Mho, Quad }

/// <summary>Which measuring loops a distance element uses: the three phase-to-phase loops, the three phase-to-earth
/// loops, or both.</summary>
public enum DistanceLoops { Phase, Earth, Both }

/// <summary>One distance zone, in CT/VT secondary ohms and degrees. <see cref="ReachOhms"/> and <see cref="AngleDeg"/>
/// give the zone's reach Zr at its characteristic angle: the mho diameter, or for a quadrilateral the reactance line
/// X = |Zr| sin(angle). <see cref="ResistiveReachOhms"/> and <see cref="LeftReachOhms"/> are the quadrilateral's right
/// and left resistive blinders, measured along the real axis from the line through the origin at the zone angle.</summary>
public sealed record DistanceZone(double ReachOhms, double AngleDeg, double TimeS, double ResistiveReachOhms, double LeftReachOhms)
{
    /// <summary>RC5 WP17: a reverse zone looks behind the relay. Its characteristic is the forward one mirrored through the
    /// origin (Z is tested as -Z). Reverse zones follow the forward zones in the file. False by default.</summary>
    public bool Reverse { get; init; }
}

/// <summary>A placed distance (ANSI 21) element. <see cref="K0"/> is the residual compensation factor
/// k0 = (Z0L - Z1L) / (3 Z1L) as magnitude and angle; null takes it from the protected line's own Z0 and Z1.
/// <see cref="DirectionalLowDeg"/> and <see cref="DirectionalHighDeg"/> bound the argument of Z for a quadrilateral
/// zone (the mho circle is directional by itself).</summary>
public sealed record DistanceSetting(
    DistanceCharacteristic Characteristic,
    DistanceLoops Loops,
    (double Magnitude, double AngleDeg)? K0,
    IReadOnlyList<DistanceZone> Zones,
    double DirectionalLowDeg,
    double DirectionalHighDeg)
{
    /// <summary>RC5 WP17: a communication-aided scheme with the relay at the other end of the line (null: none).</summary>
    public SchemeSetting? Scheme { get; init; }
    /// <summary>RC5 WP17: power-swing blocking on an RMS trace (null: none).</summary>
    public PowerSwingSetting? PowerSwing { get; init; }
    /// <summary>RC5 WP17: the load blinder for the phase loops (null: none).</summary>
    public LoadEncroachmentSetting? LoadEncroachment { get; init; }
    /// <summary>RC5 WP17: switch-onto-fault (null: none).</summary>
    public SotfSetting? Sotf { get; init; }
    /// <summary>RC5 WP17: zero-sequence mutual compensation from a parallel line (null: none).</summary>
    public MutualSetting? Mutual { get; init; }
    /// <summary>RC5 WP17: true places the element on a two-winding transformer (opt-in; without it a 21 with zones on a
    /// transformer is refused, as before).</summary>
    public bool OnTransformer { get; init; }
}

/// <summary>The communication-aided schemes (Horowitz and Phadke, Power System Relaying, 4th ed., ch. 6): direct and
/// permissive underreach transfer trip, permissive overreach transfer trip, and directional comparison blocking.</summary>
public enum SchemeType { Dutt, Putt, Pott, Dcb }

/// <summary>A scheme with the relay <see cref="RemoteId"/> at the other end of the same line. <see cref="ChannelS"/> is the
/// signal's channel time; <see cref="CoordinationS"/> is the DCB carrier coordination delay.</summary>
public sealed record SchemeSetting(SchemeType Type, string RemoteId, double ChannelS, double CoordinationS);

/// <summary>Power-swing blocking: two concentric rectangles centred on the origin, |R| &lt;= R and |X| &lt;= X, the outer
/// larger than the inner on both axes. A trace that stays between them for at least <see cref="DeltaTS"/> is a swing; the
/// zones in <see cref="BlockZones"/> are then blocked until the impedance leaves the outer rectangle or
/// <see cref="UnblockS"/> has passed.</summary>
public sealed record PowerSwingSetting(double OuterR, double InnerR, double OuterX, double InnerX, double DeltaTS, IReadOnlyList<int> BlockZones, double UnblockS);

/// <summary>The load blinder: a phase loop whose impedance has |Z| &gt;= <see cref="MinLoadOhms"/> and lies within
/// <see cref="FwdAngleDeg"/> of 0 degrees (forward load) or <see cref="RevAngleDeg"/> of 180 degrees (reverse load) is
/// blocked. Earth loops are not affected.</summary>
public sealed record LoadEncroachmentSetting(double FwdAngleDeg, double RevAngleDeg, double MinLoadOhms);

/// <summary>Switch-onto-fault modes: trip when zone 2 (or zone 1) picks up, or on any phase current above the CT primary
/// rating whatever the direction.</summary>
public enum SotfMode { Z2, Nondirectional }

/// <summary>Switch-onto-fault: armed for <see cref="WindowS"/> after the line is closed.</summary>
public sealed record SotfSetting(double WindowS, SotfMode Mode);

/// <summary>Zero-sequence mutual compensation: the earth loop current becomes Ip + k0 3I0 + k0m 3I0m, where I0m is the
/// zero-sequence current into <see cref="ParallelBranch"/> at the relay's bus. <see cref="K0m"/> is Z0m / (3 Z1L) as
/// magnitude and angle, with Z0m signed for the parallel line taken from the relay's bus; null takes Z0m from the case's
/// zero-sequence mutual data.</summary>
public sealed record MutualSetting(int ParallelBranch, (double Magnitude, double AngleDeg)? K0m);

/// <summary>How a directional (ANSI 67) element finds the direction: the 90-degree (quadrature) connection for a phase
/// element, the zero-sequence voltage for 67N, the negative-sequence voltage for 67Q.</summary>
public enum Polarisation { Quadrature, ZeroSequence, NegativeSequence }

/// <summary>A placed directional element. The fault is forward when cos(arg Iop - arg Vpol - RCA) &gt; 0.
/// <see cref="MinPolarisingVPu"/> is the smallest polarising voltage (pu) the element can use.</summary>
public sealed record DirectionalSetting(Polarisation Polarisation, double CharacteristicAngleDeg, bool Forward, double MinPolarisingVPu);

/// <summary>A biased line differential (ANSI 87L) element, CT secondary amps. Dual slope: operate when
/// Id &gt; k1 Ib + Is1 for Ib &lt;= Is2, and Id &gt; k2 Ib - (k2 - k1) Is2 + Is1 above. The remote end's current is read
/// through <see cref="RemoteCtPrimaryA"/> / <see cref="RemoteCtSecondaryA"/> (by default the device's own CT).</summary>
public sealed record DifferentialSetting(double Is1A, double K1, double Is2A, double K2, double RemoteCtPrimaryA, double RemoteCtSecondaryA)
{
    public double RemoteCtRatio => RemoteCtPrimaryA / RemoteCtSecondaryA;
}

/// <summary>RC5 WP18: where a differential CT sits: a branch (by index) and the end it measures at. The CT measures the
/// current flowing into that branch at that end.</summary>
public sealed record CtLocation(int BranchIndex, int From, int To, string End)
{
    public bool AtFromEnd => End == "from";
    /// <summary>The bus the CT is at.</summary>
    public int Bus => AtFromEnd ? From : To;
}

/// <summary>
/// RC5 WP18: a biased transformer differential (ANSI 87T) on the device's own two-winding transformer. The settings are
/// in pu of the transformer rating (<see cref="RatedMva"/>): <see cref="Is1"/> and <see cref="Is2"/> in pu, <see cref="K1"/>
/// and <see cref="K2"/> are slopes (the dual-slope threshold of the 87L). <see cref="VectorGroup"/> names the HV winding,
/// the LV winding and the clock number (YNd1, Yd11, Dyn1, Dyn11, YNyn0, Yy0, Dd0). Each side's CT sits on the
/// transformer's own end unless <see cref="HvCt"/> or <see cref="LvCt"/> places it on another branch.
/// </summary>
public sealed record TransformerDifferentialSetting(string VectorGroup, double RatedMva, double Is1, double K1, double Is2, double K2)
{
    /// <summary>The HV CT on another branch (null: on the transformer's HV end).</summary>
    public CtLocation? HvCt { get; init; }
    /// <summary>The LV CT on another branch (null: on the transformer's LV end).</summary>
    public CtLocation? LvCt { get; init; }
}

/// <summary>RC5 WP18: one CT of a busbar differential: where it sits and its ratio.</summary>
public sealed record BusCt(CtLocation At, double PrimaryA, double SecondaryA)
{
    public double Ratio => PrimaryA / SecondaryA;
}

/// <summary>How a busbar differential forms its bias: the sum or the largest of the feeder current magnitudes.</summary>
public enum BusBias { Sum, Max }

/// <summary>
/// RC5 WP18: a low-impedance busbar differential (ANSI 87B). <see cref="Zone"/> lists the bus IDs of the protected zone;
/// <see cref="Cts"/> are the CTs on the branches that leave it. Currents are in secondary amps of the reference CT (the
/// one with the largest primary). Operate when Id &gt; K1 Ib + Is1 (single slope), or by the dual-slope 87L threshold when
/// <see cref="Is2A"/> and <see cref="K2"/> are given. <see cref="CheckZone"/>, when given, lists the bus IDs of the whole
/// station (a superset of the zone); a trip then needs both zones to operate.
/// </summary>
public sealed record BusDifferentialSetting(IReadOnlyList<int> Zone, IReadOnlyList<BusCt> Cts, double Is1A, double K1, BusBias Bias)
{
    public double? Is2A { get; init; }
    public double? K2 { get; init; }
    public IReadOnlyList<int>? CheckZone { get; init; }
}
