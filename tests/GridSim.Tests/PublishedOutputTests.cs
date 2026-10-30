using GridSim.Core;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Marks a test that checks a PUBLISHED release folder. It runs only when GRIDSIM_PUBLISHED_DIR names that folder (the
/// release workflow sets it after `dotnet publish -p:PublishProfile=&lt;rid&gt;`, on a runner of that OS); otherwise
/// the test is reported as SKIPPED, never as passed, so a normal run cannot be mistaken for a checked release.
/// </summary>
public sealed class PublishedOutputFactAttribute : FactAttribute
{
    /// <summary>The environment variable naming the published folder.</summary>
    public const string Variable = "GRIDSIM_PUBLISHED_DIR";

    public PublishedOutputFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not set: no published release folder to check (the release workflow sets it).";
    }
}

/// <summary>
/// F11 (release register section 6): the published-output test. A release binary is a different artefact from the
/// test build (single-file bundle, self-contained runtime, files laid out by the publish step), so it is checked as
/// shipped: it is self-contained and single-file, data/ and schemas/ and the licence files sit beside it, its version
/// stamp is the same build as this test assembly, and it solves a case.
///
/// Run locally with, for example:
///   dotnet publish src/GridSim.Cli -c Release -p:PublishProfile=win-x64
///   set GRIDSIM_PUBLISHED_DIR=artifacts\publish\gridsim-win-x64   (export ... on Linux / macOS)
///   dotnet test tests/GridSim.Tests -c Release --filter FullyQualifiedName~PublishedOutputTests
/// </summary>
public sealed class PublishedOutputTests
{
    private static string Dir()
    {
        string dir = Path.GetFullPath(Environment.GetEnvironmentVariable(PublishedOutputFactAttribute.Variable)!, CliProcess.Root());
        Assert.True(Directory.Exists(dir), $"{PublishedOutputFactAttribute.Variable} names a folder that does not exist: {dir}");
        return dir;
    }

    private static string Exe() => Path.Combine(Dir(), OperatingSystem.IsWindows() ? "gridsim.exe" : "gridsim");

    [PublishedOutputFact]
    public void Executable_is_single_file_and_self_contained()
    {
        Assert.True(File.Exists(Exe()), $"no gridsim executable in {Dir()}");
        // Single file: the managed assemblies are inside the executable, not beside it.
        foreach (string dll in new[] { "gridsim.dll", "GridSim.Core.dll", "GridSim.Gda.dll", "GridSim.Gpu.dll", "Parquet.dll", "ILGPU.dll" })
            Assert.False(File.Exists(Path.Combine(Dir(), dll)), $"{dll} sits beside the executable: the publish is not single-file");
        // Self-contained: no runtimeconfig asking for an installed shared framework, and no host library left loose.
        Assert.False(File.Exists(Path.Combine(Dir(), "gridsim.runtimeconfig.json")), "a runtimeconfig.json means a framework-dependent publish");
    }

    [PublishedOutputFact]
    public void Data_schemas_licence_and_notices_ship_beside_it()
    {
        string d = Dir();
        foreach (string f in new[]
        {
            "LICENSE.md", "THIRD-PARTY-NOTICES.md",
            Path.Combine("data", "matpower", "lib", "case9.m"),
            Path.Combine("data", "uk", "buses.json"),
        })
            Assert.True(File.Exists(Path.Combine(d, f)), $"{f} missing from the publish");
        Assert.True(Directory.Exists(Path.Combine(d, "schemas", "v2")), "schemas/v2 missing from the publish");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(d, "schemas", "v2"), "*.json", SearchOption.AllDirectories));
    }

    [PublishedOutputFact]
    public void Crossval_sidecar_ships_beside_it()
    {
        // The `crossval` verb runs tools/crossval/crossval.py (and the sidecar_common.py it imports) from beside the
        // executable; a release folder without them could not run the verb at all (bug #51).
        foreach (string f in new[] { "crossval.py", "sidecar_common.py" })
        {
            string shipped = Path.Combine(Dir(), "tools", "crossval", f);
            Assert.True(File.Exists(shipped), $"tools/crossval/{f} missing from the publish");
            Assert.Equal(File.ReadAllBytes(Path.Combine(CliProcess.Root(), "tools", "crossval", f)), File.ReadAllBytes(shipped));
        }
    }

    [PublishedOutputFact]
    public void Version_stamp_is_this_build()
    {
        CliProcess.Result r = CliProcess.Run(Exe(), new[] { "--version" });
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal($"gridsim {BuildInfo.InformationalVersion}", r.StdOut.Trim());
    }

    [PublishedOutputFact]
    public void Published_binary_solves_case9_like_the_build()
    {
        CliProcess.Result published = CliProcess.Run(Exe(), new[] { "case9", "--once" });
        Assert.True(published.ExitCode == 0, published.ToString());
        // The published single-file binary prints exactly the report the normal build prints.
        CliProcess.Result built = CliProcess.RunBuilt("case9", "--once");
        Assert.Equal(built.StdOut, published.StdOut);
    }
}
