using System;
using System.Collections.Generic;
using System.Linq;

namespace GridSim.Core.Analysis.CableRating.Sizing;

/// <summary>A correction factor the user states directly (a thermal-insulation or harmonic factor, a soil factor, ...), with its source.</summary>
public sealed record NamedFactor(string Name, double Value, string Source);

/// <summary>One candidate cross-section rated by IEC 60287 (the buried single-core method already in <see cref="Iec60287CableRating"/>).</summary>
public sealed record Iec60287Candidate(double SizeMm2, CableConstruction Cable);

/// <summary>The installation used when ratings are computed by IEC 60287 instead of read from a table.</summary>
public sealed record Iec60287Basis(
    IReadOnlyList<Iec60287Candidate> Candidates, CableSystem System, BuriedInstallation Installation, ScreenLossInput ScreenLoss);

/// <summary>
/// The circuit to be sized. <see cref="AmbientC"/> and <see cref="GroupCount"/> null mean "no such correction requested" (stated in the result, not
/// assumed). <see cref="DeviceRatingA"/> null means the design current is used as In. A limit or fault input left null means that check is not
/// requested. With <see cref="Iec60287"/> set the base ratings come from IEC 60287 (soil temperature is in the installation, so no ambient factor
/// is applied) and only grouping and the named factors are applied on top; otherwise they come from the ampacity table.
/// </summary>
public sealed record SizingCircuit(
    double DesignCurrentA, double? DeviceRatingA, double SystemVoltageV, int Phases, double LengthM, double PowerFactor,
    string Conductor, string Insulation, string InstallationMethod, int LoadedConductors,
    double? AmbientC = null, int? GroupCount = null, IReadOnlyList<NamedFactor>? ExtraFactors = null,
    double? VoltDropLimitPercent = null, double? FaultCurrentA = null, double? FaultDurationS = null, Iec60287Basis? Iec60287 = null);

/// <summary>One check's outcome. <see cref="Status"/> is EVALUATED, OUT OF SCOPE (a table or input is missing; <see cref="Detail"/> says which) or NOT REQUESTED.</summary>
public sealed record SizingCheck(string Name, string Status, string Detail, double? MinimumSizeMm2, string Clause);

/// <summary>One candidate size with each check's figure; null when that check could not be evaluated.</summary>
public sealed record SizingCandidate(
    double SizeMm2, double? TabulatedAmps, double? PermittedAmps, bool? AmpacityOk, double? VoltDropPercent, bool? VoltDropOk, bool? ThermalOk);

public sealed record CableSizingResult(
    string Method, string Label, string Status, IReadOnlyList<string> OutOfScope, string TablesId, string TablesStatus, TableProvenance Provenance,
    double? DeviceCurrentA, double? CombinedFactor, double? RequiredTabulatedAmps, IReadOnlyList<SizingCheck> Checks,
    double? SelectedSizeMm2, IReadOnlyList<SizingCandidate> Candidates, IReadOnlyList<string> Factors, IReadOnlyList<string> Awaiting);

/// <summary>
/// Cable sizing by the design rule of IEC 60364-5-52 and BS 7671 (RC4 R4-20d): <c>Ib &lt;= In &lt;= Iz</c> with <c>Iz = It Ca Cg ...</c>, so the
/// tabulated current needed is <c>It &gt;= In / (Ca Cg ...)</c>; then voltage drop and the adiabatic short-circuit check. The METHOD is built; the
/// NUMBERS (current-carrying tables, correction factors, volt-drop tables, adiabatic k) are paid text and come only from the table file the owner
/// fills with provenance. Order: validate, factors, required current, ampacity selection, voltage drop, thermal, combine. A missing table makes that
/// quantity OUT OF SCOPE (never a default), and no size is reported unless every requested check was evaluated.
/// <para>Voltage drop: <c>dV = k Ib L mV/A/m / 1000</c> (k = 1 for the tabulated two-way figure) or computed
/// <c>dV = K Ib L (R cos(phi) + X sin(phi))</c> with K = sqrt(3) (three-phase) or 2 (single-phase) from resistance and reactance per km. Thermal:
/// <c>S &gt;= sqrt(I^2 t) / k</c>. Clause references name the sections of the public design rule (IEC 60364-5-52 clause 523 and Appendix B; BS 7671
/// Appendix 4 and regulation 434.5.2); the clause numbers were not checked against the paid texts.</para>
/// </summary>
public static class CableSizing
{
    public const string Method = "Ib <= In <= Iz, Iz = It Ca Cg (IEC 60364-5-52 / BS 7671 design rule); voltage drop; adiabatic equation; tables from the owner's file";

