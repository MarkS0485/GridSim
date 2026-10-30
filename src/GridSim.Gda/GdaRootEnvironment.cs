namespace GridSim.Gda;

/// <summary>
/// D06 (Ruling R1, Mark delegated 2026-10-04): which environment variable names the GDA lake root when the caller gives
/// no explicit <c>--root</c>.
///
/// <para>The GDA estate's own convention is <c>GDA_DATA_ROOT</c> (its server configuration sets it, and its Python
/// builders and downloaders read it). GridSim used to read only <c>GDA_ROOT</c>. The ruling: GridSim reads
/// <c>GDA_DATA_ROOT</c> first and falls back to <c>GDA_ROOT</c>, so a machine set up for either works. Both name the
/// same folder: the lake directory that holds <c>NationalGrid/</c>, <c>NESO/</c> and the other publishers.</para>
///
/// <para>Fail loud: a variable that is SET (not empty) but does not name an existing folder is refused, naming the
/// variable. It never falls back to the other variable in that case, because a wrong setting must be fixed, not
/// silently replaced by a different lake. The value itself is never printed (the variables are machine settings and
/// may carry private paths); the message names only the variable.</para>
///
/// <para>An explicit <c>--root</c> always wins and is not checked here (the caller hands it to <see cref="GdaPaths"/>,
/// which refuses a missing folder as it always did).</para>
/// </summary>
public static class GdaRootEnvironment
{
    /// <summary>The GDA estate's own variable for the lake root; read first.</summary>
    public const string Primary = "GDA_DATA_ROOT";

    /// <summary>GridSim's older variable for the same folder; read only when <see cref="Primary"/> is unset or empty.</summary>
    public const string Fallback = "GDA_ROOT";

    /// <summary>
    /// The lake root from the environment, or <c>null</c> when neither variable is set (the caller then prints its
    /// usage). <paramref name="variable"/> receives the name of the variable the root came from (null when none).
    /// </summary>
    /// <param name="getEnvironmentVariable">How to read a variable (tests pass a dictionary lookup; production passes
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <param name="directoryExists">How to test a folder (tests pass a fake; production passes
    /// <see cref="Directory.Exists(string)"/>).</param>
    /// <param name="variable">The variable the returned root came from, or null when none was set.</param>
    /// <exception cref="DirectoryNotFoundException">The chosen variable is set but does not name an existing folder;
    /// the message names the variable, never its value.</exception>
    public static string? Resolve(Func<string, string?> getEnvironmentVariable, Func<string, bool> directoryExists,
        out string? variable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(directoryExists);

        // Primary first, then the fallback. "Set" means a non-empty value: an empty variable is treated as unset, as
        // the CLI always treated an empty GDA_ROOT (string.IsNullOrEmpty) before this change.
        foreach (string name in new[] { Primary, Fallback })
        {
            string? value = getEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;

            if (!directoryExists(value))
                throw new DirectoryNotFoundException(
                    $"the environment variable {name} is set but does not name an existing folder (its value is not " +
                    $"printed). Point {name} at the GDA lake folder (the one holding NationalGrid/ and NESO/), unset it, " +
                    "or pass --root <path>." +
                    (name == Primary
                        ? $" GridSim does not fall back to {Fallback} while {Primary} is set."
                        : ""));

            variable = name;
            return value;
        }

        variable = null;
        return null;
    }

    /// <summary>
    /// <see cref="Resolve(Func{string, string?}, Func{string, bool}, out string?)"/> on the real process environment
    /// and file system.
    /// </summary>
    public static string? Resolve(out string? variable) =>
        Resolve(Environment.GetEnvironmentVariable, Directory.Exists, out variable);
}
