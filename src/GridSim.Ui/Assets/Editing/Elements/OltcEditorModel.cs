using GridSim.Core.IO.Json.CaseEditing;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the on-load tap changer editor (v1 onloadtapchanger.json, v2 network oltcs). The transformer
// is a picker over the case's branch names (the engine's branch reference field), the controlled bus a bus picker.
//
// Derived read-outs (display only, never written back):
//   Transformer and controlled bus:
//     Drives                         the branch the engine's reference index maps this OLTC to (ReferenceIndex.OltcBranch,
//                                    the file-level mirror of SteadyStateSolver.MapOltcRecordsToBranches), or none
//   Voltage control:
//     Target voltage (kV)            = targetVpu * baseKv(controlsBus)
//     Deadband (kV)                  = deadbandPu * baseKv(controlsBus)
//   Tap range:
//     Tap positions                  = round((maxTap - minTap) / stepPu) + 1   (rounded half away from zero)
//     Lowest tap off nominal (%)     = (minTap - 1) * 100
//     Highest tap off nominal (%)    = (maxTap - 1) * 100

/// <summary>The per-element editor of an on-load tap changer (v1 onloadtapchanger.json, v2 network oltcs).</summary>
public sealed class OltcEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Target = "Transformer and controlled bus", Control = "Voltage control", Range = "Tap range";

    internal static readonly SectionSpec[] Layout =
    {
        new(Target, "transformer", "controlsBus"),
        new(Control, "targetVpu", "deadbandPu"),
        new(Range, "minTap", "maxTap", "stepPu"),
    };

    internal OltcEditorModel(EditorContext c) : base(c, "On-load tap changer", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        (double? kv, string kvWhy) = c.BusKv("controlsBus");
        if (section == Target)
        {
            // The branch this OLTC drives, by the engine's own mapping (aligned to ReferenceIndex.Oltcs).
            int at = -1;
            for (int i = 0; i < c.Index.Oltcs.Count; i++)
                if (c.Index.Oltcs[i] == c.Grid.Record.ToRecordRef()) { at = i; break; }
            RecordRef? branch = at >= 0 ? c.Index.OltcBranch[at] : null;
            list.Add(branch is RecordRef b
                ? DerivedReadout.Words("Drives", c.Session.Documents[b.Document].FileName + " " + b.Pointer + " (" + (c.Session.CollectionAt(b.Document, b.ArrayPointer)?.Role ?? "branch") + ")",
                    "the first branch of the name with the controlled bus at an end, else the first of the name")
                : DerivedReadout.Words("Drives", "n/a: no line or transformer carries this name", "the first branch of the name"));
        }
        else if (section == Control)
        {
            (double? t, string tWhy) = c.Num("targetVpu");
            (double? d, string dWhy) = c.Num("deadbandPu");
            list.Add(t is double tv && kv is double k1 ? DerivedReadout.Of("Target voltage", tv * k1, "kV", "targetVpu * baseKv(controlsBus)")
                : DerivedReadout.NotShown("Target voltage", "kV", t is null ? tWhy : kvWhy, "targetVpu * baseKv(controlsBus)"));
            list.Add(d is double dv && kv is double k2 ? DerivedReadout.Of("Deadband", dv * k2, "kV", "deadbandPu * baseKv(controlsBus)")
                : DerivedReadout.NotShown("Deadband", "kV", d is null ? dWhy : kvWhy, "deadbandPu * baseKv(controlsBus)"));
        }
        else if (section == Range)
        {
            (double? lo, string loWhy) = c.Num("minTap");
            (double? hi, string hiWhy) = c.Num("maxTap");
            (double? st, string stWhy) = c.Num("stepPu");
            const string f = "round((maxTap - minTap) / stepPu) + 1";
            list.Add(lo is double a && hi is double b && st is double s && s > 0
                ? DerivedReadout.Of("Tap positions", Math.Round((b - a) / s, MidpointRounding.AwayFromZero) + 1.0, "", f)
                : DerivedReadout.NotShown("Tap positions", "", lo is null ? loWhy : hi is null ? hiWhy : st is null ? stWhy : "stepPu is not above 0", f));
            if (lo is double a2) list.Add(DerivedReadout.Of("Lowest tap off nominal", (a2 - 1.0) * 100.0, "%", "(minTap - 1) * 100"));
            if (hi is double b2) list.Add(DerivedReadout.Of("Highest tap off nominal", (b2 - 1.0) * 100.0, "%", "(maxTap - 1) * 100"));
        }
        return list;
    }
}
