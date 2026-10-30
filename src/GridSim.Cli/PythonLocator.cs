namespace GridSim.Cli;

/// <summary>
/// N219 (October 2026): which Python interpreter `gridsim crossval` runs its sidecar with, in a documented order, and
/// which one it actually used.
///
/// <para>The defect. The verb started the bare command names "python", "py" and "python3" in turn and let the
/// operating system find them. On Windows, CreateProcess searches the folder the CLI was started from, the current
/// folder and the Windows system folders BEFORE the PATH, and the store's "python" alias sits in a folder of its own,
/// so the interpreter that ran was not reliably the one first on PATH: the D1 session found a system Python without
/// pandapower chosen while a virtual environment's Scripts folder was first on PATH (the sidecar then exited 77,
/// "pandapower absent", although the environment the user had set up had it). The verb also never said which
/// interpreter it had used.</para>
///
/// <para>The rule, in order (the first that applies wins):</para>
/// <list type="number">
/// <item><c>GRIDSIM_PYTHON</c>, when set: that interpreter and no other. A path must exist; a bare name is looked up
/// on PATH. When it names nothing that exists the run fails loud (no fallback: the user asked for that one).</item>
/// <item>An active virtual environment, <c>VIRTUAL_ENV</c>: its own interpreter (<c>Scripts\python.exe</c> on
/// Windows, <c>bin/python</c> elsewhere), when that file exists.</item>
/// <item>The PATH, folder by folder in order: in each folder <c>python.exe</c> then <c>python3.exe</c> on Windows,
/// <c>python3</c> then <c>python</c> elsewhere. The search is done here, so PATH order is honoured exactly.</item>
/// <item>On Windows, the <c>py</c> launcher by name.</item>
/// <item>Last, the bare names <c>python</c> and <c>python3</c>, as before, for a setup whose interpreter only the
/// operating system can find (a shim script, for example).</item>
/// </list>
/// <para>A candidate that is the Windows store alias (it exits 9009 without running) is skipped by the caller and the
/// next one is tried. The caller prints the interpreter and where it came from on every run.</para>
/// </summary>
internal static class PythonLocator
{
    /// <summary>The override environment variable: a full path to, or a command name of, the interpreter to use.</summary>
    internal const string OverrideVariable = "GRIDSIM_PYTHON";

    /// <summary>The variable an activated virtual environment sets to its own folder.</summary>
    internal const string VirtualEnvVariable = "VIRTUAL_ENV";

    /// <summary>One interpreter to try: the command (a full path, or a bare name the OS resolves) and why it is here.</summary>
    internal sealed record Candidate(string Command, string Source);

    /// <summary>
    /// The ordered candidates for this environment, or an error when <c>GRIDSIM_PYTHON</c> is set but names nothing
    /// that exists. Pure: the environment, the platform and the file system are passed in, so tests drive it directly.
    /// </summary>
    /// <param name="getEnv">Reads an environment variable (null when unset).</param>
    /// <param name="isWindows">True on Windows (decides file names, the venv layout and the py launcher).</param>
    /// <param name="fileExists">True when a file exists at the path.</param>
    /// <param name="error">Set (and the list empty) when the override cannot be honoured.</param>
    internal static IReadOnlyList<Candidate> Candidates(
        Func<string, string?> getEnv, bool isWindows, Func<string, bool> fileExists, out string? error)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(fileExists);
        error = null;
        var list = new List<Candidate>();
        var seen = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string command, string source)
        {
            if (seen.Add(command)) list.Add(new Candidate(command, source));
        }

        string[] pathDirs = (getEnv("PATH") ?? "")
            .Split(isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] names = isWindows ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" };

        // 1. The explicit override: that interpreter only.
        string? forced = getEnv(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            forced = forced.Trim().Trim('"');
            bool hasFolder = forced.Contains('/') || forced.Contains('\\');
            if (hasFolder)
            {
                if (fileExists(forced)) { Add(forced, $"{OverrideVariable}"); return list; }
                error = $"{OverrideVariable} is set to '{forced}', which does not exist; set it to a Python interpreter, or unset it to search the active virtual environment and PATH.";
                return Array.Empty<Candidate>();
            }
            // A bare name: look it up on PATH in order (with .exe on Windows when it has no extension).
            string file = isWindows && !forced.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? forced + ".exe" : forced;
            foreach (string dir in pathDirs)
            {
                string full = Path.Combine(dir, file);
                if (fileExists(full)) { Add(full, $"{OverrideVariable} ({forced} on PATH)"); return list; }
            }
            error = $"{OverrideVariable} is set to '{forced}', which is not on PATH; give a full path to a Python interpreter, or unset it.";
            return Array.Empty<Candidate>();
        }

        // 2. The active virtual environment's own interpreter.
        string? venv = getEnv(VirtualEnvVariable);
        if (!string.IsNullOrWhiteSpace(venv))
        {
            string py = isWindows ? Path.Combine(venv.Trim(), "Scripts", "python.exe") : Path.Combine(venv.Trim(), "bin", "python");
            if (fileExists(py)) Add(py, $"{VirtualEnvVariable}");
        }

        // 3. PATH, in order.
        foreach (string dir in pathDirs)
            foreach (string name in names)
            {
                string full = Path.Combine(dir, name);
                if (fileExists(full)) Add(full, $"PATH ({dir})");
            }

        // 4 and 5. The launcher, then the bare names the OS resolves (the old behaviour, now the last resort).
        if (isWindows) Add("py", "py launcher");
        Add("python", "bare name");
        Add("python3", "bare name");
        return list;
    }
}
