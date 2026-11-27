using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using GridSim.Core.Dynamics.UserModels;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>The models of one loaded plug-in configuration.</summary>
public sealed class PluginSet
{
    internal PluginSet(PluginConfig config, IReadOnlyList<PluginModelDefinition> models, AssemblyLoadContext context)
    {
        Config = config;
        Models = models;
        Context = context;
    }

    /// <summary>The configuration it was loaded from.</summary>
    public PluginConfig Config { get; }

    /// <summary>The listed models, in configuration order (assembly by assembly, id by id as written).</summary>
    public IReadOnlyList<PluginModelDefinition> Models { get; }

    internal AssemblyLoadContext Context { get; }

    /// <summary>The model with id <paramref name="id"/>, or null.</summary>
    public PluginModelDefinition? Find(string id)
    {
        foreach (PluginModelDefinition m in Models) if (m.Id == id) return m;
        return null;
    }
}

/// <summary>
/// Loads model plug-ins (contract versions <see cref="PluginContract.Version"/> and <see cref="PluginContract.Version2"/>), only from the assemblies a
/// <c>gridsim-plugins/1</c> configuration names, only after every check below has passed for EVERY entry, so a refusal loads
/// nothing:
/// <list type="number">
/// <item>the listed ids are unique across the configuration and differ from every shipped declarative model id;</item>
/// <item>each path resolves inside the root under the confinement rule (<see cref="GridSim.Core.Security.PathConfinement"/>:
/// no escape, no absolute path outside, no network or device path, no symbolic link or junction on any component);</item>
/// <item>the SHA-256 of the file's bytes equals the pinned value (the bytes are read once; the same bytes are verified and
/// loaded, so nothing can change between the check and the load);</item>
/// <item>the assembly carries <c>[GridSimModelPlugin(1)]</c> or <c>[GridSimModelPlugin(2)]</c> from GridSim.Core (and, when the
/// entry gives a "contract", the same version); the machine and relay roles are contract 2 roles (a contract 1 assembly that
/// gives their numbers is read as giving no contract 1 role, as before contract 2 existed);</item>
/// <item>the determinism lint (<see cref="PluginVerifier"/>) finds nothing.</item>
/// </list>
/// The assemblies are then loaded from those bytes into one collectible <see cref="AssemblyLoadContext"/> per configuration,
/// named <c>gridsim-plugins:&lt;configuration path&gt;</c>, which resolves nothing itself (the framework and GridSim.Core come
/// from the host, so the contract interfaces are the host's). Each listed id must then be found among the assembly's public
/// factory classes. This is a lint and a pin, not a sandbox: a plug-in runs with the user's rights.
/// </summary>
public static class PluginLoader
{
    /// <summary>The name of the load context of the configuration at <paramref name="configFullPath"/>.</summary>
    public static string ContextName(string configFullPath) => "gridsim-plugins:" + configFullPath;

    /// <summary>Read the configuration at <paramref name="configPath"/> and load its plug-ins.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Loads plug-in assemblies and finds their factory classes by reflection.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Loads plug-in assemblies at run time.")]
    public static PluginSet Load(string configPath) => Load(PluginConfig.Read(configPath));

    /// <summary>Load the plug-ins of a parsed configuration.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Loads plug-in assemblies and finds their factory classes by reflection.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Loads plug-in assemblies at run time.")]
    public static PluginSet Load(PluginConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Assemblies.Count; i++)
        {
            PluginAssemblyEntry e = config.Assemblies[i];
            foreach (string id in e.Models)
            {
                if (UserModelLibrary.Ids.Contains(id, StringComparer.Ordinal))
                    throw new PluginRefusedException($"model plug-ins: assemblies[{i}] ({e.Path}): the id {id} is a shipped model's id; a plug-in model needs an id of its own.");
                if (!listed.Add(id))
                    throw new PluginRefusedException($"model plug-ins: assemblies[{i}] ({e.Path}): the id {id} is listed twice.");
            }
        }

