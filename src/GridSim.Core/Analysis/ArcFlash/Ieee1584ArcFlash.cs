using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.Protection.ShortCircuit;

namespace GridSim.Core.Analysis.ArcFlash;

/// <summary>IEEE 1584-2018 electrode configurations (clause 4.2 / Table 1).</summary>
public enum ElectrodeConfiguration
{
    /// <summary>Vertical conductors in a box.</summary>
    VCB,
    /// <summary>Vertical conductors terminated in an insulating barrier, in a box.</summary>
    VCBB,
    /// <summary>Horizontal conductors in a box.</summary>
    HCB,
    /// <summary>Vertical conductors in open air.</summary>
    VOA,
    /// <summary>Horizontal conductors in open air.</summary>
    HOA,
}

/// <summary>Enclosure inside dimensions (mm). Required for VCB, VCBB and HCB; refused for VOA and HOA.</summary>
public sealed record ArcFlashEnclosure(double HeightMm, double WidthMm, double DepthMm);

/// <summary>Evidence of a bolted fault current read from the incumbent IEC 60909 solver (outputs only).</summary>
/// <param name="BusId">Fault bus.</param>
/// <param name="Objective">IEC 60909 objective (Maximum or Minimum).</param>
/// <param name="IkppKa">Initial symmetrical short-circuit current Ik'' (kA), used as the bolted fault current.</param>
/// <param name="BusBaseKv">The bus's base voltage (kV).</param>
/// <param name="RoverX">Fault-loop R/X reported by the solver.</param>
/// <param name="PeakIpKa">Peak current ip (kA), reported for reference only.</param>
/// <param name="Engine">The solver entry point.</param>
public sealed record Iec60909FaultEvidence(int BusId, string Objective, double IkppKa, double BusBaseKv, double RoverX, double PeakIpKa, string Engine);

/// <summary>The three-phase bolted fault current at the equipment.</summary>
/// <param name="Ka">Bolted fault current Ibf (kA, rms symmetrical).</param>
/// <param name="Source">Where it came from, in plain words.</param>
/// <param name="Iec60909">The IEC 60909 evidence when it was read from the incumbent solver.</param>
public sealed record BoltedFaultInput(double Ka, string Source, Iec60909FaultEvidence? Iec60909 = null);

/// <summary>Arc duration for each arcing-current scenario. IEEE 1584-2018 requires the clearing time to be read from the
/// protective device's time-current characteristic at each arcing current; this study does not evaluate a device curve,
/// so both times are declared inputs with their source.</summary>
/// <param name="FullArcingCurrentMs">Clearing time at the full arcing current (ms).</param>
/// <param name="ReducedArcingCurrentMs">Clearing time at the reduced arcing current (ms).</param>
/// <param name="Source">Where the times came from (device, curve, setting), in plain words.</param>
public sealed record ClearingTimeInput(double FullArcingCurrentMs, double ReducedArcingCurrentMs, string Source);

/// <summary>An arc-flash study request for one piece of equipment.</summary>
/// <param name="Label">Equipment label.</param>
/// <param name="SystemVoltageKv">Open-circuit (nominal) three-phase line-to-line voltage Voc (kV).</param>
/// <param name="Configuration">Electrode configuration.</param>
/// <param name="GapMm">Gap between conductors G (mm).</param>
/// <param name="WorkingDistanceMm">Working distance D (mm).</param>
/// <param name="Enclosure">Enclosure (box configurations only).</param>
/// <param name="BoltedFault">Bolted fault current and its source.</param>
/// <param name="ClearingTime">Clearing times and their source.</param>
/// <param name="FrequencyHz">System frequency (50 or 60).</param>
/// <param name="Phases">Number of phases (the model covers 3 only).</param>
public sealed record ArcFlashRequest(
    string Label, double SystemVoltageKv, ElectrodeConfiguration Configuration, double GapMm, double WorkingDistanceMm,
    ArcFlashEnclosure? Enclosure, BoltedFaultInput BoltedFault, ClearingTimeInput ClearingTime, double FrequencyHz, int Phases = 3);

/// <summary>Enclosure size correction (equations 11 to 15, Tables 6 and 7).</summary>
/// <param name="EnclosureType"><c>Typical</c>, <c>Shallow</c> or <c>Open air</c>.</param>
/// <param name="Width1In">Equivalent width (in); null for open air.</param>
/// <param name="Height1In">Equivalent height (in); null for open air.</param>
/// <param name="EesIn">Equivalent enclosure size (in); null for open air.</param>
/// <param name="Cf">Enclosure size correction factor CF (1 for open air).</param>
public sealed record ArcFlashEnclosureCorrection(string EnclosureType, double? Width1In, double? Height1In, double? EesIn, double Cf);

