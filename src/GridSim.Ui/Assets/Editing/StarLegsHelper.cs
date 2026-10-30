using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Overlay;

namespace GridSim.Ui.Assets.Editing;

// =====================================================================================================================
// UI programme Phase 5f (2026-10-20; plan Phase 5 "3W transformer: transformer3W over the existing star bus + 2W legs.
// Decided: the editor offers a 'derive star legs' helper from the pairwise nameplate impedances (Z1 = (Z12 + Z13 - Z23)/2
// and cyclic, all referred to a common MVA base). It writes the 2W leg r/x via ordinary CaseOps edits shown before they
// are applied. The authored leg values remain what the solver uses.").
//
// THE MATHS (StarLegsMath, pure, double precision, deterministic: only +, -, *, / in a fixed, stated order, no fused
// multiply-add, no library maths):
//
//   1. Refer each pairwise impedance to ONE common MVA base. The per-unit referral formula for a change of base is
//          Z_new = Z_old * (S_new / S_old) * (V_old / V_new)^2
//      and between the pairwise values the voltage base is the same winding's rating on both sides, so only the MVA
//      term remains: Z_ij(common) = Z_ij * (S_common / S_ij). Evaluated as k = S_common / S_ij, then R * k and X * k.
//      The transformer3W kind spec (schemas/v2/kinds/overlay/transformer3W.kind.json) states that the pairwise
//      impedances r12Pu ... x23Pu are ALL on its baseMva, so for a record S_ij = S_common = baseMva and k is exactly 1;
//      the referral step is still made (and tested with unequal pair bases on the pure class), so a later kind with
//      per-pair bases needs no new maths. Without baseMva the bases are not given and the helper refuses, saying so.
//
//   2. The star (T) equivalent, resistance and reactance separately, all on the common base:
//          Z1 = (Z12 + Z13 - Z23) / 2     (the HV leg)
//          Z2 = (Z12 + Z23 - Z13) / 2     (the MV leg)
//          Z3 = (Z13 + Z23 - Z12) / 2     (the LV leg)
//      Evaluated as ((a + b) - c) / 2. A derived leg (most often the MV leg's reactance) can be NEGATIVE: that is
//      physically possible for a star equivalent (it is a fictitious midpoint), so it is shown and written, not refused.
//
//   3. Convert each leg to the per-unit system the case stores it in: the case's system base S_system (the manifest's
//      /system/baseMva) and the base voltage of the leg's outer bus (the end that is not the star bus):
//          Z_leg(system) = Z_leg(common) * (S_system / S_common) * (V_rated / V_base)^2
//      where V_rated is the winding's rated voltage (hvKv, mvKv, lvKv) and V_base the outer bus's baseKv. Evaluated as
//      f = S_system / S_common, v = V_rated / V_base, g = v * v, then (R * f) * g and (X * f) * g. When the record
//      gives no rated voltage for a winding, the winding is taken as rated at its bus's base voltage (g = 1, stated);
//      when it gives one but the bus has no base voltage, the helper refuses (the referral cannot be made).
//
// THE EDITS (StarLegsHelper, Author mode): the six values (rPu and xPu of the HV, MV and LV legs) are PROPOSED first,
// each old -> new, and written only when the user confirms, as ONE CaseOps transaction (CaseOps.SetFields: one undo step,
// every edit through SetField's own checks). Each new value is written shortest round-trip (JsonLiteral.FormatNumber),
// so the file holds exactly the derived double. Nothing is automatic: the solver keeps using the authored leg values,
// before and after, and a change to the overlay record never touches a leg on its own.
// =====================================================================================================================

/// <summary>A series impedance R + jX, per unit (Phase 5f).</summary>
/// <param name="R">Resistance, per unit.</param>
/// <param name="X">Reactance, per unit.</param>
public readonly record struct SeriesImpedance(double R, double X);

/// <summary>The pure maths of the star-legs helper (Phase 5f). See the file header for the formulas and their order.</summary>
public static class StarLegsMath
{
    /// <summary>
    /// Refer <paramref name="z"/> from base <paramref name="fromMva"/> to base <paramref name="toMva"/> (same voltage
    /// base): Z_new = Z_old * (S_new / S_old), evaluated as k = toMva / fromMva, then R * k and X * k.
    /// Throws for a base that is not a finite power above 0 MVA (a caller refuses before that; this is the last guard).
    /// </summary>
    public static SeriesImpedance Rebase(SeriesImpedance z, double fromMva, double toMva)
    {
        RequirePositive(fromMva, nameof(fromMva));
        RequirePositive(toMva, nameof(toMva));
        double k = toMva / fromMva;
        return new SeriesImpedance(z.R * k, z.X * k);
    }

