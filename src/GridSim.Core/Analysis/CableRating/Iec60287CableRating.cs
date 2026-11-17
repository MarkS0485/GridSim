using GridSim.Core.Numerics;

namespace GridSim.Core.Analysis.CableRating;

/// <summary>Conductor material (sets the temperature coefficient alpha20 of IEC 60287-1-1 Table 1).</summary>
public enum ConductorMaterial
{
    /// <summary>Copper, alpha20 = 3.93e-3 per K.</summary>
    Copper,
    /// <summary>Aluminium, alpha20 = 4.03e-3 per K.</summary>
    Aluminium,
}

/// <summary>Which rating the study selects. Until a selection is made the nameplate and calculated ratings stay
/// separate evidence and no rating is selected.</summary>
public enum RatingSelection
{
    /// <summary>No rating selected (both are reported as evidence only).</summary>
    None,
    /// <summary>Select the calculated IEC 60287 rating.</summary>
    Calculated,
    /// <summary>Select the nameplate (declared) rating.</summary>
    Nameplate,
    /// <summary>Select the lower of the two.</summary>
    Lower,
}

/// <summary>Single-core unarmoured cable construction. Diameters in mm, resistivities in K.m/W.</summary>
/// <param name="Material">Conductor material.</param>
/// <param name="DcResistance20OhmPerKm">Conductor DC resistance at 20 C (ohm/km), e.g. the IEC 60228 maximum.</param>
/// <param name="SkinEffectKs">Skin-effect coefficient ks (IEC 60287-1-1 Table 2; 1.0 for round stranded or solid).</param>
/// <param name="ConductorDiameterMm">Conductor diameter dc.</param>
/// <param name="InsulationDiameterMm">Diameter over the insulation including its semiconducting screens, Di.</param>
/// <param name="ScreenDiameterMm">Diameter over the metallic screen or sheath, Ds.</param>
/// <param name="OverallDiameterMm">Overall (external) diameter De.</param>
/// <param name="InsulationThermalResistivity">Insulation thermal resistivity rho_T1 (K.m/W; XLPE 3.5).</param>
/// <param name="OversheathThermalResistivity">Oversheath thermal resistivity rho_T3 (K.m/W; PE 3.5, PVC 5.0).</param>
/// <param name="RelativePermittivity">Insulation relative permittivity epsilon (XLPE 2.5).</param>
/// <param name="LossTangent">Insulation loss factor tan delta (XLPE 0.001).</param>
/// <param name="MaxConductorTemperatureC">Maximum permissible conductor temperature (C; XLPE 90).</param>
public sealed record CableConstruction(
    ConductorMaterial Material, double DcResistance20OhmPerKm, double SkinEffectKs, double ConductorDiameterMm, double InsulationDiameterMm,
    double ScreenDiameterMm, double OverallDiameterMm, double InsulationThermalResistivity, double OversheathThermalResistivity,
    double RelativePermittivity, double LossTangent, double MaxConductorTemperatureC);

/// <summary>System quantities.</summary>
/// <param name="U0Kv">Phase-to-earth voltage U0 (kV rms).</param>
/// <param name="FrequencyHz">System frequency (50 or 60 Hz).</param>
public sealed record CableSystem(double U0Kv, double FrequencyHz);

/// <summary>A single isolated cable laid direct in uniform soil.</summary>
/// <param name="DepthToAxisM">Depth from the ground surface to the cable axis, L (m).</param>
/// <param name="SoilThermalResistivity">Soil thermal resistivity (K.m/W).</param>
/// <param name="AmbientGroundTemperatureC">Ambient ground temperature at the burial depth (C).</param>
public sealed record BuriedInstallation(double DepthToAxisM, double SoilThermalResistivity, double AmbientGroundTemperatureC);

/// <summary>The metallic screen loss factor lambda1 (screen losses over conductor losses), declared with its source.
/// v1 does not compute lambda1 (IEC 60287-1-1 clause 2.3 depends on bonding, spacing and the other phases).</summary>
public sealed record ScreenLossInput(double Lambda1, string Source);

/// <summary>A declared (nameplate, datasheet or asset-register) rating, kept as separate evidence.</summary>
public sealed record NameplateRating(double Amperes, string Source);

