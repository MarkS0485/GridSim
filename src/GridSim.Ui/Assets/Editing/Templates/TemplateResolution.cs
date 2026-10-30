using System.Globalization;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Model;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): the EFFECTIVE parameters of every generator, as the v2 loader resolves them. The
// editor does not work them out itself (a second resolver could drift from the loader's): the case as the session holds
// it now is written to a fresh temporary folder (every document at its path relative to the case folder, beside the
// embedded v2 schemas and kind specs, as the save gate's loader stage stages a case) and loaded with V2CaseLoader, the
// run pipeline's own loader. The loader applies the overrides to their templates (its Template.WithOverrides), turns
// every active template of a modelled kind into an asset class and builds the model; then each generator's class is
// found exactly as every consumer of the model finds it (AssetBindingSet.Resolve with the generator's own class and its
// type: AssetDynamicsFleet, FormingAggregate, the small-signal assembler) and its parameters are the class's. Nothing is
// written to the case. The load is made WITHOUT dispatch, so the model's generators are the network files' generators in
// file order (dispatch balances outputs and may move an out-of-service unit to the end; it changes no binding and no
// class), and each one is matched to its record by position and checked by name (fail loud on a mismatch).

/// <summary>One effective parameter of a generator's template as the loader resolves it.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Value">The value the loaded asset class holds (exactly: compare by bits).</param>
/// <param name="Unit">Its unit from the kind spec, or "".</param>
/// <param name="Source">"template" (the template's own value) or "override (reason)" (this case's override of it).</param>
public sealed record EffectiveParameter(string Name, double Value, string Unit, string Source)
{
    /// <summary>The value written shortest round-trip, invariant (what the loader read, digit for digit).</summary>
    public string Text => Value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>True when the value comes from an override.</summary>
    public bool IsOverride => Source.StartsWith("override", StringComparison.Ordinal);
}

/// <summary>One generator's effective binding: its template, the asset class the loader made of it, and its parameters.</summary>
/// <param name="Generator">The generator and how it is bound (from the case text).</param>
/// <param name="ClassName">The asset class the loaded model binds it to, or null when it is unbound (passive).</param>
/// <param name="ClassKind">The class's kind (GridFollowingInverter, ...), or "".</param>
/// <param name="Params">The class's parameters, in the template's order, each with where it comes from.</param>
/// <param name="Values">The class's parameters exactly as the loaded model holds them (AssetClass.Params).</param>
public sealed record EffectiveBinding(GeneratorBindingInfo Generator, string? ClassName, string ClassKind,
    IReadOnlyList<EffectiveParameter> Params, IReadOnlyDictionary<string, double> Values)
{
    /// <summary>True when the generator is bound to a class (it has an inverter controller).</summary>
    public bool IsBound => ClassName is not null;
}

/// <summary>
/// The effective template parameters of every generator of a v2 case, as the v2 loader resolves them (see the file
/// comment). Made by <see cref="Load"/>; a case the loader refuses has <see cref="Error"/> (the loader's message) and
/// no bindings.
/// </summary>
public sealed class TemplateResolution
{
    private TemplateResolution(long revision, string? error, IReadOnlyList<EffectiveBinding> generators, IReadOnlyList<string> notes)
    {
        Revision = revision;
        Error = error;
        Generators = generators;
        Notes = notes;
    }

    /// <summary>The authoring revision the case was loaded at (an edit after it makes this stale).</summary>
    public long Revision { get; }

    /// <summary>The loader's refusal (the case as it is now does not load), or null.</summary>
    public string? Error { get; }

    /// <summary>True when the loader read the case.</summary>
    public bool Loaded => Error is null;

    /// <summary>Every generator (network file order) and its effective binding; empty when the case did not load.</summary>
    public IReadOnlyList<EffectiveBinding> Generators { get; }

    /// <summary>The loader's notes (for example which revision an unpinned import resolved to).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The effective binding of the generator record <paramref name="generator"/>, or null.</summary>
    public EffectiveBinding? For(Keys.CaseRef generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return Generators.FirstOrDefault(g => g.Generator.Record == generator);
    }

