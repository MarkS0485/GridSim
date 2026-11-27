using System.Globalization;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// R4-08: bind one declarative model to one machine of a dynamics study, in place of the controller the DYR file gave that
/// machine (or in addition to none). The governor or the exciter chain of that machine is then the user model.
/// </summary>
/// <param name="Machine">The machine, by native identity (bus, id).</param>
/// <param name="Model">The compiled, parameter-bound model. Its role must be <c>governor</c> or <c>exciter</c>.</param>
/// <param name="ReplacesStabiliser">For an exciter model: true when the model's own diagram includes the stabiliser path, so a
/// PSS1A record the DYR file gives the machine is superseded (and reported as such). When the machine has a coded stabiliser
/// and this is false, the assignment is refused rather than dropping the stabiliser silently.</param>
public sealed record UserModelAssignment(DynamicsMachineId Machine, UserModel Model, bool ReplacesStabiliser = false);

/// <summary>
/// RC5 WP12: bind one plug-in model (<see cref="GridSim.Core.Dynamics.Plugins.PluginModel"/>, role governor or exciter) to one
/// machine of a dynamics study, exactly as <see cref="UserModelAssignment"/> binds a declarative model. Its coverage status is
/// <c>SUPPORTED (PLUGIN, sha256:&lt;assembly hash&gt;, OUTSIDE THE VERIFIED CONTRACT)</c>.
/// </summary>
public sealed record PluginModelAssignment(DynamicsMachineId Machine, GridSim.Core.Dynamics.Plugins.PluginModel Model, bool ReplacesStabiliser = false);

/// <summary>RC5 WP12: what the overlay needs of a bound model, declarative or plug-in; one binding route for both.</summary>
internal interface IAssignableModel
{
    string Id { get; }
    string Role { get; }
    string Hash { get; }
    /// <summary>The unit tag in the coverage report.</summary>
    string Tag { get; }
    /// <summary>The coverage status text.</summary>
    string Status { get; }
    /// <summary>The model name of a coverage record added for a machine with no DYR record.</summary>
    string RecordName { get; }
    /// <summary>The reader note naming the model and where it is bound.</summary>
    string Note(DynamicsMachineId machine, string role);
    GovernorData GovernorData();
    RmsExciter NewExciter();
}

/// <summary>The declarative route: every text is the one R4-08 has always written.</summary>
internal sealed class AssignedUserModel(UserModel model) : IAssignableModel
{
    public string Id => model.Id;
    public string Role => model.Role;
    public string Hash => model.Hash;
    public string Tag => "USER:" + model.Id + " (sha256:" + model.Hash + ")";
    public string Status => UserModelOverlay.StatusText(model.Hash);
    public string RecordName => "USER:" + model.Id;
    public string Note(DynamicsMachineId machine, string role)
        => $"user model {model.Id} ({role}) sha256:{model.Hash} order-digest:{model.Plan.OrderDigest} bound to {machine}; parameters: "
            + string.Join(", ", model.NewInstance().ParameterValues.Select(p => p.Key + "=" + p.Value.ToString("R", CultureInfo.InvariantCulture)));
    public GovernorData GovernorData() => new UserModelGovernorData(model);
    public RmsExciter NewExciter() => RmsExciter.FromUser(new UserModelFieldController(model), "USER:" + model.Id);
}

/// <summary>The plug-in route.</summary>
internal sealed class AssignedPluginModel(GridSim.Core.Dynamics.Plugins.PluginModel model) : IAssignableModel
{
    public string Id => model.Id;
    public string Role => model.Definition.RoleText;
    public string Hash => model.Hash;
    public string Tag => "PLUGIN:" + model.Id + " (sha256:" + model.Hash + ")";
    public string Status => UserModelOverlay.PluginStatusText(model.Hash);
    public string RecordName => "PLUGIN:" + model.Id;
    public string Note(DynamicsMachineId machine, string role) => model.ProvenanceNote + "; bound to " + machine;
    public GovernorData GovernorData() => new GridSim.Core.Dynamics.Plugins.PluginGovernorData(model);
    public RmsExciter NewExciter() => RmsExciter.FromUser(new GridSim.Core.Dynamics.Plugins.PluginFieldController(model), "PLUGIN:" + model.Id);
}

