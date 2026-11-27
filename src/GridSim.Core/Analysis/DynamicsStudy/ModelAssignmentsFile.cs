using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Dynamics.Plugins;
using GridSim.Core.Dynamics.UserModels;

namespace GridSim.Core.Analysis.DynamicsStudy;

/// <summary>
/// RC5 WP12: a <c>gridsim-model-assignments/1</c> document, the CLI's route to user models in a dynamics study (a sidecar of
/// the study file; the frozen study document is not touched):
/// <code>
/// { "schema": "gridsim-model-assignments/1",
///   "userModels":   [ { "machine": { "bus": 1, "id": "1" }, "model": "TGOV1", "parameters": { "R": 0.05 }, "replacesStabiliser": false },
///                     { "machine": { "bus": 2, "id": "1" }, "file": "my.model.json", "sha256": "&lt;hex&gt;" } ],
///   "pluginModels": [ { "machine": { "bus": 3, "id": "1" }, "model": "MY_GOV", "parameters": { "R": 0.05 } } ] }
/// </code>
/// A declarative entry names a shipped model (<c>model</c>) or a document file (<c>file</c>, relative to this file, pinned by
/// its SHA-256). A plug-in entry names a model of the <c>--model-plugins</c> configuration; without one it is refused with
/// "plug-in models need --model-plugins". Parsing is strict: an unknown or repeated member is refused naming the entry.
/// </summary>
public static class ModelAssignmentsFile
{
    /// <summary>The format identifier.</summary>
    public const string Schema = "gridsim-model-assignments/1";

    /// <summary>The refusal of a plug-in entry without a plug-in configuration.</summary>
    public const string NeedsPluginsMessage = "plug-in models need --model-plugins";

    /// <summary>Parse the bytes of an assignments file read from <paramref name="fullPath"/>; plug-in entries are resolved in
    /// <paramref name="plugins"/> and bound (with the replay check at <paramref name="stepSeconds"/>).</summary>
    public static (IReadOnlyList<UserModelAssignment> UserModels, IReadOnlyList<PluginModelAssignment> PluginModels) Parse(byte[] bytes, string fullPath,
        PluginSet? plugins, double stepSeconds)
    {
        try { return ParseCore(bytes, fullPath, plugins, stepSeconds); }
        catch (UserModelException ex) { throw new InvalidDataException("model assignments: a user model was refused: " + ex.Message); }
    }