/// <summary>One arcing-current scenario.</summary>
/// <param name="Name"><c>full</c> (the arcing current of equations 1 and 16-18 or 25) or <c>reduced</c> (times
/// <c>1 - 0.5 VarCf</c>, equation 2).</param>
/// <param name="ArcingCurrentKa">Final arcing current (kA).</param>
/// <param name="IntermediateArcingCurrentKa">The 600 V, 2700 V and 14300 V intermediate currents (kA), reduced in the
/// reduced scenario; below 0.6 kV only the 600 V entry is given and it is always the full value (equations 6 and 10 use
/// the full 600 V current in both scenarios).</param>
/// <param name="ClearingTimeMs">Arc duration used (ms).</param>
/// <param name="IntermediateIncidentEnergyJPerCm2">The 600, 2700 and 14300 V intermediate energies; null entries below 0.6 kV.</param>
/// <param name="IncidentEnergyJPerCm2">Incident energy at the working distance (J/cm2).</param>
/// <param name="IncidentEnergyCalPerCm2">The same in cal/cm2 (1 cal = 4.184 J).</param>
/// <param name="IntermediateBoundaryMm">The 600, 2700 and 14300 V intermediate boundaries; null entries below 0.6 kV.</param>
/// <param name="ArcFlashBoundaryMm">Distance at which the incident energy is 1.2 cal/cm2 (5.0208 J/cm2), mm.</param>
public sealed record ArcFlashScenario(string Name, double ArcingCurrentKa, IReadOnlyList<double?> IntermediateArcingCurrentKa, double ClearingTimeMs,
    IReadOnlyList<double?> IntermediateIncidentEnergyJPerCm2, double IncidentEnergyJPerCm2, double IncidentEnergyCalPerCm2,
    IReadOnlyList<double?> IntermediateBoundaryMm, double ArcFlashBoundaryMm);

/// <summary>The study result. When <see cref="Status"/> is <c>OUT OF SCOPE</c> every calculated field is null and
/// <see cref="OutOfScope"/> lists every envelope breach.</summary>
public sealed record ArcFlashResult(
    string Edition, string Label, string Status, IReadOnlyList<string> OutOfScope, string? VoltageBand, ArcFlashRequest Inputs,
    double? VarCf, ArcFlashEnclosureCorrection? Enclosure, ArcFlashScenario? Full, ArcFlashScenario? Reduced, string? Governing,
    double? IncidentEnergyJPerCm2, double? IncidentEnergyCalPerCm2, double? ArcFlashBoundaryMm, double? GoverningClearingTimeMs,
    IReadOnlyList<string> Envelope, IReadOnlyList<string> Limitations);

/// <summary>
/// RC3 P8 (industrial arc flash): the IEEE 1584-2018 empirical model, three-phase AC, 208 V to 15 kV.
/// <para><b>Frozen edition.</b> IEEE Std 1584-2018, "IEEE Guide for Performing Arc-Flash Hazard Calculations": equations 1
/// (intermediate arcing current), 2 (reduced arcing current and VarCf), 3 to 6 (intermediate incident energy), 7 to 10
/// (intermediate arc-flash boundary), 11 to 15 (enclosure size correction), 16 to 24 (interpolation) and 25 (final
/// arcing current below 600 V), with the coefficients of Tables 1, 2, 3, 4, 5 and 7 and the Table 6 rules. The
/// coefficient values are as transcribed in the MIT-licensed <c>arcflash</c> library (Li-aung Yip, 2022,
/// github.com/LiaungYip/arcflash, <c>ieee_1584/tables.py</c>), which states they were copied from the standard and
/// which verifies itself against the IEEE 1584 calculator spreadsheet. They were NOT checked against a copy of the
/// standard on this machine.</para>
/// <para>IEC 60909 fault duty is independent: this class only reads <see cref="Iec60909.ThreePhase"/> outputs
/// (<see cref="FromIec60909"/>), and nothing here feeds back into fault, protection or equipment checks.</para>
/// </summary>
public static class Ieee1584ArcFlash
{
    /// <summary>The frozen edition.</summary>
    public const string Edition = "IEEE Std 1584-2018 (equations 1-25, Tables 1-7)";

