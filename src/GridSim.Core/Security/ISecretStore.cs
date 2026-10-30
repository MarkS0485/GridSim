namespace GridSim.Core.Security;

/// <summary>
/// The seam through which a service resolves a secret it needs (a key-ring certificate password, a feed
/// credential, a message-bus connection string) - NCSC CAF B3 "data security / secrets management". The point
/// is that no service reads a secret straight from plain configuration or a committed file: it asks the secret
/// store, and a deployment chooses the backend.
///
/// <para><b>GridSim defines the seam; the deploying party supplies the backend</b>, exactly as with
/// <c>IAuditSink</c> and <c>IIncidentSink</c>. GridSim ships <see cref="EnvironmentSecretStore"/> (secrets from
/// environment variables, the current source) and <see cref="CompositeSecretStore"/> (try a chain). A
/// deployment that wants secrets out of the environment supplies its own store backed by an OS keychain,
/// Windows DPAPI, or a vault - without pulling a third-party dependency into the estimator core (Core stays on
/// the base class library only, which is itself a supply-chain assurance property).</para>
///
/// <para><b>Not a secret:</b> the GridSim licence key is signed, not encrypted (its fields are inspectable by
/// design), so it is not a confidential secret and does not need this store; the key-ring certificate password
/// and any feed credential do.</para>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Try to resolve the secret named <paramref name="name"/>. Returns true and sets <paramref name="value"/>
    /// when the store holds it; returns false (with <paramref name="value"/> null) when it does not. A store
    /// must not throw for a missing secret - a missing secret is a normal answer, not an error.
    /// </summary>
    bool TryGet(string name, out string? value);
}

/// <summary>Convenience helpers over <see cref="ISecretStore"/>.</summary>
public static class SecretStoreExtensions
{
    /// <summary>The secret's value, or null when the store does not hold it.</summary>
    public static string? Get(this ISecretStore store, string name)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.TryGet(name, out string? value) ? value : null;
    }
}

/// <summary>A secret store that holds nothing. The honest default when no store is configured: every lookup
/// misses, so a caller falls back to whatever it did before (for example reading configuration directly).</summary>
public sealed class NullSecretStore : ISecretStore
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullSecretStore Instance = new();

    private NullSecretStore() { }

    /// <inheritdoc />
    public bool TryGet(string name, out string? value)
    {
        value = null;
        return false;
    }
}

/// <summary>
/// Resolves secrets from environment variables (base-class-library only). A logical name such as
/// <c>Auth:KeyCertificatePassword</c> is tried as given and in the ASP.NET double-underscore form
/// (<c>Auth__KeyCertificatePassword</c>), so it matches how the host already surfaces configuration secrets. Only the
/// ':' separator is mapped, as ASP.NET does: <c>Bridge:API_KEY</c> is read from <c>Bridge__API_KEY</c>. The older
/// form with every '_' and '.' doubled (<c>Bridge__API__KEY</c>) is still tried after it.
/// An empty or whitespace value is treated as absent (an unset secret, not an empty secret).
/// </summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    /// <summary>The shared instance (environment variables are process-wide).</summary>
    public static readonly EnvironmentSecretStore Instance = new();

    /// <inheritdoc />
    public bool TryGet(string name, out string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string direct = Environment.GetEnvironmentVariable(name) ?? "";
        if (!string.IsNullOrWhiteSpace(direct)) { value = direct; return true; }

        // GR-719: the ASP.NET environment form replaces ONLY the section separator ':' with a double underscore and
        // leaves every other character alone, so Bridge:API_KEY is the variable Bridge__API_KEY. That form is tried
        // first. The older mapping (':' and '.' to '_', then every '_' doubled, giving Bridge__API__KEY) is still
        // tried second so a deployment that already set the variable that way keeps working; for a name with no '_'
        // or '.' the two forms are the same and the lookup happens once.
        string aspNetForm = name.Replace(":", "__", StringComparison.Ordinal);
        string legacyForm = name.Replace(':', '_').Replace('.', '_').Replace("_", "__", StringComparison.Ordinal);
        if (TryVariable(aspNetForm, name, out value)) return true;
        if (!string.Equals(legacyForm, aspNetForm, StringComparison.Ordinal) && TryVariable(legacyForm, name, out value))
            return true;

        value = null;
        return false;
    }

    // Read one alternative variable name; a name equal to the logical name was already tried as given and is skipped.
    private static bool TryVariable(string envForm, string name, out string? value)
    {
        value = null;
        if (string.Equals(envForm, name, StringComparison.Ordinal)) return false;
        string alt = Environment.GetEnvironmentVariable(envForm) ?? "";
        if (string.IsNullOrWhiteSpace(alt)) return false;
        value = alt;
        return true;
    }
}

/// <summary>
/// Resolves secrets from files in one directory, one secret per file: the layout of Docker / Compose secrets
/// (<c>/run/secrets/&lt;name&gt;</c>), Kubernetes secret volumes and systemd credentials (<c>$CREDENTIALS_DIRECTORY</c>).
/// The secret then never appears in configuration or the process environment, where any child process and many
/// diagnostics can read it. A logical name such as <c>Auth:KeyCertificatePassword</c> is looked up as the file
/// <c>Auth__KeyCertificatePassword</c> (the ASP.NET form) and then as given if that is a valid file name. One trailing
/// newline is removed (secret files are usually written with one); an empty file counts as absent.
///
/// <para><b>Fail loud on set-up, never on a miss.</b> The directory must exist when the store is built (a typo in the
/// setting must not silently mean "no secrets"). A name that could leave the directory (a path separator or
/// <c>..</c>) is refused. A missing file is a normal miss, as the <see cref="ISecretStore"/> contract requires.</para>
/// </summary>
public sealed class DirectorySecretStore : ISecretStore
{
    /// <summary>Read secrets from the files in <paramref name="directory"/>, which must exist.</summary>
    public DirectorySecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(Directory))
            throw new DirectoryNotFoundException($"The secrets directory '{Directory}' does not exist.");
    }

    /// <summary>The full path of the secrets directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public bool TryGet(string name, out string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (string candidate in new[] { name.Replace(":", "__", StringComparison.Ordinal), name })
        {
            if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains("..", StringComparison.Ordinal)
                || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;   // never a path outside the directory (and ':' is not a file name character on Windows)
            string path = Path.Combine(Directory, candidate);
            if (!File.Exists(path)) continue;
            string text = File.ReadAllText(path);
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
            else if (text.EndsWith('\n')) text = text[..^1];
            if (text.Length == 0) continue;
            value = text;
            return true;
        }
        value = null;
        return false;
    }
}

/// <summary>Tries each store in order and returns the first hit. A deployment puts its most trusted backend
/// first (an OS keychain or vault) and <see cref="EnvironmentSecretStore"/> last as the fallback.</summary>
public sealed class CompositeSecretStore : ISecretStore
{
    private readonly IReadOnlyList<ISecretStore> _stores;

    /// <summary>Create a composite over <paramref name="stores"/>, tried in order. At least one is required.</summary>
    public CompositeSecretStore(params ISecretStore[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Length == 0)
            throw new ArgumentException("A composite secret store needs at least one backing store.", nameof(stores));
        foreach (ISecretStore store in stores)
            ArgumentNullException.ThrowIfNull(store, nameof(stores));
        _stores = stores.ToArray();
    }

    /// <inheritdoc />
    public bool TryGet(string name, out string? value)
    {
        foreach (ISecretStore store in _stores)
            if (store.TryGet(name, out value))
                return true;
        value = null;
        return false;
    }
}
