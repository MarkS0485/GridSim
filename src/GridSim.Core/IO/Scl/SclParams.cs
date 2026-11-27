using System.Globalization;
using System.Text.Json;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Scl;

// =====================================================================================================================
// RC5 WP30: the gridsim-scl-params/1 sidecar. An SCL substation section holds topology and equipment only, no impedances
// and no set points, so every electrical value the node-breaker model needs comes from this file, keyed by SCL path:
// an equipment's substation/voltage level/bay/name, a power transformer's container path plus its name, a connectivity
// node's pathName. The reader is strict: an unknown member, a repeated member, a wrong type or a missing required value
// is refused, naming the JSON path.
//
// {
//   "format": "gridsim-scl-params/1",
//   "name": "model name",                       optional (default: the SCL Header id, else "scl")
//   "baseMva": 100, "frequencyHz": 50,          optional
//   "lines": [ { "ends": ["S1/V1/B1/L1", "S2/V1/B4/L1"], "id": "optional", "rPu": .., "xPu": .., "bPu": .., "rateAMva": .. } ],
//   "transformers": [ { "path": "S1/V1/T1", "hv": "W1", "lv": "W2", "rPu": .., "xPu": .., "bPu": .., "tapRatio": .., "phaseShiftDeg": .., "rateAMva": .. } ],
//   "transformers3w": [ { "path": "S1/T2", "hv": "W1", "mv": "W2", "lv": "W3", "r12Pu": .., "x12Pu": .., "r13Pu": .., "x13Pu": .., "r23Pu": .., "x23Pu": .., "bPu": .., "ratio": [..], "phaseShiftDeg": [..], "ratedMva": [..] } ],
//   "generators": [ { "path": "S1/V1/B2/G1", "pMw": .., "vPu": .., "qMvar": .., "qMinMvar": .., "qMaxMvar": .., "pMinMw": .., "pMaxMw": .., "slack": false } ],
//   "infeeds": [ { "path": "S2/V1/B5/GRID", "vPu": .., "pMw": 0 } ],
//   "loads": [ { "id": "LD1", "node": "S1/V1/B1/N1", "pMw": .., "qMvar": .. } ],
//   "switches": [ { "path": "S1/V1/B1/QA1", "normalOpen": true } ]
// }
//
// Lines: one end names a LIN or CAB with two terminals; two ends name two LIN/CAB pieces of one terminal each (the two
// halves of a line between substations, as an SCL file holds them). rPu, xPu and bPu are required (per unit on the
// model base). Transformers name their windings by TransformerWinding name; rPu and xPu (x12Pu, x13Pu, x23Pu for three
// windings) are required. Generators need pMw and vPu; an infeed (IFL) needs vPu and becomes a slack generator.
// =====================================================================================================================

public sealed record SclLineParams(IReadOnlyList<string> Ends, string? Id, double R, double X, double B, double RateAMva, string JsonPath);
public sealed record SclTransformerParams(string Path, string Hv, string Lv, double R, double X, double B, double TapRatio, double PhaseShiftDeg, double RateAMva, string JsonPath);
public sealed record SclTransformer3WParams(string Path, string Hv, string Mv, string Lv, double R12, double X12, double R13, double X13, double R23, double X23, double B,
    IReadOnlyList<double>? Ratio, IReadOnlyList<double>? ShiftDeg, IReadOnlyList<double>? RatedMva, string JsonPath);
public sealed record SclGeneratorParams(string Path, double Pg, double Vg, double Qg, double Qmin, double Qmax, double Pmin, double Pmax, bool Slack, string JsonPath);
public sealed record SclInfeedParams(string Path, double Vg, double Pg, string JsonPath);
public sealed record SclLoadParams(string Id, string Node, double Pd, double Qd, string JsonPath);
public sealed record SclSwitchParams(string Path, bool NormalOpen, string JsonPath);

/// <summary>A <c>gridsim-scl-params/1</c> sidecar (see the file header for the format).</summary>
public sealed class SclParams
{
    /// <summary>The format identifier.</summary>
    public const string Format = "gridsim-scl-params/1";

    public string? Name { get; init; }
    public double BaseMva { get; init; } = GridModel.DefaultBaseMva;
    public double FrequencyHz { get; init; } = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz;
    public IReadOnlyList<SclLineParams> Lines { get; init; } = [];
    public IReadOnlyList<SclTransformerParams> Transformers { get; init; } = [];
    public IReadOnlyList<SclTransformer3WParams> Transformers3W { get; init; } = [];
    public IReadOnlyList<SclGeneratorParams> Generators { get; init; } = [];
    public IReadOnlyList<SclInfeedParams> Infeeds { get; init; } = [];
    public IReadOnlyList<SclLoadParams> Loads { get; init; } = [];
    public IReadOnlyList<SclSwitchParams> Switches { get; init; } = [];

