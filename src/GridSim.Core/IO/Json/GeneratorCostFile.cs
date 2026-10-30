using System.Globalization;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>
/// The JSON generator cost file (F19, October 2026): the JSON equivalent of MATPOWER's <c>mpc.gencost</c>, read by
/// <c>dcopf --costs &lt;file.json&gt;</c> and laid onto a loaded case's generators (<see cref="Generator.Cost"/>).
///
/// <para>Layout (every key is checked; an unknown key is refused, so a misspelling cannot silently drop a cost):</para>
/// <code>
/// {
///   "format": "gridsim-costs/1",
///   "generators": [
///     { "index": 1, "model": "polynomial", "coefficients": [0.11, 5, 150] },
///     { "name": "G2", "model": "piecewiseLinear", "points": [[0, 0], [100, 2500], [200, 6000]],
///       "startup": 0, "shutdown": 0 }
///   ]
/// }
/// </code>
/// <list type="bullet">
/// <item><c>index</c> is 1-based in the case's generator list in FILE order, out-of-service units included (the order of
/// MATPOWER's mpc.gen rows, or of generation.json); <c>name</c> matches <see cref="Generator.Name"/> exactly and must
/// name one unit. Give exactly one of them. A unit may be named once only.</item>
/// <item><c>polynomial</c>: <c>coefficients</c> highest order first, as MATPOWER writes them (c2 c1 c0 for a quadratic);
/// <c>piecewiseLinear</c>: <c>points</c>, each [MW, cost per hour]. <c>startup</c> / <c>shutdown</c> are optional
/// (default 0) and carried only (the single-period DC-OPF has no commitment decision).</item>
/// <item>The file OVERLAYS the case: a unit it names takes the file's curve; a unit it does not name keeps the case's
/// own curve (MATPOWER gencost), if any. Whether every in-service unit then has a curve is checked by the solve.</item>
/// <item>Each curve is checked for what the DC-OPF can optimise (<see cref="GeneratorCostCurve.From"/>) when it is read,
/// so a bad curve is refused here, naming the entry.</item>
/// </list>
/// </summary>
public static class GeneratorCostFile
{
    /// <summary>The format tag the file must carry.</summary>
    public const string Format = "gridsim-costs/1";

