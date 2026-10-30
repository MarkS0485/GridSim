using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>
/// Checks the JSON case writers (v1 <see cref="JsonCaseWriter"/> and <see cref="V2CaseWriter"/>) share before they
/// write anything.
/// </summary>
internal static class CaseWriterChecks
{
    /// <summary>
    /// GR-164: the writers leave out-of-service branches out of the case (and say so), but used to write boundaries and
    /// OLTC records unchanged, so a record naming such a branch made the written case unloadable (the loader refuses a
    /// boundary or OLTC name that matches no branch), and only after the files existed. Refused up front instead, naming
    /// every such record. A name is affected when it matches an out-of-service branch and no in-service one.
    /// </summary>
    public static void RefuseReferencesToOmittedBranches(GridModel model, string writer)
    {
        var inService = new HashSet<string>(StringComparer.Ordinal);
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (Branch br in model.Branches)
        {
            if (br.Name is null) continue;
            (br.InService ? inService : omitted).Add(br.Name);
        }
        omitted.ExceptWith(inService);
        if (omitted.Count == 0) return;

        var problems = new List<string>();
        foreach (Boundary b in model.Boundaries)
            foreach (BoundaryBranch bb in b.Branches)
                if (omitted.Contains(bb.Branch))
                    problems.Add($"boundary '{b.Name}' lists branch '{bb.Branch}'");
        foreach (Oltc o in model.Oltcs)
            if (omitted.Contains(o.Transformer))
                problems.Add($"the OLTC record names transformer '{o.Transformer}'");
        if (problems.Count > 0)
            throw new InvalidDataException(
                $"{writer}: {string.Join("; ", problems)}, which is out of service. Out-of-service branches are not written, so " +
                "the written case could not be loaded. Return the branch to service, or remove it from the boundary / OLTC record, first.");
    }

    /// <summary>A writer whose format cannot hold a two-terminal LCC link refuses a model that has one, naming the links,
    /// rather than write a case whose power flow differs from the model's.</summary>
    public static void RefuseLccLinks(GridModel model, string writer)
    {
        if (model.LccLinks.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.LccLinks.Count} two-terminal LCC HVDC link(s) ({string.Join(", ", model.LccLinks.Select(l => l.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
        if (model.VscLinks.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.VscLinks.Count} VSC HVDC link(s) ({string.Join(", ", model.VscLinks.Select(l => l.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
        if (model.MtdcLinks.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.MtdcLinks.Count} multi-terminal DC link(s) ({string.Join(", ", model.MtdcLinks.Select(l => l.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
        if (model.FactsDevices.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.FactsDevices.Count} FACTS device(s) ({string.Join(", ", model.FactsDevices.Select(d => d.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
        if (model.StaticVarCompensators.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.StaticVarCompensators.Count} static var compensator(s) ({string.Join(", ", model.StaticVarCompensators.Select(c => c.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
        if (model.TransformerFlowControls.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.TransformerFlowControls.Count} transformer flow control(s) ({string.Join(", ", model.TransformerFlowControls.Select(f => f.Transformer))}), " +
                "which this format cannot hold; the written case's regulated power flow would differ from the model's.");
        if (model.ZeroSequenceMutuals.Count > 0 || model.AllGenerators.Any(g => g.ZeroSequenceZPu is not null))
            throw new InvalidDataException(
                $"{writer}: the model has zero-sequence mutual couplings ({model.ZeroSequenceMutuals.Count}) or machine zero-sequence impedances ({model.AllGenerators.Count(g => g.ZeroSequenceZPu is not null)}), " +
                "which this format cannot hold; the written case's fault currents would differ from the model's.");
        if (model.InductionMachines.Count > 0)
            throw new InvalidDataException(
                $"{writer}: the model has {model.InductionMachines.Count} induction machine(s) ({string.Join(", ", model.InductionMachines.Select(m => m.Name))}), " +
                "which this format cannot hold; the written case's power flow would differ from the model's.");
    }
}
