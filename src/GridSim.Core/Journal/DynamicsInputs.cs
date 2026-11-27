using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Security;

namespace GridSim.Core.Journal;

/// <summary>
/// RC5 (2.1.127): the files a dynamics input names inside itself, so that the run journal can hash them (and keep them in
/// the snapshot store) and <c>breaking-change validate</c> can find them again, or refuse the re-run naming the one that
/// is missing or changed.
/// <list type="bullet">
/// <item>A <c>dynamics-study</c> study file (<c>schemaVersion</c> with <c>raw</c> and <c>dyr</c> file references): its
/// RAW case and DYR, and the adaptive member's unit protection file, device file and every <c>*.json</c> under its curves
/// folder.</item>
/// <item>A <c>gridsim-model-assignments/1</c> sidecar: each user model file it names.</item>
/// <item>A <c>gridsim-plugins/1</c> configuration: each plug-in assembly, resolved against the configuration's root as the
/// loader resolves it. An assembly is code: it is hashed but never kept in the snapshot store.</item>
/// </list>
/// Each file is given with the path as the run resolves it relative to the naming file's folder (<see cref="NamedFile.Written"/>):
/// relative when the reference is relative, the full path when it is absolute. A copy of the naming file placed elsewhere
/// with the relative files beside it in the same layout is therefore read exactly as the original.
/// </summary>
public static class DynamicsInputs
{
    /// <summary>The label prefix of a plug-in assembly.</summary>
    public const string PluginAssemblyLabel = "plugin-assembly:";

    /// <summary>The label prefix of a file of a study's adaptive curves folder.</summary>
    public const string CurvesLabel = "study:adaptive.curves/";

    /// <summary>The label of a study's adaptive curves folder itself (2.1.130). Its hash is that of the folder's file list
    /// (<see cref="FolderListing"/>), so a curve file added, removed or renamed later is seen, even when the folder was empty
    /// when the run was recorded. It is never kept in the snapshot store (it is a list, not a file).</summary>
    public const string CurvesFolderLabel = "study:adaptive.curves";