    /// <summary>Read <paramref name="path"/> and return <paramref name="model"/> with the file's curves laid onto its
    /// generators. Throws <see cref="InvalidDataException"/> naming the entry for any problem (and
    /// <see cref="FileNotFoundException"/> for a missing file).</summary>
    public static GridModel Apply(GridModel model, string path)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"cost file '{path}' not found.", path);
        string text = File.ReadAllText(path);
        return Apply(model, text, Path.GetFileName(path));
    }

    /// <summary>The same, from the file's text; <paramref name="label"/> names it in messages.</summary>
    public static GridModel Apply(GridModel model, string json, string label)
    {
        ArgumentNullException.ThrowIfNull(model);
        JsonDocument doc;
        // Duplicate keys are refused (JsonDocument kept the last one silently, so a second "coefficients" replaced the
        // first without a word).
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException ex) { throw new InvalidDataException($"cost file {label}: not valid JSON ({ex.Message})."); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"cost file {label}: the top level must be an object.");
            string? format = null;
            JsonElement? list = null;
            foreach (JsonProperty p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "format": format = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                    case "generators": list = p.Value; break;
                    default: throw new InvalidDataException($"cost file {label}: unknown key '{p.Name}' (allowed: format, generators).");
                }
            }
            if (format != Format) throw new InvalidDataException($"cost file {label}: \"format\" must be \"{Format}\".");
            if (list is not { ValueKind: JsonValueKind.Array } arr) throw new InvalidDataException($"cost file {label}: \"generators\" must be an array.");

            var fleet = model.AllGenerators.ToArray();
            var named = new bool[fleet.Length];
            int entry = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                entry++;
                string where = $"cost file {label}, generators entry {entry}";
                (int unit, GeneratorCost cost) = ReadEntry(e, where, fleet, model.DroppedGeneratorRows);
                if (named[unit]) throw new InvalidDataException($"{where}: generator {unit + 1} is given a cost twice.");
                named[unit] = true;
                // Check the curve is one the DC-OPF can optimise now, naming the entry, rather than at solve time.
                GeneratorCostCurve.From(cost, where);
                fleet[unit] = fleet[unit] with { Cost = cost };
            }
            return model.CopyWith(generators: fleet);
        }
    }

    private static (int Unit, GeneratorCost Cost) ReadEntry(JsonElement e, string where, Generator[] fleet, int droppedRows)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: must be an object.");
        int? index = null;
        string? name = null, model = null;
        double startup = 0.0, shutdown = 0.0;
        List<double>? coefficients = null;
        List<double>? points = null;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "index":
                    // Bug hunt 2026-10-29, finding 34: read like every other integer member of the JSON family
                    // (JsonWhole), so 1.0, which the schemas accept as an integer, is index 1 and not refused.
                    try { index = JsonWhole.Int(p.Value, "index"); }
                    catch (InvalidDataException ex) { throw new InvalidDataException($"{where}: {ex.Message}", ex); }
                    break;
                case "name":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: \"name\" must be a string.");
                    name = p.Value.GetString(); break;
                case "model":
                    if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: \"model\" must be a string.");
                    model = p.Value.GetString(); break;
                case "startup": startup = Number(p.Value, where, "startup"); break;
                case "shutdown": shutdown = Number(p.Value, where, "shutdown"); break;
                case "coefficients":
                    if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"coefficients\" must be an array of numbers.");
                    coefficients = p.Value.EnumerateArray().Select(v => Number(v, where, "coefficients")).ToList();
                    break;
                case "points":
                    if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{where}: \"points\" must be an array of [MW, cost] pairs.");
                    points = new List<double>();
                    foreach (JsonElement pt in p.Value.EnumerateArray())
                    {
                        if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() != 2)
                            throw new InvalidDataException($"{where}: each point must be a [MW, cost per hour] pair.");
                        points.Add(Number(pt[0], where, "points"));
                        points.Add(Number(pt[1], where, "points"));
                    }
                    break;
                default:
                    throw new InvalidDataException($"{where}: unknown key '{p.Name}' (allowed: index, name, model, coefficients, points, startup, shutdown).");
            }
        }

        int unit;
        if (index is not null && name is not null) throw new InvalidDataException($"{where}: give \"index\" or \"name\", not both.");
        if (index is int i)
        {
            if (droppedRows > 0)
                throw new InvalidDataException($"{where}: \"index\" cannot be used with this case: its loader skipped {droppedRows} " +
                    "generator row(s) with no bus to sit on (an isolated or undefined bus), so a file-order position no longer " +
                    "names the same unit. Address the units by \"name\" instead.");
            if (i < 1 || i > fleet.Length) throw new InvalidDataException($"{where}: index {i} is outside 1..{fleet.Length} (the case's generators, in file order).");
            unit = i - 1;
        }
        else if (name is not null)
        {
            int[] hits = Enumerable.Range(0, fleet.Length).Where(k => fleet[k].Name == name).ToArray();
            if (hits.Length == 0) throw new InvalidDataException($"{where}: no generator is named '{name}'.");
            if (hits.Length > 1) throw new InvalidDataException($"{where}: {hits.Length} generators are named '{name}'; use \"index\".");
            unit = hits[0];
        }
        else throw new InvalidDataException($"{where}: give \"index\" (1-based, file order) or \"name\".");

        GeneratorCost cost = model switch
        {
            "polynomial" => coefficients is { Count: > 0 } && points is null
                ? new GeneratorCost(GeneratorCostModel.Polynomial, startup, shutdown, coefficients.ToArray())
                : throw new InvalidDataException($"{where}: a polynomial cost needs \"coefficients\" (highest order first) and no \"points\"."),
            "piecewiseLinear" => points is { Count: > 0 } && coefficients is null
                ? new GeneratorCost(GeneratorCostModel.PiecewiseLinear, startup, shutdown, points.ToArray())
                : throw new InvalidDataException($"{where}: a piecewise-linear cost needs \"points\" and no \"coefficients\"."),
            null => throw new InvalidDataException($"{where}: \"model\" is required (\"polynomial\" or \"piecewiseLinear\")."),
            _ => throw new InvalidDataException($"{where}: \"model\" is '{model}'; it must be \"polynomial\" or \"piecewiseLinear\"."),
        };
        return (unit, cost);
    }

    private static double Number(JsonElement v, string where, string key)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d))
            throw new InvalidDataException($"{where}: \"{key}\" must hold finite numbers (found {v.GetRawText()}).");
        return d;
    }

    /// <summary>The curves of every generator of <paramref name="model"/> (file order) as a cost file (the inverse of
    /// <see cref="Apply(GridModel, string)"/>); units with no curve are left out. Numbers are written in round-trip form.
    /// Units are addressed by "index", except for a model whose loader dropped generator rows (Apply refuses "index"
    /// there): each costed unit is then addressed by its "name", which must be present and unique, or the write is
    /// refused naming the unit (bug hunt 2026-10-29, finding 33). A non-finite coefficient or start-up / shut-down cost
    /// is refused by name too (it used to reach Utf8JsonWriter and fail with a bare ArgumentException).</summary>
    public static string Write(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        bool byName = model.DroppedGeneratorRows > 0;
        for (int i = 0; i < model.AllGenerators.Count; i++)
        {
            Generator g = model.AllGenerators[i];
            if (g.Cost is not GeneratorCost c0) continue;
            string who = $"generator {i + 1} (bus {g.Bus})";
            foreach (double v in c0.Coefficients.Append(c0.StartupCost).Append(c0.ShutdownCost))
                if (!double.IsFinite(v))
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                        $"Generator cost file: {who} has a non-finite cost value ({v:R}); a cost file holds finite numbers only."));
            if (byName && (string.IsNullOrEmpty(g.Name) || model.AllGenerators.Count(x => x.Name == g.Name) != 1))
                throw new InvalidDataException(
                    $"Generator cost file: the model's loader dropped {model.DroppedGeneratorRows} generator row(s), so units must be " +
                    $"addressed by a unique name, but {who} has {(string.IsNullOrEmpty(g.Name) ? "no name" : $"the shared name '{g.Name}'")}; " +
                    "the file could not be applied back to this model.");
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteStartArray("generators");
            for (int i = 0; i < model.AllGenerators.Count; i++)
            {
                GeneratorCost? c = model.AllGenerators[i].Cost;
                if (c is null) continue;
                w.WriteStartObject();
                if (byName) w.WriteString("name", model.AllGenerators[i].Name);
                else w.WriteNumber("index", i + 1);
                if (c.Model == GeneratorCostModel.Polynomial)
                {
                    w.WriteString("model", "polynomial");
                    w.WriteStartArray("coefficients");
                    foreach (double v in c.Coefficients) w.WriteNumberValue(v);
                    w.WriteEndArray();
                }
                else
                {
                    w.WriteString("model", "piecewiseLinear");
                    w.WriteStartArray("points");
                    for (int k = 0; k + 1 < c.Coefficients.Count; k += 2)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(c.Coefficients[k]);
                        w.WriteNumberValue(c.Coefficients[k + 1]);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                if (c.StartupCost != 0.0) w.WriteNumber("startup", c.StartupCost);
                if (c.ShutdownCost != 0.0) w.WriteNumber("shutdown", c.ShutdownCost);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
