namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the demand editor (v1 demand.json, v2 network demands).
//
// Derived read-outs (display only, never written back):
//   Power:
//     Reactive power used (Mvar) = qMvar if written; else from the power factor (written, or the loader's default):
//                                  Q = P * sqrt(1 - pf^2) / pf   (lagging, so Q >= 0 for P >= 0)
//     Apparent power (MVA)       S = sqrt(P^2 + Q^2)
//     Power factor               = P / S (when S > 0)

/// <summary>The per-element editor of a demand (v1 demand.json, v2 network demands).</summary>
public sealed class DemandEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and bus", Power = "Power";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "bus"),
        new(Power, "pMw", "qMvar", "powerFactor"),
    };

    internal DemandEditorModel(EditorContext c) : base(c, "Demand", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        if (section != Power) return list;
        (double? p, string pWhy) = c.Num("pMw");
        const string qL = "Reactive power used";
        double? q = null;
        if (c.Written("qMvar"))
        {
            (double? qw, string qWhy) = c.Num("qMvar");
            q = qw;
            list.Add(qw is double v ? DerivedReadout.Of(qL, v, "Mvar", "qMvar as written") : DerivedReadout.NotShown(qL, "Mvar", qWhy, "qMvar as written"));
        }
        else
        {
            (double? pf, string pfWhy) = c.Num("powerFactor");
            const string f = "P * sqrt(1 - pf^2) / pf (qMvar absent)";
            if (p is double pp && pf is double k && k > 0 && k <= 1)
            {
                // Q from P and a lagging power factor: tan(acos(pf)) = sqrt(1 - pf^2) / pf (no transcendental needed).
                q = pp * Math.Sqrt(1.0 - k * k) / k;
                list.Add(DerivedReadout.Of(qL, q.Value, "Mvar", f));
            }
            else list.Add(DerivedReadout.NotShown(qL, "Mvar", p is null ? pWhy : pf is null ? pfWhy : "powerFactor is outside (0, 1]", f));
        }
        if (p is double P && q is double Q)
        {
            double s = Math.Sqrt(P * P + Q * Q);
            list.Add(DerivedReadout.Of("Apparent power", s, "MVA", "sqrt(P^2 + Q^2)"));
            list.Add(s > 0 ? DerivedReadout.Of("Power factor", P / s, "", "P / S") : DerivedReadout.NotShown("Power factor", "", "S is 0", "P / S"));
        }
        return list;
    }
}
