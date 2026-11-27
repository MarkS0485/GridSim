using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.Dynamics.UserModels;

/// <summary>The format identifier every document must carry.</summary>
public static class ModelFormat
{
    /// <summary>The one supported document format.</summary>
    public const string Id = "gridsim-model/1";

    /// <summary>The model roles: where a model plugs into the dynamics engine. RC5 WP12 adds <c>injection</c> (a generator-bound
    /// current source) and <c>load</c> (a bus load model); a document that names neither parses exactly as before.</summary>
    public static readonly IReadOnlyList<string> Roles = new[] { "exciter", "governor", "stabiliser", "generic", "injection", "load" };

    /// <summary>RC5 wp32b: the <c>relay</c> role (<see cref="DeclarativeRelay"/>), read beside <see cref="Roles"/>. It is kept out of
    /// that list; the refusal of an unknown role names it as well.</summary>
    public const string RelayRole = DeclarativeRelay.Role;

    /// <summary>True for a role this build reads: one of <see cref="Roles"/>, or <see cref="RelayRole"/>.</summary>
    public static bool IsRole(string role) => Roles.Contains(role) || role == RelayRole;
}

/// <summary>The three kinds of reference.</summary>
public enum ModelRefKind
{
    /// <summary>A number written in the document.</summary>
    Literal,
    /// <summary>A declared parameter or derived constant, written <c>@Name</c>.</summary>
    Parameter,
    /// <summary>An input port or a block output.</summary>
    Signal,
}

/// <summary>A reference to a number in a block's input list or parameter list.</summary>
/// <param name="Kind">Literal, Parameter (<c>@Name</c>) or Signal (a port or block id, optionally <c>.prev</c>).</param>
/// <param name="Name">Parameter name, or the signal name without the <c>.prev</c> suffix.</param>
/// <param name="Prev">True for <c>block.prev</c>: the block's output as it stood at the start of the step.</param>
/// <param name="Literal">The number for a literal (or +/-infinity for the texts <c>+inf</c> and <c>-inf</c>).</param>
public sealed record ModelRef(ModelRefKind Kind, string Name, bool Prev, double Literal)
{
    /// <summary>The document text of the reference, for messages.</summary>
    public override string ToString() => Kind switch
    {
        ModelRefKind.Literal => Literal.ToString("R", CultureInfo.InvariantCulture),
        ModelRefKind.Parameter => "@" + Name,
        _ => Prev ? Name + ".prev" : Name,
    };
}

/// <summary>A declared parameter.</summary>
public sealed record ModelParameter(string Name, string UnitText, ModelUnit Unit, double Value, double? Min, bool MinExclusive,
    double? Max, bool MaxExclusive, bool AllowInfinite, string? Description);

/// <summary>A constant computed from parameters in declared order (so a coded model's constructor arithmetic can be reproduced exactly).</summary>
public sealed record ModelDerived(string Name, string Op, ModelRef A, ModelRef B);

/// <summary>An input or output port.</summary>
public sealed record ModelPort(string Name, string UnitText, ModelUnit Unit, ModelRef? From);

/// <summary>A one-dimensional lookup table (table1d).</summary>
public sealed record ModelTable(IReadOnlyList<double> X, IReadOnlyList<double> Y, string XUnitText, string YUnitText);

/// <summary>One block: an operation from the closed catalogue with its wiring.</summary>
public sealed record ModelBlock(string Id, string Type, IReadOnlyList<ModelRef> Inputs,
    IReadOnlyList<KeyValuePair<string, ModelRef>> Params, string? Signs, ModelTable? Table, string Path)
{
    /// <summary>RC5 WP12: the expression text of an <c>expr</c> block (null for every other block).</summary>
    public string? Expr { get; init; }

    /// <summary>RC5 WP12: the declared output unit of an <c>expr</c> block (null for every other block).</summary>
    public string? UnitText { get; init; }
}

/// <summary>One step of the initialisation block.</summary>
public abstract record ModelInitStep(string Path);

/// <summary>A scalar computed from earlier values with a combinational block type.</summary>
public sealed record ModelInitCompute(ModelBlock Block) : ModelInitStep(Block.Path);

/// <summary>Set a stateful block's initial state from computed values.</summary>
public sealed record ModelInitInitialise(string BlockId, IReadOnlyList<ModelRef> Values, string Path) : ModelInitStep(Path);