    /// <summary>1.2 cal/cm2 in J/cm2: the arc-flash boundary energy.</summary>
    public const double BoundaryEnergyJPerCm2 = 5.0208;

    /// <summary>Joules per calorie.</summary>
    public const double JoulesPerCalorie = 4.184;

    /// <summary>The inch per mm factor printed in Table 6 (used as printed, not 1/25.4).</summary>
    public const double Table6InchPerMm = 0.03937;

    /// <summary>The model envelope, as applied (IEEE 1584-2018 clause 4.2, range of model).</summary>
    public static readonly IReadOnlyList<string> EnvelopeText =
    [
        "Three-phase AC only, 50 or 60 Hz.",
        "System voltage Voc 0.208 kV to 15 kV line to line.",
        "Bolted fault current 0.5 kA to 106 kA for Voc <= 0.6 kV; 0.2 kA to 65 kA for Voc > 0.6 kV.",
        "Gap 6.35 mm to 76.2 mm for Voc <= 0.6 kV; 19.05 mm to 254 mm for Voc > 0.6 kV.",
        "Working distance at least 305 mm.",
        "Box configurations (VCB, VCBB, HCB): enclosure height, width and depth positive, width at least 4 times the gap; " +
        "a height or width above 1244.6 mm is taken at 1244.6 mm (Table 6). Open-air configurations (VOA, HOA) take no enclosure.",
        "Clearing times positive and finite (no cap applied).",
    ];

    /// <summary>Stated limitations of this implementation.</summary>
    public static readonly IReadOnlyList<string> LimitationText =
    [
        "Clearing times are declared inputs: no protective-device time-current curve is evaluated, so the study cannot show the time at each arcing current is consistent with a device.",
        "Single arc duration per scenario: no multi-step (changing contribution) energy accumulation.",
        "Single-phase, DC and above-15 kV arc flash are outside IEEE 1584-2018 and are refused.",
        "Coefficients transcribed from a third-party MIT-licensed library, not checked against a copy of IEEE 1584-2018 on this machine.",
    ];

    private readonly record struct ArcCoefficients(double K1, double K2, double K3, double K4, double K5, double K6, double K7, double K8, double K9, double K10);
    private readonly record struct EnergyCoefficients(double K1, double K2, double K3, double K4, double K5, double K6, double K7,
        double K8, double K9, double K10, double K11, double K12, double K13);

    // Table 1: equation 1 coefficients, keyed by configuration, at 600, 2700 and 14300 V.
    private static ArcCoefficients Table1(ElectrodeConfiguration ec, int level) => (ec, level) switch
    {
        (ElectrodeConfiguration.VCB, 0) => new(-0.04287, 1.035, -0.083, 0, 0, -4.783E-09, 1.962E-06, -0.000229, 0.003141, 1.092),
        (ElectrodeConfiguration.VCB, 1) => new(0.0065, 1.001, -0.024, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729),
        (ElectrodeConfiguration.VCB, 2) => new(0.005795, 1.015, -0.011, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729),
        (ElectrodeConfiguration.VCBB, 0) => new(-0.017432, 0.98, -0.05, 0, 0, -5.767E-09, 2.524E-06, -0.00034, 0.01187, 1.013),
        (ElectrodeConfiguration.VCBB, 1) => new(0.002823, 0.995, -0.0125, 0, -9.204E-11, 2.901E-08, -3.262E-06, 0.0001569, -0.004003, 0.9825),
        (ElectrodeConfiguration.VCBB, 2) => new(0.014827, 1.01, -0.01, 0, -9.204E-11, 2.901E-08, -3.262E-06, 0.0001569, -0.004003, 0.9825),
        (ElectrodeConfiguration.HCB, 0) => new(0.054922, 0.988, -0.11, 0, 0, -5.382E-09, 2.316E-06, -0.000302, 0.0091, 0.9725),
        (ElectrodeConfiguration.HCB, 1) => new(0.001011, 1.003, -0.0249, 0, 0, 4.859E-10, -1.814E-07, -9.128E-06, -0.0007, 0.9881),
        (ElectrodeConfiguration.HCB, 2) => new(0.008693, 0.999, -0.02, 0, -5.043E-11, 2.233E-08, -3.046E-06, 0.000116, -0.001145, 0.9839),
        (ElectrodeConfiguration.VOA, 0) => new(0.043785, 1.04, -0.18, 0, 0, -4.783E-09, 1.962E-06, -0.000229, 0.003141, 1.092),
        (ElectrodeConfiguration.VOA, 1) => new(-0.02395, 1.006, -0.0188, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729),
        (ElectrodeConfiguration.VOA, 2) => new(0.005371, 1.0102, -0.029, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729),
        (ElectrodeConfiguration.HOA, 0) => new(0.111147, 1.008, -0.24, 0, 0, -3.895E-09, 1.641E-06, -0.000197, 0.002615, 1.1),
        (ElectrodeConfiguration.HOA, 1) => new(0.000435, 1.006, -0.038, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981),
        (ElectrodeConfiguration.HOA, 2) => new(0.000904, 0.999, -0.02, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981),
        _ => throw new ArgumentOutOfRangeException(nameof(ec)),
    };

