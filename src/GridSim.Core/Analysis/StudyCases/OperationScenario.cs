using System.Globalization;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: operation scenarios. A scenario is a named set of group-scoped scale or set operations on load and
// generation, plus availability. It is expanded, for one instant, to EXPLICIT per-element PlanningOperations (the RC3
// vocabulary), so the dated, conflict-checked application is the unchanged PlanningExpansion.

/// <summary>What a scenario operation changes. <c>Load</c> is demand (MW and MVAr together, scale only);
/// <c>Generation</c> is the active-power setpoint (an alias of <c>PgMw</c>).</summary>
public enum ScenarioQuantity { Load, Generation, PdMw, QdMvar, PgMw, QgMvar, InService }

public enum ScenarioAction { Scale, Set }

/// <summary>One scenario operation. <see cref="Group"/> null means every element of the quantity's kind in the case.
/// Scale multiplies the value the element holds when the scenario applies (after the variations active at the instant);
/// set replaces it. Availability (<c>InService</c>) applies to branches and is set-only.</summary>
public sealed record ScenarioOperation(string Id, string? Group, ScenarioQuantity Quantity, ScenarioAction Action, PlanningValue Value);

public sealed record OperationScenario(string Id, string? Description, IReadOnlyList<ScenarioOperation> Operations,
    IReadOnlyList<string>? Instants = null);

/// <summary>One element-level change a scenario made, in application order: what the derivation record lists.</summary>
public sealed record ScenarioElementChange(string Scenario, string Operation, string? Group, string Target, string Field,
    string Action, string Before, string After);

/// <summary>The result of expanding one scenario at one instant.</summary>
public sealed record ScenarioExpansion(IReadOnlyList<PlanningOperation> Operations, IReadOnlyList<ScenarioElementChange> Changes);

public static class ScenarioExpander
{
    public const string ProjectPrefix = "scenario.";

    public static string QuantityName(ScenarioQuantity q) => q switch
    {
        ScenarioQuantity.Load => "load",
        ScenarioQuantity.Generation => "generation",
        ScenarioQuantity.PdMw => "pdMw",
        ScenarioQuantity.QdMvar => "qdMvar",
        ScenarioQuantity.PgMw => "pgMw",
        ScenarioQuantity.QgMvar => "qgMvar",
        _ => "inService"
    };

    public static bool TryParseQuantity(string text, out ScenarioQuantity q)
    {
        foreach (ScenarioQuantity c in Enum.GetValues<ScenarioQuantity>())
            if (QuantityName(c) == text) { q = c; return true; }
        q = default;
        return false;
    }

