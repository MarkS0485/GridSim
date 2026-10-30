using System.Globalization;
using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// The semantic checks a v2 save must pass besides the schemas (SCHEMA-EDITOR-PLAN.md section 7, step 3, v2), because
/// the save gate does not run the v2 loader (<see cref="V2CaseLoader"/>) to catch them (D-DOC-162, DOC-17: this used to
/// say there was no production v2 loader yet). Errors:
/// <list type="bullet">
/// <item>every network file the manifest lists exists, and lies inside the case folder;</item>
/// <item>bus ids are unique across all network files;</item>
/// <item>every bus reference resolves to a bus of the case;</item>
/// <item>branch names (lines and transformers together) and boundary names are unique across the case (Q4, resolved
/// to the plan's recommendation: generators may share names, as stations);</item>
/// <item>each ZIP triple sums to 1 within <see cref="JsonGridLoader.ZipSumTolerance"/>, as the v1 loader requires;</item>
/// <item>Phase 5b (2026-10-20): when the manifest lists asset overlay files, each exists inside the case folder and the
/// set passes <see cref="Overlay.V2AssetChecker"/> against the case, every problem listed.</item>
/// </list>
/// Warnings (the v1 loader only warns about these, or silently skips them): an OLTC naming no branch, a boundary
/// branch or protection target naming no branch.
/// </summary>
internal static class V2CaseChecks
{
    /// <summary>Run every check over the session's current documents.</summary>
    public static (List<SaveProblem> Errors, List<string> Warnings) Run(CaseSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var errors = new List<SaveProblem>();
        var warnings = new List<string>();

        // Network files: present (unless the edit created them), inside the folder.
        foreach (CaseDocument d in s.Documents.Where(d => d.SchemaFile == CaseSession.V2NetworkSchema))
        {
            if (!CaseSession.IsInside(s.RootPath, d.Path)) errors.Add(new SaveProblem(d.FileName, "", "the network file lies outside the case folder."));
            if (!d.Exists) errors.Add(new SaveProblem(d.FileName, "", "the manifest lists this network file but it does not exist."));
        }

        ReferenceIndex ix = ReferenceIndex.Build(s);

        // Unique bus ids across the case.
        foreach ((int id, int count) in ix.BusIdCounts.Where(kv => kv.Value > 1).OrderBy(kv => kv.Key))
        {
            RecordRef first = ix.Buses[id];
            errors.Add(new SaveProblem(s.Documents[first.Document].FileName, first.Pointer + "/id",
                $"bus id {id.ToString(CultureInfo.InvariantCulture)} is used by {count.ToString(CultureInfo.InvariantCulture)} buses; ids must be unique across every network file."));
        }

        // Bus references, branch and boundary names, ZIP sums: one pass over every collection.
        var boundaryNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RecordCollection c in s.Collections)
        {
            CaseDocument d = s.Documents[c.Document];
            JsonSourceNode? array = s.ArrayOf(c);
            if (array is null) continue;
            var busFields = c.Shape.Fields.Where(f => ReferenceIndex.RefersToBus(c.Shape, f)).Select(f => f.Name).ToList();
            for (int i = 0; i < array.Elements.Count; i++)
            {
                JsonSourceNode rec = array.Elements[i];
                if (rec.Kind != JsonSourceKind.Object) continue;
                string pointer = c.ArrayPointer + "/" + i.ToString(CultureInfo.InvariantCulture);
                foreach (string f in busFields)
                    if (RecordRead.Int(rec, f, d.KeyComparer) is int bus && !ix.Buses.ContainsKey(bus))
                        errors.Add(new SaveProblem(d.FileName, pointer + "/" + f, $"bus {bus.ToString(CultureInfo.InvariantCulture)} does not exist in any network file of the case."));

                if (c.Role == CaseRoles.Boundaries && RecordRead.Str(rec, "name", d.KeyComparer) is string bn)
                    boundaryNames[bn] = boundaryNames.GetValueOrDefault(bn) + 1;

                if (c.Role == CaseRoles.Buses && RecordRead.Member(rec, "zip", d.KeyComparer) is { Kind: JsonSourceKind.Object } zip)
                {
                    CheckZip(d, pointer + "/zip", zip, "ap", "bp", "cp", errors);
                    CheckZip(d, pointer + "/zip", zip, "aq", "bq", "cq", errors);
                }
            }
        }
        foreach ((string name, List<RecordRef> named) in ix.Branches.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            errors.Add(new SaveProblem(s.Documents[named[0].Document].FileName, named[0].Pointer + "/name",
                $"{named.Count.ToString(CultureInfo.InvariantCulture)} branches are named '{name}'; branch names must be unique (boundaries and tap changers find branches by name)."));
        foreach ((string name, int count) in boundaryNames.Where(kv => kv.Value > 1).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            errors.Add(new SaveProblem("case", "", $"{count.ToString(CultureInfo.InvariantCulture)} boundaries are named '{name}'; boundary names must be unique."));

        // Names that resolve to no branch: warnings, as in v1.
        foreach (RecordCollection c in s.Collections.Where(c => c.Role is CaseRoles.Oltcs or CaseRoles.Boundaries or CaseRoles.Protections))
        {
            CaseDocument d = s.Documents[c.Document];
            JsonSourceNode? array = s.ArrayOf(c);
            if (array is null) continue;
            for (int i = 0; i < array.Elements.Count; i++)
            {
                JsonSourceNode rec = array.Elements[i];
                string where = d.FileName + " " + c.ArrayPointer + "/" + i.ToString(CultureInfo.InvariantCulture);
                if (c.Role == CaseRoles.Oltcs && RecordRead.Str(rec, "transformer", d.KeyComparer) is string t && !ix.Branches.ContainsKey(t))
                    warnings.Add($"{where}: the tap changer names transformer '{t}', which no line or transformer carries.");
                if (c.Role == CaseRoles.Protections && RecordRead.Str(rec, "targetBranch", d.KeyComparer) is string tb && !ix.Branches.ContainsKey(tb))
                    warnings.Add($"{where}: the protection's target branch '{tb}' does not exist.");
                if (c.Role == CaseRoles.Boundaries && RecordRead.Member(rec, "branches", d.KeyComparer) is { Kind: JsonSourceKind.Array } brs)
                    foreach (JsonSourceNode b in brs.Elements)
                        if (RecordRead.Str(b, "branch", d.KeyComparer) is string br && !ix.Branches.ContainsKey(br))
                            warnings.Add($"{where}: the boundary names branch '{br}', which does not exist.");
            }
        }

        // Phase 5b (2026-10-20): the asset overlay, only when the case lists one (so a case without "assets" is
        // checked exactly as before: nothing below runs).
        if (s.HasOverlay) OverlayChecks(s, errors);
        return (errors, warnings);
    }

    /// <summary>
    /// Phase 5b: the asset overlay files of the case, checked as the v2 loader checks them (<see cref="Overlay.V2AssetChecker"/>:
    /// overlay kinds, params against the kind specs, unique ids, parents, every reference resolving against the case's
    /// network files), with EVERY problem listed, each naming the record id and the field. Each listed file must exist
    /// and lie inside the case folder. The checker's first layer is the schema, which the save gate already runs over
    /// every document; so the checker runs only on a schema-valid, complete set, and no problem is listed twice.
    /// </summary>
    private static void OverlayChecks(CaseSession s, List<SaveProblem> errors)
    {
        bool complete = true;
        foreach (CaseDocument d in s.AssetDocuments)
        {
            if (!CaseSession.IsInside(s.RootPath, d.Path)) { errors.Add(new SaveProblem(d.FileName, "", "the asset overlay file lies outside the case folder.")); complete = false; }
            if (!d.Exists) { errors.Add(new SaveProblem(d.FileName, "", "the manifest lists this asset overlay file but it does not exist.")); complete = false; }
        }
        if (!complete) return;

        var documents = s.AssetDocuments.Select(d => (d.FileName, d.Text.ToElement())).ToList();
        if (documents.Any(x => BundledSchemas.V2Validate.Validate(x.Item2, CaseSession.V2AssetsSchema).Count > 0)) return;   // the schema stage lists those
        Overlay.AssetOverlayTargets targets = Overlay.AssetOverlayTargets.FromNetworks(
            s.Documents.Where(d => d.SchemaFile == CaseSession.V2NetworkSchema && d.Exists).Select(d => d.Text.ToElement()));
        foreach (Schema.SchemaError e in Overlay.V2AssetChecker.Check(documents, BundledSchemas.V2Validate, BundledSchemas.OverlayKinds(), targets))
            errors.Add(new SaveProblem(e.File ?? "case", e.Pointer, e.Message));
    }

    private static void CheckZip(CaseDocument d, string pointer, JsonSourceNode zip, string a, string b, string c, List<SaveProblem> errors)
    {
        double? x = RecordRead.Num(zip, a, d.KeyComparer), y = RecordRead.Num(zip, b, d.KeyComparer), z = RecordRead.Num(zip, c, d.KeyComparer);
        if (x is not double xv || y is not double yv || z is not double zv) return;   // a missing coefficient is a schema error
        double sum = xv + yv + zv;
        if (!(Math.Abs(sum - 1.0) <= JsonGridLoader.ZipSumTolerance))
            errors.Add(new SaveProblem(d.FileName, pointer, $"zip coefficients {a} + {b} + {c} sum to {sum.ToString("R", CultureInfo.InvariantCulture)}, not 1."));
    }
}