    // Table 2: VarCf polynomial coefficients k1..k7 (Voc in kV, powers 6..0).
    private static double[] Table2(ElectrodeConfiguration ec) => ec switch
    {
        ElectrodeConfiguration.VCB => [0, -0.0000014269, 0.000083137, -0.0019382, 0.022366, -0.12645, 0.30226],
        ElectrodeConfiguration.VCBB => [1.138e-06, -6.0287e-05, 0.0012758, -0.013778, 0.080217, -0.24066, 0.33524],
        ElectrodeConfiguration.HCB => [0, -3.097e-06, 0.00016405, -0.0033609, 0.033308, -0.16182, 0.34627],
        ElectrodeConfiguration.VOA => [9.5606E-07, -5.1543E-05, 0.0011161, -0.01242, 0.075125, -0.23584, 0.33696],
        ElectrodeConfiguration.HOA => [0, -3.1555e-06, 0.0001682, -0.0034607, 0.034124, -0.1599, 0.34629],
        _ => throw new ArgumentOutOfRangeException(nameof(ec)),
    };

    // Tables 3, 4, 5: equations 3 to 10 coefficients at 600, 2700 and 14300 V.
    private static EnergyCoefficients EnergyTable(ElectrodeConfiguration ec, int level) => (ec, level) switch
    {
        (ElectrodeConfiguration.VCB, 0) => new(0.753364, 0.566, 1.752636, 0, 0, -4.783E-09, 0.000001962, -0.000229, 0.003141, 1.092, 0, -1.598, 0.957),
        (ElectrodeConfiguration.VCBB, 0) => new(3.068459, 0.26, -0.098107, 0, 0, -5.767E-09, 0.000002524, -0.00034, 0.01187, 1.013, -0.06, -1.809, 1.19),
        (ElectrodeConfiguration.HCB, 0) => new(4.073745, 0.344, -0.370259, 0, 0, -5.382E-09, 0.000002316, -0.000302, 0.0091, 0.9725, 0, -2.03, 1.036),
        (ElectrodeConfiguration.VOA, 0) => new(0.679294, 0.746, 1.222636, 0, 0, -4.783E-09, 0.000001962, -0.000229, 0.003141, 1.092, 0, -1.598, 0.997),
        (ElectrodeConfiguration.HOA, 0) => new(3.470417, 0.465, -0.261863, 0, 0, -3.895E-09, 0.000001641, -0.000197, 0.002615, 1.1, 0, -1.99, 1.04),
        (ElectrodeConfiguration.VCB, 1) => new(2.40021, 0.165, 0.354202, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729, 0, -1.569, 0.9778),
        (ElectrodeConfiguration.VCBB, 1) => new(3.870592, 0.185, -0.736618, 0, -9.204E-11, 2.901E-08, -3.262E-06, 0.0001569, -0.004003, 0.9825, 0, -1.742, 1.09),
        (ElectrodeConfiguration.HCB, 1) => new(3.486391, 0.177, -0.193101, 0, 0, 4.859E-10, -1.814E-07, -9.128E-06, -0.0007, 0.9881, 0.027, -1.723, 1.055),
        (ElectrodeConfiguration.VOA, 1) => new(3.880724, 0.105, -1.906033, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729, 0, -1.515, 1.115),
        (ElectrodeConfiguration.HOA, 1) => new(3.616266, 0.149, -0.761561, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981, 0, -1.639, 1.078),
        (ElectrodeConfiguration.VCB, 2) => new(3.825917, 0.11, -0.999749, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729, 0, -1.568, 0.99),
        (ElectrodeConfiguration.VCBB, 2) => new(3.644309, 0.215, -0.585522, 0, -9.204E-11, 2.901E-08, -3.262E-06, 0.0001569, -0.004003, 0.9825, 0, -1.677, 1.06),
        (ElectrodeConfiguration.HCB, 2) => new(3.044516, 0.125, 0.245106, 0, -5.043E-11, 2.233E-08, -3.046E-06, 0.000116, -0.001145, 0.9839, 0, -1.655, 1.084),
        (ElectrodeConfiguration.VOA, 2) => new(3.405454, 0.12, -0.93245, -1.557E-12, 4.556E-10, -4.186E-08, 8.346E-07, 5.482E-05, -0.003191, 0.9729, 0, -1.534, 0.979),
        (ElectrodeConfiguration.HOA, 2) => new(2.04049, 0.177, 1.005092, 0, 0, 7.859E-10, -1.914E-07, -9.128E-06, -0.0007, 0.9981, -0.05, -1.633, 1.151),
        _ => throw new ArgumentOutOfRangeException(nameof(ec)),
    };

