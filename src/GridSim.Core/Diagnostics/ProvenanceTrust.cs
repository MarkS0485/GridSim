using System.Text.Json;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// N327 part 2 (CF5): the provenance trust rules of technical chapter 8 ("Trust order and the downgrade-only rule"), in
/// code. Until CF5 they were reading rules for authors and reviewers only (D-DOC-212); these are the parts that can be
/// checked mechanically without changing what any valid input does:
/// <list type="bullet">
/// <item>The trust order Recorded &gt; DerivedFromRecorded &gt; Interpolated = Synthetic &gt; CuratedPlaceholder
/// (<see cref="Rank"/>). <see cref="ProvenanceTag.Counterfactual"/> and <see cref="ProvenanceTag.UnvalidatedAtScale"/>
/// are OUTPUT tags (<see cref="IsOutputOnly"/>): they label a conclusion and have no place in the input order.</item>
/// <item>An output tag is never an input (<see cref="RequireInput"/>): a counterfactual is never an input to another
/// replay, and an unvalidated-at-scale result is never fed back as data. Enforced at the replay entry
/// (<c>ReplayValidator.Validate</c>, every tag of the event) and at the case editor's save gate (any provenance tag in a
/// case file).</item>
/// <item>Trust only moves down (<see cref="RequireNotRaised"/>): a value computed from inputs may carry at most the
/// weakest input's trust. For combinators that merge tags.</item>
/// </list>
/// N359 (CF6): the three rules CF5 left to reviewers are now code too:
/// <list type="bullet">
/// <item>an event is replayed as a reproduction of history only when its effective tag (the weakest of its parts,
/// <c>ReplayValidator.EffectiveProvenance</c>, <see cref="Weakest"/>) is Recorded or DerivedFromRecorded
/// (<c>ReplayValidator.RequireRecordedReplay</c>, called by replay-event, refusing by name); the study verbs (scenario,
/// fleet, evidence, h0-study) accept a placeholder or synthetic event and their outputs inherit the weaker tag (main's
/// ruling on CF6-F3 / F4);</item>
/// <item>a VoLL figure keeps its policy tag (<c>AvoidedCost.VollBaseProvenance</c>): <c>--voll-source</c> records the
/// citation but no longer raises the tag, and <c>AvoidedCost.FromLfddAvoided</c> refuses a raised VoLL tag through
/// <see cref="RequireNotRaised"/>;</item>
/// <item>the harmonic export headline is the weakest of its sources' tags among Recorded / DerivedFromRecorded (else
/// CuratedPlaceholder), checked by <see cref="RequireNotRaised"/> (<c>HarmonicExport.HeadlineProvenance</c>); it used to
/// read Recorded when some sources were only DerivedFromRecorded.</item>
/// </list>
/// </summary>
public static class ProvenanceTrust
{
    /// <summary>The trust rank of an input tag: 4 Recorded, 3 DerivedFromRecorded, 2 Interpolated and Synthetic, 1
    /// CuratedPlaceholder. An output tag has no rank and is refused (ArgumentException); an undefined value too.</summary>
    public static int Rank(ProvenanceTag tag) => tag switch
    {
        ProvenanceTag.Recorded => 4,
        ProvenanceTag.DerivedFromRecorded => 3,
        ProvenanceTag.Interpolated or ProvenanceTag.Synthetic => 2,
        ProvenanceTag.CuratedPlaceholder => 1,
        ProvenanceTag.Counterfactual or ProvenanceTag.UnvalidatedAtScale =>
            throw new ArgumentException($"{tag} is an output tag: it labels a conclusion and has no input trust rank.", nameof(tag)),
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "not a defined provenance tag."),
    };

    /// <summary>True for the tags that label outputs only: <see cref="ProvenanceTag.Counterfactual"/> and
    /// <see cref="ProvenanceTag.UnvalidatedAtScale"/>.</summary>
    public static bool IsOutputOnly(ProvenanceTag tag) => tag is ProvenanceTag.Counterfactual or ProvenanceTag.UnvalidatedAtScale;

    /// <summary>Refuse (<see cref="InvalidDataException"/>, so every handler that reports bad input data reports it) an output tag, or an undefined one, used as an input.
    /// <paramref name="what"/> names the datum in the message.</summary>
    public static void RequireInput(ProvenanceTag tag, string what)
    {
        if (!Enum.IsDefined(tag))
            throw new InvalidDataException($"{what} carries provenance {(int)tag}, which is not a defined tag.");
        if (IsOutputOnly(tag))
            throw new InvalidDataException(
                $"{what} is tagged {tag}, an output tag: a {(tag == ProvenanceTag.Counterfactual ? "counterfactual is never an input to another replay" : "result unvalidated at scale is never fed back as data")} (technical ch. 8, trust order).");
    }

    /// <summary>Refuse (<see cref="InvalidDataException"/>, so every handler that reports bad input data reports it) a computed tag stronger than the weakest of its inputs
    /// (trust only moves down), or any output tag among the inputs.</summary>
    public static void RequireNotRaised(IEnumerable<ProvenanceTag> inputs, ProvenanceTag result, string what)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        int weakest = int.MaxValue;
        ProvenanceTag weakestTag = ProvenanceTag.Recorded;
        int k = 0;
        foreach (ProvenanceTag t in inputs)
        {
            RequireInput(t, $"{what}: input {k}");
            int r = Rank(t);
            if (r < weakest) { weakest = r; weakestTag = t; }
            k++;
        }
        if (k == 0) throw new ArgumentException($"{what}: a computed tag needs at least one input tag.", nameof(inputs));
        if (IsOutputOnly(result)) return;   // labelling a result as an output never raises trust
        if (Rank(result) > weakest)
            throw new InvalidDataException($"{what} claims {result} but its weakest input is {weakestTag}: trust only moves down (technical ch. 8).");
    }

    /// <summary>
    /// CF6 (main's ruling on N359 / CF6-F3): the weakest of several input tags, the tag a datum built from all of them
    /// may carry (trust only moves down). The lowest <see cref="Rank"/> wins; when two DIFFERENT tags share the lowest
    /// rank (Interpolated and Synthetic, both 2) the result is <see cref="ProvenanceTag.CuratedPlaceholder"/> (rank 1),
    /// the same rule as <c>AvoidedCost.Weakest</c>, so the answer never claims either one's meaning for the other. Every
    /// input must be an input tag (<see cref="RequireInput"/>, <see cref="InvalidDataException"/> otherwise) and there must
    /// be at least one (<see cref="ArgumentException"/>). <paramref name="what"/> names the datum in a refusal.
    /// </summary>
    public static ProvenanceTag Weakest(IEnumerable<ProvenanceTag> tags, string what)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ProvenanceTag weakest = ProvenanceTag.Recorded;
        int weakestRank = int.MaxValue;
        bool mixedAtLowest = false;
        int k = 0;
        foreach (ProvenanceTag t in tags)
        {
            RequireInput(t, $"{what}: part {k}");
            int r = Rank(t);
            if (r < weakestRank) { weakestRank = r; weakest = t; mixedAtLowest = false; }
            else if (r == weakestRank && t != weakest) mixedAtLowest = true;
            k++;
        }
        if (k == 0) throw new ArgumentException($"{what}: the weakest of no tags is undefined.", nameof(tags));
        return mixedAtLowest ? ProvenanceTag.CuratedPlaceholder : weakest;
    }

    /// <summary>
    /// Every provenance tag written in a JSON document (a case file, an asset-class library): the <c>"tag"</c> of an object
    /// under a member named <c>"prov"</c>, of an object under a member named <c>"provenance"</c>, or of each object one
    /// level inside a <c>"provenance"</c> map (<c>{"_default": {...}, "param": {...}}</c>). Returns (JSON pointer of the
    /// tag, tag text) in document order. Text that is not a tag name is returned as written (the schema reports it).
    /// </summary>
    public static IReadOnlyList<(string Pointer, string Tag)> TagsIn(JsonElement root)
    {
        var found = new List<(string, string)>();
        Walk(root, "", parentName: null, found);
        return found;
    }

    /// <summary>The output tags (<see cref="IsOutputOnly"/>) among <see cref="TagsIn"/>: each is an output used as an input.</summary>
    public static IReadOnlyList<(string Pointer, string Tag)> OutputTagsIn(JsonElement root)
        => TagsIn(root).Where(t => Enum.TryParse(t.Tag, ignoreCase: false, out ProvenanceTag p) && Enum.IsDefined(p) && IsOutputOnly(p)).ToList();

    private static void Walk(JsonElement e, string pointer, string? parentName, List<(string, string)> found)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            bool provObject = parentName is "prov" or "provenance";
            foreach (JsonProperty p in e.EnumerateObject())
            {
                string child = pointer + "/" + p.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                if (provObject && p.Name == "tag" && p.Value.ValueKind == JsonValueKind.String)
                {
                    found.Add((child, p.Value.GetString()!));
                    continue;
                }
                // One level inside a "provenance" map: each entry is itself a provenance object.
                string? name = parentName == "provenance" && p.Value.ValueKind == JsonValueKind.Object ? "prov" : p.Name;
                Walk(p.Value, child, name, found);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (JsonElement item in e.EnumerateArray())
            {
                Walk(item, pointer + "/" + i, null, found);
                i++;
            }
        }
    }
}