/// <summary>A steady-state cable rating request.</summary>
public sealed record CableRatingRequest(
    string Label, CableConstruction Cable, CableSystem System, BuriedInstallation Installation, ScreenLossInput ScreenLoss,
    NameplateRating? Nameplate, RatingSelection Selection);

/// <summary>Every intermediate quantity of the calculation, with units in the names.</summary>
public sealed record CableRatingWorking(
    double Alpha20PerK, double DcResistanceAtMaxOhmPerM, double SkinEffectXs2, double SkinEffectXs, double SkinEffectYs,
    double ProximityEffectYp, double AcResistanceOhmPerM, double CapacitanceFPerM, double DielectricLossWPerM,
    double T1KmPerW, double T2KmPerW, double T3KmPerW, double U, double T4KmPerW, double Lambda1, double Lambda2,
    double PermissibleRiseK, double ConductorLossAtRatingWPerM, double ScreenLossAtRatingWPerM);

/// <summary>The result. When <see cref="Status"/> is <c>OUT OF SCOPE</c> the numbers are null and
/// <see cref="OutOfScope"/> lists every reason.</summary>
public sealed record CableRatingResult(
    string Method, string Label, string Status, IReadOnlyList<string> OutOfScope, CableRatingRequest Inputs,
    CableRatingWorking? Working, double? CalculatedRatingA, NameplateRating? Nameplate, double? NameplateOverCalculated,
    string Selection, double? SelectedRatingA, string? SelectedBasis, IReadOnlyList<string> Scope, IReadOnlyList<string> Deferred);

/// <summary>
/// RC3 P8 (cable thermal sizing): the steady-state (100 % load factor) current rating of one single-core unarmoured
/// cable laid direct in the ground, isolated from other cables.
/// <para><b>Declared method.</b> IEC 60287-1-1 (rating equation for a buried AC cable, clause 1.4.1.1, with n = 1,
/// T2 = 0 and lambda2 = 0 for an unarmoured cable; AC resistance clause 2.1 with the skin effect of 2.1.2 for xs up to
/// 2.8; dielectric losses clause 2.2) and IEC 60287-2-1 (T1 clause 4.1.2, T3 clause 4.1.4, T4 of a single isolated
/// buried cable clause 4.2.2, <c>T4 = rho/(2 pi) ln(u + sqrt(u^2 - 1))</c>, <c>u = 2L/De</c>). Clause numbers are those
/// of the 2006/2015 editions as recalled; they were not checked against a copy of the standard on this machine
/// [VERIFY]. The proximity effect is zero (no other conductor nearby) and the screen loss factor lambda1 is a declared
/// input.</para>
/// <para>The rating is computed at the maximum conductor temperature, as the standard does, so no iteration is needed.
/// The nameplate rating is never mixed into the calculation: it is echoed as separate evidence and a rating is selected
/// only when the request says so.</para>
/// </summary>
public static class Iec60287CableRating
{
    /// <summary>The declared method.</summary>
    public const string Method = "IEC 60287-1-1 clause 1.4.1.1 (n = 1, unarmoured), 2.1, 2.2; IEC 60287-2-1 clauses 4.1.2, 4.1.4, 4.2.2 (single isolated buried cable); steady state";

    /// <summary>The scope, as applied.</summary>
    public static readonly IReadOnlyList<string> ScopeText =
    [
        "One single-core unarmoured cable, laid direct in uniform soil, far from other cables and heat sources.",
        "Steady state, 100 % load factor; the ground surface is taken as isothermal (the Kennelly image method behind T4).",
        "AC resistance at the maximum conductor temperature; skin effect for xs <= 2.8; proximity effect zero.",
        "Dielectric losses always included (the standard allows them to be neglected below a voltage threshold).",
        "Screen loss factor lambda1 declared by the caller; armour (T2, lambda2) not modelled.",
        "No moisture migration (soil drying) and no backfill, duct or trough.",
    ];

    /// <summary>What is not evidenced.</summary>
    public static readonly IReadOnlyList<string> DeferredText =
    [
        "Published ampacity reference check (for example a CIGRE TB 880 case with the same conductor, installation and thermal assumptions): deferred, no such example is available on this machine.",
        "Transient and cyclic ratings (IEC 60853): not implemented.",
        "Groups of cables, trefoil and flat formations, ducts, troughs, mutual heating and lambda1 from bonding arrangements (IEC 60287-1-1 clause 2.3): not implemented.",
    ];

