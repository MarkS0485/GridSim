using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Ui.Hosting;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme Phase 0.2: GridSim.Ui is the platform-neutral half of the desktop app, shared by the WPF app now and
/// by the planned Avalonia app (desktop and mobile) later. It must never take a dependency on a user-interface
/// framework: not WPF (System.Windows, PresentationFramework, PresentationCore, WindowsBase, System.Xaml), not Windows
/// Forms, not Avalonia. These tests check that three ways, so no single loophole lets one in: the built assembly's
/// references, the project file, and the source text.
/// </summary>
public sealed class UiProjectNeutralityTests
{
    /// <summary>Assembly-name prefixes of user-interface frameworks GridSim.Ui must not reference.</summary>
    private static readonly string[] ForbiddenAssemblies =
    {
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "System.Windows", "UIAutomation",
        "ReachFramework", "Avalonia", "SkiaSharp", "HarfBuzzSharp", "Microsoft.WindowsDesktop",
    };

    private static string UiDir() => Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), "src", "GridSim.Ui");

    [Fact]
    public void The_built_assembly_references_no_user_interface_framework()
    {
        Assembly ui = typeof(SimHost).Assembly;
        Assert.Equal("GridSim.Ui", ui.GetName().Name);
        AssemblyName[] refs = ui.GetReferencedAssemblies();
        Assert.Contains(refs, r => r.Name == "GridSim.Core");   // the one project it references
        foreach (AssemblyName r in refs)
            foreach (string bad in ForbiddenAssemblies)
                Assert.False(r.Name!.StartsWith(bad, StringComparison.OrdinalIgnoreCase),
                    $"GridSim.Ui references {r.Name}, a user-interface framework assembly");
        // Only Core and the base class library: anything else is a new dependency to justify here first.
        foreach (AssemblyName r in refs)
            Assert.True(r.Name == "GridSim.Core" || r.Name!.StartsWith("System", StringComparison.Ordinal) || r.Name == "netstandard",
                $"GridSim.Ui references {r.Name}: only GridSim.Core and the base class library are expected");
    }

    [Fact]
    public void The_project_targets_plain_net10_and_references_Core_only()
    {
        string csproj = File.ReadAllText(Path.Combine(UiDir(), "GridSim.Ui.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-", csproj, StringComparison.Ordinal);          // no -windows / -android / -ios head
        Assert.DoesNotContain("<UseWPF>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<UseWindowsForms>", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameworkReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableWindowsTargeting", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);  // no third-party package
        Assert.DoesNotContain("<Version>", csproj, StringComparison.Ordinal);          // the one version (F02)
        MatchCollection refs = Regex.Matches(csproj, @"<ProjectReference\s+Include=""([^""]+)""");
        Assert.Single(refs);
        Assert.Equal(@"..\GridSim.Core\GridSim.Core.csproj", refs[0].Groups[1].Value);
        Assert.Contains("<Project Path=\"src/GridSim.Ui/GridSim.Ui.csproj\" />",
            PresentationConstantsAntiDriftTests.Source("GridSim.slnx"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_source_file_names_a_user_interface_framework_in_code()
    {
        // Comments may say what a type replaced (for example "the old System.Windows.Point"); code may not use one.
        string[] forbidden = { "System.Windows", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "Avalonia" };
        var offenders = new List<string>();
        string[] files = Directory.EnumerateFiles(UiDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = CodePart(lines[i]);
                foreach (string bad in forbidden)
                    if (code.Contains(bad, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "GridSim.Ui code names a user-interface framework:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_code_part_of_a_line_drops_only_comments()
    {
        // The scanner above is only as strong as this split, so it is pinned too.
        Assert.Equal("using System.Windows;", CodePart("using System.Windows;"));
        Assert.Equal("", CodePart("/// the old System.Windows.Point"));
        Assert.Equal("", CodePart("   // System.Windows"));
        Assert.Equal("var p = new Point(); ", CodePart("var p = new Point(); // System.Windows"));
        Assert.Equal("string s = \"a//b\"; ", CodePart("string s = \"a//b\"; // c"));
    }

    /// <summary>A source line without its <c>//</c> comment (a <c>//</c> inside a string literal is kept).</summary>
    private static string CodePart(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i].TrimStart() is { Length: 0 } ? "" : line[..i];
        }
        return line;
    }
}
