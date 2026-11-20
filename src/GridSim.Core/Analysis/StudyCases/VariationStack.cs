using GridSim.Core.Analysis.PlanningStudies;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: variations. A variation is a named, ordered list of dated operations (the RC3 operation vocabulary, with
// effectiveFrom and effectiveTo) carrying an expansion-stage label. It is exactly a RC3 PlanningProject plus the label,
// so dated application, expiry and conflict refusal are the unchanged RC3 behaviour.

/// <summary>One variation. <see cref="ExpansionStage"/> is a free label (for example "2027 reinforcement") recorded in
/// the derivation; it does not change which operations are active, the dates do.</summary>
public sealed record Variation(string Id, string? Description, string? ExpansionStage, IReadOnlyList<PlanningOperation> Operations);

/// <summary>The variations of a study case, in declared order, and the explicit application order for those that may
/// change one field at one instant (the RC3 <c>projectOrder</c> rule: unlisted variations that clash are refused).</summary>
public sealed record VariationSet(IReadOnlyList<Variation> Variations, IReadOnlyList<string> Order)
{
    public static VariationSet Empty { get; } = new([], []);

    public IReadOnlyList<PlanningProject> ToProjects() => Variations.Select(v => new PlanningProject(v.Id, v.Operations)).ToArray();

    /// <summary>The expansion-stage label of a project ID (null for a scenario project or an unlabelled variation).</summary>
    public string? StageOf(string projectId) => Variations.FirstOrDefault(v => v.Id == projectId)?.ExpansionStage;

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Variation v in Variations)
            if (!seen.Add(v.Id)) throw new StudyCaseException($"Duplicate variation ID '{v.Id}'.");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in Order)
        {
            if (!seen.Contains(id)) throw new StudyCaseException($"variationOrder names '{id}', which is not a declared variation.");
            if (!listed.Add(id)) throw new StudyCaseException($"Duplicate variationOrder entry '{id}'.");
        }
    }
}