    public static readonly IReadOnlyList<string> Awaiting = new[]
    {
        "AWAITING STANDARD TEXT: the current-carrying capacity tables (IEC 60364-5-52 Appendix B / BS 7671 Appendix 4): supplied in the table file, none shipped",
        "AWAITING STANDARD TEXT: the ambient-temperature, grouping and other correction factors (supplied in the table file or as named factors with a source)",
        "AWAITING STANDARD TEXT: the voltage-drop tables or resistance and reactance values, and the voltage-drop limits (the limit is a required input here)",
        "AWAITING STANDARD TEXT: the adiabatic constants k and the protective-device rules (Ib <= In <= Iz and the conventional tripping current are only the arithmetic here)",
        "AWAITING STANDARD TEXT: the standard's worked examples (none reproduced; no acceptance claimed)",
    };

    public static IReadOnlyList<string> Validate(SizingCircuit c)
    {
        var bad = new List<string>();
        if (!(c.DesignCurrentA > 0.0) || !double.IsFinite(c.DesignCurrentA)) bad.Add("design current must be finite and above 0 A");
        if (c.DeviceRatingA is double d && (!(d > 0.0) || !double.IsFinite(d))) bad.Add("device rating must be finite and above 0 A");
        else if (c.DeviceRatingA is double d2 && d2 < c.DesignCurrentA) bad.Add($"device rating {d2} A is below the design current {c.DesignCurrentA} A (Ib <= In is required)");
        if (!(c.SystemVoltageV > 0.0)) bad.Add("system voltage must be above 0 V");
        if (c.Phases is not (1 or 3)) bad.Add("phases must be 1 or 3");
        if (!(c.LengthM > 0.0)) bad.Add("length must be above 0 m");
        if (!(c.PowerFactor > 0.0 && c.PowerFactor <= 1.0)) bad.Add("power factor must be above 0 and at most 1");
        if (c.LoadedConductors < 2) bad.Add("loaded conductors must be 2 or more");
        if (c.GroupCount is int g && g < 1) bad.Add("group count must be 1 or more");
        if (c.VoltDropLimitPercent is double v && !(v > 0.0)) bad.Add("voltage-drop limit must be above 0 percent");
        if ((c.FaultCurrentA is null) != (c.FaultDurationS is null)) bad.Add("fault current and fault duration are given together or not at all");
        if (c.FaultCurrentA is double fi && !(fi > 0.0)) bad.Add("fault current must be above 0 A");
        if (c.FaultDurationS is double ft && !(ft > 0.0)) bad.Add("fault duration must be above 0 s");
        if (c.ExtraFactors is not null) foreach (NamedFactor f in c.ExtraFactors) if (!(f.Value > 0.0) || string.IsNullOrWhiteSpace(f.Source)) bad.Add($"factor '{f.Name}' needs a value above 0 and a source");
        if (string.IsNullOrWhiteSpace(c.Conductor) || string.IsNullOrWhiteSpace(c.Insulation) || string.IsNullOrWhiteSpace(c.InstallationMethod)) bad.Add("conductor, insulation and installation method are required labels");
        return bad;
    }

    private static double? Lookup(IReadOnlyList<double> keys, IReadOnlyList<double> vals, double key)
    {
        for (int i = 0; i < keys.Count; i++) if (keys[i] == key) return vals[i];
        return null;
    }

