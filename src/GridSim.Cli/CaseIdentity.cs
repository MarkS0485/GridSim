using System.Security.Cryptography;
using System.Text.Json;

namespace GridSim.Cli;

/// <summary>
/// CLI-08: the consumed-file manifest of a native case, shared by every study verb that binds a case by SHA-256
/// (transmission-security, transfer-capability and motor-start through it, corrective-sequence, restoration-study,
/// planning-study). It used to be copied four times, so a file added to the loader's read list could be added to one copy
/// and not the others. One list now: eleven v1 directory files, or a v2 manifest with its <c>network</c>, <c>assets</c>,
/// <c>imports</c> and the schema files, in load order. Each verb still builds its own record, so the serialised output is
/// byte-identical to the copies this replaced.
/// </summary>
internal static class CaseIdentity
{
    /// <summary>The v1 directory files a case folder may hold, in load order.</summary>
    internal static readonly string[] V1Names =
    [
        "system.json", "buses.json", "generation.json", "demand.json", "transmission.json", "transformer.json",
        "onloadtapchanger.json", "asset-classes.json", "asset-bindings.json", "protections.json", "boundaries.json",
    ];

    /// <summary>The path comparison for "inside its declared root": exact on Linux and the other case-sensitive systems,
    /// ignoring case on Windows (the loader's own rule, FilePathComparison).</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The consumed files of <paramref name="path"/> (a case folder, or a v2 manifest) with their SHA-256, each
    /// built with <paramref name="make"/>(label, sha256). <paramref name="requireJsonExtension"/> keeps the one verb
    /// difference: planning-study reads any non-folder path as a manifest, the others only a <c>.json</c> one.</summary>
    internal static List<T> Inputs<T>(string path, string root, Func<string, string, T> make, bool requireJsonExtension = true)
    {
        var files = new List<T>();
        void Add(string label, string full) => files.Add(make(label, Hash(File.ReadAllBytes(full))));
        if (Directory.Exists(path))
        {
            foreach (string name in V1Names)
                if (File.Exists(Path.Combine(path, name))) Add(name, Path.Combine(path, name));
            return files;
        }
        Add(Path.GetFileName(path), path);
        if (requireJsonExtension && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return files;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        string directory = Path.GetDirectoryName(path)!;
        foreach (string kind in new[] { "network", "assets" })
            if (manifest.RootElement.TryGetProperty(kind, out JsonElement list))
                foreach (JsonElement item in list.EnumerateArray())
                    Add(kind + "/" + item.GetString(), Inside(directory, item.GetString()!));
        if (manifest.RootElement.TryGetProperty("imports", out JsonElement imports))
            foreach (JsonElement item in imports.EnumerateArray())
            {
                string library = item.GetProperty("library").GetString()!;
                Add("imports/" + library, Inside(Path.Combine(root, "data"), library));
            }
        string schemas = Gated.FindV2Schemas(root) ?? throw new InvalidDataException("v2 schemas unavailable.");
        foreach (string schema in Directory.GetFiles(schemas, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            Add("schemas/" + Path.GetRelativePath(schemas, schema).Replace('\\', '/'), schema);
        return files;
    }

    internal static string Inside(string root, string relative)
    {
        string full = Path.GetFullPath(relative, Path.GetFullPath(root));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison))
            throw new InvalidDataException("Dependency is outside its declared root.");
        return full;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
