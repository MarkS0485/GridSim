namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the line editor (plan: "line (ohl/cable, r0/x0, circuits, ratings)"). One layout for v1
// (transmission.json) and v2 (network.json lines): a member the record's schema does not have is skipped (v1 rPu0/xPu0
// and dynamicsClass, v2 r0Pu/x0Pu, template, type and inService). v2 type and inService are LOCKED with the v2 loader's
// reason (LockedFields), shown in the last section.
//
// Derived read-outs (display only, never written back; double precision; the values as written, or the loader's default
// where the field is absent and its default is a plain value):
//   Electrical:
//     X/R ratio                                      = xPu / rPu                       (n/a when rPu is 0)
//     Base impedance at the from bus (ohm)           Zb = baseKv(from)^2 / baseMva
//     Series resistance per circuit (ohm)            = rPu * Zb
//     Series reactance per circuit (ohm)             = xPu * Zb
//     Series impedance magnitude per circuit (ohm)   = sqrt(rPu^2 + xPu^2) * Zb
//     Series impedance, all circuits in parallel     = sqrt(rPu^2 + xPu^2) * Zb / circuits
//     Charging susceptance per circuit (uS)          = bPu / Zb * 1e6
//     Charging at 1 pu, all circuits (Mvar)          = bPu * circuits * baseMva
//     Reactance per km (ohm/km)                      = xPu * Zb / lengthKm           (only when lengthKm > 0)
//     a note when the two ends' base voltages differ (the ohms use the from bus).
//   Ratings:
//     Rating A, all circuits (MVA)                   = ratingMva * circuits
//     Rated current per circuit (kA)                 = ratingMva / (sqrt(3) * baseKv(from))

/// <summary>The per-element editor of a line or cable (v1 transmission.json, v2 network lines).</summary>
public sealed class LineEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and ends", Electrical = "Electrical (per unit on the case base, one circuit)",
        Ratings = "Ratings (one circuit)", TransformerBranch = "Converted transformer branch", Service = "Type, template and service";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "from", "to", "equipment", "lengthKm"),
        new(Electrical, "rPu", "xPu", "bPu", "circuits", "r0Pu", "x0Pu", "rPu0", "xPu0"),
        new(Ratings, "ratingMva", "ratingBMva", "ratingCMva"),
        new(TransformerBranch, "isTransformer", "tapRatio", "phaseShiftDeg"),
        new(Service, "type", "template", "dynamicsClass", "inService"),
    };

    internal LineEditorModel(EditorContext c) : base(c, "Line", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        (double? r, string rWhy) = c.Num("rPu");
        (double? x, string xWhy) = c.Num("xPu");
        (double? b, string bWhy) = c.Num("bPu");
        (double? n, string nWhy) = c.Num("circuits");
        (double? kv, string kvWhy) = c.BusKv("from");
        (double? mva, string mvaWhy) = c.BaseMva();
        string baseText = "baseKv of the from bus, system base MVA (" + mvaWhy + ")";
        if (section == Electrical)
        {
            const string xr = "X/R ratio";
            list.Add(r is double r0 && x is double x0
                ? r0 != 0 ? DerivedReadout.Of(xr, x0 / r0, "", "xPu / rPu") : DerivedReadout.NotShown(xr, "", "rPu is 0", "xPu / rPu")
                : DerivedReadout.NotShown(xr, "", r is null ? rWhy : xWhy, "xPu / rPu"));

            const string zb = "Base impedance at the from bus";
            if (kv is not double k || mva is not double s)
            {
                string why = kv is null ? kvWhy : mvaWhy;
                list.Add(DerivedReadout.NotShown(zb, "ohm", why, "Zb = baseKv(from)^2 / baseMva"));
                return list;
            }
            // Zb = kV^2 / MVA (ohm): the per-unit impedance base at the from bus on the system MVA base.
            double zBase = k * k / s;
            list.Add(DerivedReadout.Of(zb, zBase, "ohm", "Zb = baseKv(from)^2 / baseMva = " + EditorContext.Show(k) + "^2 / " + EditorContext.Show(s) + "; " + baseText));
            if (r is double rr && x is double xx)
            {
                // Ohms from per unit: R = rPu * Zb, X = xPu * Zb, |Z| = sqrt(rPu^2 + xPu^2) * Zb (one circuit).
                double zMag = Math.Sqrt(rr * rr + xx * xx) * zBase;
                list.Add(DerivedReadout.Of("Series resistance per circuit", rr * zBase, "ohm", "rPu * Zb"));
                list.Add(DerivedReadout.Of("Series reactance per circuit", xx * zBase, "ohm", "xPu * Zb"));
                list.Add(DerivedReadout.Of("Series impedance per circuit", zMag, "ohm", "sqrt(rPu^2 + xPu^2) * Zb"));
                list.Add(n is double nc && nc > 0
                    ? DerivedReadout.Of("Series impedance, all circuits in parallel", zMag / nc, "ohm", "sqrt(rPu^2 + xPu^2) * Zb / circuits")
                    : DerivedReadout.NotShown("Series impedance, all circuits in parallel", "ohm", n is null ? nWhy : "circuits is not above 0", "sqrt(rPu^2 + xPu^2) * Zb / circuits"));
                (double? len, _) = c.Num("lengthKm");
                if (len is double km && km > 0)
                    list.Add(DerivedReadout.Of("Reactance per km", xx * zBase / km, "ohm/km", "xPu * Zb / lengthKm"));
            }
            if (b is double bb)
            {
                // Siemens from per unit: B = bPu / Zb; shown in microsiemens. Charging at 1 pu: Q = bPu * circuits * baseMva.
                list.Add(DerivedReadout.Of("Charging susceptance per circuit", bb / zBase * 1e6, "uS", "bPu / Zb * 1e6"));
                if (n is double nc2)
                    list.Add(DerivedReadout.Of("Charging at 1 pu, all circuits", bb * nc2 * s, "Mvar", "bPu * circuits * baseMva"));
            }
            else list.Add(DerivedReadout.NotShown("Charging susceptance per circuit", "uS", bWhy, "bPu / Zb * 1e6"));
            (double? kvTo, _) = c.BusKv("to");
            if (kvTo is double kt && kt != k)
                list.Add(DerivedReadout.Words("Ends at different base voltages",
                    "from bus " + EditorContext.Show(k) + " kV, to bus " + EditorContext.Show(kt) + " kV: the ohms above use the from bus",
                    "baseKv(from) != baseKv(to)"));
        }
        else if (section == Ratings)
        {
            (double? a, string aWhy) = c.Num("ratingMva");
            list.Add(a is double ra && n is double nc
                ? DerivedReadout.Of("Rating A, all circuits", ra * nc, "MVA", "ratingMva * circuits")
                : DerivedReadout.NotShown("Rating A, all circuits", "MVA", a is null ? aWhy : nWhy, "ratingMva * circuits"));
            const string cur = "Rated current per circuit";
            const string curF = "ratingMva / (sqrt(3) * baseKv(from))";
            // I = S / (sqrt(3) * V): kA from MVA and kV (line-to-line).
            list.Add(a is double ra2 && kv is double k2 && k2 > 0
                ? DerivedReadout.Of(cur, ra2 / (Math.Sqrt(3.0) * k2), "kA", curF)
                : DerivedReadout.NotShown(cur, "kA", a is null ? aWhy : kv is null ? kvWhy : "baseKv is not above 0", curF));
        }
        return list;
    }
}
