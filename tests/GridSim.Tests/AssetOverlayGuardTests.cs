using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.IO.Json.Overlay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Phase 5a (2026-10-20), the asset overlay GUARD (plan key risk 5: the overlay drifting into the solver). The overlay
/// (namespace GridSim.Core.IO.Json.Overlay) is carried beside the model and never used by the solver. This guard keeps
/// it that way by construction, alongside the pin test (V2CaseLoaderTests.Overlay) that proves it by result:
/// <list type="number">
/// <item>Source scan: no GridSim.Core source file outside the overlay folder and the case editor (IO/Json/CaseEditing)
/// names the overlay namespace or any of its types, except the two named carry points below.</item>
/// <item>The carry points are exactly two, each needed and each pinned: the v2 loader (V2CaseLoader.cs, which puts the
/// overlay on its load result) and the v2 document validator (V2DocumentValidator.cs, which runs the checker). In the
/// loader every mention lies AFTER the one call to the model builder (JsonGridLoader.Build) and up to the return that
/// carries the overlay, so the overlay is read only once the model is complete.</item>
/// <item>Reflection over the compiled GridSim.Core: no field, property, method parameter or return type of any type
/// outside those places involves an overlay type.</item>
/// </list>
/// A deliberate back-port of overlay data into the model is a change to THIS guard, made and justified on purpose,
/// never a side effect.
/// </summary>
public sealed class AssetOverlayGuardTests
{
    private const string OverlayNamespace = "GridSim.Core.IO.Json.Overlay";
    private const string CaseEditingNamespace = "GridSim.Core.IO.Json.CaseEditing";

    /// <summary>The only Core files outside the overlay and case-editor folders that may name the overlay, with why.</summary>
    private static readonly Dictionary<string, string> CarryPoints = new(StringComparer.Ordinal)
    {
        ["IO/Json/V2CaseLoader.cs"] = "reads the checked overlay after the model is built and carries it on V2CaseLoadResult.Overlay",
        ["IO/Json/Schema/V2DocumentValidator.cs"] = "runs V2AssetChecker on the overlay files a manifest lists (validation only, no model)",
    };

    private static string CoreDir => Path.Combine(V2.V2TestSupport.RepoRoot(), "src", "GridSim.Core");

    /// <summary>Every public or internal type declared in the overlay namespace (compiler-generated ones aside).</summary>
    private static IReadOnlyList<Type> OverlayTypes()
        => typeof(CaseAssetOverlay).Assembly.GetTypes()
            .Where(t => t.Namespace == OverlayNamespace && !t.Name.Contains('<', StringComparison.Ordinal) && !t.IsNested)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    private static Regex Mentions()
        => new(@"\b(" + string.Join("|", OverlayTypes().Select(t => Regex.Escape(t.Name.Split('`')[0])).Append(Regex.Escape(OverlayNamespace))) + @")\b",
            RegexOptions.CultureInvariant);

    private static string Rel(string full) => Path.GetRelativePath(CoreDir, full).Replace('\\', '/');

    private static IEnumerable<string> CoreSources()
        => Directory.EnumerateFiles(CoreDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Rel(f).StartsWith("obj/", StringComparison.Ordinal) && !Rel(f).StartsWith("bin/", StringComparison.Ordinal));

    [Fact]
    public void The_guard_knows_the_overlay_types_so_it_cannot_pass_by_scanning_for_nothing()
    {
        string[] names = OverlayTypes().Select(t => t.Name).ToArray();
        foreach (string expected in new[] { "AssetOverlayRecord", "AssetOverlayRef", "AssetOverlayTargets", "AssetRefTarget", "CaseAssetOverlay",
                                            "OverlayKindSet", "OverlayKindSpec", "OverlayRefSpec", "V2AssetChecker" })
            Assert.Contains(expected, names);
        // And every overlay type is declared in the overlay folder (so the folder exclusion below is the namespace).
        foreach (string f in CoreSources().Where(f => Rel(f).StartsWith("IO/Json/Overlay/", StringComparison.Ordinal)))
            Assert.Contains("namespace " + OverlayNamespace + ";", File.ReadAllText(f), StringComparison.Ordinal);
    }