/// <summary>Refuse the start unless a value lies in [min, max] (the equilibrium-within-limits rule, R252).</summary>
public sealed record ModelInitRequire(ModelRef Value, ModelRef? Min, ModelRef? Max, string Model, string Quantity, string Argument, string Path)
    : ModelInitStep(Path);

/// <summary>The initialisation block.</summary>
public sealed record ModelInitialisation(IReadOnlyList<ModelPort> Inputs, IReadOnlyList<ModelInitStep> Steps, string? EquilibriumError);

/// <summary>
/// A parsed <c>gridsim-model/1</c> document (R4-08): a block diagram with ports, parameters, a closed set of blocks and an
/// initialisation block. Parsing is strict (unknown members, duplicate members and wrong types are refused naming the
/// JSON path); meaning (vocabulary, units, ports, loops) is checked by <see cref="ModelValidator"/> and
/// <see cref="ModelCompiler"/>. The hash is the SHA-256 of the exact bytes parsed, so any edit, including a reordering of
/// blocks (the declared order is part of the evaluation order), changes it.
/// </summary>
public sealed class ModelDocument
{
    private static readonly Regex NameRe = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>The model id (also its name in reports).</summary>
    public string Id { get; init; } = "";

    /// <summary>A short title.</summary>
    public string Title { get; init; } = "";

    /// <summary>exciter, governor, stabiliser or generic.</summary>
    public string Role { get; init; } = "";

    /// <summary>The public source the structure is taken from (required).</summary>
    public string Source { get; init; } = "";

    /// <summary>Free text.</summary>
    public string? Description { get; init; }

    /// <summary>Declared parameters in document order.</summary>
    public IReadOnlyList<ModelParameter> Parameters { get; init; } = Array.Empty<ModelParameter>();

    /// <summary>Derived constants in document order.</summary>
    public IReadOnlyList<ModelDerived> Derived { get; init; } = Array.Empty<ModelDerived>();

    /// <summary>Parameter sum constraints (each: names, target, tolerance), in document order.</summary>
    public IReadOnlyList<(IReadOnlyList<string> Names, double Target, double Tolerance)> SumConstraints { get; init; }
        = Array.Empty<(IReadOnlyList<string>, double, double)>();

    /// <summary>Input ports.</summary>
    public IReadOnlyList<ModelPort> Inputs { get; init; } = Array.Empty<ModelPort>();

    /// <summary>Output ports (each bound to a block through <see cref="ModelPort.From"/>).</summary>
    public IReadOnlyList<ModelPort> Outputs { get; init; } = Array.Empty<ModelPort>();

    /// <summary>Blocks in document order.</summary>
    public IReadOnlyList<ModelBlock> Blocks { get; init; } = Array.Empty<ModelBlock>();

    /// <summary>The initialisation block.</summary>
    public ModelInitialisation Initialisation { get; init; } = new(Array.Empty<ModelPort>(), Array.Empty<ModelInitStep>(), null);