    /// <summary>
    /// Load the case <paramref name="a"/> holds now with the v2 loader (on a staged copy; nothing is written to the case)
    /// and resolve every generator's class and parameters. <paramref name="dataRoot"/> is the folder library imports
    /// resolve against (<see cref="CaseTemplates.DefaultDataRoot"/> when null; a folder that does not exist is not given
    /// to the loader, which then refuses a case that imports, naming the missing data root).
    /// </summary>
    public static TemplateResolution Load(CaseAuthoring a, string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        CaseSession s = a.Session;
        CaseTemplates templates = CaseTemplates.Read(a, dataRoot);
        if (a.Format != AuthoringFormat.V2Case)
            return new TemplateResolution(a.Revision, "the effective template parameters are those of a v2 case manifest; this case is not one.", Array.Empty<EffectiveBinding>(), Array.Empty<string>());

        string stage = Path.Combine(Path.GetTempPath(), "gridsim-templates-" + Guid.NewGuid().ToString("N"));
        string caseDir = Path.Combine(stage, "case");
        try
        {
            string schemas = Path.Combine(stage, "schemas");
            Directory.CreateDirectory(caseDir);
            BundledSchemas.WriteV2SchemaFolder(schemas);
            foreach (CaseDocument d in s.Documents.Where(d => d.Exists))
            {
                string target = Path.Combine(caseDir, Path.GetRelativePath(s.RootPath, d.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, d.Text.ToArray());
            }
            string root = dataRoot ?? CaseTemplates.DefaultDataRoot;
            V2CaseLoadResult loaded = V2CaseLoader.LoadWithNotes(Path.Combine(caseDir, Path.GetRelativePath(s.RootPath, s.Documents[0].Path)),
                schemas, Directory.Exists(root) ? root : null, dispatch: false);
            return new TemplateResolution(a.Revision, null, Resolve(loaded.Model, templates), loaded.Notes);
        }
        catch (Exception ex) when (CaseSession.IsLoaderFailure(ex))
        {
            // The staged folder's path means nothing to the user: show the case folder in its place.
            return new TemplateResolution(a.Revision, ex.Message.Replace(caseDir, s.RootPath, StringComparison.Ordinal),
                Array.Empty<EffectiveBinding>(), Array.Empty<string>());
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp folder is harmless */ }
        }
    }

    // Each generator's class, found as every consumer of the model finds it, and that class's parameters.
    private static List<EffectiveBinding> Resolve(GridModel model, CaseTemplates templates)
    {
        IReadOnlyList<GeneratorBindingInfo> records = templates.Generators;
        if (model.Generators.Count != records.Count)
            throw new InvalidOperationException("the loaded model has " + model.Generators.Count.ToString(CultureInfo.InvariantCulture)
                + " generators but the case's network files hold " + records.Count.ToString(CultureInfo.InvariantCulture) + "; the effective parameters cannot be matched to the records.");
        var list = new List<EffectiveBinding>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Generator g = model.Generators[i];
            GeneratorBindingInfo rec = records[i];
            if (!string.Equals(g.Name, rec.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("generator " + i.ToString(CultureInfo.InvariantCulture) + " of the loaded model is '" + g.Name
                    + "' but the record in that place is " + rec.Label + "; the effective parameters cannot be matched to the records.");
            string? cls = model.AssetDynamics?.Bindings.Resolve(g.DynamicsClass, g.Type);
            if (cls is null || model.AssetDynamics is not { } dyn)
            {
                list.Add(new EffectiveBinding(rec, null, "", Array.Empty<EffectiveParameter>(), new Dictionary<string, double>()));
                continue;
            }
            AssetClass c = dyn.Library.Get(cls);   // throws on an unknown class: the loader would have refused it first
            TemplateInfo? tpl = rec.Template is null ? null : templates.Find(rec.Template);
            var ps = new List<EffectiveParameter>();
            foreach ((string name, double value) in c.Params)
            {
                string unit = tpl is null ? "" : TemplateKinds.ParamsOf(tpl.Kind).FirstOrDefault(p => p.Name == name)?.Unit ?? "";
                string source = rec.Template is not null && templates.OverrideOf(rec.Template, name) is (TemplateOverrideEntry o, _)
                    ? "override (" + o.Reason + ")" : "template";
                ps.Add(new EffectiveParameter(name, value, unit, source));
            }
            list.Add(new EffectiveBinding(rec, cls, c.Kind.ToString(), ps, c.Params));
        }
        return list;
    }
}