    public static CableSizingResult Compute(string label, CableTables tables, SizingCircuit c)
    {
        IReadOnlyList<string> bad = Validate(c);
        if (bad.Count > 0)
            return Result(label, tables, "OUT OF SCOPE", bad, null, null, null, Array.Empty<SizingCheck>(), null, Array.Empty<SizingCandidate>(), Array.Empty<string>());

        var oos = new List<string>();
        var factors = new List<string>();
        double inA = c.DeviceRatingA ?? c.DesignCurrentA;
        double cf = 1.0;
        bool factorsOk = true;

        // ---- correction factors, in a fixed order: ambient, grouping, named ----
        if (c.Iec60287 is null && c.AmbientC is double amb)
        {
            FactorTable? ft = tables.Factors.FirstOrDefault(f => f.Kind == "ambient" && f.Insulation == c.Insulation && (f.Method.Length == 0 || f.Method == c.InstallationMethod));
            double? ca = ft is null ? null : Lookup(ft.Keys, ft.Factors, amb);
            if (ft is null) { oos.Add($"ambient correction: no ambient table for insulation '{c.Insulation}' in table file '{tables.Id}'"); factorsOk = false; }
            else if (ca is null) { oos.Add($"ambient correction: {amb} C is not a tabulated temperature (tabulated: {string.Join(", ", ft.Keys)}); no interpolation is done"); factorsOk = false; }
            else { cf *= ca.Value; factors.Add(FormattableString.Invariant($"Ca = {ca.Value:R} (ambient {amb:R} C, table)")); }
        }
        else if (c.Iec60287 is not null && c.AmbientC is not null) factors.Add("ambient: not applied; the IEC 60287 calculation carries the soil temperature");
        else factors.Add("Ca: no ambient correction requested");
        if (c.GroupCount is int n)
        {
            FactorTable? gt = tables.Factors.FirstOrDefault(f => f.Kind == "grouping" && (f.Method.Length == 0 || f.Method == c.InstallationMethod));
            double? cg = gt is null ? null : Lookup(gt.Keys, gt.Factors, n);
            if (gt is null) { oos.Add($"grouping correction: no grouping table for method '{c.InstallationMethod}' in table file '{tables.Id}'"); factorsOk = false; }
            else if (cg is null) { oos.Add($"grouping correction: {n} circuits is not tabulated (tabulated: {string.Join(", ", gt.Keys)})"); factorsOk = false; }
            else { cf *= cg.Value; factors.Add(FormattableString.Invariant($"Cg = {cg.Value:R} ({n} circuits, table)")); }
        }
        else factors.Add("Cg: no grouping correction requested");
        if (c.ExtraFactors is not null)
            foreach (NamedFactor f in c.ExtraFactors) { cf *= f.Value; factors.Add(FormattableString.Invariant($"{f.Name} = {f.Value:R} (source: {f.Source})")); }

        // ---- base ratings per candidate size ----
        var sizes = new List<double>();
        var baseAmps = new Dictionary<double, double>();
        var checks = new List<SizingCheck>();
        bool ampOk = factorsOk;
        string ampClause = "IEC 60364-5-52 clause 523 and Appendix B; BS 7671 Appendix 4 (Ib <= In <= Iz)";
        if (c.Iec60287 is Iec60287Basis basis)
        {
            foreach (Iec60287Candidate k in basis.Candidates.OrderBy(x => x.SizeMm2))
            {
                CableRatingResult r = Iec60287CableRating.Calculate(new CableRatingRequest(FormattableString.Invariant($"size {k.SizeMm2:R}"), k.Cable, basis.System, basis.Installation, basis.ScreenLoss, null, RatingSelection.Calculated));
                if (r.CalculatedRatingA is double a) { sizes.Add(k.SizeMm2); baseAmps[k.SizeMm2] = a; }
                else oos.Add(FormattableString.Invariant($"IEC 60287 rating of {k.SizeMm2:R} mm2 is out of scope: {string.Join("; ", r.OutOfScope)}"));
            }
            ampClause = "IEC 60287-1-1 / 60287-2-1 (buried single-core cable, steady state) with Cg and the named factors on top";
            if (sizes.Count == 0) { oos.Add("no candidate could be rated by IEC 60287"); ampOk = false; }
        }
        else
        {
            AmpacityTable? at = tables.Ampacity.FirstOrDefault(t => t.Method == c.InstallationMethod && t.Conductor == c.Conductor && t.Insulation == c.Insulation && t.LoadedConductors == c.LoadedConductors);
            if (at is null) { oos.Add($"current-carrying capacity: no table for method '{c.InstallationMethod}', {c.Conductor}, {c.Insulation}, {c.LoadedConductors} loaded conductors in table file '{tables.Id}' (status {tables.Status})"); ampOk = false; }
            else for (int i = 0; i < at.SizesMm2.Count; i++) { sizes.Add(at.SizesMm2[i]); baseAmps[at.SizesMm2[i]] = at.Amps[i]; }
        }
        double? required = ampOk ? inA / cf : null;
        double? ampMin = null;
        if (ampOk)
        {
            foreach (double s in sizes) if (baseAmps[s] >= required!.Value) { ampMin = s; break; }
            checks.Add(new SizingCheck("ampacity", "EVALUATED",
                ampMin is null ? FormattableString.Invariant($"no tabulated size carries {required:R} A (In {inA:R} A / factors {cf:R})") : FormattableString.Invariant($"smallest size with It >= In / (product of factors) = {inA:R} / {cf:R} = {required:R} A"),
                ampMin, ampClause));
        }
        else checks.Add(new SizingCheck("ampacity", "OUT OF SCOPE", string.Join("; ", oos), null, ampClause));

        // ---- voltage drop ----
        Func<double, double?> vd = _ => null;
        string vdClause = "IEC 60364-5-52 Appendix G / BS 7671 Appendix 4 section 6.2";
        if (c.VoltDropLimitPercent is double lim)
        {
            VoltDropTable? vt = tables.VoltDrop.FirstOrDefault(t => t.Conductor == c.Conductor && t.Insulation == c.Insulation && t.Phases == (c.Phases == 3 ? "3ph" : "1ph"));
            ImpedanceTable? it = tables.Impedance.FirstOrDefault(t => t.Conductor == c.Conductor && t.Insulation == c.Insulation);
            double kk = c.Phases == 3 ? Math.Sqrt(3.0) : 2.0, sinPhi = Math.Sqrt(1.0 - c.PowerFactor * c.PowerFactor);
            if (vt is not null)
                vd = s => Lookup(vt.SizesMm2, vt.MvPerAPerM, s) is double mv ? mv * c.DesignCurrentA * c.LengthM / 1000.0 / c.SystemVoltageV * 100.0 : null;
            else if (it is not null)
                vd = s => Lookup(it.SizesMm2, it.ROhmPerKm, s) is double r && Lookup(it.SizesMm2, it.XOhmPerKm, s) is double x
                    ? kk * c.DesignCurrentA * c.LengthM / 1000.0 * (r * c.PowerFactor + x * sinPhi) / c.SystemVoltageV * 100.0 : null;
            if (vt is null && it is null) checks.Add(new SizingCheck("voltage drop", "OUT OF SCOPE", $"no mV/A/m or impedance table for {c.Conductor}, {c.Insulation}, {(c.Phases == 3 ? "3ph" : "1ph")} in table file '{tables.Id}'", null, vdClause));
            else checks.Add(new SizingCheck("voltage drop", "EVALUATED", FormattableString.Invariant($"{(vt is not null ? "tabulated mV/A/m" : "computed from R and X")}; limit {lim:R} percent of {c.SystemVoltageV:R} V"), null, vdClause));
        }
        else checks.Add(new SizingCheck("voltage drop", "NOT REQUESTED", "no voltage-drop limit given", null, vdClause));

        // ---- thermal (adiabatic) ----
        double? thermalMin = null;
        string thClause = "IEC 60364-4-43 / BS 7671 regulation 434.5.2 (S >= sqrt(I^2 t) / k)";
        if (c.FaultCurrentA is double fc && c.FaultDurationS is double fd)
        {
            AdiabaticK? kt = tables.AdiabaticK.FirstOrDefault(k => k.Conductor == c.Conductor && k.Insulation == c.Insulation);
            if (kt is null) checks.Add(new SizingCheck("thermal", "OUT OF SCOPE", $"no adiabatic k for {c.Conductor}, {c.Insulation} in table file '{tables.Id}'", null, thClause));
            else
            {
                thermalMin = Math.Sqrt(fc * fc * fd) / kt.K;
                checks.Add(new SizingCheck("thermal", "EVALUATED", FormattableString.Invariant($"S >= sqrt({fc:R}^2 x {fd:R}) / {kt.K:R} = {thermalMin:R} mm2"), thermalMin, thClause));
            }
        }
        else checks.Add(new SizingCheck("thermal", "NOT REQUESTED", "no fault current and duration given", null, thClause));

        // ---- candidates and the combined selection ----
        var cands = new List<SizingCandidate>();
        double? selected = null;
        bool vdEval = checks.First(x => x.Name == "voltage drop").Status == "EVALUATED";
        bool thEval = checks.First(x => x.Name == "thermal").Status == "EVALUATED";
        foreach (double s in sizes)
        {
            double? tab = baseAmps[s];
            double? perm = ampOk ? tab * cf : null;
            bool? aOk = ampOk ? tab >= required : null;
            double? dv = vdEval ? vd(s) : null;
            bool? vOk = dv is double d ? d <= c.VoltDropLimitPercent : null;
            bool? tOk = thEval ? s >= thermalMin : null;
            cands.Add(new SizingCandidate(s, tab, perm, aOk, dv, vOk, tOk));
        }
        bool requestedAllEvaluated = ampOk && checks.All(x => x.Status != "OUT OF SCOPE");
        if (vdEval && cands.Any(x => x.VoltDropOk is null)) { oos.Add("voltage drop: a candidate size is not tabulated in the voltage-drop or impedance table"); requestedAllEvaluated = false; }
        if (requestedAllEvaluated)
            foreach (SizingCandidate k in cands)
                if (k.AmpacityOk == true && (!vdEval || k.VoltDropOk == true) && (!thEval || k.ThermalOk == true)) { selected = k.SizeMm2; break; }
        string status = !requestedAllEvaluated ? "OUT OF SCOPE" : selected is null ? "NO SIZE" : "EVALUATED";
        if (status == "NO SIZE") oos.Add("no tabulated size satisfies every requested check");
        return Result(label, tables, status, oos, inA, cf, required, checks, selected, cands, factors);
    }

    private static CableSizingResult Result(string label, CableTables t, string status, IReadOnlyList<string> oos, double? inA, double? cf, double? req,
        IReadOnlyList<SizingCheck> checks, double? sel, IReadOnlyList<SizingCandidate> cands, IReadOnlyList<string> factors) =>
        new(Method, label, status, oos, t.Id, t.Status, t.Provenance, inA, cf, req, checks, sel, cands, factors, Awaiting);
}
