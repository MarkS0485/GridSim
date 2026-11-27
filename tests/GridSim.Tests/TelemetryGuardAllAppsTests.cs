using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Telemetry is provably off in every GridSim app, not only the Avalonia desktop app and the web app
/// (<see cref="TelemetryGuardTests"/>): the command line tool (gridsim) and the Windows dashboard (GridSim.Wpf) run the
/// same guard first, with the same exit code, and their assemblies carry the build marker. The guard also refuses the
/// variables and agent assemblies by which an APM product injects itself into a process (a profiler, a startup hook, a
/// hosting-startup assembly).
///
/// <para>The decision is a pure function, so every trigger is driven here from made-up facts. Only the CLI tests below
/// run the real guard, in a child process whose environment the test sets; the test host's own environment is never
/// changed.</para>
/// </summary>
public sealed class TelemetryGuardAllAppsTests
{
    private static string Root() => CliProcess.Root();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    // The GridSim assemblies of the CLI and the WPF app that TelemetryGuardTests.AppProjects does not already cover:
    // (project folder, assembly name, target framework).
    private static readonly (string Project, string Assembly, string Tfm)[] MoreAppAssemblies =
    [
        ("GridSim.Cli", "gridsim", "net10.0"),
        ("GridSim.Gda", "GridSim.Gda", "net10.0"),
        ("GridSim.Gpu", "GridSim.Gpu", "net10.0"),
        ("GridSim.Wpf", "GridSim.Wpf", "net10.0-windows"),
    ];

    private static TelemetryGuardFacts Clean(IReadOnlyList<string>? loaded = null, IEnumerable<KeyValuePair<string, string?>>? env = null) =>
        new([new MarkerFact("gridsim", ["true"]), new MarkerFact("GridSim.Core", ["true"])],
            loaded ?? ["System.Private.CoreLib", "GridSim.Core"],
            env ?? [new("PATH", "/usr/bin")],
            null,
            []);

    private static IEnumerable<KeyValuePair<string, string?>> Env(string name, string? value) =>
        [new("PATH", "/usr/bin"), new(name, value)];

    // ---- Entry points ---------------------------------------------------------------------------------------------