    // Table 7: enclosure correction b1, b2, b3.
    private static (double B1, double B2, double B3) Table7(bool shallow, ElectrodeConfiguration ec) => (shallow, ec) switch
    {
        (false, ElectrodeConfiguration.VCB) => (-0.000302, 0.03441, 0.4325),
        (false, ElectrodeConfiguration.VCBB) => (-0.0002976, 0.032, 0.479),
        (false, ElectrodeConfiguration.HCB) => (-0.0001923, 0.01935, 0.6899),
        (true, ElectrodeConfiguration.VCB) => (0.002222, -0.02556, 0.6222),
        (true, ElectrodeConfiguration.VCBB) => (-0.002778, 0.1194, -0.2778),
        (true, ElectrodeConfiguration.HCB) => (-0.0005556, 0.03722, 0.4778),
        _ => throw new ArgumentOutOfRangeException(nameof(ec)),
    };

    private static readonly double[] LevelKv = [0.6, 2.7, 14.3];

    private static bool IsBox(ElectrodeConfiguration ec) => ec is ElectrodeConfiguration.VCB or ElectrodeConfiguration.VCBB or ElectrodeConfiguration.HCB;

    /// <summary>Every envelope breach of <paramref name="r"/> (empty when it is inside the model's range).</summary>
    public static IReadOnlyList<string> EnvelopeBreaches(ArcFlashRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var bad = new List<string>();
        bool Finite(string name, double v) { if (double.IsFinite(v)) return true; bad.Add($"{name} is not a finite number"); return false; }
        if (r.Phases != 3) bad.Add($"phases = {r.Phases}: IEEE 1584-2018 covers three-phase AC only");
        if (Finite("frequencyHz", r.FrequencyHz) && r.FrequencyHz is not (50.0 or 60.0)) bad.Add($"frequency {r.FrequencyHz:R} Hz: only 50 or 60 Hz");
        bool lv = r.SystemVoltageKv <= 0.6;
        if (Finite("systemVoltageKv", r.SystemVoltageKv) && (r.SystemVoltageKv < 0.208 || r.SystemVoltageKv > 15.0))
            bad.Add($"system voltage {r.SystemVoltageKv:R} kV is outside 0.208 kV to 15 kV");
        if (Finite("boltedFault.ka", r.BoltedFault.Ka))
        {
            (double lo, double hi) = lv ? (0.5, 106.0) : (0.2, 65.0);
            if (r.BoltedFault.Ka < lo || r.BoltedFault.Ka > hi)
                bad.Add($"bolted fault current {r.BoltedFault.Ka:R} kA is outside {lo:R} kA to {hi:R} kA for {(lv ? "Voc <= 0.6 kV" : "Voc > 0.6 kV")}");
        }
        if (Finite("gapMm", r.GapMm))
        {
            (double lo, double hi) = lv ? (6.35, 76.2) : (19.05, 254.0);
            if (r.GapMm < lo || r.GapMm > hi)
                bad.Add($"gap {r.GapMm:R} mm is outside {lo:R} mm to {hi:R} mm for {(lv ? "Voc <= 0.6 kV" : "Voc > 0.6 kV")}");
        }
        if (Finite("workingDistanceMm", r.WorkingDistanceMm) && r.WorkingDistanceMm < 305.0)
            bad.Add($"working distance {r.WorkingDistanceMm:R} mm is below 305 mm");
        if (IsBox(r.Configuration))
        {
            if (r.Enclosure is null) bad.Add($"{r.Configuration} is a box configuration and needs the enclosure dimensions");
            else
            {
                ArcFlashEnclosure e = r.Enclosure;
                foreach ((string n, double v) in new[] { ("heightMm", e.HeightMm), ("widthMm", e.WidthMm), ("depthMm", e.DepthMm) })
                    if (!(v > 0) || !double.IsFinite(v)) bad.Add($"enclosure.{n} must be a positive finite number");
                if (double.IsFinite(e.WidthMm) && double.IsFinite(r.GapMm) && e.WidthMm < 4.0 * r.GapMm)
                    bad.Add($"enclosure width {e.WidthMm:R} mm is less than 4 times the gap ({4.0 * r.GapMm:R} mm)");
            }
        }
        else if (r.Enclosure is not null) bad.Add($"{r.Configuration} is an open-air configuration and takes no enclosure");
        foreach ((string n, double v) in new[] { ("clearingTime.fullArcingCurrentMs", r.ClearingTime.FullArcingCurrentMs), ("clearingTime.reducedArcingCurrentMs", r.ClearingTime.ReducedArcingCurrentMs) })
            if (!(v > 0) || !double.IsFinite(v)) bad.Add($"{n} must be a positive finite number");
        if (string.IsNullOrWhiteSpace(r.ClearingTime.Source)) bad.Add("clearingTime.source must say where the clearing times came from");
        if (string.IsNullOrWhiteSpace(r.BoltedFault.Source)) bad.Add("boltedFault.source must say where the bolted fault current came from");
        return bad;
    }