    /// <summary>SHA-256 (lower-case hex) of the exact document bytes.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>Parse a UTF-8 document. Refuses with <see cref="UserModelException"/> naming the JSON path.</summary>
    public static ModelDocument Parse(ReadOnlySpan<byte> utf8)
    {
        string hash = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(utf8.ToArray()), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 32,
            });
        }
        catch (JsonException ex)
        {
            throw new UserModelException("not-json", "$", "the document is not valid JSON: " + ex.Message);
        }
        using (doc)
            return Read(doc.RootElement, hash);
    }

    /// <summary>Parse a document from text (UTF-8 encoded; the hash is of those bytes).</summary>
    public static ModelDocument Parse(string text) => Parse(System.Text.Encoding.UTF8.GetBytes(text));

    // ---- strict reading helpers -------------------------------------------------------------------------------------

    private sealed class Fail(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }

    private static Fail Bad(string path, string message) => new(path, message);

    private static Dictionary<string, JsonElement> Members(JsonElement el, string path, string[] required, string[] optional)
    {
        if (el.ValueKind != JsonValueKind.Object) throw Bad(path, "must be a JSON object.");
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in el.EnumerateObject())
        {
            if (!d.TryAdd(p.Name, p.Value)) throw Bad(path, $"the member '{p.Name}' appears twice.");
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw Bad(path, $"the member '{p.Name}' is not part of {ModelFormat.Id} (allowed: {string.Join(", ", required.Concat(optional))}).");
        }
        foreach (string r in required)
            if (!d.ContainsKey(r)) throw Bad(path, $"the required member '{r}' is missing.");
        return d;
    }

    private static string Str(JsonElement el, string path)
        => el.ValueKind == JsonValueKind.String ? el.GetString()! : throw Bad(path, "must be a string.");

    private static string Name(JsonElement el, string path)
    {
        string s = Str(el, path);
        if (!NameRe.IsMatch(s)) throw Bad(path, $"'{s}' is not a valid name (letters, digits and underscore, not starting with a digit).");
        return s;
    }

    private static bool Bool(JsonElement el, string path)
        => el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : throw Bad(path, "must be true or false.");

    private static double Num(JsonElement el, string path)
    {
        if (el.ValueKind != JsonValueKind.Number) throw Bad(path, "must be a number.");
        double v = el.GetDouble();
        if (!double.IsFinite(v)) throw Bad(path, "must be a finite number.");
        return v;
    }

    private static List<JsonElement> Arr(JsonElement el, string path)
        => el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().ToList() : throw Bad(path, "must be an array.");

    /// <summary>A number, "+inf", "-inf", "@Param", or (for signals) a name with an optional ".prev".</summary>
    private static ModelRef Ref(JsonElement el, string path, bool allowSignal)
    {
        if (el.ValueKind == JsonValueKind.Number) return new ModelRef(ModelRefKind.Literal, "", false, Num(el, path));
        if (el.ValueKind != JsonValueKind.String) throw Bad(path, "must be a number or a reference string.");
        string s = el.GetString()!;
        if (s == "+inf") return new ModelRef(ModelRefKind.Literal, "", false, double.PositiveInfinity);
        if (s == "-inf") return new ModelRef(ModelRefKind.Literal, "", false, double.NegativeInfinity);
        if (s.StartsWith('@'))
        {
            string n = s.Substring(1);
            if (!NameRe.IsMatch(n)) throw Bad(path, $"'{s}' is not a valid parameter reference.");
            return new ModelRef(ModelRefKind.Parameter, n, false, 0);
        }
        if (!allowSignal) throw Bad(path, $"'{s}' must be a number, '+inf', '-inf' or an '@Parameter' reference.");
        bool prev = false;
        if (s.EndsWith(".prev", StringComparison.Ordinal)) { prev = true; s = s.Substring(0, s.Length - 5); }
        if (!NameRe.IsMatch(s)) throw Bad(path, $"'{s}' is not a valid signal reference.");
        return new ModelRef(ModelRefKind.Signal, s, prev, 0);
    }

    private static ModelUnit UnitOf(string text, string path)
        => ModelUnit.Parse(text) ?? throw Bad(path, $"'{text}' is not a unit in the grammar (pu, 1, s combined with * and /).");

    private static ModelDocument Read(JsonElement root, string hash)
    {
        try
        {
            var m = Members(root, "$", new[] { "format", "id", "role", "source", "ports", "blocks", "initialisation" },
                new[] { "title", "description", "parameters", "derived", "constraints" });
            string format = Str(m["format"], "$.format");
            if (format != ModelFormat.Id)
                throw Bad("$.format", $"'{format}' is not supported; this build reads {ModelFormat.Id}.");
            string id = Name(m["id"], "$.id");
            string role = Str(m["role"], "$.role");
            if (!ModelFormat.IsRole(role))
                throw Bad("$.role", $"'{role}' is not a role (use one of {string.Join(", ", ModelFormat.Roles.Append(ModelFormat.RelayRole))}).");
            string source = Str(m["source"], "$.source");
            if (string.IsNullOrWhiteSpace(source)) throw Bad("$.source", "a public source for the structure is required.");

            var pars = new List<ModelParameter>();
            if (m.TryGetValue("parameters", out JsonElement pe))
            {
                int i = 0;
                foreach (JsonElement e in Arr(pe, "$.parameters"))
                {
                    string pp = $"$.parameters[{i++}]";
                    var d = Members(e, pp, new[] { "name", "unit", "value" },
                        new[] { "min", "max", "minExclusive", "maxExclusive", "allowInfinite", "description" });
                    string unit = Str(d["unit"], pp + ".unit");
                    bool allowInf = d.TryGetValue("allowInfinite", out JsonElement ai) && Bool(ai, pp + ".allowInfinite");
                    double value;
                    JsonElement ve = d["value"];
                    string? vs = ve.ValueKind == JsonValueKind.String ? ve.GetString() : null;
                    if (vs is "+inf" or "-inf")
                    {
                        if (!allowInf) throw Bad(pp + ".value", "an infinite value needs \"allowInfinite\": true.");
                        value = vs == "+inf" ? double.PositiveInfinity : double.NegativeInfinity;
                    }
                    else value = Num(ve, pp + ".value");
                    pars.Add(new ModelParameter(Name(d["name"], pp + ".name"), unit, UnitOf(unit, pp + ".unit"), value,
                        d.TryGetValue("min", out JsonElement mn) ? Num(mn, pp + ".min") : null,
                        d.TryGetValue("minExclusive", out JsonElement mx0) && Bool(mx0, pp + ".minExclusive"),
                        d.TryGetValue("max", out JsonElement mxe) ? Num(mxe, pp + ".max") : null,
                        d.TryGetValue("maxExclusive", out JsonElement mx1) && Bool(mx1, pp + ".maxExclusive"),
                        allowInf, d.TryGetValue("description", out JsonElement de) ? Str(de, pp + ".description") : null));
                }
            }

            var derived = new List<ModelDerived>();
            if (m.TryGetValue("derived", out JsonElement dv))
            {
                int i = 0;
                foreach (JsonElement e in Arr(dv, "$.derived"))
                {
                    string dp = $"$.derived[{i++}]";
                    var d = Members(e, dp, new[] { "name", "op", "a", "b" }, Array.Empty<string>());
                    string op = Str(d["op"], dp + ".op");
                    if (op is not ("add" or "sub" or "mul" or "div"))
                        throw Bad(dp + ".op", $"'{op}' is not a derived-constant operation (add, sub, mul, div).");
                    derived.Add(new ModelDerived(Name(d["name"], dp + ".name"), op, Ref(d["a"], dp + ".a", false), Ref(d["b"], dp + ".b", false)));
                }
            }

            var sums = new List<(IReadOnlyList<string>, double, double)>();
            if (m.TryGetValue("constraints", out JsonElement ce))
            {
                int i = 0;
                foreach (JsonElement e in Arr(ce, "$.constraints"))
                {
                    string cp = $"$.constraints[{i++}]";
                    var d = Members(e, cp, new[] { "sumOf", "equals" }, new[] { "tolerance" });
                    List<string> names = Arr(d["sumOf"], cp + ".sumOf").Select((x, k) => Name(x, $"{cp}.sumOf[{k}]")).ToList();
                    sums.Add((names, Num(d["equals"], cp + ".equals"),
                        d.TryGetValue("tolerance", out JsonElement te) ? Num(te, cp + ".tolerance") : 0.0));
                }
            }

            var pm = Members(m["ports"], "$.ports", new[] { "inputs", "outputs" }, Array.Empty<string>());
            List<ModelPort> ins = Ports(pm["inputs"], "$.ports.inputs", false);
            List<ModelPort> outs = Ports(pm["outputs"], "$.ports.outputs", true);

            var blocks = new List<ModelBlock>();
            {
                int i = 0;
                foreach (JsonElement e in Arr(m["blocks"], "$.blocks")) blocks.Add(Block(e, $"$.blocks[{i++}]"));
            }

            var im = Members(m["initialisation"], "$.initialisation", new[] { "inputs", "steps" }, new[] { "equilibriumError" });
            List<ModelPort> initIns = Ports(im["inputs"], "$.initialisation.inputs", false);
            var steps = new List<ModelInitStep>();
            {
                int i = 0;
                foreach (JsonElement e in Arr(im["steps"], "$.initialisation.steps")) steps.Add(InitStep(e, $"$.initialisation.steps[{i++}]"));
            }
            string? eq = im.TryGetValue("equilibriumError", out JsonElement eqe) ? Name(eqe, "$.initialisation.equilibriumError") : null;

            return new ModelDocument
            {
                Id = id,
                Title = m.TryGetValue("title", out JsonElement t) ? Str(t, "$.title") : id,
                Role = role,
                Source = source,
                Description = m.TryGetValue("description", out JsonElement de2) ? Str(de2, "$.description") : null,
                Parameters = pars,
                Derived = derived,
                SumConstraints = sums,
                Inputs = ins,
                Outputs = outs,
                Blocks = blocks,
                Initialisation = new ModelInitialisation(initIns, steps, eq),
                Sha256 = hash,
            };
        }
        catch (Fail f)
        {
            throw new UserModelException("schema", f.Path, f.Message);
        }
    }

    private static List<ModelPort> Ports(JsonElement el, string path, bool output)
    {
        var list = new List<ModelPort>();
        int i = 0;
        foreach (JsonElement e in Arr(el, path))
        {
            string pp = $"{path}[{i++}]";
            var d = output
                ? Members(e, pp, new[] { "name", "unit", "from" }, Array.Empty<string>())
                : Members(e, pp, new[] { "name", "unit" }, Array.Empty<string>());
            string unit = Str(d["unit"], pp + ".unit");
            list.Add(new ModelPort(Name(d["name"], pp + ".name"), unit, UnitOf(unit, pp + ".unit"),
                output ? Ref(d["from"], pp + ".from", true) : null));
        }
        return list;
    }

    private static ModelBlock Block(JsonElement e, string path)
    {
        var d = Members(e, path, new[] { "id", "type" }, new[] { "inputs", "params", "signs", "table", "expr", "unit" });
        var inputs = new List<ModelRef>();
        if (d.TryGetValue("inputs", out JsonElement ie))
        {
            int k = 0;
            foreach (JsonElement x in Arr(ie, path + ".inputs")) inputs.Add(Ref(x, $"{path}.inputs[{k++}]", true));
        }
        var pars = new List<KeyValuePair<string, ModelRef>>();
        if (d.TryGetValue("params", out JsonElement pe))
        {
            if (pe.ValueKind != JsonValueKind.Object) throw Bad(path + ".params", "must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in pe.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw Bad(path + ".params", $"the member '{p.Name}' appears twice.");
                pars.Add(new(p.Name, Ref(p.Value, $"{path}.params.{p.Name}", false)));
            }
        }
        ModelTable? table = null;
        if (d.TryGetValue("table", out JsonElement te))
        {
            var td = Members(te, path + ".table", new[] { "x", "y", "xUnit", "yUnit" }, Array.Empty<string>());
            table = new ModelTable(
                Arr(td["x"], path + ".table.x").Select((v, k) => Num(v, $"{path}.table.x[{k}]")).ToList(),
                Arr(td["y"], path + ".table.y").Select((v, k) => Num(v, $"{path}.table.y[{k}]")).ToList(),
                Str(td["xUnit"], path + ".table.xUnit"), Str(td["yUnit"], path + ".table.yUnit"));
        }
        return new ModelBlock(Name(d["id"], path + ".id"), Str(d["type"], path + ".type"), inputs, pars,
            d.TryGetValue("signs", out JsonElement se) ? Str(se, path + ".signs") : null, table, path)
        {
            Expr = d.TryGetValue("expr", out JsonElement xe) ? Str(xe, path + ".expr") : null,
            UnitText = d.TryGetValue("unit", out JsonElement ue) ? Str(ue, path + ".unit") : null,
        };
    }

    private static ModelInitStep InitStep(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Bad(path, "must be a JSON object.");
        if (e.TryGetProperty("action", out JsonElement ae))
        {
            string action = Str(ae, path + ".action");
            switch (action)
            {
                case "initialise":
                {
                    var d = Members(e, path, new[] { "action", "block", "values" }, Array.Empty<string>());
                    return new ModelInitInitialise(Name(d["block"], path + ".block"),
                        Arr(d["values"], path + ".values").Select((v, k) => Ref(v, $"{path}.values[{k}]", true)).ToList(), path);
                }
                case "require":
                {
                    var d = Members(e, path, new[] { "action", "value", "model", "quantity", "argument" }, new[] { "min", "max" });
                    return new ModelInitRequire(Ref(d["value"], path + ".value", true),
                        d.TryGetValue("min", out JsonElement mn) ? Ref(mn, path + ".min", false) : null,
                        d.TryGetValue("max", out JsonElement mx) ? Ref(mx, path + ".max", false) : null,
                        Str(d["model"], path + ".model"), Str(d["quantity"], path + ".quantity"), Name(d["argument"], path + ".argument"), path);
                }
                default:
                    throw Bad(path + ".action", $"'{action}' is not an initialisation action (initialise, require).");
            }
        }
        return new ModelInitCompute(Block(e, path));
    }
}
