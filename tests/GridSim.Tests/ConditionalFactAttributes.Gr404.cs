using Xunit;

namespace GridSim.Tests;

// GR-404 (completed 2026-11-02, owner decision): the last two tests that returned early, and so PASSED having checked
// nothing, now decide at discovery time and are reported as SKIPPED with the reason. They follow the pattern of the
// attributes in ConditionalFactAttributes.cs.

/// <summary>A fact that runs only when the named environment variable is NOT set (non-blank); when it is set the test is
/// SKIPPED with the reason, never passed. For a test that can only observe a default when the override is absent.</summary>
public sealed class EnvironmentUnsetFactAttribute : FactAttribute
{
    public EnvironmentUnsetFactAttribute(string why, string variable)
    {
        // Fail loud on a mis-declared attribute rather than silently running (or skipping) everything.
        if (string.IsNullOrWhiteSpace(variable))
            throw new ArgumentException("name the environment variable", nameof(variable));
        Why = why;
        Variable = variable;
        Skip = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))
            ? null
            : $"{variable} is set: {why}";
    }

    /// <summary>Why the test needs the variable unset (shown in the skip reason).</summary>
    public string Why { get; }

    /// <summary>The environment variable whose presence skips the test.</summary>
    public string Variable { get; }
}

/// <summary>A fact that runs only when the repository holds at least one file matching <see cref="Pattern"/> under the
/// repository-relative <see cref="Folder"/> (searched recursively); otherwise it is SKIPPED with the reason, never
/// passed. For a genuinely optional data set.</summary>
public sealed class RepositoryDataFactAttribute : FactAttribute
{
    public RepositoryDataFactAttribute(string why, string folder, string pattern)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("name the folder", nameof(folder));
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("name the file pattern", nameof(pattern));
        Why = why;
        Folder = folder;
        Pattern = pattern;
        string? root = RepositoryRoot();
        if (root is null)
        {
            // Not a skip: a test run that cannot find its own repository is broken, so the test runs and fails there.
            Skip = null;
            return;
        }
        string dir = Path.Combine(root, folder);
        bool any = Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any();
        Skip = any ? null : $"no {pattern} under {folder}: {why}";
    }

    /// <summary>Why the data is optional (shown in the skip reason).</summary>
    public string Why { get; }

    /// <summary>The repository-relative folder searched.</summary>
    public string Folder { get; }

    /// <summary>The file pattern that must match at least once.</summary>
    public string Pattern { get; }

    // The folder holding GridSim.slnx, walking up from the test output folder; null when there is none.
    private static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