    private static (IReadOnlyList<UserModelAssignment> UserModels, IReadOnlyList<PluginModelAssignment> PluginModels) ParseCore(byte[] bytes, string fullPath,
        PluginSet? plugins, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(fullPath);
        string folder = Path.GetDirectoryName(Path.GetFullPath(fullPath))!;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { throw new InvalidDataException("model assignments: not valid JSON: " + ex.Message); }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            // RC5 WP32: "deviceModels" is read by ParseDevices (absent: the file reads exactly as before).
            Members(root, "$", new[] { "schema" }, new[] { "userModels", "pluginModels", "deviceModels" });
            string schema = Str(root.GetProperty("schema"), "$.schema");
            if (schema != Schema) throw new InvalidDataException($"model assignments: $.schema is '{schema}'; this build reads {Schema}.");
            var users = new List<UserModelAssignment>();
            var pluginList = new List<PluginModelAssignment>();
            if (root.TryGetProperty("pluginModels", out JsonElement pm) && pm.ValueKind == JsonValueKind.Array && pm.GetArrayLength() > 0 && plugins is null)
                throw new InvalidDataException($"model assignments: $.pluginModels: {NeedsPluginsMessage} (a plug-in configuration file); nothing was loaded.");
            if (root.TryGetProperty("userModels", out JsonElement um))
            {
                int i = 0;
                foreach (JsonElement e in Arr(um, "$.userModels"))
                {
                    string at = $"$.userModels[{i++}]";
                    Members(e, at, new[] { "machine" }, new[] { "model", "file", "sha256", "parameters", "replacesStabiliser" });
                    bool hasModel = e.TryGetProperty("model", out JsonElement me), hasFile = e.TryGetProperty("file", out JsonElement fe);
                    if (hasModel == hasFile) throw new InvalidDataException($"model assignments: {at}: give exactly one of 'model' (a shipped id) and 'file'.");
                    ModelDocument document;
                    if (hasModel)
                    {
                        if (e.TryGetProperty("sha256", out _)) throw new InvalidDataException($"model assignments: {at}: 'sha256' pins a 'file'; a shipped model is pinned by the build.");
                        string id = Str(me, at + ".model");
                        if (!UserModelLibrary.Ids.Contains(id, StringComparer.Ordinal))
                            throw new InvalidDataException($"model assignments: {at}.model: '{id}' is not a shipped model (shipped: {string.Join(", ", UserModelLibrary.Ids)}).");
                        document = UserModelLibrary.Document(id);
                    }
                    else
                    {
                        if (!e.TryGetProperty("sha256", out JsonElement he)) throw new InvalidDataException($"model assignments: {at}: a 'file' needs its 'sha256'.");
                        byte[] modelBytes = ReadPinned(Str(fe, at + ".file"), folder, at, Str(he, at + ".sha256"));
                        document = ModelDocument.Parse(modelBytes);
                    }
                    users.Add(new UserModelAssignment(Machine(e.GetProperty("machine"), at + ".machine"),
                        UserModel.FromDocument(document, Params(e, at)), Bool(e, "replacesStabiliser", at)));
                }
            }
            if (root.TryGetProperty("pluginModels", out JsonElement pe))
            {
                int i = 0;
                foreach (JsonElement e in Arr(pe, "$.pluginModels"))
                {
                    string at = $"$.pluginModels[{i++}]";
                    Members(e, at, new[] { "machine", "model" }, new[] { "parameters", "replacesStabiliser" });
                    string id = Str(e.GetProperty("model"), at + ".model");
                    PluginModelDefinition def = plugins!.Find(id)
                        ?? throw new InvalidDataException($"model assignments: {at}.model: '{id}' is not a model of the plug-in configuration (it lists: {string.Join(", ", plugins.Models.Select(m => m.Id))}).");
                    pluginList.Add(new PluginModelAssignment(Machine(e.GetProperty("machine"), at + ".machine"),
                        new PluginModel(def, Params(e, at), stepSeconds), Bool(e, "replacesStabiliser", at)));
                }
            }
            return (users, pluginList);
        }
    }

    /// <summary>
    /// RC5 WP32: the file's <c>deviceModels</c>, the injection, machine and load models of the study (empty when the member is
    /// absent). Each entry is <c>{ "role": "injection" | "machine" | "load", "bind": {...}, "plugin": "ID" }</c> or, for a
    /// declarative model, <c>"file": "x.model.json", "sha256": "&lt;hex&gt;"</c> in place of <c>"plugin"</c>, with optional
    /// <c>"parameters"</c>. An injection or machine model binds to a unit, <c>"bind": { "machine": { "bus": 3, "id": "1" } }</c>;
    /// a load model to a bus, <c>"bind": { "bus": 5, "pMw": 10, "qMvar": 2 }</c>. The role must be the model's own role. A
    /// machine model is a contract 2 plug-in (the declarative format has no machine role). Relay entries (RC5 wp32b) are read by
    /// <see cref="ParseModels"/> and left out here.
    /// Plug-in entries need <paramref name="plugins"/>, and are bound with the replay check at <paramref name="stepSeconds"/>.
    /// </summary>
    public static IReadOnlyList<DeviceModelAssignment> ParseDevices(byte[] bytes, string fullPath, PluginSet? plugins, double stepSeconds)
        => ParseModels(bytes, fullPath, plugins, stepSeconds).Devices;

    /// <summary>
    /// RC5 wp32b: the file's <c>deviceModels</c>: the injection, machine and load models (as <see cref="ParseDevices"/>) and the
    /// relays. A relay entry is <c>{ "role": "relay", "bind": {...}, "plugin": "ID" }</c>, or a declarative relay (<c>"file"</c> and
    /// <c>"sha256"</c>, a <c>gridsim-model/1</c> document of role <c>relay</c>), with optional <c>"parameters"</c>. It binds to a
    /// unit, <c>"bind": { "machine": { "bus": 3, "id": "1" } }</c>, or to a branch, <c>"bind": { "branch": { "from": 4, "to": 5,
    /// "circuit": "1" } }</c>, read at its <c>from</c> end as written.
    /// </summary>
    public static (IReadOnlyList<DeviceModelAssignment> Devices, IReadOnlyList<RelayModelAssignment> Relays) ParseModels(byte[] bytes,
        string fullPath, PluginSet? plugins, double stepSeconds)
    {
        try { return ParseDevicesCore(bytes, fullPath, plugins, stepSeconds); }
        catch (UserModelException ex) { throw new InvalidDataException("model assignments: a user model was refused: " + ex.Message); }
        catch (ArgumentException ex) { throw new InvalidDataException("model assignments: " + ex.Message); }
    }

    private static (IReadOnlyList<DeviceModelAssignment> Devices, IReadOnlyList<RelayModelAssignment> Relays) ParseDevicesCore(byte[] bytes,
        string fullPath, PluginSet? plugins, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(fullPath);
        string folder = Path.GetDirectoryName(Path.GetFullPath(fullPath))!;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(bytes), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { throw new InvalidDataException("model assignments: not valid JSON: " + ex.Message); }
        var list = new List<DeviceModelAssignment>();
        var relays = new List<RelayModelAssignment>();
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("deviceModels", out JsonElement dm)) return (list, relays);
            int i = 0;
            foreach (JsonElement e in Arr(dm, "$.deviceModels"))
            {
                string at = $"$.deviceModels[{i++}]";
                Members(e, at, new[] { "role", "bind" }, new[] { "plugin", "file", "sha256", "parameters" });
                string role = Str(e.GetProperty("role"), at + ".role");
                if (role is not ("injection" or "machine" or "load" or "relay"))
                    throw new InvalidDataException($"model assignments: {at}.role must be \"injection\", \"machine\", \"load\" or \"relay\" (got \"{role}\").");
                bool hasPlugin = e.TryGetProperty("plugin", out JsonElement pe), hasFile = e.TryGetProperty("file", out JsonElement fe);
                if (hasPlugin == hasFile) throw new InvalidDataException($"model assignments: {at}: give exactly one of 'plugin' (a plug-in model id) and 'file' (a declarative model).");
                UserDeviceModel? device = null;
                PluginModel? relayPlugin = null;
                UserModel? relayDeclared = null;
                if (hasPlugin)
                {
                    if (e.TryGetProperty("sha256", out _)) throw new InvalidDataException($"model assignments: {at}: 'sha256' pins a 'file'; a plug-in is pinned by its configuration.");
                    if (plugins is null) throw new InvalidDataException($"model assignments: {at}: {NeedsPluginsMessage} (a plug-in configuration file); nothing was loaded.");
                    string id = Str(pe, at + ".plugin");
                    PluginModelDefinition def = plugins.Find(id)
                        ?? throw new InvalidDataException($"model assignments: {at}.plugin: '{id}' is not a model of the plug-in configuration (it lists: {string.Join(", ", plugins.Models.Select(m => m.Id))}).");
                    if (def.RoleText != role)
                        throw new InvalidDataException($"model assignments: {at}: the plug-in model {id} has role {def.RoleText}, not {role}.");
                    if (role == "relay") relayPlugin = new PluginModel(def, Params(e, at), stepSeconds);
                    else device = UserDeviceModel.FromPlugin(new PluginModel(def, Params(e, at), stepSeconds));
                }
                else
                {
                    if (role == "machine")
                        throw new InvalidDataException($"model assignments: {at}: a machine model is a contract 2 plug-in; the declarative format has no machine role.");
                    if (!e.TryGetProperty("sha256", out JsonElement he)) throw new InvalidDataException($"model assignments: {at}: a 'file' needs its 'sha256'.");
                    byte[] modelBytes = ReadPinned(Str(fe, at + ".file"), folder, at, Str(he, at + ".sha256"));
                    UserModel model = UserModel.FromDocument(ModelDocument.Parse(modelBytes), Params(e, at));
                    if (model.Role != role)
                        throw new InvalidDataException($"model assignments: {at}: the model {model.Id} has role {model.Role}, not {role}.");
                    if (role == "relay") relayDeclared = model;
                    else device = UserDeviceModel.FromDeclarative(model);
                }
                JsonElement bind = e.GetProperty("bind");
                if (role == "relay")
                {
                    // RC5 wp32b: a relay binds to a unit or to a branch (exactly one of the two).
                    if (bind.ValueKind == JsonValueKind.Object && bind.TryGetProperty("machine", out _))
                    {
                        Members(bind, at + ".bind", new[] { "machine" }, Array.Empty<string>());
                        DynamicsMachineId m = Machine(bind.GetProperty("machine"), at + ".bind.machine");
                        relays.Add(relayPlugin is not null ? RelayModelAssignment.AtMachine(m, relayPlugin) : RelayModelAssignment.AtMachine(m, relayDeclared!));
                    }
                    else
                    {
                        Members(bind, at + ".bind", new[] { "branch" }, Array.Empty<string>());
                        DynamicsBranchId b = BranchId(bind.GetProperty("branch"), at + ".bind.branch");
                        relays.Add(relayPlugin is not null ? RelayModelAssignment.AtBranch(b, relayPlugin) : RelayModelAssignment.AtBranch(b, relayDeclared!));
                    }
                    continue;
                }
                if (role == "load")
                {
                    Members(bind, at + ".bind", new[] { "bus", "pMw", "qMvar" }, Array.Empty<string>());
                    JsonElement b = bind.GetProperty("bus");
                    if (b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out int bus)) throw new InvalidDataException($"model assignments: {at}.bind.bus must be an integer.");
                    list.Add(DeviceModelAssignment.AtBus(bus, Finite(bind.GetProperty("pMw"), at + ".bind.pMw"), Finite(bind.GetProperty("qMvar"), at + ".bind.qMvar"), device!));
                }
                else
                {
                    Members(bind, at + ".bind", new[] { "machine" }, Array.Empty<string>());
                    list.Add(DeviceModelAssignment.AtMachine(Machine(bind.GetProperty("machine"), at + ".bind.machine"), device!));
                }
            }
        }
        return (list, relays);
    }

    /// <summary>ANA-23: the bytes of a model document named by an entry's <c>file</c>, read as a side file of the assignments
    /// file (<see cref="GridSim.Core.Security.SideFile"/>: no network or device path, resolved against this file's folder, at
    /// most <see cref="GridSim.Core.Security.SideFile.DocumentMaxBytes"/>) and checked against the entry's pin. A mismatch names
    /// the pin only, never the hash of the file that was read.</summary>
    private static byte[] ReadPinned(string written, string folder, string at, string pin)
    {
        string what = $"model assignments: {at}.file";
        string full = GridSim.Core.Security.SideFile.Resolve(written, folder, what);
        if (!File.Exists(full)) throw new InvalidDataException($"{what}: '{written}' does not exist.");
        byte[] bytes;
        try { bytes = GridSim.Core.Security.SideFile.ReadAllBytes(full, GridSim.Core.Security.SideFile.DocumentMaxBytes, what); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"{what}: '{written}' cannot be read ({ex.GetType().Name})."); }
        string got = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(pin, got, StringComparison.Ordinal))
            throw new InvalidDataException($"{what}: the SHA-256 of '{written}' is not the pinned {pin}.");
        return bytes;
    }

    /// <summary>RC5 wp32b: a branch by its native identity, <c>{ "from": 4, "to": 5, "circuit": "1" }</c>.</summary>
    private static DynamicsBranchId BranchId(JsonElement e, string at)
    {
        Members(e, at, new[] { "from", "to", "circuit" }, Array.Empty<string>());
        JsonElement f = e.GetProperty("from"), t = e.GetProperty("to");
        if (f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out int from)) throw new InvalidDataException($"model assignments: {at}.from must be an integer.");
        if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out int to)) throw new InvalidDataException($"model assignments: {at}.to must be an integer.");
        return new DynamicsBranchId(from, to, Str(e.GetProperty("circuit"), at + ".circuit"));
    }

    private static double Finite(JsonElement e, string at)
        => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double v) && double.IsFinite(v) ? v
            : throw new InvalidDataException($"model assignments: {at} must be a finite number.");

    private static Dictionary<string, double>? Params(JsonElement e, string at)
    {
        if (!e.TryGetProperty("parameters", out JsonElement p)) return null;
        if (p.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"model assignments: {at}.parameters must be an object of numbers.");
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonProperty x in p.EnumerateObject())
        {
            if (x.Value.ValueKind != JsonValueKind.Number) throw new InvalidDataException($"model assignments: {at}.parameters.{x.Name} must be a number.");
            if (!d.TryAdd(x.Name, x.Value.GetDouble())) throw new InvalidDataException($"model assignments: {at}.parameters: '{x.Name}' appears twice.");
        }
        return d;
    }

    private static bool Bool(JsonElement e, string name, string at)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return false;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean()
            : throw new InvalidDataException($"model assignments: {at}.{name} must be true or false.");
    }

    private static DynamicsMachineId Machine(JsonElement e, string at)
    {
        Members(e, at, new[] { "bus", "id" }, Array.Empty<string>());
        JsonElement b = e.GetProperty("bus");
        if (b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out int bus)) throw new InvalidDataException($"model assignments: {at}.bus must be an integer.");
        return new DynamicsMachineId(bus, Str(e.GetProperty("id"), at + ".id"));
    }

    private static List<JsonElement> Arr(JsonElement e, string at)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : throw new InvalidDataException($"model assignments: {at} must be an array.");

    private static void Members(JsonElement e, string at, string[] required, string[] optional)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"model assignments: {at} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new InvalidDataException($"model assignments: {at}: the member '{p.Name}' appears twice.");
            if (Array.IndexOf(required, p.Name) < 0 && Array.IndexOf(optional, p.Name) < 0)
                throw new InvalidDataException($"model assignments: {at}: unknown member '{p.Name}'.");
        }
        foreach (string r in required)
            if (!seen.Contains(r)) throw new InvalidDataException($"model assignments: {at}: the member '{r}' is missing.");
    }

    private static string Str(JsonElement e, string at)
        => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException($"model assignments: {at} must be a string.");
}
