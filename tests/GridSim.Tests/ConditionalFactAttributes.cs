using Xunit;

namespace GridSim.Tests;

// GR-404 / GR-410 / GR-877 (October 2026): xUnit 2 has no runtime skip (Assert.Skip is xUnit 3), so a test that
// returned early on a missing opt-in data set or the wrong operating system was reported as PASSED although it checked
// nothing. These attributes decide at discovery time instead, so the same test is reported as SKIPPED, with the reason,
// and a green run can no longer be read as "this was checked". They follow the pattern of PublishedOutputFactAttribute
// and LargeCaseFactAttribute.

/// <summary>Helpers shared by the conditional attributes below.</summary>
internal static class ConditionalSkip
{
    /// <summary>The skip reason when neither named environment variable is set (non-blank), else null.</summary>
    public static string? UnlessAnySet(string why, string variable, string? orVariable)
    {
        // Fail loud on a mis-declared attribute rather than silently running (or skipping) everything.
        if (string.IsNullOrWhiteSpace(variable))
            throw new ArgumentException("name the environment variable", nameof(variable));
        if (IsSet(variable) || (orVariable is not null && IsSet(orVariable))) return null;
        string names = orVariable is null ? variable : $"{variable} or {orVariable}";
        return $"{names} is not set: {why}";
    }

    private static bool IsSet(string variable) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

    /// <summary>The skip reason when this process is not running on Windows, else null.</summary>
    public static string? UnlessWindows(string why) =>
        OperatingSystem.IsWindows() ? null : $"Windows only: {why}";

    /// <summary>GR-839: the skip reason when this process is running on Windows (no Unix file modes), else null.</summary>
    public static string? UnlessUnix(string why) =>
        OperatingSystem.IsWindows() ? $"Linux and macOS only: {why}" : null;
}

/// <summary>A fact that runs only when the named environment variable (or the second one) is set; otherwise it is SKIPPED with the
/// reason, never passed.</summary>
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string why, string variable, string? orVariable = null)
    {
        Why = why;
        Variable = variable;
        OrVariable = orVariable;
        Skip = ConditionalSkip.UnlessAnySet(why, variable, orVariable);
    }

    /// <summary>What the test needs the variable for (shown in the skip reason).</summary>
    public string Why { get; }

    /// <summary>The environment variable that turns the test on.</summary>
    public string Variable { get; }

    /// <summary>A second variable that also turns it on, or null.</summary>
    public string? OrVariable { get; }
}

/// <summary>A theory that runs only when the named environment variable (or the second one) is set; otherwise every row is SKIPPED
/// with the reason, never passed.</summary>
public sealed class EnvironmentTheoryAttribute : TheoryAttribute
{
    public EnvironmentTheoryAttribute(string why, string variable, string? orVariable = null)
    {
        Why = why;
        Variable = variable;
        OrVariable = orVariable;
        Skip = ConditionalSkip.UnlessAnySet(why, variable, orVariable);
    }

    /// <summary>What the test needs the variable for (shown in the skip reason).</summary>
    public string Why { get; }

    /// <summary>The environment variable that turns the test on.</summary>
    public string Variable { get; }

    /// <summary>A second variable that also turns it on, or null.</summary>
    public string? OrVariable { get; }
}

/// <summary>A fact that runs only on Windows; elsewhere it is SKIPPED with the reason, never passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string why)
    {
        Why = why;
        Skip = ConditionalSkip.UnlessWindows(why);
    }

    /// <summary>Why the test is Windows only (shown in the skip reason).</summary>
    public string Why { get; }
}

/// <summary>GR-839: a fact that runs only on Linux and macOS; on Windows it is SKIPPED with the reason, never passed.
/// Mark the test method <c>[UnsupportedOSPlatform("windows")]</c> too when it calls a Unix-only API.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string why)
    {
        Why = why;
        Skip = ConditionalSkip.UnlessUnix(why);
    }

    /// <summary>Why the test is Unix only (shown in the skip reason).</summary>
    public string Why { get; }
}