    /// <summary>Run the study. An input outside the envelope gives an <c>OUT OF SCOPE</c> result with no numbers.</summary>
    public static ArcFlashResult Calculate(ArcFlashRequest r)
    {
        IReadOnlyList<string> breaches = EnvelopeBreaches(r);
        if (breaches.Count > 0)
            return new ArcFlashResult(Edition, r.Label, "OUT OF SCOPE", breaches, null, r, null, null, null, null, null, null, null, null, null,
                EnvelopeText, LimitationText);

        double voc = r.SystemVoltageKv, ibf = r.BoltedFault.Ka, g = r.GapMm, d = r.WorkingDistanceMm;
        bool lv = voc <= 0.6;
        double varCf = VarCf(r.Configuration, voc);
        ArcFlashEnclosureCorrection enc = EnclosureCorrection(r);

        ArcFlashScenario Scenario(string name, double tMs, bool reduced)
        {
            double factor = reduced ? 1.0 - 0.5 * varCf : 1.0;   // equation 2
            if (lv)
            {
                double i600Full = IntermediateArcingCurrent(r.Configuration, 0, ibf, g);
                double iFull = FinalLowVoltageArcingCurrent(voc, i600Full, ibf);    // equation 25
                double ia = iFull * factor;
                double e = IntermediateEnergy(r.Configuration, 0, ia, i600Full, ibf, g, d, enc.Cf, tMs);   // equation 6
                double afb = IntermediateBoundary(r.Configuration, 0, ia, i600Full, ibf, g, enc.Cf, tMs);   // equation 10
                return new ArcFlashScenario(name, ia, [i600Full, null, null], tMs, [null, null, null], e, e / JoulesPerCalorie,
                    [null, null, null], afb);
            }
            var ii = new double[3]; var ee = new double[3]; var bb = new double[3];
            for (int lvl = 0; lvl < 3; lvl++)
            {
                ii[lvl] = IntermediateArcingCurrent(r.Configuration, lvl, ibf, g) * factor;              // equations 1, 2
                ee[lvl] = IntermediateEnergy(r.Configuration, lvl, ii[lvl], ii[lvl], ibf, g, d, enc.Cf, tMs);   // equations 3-5
                bb[lvl] = IntermediateBoundary(r.Configuration, lvl, ii[lvl], ii[lvl], ibf, g, enc.Cf, tMs);    // equations 7-9
            }
            double iArc = Interpolate(voc, ii[0], ii[1], ii[2]);       // equations 16-18
            double eFinal = Interpolate(voc, ee[0], ee[1], ee[2]);     // equations 19-21
            double bFinal = Interpolate(voc, bb[0], bb[1], bb[2]);     // equations 22-24
            return new ArcFlashScenario(name, iArc, [ii[0], ii[1], ii[2]], tMs, [ee[0], ee[1], ee[2]], eFinal, eFinal / JoulesPerCalorie,
                [bb[0], bb[1], bb[2]], bFinal);
        }

        ArcFlashScenario full = Scenario("full", r.ClearingTime.FullArcingCurrentMs, reduced: false);
        ArcFlashScenario red = Scenario("reduced", r.ClearingTime.ReducedArcingCurrentMs, reduced: true);
        ArcFlashScenario gov = red.IncidentEnergyJPerCm2 > full.IncidentEnergyJPerCm2 ? red : full;
        return new ArcFlashResult(Edition, r.Label, "EVALUATED", [], lv ? "Voc <= 0.6 kV (equations 1, 2, 6, 10, 25)" : "0.6 kV < Voc <= 15 kV (equations 1-5, 7-9, 16-24)",
            r, varCf, enc, full, red, gov.Name, gov.IncidentEnergyJPerCm2, gov.IncidentEnergyCalPerCm2, gov.ArcFlashBoundaryMm, gov.ClearingTimeMs,
            EnvelopeText, LimitationText);
    }

