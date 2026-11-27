using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The case editor (GridSim.SchemaEditor) and the script console (gridsim-script, GridSim.Scripting.Console) run the
/// telemetry guard as their first statement, with the same exit code 78 as every other GridSim app, and their assemblies
/// carry the build marker. The guard is strict everywhere (Mark's ruling of 2026-11-25), so neither app has a way round
/// it.
/// <para>The entry points are checked in their source (the guard ends the process, so it cannot be called in the test
/// host); the markers are read from the built assemblies' metadata without loading them; and each app is started for
/// real, as a child process whose environment the test sets, both clean and with a refused variable.</para>
/// </summary>
public sealed class TelemetryGuardConsoleAppsTests
{
    private static string Root() => CliProcess.Root();

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    // The code of a file without blank lines, comment lines and using directives, joined by newlines.
    private static string CodeLines(string src) => string.Join('\n', src.Split('\n').Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("using ", StringComparison.Ordinal)));

    // The statements of the method whose signature is given, from its opening brace on.
    private static string[] StatementsOf(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found");
        string body = src[(src.IndexOf('{', at) + 1)..];
        return CodeLines(body).Split(';').Select(s => s.Replace("\n", " ", StringComparison.Ordinal).Trim()).ToArray();
    }

    private static string[] ProjectReferences(string csproj) =>
        Regex.Matches(csproj, @"<ProjectReference\s+Include=""\.\.\\(GridSim\.[A-Za-z]+)\\")
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- Entry points -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_case_editor_runs_the_guard_as_the_first_statement_of_main()
    {
        string src = Read("src/GridSim.SchemaEditor/EntryPoint.cs");
        string[] statements = StatementsOf(src, "public static int Main(string[] args)");
        Assert.Equal("GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup(\"GridSim.SchemaEditor\", AppAssemblies, foreignEntryAllowed: false)", statements[0]);
        // Main is the process entry (the project's StartupObject); Run, which the editor tests drive in process, is not.
        Assert.Contains("<StartupObject>GridSim.SchemaEditor.EntryPoint</StartupObject>", Read("src/GridSim.SchemaEditor/GridSim.SchemaEditor.csproj"), StringComparison.Ordinal);
        Assert.DoesNotContain("TelemetryGuard", src[src.IndexOf("internal static int Run(", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void The_case_editor_checks_its_whole_gridsim_reference_closure()
    {
        Assert.Equal(new[] { "GridSim.SchemaEditor", "GridSim.Core" }, GridSim.SchemaEditor.EntryPoint.AppAssemblies);
        string csproj = Read("src/GridSim.SchemaEditor/GridSim.SchemaEditor.csproj");
        Assert.Contains("<AssemblyName>GridSim.SchemaEditor</AssemblyName>", csproj, StringComparison.Ordinal);
        Assert.Equal(new[] { "GridSim.Core" }, ProjectReferences(csproj));
    }

    [Fact]
    public void The_script_console_runs_the_guard_as_the_first_statement_of_main()
    {
        string src = Read("src/GridSim.Scripting.Console/Program.cs");
        string[] statements = StatementsOf(src, "public static int Main(string[] args)");
        Assert.Equal("GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup(\"gridsim-script\", AppAssemblies, foreignEntryAllowed: false)", statements[0]);
        // The culture pins come straight after the guard, still before the command dispatch.
        Assert.Equal("CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture", statements[1]);
        Assert.Equal("CultureInfo.CurrentCulture = CultureInfo.InvariantCulture", statements[2]);
    }

    [Fact]
    public void The_script_console_checks_its_whole_gridsim_reference_closure()
    {
        string src = Read("src/GridSim.Scripting.Console/Program.cs");
        Assert.Contains("internal static readonly string[] AppAssemblies = [\"gridsim-script\", \"GridSim.Scripting\", \"GridSim.Core\"];", src, StringComparison.Ordinal);
        string csproj = Read("src/GridSim.Scripting.Console/GridSim.Scripting.Console.csproj");
        Assert.Contains("<AssemblyName>gridsim-script</AssemblyName>", csproj, StringComparison.Ordinal);
        Assert.Equal(new[] { "GridSim.Scripting" }, ProjectReferences(csproj));
        // GridSim.Scripting references only GridSim.Core, so the closure is complete.
        Assert.Equal(new[] { "GridSim.Core" }, ProjectReferences(Read("src/GridSim.Scripting/GridSim.Scripting.csproj")));
    }

    // ---- The build marker -------------------------------------------------------------------------------------------

    private static string Configuration() =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    // (project folder, assembly name) of the two apps' GridSim assemblies not already covered by TelemetryGuardTests.
    private static readonly (string Project, string Assembly)[] ConsoleAppAssemblies =
    [
        ("GridSim.SchemaEditor", "GridSim.SchemaEditor"),
        ("GridSim.Scripting.Console", "gridsim-script"),
        ("GridSim.Scripting", "GridSim.Scripting"),
    ];

    [Fact]
    public void Neither_project_opts_out_of_the_stamped_marker()
    {
        // Directory.Build.targets adds the marker as an AssemblyMetadata item to every project (TelemetryGuardTests pins
        // that item); a project could still lose it by not generating assembly attributes or by removing the item.
        foreach ((string project, _) in ConsoleAppAssemblies)
        {
            string csproj = Read($"src/{project}/{project}.csproj");
            Assert.DoesNotContain("GenerateAssemblyInfo", csproj, StringComparison.Ordinal);
            Assert.DoesNotContain("AssemblyMetadata", csproj, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_case_editor_and_script_console_assemblies_carry_the_telemetry_marker()
    {
        string config = Configuration();
        foreach ((string project, string assembly) in ConsoleAppAssemblies)
        {
            string dll = Path.Combine(Root(), "src", project, "bin", config, "net10.0", assembly + ".dll");
            Assert.True(File.Exists(dll), $"{dll} is missing; build the solution first");
            List<string?> values = MarkerValuesInFile(dll);
            Assert.Equal(new string?[] { TelemetryGuard.MarkerValue }, values);
            Assert.Null(TelemetryGuard.CheckMarker(assembly, values));
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

    // ---- The apps, run for real ---------------------------------------------------------------------------------------

    private static string BuiltDll(string project, string assembly)
    {
        string dll = Path.Combine(Root(), "src", project, "bin", Configuration(), "net10.0", assembly + ".dll");
        Assert.True(File.Exists(dll), $"{dll} is missing; build the solution first");
        return dll;
    }

    private static CliProcess.Result RunApp(string project, string assembly, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return CliProcess.Run(string.IsNullOrWhiteSpace(host) ? "dotnet" : host, new[] { BuiltDll(project, assembly) }.Concat(args).ToArray(), environment: env);
    }

    [Theory]
    [InlineData("GridSim.SchemaEditor", "GridSim.SchemaEditor", "GridSim.SchemaEditor", "SENTRY_DSN", "https://k@o.example/1", "environment variable SENTRY_DSN is set (it switches on a telemetry exporter)")]
    [InlineData("GridSim.SchemaEditor", "GridSim.SchemaEditor", "GridSim.SchemaEditor", "DT_TENANT", "abc12345", "environment variable DT_TENANT is set (it configures an APM agent)")]
    [InlineData("GridSim.SchemaEditor", "GridSim.SchemaEditor", "GridSim.SchemaEditor", "DOTNET_HOSTINGSTARTUPASSEMBLIES", "Some.Agent", "environment variable DOTNET_HOSTINGSTARTUPASSEMBLIES is set (it loads code into the process)")]
    [InlineData("GridSim.Scripting.Console", "gridsim-script", "gridsim-script", "SENTRY_DSN", "https://k@o.example/1", "environment variable SENTRY_DSN is set (it switches on a telemetry exporter)")]
    [InlineData("GridSim.Scripting.Console", "gridsim-script", "gridsim-script", "APPDYNAMICS_CONTROLLER_HOST_NAME", "controller.example", "environment variable APPDYNAMICS_CONTROLLER_HOST_NAME is set (it configures an APM agent)")]
    [InlineData("GridSim.SchemaEditor", "GridSim.SchemaEditor", "GridSim.SchemaEditor", "DD_ENV", "prod", "environment variable DD_ENV is set (it configures an APM agent)")]
    [InlineData("GridSim.Scripting.Console", "gridsim-script", "gridsim-script", "DD_SERVICE", "gridsim", "environment variable DD_SERVICE is set (it configures an APM agent)")]
    [InlineData("GridSim.Scripting.Console", "gridsim-script", "gridsim-script", "DOTNET_HOSTINGSTARTUPASSEMBLIES", "Some.Agent", "environment variable DOTNET_HOSTINGSTARTUPASSEMBLIES is set (it loads code into the process)")]
    public void Each_app_exits_78_with_one_line_before_doing_anything(string project, string assembly, string app, string name, string value, string reason)
    {
        // --version is answered after the guard, so a refusal shows as exit 78, the one line, and nothing on stdout.
        // DOTNET_HOSTINGSTARTUPASSEMBLIES is read only by an ASP.NET Core host, so for these console apps it is inert
        // apart from the guard.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = value };
        CliProcess.Result r = RunApp(project, assembly, env, "--version");
        Assert.True(r.ExitCode == TelemetryGuard.ExitCode, r.ToString());
        Assert.Equal(TelemetryGuard.ExitLine(app, reason), r.StdErr.TrimEnd('\r', '\n'));
        Assert.True(r.StdOut.Length == 0, r.ToString());
    }

    [Theory]
    [InlineData("GridSim.SchemaEditor", "GridSim.SchemaEditor", "GridSim.SchemaEditor ")]
    [InlineData("GridSim.Scripting.Console", "gridsim-script", "gridsim-script ")]
    public void Each_app_starts_normally_in_a_clean_environment(string project, string assembly, string versionPrefix)
    {
        CliProcess.Result r = RunApp(project, assembly, null, "--version");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.StartsWith(versionPrefix, r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("telemetry guard", r.StdErr, StringComparison.Ordinal);
    }

    // ---- Docs -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_docs_list_both_apps_and_say_the_guard_is_strict_in_development()
    {
        string ch35 = Read("docs/technical/0035 Deployment and security.md");
        Assert.Contains("| Case editor (`GridSim.SchemaEditor`) |", ch35, StringComparison.Ordinal);
        Assert.Contains("| Script console (`gridsim-script`) |", ch35, StringComparison.Ordinal);
        string contributing = Read("CONTRIBUTING.md");
        Assert.Contains("### Developing with the telemetry guard", contributing, StringComparison.Ordinal);
        int section = contributing.IndexOf("### Developing with the telemetry guard", StringComparison.Ordinal);
        string text = contributing[section..contributing.IndexOf("\n## ", section, StringComparison.Ordinal)];
        foreach (string must in new[] { "`dotnet watch`", "Hot Reload", "profiler-based coverage", "exit with code 78", "`dotnet run" })
            Assert.Contains(must, text, StringComparison.Ordinal);
    }
}
