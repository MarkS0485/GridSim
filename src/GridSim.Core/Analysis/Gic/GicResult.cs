namespace GridSim.Core.Analysis.Gic;

/// <summary>A substation's GIC: the current from its neutral into the ground (all three phases, amperes, positive into
/// the ground) and the neutral's voltage to remote earth (volts).</summary>
public sealed record GicSubstationResult(string Name, double NeutralA, double NeutralV);

/// <summary>
/// A transformer's GIC, per phase. <see cref="IhA"/> is the current entering the HV (H) terminal and <see cref="IxA"/> the
/// current entering the LV (X) terminal, both towards the neutral; for an autotransformer the series winding carries
/// <see cref="IhA"/> and the common winding <see cref="IhA"/> + <see cref="IxA"/>. <see cref="NeutralA"/> is the current
/// from the transformer's neutral to the substation neutral, all three phases (3 (I_H + I_X)). <see cref="Ratio"/> is
/// V_H / V_X from the bus base voltages. <see cref="EffectiveA"/> is |I_H + I_X / a| (NERC Transformer Thermal Impact
/// Assessment white paper, eq. 1: I_dc,eq = I_H + (I_N/3 - I_H) V_X/V_H, as a magnitude). <see cref="QMvar"/> is
/// k V_pu I_eff, or null when the file gives no k.
/// </summary>
public sealed record GicTransformerResult(string Branch, GicTransformerType Type, int HvBus, int LvBus, double Ratio,
    double IhA, double IxA, double NeutralA, double EffectiveA, double VoltagePu, double? QMvar);

/// <summary>A line's GIC: its path vector (km), its source voltage (V), its DC resistance per phase (ohms) and the
/// current from its from bus to its to bus, all three phases (A).</summary>
public sealed record GicLineResult(string Branch, int FromBus, int ToBus, double NorthKm, double EastKm, double SourceV,
    double OhmsPerPhase, double CurrentA);

/// <summary>A GIC solve for one uniform field (V/km).</summary>
public sealed record GicResult(double FieldNorthVPerKm, double FieldEastVPerKm, IReadOnlyList<GicSubstationResult> Substations,
    IReadOnlyList<GicTransformerResult> Transformers, IReadOnlyList<GicLineResult> Lines);

/// <summary>One transformer's largest effective GIC over a direction sweep at 1 V/km, and the first direction (degrees
/// from north, towards east) that reaches it.</summary>
public sealed record GicSweepEntry(string Branch, double MaxEffectiveA, double DirectionDeg);
