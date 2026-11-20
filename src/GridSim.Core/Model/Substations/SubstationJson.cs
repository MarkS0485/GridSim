using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.Model.Substations;

/// <summary>
/// Reader and writer for a native node-breaker file (<c>gridsim-substations/1</c>). The vocabulary is the v2 asset
/// overlay's: a switching device is a <c>breaker</c>, <c>disconnector</c> or <c>earthSwitch</c> (plus <c>loadBreak</c>)
/// with <c>from</c>, <c>to</c> and <c>normalOpen</c>, in a substation holding sections (nodes) by id, so a case written
/// for the overlay reads here with the same words. The reader is strict (an unknown member, a wrong type or a missing
/// required member is refused, naming the path) and the writer is deterministic: members in a fixed order, lists in
/// declaration order, numbers in round-trip form, so read-then-write returns the same bytes and write-then-read the
/// same model.
/// </summary>
public static class SubstationJson
{
    /// <summary>The format identifier of the document.</summary>
    public const string Format = "gridsim-substations/1";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------------------------------------------- writer

    public static string Write(SubstationModel m)
    {
        ArgumentNullException.ThrowIfNull(m);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteString("name", m.Name);
            w.WriteNumber("baseMva", m.BaseMva);
            w.WriteNumber("frequencyHz", m.FrequencyHz);
            w.WriteStartArray("substations");
            foreach (Substation s in m.Substations)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                if (s.Name is not null) w.WriteString("name", s.Name);
                w.WriteStartArray("voltageLevels");
                foreach (VoltageLevel v in s.VoltageLevels)
                { w.WriteStartObject(); w.WriteString("id", v.Id); w.WriteNumber("nominalKv", v.NominalKv); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteStartArray("bays");
                foreach (SubstationBay b in s.Bays)
                {
                    w.WriteStartObject(); w.WriteString("id", b.Id); w.WriteString("voltageLevel", b.VoltageLevel);
                    if (b.Name is not null) w.WriteString("name", b.Name);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("nodes");
                foreach (ConnectivityNode n in s.Nodes)
                {
                    w.WriteStartObject(); w.WriteString("id", n.Id); w.WriteString("voltageLevel", n.VoltageLevel);
                    if (n.Bay is not null) w.WriteString("bay", n.Bay);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("switches");
                foreach (NbSwitch sw in s.Switches)
                {
                    w.WriteStartObject(); w.WriteString("id", sw.Id); w.WriteString("device", DeviceWord(sw.Kind));
                    w.WriteString("from", sw.FromNode);
                    if (sw.ToNode is not null) w.WriteString("to", sw.ToNode);
                    w.WriteBoolean("normalOpen", sw.NormalOpen);
                    if (sw.Bay is not null) w.WriteString("bay", sw.Bay);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("lines");
            foreach (NbLine l in m.Lines)
            {
                w.WriteStartObject(); w.WriteString("id", l.Id); w.WriteString("a", l.NodeA); w.WriteString("b", l.NodeB);
                w.WriteNumber("rPu", l.R); w.WriteNumber("xPu", l.X); w.WriteNumber("bPu", l.B); w.WriteNumber("rateAMva", l.RateAMva);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("transformers");
            foreach (NbTransformer x in m.Transformers)
            {
                w.WriteStartObject(); w.WriteString("id", x.Id); w.WriteString("hv", x.NodeHv); w.WriteString("lv", x.NodeLv);
                w.WriteNumber("rPu", x.R); w.WriteNumber("xPu", x.X); w.WriteNumber("bPu", x.B);
                w.WriteNumber("tapRatio", x.TapRatio); w.WriteNumber("phaseShiftDeg", x.PhaseShiftDeg); w.WriteNumber("rateAMva", x.RateAMva);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("loads");
            foreach (NbLoad x in m.Loads)
            { w.WriteStartObject(); w.WriteString("id", x.Id); w.WriteString("node", x.Node); w.WriteNumber("pMw", x.Pd); w.WriteNumber("qMvar", x.Qd); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("generators");
            foreach (NbGenerator x in m.Generators)
            {
                w.WriteStartObject(); w.WriteString("id", x.Id); w.WriteString("node", x.Node);
                w.WriteNumber("pMw", x.Pg); w.WriteNumber("vPu", x.Vg); w.WriteNumber("qMvar", x.Qg);
                if (double.IsFinite(x.Qmin)) w.WriteNumber("qMinMvar", x.Qmin);
                if (double.IsFinite(x.Qmax)) w.WriteNumber("qMaxMvar", x.Qmax);
                w.WriteNumber("pMinMw", x.Pmin);
                if (double.IsFinite(x.Pmax)) w.WriteNumber("pMaxMw", x.Pmax);
                if (x.Slack) w.WriteBoolean("slack", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("shunts");
            foreach (NbShunt x in m.Shunts)
            { w.WriteStartObject(); w.WriteString("id", x.Id); w.WriteString("node", x.Node); w.WriteNumber("gMw", x.Gs); w.WriteNumber("bMvar", x.Bs); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static string DeviceWord(SwitchKind k) => k switch
    {
        SwitchKind.Breaker => "breaker",
        SwitchKind.Disconnector => "disconnector",
        SwitchKind.EarthSwitch => "earthSwitch",
        _ => "loadBreak",
    };

    // ---------------------------------------------------------------------------------------------------- reader

    /// <summary>Read a <c>gridsim-substations/1</c> document. Does not run <see cref="SubstationModel.Validate"/>
    /// (the projection does), so an editor can load a model that is mid-edit.</summary>
    public static SubstationModel Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false }); }
        catch (JsonException e) { throw new InvalidDataException("substation file is not valid JSON: " + e.Message, e); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            var o = new Obj(root, "$", "format", "name", "baseMva", "frequencyHz", "substations", "lines", "transformers", "loads", "generators", "shunts");
            if (o.Str("format") != Format) throw new InvalidDataException($"$.format: expected \"{Format}\" but got \"{o.StrOr("format", "")}\".");
            return new SubstationModel
            {
                Name = o.StrOr("name", "nodebreaker"),
                BaseMva = o.NumOr("baseMva", GridModel.DefaultBaseMva),
                FrequencyHz = o.NumOr("frequencyHz", global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz),
                Substations = o.Items("substations", ReadSubstation),
                Lines = o.Items("lines", e => { var x = new Obj(e.El, e.Path, "id", "a", "b", "rPu", "xPu", "bPu", "rateAMva");
                    return new NbLine(x.Str("id"), x.Str("a"), x.Str("b"), x.Num("rPu"), x.Num("xPu"), x.NumOr("bPu", 0), x.NumOr("rateAMva", 0)); }),
                Transformers = o.Items("transformers", e => { var x = new Obj(e.El, e.Path, "id", "hv", "lv", "rPu", "xPu", "bPu", "tapRatio", "phaseShiftDeg", "rateAMva");
                    return new NbTransformer(x.Str("id"), x.Str("hv"), x.Str("lv"), x.Num("rPu"), x.Num("xPu"), x.NumOr("bPu", 0),
                        x.NumOr("tapRatio", 1), x.NumOr("phaseShiftDeg", 0), x.NumOr("rateAMva", 0)); }),
                Loads = o.Items("loads", e => { var x = new Obj(e.El, e.Path, "id", "node", "pMw", "qMvar");
                    return new NbLoad(x.Str("id"), x.Str("node"), x.Num("pMw"), x.NumOr("qMvar", 0)); }),
                Generators = o.Items("generators", e => { var x = new Obj(e.El, e.Path, "id", "node", "pMw", "vPu", "qMvar", "qMinMvar", "qMaxMvar", "pMinMw", "pMaxMw", "slack");
                    return new NbGenerator(x.Str("id"), x.Str("node"), x.Num("pMw"), x.NumOr("vPu", 1), x.NumOr("qMvar", 0),
                        x.NumOr("qMinMvar", double.NegativeInfinity), x.NumOr("qMaxMvar", double.PositiveInfinity),
                        x.NumOr("pMinMw", 0), x.NumOr("pMaxMw", double.PositiveInfinity), x.BoolOr("slack", false)); }),
                Shunts = o.Items("shunts", e => { var x = new Obj(e.El, e.Path, "id", "node", "gMw", "bMvar");
                    return new NbShunt(x.Str("id"), x.Str("node"), x.NumOr("gMw", 0), x.NumOr("bMvar", 0)); }),
            };
        }
    }

    private static Substation ReadSubstation((JsonElement El, string Path) e)
    {
        var o = new Obj(e.El, e.Path, "id", "name", "voltageLevels", "bays", "nodes", "switches");
        return new Substation(o.Str("id"), o.StrOrNull("name"),
            o.Items("voltageLevels", v => { var x = new Obj(v.El, v.Path, "id", "nominalKv"); return new VoltageLevel(x.Str("id"), x.Num("nominalKv")); }),
            o.Items("bays", v => { var x = new Obj(v.El, v.Path, "id", "voltageLevel", "name"); return new SubstationBay(x.Str("id"), x.Str("voltageLevel"), x.StrOrNull("name")); }),
            o.Items("nodes", v => { var x = new Obj(v.El, v.Path, "id", "voltageLevel", "bay"); return new ConnectivityNode(x.Str("id"), x.Str("voltageLevel"), x.StrOrNull("bay")); }),
            o.Items("switches", v =>
            {
                var x = new Obj(v.El, v.Path, "id", "device", "from", "to", "normalOpen", "bay");
                string dev = x.Str("device");
                SwitchKind kind = dev switch
                {
                    "breaker" => SwitchKind.Breaker,
                    "disconnector" => SwitchKind.Disconnector,
                    "earthSwitch" => SwitchKind.EarthSwitch,
                    "loadBreak" => SwitchKind.LoadBreak,
                    _ => throw new InvalidDataException($"{v.Path}.device: expected breaker, disconnector, earthSwitch or loadBreak but got \"{dev}\"."),
                };
                return new NbSwitch(x.Str("id"), kind, x.Str("from"), x.StrOrNull("to"), x.Bool("normalOpen"), x.StrOrNull("bay"));
            }));
    }

    /// <summary>A strict view of one JSON object: unknown members are refused when it is created.</summary>
    private sealed class Obj
    {
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
            if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{_path}.{n}: expected a string.");
            return v.GetString()!;
        }
        public string? StrOrNull(string n)
        {
            if (!Has(n, out JsonElement v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{_path}.{n}: expected a string.");
            return v.GetString();
        }
        public string StrOr(string n, string d) => StrOrNull(n) ?? d;
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
            if (!Has(n, out JsonElement v)) return Array.Empty<T>();
            if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{_path}.{n}: expected an array.");
            var list = new List<T>();
            int i = 0;
            foreach (JsonElement item in v.EnumerateArray())
                list.Add(read((item, $"{_path}.{n}[{(i++).ToString(Inv)}]")));
            return list.AsReadOnly();
        }
    }
}
