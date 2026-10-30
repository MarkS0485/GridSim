namespace GridSim.Core.Diagnostics;

/// <summary>
/// Where a datum came from. Every series the evidence engine emits carries one of these so a reviewer can
/// trust the provenance. <see cref="Interpolated"/>/<see cref="Synthetic"/> data may improve replay
/// fidelity but must never be fed to the forward integrator as if it were a recorded input.
/// <para>What the code does and does not enforce (D-DOC-212, DOC-17; this summary used to say the
/// <see cref="ForwardInferenceGuard"/> refuses non-recorded data, but that guard checks time stamps only and never
/// reads a tag). The trust order Recorded &gt; DerivedFromRecorded &gt; Interpolated, Synthetic &gt; CuratedPlaceholder,
/// the rule that trust only moves down (a datum gains a stronger tag only by being re-sourced from a primary record),
/// and the rule that a <see cref="Counterfactual"/> is never an input to another replay are rules for authors and
/// reviewers: each tag is fixed at the site that creates the datum, and no code ranks tags or refuses an input by
/// its tag. The one combinator that merges two tags, <c>AvoidedCost.Weakest</c>, never returns a tag stronger than
/// either input (pinned by <c>Doc17TextHygieneTests</c>), and the model-output records default to non-recorded tags
/// (FrequencyExportMeta: Synthetic, H0StudyMeta: Counterfactual, a storage replay: Counterfactual). Technical
/// chapter 8 lists every site that assigns or computes a tag.</para>
/// <para>CF5 (N327 part 2): the checkable parts are now code, in <see cref="ProvenanceTrust"/>: the trust ranks, the
/// refusal of an output tag (Counterfactual, UnvalidatedAtScale) as an input (at the replay entry and the case editor's
/// save gate) and a downgrade-only check for combinators. What stays a reviewer rule is listed there.</para>
/// </summary>
public enum ProvenanceTag
{
    /// <summary>Directly from an archived record (NESO/GDA/published event log).</summary>
    Recorded,
    /// <summary>Computed from recorded data (e.g. inertia inferred from a recorded RoCoF+deltaP).</summary>
    DerivedFromRecorded,
    /// <summary>A placeholder standing in for real data not yet sourced - always accompanied by [VERIFY].</summary>
    CuratedPlaceholder,
    /// <summary>Model-generated, not from any record (e.g. a synthetic demand curve for engine testing).</summary>
    Synthetic,
    /// <summary>A counterfactual result (what a device WOULD have done) - never an input to another replay.</summary>
    Counterfactual,
    /// <summary>Gap-filled / augmented replay input (e.g. ML/MoE). Usable to improve fidelity, never as an event source.</summary>
    Interpolated,

    /// <summary>
    /// A distributed-inertia-model result with no GB-scale reference (spatial RoCoF spread, propagation
    /// delay). It is self-consistent and passes the reduction/invariant checks, but its absolute value at
    /// scale is NOT validated (it awaits PMU/DFR field data) and must never be presented as validated.
    /// </summary>
    UnvalidatedAtScale,
}
