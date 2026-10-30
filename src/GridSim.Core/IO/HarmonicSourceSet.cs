using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>
/// A versioned, self-describing set of harmonic current sources over one network case - the input to the
/// harmonic engine. Wire format <c>gridsim-harmonics/1</c> (camelCase JSON, enums by name), the authoritative
/// contract for which lives in GDA (<c>NetworkModel/HarmonicDataSchema.json</c>) so GDA or a third party can
/// feed spectra the same way. Like every dated input, an optional <see cref="TakenUtc"/> stamp is checked
/// against the historical horizon on load - measured harmonic data is a historical replay, never a nowcast
/// (see <see cref="ForwardInferenceGuard"/>). Assumed-typical library spectra load tagged
/// <see cref="ProvenanceTag.CuratedPlaceholder"/>; measured spectra load tagged
/// <see cref="ProvenanceTag.Recorded"/> - the same solver either way.
/// </summary>
public sealed record HarmonicSourceSet
{
    /// <summary>The wire-format identifier this type reads and writes.</summary>
    public const string SchemaId = "gridsim-harmonics/1";

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now resolved through the source-generated HarmonicSourceSetJsonContext (below) so no runtime reflection is needed.
    // Enums are still by name, through the source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter
    // on the context) in place of the reflection-based JsonStringEnumConverter: both write the member name and both
    // still accept an integer on read.
    // The metadata comes from the source-generated HarmonicSourceSetJsonContext (below), with GridSim.Core.IO.Json.InitMemberDefaults
    // chained in front of it: that resolver describes HarmonicSourceSet (and its nested init-only types) so a member the file
    // leaves out keeps its initialiser, as it did under reflection (see InitMemberDefaults for why the generated
    // metadata alone would not). Internal, not private, so JsonSourceGenEquivalenceTests uses this very instance.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // An authored input: a misspelt key ("fundamentalCurrent") is refused rather than defaulted (it loaded a source
        // with 0 current that injected nothing).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
            GridSim.Core.IO.Json.InitMemberDefaults.Resolver, HarmonicSourceSetJsonContext.Default),
    };

    /// <summary>Phase 0.1: the metadata every read and write of this type goes through (cached by the options).</summary>
    internal static System.Text.Json.Serialization.Metadata.JsonTypeInfo<HarmonicSourceSet> JsonInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<HarmonicSourceSet>)JsonOptions.GetTypeInfo(typeof(HarmonicSourceSet));

    /// <summary>Schema tag; must equal <see cref="SchemaId"/> for the file to be accepted.</summary>
    public string Schema { get; init; } = SchemaId;

    /// <summary>Name of the network case the sources describe (informational).</summary>
    public string Case { get; init; } = "";

    /// <summary>System base the authoring side assumed, MVA. Checked against the model on <see cref="Validate"/>.</summary>
    public double BaseMva { get; init; } = 100.0;

    /// <summary>When the spectra were measured (UTC). Required on load when any source is measured (provenance
    /// Recorded or DerivedFromRecorded, D-DOC-061); optional for an all-assumed library set; when present it must be
    /// historical.</summary>
    public DateTime? TakenUtc { get; init; }

    /// <summary>Free-text provenance note.</summary>
    public string? Note { get; init; }

    /// <summary>The harmonic current sources.</summary>
    public IReadOnlyList<HarmonicSource> Sources { get; init; } = Array.Empty<HarmonicSource>();

    /// <summary>Load a set from JSON, rejecting unknown schemas and non-historical timestamps.</summary>
    public static HarmonicSourceSet Load(string path, DateTime? utcNow = null)
    {
        string json = File.ReadAllText(path);
        // D-DOC-070: the file must declare its schema; a file with no "schema" member used to load as a set because the
        // Schema property defaults to SchemaId.
        SchemaMember.Require(json, path, SchemaId, "harmonic source set");
        HarmonicSourceSet s = JsonSerializer.Deserialize(json, JsonInfo)
            ?? throw new InvalidDataException($"Not a harmonic source set: {path}");
        if (!string.Equals(s.Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"'{path}' declares schema '{s.Schema}'; this build reads '{SchemaId}'.");
        // D-DOC-061 (October 2026, best judgement recorded in the doc3 notes): the guard used to run only "when takenUtc
        // is present", so an undated set of MEASURED spectra loaded with no horizon check. A harmonic set has two kinds
        // of source, and the rule follows them:
        //  - a MEASURED source (provenance Recorded or DerivedFromRecorded) is telemetry: the set must carry takenUtc,
        //    and it must be historical; an undated one is refused (ForwardInferenceException, reason Undated);
        //  - an ASSUMED source (CuratedPlaceholder, Synthetic: an equipment library spectrum such as a G5 six-pulse
        //    profile) describes the equipment, not an instant of the grid, so an all-assumed set may stay undated.
        // A dated set is checked against the horizon whatever its sources, as before.
        if (s.Sources?.Any(src => src is { Provenance: ProvenanceTag.Recorded or ProvenanceTag.DerivedFromRecorded }) == true)
            _ = ForwardInferenceGuard.RequireDated(s.TakenUtc,
                $"harmonic source set '{path}' (it holds measured spectra, provenance Recorded or DerivedFromRecorded)", "takenUtc");
        if (s.TakenUtc is DateTime taken)
            ForwardInferenceGuard.RejectFuture(taken, utcNow, "harmonic source set");
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonInfo));
    }

    /// <summary>
    /// Structural validation against the model the sources inject into. Throws a single
    /// <see cref="InvalidDataException"/> naming every offender: a bus id the model lacks, a non-positive
    /// harmonic order, a non-finite magnitude/phase, or a base-MVA mismatch.
    /// </summary>
    public void Validate(GridModel model)
    {
        var problems = new List<string>();

        // GR-653: a declared base that is not a finite number above 0 used to skip the base check entirely.
        if (!(BaseMva > 0) || !double.IsFinite(BaseMva))
            problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"set baseMva {BaseMva} is not a finite number above 0"));
        else if (model.BaseMva > 0 && Math.Abs(BaseMva - model.BaseMva) > 1e-9)
            problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"set baseMva {BaseMva} does not match model BaseMva {model.BaseMva}"));

        var busIds = new HashSet<int>(model.Buses.Select(b => b.Id));
        for (int i = 0; i < Sources.Count; i++)
        {
            HarmonicSource src = Sources[i];
            string where = $"source #{i} ({src.SourceType} @ bus {src.Bus})";
            if (!busIds.Contains(src.Bus))
                problems.Add($"{where}: bus {src.Bus} does not exist in the model");
            if (!double.IsFinite(src.FundamentalCurrentPu))
                problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{where}: fundamental current {src.FundamentalCurrentPu} is not finite"));
            // GR-652: each order once (InjectionAt reads the first, so a repeated order's later entries were ignored), and
            // a magnitude of 0 % or more.
            var seenOrders = new HashSet<int>();
            for (int k = 0; k < src.Spectrum.Count; k++)
            {
                HarmonicComponent c = src.Spectrum[k];
                if (c.Order < 1)
                    problems.Add($"{where}: component #{k} order {c.Order} must be >= 1");
                else if (!seenOrders.Add(c.Order))
                    problems.Add($"{where}: component #{k} repeats order {c.Order}");
                if (c.MagnitudePercent < 0)
                    problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{where}: component #{k} (order {c.Order}) has a negative magnitude {c.MagnitudePercent} %"));
                if (!double.IsFinite(c.MagnitudePercent) || !double.IsFinite(c.PhaseDeg))
                    problems.Add($"{where}: component #{k} (order {c.Order}) has a non-finite magnitude/phase");
            }
        }

        if (problems.Count > 0)
            throw new InvalidDataException(
                $"Harmonic source set '{Case}' failed validation:{Environment.NewLine}  " +
                string.Join($"{Environment.NewLine}  ", problems));
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a harmonic source set and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path. Enums by name, as before.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HarmonicSourceSet))]
internal sealed partial class HarmonicSourceSetJsonContext : JsonSerializerContext;
