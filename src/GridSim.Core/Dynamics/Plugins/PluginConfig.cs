using System.Text.Json;
using System.Text.RegularExpressions;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>A plug-in configuration or loading rule was broken. The message names the entry.</summary>
public sealed class PluginRefusedException(string message) : InvalidOperationException(message);

/// <summary>One assembly entry of a <c>gridsim-plugins/1</c> configuration.</summary>
/// <param name="Path">The path as written (relative to the root, or absolute inside it).</param>
/// <param name="Sha256">The pinned SHA-256 of the file's bytes (lower-case hex).</param>
/// <param name="Models">The model ids to take from it, in the written order.</param>
public sealed record PluginAssemblyEntry(string Path, string Sha256, IReadOnlyList<string> Models)
{
    /// <summary>RC5 WP32: the plug-in contract the entry says the assembly is written against (1 or 2), or null when the entry
    /// does not say. When given, the assembly's <see cref="GridSimModelPluginAttribute"/> must declare the same version.</summary>
    public int? Contract { get; init; }
}

/// <summary>
/// A parsed <c>gridsim-plugins/1</c> configuration: <c>{ "schema": "gridsim-plugins/1", "root": "&lt;folder&gt;",
/// "assemblies": [ { "path": "rel.dll", "sha256": "&lt;hex&gt;", "models": ["ID", ...], "contract": 2 } ] }</c> ("contract" is
/// optional, RC5 WP32). The root defaults to the
/// configuration file's folder, and a relative root is read from that folder. Parsing is strict: an unknown or repeated
/// member, a malformed hash or id, or an empty list is refused naming the entry. There is no default folder, no environment
/// variable and no discovery: only the listed files are ever read.
/// </summary>
public sealed class PluginConfig
{
    /// <summary>The format identifier.</summary>
    public const string Schema = "gridsim-plugins/1";

    private static readonly Regex HexRe = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>The model id rule: 1 to 16 of A-Z, 0-9 and underscore.</summary>
    public static readonly Regex IdRe = new("^[A-Z0-9_]{1,16}$", RegexOptions.CultureInvariant);

    /// <summary>The configuration file's full path.</summary>
    public string ConfigPath { get; private init; } = "";

    /// <summary>The SHA-256 of the configuration file's bytes.</summary>
    public string ConfigSha256 { get; private init; } = "";

    /// <summary>The resolved root folder (full path).</summary>
    public string Root { get; private init; } = "";

    /// <summary>The assembly entries, in the written order.</summary>
    public IReadOnlyList<PluginAssemblyEntry> Assemblies { get; private init; } = Array.Empty<PluginAssemblyEntry>();

    /// <summary>Read and parse a configuration file.</summary>
    public static PluginConfig Read(string configPath)
    {
        ArgumentNullException.ThrowIfNull(configPath);
        if (GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(configPath))
            throw new PluginRefusedException($"model plug-ins: the configuration '{configPath}' is a network or device path; use a local file.");
        string full = System.IO.Path.GetFullPath(configPath);
        if (!File.Exists(full)) throw new PluginRefusedException($"model plug-ins: the configuration file '{configPath}' does not exist.");
        return Parse(File.ReadAllBytes(full), full);
    }

    /// <summary>Parse configuration bytes read from <paramref name="configFullPath"/>.</summary>
    public static PluginConfig Parse(byte[] bytes, string configFullPath)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { throw new PluginRefusedException("model plug-ins: the configuration is not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            Members(root, "$", new[] { "schema", "assemblies" }, new[] { "root" });
            string schema = Str(root.GetProperty("schema"), "$.schema");
            if (schema != Schema) throw new PluginRefusedException($"model plug-ins: $.schema is '{schema}'; this build reads {Schema}.");
            string folder = System.IO.Path.GetDirectoryName(configFullPath)!;
            string rootDir = folder;
            if (root.TryGetProperty("root", out JsonElement re))
            {
                string written = Str(re, "$.root");
                if (GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(written))
                    throw new PluginRefusedException($"model plug-ins: $.root '{written}' is a network or device path.");
                rootDir = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(written) ? written : System.IO.Path.Combine(folder, written));
            }
            if (root.GetProperty("assemblies").ValueKind != JsonValueKind.Array) throw new PluginRefusedException("model plug-ins: $.assemblies must be an array.");
            var list = new List<PluginAssemblyEntry>();
            int i = 0;
            foreach (JsonElement e in root.GetProperty("assemblies").EnumerateArray())
            {
                string at = $"$.assemblies[{i++}]";
                // RC5 WP32: an optional "contract": 1 | 2 (absent: the entry reads exactly as before).
                Members(e, at, new[] { "path", "sha256", "models" }, new[] { "contract" });
                string path = Str(e.GetProperty("path"), at + ".path");
                string sha = Str(e.GetProperty("sha256"), at + ".sha256");
                if (!HexRe.IsMatch(sha)) throw new PluginRefusedException($"model plug-ins: {at}.sha256 must be 64 lower-case hex digits.");
                JsonElement me = e.GetProperty("models");
                if (me.ValueKind != JsonValueKind.Array || me.GetArrayLength() == 0) throw new PluginRefusedException($"model plug-ins: {at}.models must be a non-empty array of model ids.");
                var ids = new List<string>();
                int k = 0;
                foreach (JsonElement m in me.EnumerateArray())
                {
                    string id = Str(m, $"{at}.models[{k++}]");
                    if (!IdRe.IsMatch(id)) throw new PluginRefusedException($"model plug-ins: {at}.models: '{id}' is not a model id (1 to 16 of A-Z, 0-9 and underscore).");
                    ids.Add(id);
                }
                int? contract = null;
                if (e.TryGetProperty("contract", out JsonElement ce))
                {
                    if (ce.ValueKind != JsonValueKind.Number || !ce.TryGetInt32(out int cv) || !PluginContract.IsSupported(cv))
                        throw new PluginRefusedException($"model plug-ins: {at}.contract must be {PluginContract.Version} or {PluginContract.Version2}.");
                    contract = cv;
                }
                list.Add(new PluginAssemblyEntry(path, sha, ids) { Contract = contract });
            }
            if (list.Count == 0) throw new PluginRefusedException("model plug-ins: $.assemblies is empty.");
            return new PluginConfig { ConfigPath = configFullPath, ConfigSha256 = hash, Root = rootDir, Assemblies = list };
        }
    }

    private static void Members(JsonElement e, string at, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new PluginRefusedException($"model plug-ins: {at} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new PluginRefusedException($"model plug-ins: {at}: the member '{p.Name}' appears twice.");
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw new PluginRefusedException($"model plug-ins: {at}: unknown member '{p.Name}'.");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new PluginRefusedException($"model plug-ins: {at}: the member '{r}' is missing.");
    }

    private static string Str(JsonElement e, string at)
        => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new PluginRefusedException($"model plug-ins: {at} must be a string.");
}