    /// <summary>
    /// The star (T) equivalent of the pairwise impedances (all on one base): Z1 = (Z12 + Z13 - Z23) / 2,
    /// Z2 = (Z12 + Z23 - Z13) / 2, Z3 = (Z13 + Z23 - Z12) / 2, resistance and reactance separately, each evaluated as
    /// ((a + b) - c) / 2. A negative result is returned as it is (a star leg may be negative).
    /// </summary>
    public static (SeriesImpedance Z1, SeriesImpedance Z2, SeriesImpedance Z3) Star(SeriesImpedance z12, SeriesImpedance z13, SeriesImpedance z23)
    {
        static double Leg(double a, double b, double c) => ((a + b) - c) / 2.0;
        return (new SeriesImpedance(Leg(z12.R, z13.R, z23.R), Leg(z12.X, z13.X, z23.X)),
                new SeriesImpedance(Leg(z12.R, z23.R, z13.R), Leg(z12.X, z23.X, z13.X)),
                new SeriesImpedance(Leg(z13.R, z23.R, z12.R), Leg(z13.X, z23.X, z12.X)));
    }

    /// <summary>The voltage term (V_rated / V_base)^2 of the base change, evaluated as v = ratedKv / baseKv, then v * v.</summary>
    public static double VoltageFactor(double ratedKv, double baseKv)
    {
        RequirePositive(ratedKv, nameof(ratedKv));
        RequirePositive(baseKv, nameof(baseKv));
        double v = ratedKv / baseKv;
        return v * v;
    }

    /// <summary>
    /// A leg on the common base to the case's system base: Z * (S_system / S_common) * voltageFactor, evaluated as
    /// f = systemMva / commonMva, then (R * f) * voltageFactor and (X * f) * voltageFactor.
    /// </summary>
    public static SeriesImpedance ToSystem(SeriesImpedance zCommon, double commonMva, double systemMva, double voltageFactor)
    {
        RequirePositive(commonMva, nameof(commonMva));
        RequirePositive(systemMva, nameof(systemMva));
        RequirePositive(voltageFactor, nameof(voltageFactor));
        double f = systemMva / commonMva;
        return new SeriesImpedance((zCommon.R * f) * voltageFactor, (zCommon.X * f) * voltageFactor);
    }

    private static void RequirePositive(double v, string name)
    {
        if (!(double.IsFinite(v) && v > 0))
            throw new ArgumentOutOfRangeException(name, v, "must be a finite number above 0.");
    }
}

/// <summary>One authored leg of a three-winding transformer as the helper reads it (from the case, or the model).</summary>
/// <param name="Role">"hv", "mv" or "lv".</param>
/// <param name="Transformer">The leg transformer's name.</param>
/// <param name="From">Its from bus id.</param>
/// <param name="To">Its to bus id.</param>
/// <param name="R">Its authored resistance, per unit on the system base.</param>
/// <param name="X">Its authored reactance, per unit on the system base.</param>
/// <param name="OuterBaseKv">The base voltage of its outer bus (the end that is not the star bus), or null when the bus
/// gives none (or is not found).</param>
public sealed record StarLegCase(string Role, string Transformer, int From, int To, double R, double X, double? OuterBaseKv)
{
    /// <summary>The leg's outer bus: the end that is not the star bus (From when neither end is).</summary>
    public int OuterBus(int starBus) => From == starBus ? To : From;
}

/// <summary>One leg of a derivation: the authored values and the derived ones (both on the system base).</summary>
/// <param name="Role">"hv", "mv" or "lv".</param>
/// <param name="Transformer">The leg transformer's name.</param>
/// <param name="Authored">The authored r and x (what the solver uses).</param>
/// <param name="OnCommon">The derived star leg on the common base.</param>
/// <param name="Derived">The derived star leg on the case's system base (what the helper proposes to write).</param>
/// <param name="VoltageFactor">The voltage term (V_rated / V_base)^2 used (1 when no rated voltage was given).</param>
/// <param name="VoltageNote">How the voltage term was found, in words.</param>
public sealed record StarLegResult(string Role, string Transformer, SeriesImpedance Authored, SeriesImpedance OnCommon, SeriesImpedance Derived,
    double VoltageFactor, string VoltageNote);

