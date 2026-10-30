namespace GridSim.Gda.Ltds;

// Faithful, lightly-typed mirrors of the NGED LTDS Tabular Model rows. These are
// the *electrical* description of a real DNO network (East/West Midlands, South
// West, South Wales). The record fields keep their historical names (R1Ohm, B1MicroS ...), but NGED publishes
// circuit and transformer impedances as PERCENT ON 100 MVA (B1 the percent charging): N252 proved it for the
// transformers and N347 (CF4, October 2026) for the circuits, against NGED's own Table 4 Thevenin impedances
// (see GdaModelBuilder.NgedCircuitConvention). The builder converts by the dataset's convention; the raw values
// are stored here as published. Lengths in km, ratings seasonal MVA.

/// <summary>A node (bus) from <c>ltds-attachment-8-node.csv</c>  -  the bus dictionary.</summary>
public sealed record LtdsNode(
    string Licence, string Code, string Name, string BspGroup, string GspGroup, string Status);

/// <summary>
/// A circuit (line/cable/busbar/disconnector) from <c>ltds-table-1-circuit.csv</c>.
/// Only rows whose <see cref="Equipment"/> denotes a real series element carry
/// non-zero R1/X1; busbars and disconnectors provide connectivity at ~zero impedance.
/// </summary>
public sealed record LtdsCircuit(
    string Licence, string GspGroup, string BspGroup,
    string Node1, string Node2, string Status,
    double KvBase, string CircuitCode,
    double R1Ohm, double X1Ohm, double B1MicroS, double R0Ohm, double X0Ohm,
    double WinterRatingMva, double SummerRatingMva, double LengthKm, string Equipment)
{
    /// <summary>A real series branch (has impedance), as opposed to a busbar/disconnector tie.</summary>
    public bool IsSeries => (Math.Abs(R1Ohm) + Math.Abs(X1Ohm)) > GridSim.Gda.GdaBuildDefaults.TieImpedanceThresholdOhm;
}

/// <summary>A two-winding transformer from <c>ltds-table-2a</c> (R1/X1/X0 in ohm on the LV base, taps, ratings).</summary>
public sealed record LtdsTransformer2(
    string Licence, string GspGroup,
    string Node1, double Kv1, string Node2, double Kv2,
    string Status, string VectorGroup,
    double R1Ohm, double X1Ohm, double X0Ohm,
    double MinTap, double MaxTap,
    double NominalRatingMva, double EmergencyRatingMva);

/// <summary>
/// A three-winding transformer from <c>ltds-table-2b</c>. The three measured
/// short-circuit reactances (HV-LV, LV-Tertiary, HV-Tertiary) convert to a star
/// (T) equivalent in the builder.
/// </summary>
public sealed record LtdsTransformer3(
    string Licence, string GspGroup,
    string Node1, double Kv1, string Node2, double Kv2, string Node3, double Kv3,
    string Status, string VectorGroup,
    double R1Ohm, double X1HvLvOhm, double X1LvTertOhm, double X1HvTertOhm,
    double MinTap, double MaxTap,
    double NominalRatingMva, double EmergencyRatingMva);

/// <summary>A demand point from <c>ltds-table-3-demand.csv</c> (primary substation max demand + power factor).</summary>
/// <remarks>
/// <see cref="JoinOnly"/> (N194, October 2026; default false, so every existing reader builds exactly as before): a
/// row that carries NO load into the replica and only places its substation name on its bus for the generation join.
/// UKPN uses it for a grid board's demand row, which is the sum of the primary rows behind it (counting it would
/// count that load twice) but is still the substation that generation connected at that board names.
/// </remarks>
public sealed record LtdsDemand(
    string GspGroup, string BspGroup, string PrimarySubstation, string Node,
    double Kv, double MaxDemandMw, double PowerFactor, double FirmCapacityMva,
    string Status = "Energised", bool JoinOnly = false);

/// <summary>An embedded generator from <c>table-5-generation.csv</c> (agreed export capacity + fuel type).</summary>
public sealed record LtdsGeneration(
    string GspGroup, string Bsp, string PrimarySubstation,
    double Kv, double ExportCapacityMw, string FuelType, string Status);
