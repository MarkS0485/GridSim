using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the two-winding transformer editor (plan: "2W transformer (windings, neutral X)"). One layout
// for v1 (transformer.json) and v2 (network.json transformers); v2 adds windings, neutralXPu, r0Pu/x0Pu, type, template
// and inService (type and inService LOCKED with the v2 loader's reason). The windings and neutralXPu blocks, when written,
// show their own fields (from / to) under the block's row.
//
// Derived read-outs (display only, never written back):
//   Windings and ratings:
//     Vector group (connections only, no clock number): from side upper case, to side lower case:
//       Delta -> D / d, WyeGrounded -> YN / yn, WyeUngrounded -> Y / y (for example "YNd").
//     Rated current, from side (kA)        = ratingMva / (sqrt(3) * baseKv(from))
//     Rated current, to side (kA)          = ratingMva / (sqrt(3) * baseKv(to))
//   Impedance:
//     X/R ratio                            = xPu / rPu
//     Resistance on own rating (pu)        = rPu * ratingMva / baseMva
//     Reactance on own rating (pu)         = xPu * ratingMva / baseMva
//     Impedance on own rating (percent)    = 100 * sqrt(rPu^2 + xPu^2) * ratingMva / baseMva
//     Series impedance referred to the from side (ohm) = sqrt(rPu^2 + xPu^2) * baseKv(from)^2 / baseMva
//   Tap and phase shift:
//     Tap off nominal (percent)            = (tapRatio - 1) * 100
//   Neutral earthing:
//     Neutral reactance, from side (ohm)   = neutralXPu.from * baseKv(from)^2 / baseMva
//     Neutral reactance, to side (ohm)     = neutralXPu.to * baseKv(to)^2 / baseMva

