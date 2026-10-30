using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// UI programme 0.4: the Avalonia app is built on Core and GridSim.Ui only. Neither GridSim.App nor
/// GridSim.App.Desktop may reference the WPF app, the web app, the GDA model or the GPU backend, directly or through
/// another GridSim project or assembly they reference. Checked twice: in the compiled assemblies' metadata (what the
/// code really uses) and in the build's project graph (what the project files reference, used or not).
/// </summary>
public sealed class AppProjectReferenceTests
{
    private static readonly string[] Forbidden = { "GridSim.Wpf", "GridSim.Web", "GridSim.Gda", "GridSim.Gpu" };

    [Theory]
    [InlineData("GridSim.App")]
    [InlineData("GridSim.App.Desktop")]
    public void The_app_assemblies_do_not_reference_Wpf_Web_Gda_or_Gpu(string assemblyName)
    {
        Assembly root = Assembly.Load(new AssemblyName(assemblyName));
        Assert.Equal(assemblyName, root.GetName().Name);

        // Walk every GridSim assembly reachable from this one in metadata, so a forbidden reference cannot hide one
        // step away.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Assembly a = queue.Dequeue();
            if (!seen.Add(a.GetName().Name!)) continue;
            foreach (AssemblyName r in a.GetReferencedAssemblies())
            {
                foreach (string bad in Forbidden)
                    Assert.False(string.Equals(r.Name, bad, StringComparison.OrdinalIgnoreCase),
                        $"{a.GetName().Name} references {r.Name}; the Avalonia app must not depend on it");
                if (r.Name!.StartsWith("GridSim.", StringComparison.Ordinal))
                    queue.Enqueue(Assembly.Load(r));
            }
        }
        // The walk really went somewhere (a broken walk would pass vacuously): the window shows Core's BuildInfo.
        Assert.Contains("GridSim.Core", seen);
        if (assemblyName == "GridSim.App.Desktop") Assert.Contains("GridSim.App", seen);
    }

    [Theory]
    [InlineData("GridSim.App")]
    [InlineData("GridSim.App.Desktop")]
    public void The_app_projects_do_not_reference_Wpf_Web_Gda_or_Gpu_in_the_project_graph(string project)
    {
        // The test run's .deps.json holds the resolved graph: "Name/Version" -> { dependencies: { Name: Version } }.
        string depsPath = Path.Combine(AppContext.BaseDirectory, "GridSim.App.Tests.deps.json");
        Assert.True(File.Exists(depsPath), $"dependency graph not found: {depsPath}");
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        JsonElement target = deps.RootElement.GetProperty("targets").EnumerateObject().Single().Value;

        string start = target.EnumerateObject().Select(p => p.Name)
            .Single(n => n.StartsWith(project + "/", StringComparison.Ordinal));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            string name = key.Split('/')[0];
            if (!seen.Add(name)) continue;
            foreach (string bad in Forbidden)
                Assert.False(string.Equals(name, bad, StringComparison.OrdinalIgnoreCase),
                    $"{project} reaches {name} in the project graph; the Avalonia app must not depend on it");
            if (target.TryGetProperty(key, out JsonElement node) && node.TryGetProperty("dependencies", out JsonElement d))
                foreach (JsonProperty dep in d.EnumerateObject())
                    queue.Enqueue($"{dep.Name}/{dep.Value.GetString()}");
        }
        // The graph really holds the expected chain: App -> Ui -> Core, and Desktop -> App.
        Assert.Contains("GridSim.Ui", seen);
        Assert.Contains("GridSim.Core", seen);
        Assert.Contains("Avalonia", seen);
        if (project == "GridSim.App.Desktop") Assert.Contains("GridSim.App", seen);
    }
}
