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
/// R4-08: applies user-model assignments to the incumbent DYR binding and extends the model coverage report: a record bound
/// to a user model is listed as <c>SUPPORTED (USER MODEL, sha256:&lt;hash&gt;)</c>, with the model id, the compiled order
/// digest and the bound parameter values in the report notes. Additive: with no assignments the binding is returned
/// unchanged, and nothing here edits the incumbent reader, loader or coverage builder.
/// </summary>
public static class UserModelOverlay
{
    /// <summary>The coverage status of a record bound to a user model.</summary>
    public static string StatusText(string hash) => $"SUPPORTED (USER MODEL, sha256:{hash})";

    /// <summary>Apply the assignments to <paramref name="binding"/> (governors and the coverage report; exciters are placed
    /// by <see cref="ApplyExciters"/> where the study builds its exciter list).</summary>
    /// <exception cref="InvalidDataException">An assignment names no in-service machine with bound data, has a role that is
    /// neither governor nor exciter, repeats a machine and role, or would drop a coded stabiliser silently.</exception>
    public static DynamicsModelBinding Apply(PsseParseResult raw, DynamicsModelBinding binding, IReadOnlyList<UserModelAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0 || binding.Set is null) return binding;

        var entries = binding.Set.Entries.ToList();
        var records = binding.Report.Records.ToList();
        var units = binding.Report.Units.ToList();
        var notes = binding.Report.ReaderNotes.ToList();
        var seen = new HashSet<(int, string, string)>();
        foreach (UserModelAssignment a in assignments)
        {
            string role = Role(a);
            if (!seen.Add((a.Machine.Bus, a.Machine.Id, role)))
                throw new InvalidDataException($"user models: {a.Machine} is given two {role} models.");
            int gi = GeneratorIndex(raw.Model, raw, a.Machine);
            int ei = entries.FindIndex(e => e.GeneratorIndex == gi);
            if (ei < 0) throw new InvalidDataException($"user models: {a.Machine} has no bound machine data (no GENROU or GENSAL record), so a controller cannot be attached.");
            string tag = "USER:" + a.Model.Id + " (sha256:" + a.Model.Hash + ")";
            string status = StatusText(a.Model.Hash);
            string semantics = $"user model {a.Model.Id}; the values of the DYR record, if any, are not used";
            if (role == "governor")
            {
                entries[ei] = entries[ei] with { Data = entries[ei].Data with { Governor = new UserModelGovernorData(a.Model) } };
                ReplaceRecords(records, gi, a.Machine, new[] { "governor" }, status, semantics, a, role);
                int ui = units.FindIndex(u => u.GeneratorIndex == gi);
                if (ui >= 0) units[ui] = units[ui] with { Governor = tag };
            }
            else
            {
                if (entries[ei].Exciter?.Pss is not null && !a.ReplacesStabiliser)
                    throw new InvalidDataException($"user models: {a.Machine} has a coded stabiliser (PSS1A) in its DYR data; the exciter model {a.Model.Id} would drop it. "
                        + "Either include the stabiliser in the user model and set ReplacesStabiliser, or remove the PSS1A record.");
                entries[ei] = entries[ei] with { Exciter = null };
                ReplaceRecords(records, gi, a.Machine, a.ReplacesStabiliser ? new[] { "exciter", "pss" } : new[] { "exciter" }, status, semantics, a, role);
                int ui = units.FindIndex(u => u.GeneratorIndex == gi);
                if (ui >= 0) units[ui] = units[ui] with { Exciter = tag, Stabiliser = a.ReplacesStabiliser ? tag : units[ui].Stabiliser };
            }
            notes.Add($"user model {a.Model.Id} ({role}) sha256:{a.Model.Hash} order-digest:{a.Model.Plan.OrderDigest} bound to {a.Machine}; parameters: "
                + string.Join(", ", a.Model.NewInstance().ParameterValues.Select(p => p.Key + "=" + p.Value.ToString("R", CultureInfo.InvariantCulture))));
        }
        var report = binding.Report with { Records = records, Units = units, ReaderNotes = notes };
        MachineDynamicsSet set = new MachineDynamicsSet(binding.Set.Name, binding.Set.Path, entries).WithRenewables(binding.Set.Renewables);
        return new DynamicsModelBinding(report, set);
    }

    /// <summary>Place the exciter-role assignments into the study's exciter list (aligned to <c>model.Generators</c>), each as a
    /// fresh <see cref="RmsExciter"/> wrapping a fresh controller. Replaces whatever the DYR binding put at that machine.</summary>
    public static void ApplyExciters(PsseParseResult raw, IList<RmsExciter?> list, IReadOnlyList<UserModelAssignment> assignments)
    {
        foreach (UserModelAssignment a in assignments)
        {
            if (a.Model.Role != "exciter") continue;
            int gi = GeneratorIndex(raw.Model, raw, a.Machine);
            list[gi] = RmsExciter.FromUser(new UserModelFieldController(a.Model), "USER:" + a.Model.Id);
        }
    }

    private static string Role(UserModelAssignment a)
        => a.Model.Role is "governor" or "exciter" ? a.Model.Role
            : throw new InvalidDataException($"user models: {a.Machine}: the model {a.Model.Id} has role '{a.Model.Role}'; a study binds governor and exciter models "
                + "(a stabiliser is part of the exciter model's diagram).");

    private static void ReplaceRecords(List<DyrRecordCoverage> records, int gi, DynamicsMachineId machine, string[] roles, string status, string semantics,
        UserModelAssignment a, string role)
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
            records.Add(new DyrRecordCoverage(0, machine.Bus, "USER:" + a.Model.Id, machine.Id, role, gi, status,
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
