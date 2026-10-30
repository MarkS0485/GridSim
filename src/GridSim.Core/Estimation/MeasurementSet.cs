using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Core.Estimation;

/// <summary>
/// A versioned, self-describing set of measurements over one network case - the input to the
/// <see cref="WlsStateEstimator"/>. Wire format <c>gridsim-measurements/1</c> (camelCase JSON,
/// enums by name). Like every dated input to the engine, the <see cref="TakenUtc"/> stamp (required on
/// load since D-DOC-061) is checked against the historical horizon: the estimator can only ever
/// reconstruct the past, never nowcast (see <see cref="ForwardInferenceGuard"/>).
/// </summary>
public sealed record MeasurementSet
{
    /// <summary>The set's base MVA must match the model's to within this (MVA); it absorbs only the rounding of
    /// a value written to text and read back (R174).</summary>
    public const double BaseMvaMatchToleranceMva = 1e-9;

    /// <summary>The wire-format identifier this type reads and writes.</summary>
    public const string SchemaId = "gridsim-measurements/1";

    /// <summary>NCSC CAF B4 resource limit: the most measurements <see cref="Load"/> accepts from one file by
    /// default. Set far above any real case (gb-full is of order a few hundred thousand rows), so it never refuses a
    /// legitimate set; it only refuses an implausibly large file that could be an attempt to exhaust memory. A caller
    /// with a genuinely larger set passes a higher limit explicitly.</summary>
    public const int DefaultMaxMeasurements = 20_000_000;

    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): these are exactly the options this file
    // has always used, with one change of form only. Enums are still written and read by name, but by the
    // source-generated per-enum JsonStringEnumConverter<TEnum> (UseStringEnumConverter on MeasurementSetJsonContext)
    // instead of the reflection-based JsonStringEnumConverter: both write the member name and both still accept an
    // integer on read. No runtime reflection is used.
    // The metadata comes from the source-generated MeasurementSetJsonContext (below), with GridSim.Core.IO.Json.InitMemberDefaults
    // chained in front of it: that resolver describes MeasurementSet (and its nested init-only types) so a member the file
    // leaves out keeps its initialiser, as it did under reflection (see InitMemberDefaults for why the generated
    // metadata alone would not). Internal, not private, so JsonSourceGenEquivalenceTests uses this very instance.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
            GridSim.Core.IO.Json.InitMemberDefaults.Resolver, MeasurementSetJsonContext.Default),
    };

    /// <summary>Phase 0.1: the metadata every read and write of this type goes through (cached by the options).</summary>
    internal static System.Text.Json.Serialization.Metadata.JsonTypeInfo<MeasurementSet> JsonInfo =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<MeasurementSet>)JsonOptions.GetTypeInfo(typeof(MeasurementSet));

    /// <summary>Schema tag; must equal <see cref="SchemaId"/> for the file to be accepted.</summary>
    public string Schema { get; init; } = SchemaId;

    /// <summary>Name of the network case the measurements describe (informational).</summary>
    public string Case { get; init; } = "";

    /// <summary>System base the authoring side assumed, MVA. Checked against the model on <see cref="Validate"/>.</summary>
    public double BaseMva { get; init; } = 100.0;

    /// <summary>When the telemetry was taken (UTC). Required by <see cref="Load"/> (D-DOC-061: an undated set is
    /// refused) and it must be historical; in memory it stays nullable so a caller can build a set before dating it.</summary>
    public DateTime? TakenUtc { get; init; }

    /// <summary>Free-text provenance note (e.g. the synthetic sampler's seed and coverage).</summary>
    public string? Note { get; init; }

    public IReadOnlyList<Measurement> Measurements { get; init; } = Array.Empty<Measurement>();

    /// <summary>Load a set from JSON, rejecting unknown schemas and non-historical timestamps.</summary>
    /// <param name="path">The measurement-set file.</param>
    /// <param name="utcNow">The "now" for the horizon check (null uses the real clock).</param>
    /// <param name="maxMeasurements">A resource-limit cap on the number of rows (B4); the default is far above any
    /// real case. A set larger than this is refused with <see cref="InvalidDataException"/>.</param>
    public static MeasurementSet Load(string path, DateTime? utcNow = null, int maxMeasurements = DefaultMaxMeasurements)
    {
        string json = File.ReadAllText(path);
        // D-DOC-070: the file must declare its schema; a file with no "schema" member used to load as a set because the
        // Schema property defaults to SchemaId.
        GridSim.Core.IO.SchemaMember.Require(json, path, SchemaId, "measurement set");
        RequireKindAndValue(json, path);
        MeasurementSet s = JsonSerializer.Deserialize(json, JsonInfo)
            ?? throw new InvalidDataException($"Not a measurement set: {path}");
        if (!string.Equals(s.Schema, SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"'{path}' declares schema '{s.Schema}'; this build reads '{SchemaId}'.");
        // D-DOC-061 (October 2026, best judgement recorded in the doc3 notes): a measurement set is telemetry, so it must
        // say when it was taken. The guard used to run only "when takenUtc is present", so an undated set (or a live set
        // with its date stripped) loaded with no horizon check at all. It is now refused (ForwardInferenceException,
        // reason Undated), after the schema and field checks so a malformed file still reports what is malformed.
        // Every set GridSim writes is dated (the synthetic sampler, the RTU and PMU mappers), and so is every shipped
        // fixture.
        // B4 resource limit: refuse an implausibly large set before the horizon check, so a hostile file is turned
        // away on size rather than processed.
        if (s.Measurements.Count > maxMeasurements)
            throw new InvalidDataException(
                $"'{path}' holds {s.Measurements.Count} measurements, above the {maxMeasurements} limit; " +
                "pass a higher maxMeasurements only if this large a set is expected.");
        DateTime taken = ForwardInferenceGuard.RequireDated(s.TakenUtc, $"measurement set '{path}'", "takenUtc");
        ForwardInferenceGuard.RejectFuture(taken, utcNow, "measurement set");
        return s;
    }

    /// <summary>
    /// Every measurement entry must state its <c>kind</c> and its <c>value</c> (docs-programme finding). The record is
    /// deserialised through its constructor, which fills a missing parameter with its default, so a reading with no
    /// value loaded as 0 and one with no kind loaded as the first enum member, <see cref="MeasurementKind.VmPu"/>: a
    /// typo in a hand-written file became a confident 0 pu voltage reading. Both are now required. The check reads the
    /// raw JSON (property names compared case-insensitively, as the deserialiser does) and throws one
    /// <see cref="InvalidDataException"/> naming every offending entry by its index (and its bus or branch when given).
    /// <see cref="Save"/> always writes both, so every file this program wrote passes unchanged.
    /// </summary>
    private static void RequireKindAndValue(string json, string path)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;   // the deserialiser reports the wrong shape
        JsonElement? list = null;
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, "measurements", StringComparison.OrdinalIgnoreCase)) list = p.Value;
        if (list is not { ValueKind: JsonValueKind.Array } arr) return;   // absent: an empty set, as before

        static bool Has(JsonElement e, string name) =>
            e.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        static string Where(JsonElement e)
        {
            foreach (string key in new[] { "bus", "from" })
                foreach (JsonProperty p in e.EnumerateObject())
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                        return key == "bus" ? $" (bus {p.Value})" : $" (branch from {p.Value})";
            return "";
        }

        var problems = new List<string>();
        int i = 0;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) problems.Add($"measurement #{i}: is not an object");
            else
            {
                if (!Has(e, "kind")) problems.Add($"measurement #{i}{Where(e)}: missing 'kind'");
                if (!Has(e, "value")) problems.Add($"measurement #{i}{Where(e)}: missing 'value'");
            }
            i++;
        }
        if (problems.Count > 0)
            throw new InvalidDataException(
                $"'{path}': every measurement needs a 'kind' and a 'value' ({problems.Count} problem(s)): " +
                string.Join("; ", problems.Take(20)) + (problems.Count > 20 ? $"; ... and {problems.Count - 20} more" : ""));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonInfo));
    }

    /// <summary>
    /// Save to a NEW file: never replaces an existing one. If <paramref name="path"/> is taken, a "-dupN" suffix is
    /// added before the extension (see <see cref="GridSim.Core.IO.NeverOverwrite"/>). Returns the path actually written. Used by
    /// the interposer, whose window files must never overwrite each other.
    /// </summary>
    public string SaveWithoutOverwrite(string path) => GridSim.Core.IO.NeverOverwrite.WriteAllText(path, JsonSerializer.Serialize(this, JsonInfo));

    /// <summary>
    /// Structural validation against the model the set will be estimated on. Throws a single
    /// <see cref="InvalidDataException"/> naming every offender: non-finite values, non-positive or
    /// non-finite sigmas, bus ids the model doesn't contain, branch references that resolve to no
    /// branch (or ambiguously to several parallel circuits without a <see cref="Measurement.Circuit"/>
    /// name), and a base-MVA mismatch. A NaN or a zero-weight measurement caught here is one that
    /// cannot silently poison the gain matrix later.
    /// </summary>
    public void Validate(GridModel model)
    {
        var problems = new List<string>();

        // GR-528: a set that declares a base of 0, a negative one or NaN skipped the base check entirely; it is refused.
        if (!(BaseMva > 0) || !double.IsFinite(BaseMva))
            problems.Add($"set baseMva {BaseMva} is not a finite number above 0");
        else if (model.BaseMva > 0 && Math.Abs(BaseMva - model.BaseMva) > BaseMvaMatchToleranceMva)
            problems.Add($"set baseMva {BaseMva} does not match model BaseMva {model.BaseMva}");

        var busIds = new HashSet<int>(model.Buses.Select(b => b.Id));
        for (int i = 0; i < Measurements.Count; i++)
        {
            Measurement m = Measurements[i];
            string where = $"measurement #{i} ({m.Kind} @ {m.Describe()})";
            if (!double.IsFinite(m.Value))
                problems.Add($"{where}: value {m.Value} is not finite");
            if (!double.IsFinite(m.Sigma) || m.Sigma <= 0)
                problems.Add($"{where}: sigma {m.Sigma} must be finite and > 0");

            if (m.IsBranch)
            {
                if (!TryResolveBranch(model, m, out _, out string? error))
                    problems.Add($"{where}: {error}");
            }
            else if (!busIds.Contains(m.Bus))
            {
                problems.Add($"{where}: bus {m.Bus} does not exist in the model");
            }
        }

        if (problems.Count > 0)
            throw new InvalidDataException(
                $"Measurement set '{Case}' failed validation:{Environment.NewLine}  " +
                string.Join($"{Environment.NewLine}  ", problems));
    }

    /// <summary>
    /// Resolve a branch measurement to its index in <see cref="GridModel.Branches"/>. Matches the
    /// stored orientation only (From->To as authored); parallel circuits between the same endpoints
    /// need the <see cref="Measurement.Circuit"/> to be unambiguous - either the branch's name, or
    /// the ordinal form <c>"#k"</c> (model branch index) for unnamed parallels (IEEE imports carry
    /// no circuit names; the synthetic sampler emits this form).
    /// </summary>
    internal static bool TryResolveBranch(GridModel model, Measurement m, out int branchIndex, out string? error)
    {
        branchIndex = -1;
        error = null;

        if (m.Circuit is not null && m.Circuit.StartsWith('#')
            && int.TryParse(m.Circuit.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out int ordinal))
        {
            if (ordinal >= 0 && ordinal < model.Branches.Count
                && model.Branches[ordinal].From == m.From && model.Branches[ordinal].To == m.To)
            {
                branchIndex = ordinal;
                return true;
            }
            error = $"circuit '{m.Circuit}' does not name branch {m.From}->{m.To} (ordinal out of range or endpoints differ)";
            return false;
        }
        int found = -1, count = 0;
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (br.From != m.From || br.To != m.To) continue;
            if (m.Circuit is not null && !string.Equals(br.Name, m.Circuit, StringComparison.Ordinal)) continue;
            found = k;
            count++;
        }

        if (count == 1) { branchIndex = found; return true; }
        error = count == 0
            ? $"no branch {m.From}->{m.To}{(m.Circuit is null ? "" : $" named '{m.Circuit}'")} in the model"
            : $"{count} parallel branches {m.From}->{m.To} match; set circuit to the branch name to disambiguate";
        return false;
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for <see cref="MeasurementSet"/> and every type it reaches, so a set
/// loads and saves without runtime reflection (NativeAOT). Metadata mode only: the bytes are written by the same
/// converters the reflection path used, never by a separately generated fast path. Enums by name, as before. The
/// runtime options (naming, key case, null handling, indentation) are passed in by <see cref="MeasurementSet"/>.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MeasurementSet))]
internal sealed partial class MeasurementSetJsonContext : JsonSerializerContext;