/// <summary>
/// R4-08: applies user-model assignments to the incumbent DYR binding and extends the model coverage report: a record bound
/// to a user model is listed as <c>SUPPORTED (USER MODEL, sha256:&lt;hash&gt;)</c>, with the model id, the compiled order
/// digest and the bound parameter values in the report notes. Additive: with no assignments the binding is returned
/// unchanged, and nothing here edits the incumbent reader, loader or coverage builder.
/// </summary>
public static class UserModelOverlay
{
    /// <summary>The coverage status of a record bound to a user model.</summary>
    public static string StatusText(string hash) => $"SUPPORTED (USER MODEL, sha256:{hash})";

    /// <summary>RC5 WP12: the coverage status of a record bound to a plug-in model.</summary>
    public static string PluginStatusText(string assemblyHash) => $"SUPPORTED (PLUGIN, sha256:{assemblyHash}, {GridSim.Core.Dynamics.Plugins.PluginContract.OutsideLabel})";

    /// <summary>Apply the assignments to <paramref name="binding"/> (governors and the coverage report; exciters are placed
    /// by <see cref="ApplyExciters"/> where the study builds its exciter list).</summary>
    /// <exception cref="InvalidDataException">An assignment names no in-service machine with bound data, has a role that is
    /// neither governor nor exciter, repeats a machine and role, or would drop a coded stabiliser silently.</exception>
    public static DynamicsModelBinding Apply(PsseParseResult raw, DynamicsModelBinding binding, IReadOnlyList<UserModelAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        return ApplyAll(raw, binding, assignments, null);
    }

    /// <summary>RC5 WP12: apply declarative assignments, then plug-in assignments, in one pass (a machine and role is given one
    /// model across both lists). With no plug-in assignments this is <see cref="Apply"/>, text for text.</summary>
    public static DynamicsModelBinding ApplyAll(PsseParseResult raw, DynamicsModelBinding binding, IReadOnlyList<UserModelAssignment>? assignments,
        IReadOnlyList<PluginModelAssignment>? plugins)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(binding);
        var all = Assigned(assignments, plugins);
        if (all.Count == 0 || binding.Set is null) return binding;