    [Fact]
    public void No_core_source_outside_the_overlay_and_case_editor_names_the_overlay_except_the_two_carry_points()
    {
        Regex mentions = Mentions();
        var offenders = new List<string>();
        foreach (string f in CoreSources())
        {
            string rel = Rel(f);
            if (rel.StartsWith("IO/Json/Overlay/", StringComparison.Ordinal) || rel.StartsWith("IO/Json/CaseEditing/", StringComparison.Ordinal)) continue;
            if (CarryPoints.ContainsKey(rel)) continue;
            // Comments count too: a solver file has no reason to talk about the overlay at all.
            foreach ((string line, int i) in File.ReadAllLines(f).Select((l, i) => (l, i)))
                if (mentions.IsMatch(line)) offenders.Add($"{rel}:{i + 1}: {line.Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "the asset overlay is named outside its namespace, the case editor and the two carry points (a back-port into the solver must be a deliberate change to this guard):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Each_carry_point_really_carries_the_overlay_so_the_exceptions_cannot_go_stale()
    {
        Regex mentions = Mentions();
        foreach (string rel in CarryPoints.Keys)
            Assert.True(mentions.IsMatch(File.ReadAllText(Path.Combine(CoreDir, rel))), $"{rel} is a listed carry point but no longer names the overlay; remove it from the guard.");
    }

    [Fact]
    public void The_loader_reads_the_overlay_only_after_the_model_is_built()
    {
        Regex mentions = Mentions();
        string[] lines = File.ReadAllLines(Path.Combine(CoreDir, "IO", "Json", "V2CaseLoader.cs"));
        // Code only: a comment may explain the overlay anywhere.
        string Code(string l) { int c = l.IndexOf("//", StringComparison.Ordinal); return c < 0 ? l : l[..c]; }
        int classStart = Array.FindIndex(lines, l => l.Contains("public static class V2CaseLoader", StringComparison.Ordinal));
        Assert.True(classStart > 0);
        int[] builds = lines.Select((l, i) => (l, i)).Where(x => Code(x.l).Contains("JsonGridLoader.Build(", StringComparison.Ordinal)).Select(x => x.i).ToArray();
        Assert.True(builds.Length == 1, $"expected exactly one model build in V2CaseLoader.cs, found {builds.Length}");
        int[] carries = lines.Select((l, i) => (l, i)).Where(x => Code(x.l).Contains("{ Overlay = overlay }", StringComparison.Ordinal)).Select(x => x.i).ToArray();
        Assert.True(carries.Length == 1, $"expected exactly one return carrying the overlay, found {carries.Length}");
        Assert.True(carries[0] > builds[0]);

        for (int i = classStart; i < lines.Length; i++)
            if (mentions.IsMatch(Code(lines[i])))
                Assert.True(i > builds[0] && i <= carries[0],
                    $"V2CaseLoader.cs:{i + 1} names the overlay outside the window after the model build: {lines[i].Trim()}");
        // Before the class, only the load result's carried property (and the using) may name it.
        for (int i = 0; i < classStart; i++)
            if (mentions.IsMatch(Code(lines[i])))
                Assert.True(lines[i].Contains("using GridSim.Core.IO.Json.Overlay;", StringComparison.Ordinal)
                            || lines[i].Contains("public CaseAssetOverlay? Overlay { get; init; }", StringComparison.Ordinal),
                    $"V2CaseLoader.cs:{i + 1}: {lines[i].Trim()}");
    }

    [Fact]
    public void No_compiled_core_type_outside_the_overlay_carries_an_overlay_type_in_its_shape()
    {
        Assembly core = typeof(CaseAssetOverlay).Assembly;
        var overlay = OverlayTypes().ToHashSet();
        // The only member outside the overlay namespace allowed to have an overlay type: the load result's carried property.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "GridSim.Core.IO.Json.V2CaseLoadResult.Overlay", "GridSim.Core.IO.Json.V2CaseLoadResult.<Overlay>k__BackingField",
            "GridSim.Core.IO.Json.V2CaseLoadResult.get_Overlay", "GridSim.Core.IO.Json.V2CaseLoadResult.set_Overlay",
        };
        bool Involves(Type? t)
        {
            if (t is null) return false;
            if (t.HasElementType) return Involves(t.GetElementType());
            if (overlay.Contains(t.IsGenericType ? t.GetGenericTypeDefinition() : t)) return true;
            return t.IsGenericType && t.GetGenericArguments().Any(Involves);
        }
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (Type t in core.GetTypes())
        {
            string ns = t.Namespace ?? "";
            if (ns == OverlayNamespace || ns == CaseEditingNamespace) continue;
            // The two carry points' own methods (and their compiler-generated helpers) hold locals of overlay types; their
            // FIELDS and the load result's other members are still checked.
            bool carryType = t.FullName!.StartsWith("GridSim.Core.IO.Json.V2CaseLoader", StringComparison.Ordinal)
                             || t.FullName!.StartsWith("GridSim.Core.IO.Json.Schema.V2DocumentValidator", StringComparison.Ordinal);
            foreach (FieldInfo f in t.GetFields(all))
                if (Involves(f.FieldType) && !allowed.Contains(t.FullName + "." + f.Name) && !(carryType && t.Name.Contains('<', StringComparison.Ordinal)))
                    offenders.Add($"field {t.FullName}.{f.Name}");
            foreach (PropertyInfo p in t.GetProperties(all))
                if (Involves(p.PropertyType) && !allowed.Contains(t.FullName + "." + p.Name))
                    offenders.Add($"property {t.FullName}.{p.Name}");
            if (carryType) continue;
            foreach (MethodInfo m in t.GetMethods(all))
                if ((Involves(m.ReturnType) || m.GetParameters().Any(x => Involves(x.ParameterType))) && !allowed.Contains(t.FullName + "." + m.Name))
                    offenders.Add($"method {t.FullName}.{m.Name}");
        }
        Assert.True(offenders.Count == 0, "overlay types in the shape of non-overlay Core types:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