    /// <summary>Static checks that need no instant: group references, quantity and action compatibility, value types.</summary>
    public static void Validate(OperationScenario scenario, BoundGroups? groups)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenarioOperation op in scenario.Operations)
        {
            string name = $"Scenario '{scenario.Id}' operation '{op.Id}'";
            if (!ids.Add(op.Id)) throw new StudyCaseException($"Scenario '{scenario.Id}': duplicate operation ID '{op.Id}'.");
            if (op.Group is not null)
            {
                if (groups is null) throw new StudyCaseException($"{name}: names group '{op.Group}' but the study case has no groups document.");
                if (!groups.TryGet(op.Group, out _)) throw new StudyCaseException($"{name}: group '{op.Group}' is not defined in the groups document.");
            }
            bool flag = op.Quantity == ScenarioQuantity.InService;
            if (flag)
            {
                if (op.Action != ScenarioAction.Set || !op.Value.IsFlag)
                    throw new StudyCaseException($"{name}: availability (inService) takes action set and true or false.");
            }
            else
            {
                if (op.Value.IsFlag || !double.IsFinite(op.Value.Number!.Value))
                    throw new StudyCaseException($"{name}: value must be a finite number.");
                if (op.Action == ScenarioAction.Scale && op.Value.Number!.Value < 0)
                    throw new StudyCaseException($"{name}: a scale factor cannot be negative.");
                if (op.Quantity == ScenarioQuantity.Load && op.Action != ScenarioAction.Scale)
                    throw new StudyCaseException($"{name}: quantity load is scale only; use pdMw or qdMvar to set a value.");
            }
        }
    }

    /// <summary>
    /// Expand one scenario at <paramref name="instant"/> against <paramref name="baseline"/>, the case with the
    /// variations active at that instant already applied. Order: operations in file order; within one, the group's
    /// elements in model order (buses in bus order, units in fleet order, branches in branch order); a load scale
    /// yields pdMw then qdMvar for each bus. Each new value is computed once in IEEE double (<c>before * factor</c>),
    /// and the next operation on the same element and field starts from it. An operation whose group holds no element
    /// of its kind is refused.
    /// </summary>
    public static ScenarioExpansion Expand(GridModel baseline, BoundGroups? groups, OperationScenario scenario, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(scenario);
        Validate(scenario, groups);
        var ops = new List<PlanningOperation>();
        var changes = new List<ScenarioElementChange>();
        var working = new Dictionary<(PlanningElementKind, int, PlanningField), PlanningValue>();

        void Emit(ScenarioOperation so, PlanningTarget target, int key, PlanningField field, double? factorOrValue, bool? flag)
        {
            PlanningValue before = working.TryGetValue((target.Kind, key, field), out PlanningValue w) ? w : Read(baseline, target, field);
            PlanningValue after = flag is bool f ? PlanningValue.Of(f)
                : PlanningValue.Of(so.Action == ScenarioAction.Scale ? before.Number!.Value * factorOrValue!.Value : factorOrValue!.Value);
            working[(target.Kind, key, field)] = after;
            string id = $"{so.Id}.{PlanningExpansion.FieldName(field)}.{key.ToString(CultureInfo.InvariantCulture)}";
            ops.Add(new PlanningOperation(id, target, field, before, after, instant, null));
            changes.Add(new ScenarioElementChange(scenario.Id, so.Id, so.Group, target.Id, PlanningExpansion.FieldName(field),
                so.Action == ScenarioAction.Scale ? "scale " + PlanningValue.Of(factorOrValue!.Value) : "set", before.ToString(), after.ToString()));
        }

        foreach (ScenarioOperation so in scenario.Operations)
        {
            BoundGroup? group = so.Group is null ? null : groups!.Get(so.Group);
            double? number = so.Value.Number;
            string where = so.Group is null ? "the case" : $"group '{so.Group}'";
            switch (so.Quantity)
            {
                case ScenarioQuantity.Load or ScenarioQuantity.PdMw or ScenarioQuantity.QdMvar:
                {
                    int[] buses = group is null ? baseline.Buses.Select(b => b.Id).ToArray()
                        : baseline.Buses.Select(b => b.Id).Where(new HashSet<int>(group.BusIds).Contains).ToArray();
                    if (buses.Length == 0)
                        throw new StudyCaseException($"Scenario '{scenario.Id}' operation '{so.Id}': {where} has no bus to carry a load.");
                    foreach (int bus in buses)
                    {
                        var target = new PlanningTarget(PlanningElementKind.Load, null, null, null, bus, null);
                        if (so.Quantity != ScenarioQuantity.QdMvar) Emit(so, target, bus, PlanningField.LoadPdMw, number, null);
                        if (so.Quantity != ScenarioQuantity.PdMw) Emit(so, target, bus, PlanningField.LoadQdMvar, number, null);
                    }
                    break;
                }
                case ScenarioQuantity.Generation or ScenarioQuantity.PgMw or ScenarioQuantity.QgMvar:
                {
                    IReadOnlyList<int> units = group is null ? Enumerable.Range(0, baseline.AllGenerators.Count).ToArray()
                        : BoundGroups.GeneratorsOf(group, baseline);
                    if (units.Count == 0)
                        throw new StudyCaseException($"Scenario '{scenario.Id}' operation '{so.Id}': {where} has no generating unit.");
                    foreach (int u in units)
                    {
                        var target = new PlanningTarget(PlanningElementKind.Generator, u, null, null, baseline.AllGenerators[u].Bus, null);
                        Emit(so, target, u, so.Quantity == ScenarioQuantity.QgMvar ? PlanningField.GeneratorQgMvar : PlanningField.GeneratorPgMw, number, null);
                    }
                    break;
                }
                default:
                {
                    IReadOnlyList<int> branches = group is null ? Enumerable.Range(0, baseline.Branches.Count).ToArray() : group.BranchIndices;
                    if (branches.Count == 0)
                        throw new StudyCaseException($"Scenario '{scenario.Id}' operation '{so.Id}': {where} has no branch to switch.");
                    foreach (int bi in branches)
                    {
                        Branch b = baseline.Branches[bi];
                        var target = new PlanningTarget(PlanningElementKind.Branch, bi, b.From, b.To, null, null);
                        Emit(so, target, bi, PlanningField.BranchInService, null, so.Value.Flag!.Value);
                    }
                    break;
                }
            }
        }
        return new ScenarioExpansion(ops, changes);
    }

    private static PlanningValue Read(GridModel m, PlanningTarget t, PlanningField f) => f switch
    {
        PlanningField.LoadPdMw => PlanningValue.Of(m.Buses.First(b => b.Id == t.Bus).Pd),
        PlanningField.LoadQdMvar => PlanningValue.Of(m.Buses.First(b => b.Id == t.Bus).Qd),
        PlanningField.GeneratorPgMw => PlanningValue.Of(m.AllGenerators[t.Index!.Value].Pg),
        PlanningField.GeneratorQgMvar => PlanningValue.Of(m.AllGenerators[t.Index!.Value].Qg),
        PlanningField.BranchInService => PlanningValue.Of(m.Branches[t.Index!.Value].InService),
        _ => throw new ArgumentOutOfRangeException(nameof(f))
    };
}