    /// <summary>Equation 1: the intermediate arcing current (kA) at 600, 2700 or 14300 V (level 0, 1, 2).</summary>
    public static double IntermediateArcingCurrent(ElectrodeConfiguration ec, int level, double ibfKa, double gapMm)
    {
        ArcCoefficients k = Table1(ec, level);
        double x1 = k.K1 + k.K2 * DetMath.Log10(ibfKa) + k.K3 * DetMath.Log10(gapMm);
        double x2 = Poly(ibfKa, k.K4, k.K5, k.K6, k.K7, k.K8, k.K9, k.K10);
        return DetMath.Pow(10.0, x1) * x2;
    }

    /// <summary>Equation 2: the arcing current variation correction factor VarCf (Voc in kV).</summary>
    public static double VarCf(ElectrodeConfiguration ec, double vocKv)
    {
        double[] k = Table2(ec);
        return Poly(vocKv, k[0], k[1], k[2], k[3], k[4], k[5], k[6]);
    }

    /// <summary>Equation 25: the final arcing current below 600 V (kA).</summary>
    public static double FinalLowVoltageArcingCurrent(double vocKv, double iArc600Ka, double ibfKa)
    {
        double x1 = (0.6 / vocKv) * (0.6 / vocKv);
        double x2 = 1.0 / (iArc600Ka * iArc600Ka);
        double x3 = (0.6 * 0.6 - vocKv * vocKv) / (0.6 * 0.6 * ibfKa * ibfKa);
        return 1.0 / Math.Sqrt(x1 * (x2 - x3));
    }

    /// <summary>Equations 3 to 6: intermediate incident energy (J/cm2). <paramref name="numeratorCurrentKa"/> is the
    /// level's own arcing current for equations 3-5 and the full 600 V intermediate current for equation 6.</summary>
    public static double IntermediateEnergy(ElectrodeConfiguration ec, int level, double iArcKa, double numeratorCurrentKa, double ibfKa,
        double gapMm, double distanceMm, double cf, double tMs)
    {
        EnergyCoefficients k = EnergyTable(ec, level);
        double exponent = EnergyExponent(k, iArcKa, numeratorCurrentKa, ibfKa, gapMm, cf) + k.K12 * DetMath.Log10(distanceMm);
        return 12.552 / 50.0 * tMs * DetMath.Pow(10.0, exponent);
    }

    /// <summary>Equations 7 to 10: intermediate arc-flash boundary (mm).</summary>
    public static double IntermediateBoundary(ElectrodeConfiguration ec, int level, double iArcKa, double numeratorCurrentKa, double ibfKa,
        double gapMm, double cf, double tMs)
    {
        EnergyCoefficients k = EnergyTable(ec, level);
        double exponent = (EnergyExponent(k, iArcKa, numeratorCurrentKa, ibfKa, gapMm, cf) - DetMath.Log10(20.0 / tMs)) / -k.K12;
        return DetMath.Pow(10.0, exponent);
    }

    private static double EnergyExponent(EnergyCoefficients k, double iArcKa, double numeratorCurrentKa, double ibfKa, double gapMm, double cf)
    {
        double den = k.K4 * Pow(ibfKa, 7) + k.K5 * Pow(ibfKa, 6) + k.K6 * Pow(ibfKa, 5) + k.K7 * Pow(ibfKa, 4) +
                     k.K8 * Pow(ibfKa, 3) + k.K9 * ibfKa * ibfKa + k.K10 * ibfKa;
        return k.K1 + k.K2 * DetMath.Log10(gapMm) + k.K3 * numeratorCurrentKa / den
             + k.K11 * DetMath.Log10(ibfKa) + k.K13 * DetMath.Log10(iArcKa) + DetMath.Log10(1.0 / cf);
    }