    /// <summary>
    /// The file list of a curves folder as the curve library reads it: every <c>*.json</c> under it, relative, with forward
    /// slashes, in ordinal order, one per line (UTF-8, a line feed after each). Hashed for <see cref="CurvesFolderLabel"/>.
    /// </summary>
    public static byte[] FolderListing(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var sb = new System.Text.StringBuilder();
        foreach (string f in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
            sb.Append(f).Append('\n');
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>A file named inside an input.</summary>
    /// <param name="Label">What names it (<c>study:raw</c>, <c>study:dyr</c>, <c>assignments:userModels[0].file</c>,
    /// <c>plugin-assembly:assemblies[0]</c>, ...).</param>
    /// <param name="Written">The path relative to the naming file's folder (forward slashes), or the full path when the
    /// reference is absolute.</param>
    /// <param name="Full">The full path the run reads.</param>
    /// <param name="Snapshot">False for a file the snapshot store must never keep (a plug-in assembly).</param>
    public sealed record NamedFile(string Label, string Written, string Full, bool Snapshot);

    /// <summary>
    /// The files <paramref name="bytes"/> (the input file at <paramref name="fullPath"/>) names, in the order it names them;
    /// empty for a file that is not one of the three kinds (or is not JSON).
    /// </summary>
    public static IReadOnlyList<NamedFile> Expand(string fullPath, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(bytes);
        var list = new List<NamedFile>();
        JsonDocument d;
        try { d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { return list; }
        using (d)
        {
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return list;
            string dir = Path.GetDirectoryName(Path.GetFullPath(fullPath))!;
            string? schema = r.TryGetProperty("schema", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (schema == PluginConfig.Schema) { Plugins(r, dir, list); return list; }
            if (schema == "gridsim-model-assignments/1") { Assignments(r, dir, list); return list; }
            if (schema is null && r.TryGetProperty("schemaVersion", out _) && Ref(r, "raw") is not null && Ref(r, "dyr") is not null)
                Study(r, dir, list);
        }
        return list;
    }

    // A file reference { "path": ..., "sha256": ... } of a study file.
    private static string? Ref(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("path", out JsonElement p)
            && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;

    private static void Add(List<NamedFile> list, string label, string written, string dir, bool snapshot)
    {
        if (PathConfinement.IsNetworkOrDevice(written)) return;
        string full;
        try { full = Path.GetFullPath(written, dir); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        string kept = Path.IsPathRooted(written) ? full : written.Replace('\\', '/');
        list.Add(new NamedFile(label, kept, full, snapshot));
    }

    private static void Study(JsonElement r, string dir, List<NamedFile> list)
    {
        Add(list, "study:raw", Ref(r, "raw")!, dir, true);
        Add(list, "study:dyr", Ref(r, "dyr")!, dir, true);
        if (!r.TryGetProperty("adaptive", out JsonElement a) || a.ValueKind != JsonValueKind.Object) return;
        if (Ref(a, "unitProtection") is string up) Add(list, "study:adaptive.unitProtection", up, dir, true);
        if (Ref(a, "devices") is string dv) Add(list, "study:adaptive.devices", dv, dir, true);
        if (a.TryGetProperty("curves", out JsonElement cv) && cv.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cv.GetString()))
        {
            string written = cv.GetString()!;
            if (PathConfinement.IsNetworkOrDevice(written)) return;
            string folder;
            try { folder = Path.GetFullPath(written, dir); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!Directory.Exists(folder)) return;
            // The folder itself, by its file list (2.1.130): written as the study writes it (relative, or the full path).
            list.Add(new NamedFile(CurvesFolderLabel, Path.IsPathRooted(written) ? folder : written.Replace('\\', '/').TrimEnd('/'), folder, false));
            // The curve library reads every *.json under the folder (CurveLibrary.LoadDirectory), in ordinal order.
            foreach (string f in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string inside = Path.GetRelativePath(folder, f).Replace('\\', '/');
                string w = Path.IsPathRooted(written) ? f : written.Replace('\\', '/').TrimEnd('/') + "/" + inside;
                list.Add(new NamedFile(CurvesLabel + inside, w, f, true));
            }
        }
    }

    private static void Assignments(JsonElement r, string dir, List<NamedFile> list)
    {
        if (r.TryGetProperty("userModels", out JsonElement um) && um.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (JsonElement e in um.EnumerateArray())
            {
                int k = i++;
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("file", out JsonElement f) && f.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(f.GetString()))
                    Add(list, $"assignments:userModels[{k}].file", f.GetString()!, dir, true);
            }
        }
        // RC5 WP32: the declarative model files of the injection and load models (none without "deviceModels", so every
        // existing journal lists exactly what it did). Plug-in assemblies are listed from the plug-in configuration.
        // RC5 wp32b: a declarative relay's file (an entry of role "relay") is listed by the same rule, whatever the role.
        if (r.TryGetProperty("deviceModels", out JsonElement dm) && dm.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (JsonElement e in dm.EnumerateArray())
            {
                int k = i++;
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("file", out JsonElement f) && f.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(f.GetString()))
                    Add(list, $"assignments:deviceModels[{k}].file", f.GetString()!, dir, true);
            }
        }
    }

    // The loader's own resolution (PluginConfig.Parse, PluginLoader.Load): the root is the configuration's folder, or its
    // "root" (relative to that folder); each assembly path is relative to the root, or absolute.
    private static void Plugins(JsonElement r, string dir, List<NamedFile> list)
    {
        string root = "";
        bool rootAbsolute = false;
        if (r.TryGetProperty("root", out JsonElement re) && re.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(re.GetString()))
        {
            root = re.GetString()!;
            rootAbsolute = Path.IsPathRooted(root);
        }
        if (!r.TryGetProperty("assemblies", out JsonElement asm) || asm.ValueKind != JsonValueKind.Array) return;
        int i = 0;
        foreach (JsonElement e in asm.EnumerateArray())
        {
            int k = i++;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("path", out JsonElement p) || p.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(p.GetString())) continue;
            string path = p.GetString()!;
            string written = Path.IsPathRooted(path) || rootAbsolute ? Path.Combine(root, path)
                : root.Length == 0 ? path : root.Replace('\\', '/').TrimEnd('/') + "/" + path;
            Add(list, $"{PluginAssemblyLabel}assemblies[{k}]", written, dir, false);
        }
    }

    /// <summary>True when <paramref name="bytes"/> are a JSON object whose <c>schema</c> is <c>gridsim-plugins/1</c>.</summary>
    public static bool IsPluginConfiguration(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using JsonDocument d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 16 });
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("schema", out JsonElement s)
                && s.ValueKind == JsonValueKind.String && s.GetString() == PluginConfig.Schema;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Check every plug-in assembly a <c>gridsim-plugins/1</c> configuration names against its pinned SHA-256, before a
    /// re-run is started. <paramref name="configBytes"/> are the configuration's bytes and <paramref name="configFullPath"/>
    /// is where the re-run will read it; <paramref name="read"/> gives the bytes the re-run will find at a full path (the
    /// file there, or the copy about to be placed there), or null when there is none. Returns null when every assembly is
    /// there with its pinned bytes, else the reason, naming the assembly. The configuration is parsed by the loader's own
    /// parser, so a configuration the loader refuses is refused here too. Nothing is loaded.
    /// </summary>
    public static string? VerifyPluginPins(byte[] configBytes, string configFullPath, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(configBytes);
        ArgumentNullException.ThrowIfNull(configFullPath);
        ArgumentNullException.ThrowIfNull(read);
        PluginConfig config;
        try { config = PluginConfig.Parse(configBytes, configFullPath); }
        catch (PluginRefusedException ex) { return "plug-in configuration refused: " + ex.Message; }
        foreach (PluginAssemblyEntry e in config.Assemblies)
        {
            if (PathConfinement.IsNetworkOrDevice(e.Path)) return $"plug-in assembly refused: {e.Path} is a network or device path";
            string full = Path.GetFullPath(Path.IsPathRooted(e.Path) ? e.Path : Path.Combine(config.Root, e.Path));
            if (PathConfinement.Refusal(e.Path, full, config.Root) is { } why) return $"plug-in assembly refused: {e.Path}: {why}";
            byte[]? bytes = read(full);
            if (bytes is null) return $"plug-in assembly missing: {full}";
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(hash, e.Sha256, StringComparison.Ordinal))
                return $"plug-in assembly changed: {full} sha256 {hash} != pinned {e.Sha256}";
        }
        return null;
    }
}