        var entries = binding.Set.Entries.ToList();
        var records = binding.Report.Records.ToList();
        var units = binding.Report.Units.ToList();
        var notes = binding.Report.ReaderNotes.ToList();
        var seen = new HashSet<(int, string, string)>();
        foreach ((DynamicsMachineId machine, IAssignableModel model, bool replaces) a in all)
        {
            string role = Role(a.machine, a.model);
            if (!seen.Add((a.machine.Bus, a.machine.Id, role)))
                throw new InvalidDataException($"user models: {a.machine} is given two {role} models.");
            int gi = GeneratorIndex(raw.Model, raw, a.machine);
            int ei = entries.FindIndex(e => e.GeneratorIndex == gi);
            if (ei < 0) throw new InvalidDataException($"user models: {a.machine} has no bound machine data (no GENROU or GENSAL record), so a controller cannot be attached.");
            string tag = a.model.Tag;
            string status = a.model.Status;
            string semantics = $"user model {a.model.Id}; the values of the DYR record, if any, are not used";
            if (role == "governor")
            {
                entries[ei] = entries[ei] with { Data = entries[ei].Data with { Governor = a.model.GovernorData() } };
                ReplaceRecords(records, gi, a.machine, new[] { "governor" }, status, semantics, a.model, role);
                int ui = units.FindIndex(u => u.GeneratorIndex == gi);
                if (ui >= 0) units[ui] = units[ui] with { Governor = tag };
            }
            else
            {
                if (entries[ei].Exciter?.Pss is not null && !a.replaces)
                    throw new InvalidDataException($"user models: {a.machine} has a coded stabiliser (PSS1A) in its DYR data; the exciter model {a.model.Id} would drop it. "
                        + "Either include the stabiliser in the user model and set ReplacesStabiliser, or remove the PSS1A record.");
                entries[ei] = entries[ei] with { Exciter = null };
                ReplaceRecords(records, gi, a.machine, a.replaces ? new[] { "exciter", "pss" } : new[] { "exciter" }, status, semantics, a.model, role);
                int ui = units.FindIndex(u => u.GeneratorIndex == gi);
                if (ui >= 0) units[ui] = units[ui] with { Exciter = tag, Stabiliser = a.replaces ? tag : units[ui].Stabiliser };
            }
            notes.Add(a.model.Note(a.machine, role));
        }
        var report = binding.Report with { Records = records, Units = units, ReaderNotes = notes };
        MachineDynamicsSet set = new MachineDynamicsSet(binding.Set.Name, binding.Set.Path, entries).WithRenewables(binding.Set.Renewables);
        return new DynamicsModelBinding(report, set);
    }

    /// <summary>Place the exciter-role assignments into the study's exciter list (aligned to <c>model.Generators</c>), each as a
    /// fresh <see cref="RmsExciter"/> wrapping a fresh controller. Replaces whatever the DYR binding put at that machine.</summary>
    public static void ApplyExciters(PsseParseResult raw, IList<RmsExciter?> list, IReadOnlyList<UserModelAssignment> assignments)
        => ApplyAllExciters(raw, list, assignments, null);

    /// <summary>RC5 WP12: <see cref="ApplyExciters"/> for both lists, declarative first.</summary>
    public static void ApplyAllExciters(PsseParseResult raw, IList<RmsExciter?> list, IReadOnlyList<UserModelAssignment>? assignments,
        IReadOnlyList<PluginModelAssignment>? plugins)
    {
        foreach ((DynamicsMachineId machine, IAssignableModel model, bool _) a in Assigned(assignments, plugins))
        {
            if (a.model.Role != "exciter") continue;
            int gi = GeneratorIndex(raw.Model, raw, a.machine);
            list[gi] = a.model.NewExciter();
        }
    }

    private static List<(DynamicsMachineId Machine, IAssignableModel Model, bool Replaces)> Assigned(IReadOnlyList<UserModelAssignment>? assignments,
        IReadOnlyList<PluginModelAssignment>? plugins)
    {
        var all = new List<(DynamicsMachineId, IAssignableModel, bool)>();
        if (assignments is not null) foreach (UserModelAssignment a in assignments) all.Add((a.Machine, new AssignedUserModel(a.Model), a.ReplacesStabiliser));
        if (plugins is not null) foreach (PluginModelAssignment a in plugins) all.Add((a.Machine, new AssignedPluginModel(a.Model), a.ReplacesStabiliser));
        return all;
    }

    private static string Role(DynamicsMachineId machine, IAssignableModel model)
        => model.Role is "governor" or "exciter" ? model.Role
            : throw new InvalidDataException($"user models: {machine}: the model {model.Id} has role '{model.Role}'; a study binds governor and exciter models "
                + "(a stabiliser is part of the exciter model's diagram).");

    private static void ReplaceRecords(List<DyrRecordCoverage> records, int gi, DynamicsMachineId machine, string[] roles, string status, string semantics,
        IAssignableModel a, string role)
    {
        bool any = false;
        for (int i = 0; i < records.Count; i++)
        {
            DyrRecordCoverage r = records[i];
            if (r.GeneratorIndex != gi || Array.IndexOf(roles, r.Role) < 0) continue;
            records[i] = r with { Status = status, Semantics = semantics, Derived = Array.Empty<string>(), Reasons = Array.Empty<string>() };
            any = true;
        }
        if (!any)
            records.Add(new DyrRecordCoverage(0, machine.Bus, a.RecordName, machine.Id, role, gi, status,
                "user model assigned without a DYR record", Array.Empty<string>(), Array.Empty<string>(),
                DynamicsModelCoverage.TraceLabels(machine.Bus, machine.Id)));
    }

    private static int GeneratorIndex(GridModel model, PsseParseResult raw, DynamicsMachineId id)
    {
        for (int k = 0; k < model.AllGenerators.Count; k++)
        {
            if (raw.MachineKeys[k].Bus != id.Bus || raw.MachineKeys[k].Id != id.Id) continue;
            Generator g = model.AllGenerators[k];
            if (!g.InService) throw new InvalidDataException($"user models: {id} is out of service in the RAW case.");
            for (int j = 0; j < model.Generators.Count; j++)
                if (ReferenceEquals(model.Generators[j], g)) return j;
        }
        throw new InvalidDataException($"user models: the RAW case holds no {id}.");
    }
}
