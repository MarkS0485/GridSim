using System.Globalization;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>
/// One model of a loaded plug-in: its id, role and declared parameters, with the provenance of the assembly it came from
/// (the SHA-256 of the exact bytes that were verified and loaded, and the contract version it declares).
/// </summary>
public sealed class PluginModelDefinition
{
    private readonly int _declaredRole;

    internal PluginModelDefinition(IUserModelFactory factory, string assemblyName, string assemblyPath, string assemblySha256, int contractVersion)
        : this(factory, factory.Role, assemblyName, assemblyPath, assemblySha256, contractVersion)
    {
    }

    /// <summary>The definition of <paramref name="factory"/> with the role the loader read from it, once
    /// (<paramref name="declaredRole"/>): the factory's <c>Role</c> is not read again, so a factory that would answer differently
    /// on a second read cannot pass the loader's check with one role and be held with another.</summary>
    internal PluginModelDefinition(IUserModelFactory factory, UserModelRole declaredRole, string assemblyName, string assemblyPath, string assemblySha256,
        int contractVersion)
    {
        Factory = factory;
        Id = factory.Id;
        // RC5 wp32b: a contract 1 assembly whose factory gives a role number outside the five contract 1 roles (including the
        // numbers contract 2 gave to machine and relay) is held as no role at all, so every later check refuses it rather than
        // taking it as a contract 2 machine or relay. Its role text names the number it gave (RoleText).
        _declaredRole = (int)declaredRole;
        Role = contractVersion < PluginContract.Version2 && !PluginContract.IsContract1Role(declaredRole) ? PluginContract.NoContract1Role : declaredRole;
        Parameters = (factory.Parameters ?? throw new PluginRefusedException($"plug-in model {factory.Id}: Parameters is null.")).ToArray();
        AssemblyName = assemblyName;
        AssemblyPath = assemblyPath;
        AssemblySha256 = assemblySha256;
        ContractVersion = contractVersion;
    }

    internal IUserModelFactory Factory { get; }

    /// <summary>The model id.</summary>
    public string Id { get; }

    /// <summary>The role.</summary>
    public UserModelRole Role { get; }

    /// <summary>The declared parameters, in declared order.</summary>
    public IReadOnlyList<PluginParameter> Parameters { get; }

    /// <summary>The assembly's simple name.</summary>
    public string AssemblyName { get; }

    /// <summary>The assembly path as the configuration wrote it.</summary>
    public string AssemblyPath { get; }

    /// <summary>The SHA-256 of the assembly bytes (lower-case hex).</summary>
    public string AssemblySha256 { get; }

    /// <summary>The contract version the assembly declares.</summary>
    public int ContractVersion { get; }

    /// <summary>The role as the declarative format spells it (exciter, governor, stabiliser, injection, load; RC5 WP32: machine,
    /// relay). A contract 1 model whose factory gave a number that is no contract 1 role reads "number &lt;n&gt; (not a role of
    /// contract 1)", so a refusal that names its role says what it is.</summary>
    public string RoleText => Role switch
    {
        UserModelRole.Exciter => "exciter",
        UserModelRole.Governor => "governor",
        UserModelRole.Stabiliser => "stabiliser",
        UserModelRole.Injection => "injection",
        UserModelRole.Machine => "machine",
        UserModelRole.Relay => "relay",
        UserModelRole.Load => "load",
        _ => "number " + _declaredRole.ToString(CultureInfo.InvariantCulture) + " (not a role of contract " + ContractVersion.ToString(CultureInfo.InvariantCulture) + ")",
    };
}

/// <summary>
/// A plug-in model bound to parameter values (the plug-in counterpart of <see cref="UserModels.UserModel"/>). Binding
/// checks every value against its declared bounds and runs the replay check (<see cref="PluginReplay"/>): two instances
/// started at different operating points and stepped side by side must each repeat its own solo run bit for bit, or the model
/// is refused as not deterministic. It makes a fresh, independent
/// instance for every machine and every run.
/// </summary>
public sealed class PluginModel
{
    /// <summary>Bind <paramref name="definition"/> with <paramref name="parameters"/> (by name; the declared defaults for the
    /// rest) and run the replay check at step <paramref name="replayStepSeconds"/>.</summary>
    /// <exception cref="PluginRefusedException">An unknown name, a value outside its bounds or not finite, an instance of the
    /// wrong role, or a replay that differs.</exception>
    public PluginModel(PluginModelDefinition definition, IReadOnlyDictionary<string, double>? parameters = null, double replayStepSeconds = 0.005)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        if (parameters is not null)
            foreach (string name in parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (!definition.Parameters.Any(p => p.Name == name))
                    throw new PluginRefusedException($"plug-in model {definition.Id}: it has no parameter '{name}' (it has: {string.Join(", ", definition.Parameters.Select(p => p.Name))}).");
        var values = new double[definition.Parameters.Count];
        for (int i = 0; i < values.Length; i++)
        {
            PluginParameter p = definition.Parameters[i];
            double v = parameters is not null && parameters.TryGetValue(p.Name, out double given) ? given : p.Default;
            if (!double.IsFinite(v)) throw new PluginRefusedException($"plug-in model {definition.Id}: parameter {p.Name} is not finite.");
            if ((p.Min is double lo && v < lo) || (p.Max is double hi && v > hi))
                throw new PluginRefusedException($"plug-in model {definition.Id}: parameter {p.Name} = {v.ToString("R", CultureInfo.InvariantCulture)} {p.Unit} is outside "
                    + $"[{(p.Min ?? double.NegativeInfinity).ToString("R", CultureInfo.InvariantCulture)}, {(p.Max ?? double.PositiveInfinity).ToString("R", CultureInfo.InvariantCulture)}].");
            values[i] = v;
        }
        Values = values;
        PluginReplay.Check(this, replayStepSeconds);
    }

    /// <summary>The loaded model.</summary>
    public PluginModelDefinition Definition { get; }

    /// <summary>The bound values, in declared order.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>The model id.</summary>
    public string Id => Definition.Id;

    /// <summary>The role.</summary>
    public UserModelRole Role => Definition.Role;

    /// <summary>The assembly SHA-256.</summary>
    public string Hash => Definition.AssemblySha256;

    /// <summary>The provenance line every result that used this model carries.</summary>
    public string ProvenanceNote => $"plug-in model {Id} ({Definition.RoleText}) from {Definition.AssemblyName} sha256:{Hash} contract {Definition.ContractVersion.ToString(CultureInfo.InvariantCulture)}; "
        + "parameters: " + string.Join(", ", Definition.Parameters.Select((p, i) => p.Name + "=" + Values[i].ToString("R", CultureInfo.InvariantCulture)))
        + "; " + PluginContract.OutsideLabel;

    /// <summary>A fresh instance of the role interface <typeparamref name="T"/>.</summary>
    internal T New<T>() where T : class
    {
        object o;
        try { o = Definition.Factory.Create(Values.ToArray()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && ex is not PluginRefusedException)
        {
            throw new PluginRefusedException($"plug-in model {Id}: Create refused its parameters: {ex.Message}");
        }
        return o as T ?? throw new PluginRefusedException($"plug-in model {Id} has role {Definition.RoleText} but Create returned {(o is null ? "null" : o.GetType().FullName)}, which is not an {typeof(T).Name}.");
    }
}