/// <summary>
/// The outcome of deriving the star legs of one transformer3W record: the legs (HV, MV, LV) or the reason it was refused,
/// with the bases and the formulas used, in words.
/// </summary>
public sealed record StarLegsDerivation(string? Refusal, IReadOnlyList<StarLegResult> Legs, double CommonMva, double SystemMva, IReadOnlyList<string> Lines)
{
    /// <summary>True when the legs were derived.</summary>
    public bool Ok => Refusal is null;

    internal static StarLegsDerivation Refused(string why) => new(why, Array.Empty<StarLegResult>(), 0, 0, Array.Empty<string>());
}

/// <summary>One proposed edit: one leg value, old -> new.</summary>
/// <param name="Role">"hv", "mv" or "lv".</param>
/// <param name="Transformer">The leg transformer's name.</param>
/// <param name="Record">The leg's case record.</param>
/// <param name="Field">"rPu" or "xPu".</param>
/// <param name="OldText">The value as the file writes it now.</param>
/// <param name="NewText">The value to write (shortest round-trip of <paramref name="NewValue"/>).</param>
/// <param name="OldValue">The value now.</param>
/// <param name="NewValue">The derived value.</param>
public sealed record StarLegEdit(string Role, string Transformer, CaseRef Record, string Field, string OldText, string NewText, double OldValue, double NewValue)
{
    /// <summary>"HV leg 'T2' rPu: 0.0017 -> 0.0005" (the line the confirmation lists).</summary>
    public string Text => Role.ToUpperInvariant() + " leg '" + Transformer + "' " + Field + ": " + OldText + " -> " + NewText
        + (BitConverter.DoubleToInt64Bits(OldValue) == BitConverter.DoubleToInt64Bits(NewValue) ? "  (unchanged)" : "")
        + (NewValue < 0 ? "  (negative: possible for a star leg; written as derived)" : "");
}

/// <summary>
/// What "Derive star legs" proposes for one transformer3W record (Author mode): the derivation and the six edits, or why
/// it cannot propose any. Made for one revision of the session; applying it after any other change is refused.
/// </summary>
/// <param name="OverlayId">The transformer3W record's id.</param>
/// <param name="Derivation">The maths, with its bases and formulas.</param>
/// <param name="Edits">The six edits (HV r, HV x, MV r, MV x, LV r, LV x), empty when refused.</param>
/// <param name="Revision">The session revision the proposal was made at.</param>
public sealed record StarLegsProposal(string OverlayId, StarLegsDerivation Derivation, IReadOnlyList<StarLegEdit> Edits, long Revision)
{
    /// <summary>Why nothing can be proposed, or null.</summary>
    public string? Refusal => Derivation.Refusal;

    /// <summary>True when there are edits to confirm.</summary>
    public bool CanApply => Derivation.Ok && Edits.Count > 0;

    /// <summary>The edits' lines, in order.</summary>
    public IReadOnlyList<string> EditLines => Edits.Select(e => e.Text).ToList();
}

