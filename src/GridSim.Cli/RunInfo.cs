using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GridSim.Cli;

/// <summary>
/// Machine + code-version metadata stamped at the top of every benchmark/sweep report and written
/// as a <c>run-metadata.json</c> sidecar. This is what makes runs comparable over time: the git
/// commit (captured at build time), the runtime, and the host it ran on. When we start optimizing
/// the solver, diffing two reports' headers tells us what changed and where it ran.
/// </summary>
public static class RunInfo
{
    /// <summary>The active solver backend ("cpu" / "gpu (...)"), set by the CLI at startup.</summary>
    public static string Backend { get; set; } = "cpu";

    /// <summary>Indented JSON options, shared so no call builds its own (CA1869).</summary>
    internal static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = true };

    /// <summary>Indented JSON options shared by the CLI's small report writers (one cached instance, CA1869).</summary>
    internal static JsonSerializerOptions IndentedJson => MetadataJson;

    /// <summary>Short git commit captured at build time (see the MSBuild target in the .csproj).</summary>
    public static string GitCommit => Meta("GitCommit") ?? "unknown";
    public static string BuildConfig => Meta("BuildConfig") ?? "unknown";
    public static string CoreVersion =>
        typeof(GridSim.Core.Model.GridModel).Assembly.GetName().Version?.ToString() ?? "?";

    private static string? Meta(string key) => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>
    /// The processor's model name ("AMD Ryzen 7 5800H with Radeon Graphics", "Apple M2", ...), for the report header and
    /// the run-metadata <c>cpu</c> field. D-DOC-227 (October 2026, docs-session finding): it was read on Linux only
    /// (<c>/proc/cpuinfo</c>), so every Windows and macOS report said just "X64" or "Arm64", and two benchmark reports from
    /// different machines could not be told apart by their CPU. Now each OS is asked in its own way:
    /// <list type="bullet">
    /// <item>Linux: the first <c>model name</c> line of <c>/proc/cpuinfo</c> (as before);</item>
    /// <item>Windows: the registry value <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString</c>,
    /// the name Windows itself shows (read only; no process is started);</item>
    /// <item>macOS: <c>sysctl -n machdep.cpu.brand_string</c> (Intel and Apple silicon both answer).</item>
    /// </list>
    /// Anything that fails (no permission, a missing value, a sandbox) falls back to the process architecture, as before,
    /// so the header never throws. Informational only: nothing computed reads it.
    /// </summary>
    public static string CpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                        return line.Split(':', 2)[1].Trim();
            }
            else if (OperatingSystem.IsWindows())
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is not null)
                {
                    // A bounded wait: a header must never hang a run. GR-686: the output is read asynchronously so the
                    // 2 s limit really bounds it (a blocking ReadToEnd waited for the child to close stdout first), and
                    // a child still running at the limit is killed.
                    Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (p.WaitForExit(2000) && read.Wait(2000) && p.ExitCode == 0)
                    {
                        string text = read.Result.Trim();
                        if (text.Length > 0) return text;
                    }
                    else
                    {
                        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    }
                }
            }
        }
        catch { /* fall through to the architecture, as before */ }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static double RamGb()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
                foreach (string line in File.ReadLines("/proc/meminfo"))
                    if (line.StartsWith("MemTotal", StringComparison.Ordinal))
                        // GR-680: MemTotal is in kB (KiB); as bytes / 1e9, the decimal GB the other branch reports and the
                        // header prints (it was KiB / 1024 / 1024, GiB under a "GB" label).
                        return double.Parse(new string(line.Where(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture) * 1024.0 / 1e9;
        }
        catch { /* fall through */ }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1e9;
    }

    /// <summary>The multi-line header block emitted at the top of a report.</summary>
    public static IReadOnlyList<string> HeaderLines(string runDescription)
    {
        DateTime now = DateTime.UtcNow;
        return new[]
        {
            $"GridSim  -  {now:yyyy-MM-dd HH:mm:ss} UTC",
            $"code:    git {GitCommit}  |  GridSim.Core v{CoreVersion}  |  build {BuildConfig}",
            $"runtime: {RuntimeInformation.FrameworkDescription}  {RuntimeInformation.ProcessArchitecture}  " +
                $"GC={(GCSettings.IsServerGC ? "Server" : "Workstation")}",
            $"machine: {Environment.MachineName}  {RuntimeInformation.OSDescription}",
            $"cpu:     {CpuModel()}  -  {Environment.ProcessorCount} logical cores  -  {RamGb():F0} GB RAM",
            $"simd:    {GridSim.Core.Numerics.SimdCapabilities.Tier}  ({GridSim.Core.Numerics.SimdCapabilities.DoubleLanes} doubles/step)  FMA={GridSim.Core.Numerics.SimdCapabilities.FmaAvailable}",
            $"backend: {Backend}",
            $"run:     {runDescription}",
        };
    }

    /// <summary>
    /// The metadata sidecar of a REPORT-FILE verb (bench, nminus1-sweep, stress, chain): the report's own name with the
    /// extension <c>.run-metadata.json</c>, beside it (<c>results/bench.md</c> -> <c>results/bench.run-metadata.json</c>).
    ///
    /// <para>D-DOC-057 (October 2026, docs-session finding): these verbs used to write a fixed <c>run-metadata.json</c> into
    /// the report's folder. Their reports are named by <c>--out</c> and several share one folder (the default
    /// <c>results/</c>, or tools/run-regime.sh, which writes bench.md, nminus1-*.md, stress.md and chain-*.md into one
    /// folder), so every run replaced the metadata of the run before it: a folder of reports held the metadata of the last
    /// run only, and a bench report's sidecar could describe a stress run. Naming the sidecar after its report pairs the
    /// two for good. The verbs that write a whole run DIRECTORY (transient, replay-event, estimate, ...) keep the plain
    /// <c>run-metadata.json</c> inside it: that folder belongs to the one run.</para>
    /// </summary>
    /// <param name="reportPath">The report file (as given or defaulted; relative paths are taken from the current
    /// directory, as the report itself is).</param>
    public static string MetadataPathFor(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        return Path.ChangeExtension(Path.GetFullPath(reportPath), ".run-metadata.json");
    }

    /// <summary>Write the <c>run-metadata.json</c> sidecar next to a report.</summary>
    public static void WriteJson(string path, string runDescription)
    {
        var meta = new
        {
            timestampUtc = DateTime.UtcNow.ToString("O"),
            backend = Backend,
            gitCommit = GitCommit,
            buildConfig = BuildConfig,
            coreVersion = CoreVersion,
            runtime = RuntimeInformation.FrameworkDescription,
            arch = RuntimeInformation.ProcessArchitecture.ToString(),
            serverGc = GCSettings.IsServerGC,
            simdTier = GridSim.Core.Numerics.SimdCapabilities.Tier.ToString(),
            simdLanes = GridSim.Core.Numerics.SimdCapabilities.DoubleLanes,
            simdFma = GridSim.Core.Numerics.SimdCapabilities.FmaAvailable,
            machineName = Environment.MachineName,
            os = RuntimeInformation.OSDescription,
            cpu = CpuModel(),
            logicalCores = Environment.ProcessorCount,
            ramGb = Math.Round(RamGb(), 1),
            run = runDescription,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(meta, MetadataJson));
    }

    /// <summary>
    /// Write the <c>run-metadata.json</c> sidecar with the run's own parameters as one more object, <c>parameters</c>,
    /// after the fields <see cref="WriteJson(string, string)"/> writes (which it leaves exactly as they were). D-DOC-121
    /// (docs-session finding): the EMT sidecar carried only the one-line description, so a run's probes, events and
    /// model switches (<c>--lines</c>, <c>--no-cda</c>, <c>--no-loads</c>) could not be recovered from it.
    /// </summary>
    public static void WriteJson(string path, string runDescription, object parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        WriteJson(path, runDescription);
        // Re-read the sidecar just written and append the parameters, so the shared fields are produced by ONE
        // code path and cannot drift between the plain and the extended sidecar.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["parameters"] = JsonSerializer.SerializeToNode(parameters, MetadataJson);
        File.WriteAllText(path, node.ToJsonString(MetadataJson));
    }
}
