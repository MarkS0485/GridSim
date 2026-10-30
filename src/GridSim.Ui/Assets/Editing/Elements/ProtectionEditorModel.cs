using GridSim.Core.IO.Json.Lossless;

namespace GridSim.Ui.Assets.Editing.Elements;

// UI programme Phase 4.3: the protective device editor (v1 protections.json: class names an asset class; v2 network
// protections: template names a protection template). The monitored bus is a bus picker, the target branch a picker over
// the case's branch names.
//
// Derived read-outs (display only, never written back):
//   Monitoring and target:
//     Monitored bus base voltage (kV)  = baseKv(monitorBus)
//     Target branch                    which record the name resolves to (the first of the name), or none

/// <summary>The per-element editor of a protective device (v1 protections.json, v2 network protections).</summary>
public sealed class ProtectionEditorModel : ElementEditorModel
{
    /// <summary>The section titles, in order.</summary>
    public const string Identity = "Identity and template", Monitoring = "Monitoring and target";

    internal static readonly SectionSpec[] Layout =
    {
        new(Identity, "name", "class", "template"),
        new(Monitoring, "monitorBus", "targetBranch", "capacityMw"),
    };

    internal ProtectionEditorModel(EditorContext c) : base(c, "Protective device", Layout, Derive) { }

    private static IReadOnlyList<DerivedReadout> Derive(EditorContext c, string section)
    {
        var list = new List<DerivedReadout>();
        if (section != Monitoring) return list;
        (double? kv, string kvWhy) = c.BusKv("monitorBus");
        list.Add(kv is double k ? DerivedReadout.Of("Monitored bus base voltage", k, "kV", "baseKv(monitorBus)")
            : DerivedReadout.NotShown("Monitored bus base voltage", "kV", kvWhy, "baseKv(monitorBus)"));
        if (c.Str("targetBranch") is string name)
        {
            (JsonSourceNode? b, string? role, _) = c.Branch(name);
            list.Add(b is not null
                ? DerivedReadout.Words("Target branch", "'" + name + "' is a " + (role == Core.IO.Json.CaseEditing.CaseRoles.Transformers ? "transformer" : "line"), "the first branch of the name")
                : DerivedReadout.Words("Target branch", "n/a: no line or transformer is named '" + name + "'", "the first branch of the name"));
        }
        return list;
    }
}