/// <summary>
/// The star-legs helper of a transformer3W record (Phase 5f): derive (pure, from the record's nameplate and the legs as
/// the case holds them), propose (Author mode: the six edits, old -> new), apply on confirm (one undo step).
/// </summary>
public static class StarLegsHelper
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The roles of the three legs, in order, with their rated-voltage parameter and their pairwise sides.</summary>
    public static IReadOnlyList<(string Role, string KvParam)> Roles { get; } = new[] { ("hv", "hvKv"), ("mv", "mvKv"), ("lv", "lvKv") };

    /// <summary>The six pairwise parameters the derivation needs.</summary>
    public static IReadOnlyList<string> PairwiseParams { get; } = new[] { "r12Pu", "x12Pu", "r13Pu", "x13Pu", "r23Pu", "x23Pu" };

    private static string N(double v) => JsonLiteral.FormatNumber(v);

    /// <summary>
    /// Derive the star legs of <paramref name="record"/> (a transformer3W record) over the legs as the case or model
    /// holds them (<paramref name="legs"/>: one per role, null when the role's leg is not found, with the reason in
    /// <paramref name="legProblems"/>), the star bus <paramref name="starBus"/> and the case's system base
    /// <paramref name="systemMva"/>. Refuses, with every reason, when a base or a pairwise value is missing, a leg is
    /// missing or does not touch the star bus, or a voltage referral cannot be made.
    /// </summary>
    public static StarLegsDerivation Derive(AssetOverlayRecord record, int? starBus, IReadOnlyList<StarLegCase?> legs,
        IReadOnlyList<string> legProblems, double? systemMva)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(legProblems);
        if (record.Kind != "transformer3W")
            return StarLegsDerivation.Refused($"Derive star legs refused: '{record.Id}' is a {record.Kind} record, not a transformer3W, so there are no star legs to derive. Nothing was changed.");
        if (legs.Count != Roles.Count) throw new ArgumentException($"expected {Roles.Count} legs (hv, mv, lv), got {legs.Count}.", nameof(legs));

        var why = new List<string>();
        // The common base: the kind spec puts every pairwise impedance on baseMva, so baseMva is the base of each pair
        // AND the common base. Without it the bases are not given.
        double? baseMva = OverlayKindPages.Number(record, "baseMva");
        if (baseMva is not double sc || !double.IsFinite(sc) || !(sc > 0))
            why.Add("the MVA base of the pairwise impedances is not given: the transformer3W kind spec says r12Pu ... x23Pu are on baseMva, and baseMva is "
                + (baseMva is null ? "absent" : "not a finite power above 0 MVA (" + N(baseMva.Value) + ")"));
        var missing = PairwiseParams.Where(p => OverlayKindPages.Number(record, p) is not double v || !double.IsFinite(v)).ToList();
        if (missing.Count > 0) why.Add("the pairwise impedance" + (missing.Count == 1 ? " " : "s ") + string.Join(", ", missing) + (missing.Count == 1 ? " is" : " are") + " not given");
        if (systemMva is not double ss || !double.IsFinite(ss) || !(ss > 0))
            why.Add("the case's system base (/system/baseMva) is not a finite power above 0 MVA");
        if (starBus is null) why.Add("the star bus (reference 'starBus') is not given");
        why.AddRange(legProblems);
        if (starBus is int sb)
            foreach (StarLegCase? leg in legs)
            {
                if (leg is null) continue;
                if (leg.From != sb && leg.To != sb)
                    why.Add($"the {leg.Role.ToUpperInvariant()} leg '{leg.Transformer}' ({leg.From.ToString(Inv)} -> {leg.To.ToString(Inv)}) does not touch the star bus {sb.ToString(Inv)}");
                else if (leg.From == sb && leg.To == sb)
                    why.Add($"the {leg.Role.ToUpperInvariant()} leg '{leg.Transformer}' has the star bus {sb.ToString(Inv)} at both ends");
            }
        var names = legs.Where(l => l is not null).Select(l => l!.Transformer).ToList();
        foreach (string dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            why.Add($"transformer '{dup}' is named by more than one leg role");

        // The voltage term of each leg (needs the outer bus's base voltage when a rated voltage is given).
        var factors = new double[Roles.Count];
        var notes = new string[Roles.Count];
        for (int i = 0; i < Roles.Count; i++)
        {
            StarLegCase? leg = legs[i];
            double? rated = OverlayKindPages.Number(record, Roles[i].KvParam);
            if (leg is null || starBus is not int star) continue;
            int outer = leg.OuterBus(star);
            if (rated is null)
            {
                factors[i] = 1.0;
                notes[i] = Roles[i].KvParam + " not given: the winding is taken as rated at the base voltage of bus " + outer.ToString(Inv) + " (voltage factor 1)";
            }
            else if (!(double.IsFinite(rated.Value) && rated.Value > 0))
                why.Add($"{Roles[i].KvParam} is not a finite voltage above 0 kV ({N(rated.Value)})");
            else if (leg.OuterBaseKv is not double vb || !double.IsFinite(vb) || !(vb > 0))
                why.Add($"bus {outer.ToString(Inv)} (the outer end of the {Roles[i].Role.ToUpperInvariant()} leg) has no base voltage, so {Roles[i].KvParam} = {N(rated.Value)} kV cannot be referred to it");
            else
            {
                factors[i] = StarLegsMath.VoltageFactor(rated.Value, vb);
                notes[i] = "(" + Roles[i].KvParam + " / baseKv of bus " + outer.ToString(Inv) + ")^2 = (" + N(rated.Value) + " / " + N(vb) + ")^2 = " + N(factors[i]);
            }
        }
        if (why.Count > 0)
            return StarLegsDerivation.Refused("Derive star legs refused for '" + record.Id + "': " + string.Join("; ", why) + ". Nothing was changed.");

        double common = baseMva!.Value, system = systemMva!.Value;
        SeriesImpedance Pair(string r, string x) =>
            // Step 1: refer the pair to the common base. Its own base is baseMva (the kind spec), so k = 1 exactly.
            StarLegsMath.Rebase(new SeriesImpedance(OverlayKindPages.Number(record, r)!.Value, OverlayKindPages.Number(record, x)!.Value), common, common);
        SeriesImpedance z12 = Pair("r12Pu", "x12Pu"), z13 = Pair("r13Pu", "x13Pu"), z23 = Pair("r23Pu", "x23Pu");
        (SeriesImpedance z1, SeriesImpedance z2, SeriesImpedance z3) = StarLegsMath.Star(z12, z13, z23);
        SeriesImpedance[] onCommon = { z1, z2, z3 };
        var results = new List<StarLegResult>(Roles.Count);
        for (int i = 0; i < Roles.Count; i++)
        {
            StarLegCase leg = legs[i]!;
            SeriesImpedance derived = StarLegsMath.ToSystem(onCommon[i], common, system, factors[i]);
            if (!double.IsFinite(derived.R) || !double.IsFinite(derived.X))
                return StarLegsDerivation.Refused($"Derive star legs refused for '{record.Id}': the {leg.Role.ToUpperInvariant()} leg came out non-finite. Nothing was changed.");
            results.Add(new StarLegResult(leg.Role, leg.Transformer, new SeriesImpedance(leg.R, leg.X), onCommon[i], derived, factors[i], notes[i]));
        }
        var lines = new List<string>
        {
            "Common base: baseMva = " + N(common) + " MVA. The transformer3W kind spec states that r12Pu ... x23Pu are on baseMva, so each pair is referred by Z * (S_common / S_pair) = Z * 1.",
            "Star equivalent (r and x separately): Z1 = (Z12 + Z13 - Z23) / 2, Z2 = (Z12 + Z23 - Z13) / 2, Z3 = (Z13 + Z23 - Z12) / 2.",
            "Case base: Z_leg = Z * (S_system / S_common) * (V_rated / V_base)^2 with S_system = " + N(system) + " MVA (the case's /system/baseMva) and V_base the leg's outer bus.",
        };
        for (int i = 0; i < results.Count; i++)
            lines.Add(results[i].Role.ToUpperInvariant() + " leg '" + results[i].Transformer + "': on the common base r " + N(results[i].OnCommon.R) + ", x "
                + N(results[i].OnCommon.X) + "; voltage factor " + results[i].VoltageNote + "; on the case base r " + N(results[i].Derived.R) + ", x " + N(results[i].Derived.X) + ".");
        if (results.Any(r => r.Derived.R < 0 || r.Derived.X < 0))
            lines.Add("A derived leg value is negative: that is physically possible for a star equivalent (its midpoint is fictitious), so it is shown and written as derived, not refused.");
        lines.Add("Nothing is automatic: the solver uses the authored leg values, now and after any change to the overlay record.");
        return new StarLegsDerivation(null, results, common, system, lines);
    }

    // ===================================================================================================================
    // Run mode: the derivation over the running model (display only, the OVERVIEW of a transformer3W record)
    // ===================================================================================================================

    /// <summary>
    /// The derivation for a transformer3W overlay entry over the running model (the model's legs, buses and base): what
    /// the helper WOULD write, for display only. Nothing is written in Run mode.
    /// </summary>
    public static StarLegsDerivation DeriveOver(OverlayEntry e, GridModel model)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(model);
        AssetOverlayRecord r = e.Record;
        int? star = r.Refs.FirstOrDefault(x => x.Role == "starBus" && x.Target == AssetRefTarget.Bus)?.Bus is long b && b is >= int.MinValue and <= int.MaxValue ? (int)b : null;
        var problems = new List<string>();
        var legs = new List<StarLegCase?>();
        foreach ((string role, _) in Roles)
        {
            string? name = r.Refs.FirstOrDefault(x => x.Role == role && x.Target == AssetRefTarget.Transformer)?.Key;
            if (name is null) { problems.Add($"the {role.ToUpperInvariant()} leg (reference '{role}') is not given"); legs.Add(null); continue; }
            List<Branch> hits = model.Branches.Where(x => x.IsTransformer && string.Equals(x.Name, name, StringComparison.Ordinal)).ToList();
            if (hits.Count != 1)
            {
                problems.Add($"the {role.ToUpperInvariant()} leg '{name}' is " + (hits.Count == 0 ? "not a transformer of the running model" : $"ambiguous ({hits.Count.ToString(Inv)} transformers of the running model carry the name)"));
                legs.Add(null);
                continue;
            }
            Branch t = hits[0];
            int outer = star is int s && t.From == s ? t.To : t.From;
            Bus? ob = model.Buses.FirstOrDefault(x => x.Id == outer);
            legs.Add(new StarLegCase(role, name, t.From, t.To, t.R, t.X, ob is { BaseKv: > 0 } ? ob.BaseKv : null));
        }
        return Derive(r, star, legs, problems, model.BaseMva);
    }

    // ===================================================================================================================
    // Author mode: propose (from the session as it is now) and apply on confirm
    // ===================================================================================================================

    /// <summary>
    /// Propose the six leg edits of transformer3W record <paramref name="overlayRecord"/> from the session as it is now.
    /// Nothing is changed. A refused proposal says why, with every reason.
    /// </summary>
    public static StarLegsProposal Propose(CaseAuthoring authoring, CaseRef overlayRecord)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(overlayRecord);
        long revision = authoring.Revision;
        (string? id, string kind, _) = authoring.OverlayIdentity(overlayRecord);
        AssetOverlayRecord? rec = id is null ? null : authoring.OverlayRecordsNow().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (rec is null)
            return new StarLegsProposal(id ?? "", StarLegsDerivation.Refused("Derive star legs refused: " + authoring.Describe(overlayRecord)
                + " is not an overlay record with an id (a " + kind + ")."), Array.Empty<StarLegEdit>(), revision);

        CaseSession s = authoring.Session;
        int? star = rec.Refs.FirstOrDefault(x => x.Role == "starBus" && x.Target == AssetRefTarget.Bus)?.Bus is long b && b is >= int.MinValue and <= int.MaxValue ? (int)b : null;

        // The case's buses (id -> base kV) and transformers (name -> records), from every network file.
        var baseKv = new Dictionary<int, double?>();
        foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Buses))
            if (s.ArrayOf(c) is JsonSourceNode arr)
            {
                CaseDocument d = s.Documents[c.Document];
                foreach (JsonSourceNode bus in arr.Elements)
                    if (RecordRead.Int(bus, "id", d.KeyComparer) is int bid) baseKv.TryAdd(bid, RecordRead.Num(bus, "baseKv", d.KeyComparer));
            }
        var problems = new List<string>();
        var legs = new List<StarLegCase?>();
        var refs = new List<(CaseRef Ref, JsonSourceNode R, JsonSourceNode X, CaseDocument Doc)?>();
        foreach ((string role, _) in Roles)
        {
            string? name = rec.Refs.FirstOrDefault(x => x.Role == role && x.Target == AssetRefTarget.Transformer)?.Key;
            if (name is null) { problems.Add($"the {role.ToUpperInvariant()} leg (reference '{role}') is not given"); legs.Add(null); refs.Add(null); continue; }
            var hits = new List<(CaseRef Ref, JsonSourceNode Node, CaseDocument Doc)>();
            foreach (RecordCollection c in s.CollectionsOf(CaseRoles.Transformers))
                if (s.ArrayOf(c) is JsonSourceNode arr)
                {
                    CaseDocument d = s.Documents[c.Document];
                    for (int i = 0; i < arr.Elements.Count; i++)
                        if (string.Equals(RecordRead.Str(arr.Elements[i], "name", d.KeyComparer), name, StringComparison.Ordinal))
                            hits.Add((new CaseRef(c.Document, c.ArrayPointer, i, d.FileName), arr.Elements[i], d));
                }
            if (hits.Count != 1)
            {
                problems.Add($"the {role.ToUpperInvariant()} leg '{name}' is " + (hits.Count == 0 ? "not a transformer of the case" : $"ambiguous ({hits.Count.ToString(Inv)} transformers of the case carry the name)"));
                legs.Add(null);
                refs.Add(null);
                continue;
            }
            (CaseRef cref, JsonSourceNode node, CaseDocument doc) = hits[0];
            int? from = RecordRead.Int(node, "from", doc.KeyComparer), to = RecordRead.Int(node, "to", doc.KeyComparer);
            JsonSourceNode? rNode = RecordRead.Member(node, "rPu", doc.KeyComparer), xNode = RecordRead.Member(node, "xPu", doc.KeyComparer);
            if (from is null || to is null || rNode is not { Kind: JsonSourceKind.Number } || xNode is not { Kind: JsonSourceKind.Number })
            {
                problems.Add($"the {role.ToUpperInvariant()} leg '{name}' ({cref.Display}) does not write numeric from, to, rPu and xPu");
                legs.Add(null);
                refs.Add(null);
                continue;
            }
            int outer = star is int st && from == st ? to.Value : from.Value;
            legs.Add(new StarLegCase(role, name, from.Value, to.Value, rNode.NumberValue!.Value, xNode.NumberValue!.Value, baseKv.GetValueOrDefault(outer)));
            refs.Add((cref, rNode, xNode, doc));
        }
        double? system = SystemMva(s);
        StarLegsDerivation der = Derive(rec, star, legs, problems, system);
        if (!der.Ok) return new StarLegsProposal(rec.Id, der, Array.Empty<StarLegEdit>(), revision);

        var edits = new List<StarLegEdit>(6);
        for (int i = 0; i < Roles.Count; i++)
        {
            (CaseRef cref, JsonSourceNode rNode, JsonSourceNode xNode, CaseDocument doc) = refs[i]!.Value;
            StarLegResult leg = der.Legs[i];
            edits.Add(new StarLegEdit(leg.Role, leg.Transformer, cref, "rPu", doc.Text.RawText(rNode), N(leg.Derived.R), leg.Authored.R, leg.Derived.R));
            edits.Add(new StarLegEdit(leg.Role, leg.Transformer, cref, "xPu", doc.Text.RawText(xNode), N(leg.Derived.X), leg.Authored.X, leg.Derived.X));
        }
        return new StarLegsProposal(rec.Id, der, edits, revision);
    }

    // The case's system base: the manifest's /system/baseMva (a v2 case), else a v1 system.json's baseMva; null when
    // neither gives one.
    private static double? SystemMva(CaseSession s)
    {
        foreach (CaseDocument d in s.Documents.Where(x => x.Exists))
        {
            JsonSourceNode? sys = d.Text.Resolve("/system");
            if (sys is { Kind: JsonSourceKind.Object } && RecordRead.Num(sys, "baseMva", d.KeyComparer) is double v) return v;
            if (d.Spec?.FileName == "system.json" && RecordRead.Num(d.Text.Root, "baseMva", d.KeyComparer) is double w) return w;
        }
        return null;
    }

    /// <summary>
    /// Apply a confirmed proposal: the six leg values as ONE CaseOps transaction (one undo step). Refused, with nothing
    /// changed, when the proposal was refused or the session has changed since it was made (derive again).
    /// </summary>
    public static AuthoringResult Apply(CaseAuthoring authoring, StarLegsProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Refusal is string why) return AuthoringResult.Refused(why);
        if (proposal.Edits.Count == 0) return AuthoringResult.Refused("Derive star legs: there is nothing to apply.");
        if (proposal.Revision != authoring.Revision)
            return AuthoringResult.Refused("Derive star legs refused: the case has changed since the edits were proposed. Derive them again to see the edits for the case as it is now. Nothing was changed.");
        var edits = proposal.Edits.Select(e => new CaseOps.FieldEdit(e.Record.Document, e.Record.Pointer, e.Field, e.NewText)).ToList();
        return authoring.Apply(s => CaseOps.SetFields(s, edits, "derive star legs of '" + proposal.OverlayId + "'"));
    }
}