    /// <summary>alpha20 per K (IEC 60287-1-1 Table 1).</summary>
    public static double Alpha20(ConductorMaterial m) => m == ConductorMaterial.Copper ? 3.93e-3 : 4.03e-3;

    /// <summary>Every reason the request is outside this method's scope or is not a valid input.</summary>
    public static IReadOnlyList<string> ScopeBreaches(CableRatingRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var bad = new List<string>();
        CableConstruction c = r.Cable;
        void Positive(string n, double v) { if (!(v > 0) || !double.IsFinite(v)) bad.Add($"{n} must be a positive finite number; got {v:R}"); }
        void NonNeg(string n, double v) { if (!(v >= 0) || !double.IsFinite(v)) bad.Add($"{n} must be zero or a positive finite number; got {v:R}"); }
        Positive("cable.dcResistance20OhmPerKm", c.DcResistance20OhmPerKm);
        if (!(c.SkinEffectKs > 0 && c.SkinEffectKs <= 1.0)) bad.Add($"cable.skinEffectKs must be in (0, 1]; got {c.SkinEffectKs:R}");
        Positive("cable.conductorDiameterMm", c.ConductorDiameterMm);
        Positive("cable.insulationDiameterMm", c.InsulationDiameterMm);
        Positive("cable.screenDiameterMm", c.ScreenDiameterMm);
        Positive("cable.overallDiameterMm", c.OverallDiameterMm);
        Positive("cable.insulationThermalResistivity", c.InsulationThermalResistivity);
        Positive("cable.oversheathThermalResistivity", c.OversheathThermalResistivity);
        if (!(c.RelativePermittivity >= 1.0) || !double.IsFinite(c.RelativePermittivity)) bad.Add($"cable.relativePermittivity must be at least 1; got {c.RelativePermittivity:R}");
        NonNeg("cable.lossTangent", c.LossTangent);
        if (!double.IsFinite(c.MaxConductorTemperatureC)) bad.Add("cable.maxConductorTemperatureC must be finite");
        if (!(c.ConductorDiameterMm < c.InsulationDiameterMm && c.InsulationDiameterMm <= c.ScreenDiameterMm && c.ScreenDiameterMm < c.OverallDiameterMm))
            bad.Add("diameters must satisfy conductor < insulation <= screen < overall");
        NonNeg("system.u0Kv", r.System.U0Kv);
        if (r.System.FrequencyHz is not (50.0 or 60.0)) bad.Add($"system.frequencyHz must be 50 or 60; got {r.System.FrequencyHz:R}");
        Positive("installation.depthToAxisM", r.Installation.DepthToAxisM);
        Positive("installation.soilThermalResistivity", r.Installation.SoilThermalResistivity);
        if (!double.IsFinite(r.Installation.AmbientGroundTemperatureC)) bad.Add("installation.ambientGroundTemperatureC must be finite");
        NonNeg("screenLoss.lambda1", r.ScreenLoss.Lambda1);
        if (string.IsNullOrWhiteSpace(r.ScreenLoss.Source)) bad.Add("screenLoss.source must say where lambda1 came from");
        if (r.Nameplate is NameplateRating np)
        {
            Positive("nameplate.amperes", np.Amperes);
            if (string.IsNullOrWhiteSpace(np.Source)) bad.Add("nameplate.source must say where the rating came from");
        }
        if (r.Nameplate is null && r.Selection is RatingSelection.Nameplate or RatingSelection.Lower)
            bad.Add($"selection {r.Selection} needs a nameplate rating");
        if (bad.Count > 0) return bad;

        if (!(c.MaxConductorTemperatureC > r.Installation.AmbientGroundTemperatureC))
            bad.Add("the maximum conductor temperature must exceed the ambient ground temperature");
        double u = 2.0 * r.Installation.DepthToAxisM / (c.OverallDiameterMm / 1000.0);
        if (!(u > 1.0)) bad.Add($"u = 2L/De = {u:R}: the cable must be fully buried (L > De/2)");
        double rDc = c.DcResistance20OhmPerKm / 1000.0 * (1.0 + Alpha20(c.Material) * (c.MaxConductorTemperatureC - 20.0));
        double xs2 = 8.0 * Math.PI * r.System.FrequencyHz / rDc * 1e-7 * c.SkinEffectKs;
        if (Math.Sqrt(xs2) > 2.8) bad.Add($"skin-effect argument xs = {Math.Sqrt(xs2):R} exceeds 2.8, outside the implemented formula (large or segmental conductors)");
        return bad;
    }

