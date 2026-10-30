using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the boundary editor (v1 boundaries.json, v2 network boundaries). The branches list is the
// block's own row (the whole list as JSON: add or remove an entry there) and, under it, each entry's fields (branch: a
// picker over the case's branch names; direction: 1 or -1).
//
// Derived read-outs (display only, never written back):
//   Branches:
//     Branches listed                  = the number of entries
//     Sum of the named branches' A ratings (MVA) = sum over the entries of ratingMva * circuits (a line; circuits absent
//                                        = 1) or ratingMva (a transformer), each branch the first of its name; n/a when an
//                                        entry names no branch or a named branch has no ratingMva written
//     Limit as a share of that sum (%) = limitMw / sum * 100

/// <summary>The per-element editor of a transfer boundary (v1 boundaries.json, v2 network boundaries).</summary>
public sealed class BoundaryEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and limit", Branches = "Branches";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "limitMw"),
        new(Branches, "branches"),
    };

    internal BoundaryEditorModel(EditorContext c) : base(c, "Boundary", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        if (section != Branches) return list;
        JsonSourceNode? arr = RecordRead.Member(c.Node, "branches", c.Doc.KeyComparer);
        if (arr is not { Kind: JsonSourceKind.Array })
        {
            list.Add(DerivedReadout.Words("Branches listed", "none (branches absent)", "the number of entries"));
            return list;
        }
        list.Add(DerivedReadout.Of("Branches listed", arr.Elements.Count, "", "the number of entries"));
        const string sumL = "Sum of the named branches' A ratings";
        const string sumF = "sum of ratingMva * circuits (line) or ratingMva (transformer)";
        double sum = 0;
        string? why = null;
        foreach (JsonSourceNode e in arr.Elements)
        {
            string? name = e.Kind == JsonSourceKind.Object ? RecordRead.Str(e, "branch", c.Doc.KeyComparer) : null;
            if (name is null) { why = "an entry has no branch name"; break; }
            (JsonSourceNode? b, string? role, CaseDocument? d) = c.Branch(name);
            if (b is null || d is null) { why = "no line or transformer is named '" + name + "'"; break; }
            if (RecordRead.Num(b, "ratingMva", d.KeyComparer) is not double rating) { why = "'" + name + "' has no ratingMva written"; break; }
            double circuits = role == CaseRoles.Lines ? RecordRead.Num(b, "circuits", d.KeyComparer) ?? 1.0 : 1.0;
            sum += rating * circuits;
        }
        if (why is not null) { list.Add(DerivedReadout.NotShown(sumL, "MVA", why, sumF)); return list; }
        list.Add(DerivedReadout.Of(sumL, sum, "MVA", sumF));
        (double? limit, string limitWhy) = c.Num("limitMw");
        list.Add(limit is double l && sum > 0
            ? DerivedReadout.Of("Limit as a share of that sum", l / sum * 100.0, "%", "limitMw / sum * 100")
            : DerivedReadout.NotShown("Limit as a share of that sum", "%", limit is null ? limitWhy : "the sum is 0", "limitMw / sum * 100"));
        return list;
    }
}
