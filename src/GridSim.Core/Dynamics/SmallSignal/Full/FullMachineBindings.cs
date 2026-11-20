using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Dynamics.UserModels;
using GridSim.Core.Model;

namespace GridSim.Core.Dynamics.SmallSignal.Full;

/// <summary>A strict read problem in a machine binding file: names the JSON path.</summary>
public sealed class FullBindingException : Exception
{
    /// <summary>JSON path of the problem.</summary>
    public string Path { get; }

    /// <summary>Make the exception.</summary>
    public FullBindingException(string path, string message) : base($"{path}: {message}") { Path = path; }
}

/// <summary>The machines of a case bound to data and declarative models, read from a <c>gridsim-modal-machines/1</c> document.</summary>
/// <param name="Specs">One spec per named machine.</param>
/// <param name="Sha256">SHA-256 of the binding document bytes.</param>
/// <param name="ModelHashes">Id and document hash of every declarative model the bindings used, in first-use order.</param>
public sealed record FullMachineBindings(IReadOnlyList<FullMachineSpec> Specs, string Sha256, IReadOnlyList<(string Id, string Hash)> ModelHashes)
{
    /// <summary>The document format.</summary>
    public const string Format = "gridsim-modal-machines/1";

    /// <summary>
    /// Parse a binding document for <paramref name="model"/>. Strict: unknown members, wrong types, an unknown bus, two entries for one
    /// machine and a missing model are refused naming the JSON path. A machine is named by its bus and, when several generators sit on
    /// the bus, by <c>unit</c> (0-based position among the in-service generators of that bus, default 0).
    /// <para>Shape: <c>{ "format": "gridsim-modal-machines/1", "machines": [ { "bus": 2, "unit": 0, "model": "flux-decay" | "classical",
    /// "xdp": 0.3, "xd": 1.6, "xq": 1.55, "tdop": 6.0, "damping": 2.0, "infiniteBus": false, "exciter": { "model": "SEXS",
    /// "parameters": { "K": 50 } }, "governor": { ... }, "stabiliser": { ... }, "coded": [ "AcExciter" ] } ] }</c>. Reactances are per
    /// unit on the machine base; damping is on the system base. A model name is resolved by <paramref name="resolveModel"/> (a file
    /// next to the binding, then the shipped library).</para>
    /// </summary>
    /// <exception cref="FullBindingException">The document is invalid.</exception>
    public static FullMachineBindings Parse(ReadOnlySpan<byte> utf8, GridModel model, Func<string, byte[]?> resolveModel)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(resolveModel);
        string hash = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 }); }
        catch (JsonException ex) { throw new FullBindingException("$", "not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            CheckMembers(root, "$", new[] { "format", "machines" }, Array.Empty<string>());
            string format = Str(root.GetProperty("format"), "$.format");
            if (format != Format) throw new FullBindingException("$.format", $"'{format}' is not supported; this build reads {Format}.");
            if (root.GetProperty("machines").ValueKind != JsonValueKind.Array) throw new FullBindingException("$.machines", "must be an array.");

            var specs = new List<FullMachineSpec>();
            var seen = new HashSet<int>();
            var used = new List<(string, string)>();
            int idx = 0;
            foreach (JsonElement m in root.GetProperty("machines").EnumerateArray())
            {
                string p = $"$.machines[{idx++}]";
                CheckMembers(m, p, new[] { "bus" }, new[] { "unit", "model", "xdp", "xd", "xq", "tdop", "damping", "infiniteBus", "exciter", "governor", "stabiliser", "coded" });
                int bus = Int(m.GetProperty("bus"), p + ".bus");
                int unit = m.TryGetProperty("unit", out JsonElement ue) ? Int(ue, p + ".unit") : 0;
                var atBus = new List<int>();
                for (int k = 0; k < model.Generators.Count; k++) if (model.Generators[k].Bus == bus) atBus.Add(k);
                if (atBus.Count == 0) throw new FullBindingException(p + ".bus", $"the case has no in-service generator at bus {bus}.");
                if (unit < 0 || unit >= atBus.Count) throw new FullBindingException(p + ".unit", $"bus {bus} has {atBus.Count} generator(s); unit {unit} does not exist.");
                int gi = atBus[unit];
                if (!seen.Add(gi)) throw new FullBindingException(p, $"generator {gi} (bus {bus}) is bound twice.");

                string kind = m.TryGetProperty("model", out JsonElement me) ? Str(me, p + ".model") : "classical";
                FullMachineModel mm = kind switch
                {
                    "classical" => FullMachineModel.Classical,
                    "flux-decay" => FullMachineModel.FluxDecay,
                    _ => throw new FullBindingException(p + ".model", $"'{kind}' is not a machine model (classical, flux-decay)."),
                };
                double xdp = Num(m, "xdp", p, 0.30), xd = Num(m, "xd", p, 0.0), xq = Num(m, "xq", p, 0.0), tdop = Num(m, "tdop", p, 0.0), damp = Num(m, "damping", p, 0.0);
                bool inf = m.TryGetProperty("infiniteBus", out JsonElement ie) && Bool(ie, p + ".infiniteBus");
                UserModel? exc = Controller(m, "exciter", p, resolveModel, used);
                UserModel? gov = Controller(m, "governor", p, resolveModel, used);
                UserModel? pss = Controller(m, "stabiliser", p, resolveModel, used);
                var coded = new List<string>();
                if (m.TryGetProperty("coded", out JsonElement ce))
                {
                    if (ce.ValueKind != JsonValueKind.Array) throw new FullBindingException(p + ".coded", "must be an array of controller names.");
                    int ci = 0;
                    foreach (JsonElement c in ce.EnumerateArray()) coded.Add(Str(c, $"{p}.coded[{ci++}]"));
                }
                specs.Add(new FullMachineSpec(gi, mm, xdp, xd, xq, tdop, damp, inf, exc, gov, pss, coded.Count == 0 ? null : coded));
            }
            return new FullMachineBindings(specs, hash, used);
        }
    }

    private static UserModel? Controller(JsonElement m, string name, string p, Func<string, byte[]?> resolve, List<(string, string)> used)
    {
        if (!m.TryGetProperty(name, out JsonElement e)) return null;
        string path = $"{p}.{name}";
        CheckMembers(e, path, new[] { "model" }, new[] { "parameters" });
        string id = Str(e.GetProperty("model"), path + ".model");
        byte[] bytes = resolve(id) ?? throw new FullBindingException(path + ".model", $"no model document or shipped model named '{id}'.");
        Dictionary<string, double>? pars = null;
        if (e.TryGetProperty("parameters", out JsonElement pe))
        {
            if (pe.ValueKind != JsonValueKind.Object) throw new FullBindingException(path + ".parameters", "must be an object of name: number.");
            pars = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (JsonProperty kv in pe.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(kv.Value.GetDouble()))
                    throw new FullBindingException($"{path}.parameters.{kv.Name}", "must be a finite number.");
                pars[kv.Name] = kv.Value.GetDouble();
            }
        }
        try
        {
            ModelDocument md = ModelDocument.Parse(bytes);
            UserModel um = UserModel.FromDocument(md, pars);
            used.Add((md.Id, md.Sha256));
            return um;
        }
        catch (UserModelException ex)
        {
            throw new FullBindingException(path, $"model '{id}' was refused: " + string.Join("; ", ex.Issues.Select(i => $"[{i.Code}] {i.Object}: {i.Message}")));
        }
    }

    private static void CheckMembers(JsonElement e, string path, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FullBindingException(path, "must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new FullBindingException(path, $"the member '{p.Name}' appears twice.");
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw new FullBindingException(path, $"the member '{p.Name}' is not part of {Format} (allowed: {string.Join(", ", required.Concat(optional))}).");
        }
        foreach (string r in required) if (!seen.Contains(r)) throw new FullBindingException(path, $"the required member '{r}' is missing.");
    }

    private static string Str(JsonElement e, string path) => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FullBindingException(path, "must be a string.");

    private static bool Bool(JsonElement e, string path) => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new FullBindingException(path, "must be true or false.");

    private static int Int(JsonElement e, string path)
        => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : throw new FullBindingException(path, "must be a whole number.");

    private static double Num(JsonElement m, string name, string p, double dflt)
    {
        if (!m.TryGetProperty(name, out JsonElement e)) return dflt;
        if (e.ValueKind != JsonValueKind.Number || !double.IsFinite(e.GetDouble()))
            throw new FullBindingException($"{p}.{name}", "must be a finite number (got " + e.ToString().ToString(CultureInfo.InvariantCulture) + ").");
        return e.GetDouble();
    }
}