    /// <summary>Equations 16 to 24: interpolation between the 600, 2700 and 14300 V intermediates (Voc in kV, above 0.6).</summary>
    public static double Interpolate(double vocKv, double x600, double x2700, double x14300)
    {
        double x1 = (x2700 - x600) / 2.1 * (vocKv - 2.7) + x2700;           // 16, 19, 22
        double x2 = (x14300 - x2700) / 11.6 * (vocKv - 14.3) + x14300;      // 17, 20, 23
        double x3 = x1 * (2.7 - vocKv) / 2.1 + x2 * (vocKv - 0.6) / 2.1;    // 18, 21, 24
        return vocKv <= 2.7 ? x3 : x2;
    }

    /// <summary>Equations 11 to 15 with Tables 6 and 7: the enclosure size correction.</summary>
    public static ArcFlashEnclosureCorrection EnclosureCorrection(ArcFlashRequest r)
    {
        if (!IsBox(r.Configuration)) return new ArcFlashEnclosureCorrection("Open air", null, null, null, 1.0);
        ArcFlashEnclosure e = r.Enclosure!;
        double voc = r.SystemVoltageKv;
        bool shallow = voc < 0.6 && e.HeightMm < 508.0 && e.WidthMm < 508.0 && e.DepthMm <= 203.2;
        (double a, double b) = r.Configuration switch
        {
            ElectrodeConfiguration.VCB => (4.0, 20.0),
            ElectrodeConfiguration.VCBB => (10.0, 24.0),
            _ => (10.0, 22.0),
        };
        double Eq11(double dimMm) => (660.4 + (dimMm - 660.4) * ((voc + a) / b)) / 25.4;   // equations 11 and 12, inches

        double w = e.WidthMm, h = e.HeightMm;
        double width1 = w < 508.0 ? (shallow ? Table6InchPerMm * w : 20.0)
            : w <= 660.4 ? Table6InchPerMm * w
            : w <= 1244.6 ? Eq11(w)
            : Eq11(1244.6);
        double height1 = h < 508.0 ? (shallow ? Table6InchPerMm * h : 20.0)
            : h <= 660.4 ? Table6InchPerMm * h
            : h <= 1244.6 ? (r.Configuration == ElectrodeConfiguration.VCB ? Table6InchPerMm * h : Eq11(h))
            : (r.Configuration == ElectrodeConfiguration.VCB ? 49.0 : Eq11(1244.6));
        double ees = (height1 + width1) / 2.0;                                              // equation 13
        (double b1, double b2, double b3) = Table7(shallow, r.Configuration);
        double poly = b1 * ees * ees + b2 * ees + b3;
        double cf = shallow ? 1.0 / poly : poly;                                            // equations 14, 15
        return new ArcFlashEnclosureCorrection(shallow ? "Shallow" : "Typical", width1, height1, ees, cf);
    }

    /// <summary>
    /// Read a three-phase bolted fault current from the incumbent IEC 60909 solver (<see cref="Iec60909.ThreePhase"/>,
    /// default settings apart from the objective). Only its outputs are read; the solver, its inputs and its equipment
    /// checks are untouched.
    /// </summary>
    public static BoltedFaultInput FromIec60909(GridModel model, int busId, ShortCircuitObjective objective)
    {
        ArgumentNullException.ThrowIfNull(model);
        Bus bus = model.Buses.FirstOrDefault(b => b.Id == busId) ?? throw new ArgumentException($"Bus {busId} is not in the case.", nameof(busId));
        Iec60909.Duty duty = Iec60909.ThreePhase(model, busId, objective);
        var evidence = new Iec60909FaultEvidence(busId, objective.ToString(), duty.IkppKa, bus.BaseKv, duty.RoverX, duty.PeakIpKa,
            "GridSim.Core.Protection.ShortCircuit.Iec60909.ThreePhase (IEC 60909-0, default options)");
        return new BoltedFaultInput(duty.IkppKa, $"IEC 60909-0 Ik'' at bus {busId}, {objective} objective (GridSim Iec60909.ThreePhase)", evidence);
    }

    private static double Pow(double x, int n)
    {
        double p = 1.0;
        for (int i = 0; i < n; i++) p *= x;
        return p;
    }

    // c0*x^6 + c1*x^5 + ... + c6 (powers formed by repeated multiplication, as in the published equations).
    private static double Poly(double x, double c6, double c5, double c4, double c3, double c2, double c1, double c0) =>
        c6 * Pow(x, 6) + c5 * Pow(x, 5) + c4 * Pow(x, 4) + c3 * Pow(x, 3) + c2 * x * x + c1 * x + c0;
}