    // The code of a file without blank lines, comment lines and using directives, joined by newlines.
    private static string CodeLines(string src) => string.Join('\n', src.Split('\n').Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("using ", StringComparison.Ordinal)));

    [Fact]
    public void The_cli_runs_the_guard_as_its_first_statement()
    {
        string first = CodeLines(Read("src/GridSim.Cli/Program.cs")).Split(';')[0].Replace("\n", " ", StringComparison.Ordinal);
        Assert.Equal(
            "GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup(\"gridsim\", [\"gridsim\", \"GridSim.Core\", \"GridSim.Gda\", \"GridSim.Gpu\"], foreignEntryAllowed: false)",
            first);
    }

    [Fact]
    public void The_cli_checks_every_gridsim_assembly_it_references()
    {
        // The CLI's project references, each by assembly name, are exactly the app assemblies after the entry assembly.
        string csproj = Read("src/GridSim.Cli/GridSim.Cli.csproj");
        string[] referenced = Regex.Matches(csproj, @"<ProjectReference\s+Include=""\.\.\\(GridSim\.[A-Za-z]+)\\")
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "GridSim.Core", "GridSim.Gda", "GridSim.Gpu" }, referenced);
        Assert.Contains("<AssemblyName>gridsim</AssemblyName>", csproj, StringComparison.Ordinal);
        // GridSim.Gda and GridSim.Gpu reference only GridSim.Core, so the closure is complete.
        foreach (string p in new[] { "GridSim.Gda", "GridSim.Gpu" })
        {
            string[] refs = Regex.Matches(Read($"src/{p}/{p}.csproj"), @"<ProjectReference\s+Include=""([^""]+)""")
                .Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[] { @"..\GridSim.Core\GridSim.Core.csproj" }, refs);
        }
    }

    [Fact]
    public void The_wpf_app_runs_the_guard_before_wpf_starts()
    {
        string csproj = Read("src/GridSim.Wpf/GridSim.Wpf.csproj");
        Assert.Contains("<StartupObject>GridSim.Wpf.Program</StartupObject>", csproj, StringComparison.Ordinal);

        string src = Read("src/GridSim.Wpf/Program.cs");
        int main = src.IndexOf("public static void Main()", StringComparison.Ordinal);
        Assert.True(main > 0, "GridSim.Wpf.Program has no Main()");
        Assert.Contains("[STAThread]\n    public static void Main()", src, StringComparison.Ordinal);
        string body = src[(src.IndexOf('{', main) + 1)..];
        string[] statements = CodeLines(body).Split(';');
        Assert.Equal("GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup(\"GridSim.Wpf\", AppAssemblies, foreignEntryAllowed: false)", statements[0]);
        Assert.Equal("Start()", statements[1].Trim());
        Assert.Contains("internal static readonly string[] AppAssemblies = [\"GridSim.Wpf\", \"GridSim.Ui\", \"GridSim.Core\"];", src, StringComparison.Ordinal);
        // WPF is reached only through a method the JIT compiles after the guard has run.
        Assert.Contains("[MethodImpl(MethodImplOptions.NoInlining)]\n    private static void Start() => App.Main();", src, StringComparison.Ordinal);

        // The WPF app's GridSim references are exactly the assemblies it checks.
        string[] refs = Regex.Matches(csproj, @"<ProjectReference\s+Include=""\.\.\\(GridSim\.[A-Za-z]+)\\")
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "GridSim.Core", "GridSim.Ui" }, refs);
        string ui = Read("src/GridSim.Ui/GridSim.Ui.csproj");
        Assert.Equal(new[] { @"..\GridSim.Core\GridSim.Core.csproj" },
            Regex.Matches(ui, @"<ProjectReference\s+Include=""([^""]+)""").Select(m => m.Groups[1].Value).ToArray());
    }

    [Fact]
    public void Every_app_entry_point_calls_the_one_guard()
    {
        // One guard, one exit code: no entry point calls Environment.Exit with its own code for a telemetry refusal.
        Assert.Equal(78, TelemetryGuard.ExitCode);
        foreach (string entry in new[] { "src/GridSim.Cli/Program.cs", "src/GridSim.Wpf/Program.cs", "src/GridSim.App.Desktop/Program.cs", "src/GridSim.Web/Program.cs" })
            Assert.Contains("TelemetryGuard.EnforceAtStartup(", Read(entry), StringComparison.Ordinal);
    }

    // ---- The build marker and the package graphs --------------------------------------------------------------------

    private static string Configuration() =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    [Fact]
    public void The_cli_and_wpf_assemblies_carry_the_telemetry_marker()
    {
        string config = Configuration();
        foreach ((string project, string assembly, string tfm) in MoreAppAssemblies)
        {
            string dll = Path.Combine(Root(), "src", project, "bin", config, tfm, assembly + ".dll");
            Assert.True(File.Exists(dll), $"{dll} is missing; build the solution first");
            Assert.Null(TelemetryGuard.CheckMarker(assembly, MarkerValuesInFile(dll)));
        }
    }

    [Fact]
    public void No_cli_or_wpf_project_or_lock_file_holds_a_telemetry_package()
    {
        foreach ((string project, _, _) in MoreAppAssemblies)
        {
            string csproj = Read(Path.Combine("src", project, project + ".csproj"));
            foreach (Match m in Regex.Matches(csproj, @"<PackageReference\s+Include=""([^""]+)"""))
                Assert.Null(TelemetryGuard.CheckLoadedAssembly(m.Groups[1].Value));

            using JsonDocument lockFile = JsonDocument.Parse(Read(Path.Combine("src", project, "packages.lock.json")));
            foreach (JsonProperty target in lockFile.RootElement.GetProperty("dependencies").EnumerateObject())
                foreach (JsonProperty package in target.Value.EnumerateObject())
                    Assert.Null(TelemetryGuard.CheckLoadedAssembly(package.Name));
        }
    }

    // The TelemetryOff values on a compiled assembly, read from its metadata without loading it.
    private static List<string?> MarkerValuesInFile(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var values = new List<string?>();
        foreach (CustomAttributeHandle h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute ca = md.GetCustomAttribute(h);
            if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
            MemberReference ctor = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            if (md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "AssemblyMetadataAttribute") continue;
            CustomAttributeValue<string> v = ca.DecodeValue(new StringTypes());
            if (v.FixedArguments.Length == 2 && (string?)v.FixedArguments[0].Value == TelemetryGuard.MarkerKey)
                values.Add((string?)v.FixedArguments[1].Value);
        }
        return values;
    }

    // ---- The CLI, run for real --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SENTRY_DSN", "https://k@o.example/1", "environment variable SENTRY_DSN is set (it switches on a telemetry exporter)")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9", "environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set (it configures a telemetry exporter)")]
    [InlineData("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "Microsoft.AspNetCore.ApplicationInsights.HostingStartup", "environment variable ASPNETCORE_HOSTINGSTARTUPASSEMBLIES is set (it loads code into the process)")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "0", "environment variable AVALONIA_TELEMETRY_OPTOUT is '0', not 1 or true")]
    public void The_cli_exits_78_with_one_line_before_doing_anything(string name, string value, string reason)
    {
        // The profiler switch is removed, so the runtime itself never tries to load the named profiler: only the guard
        // reacts to the path. --version is the cheapest verb and is answered after the guard.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value, ["CORECLR_ENABLE_PROFILING"] = null };
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "--version");
        Assert.True(r.ExitCode == TelemetryGuard.ExitCode, r.ToString());
        Assert.Equal(TelemetryGuard.ExitLine("gridsim", reason), r.StdErr.TrimEnd('\r', '\n'));
        Assert.True(r.StdOut.Length == 0, r.ToString());
    }

    /// <summary>SEC-08 (Mark's ruling, 2.1.159): a profiler path with profiling off loads no profiler, so GridSim starts. This
    /// case was one of the exit-78 rows above (CORECLR_PROFILER_PATH with the switch unset); the ruling makes it a start.</summary>
    [Fact]
    public void The_cli_starts_with_a_profiler_path_set_and_profiling_off()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CORECLR_PROFILER_PATH_64"] = "no-such-profiler.dll", ["CORECLR_ENABLE_PROFILING"] = "0" };
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(env, "--version");
        Assert.True(r.ExitCode == 0, r.ToString());
    }

    [Fact]
    public void The_cli_starts_normally_in_a_clean_environment()
    {
        CliProcess.Result r = CliProcess.RunBuilt("--version");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.StartsWith("gridsim ", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("telemetry guard", r.StdErr, StringComparison.Ordinal);
    }

    // ---- Agent injection: environment ---------------------------------------------------------------------------------

    [Fact]
    public void The_injection_lists_are_the_documented_ones()
    {
        Assert.Equal(new[] { "DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "DOTNET_HOSTINGSTARTUPASSEMBLIES", "DOTNET_ADDITIONAL_DEPS" },
            TelemetryGuard.DeniedInjectionVariables);
        // SEC-08 (2.1.159): the DOTNET_ spellings the runtime also reads are listed too.
        Assert.Equal(new[] { "CORECLR_NOTIFICATION_PROFILERS", "CORECLR_PROFILER", "DOTNET_NOTIFICATION_PROFILERS", "DOTNET_PROFILER" }, TelemetryGuard.DeniedProfilerVariablePrefixes);
        Assert.Equal(new[] { "CORECLR_ENABLE_NOTIFICATION_PROFILERS", "CORECLR_ENABLE_PROFILING", "DOTNET_ENABLE_NOTIFICATION_PROFILERS", "DOTNET_ENABLE_PROFILING" }, TelemetryGuard.ProfilerSwitches);
    }

    [Fact]
    public void Every_injection_variable_exits_whatever_its_value()
    {
        foreach (string v in TelemetryGuard.DeniedInjectionVariables)
            foreach (string? value in new[] { "x", "", "0", "none", null })
            {
                Assert.Equal($"environment variable {v} is set (it loads code into the process)", TelemetryGuard.CheckEnvironment(Env(v, value)));
                Assert.Equal($"environment variable {v.ToLowerInvariant()} is set (it loads code into the process)",
                    TelemetryGuard.CheckEnvironment(Env(v.ToLowerInvariant(), value)));
            }
    }

    [Theory]
    [InlineData("CORECLR_PROFILER", "{846F5F1C-F9AE-4B07-969E-05C26BC060D8}")]
    [InlineData("CORECLR_PROFILER_PATH", "/opt/datadog/Datadog.Trace.ClrProfiler.Native.so")]
    [InlineData("CORECLR_PROFILER_PATH_32", "C:\\agent\\x86\\profiler.dll")]
    [InlineData("CORECLR_PROFILER_PATH_64", "C:\\agent\\x64\\profiler.dll")]
    [InlineData("CORECLR_PROFILER_PATH_ARM64", "/agent/profiler.so")]
    [InlineData("coreclr_profiler", "")]
    [InlineData("CORECLR_NOTIFICATION_PROFILERS", "/a.so={00000000-0000-0000-0000-000000000000}")]
    public void A_profiler_selection_variable_exits_only_while_profiling_is_on(string name, string value)
    {
        // SEC-08 (Mark's ruling, 2.1.159): with no profiler switch on the runtime loads no profiler, so the variable alone
        // no longer stops GridSim (this pin expected exit 78 for it); with a switch on, the run still stops.
        Assert.Null(TelemetryGuard.Decide(Clean(env: Env(name, value))));
        Assert.NotNull(TelemetryGuard.CheckEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value, ["CORECLR_ENABLE_PROFILING"] = "1" }));
    }

    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING", "1")]
    [InlineData("CORECLR_ENABLE_PROFILING", "0x1")]
    [InlineData("CORECLR_ENABLE_PROFILING", " 1 ")]
    [InlineData("coreclr_enable_profiling", "1")]
    [InlineData("CORECLR_ENABLE_NOTIFICATION_PROFILERS", "1")]
    [InlineData("DOTNET_ENABLE_PROFILING", "1")]
    [InlineData("CORECLR_ENABLE_PROFILING", "2")]      // R3-01: the runtime loads a profiler for any non-zero DWORD
    [InlineData("CORECLR_ENABLE_PROFILING", "10")]
    [InlineData("CORECLR_ENABLE_PROFILING", "ff")]
    [InlineData("CORECLR_ENABLE_PROFILING", "0x2")]
    [InlineData("CORECLR_ENABLE_PROFILING", "-1")]
    [InlineData("CORECLR_ENABLE_PROFILING", "01")]
    [InlineData("CORECLR_ENABLE_PROFILING", "1x")]
    public void A_profiler_switched_on_exits(string name, string value)
    {
        // SEC-08 (2.1.159) and R3-01 (2.1.160): a switch is off only when unset, empty or read as 0 the way the runtime reads
        // a DWORD (hex, strtoul-style); anything else is on. "true" reads as 0 (t is not a hex digit) and was removed here.
        Assert.Equal($"environment variable {name} is '{value.Trim()}' (it switches on a profiler)",
            TelemetryGuard.Decide(Clean(env: Env(name, value))));
    }

    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING", "0")]
    [InlineData("CORECLR_ENABLE_PROFILING", "")]
    [InlineData("CORECLR_ENABLE_PROFILING", null)]
    [InlineData("CORECLR_ENABLE_NOTIFICATION_PROFILERS", " 0 ")]
    [InlineData("DOTNET_gcServer", "1")]
    [InlineData("DOTNET_EnableAVX2", "0")]
    [InlineData("DOTNET_HOST_PATH", "/usr/share/dotnet/dotnet")]
    [InlineData("ASPNETCORE_ENVIRONMENT", "Production")]
    [InlineData("ASPNETCORE_HOSTINGSTARTUPEXCLUDEASSEMBLIES", "x")]
    public void A_profiler_switched_off_and_ordinary_runtime_settings_carry_on(string name, string? value)
    {
        Assert.Null(TelemetryGuard.Decide(Clean(env: Env(name, value))));
    }

    [Fact]
    public void The_test_host_itself_passes_the_environment_check()
    {
        // Documented in TelemetryGuard and technical chapter 35: this suite hosts the web app in process and starts the CLI
        // as a child process, and both run the real guard on this environment. Run under a profiler-based coverage tool or
        // a startup hook, this test names the variable at fault instead of the other tests dying with exit code 78.
        var env = new List<KeyValuePair<string, string?>>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env.Add(new KeyValuePair<string, string?>((string)e.Key, e.Value as string));
        Assert.Null(TelemetryGuard.CheckEnvironment(env));
    }

    // ---- Agent injection: assemblies --------------------------------------------------------------------------------

    [Fact]
    public void The_agent_denylist_is_the_documented_one()
    {
        Assert.Equal(new[]
        {
            "Microsoft.AspNetCore.ApplicationInsights.HostingStartup", "OpenTelemetry.AutoInstrumentation", "Dynatrace",
            "OneAgent", "AppDynamics", "Instana", "Serilog.Sinks.ApplicationInsights", "Serilog.Sinks.Datadog",
        }, TelemetryGuard.DeniedAgentAssemblyPrefixes);
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.ApplicationInsights.HostingStartup", "Microsoft.AspNetCore.ApplicationInsights.HostingStartup")]
    [InlineData("OpenTelemetry.AutoInstrumentation", "OpenTelemetry.AutoInstrumentation")]
    [InlineData("OpenTelemetry.AutoInstrumentation.StartupHook", "OpenTelemetry.AutoInstrumentation")]
    [InlineData("Dynatrace.OneAgent.Core", "Dynatrace")]
    [InlineData("OneAgent", "OneAgent")]
    [InlineData("AppDynamics.Agent", "AppDynamics")]
    [InlineData("Instana.Tracing.Core", "Instana")]
    [InlineData("Serilog.Sinks.ApplicationInsights", "Serilog.Sinks.ApplicationInsights")]
    [InlineData("Serilog.Sinks.Datadog.Logs", "Serilog.Sinks.Datadog")]
    [InlineData("dynatrace", "Dynatrace")]
    public void An_injected_agent_assembly_exits(string name, string prefix)
    {
        Assert.Equal($"telemetry assembly {name} is loaded (denied prefix {prefix})",
            TelemetryGuard.Decide(Clean(loaded: ["System.Private.CoreLib", name])));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting")]
    [InlineData("Microsoft.AspNetCore.ApplicationInsightsLike")]
    [InlineData("OpenTelemetry")]
    [InlineData("OpenTelemetry.Api")]
    [InlineData("DynatraceLike")]
    [InlineData("Serilog")]
    [InlineData("Serilog.Sinks.Console")]
    [InlineData("Serilog.Sinks.File")]
    public void A_name_that_only_resembles_an_agent_carries_on(string name)
    {
        Assert.Null(TelemetryGuard.CheckLoadedAssembly(name));
    }

    // A decoder for custom attribute blobs that names types as strings (enough to read AssemblyMetadata's two strings).
    private sealed class StringTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => throw new NotSupportedException(type);
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
