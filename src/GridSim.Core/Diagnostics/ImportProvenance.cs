namespace GridSim.Core.Diagnostics;

/// <summary>
/// RC4 R4-11 part C: the provenance label of an import from a file format known only from its public description.
/// It follows the output-only pattern of <see cref="ProvenanceTag.Counterfactual"/> and
/// <see cref="ProvenanceTag.UnvalidatedAtScale"/> (it labels a result and is never an input to anything), but it is a SEPARATE
/// type on purpose: <see cref="ProvenanceTag"/> is pinned by existing guard tests (the member count, both JSON schema enums,
/// the trust ranking), and an existing test may not change without a proof that the code is wrong. Promoting this value into
/// <see cref="ProvenanceTag"/> (with those guards updated deliberately) is a decision for the owner and is recorded in the
/// R4-11c report.
/// </summary>
public enum ImportProvenanceTag
{
    /// <summary>Read with a reader built from a public description of the format only; no real file of that format was tested.
    /// Not a claim about the originating tool's behaviour.</summary>
    FormatFromPublicDescription,
}