    /// <summary>The SHA-256 of the file's bytes (lower-case hex), or empty when read from text.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Read the sidecar file at <paramref name="path"/> (size-capped).</summary>
    public static SclParams Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes = InputFileLimits.ReadAllBytes(path);
        return Parse(bytes, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    /// <summary>Read sidecar text.</summary>
    public static SclParams Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json), "");
    }

    private static SclParams Parse(byte[] utf8, string sha)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(utf8), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { throw new InvalidDataException("SCL params file is not valid JSON (or repeats a member): " + e.Message, e); }
        using (doc)
        {
            var o = new Obj(doc.RootElement, "$", "format", "name", "baseMva", "frequencyHz", "lines", "transformers", "transformers3w", "generators", "infeeds", "loads", "switches");
            string format = o.Str("format");
            if (format != Format) throw new InvalidDataException($"$.format: expected \"{Format}\" but got \"{format}\".");
            double baseMva = o.NumOr("baseMva", GridModel.DefaultBaseMva);
            if (!(baseMva > 0)) throw new InvalidDataException("$.baseMva: expected a number above 0.");
            double hz = o.NumOr("frequencyHz", global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz);
            if (!(hz > 0)) throw new InvalidDataException("$.frequencyHz: expected a number above 0.");
            return new SclParams
            {
                Sha256 = sha,
                Name = o.StrOrNull("name"),
                BaseMva = baseMva,
                FrequencyHz = hz,
                Lines = o.Items("lines", e =>
                {
                    var x = new Obj(e.El, e.Path, "ends", "id", "rPu", "xPu", "bPu", "rateAMva");
                    var line = new SclLineParams(x.Strs("ends"), x.StrOrNull("id"), x.Num("rPu"), x.Num("xPu"), x.Num("bPu"), x.NumOr("rateAMva", 0), e.Path);
                    NonZeroImpedance(e.Path, "rPu", "xPu", line.R, line.X);
                    NotNegative(e.Path, "rateAMva", line.RateAMva);
                    return line;
                }),
                Transformers = o.Items("transformers", e =>
                {
                    var x = new Obj(e.El, e.Path, "path", "hv", "lv", "rPu", "xPu", "bPu", "tapRatio", "phaseShiftDeg", "rateAMva");
                    var t = new SclTransformerParams(x.Str("path"), x.Str("hv"), x.Str("lv"), x.Num("rPu"), x.Num("xPu"), x.NumOr("bPu", 0),
                        x.NumOr("tapRatio", 1), x.NumOr("phaseShiftDeg", 0), x.NumOr("rateAMva", 0), e.Path);
                    NonZeroImpedance(e.Path, "rPu", "xPu", t.R, t.X);
                    Positive(e.Path, "tapRatio", t.TapRatio);
                    NotNegative(e.Path, "rateAMva", t.RateAMva);
                    return t;
                }),
                Transformers3W = o.Items("transformers3w", e =>
                {
                    var x = new Obj(e.El, e.Path, "path", "hv", "mv", "lv", "r12Pu", "x12Pu", "r13Pu", "x13Pu", "r23Pu", "x23Pu", "bPu", "ratio", "phaseShiftDeg", "ratedMva");
                    var t = new SclTransformer3WParams(x.Str("path"), x.Str("hv"), x.Str("mv"), x.Str("lv"), x.Num("r12Pu"), x.Num("x12Pu"), x.Num("r13Pu"), x.Num("x13Pu"),
                        x.Num("r23Pu"), x.Num("x23Pu"), x.NumOr("bPu", 0), x.Nums3OrNull("ratio"), x.Nums3OrNull("phaseShiftDeg"), x.Nums3OrNull("ratedMva"), e.Path);
                    NonZeroImpedance(e.Path, "r12Pu", "x12Pu", t.R12, t.X12);
                    NonZeroImpedance(e.Path, "r13Pu", "x13Pu", t.R13, t.X13);
                    NonZeroImpedance(e.Path, "r23Pu", "x23Pu", t.R23, t.X23);
                    if (t.Ratio is not null) foreach (double r in t.Ratio) Positive(e.Path, "ratio", r);
                    if (t.RatedMva is not null) foreach (double r in t.RatedMva) NotNegative(e.Path, "ratedMva", r);
                    return t;
                }),
                Generators = o.Items("generators", e =>
                {
                    var x = new Obj(e.El, e.Path, "path", "pMw", "vPu", "qMvar", "qMinMvar", "qMaxMvar", "pMinMw", "pMaxMw", "slack");
                    var g = new SclGeneratorParams(x.Str("path"), x.Num("pMw"), x.Num("vPu"), x.NumOr("qMvar", 0),
                        x.NumOr("qMinMvar", double.NegativeInfinity), x.NumOr("qMaxMvar", double.PositiveInfinity),
                        x.NumOr("pMinMw", 0), x.NumOr("pMaxMw", double.PositiveInfinity), x.BoolOr("slack", false), e.Path);
                    Positive(e.Path, "vPu", g.Vg);
                    if (g.Qmin > g.Qmax) throw new InvalidDataException($"{e.Path}: qMinMvar must not be above qMaxMvar.");
                    if (g.Pmin > g.Pmax) throw new InvalidDataException($"{e.Path}: pMinMw must not be above pMaxMw.");
                    return g;
                }),
                Infeeds = o.Items("infeeds", e =>
                {
                    var x = new Obj(e.El, e.Path, "path", "vPu", "pMw");
                    var f = new SclInfeedParams(x.Str("path"), x.Num("vPu"), x.NumOr("pMw", 0), e.Path);
                    Positive(e.Path, "vPu", f.Vg);
                    return f;
                }),
                Loads = o.Items("loads", e =>
                {
                    var x = new Obj(e.El, e.Path, "id", "node", "pMw", "qMvar");
                    return new SclLoadParams(x.Str("id"), x.Str("node"), x.Num("pMw"), x.NumOr("qMvar", 0), e.Path);
                }),
                Switches = o.Items("switches", e =>
                {
                    var x = new Obj(e.El, e.Path, "path", "normalOpen");
                    return new SclSwitchParams(x.Str("path"), x.Bool("normalOpen"), e.Path);
                }),
            };
        }
    }

    // Range checks, made when the file is read so a bad value is named by its JSON path.
    private static void Positive(string path, string name, double v)
    {
        if (!(v > 0)) throw new InvalidDataException($"{path}.{name}: expected a number above 0.");
    }

    private static void NotNegative(string path, string name, double v)
    {
        if (v < 0) throw new InvalidDataException($"{path}.{name}: expected a number at least 0.");
    }

    private static void NonZeroImpedance(string path, string r, string x, double rv, double xv)
    {
        if (rv == 0 && xv == 0) throw new InvalidDataException($"{path}: {r} and {x} are both 0; a zero impedance is not a branch (use a switch).");
    }

    /// <summary>A strict view of one JSON object: unknown members are refused when it is created.</summary>
    private sealed class Obj
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly JsonElement _el;
        private readonly string _path;

        public Obj(JsonElement el, string path, params string[] allowed)
        {
            if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: expected an object.");
            _el = el; _path = path;
            foreach (JsonProperty p in el.EnumerateObject())
                if (Array.IndexOf(allowed, p.Name) < 0)
                    throw new InvalidDataException($"{path}.{p.Name}: unknown member (allowed: {string.Join(", ", allowed)}).");
        }

        private bool Has(string n, out JsonElement v) => _el.TryGetProperty(n, out v) && v.ValueKind != JsonValueKind.Null;

        public string Str(string n)
        {
            if (!Has(n, out JsonElement v)) throw new InvalidDataException($"{_path}.{n}: required string is missing.");
            if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString())) throw new InvalidDataException($"{_path}.{n}: expected a non-empty string.");
            return v.GetString()!;
        }

        public string? StrOrNull(string n) => Has(n, out _) ? Str(n) : null;

        public IReadOnlyList<string> Strs(string n)
        {
            if (!Has(n, out JsonElement v)) throw new InvalidDataException($"{_path}.{n}: required array is missing.");
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 2)
                throw new InvalidDataException($"{_path}.{n}: expected an array of one or two SCL equipment paths.");
            var list = new List<string>(2);
            foreach (JsonElement item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
                    throw new InvalidDataException($"{_path}.{n}: expected non-empty strings.");
                list.Add(item.GetString()!);
            }
            return list.AsReadOnly();
        }

        public double Num(string n)
        {
            if (!Has(n, out JsonElement v)) throw new InvalidDataException($"{_path}.{n}: required number is missing.");
            return Number(n, v);
        }

        public double NumOr(string n, double d) => Has(n, out JsonElement v) ? Number(n, v) : d;

        private double Number(string n, JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double x) || !double.IsFinite(x))
                throw new InvalidDataException($"{_path}.{n}: expected a finite number.");
            return x;
        }

        public IReadOnlyList<double>? Nums3OrNull(string n)
        {
            if (!Has(n, out JsonElement v)) return null;
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3) throw new InvalidDataException($"{_path}.{n}: expected an array of three numbers (one per winding).");
            var list = new List<double>(3);
            foreach (JsonElement item in v.EnumerateArray()) list.Add(Number(n, item));
            return list.AsReadOnly();
        }

        public bool Bool(string n)
        {
            if (!Has(n, out JsonElement v)) throw new InvalidDataException($"{_path}.{n}: required true or false is missing.");
            return BoolOf(n, v);
        }

        public bool BoolOr(string n, bool d) => Has(n, out JsonElement v) ? BoolOf(n, v) : d;

        private bool BoolOf(string n, JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{_path}.{n}: expected true or false."),
        };

        public IReadOnlyList<T> Items<T>(string n, Func<(JsonElement El, string Path), T> read)
        {
            if (!Has(n, out JsonElement v)) return [];
            if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{_path}.{n}: expected an array.");
            var list = new List<T>();
            int i = 0;
            foreach (JsonElement item in v.EnumerateArray())
                list.Add(read((item, $"{_path}.{n}[{(i++).ToString(Inv)}]")));
            return list.AsReadOnly();
        }
    }
}