        // Every check on every entry first; nothing is loaded until all have passed.
        var verified = new List<(PluginAssemblyEntry Entry, byte[] Bytes, int Version, string Name)>();
        for (int i = 0; i < config.Assemblies.Count; i++)
        {
            PluginAssemblyEntry e = config.Assemblies[i];
            string who = $"model plug-ins: assemblies[{i}] ({e.Path})";
            if (GridSim.Core.Security.PathConfinement.IsNetworkOrDevice(e.Path))
                throw new PluginRefusedException($"{who}: a network or device path is refused.");
            string full = Path.GetFullPath(Path.IsPathRooted(e.Path) ? e.Path : Path.Combine(config.Root, e.Path));
            if (GridSim.Core.Security.PathConfinement.Refusal(e.Path, full, config.Root) is { } why)
                throw new PluginRefusedException($"{who}: {why} (the root is the configuration's 'root').");
            if (!File.Exists(full)) throw new PluginRefusedException($"{who}: the file does not exist.");
            byte[] bytes = File.ReadAllBytes(full);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, e.Sha256, StringComparison.Ordinal))
                throw new PluginRefusedException($"{who}: the file's SHA-256 is {hash}, not the pinned {e.Sha256}.");
            PluginVerification v;
            try { v = PluginVerifier.Verify(bytes); }
            catch (BadImageFormatException ex) { throw new PluginRefusedException($"{who}: {ex.Message}"); }
            // A GridSim assembly that grants another its internals (InternalsVisibleTo, unsigned) would grant them to a plug-in of
            // that name too, so a plug-in may not take one of those names.
            if (FriendAssemblyNames().Contains(v.AssemblyName))
                throw new PluginRefusedException($"{who}: the assembly is named {v.AssemblyName}, a name GridSim grants access to its internals (InternalsVisibleTo); a plug-in may not take it.");
            if (v.ContractVersion is not int cv)
                throw new PluginRefusedException($"{who}: the assembly {v.AssemblyName} carries no [GridSimModelPlugin(1)] or [GridSimModelPlugin(2)] attribute, so it is not a model plug-in.");
            // RC5 WP32: contracts 1 and 2 (contract 2 adds the machine and relay roles; every contract 1 interface is unchanged).
            if (!PluginContract.IsSupported(cv))
                throw new PluginRefusedException($"{who}: the assembly declares plug-in contract {cv.ToString(CultureInfo.InvariantCulture)}; this build implements contracts 1 and 2.");
            if (e.Contract is int said && said != cv)
                throw new PluginRefusedException($"{who}: the entry says contract {said.ToString(CultureInfo.InvariantCulture)} but the assembly declares contract {cv.ToString(CultureInfo.InvariantCulture)}.");
            if (v.Refusals.Count > 0)
                throw new PluginRefusedException($"{who}: the determinism check refuses {v.Refusals.Count} reference(s): " + string.Join("; ", v.Refusals));
            verified.Add((e, bytes, cv, v.AssemblyName));
        }

        var context = new PluginLoadContext(ContextName(config.ConfigPath));
        try
        {
            var models = new List<PluginModelDefinition>();
            for (int i = 0; i < verified.Count; i++)
            {
                (PluginAssemblyEntry e, byte[] bytes, int cv, string name) = verified[i];
                string who = $"model plug-ins: assemblies[{i}] ({e.Path})";
                Assembly asm;
                using (var ms = new MemoryStream(bytes, writable: false)) asm = context.LoadFromStream(ms);
                var byId = new SortedDictionary<string, IUserModelFactory>(StringComparer.Ordinal);
                foreach (Type t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    if (!t.IsClass || t.IsAbstract || !typeof(IUserModelFactory).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) is null) continue;
                    IUserModelFactory f;
                    string id;
                    try
                    {
                        f = (IUserModelFactory)Activator.CreateInstance(t)!;
                        id = f.Id;
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw new PluginRefusedException($"{who}: constructing {t.FullName} failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                    if (id is null || !PluginConfig.IdRe.IsMatch(id))
                        throw new PluginRefusedException($"{who}: {t.FullName} has the id '{id}', which is not a model id (1 to 16 of A-Z, 0-9 and underscore).");
                    if (!byId.TryAdd(id, f)) throw new PluginRefusedException($"{who}: two classes give the id {id}.");
                }
                foreach (string id in e.Models)
                {
                    if (!byId.TryGetValue(id, out IUserModelFactory? f))
                        throw new PluginRefusedException($"{who}: the assembly has no model {id} (it has: {string.Join(", ", byId.Keys)}).");
                    // RC5 WP32: a contract 2 assembly must give a role of the contract. A contract 1 assembly loads exactly as it
                    // did in v2.1.105-RC-5: a role number outside the five contract 1 roles is not refused here (it was not then),
                    // and the definition carries it as no contract 1 role (PluginModelDefinition), so it is refused when bound,
                    // naming the number it gave.
                    // The role is read once: the check and the definition see the same value.
                    UserModelRole role = f.Role;
                    if (cv >= PluginContract.Version2 && !Enum.IsDefined(role))
                        throw new PluginRefusedException($"{who}: the model {id} gives the role {((int)role).ToString(CultureInfo.InvariantCulture)}, which is not a role of the plug-in contract.");
                    models.Add(new PluginModelDefinition(f, role, name, e.Path, e.Sha256, cv));
                }
            }
            return new PluginSet(config, models, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary>
    /// The names GridSim's own assemblies give access to their internals: every <see cref="InternalsVisibleToAttribute"/> target
    /// of GridSim.Core (the only GridSim assembly a plug-in may reference) and of every other GridSim assembly loaded in the
    /// default context, read from the attributes themselves (the simple name, before any public key). Compared ignoring case,
    /// as assembly names are.
    /// </summary>
    internal static IReadOnlySet<string> FriendAssemblyNames()
    {
        var sources = new List<Assembly> { typeof(PluginLoader).Assembly };
        foreach (Assembly a in AssemblyLoadContext.Default.Assemblies)
        {
            string? n = a.GetName().Name;
            if (n is not null && (n == "GridSim" || n.StartsWith("GridSim.", StringComparison.Ordinal)) && !sources.Contains(a)) sources.Add(a);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly a in sources)
            foreach (InternalsVisibleToAttribute ivt in a.GetCustomAttributes<InternalsVisibleToAttribute>())
            {
                string target = ivt.AssemblyName.Split(',')[0].Trim();
                if (target.Length > 0) names.Add(target);
            }
        return names;
    }

    /// <summary>One configuration's load context: collectible, and resolving nothing itself, so every reference (the framework
    /// and GridSim.Core, the only ones the verifier allows) comes from the host's default context.</summary>
    private sealed class PluginLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