/// <summary>The per-element editor of a two-winding transformer (v1 transformer.json, v2 network transformers).</summary>
public sealed class TransformerEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and ends", Windings = "Windings and ratings", Impedance = "Impedance (per unit on the case base)",
        Tap = "Tap and phase shift", Neutral = "Neutral earthing", Service = "Type, template and service";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "from", "to"),
        new(Windings, "windings", "ratingMva", "ratingBMva", "ratingCMva"),
        new(Impedance, "rPu", "xPu", "r0Pu", "x0Pu"),
        new(Tap, "tapRatio", "phaseShiftDeg"),
        new(Neutral, "neutralXPu"),
        new(Service, "type", "template", "dynamicsClass", "inService"),
    };

    internal TransformerEditorModel(EditorContext c) : base(c, "Two-winding transformer", Layout, Derive) { }

    /// <summary>The vector-group letters of a winding connection (from side upper case), or null for another value.</summary>
    public static string? Letters(string connection, bool fromSide) => (connection, fromSide) switch
    {
        ("Delta", true) => "D",
        ("Delta", false) => "d",
        ("WyeGrounded", true) => "YN",
        ("WyeGrounded", false) => "yn",
        ("WyeUngrounded", true) => "Y",
        ("WyeUngrounded", false) => "y",
        _ => null,
    };

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        (double? r, string rWhy) = c.Num("rPu");
        (double? x, string xWhy) = c.Num("xPu");
        (double? rating, string ratingWhy) = c.Num("ratingMva");
        (double? kvFrom, string kvFromWhy) = c.BusKv("from");
        (double? kvTo, string kvToWhy) = c.BusKv("to");
        (double? mva, string mvaWhy) = c.BaseMva();
        const string sqrt3 = "sqrt(3)";
        if (section == Windings)
        {
            if (c.Obj("windings") is JsonSourceNode w)
            {
                string? from = RecordRead.Str(w, "from", c.Doc.KeyComparer), to = RecordRead.Str(w, "to", c.Doc.KeyComparer);
                string? lf = from is null ? null : Letters(from, true), lt = to is null ? null : Letters(to, false);
                list.Add(lf is not null && lt is not null
                    ? DerivedReadout.Words("Vector group (connections, no clock number)", lf + lt, "windings.from -> D / YN / Y, windings.to -> d / yn / y")
                    : DerivedReadout.Words("Vector group (connections, no clock number)", "n/a: windings.from and windings.to must both be written", "windings.from, windings.to"));
            }
            else if (c.IsV2)
                list.Add(DerivedReadout.Words("Vector group (connections, no clock number)", "n/a: windings absent (the fault studies then have no earthed neutral from this unit)", "windings.from, windings.to"));
            Current(list, "Rated current, from side", rating, ratingWhy, kvFrom, kvFromWhy, "ratingMva / (" + sqrt3 + " * baseKv(from))");
            Current(list, "Rated current, to side", rating, ratingWhy, kvTo, kvToWhy, "ratingMva / (" + sqrt3 + " * baseKv(to))");
        }
        else if (section == Impedance)
        {
            list.Add(r is double r0 && x is double x0
                ? r0 != 0 ? DerivedReadout.Of("X/R ratio", x0 / r0, "", "xPu / rPu") : DerivedReadout.NotShown("X/R ratio", "", "rPu is 0", "xPu / rPu")
                : DerivedReadout.NotShown("X/R ratio", "", r is null ? rWhy : xWhy, "xPu / rPu"));
            if (r is not double rr || x is not double xx) return list;
            if (rating is double sr && sr > 0 && mva is double s)
            {
                // Change of MVA base: z(own) = z(case) * S(own) / S(case), the same kV base on both.
                list.Add(DerivedReadout.Of("Resistance on own rating", rr * sr / s, "pu", "rPu * ratingMva / baseMva"));
                list.Add(DerivedReadout.Of("Reactance on own rating", xx * sr / s, "pu", "xPu * ratingMva / baseMva"));
                list.Add(DerivedReadout.Of("Impedance on own rating", 100.0 * Math.Sqrt(rr * rr + xx * xx) * sr / s, "%", "100 * sqrt(rPu^2 + xPu^2) * ratingMva / baseMva"));
            }
            else
                list.Add(DerivedReadout.NotShown("Impedance on own rating", "%", rating is null ? ratingWhy : rating <= 0 ? "ratingMva is 0 (unrated)" : mvaWhy,
                    "100 * sqrt(rPu^2 + xPu^2) * ratingMva / baseMva"));
            // Ohms referred to the from side: |z| * Zb(from), Zb = kV^2 / MVA.
            list.Add(kvFrom is double kf && mva is double s2
                ? DerivedReadout.Of("Series impedance referred to the from side", Math.Sqrt(rr * rr + xx * xx) * kf * kf / s2, "ohm", "sqrt(rPu^2 + xPu^2) * baseKv(from)^2 / baseMva")
                : DerivedReadout.NotShown("Series impedance referred to the from side", "ohm", kvFrom is null ? kvFromWhy : mvaWhy, "sqrt(rPu^2 + xPu^2) * baseKv(from)^2 / baseMva"));
        }
        else if (section == Tap)
        {
            (double? tap, string tapWhy) = c.Num("tapRatio");
            list.Add(tap is double t
                ? DerivedReadout.Of("Tap off nominal", (t - 1.0) * 100.0, "%", "(tapRatio - 1) * 100")
                : DerivedReadout.NotShown("Tap off nominal", "%", tapWhy, "(tapRatio - 1) * 100"));
        }
        else if (section == Neutral && c.Obj("neutralXPu") is JsonSourceNode nx)
        {
            Neutral1(list, c, nx, "from", kvFrom, kvFromWhy, mva, mvaWhy);
            Neutral1(list, c, nx, "to", kvTo, kvToWhy, mva, mvaWhy);
        }
        return list;
    }

    // I = S / (sqrt(3) * V): kA from MVA and kV (line-to-line).
    private static void Current(List<DerivedReadout> list, string label, double? rating, string ratingWhy, double? kv, string kvWhy, string formula)
    {
        list.Add(rating is double s && kv is double k && k > 0
            ? DerivedReadout.Of(label, s / (Math.Sqrt(3.0) * k), "kA", formula)
            : DerivedReadout.NotShown(label, "kA", rating is null ? ratingWhy : kv is null ? kvWhy : "baseKv is not above 0", formula));
    }

    // Neutral reactance in ohms on one side: X(pu) * kV(side)^2 / MVA.
    private static void Neutral1(List<DerivedReadout> list, EditorContext c, JsonSourceNode nx, string side, double? kv, string kvWhy, double? mva, string mvaWhy)
    {
        if (RecordRead.Member(nx, side, c.Doc.KeyComparer) is not { Kind: JsonSourceKind.Number, NumberValue: double xn }) return;
        string label = "Neutral reactance, " + side + " side";
        string formula = "neutralXPu." + side + " * baseKv(" + side + ")^2 / baseMva";
        list.Add(kv is double k && mva is double s
            ? DerivedReadout.Of(label, xn * k * k / s, "ohm", formula)
            : DerivedReadout.NotShown(label, "ohm", kv is null ? kvWhy : mvaWhy, formula));
    }
}