    /// <summary>Calculate the rating.</summary>
    public static CableRatingResult Calculate(CableRatingRequest r)
    {
        IReadOnlyList<string> breaches = ScopeBreaches(r);
        if (breaches.Count > 0)
            return new CableRatingResult(Method, r.Label, "OUT OF SCOPE", breaches, r, null, null, r.Nameplate, null, r.Selection.ToString(), null, null, ScopeText, DeferredText);

        CableConstruction c = r.Cable;
        double alpha = Alpha20(c.Material);
        double rDc = c.DcResistance20OhmPerKm / 1000.0 * (1.0 + alpha * (c.MaxConductorTemperatureC - 20.0));
        double xs2 = 8.0 * Math.PI * r.System.FrequencyHz / rDc * 1e-7 * c.SkinEffectKs;
        double xs4 = xs2 * xs2;
        double ys = xs4 / (192.0 + 0.8 * xs4);
        double yp = 0.0;
        double rAc = rDc * (1.0 + ys + yp);

        double cap = c.RelativePermittivity / (18.0 * DetMath.Log(c.InsulationDiameterMm / c.ConductorDiameterMm)) * 1e-9;
        double omega = 2.0 * Math.PI * r.System.FrequencyHz;
        double u0 = r.System.U0Kv * 1000.0;
        double wd = omega * cap * u0 * u0 * c.LossTangent;

        double t1 = c.InsulationThermalResistivity / (2.0 * Math.PI) * DetMath.Log(c.InsulationDiameterMm / c.ConductorDiameterMm);
        double t2 = 0.0;
        double t3 = c.OversheathThermalResistivity / (2.0 * Math.PI) * DetMath.Log(c.OverallDiameterMm / c.ScreenDiameterMm);
        double u = 2.0 * r.Installation.DepthToAxisM / (c.OverallDiameterMm / 1000.0);
        double t4 = r.Installation.SoilThermalResistivity / (2.0 * Math.PI) * DetMath.Log(u + Math.Sqrt(u * u - 1.0));
        double l1 = r.ScreenLoss.Lambda1, l2 = 0.0;
        double dTheta = c.MaxConductorTemperatureC - r.Installation.AmbientGroundTemperatureC;

        double numerator = dTheta - wd * (0.5 * t1 + (t2 + t3 + t4));
        if (!(numerator > 0))
            return new CableRatingResult(Method, r.Label, "OUT OF SCOPE",
                [$"dielectric losses alone ({wd:R} W/m) use the whole permissible rise of {dTheta:R} K"], r, null, null, r.Nameplate, null,
                r.Selection.ToString(), null, null, ScopeText, DeferredText);
        double denominator = rAc * t1 + rAc * (1.0 + l1) * t2 + rAc * (1.0 + l1 + l2) * (t3 + t4);
        double amps = Math.Sqrt(numerator / denominator);

        var working = new CableRatingWorking(alpha, rDc, xs2, Math.Sqrt(xs2), ys, yp, rAc, cap, wd, t1, t2, t3, u, t4, l1, l2, dTheta,
            amps * amps * rAc, l1 * amps * amps * rAc);
        double? ratio = r.Nameplate is NameplateRating np ? np.Amperes / amps : null;
        (double? selected, string? basis) = r.Selection switch
        {
            RatingSelection.Calculated => (amps, "calculated (IEC 60287 steady state)"),
            RatingSelection.Nameplate => (r.Nameplate!.Amperes, "nameplate: " + r.Nameplate.Source),
            RatingSelection.Lower => r.Nameplate!.Amperes <= amps
                ? (r.Nameplate.Amperes, "lower of the two: nameplate (" + r.Nameplate.Source + ")")
                : (amps, "lower of the two: calculated (IEC 60287 steady state)"),
            _ => ((double?)null, (string?)null),
        };
        return new CableRatingResult(Method, r.Label, "EVALUATED", [], r, working, amps, r.Nameplate, ratio, r.Selection.ToString(), selected, basis,
            ScopeText, DeferredText);
    }
}
