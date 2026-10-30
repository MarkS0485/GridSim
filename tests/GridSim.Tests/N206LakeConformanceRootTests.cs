using System;
using System.IO;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N206 (R1 follow-up): <c>tools/crossval/lake_conformance.py</c> must read the lake root variables with the meaning the
/// CLI gives them (<see cref="GridSim.Gda.GdaRootEnvironment"/>): both <c>GDA_DATA_ROOT</c> and <c>GDA_ROOT</c> name the
/// DataSources folder itself, so the lake's Derived directory is <c>&lt;root&gt;/Derived</c>. Before N206 the script read
/// <c>GDA_ROOT</c> as the folder ABOVE DataSources and appended <c>DataSources/Derived</c>, so one setting pointed the
/// CLI and the script at different folders. These are source pins (the script needs NumPy, pyarrow and a lake to run).
/// </summary>
public sealed class N206LakeConformanceRootTests
{
    private static string Script() => File.ReadAllText(Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(),
        "tools", "crossval", "lake_conformance.py"));

    [Fact]
    public void The_derived_directory_sits_directly_under_either_variable()
    {
        string s = Script();
        // The old "folder above DataSources" join is gone ...
        Assert.DoesNotContain("os.path.join(\"DataSources\", \"Derived\")", s, StringComparison.Ordinal);
        Assert.DoesNotContain("$GDA_ROOT/DataSources/Derived", s, StringComparison.Ordinal);
        // ... and both variables take the same subdirectory.
        Assert.Contains("LAKE_DERIVED_SUBDIR = \"Derived\"", s, StringComparison.Ordinal);
        Assert.Contains("os.path.join(data_root, LAKE_DERIVED_SUBDIR), \"GDA_DATA_ROOT\"", s, StringComparison.Ordinal);
        Assert.Contains("os.path.join(root, LAKE_DERIVED_SUBDIR), \"GDA_ROOT\"", s, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_but_missing_GDA_ROOT_is_refused_naming_the_variable_as_the_cli_does()
    {
        string s = Script();
        Assert.Contains("GDA_ROOT is set but does not name an existing folder (value not printed)", s, StringComparison.Ordinal);
        Assert.Contains("GDA_DATA_ROOT is set but does not name an existing folder (value not printed)", s, StringComparison.Ordinal);
        // A Derived directory missing under an environment root names the variable, never the path built from its value.
        Assert.Contains("no Derived directory under the folder {variable} names (value not printed)", s, StringComparison.Ordinal);
    }
}
