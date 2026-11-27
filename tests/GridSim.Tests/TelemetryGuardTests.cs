using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Telemetry is provably off in the desktop app and the web app (RC5 package TEL). Three layers are pinned here:
/// <list type="bullet">
/// <item>the build switches (the empty AvaloniaStats target, the TelemetryOff marker item and the workflow-level
/// AVALONIA_TELEMETRY_OPTOUT=1);</item>
/// <item>the package graphs of the app and web projects, read from the project files, the lock files and the restore
/// assets, which carry no telemetry SDK (Avalonia.BuildServices is present as a build-only package and must stay
/// build-only);</item>
/// <item>the runtime guard (<see cref="TelemetryGuard"/>): the marker is on every shipped app assembly, both entry points
/// call the guard first, and every trigger produces an exit decision. The decision is a pure function, so it is driven
/// here without ending the test host.</item>
/// </list>
/// </summary>
public class TelemetryGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    // The projects that make up the shipped desktop app and web app.
    private static readonly string[] AppProjects =
        ["GridSim.App.Desktop", "GridSim.App", "GridSim.Ui", "GridSim.Core", "GridSim.Scripting", "GridSim.Web"];

    // The only denylisted package allowed in a graph: Avalonia's build statistics package, which Avalonia brings in. It
    // has build assets only (its AvaloniaStats target, replaced by an empty one) and no runtime or compile assembly.
    private const string BuildOnlyPackage = "Avalonia.BuildServices";

    private static TelemetryGuardFacts Clean(
        IReadOnlyList<MarkerFact>? markers = null, IReadOnlyList<string>? loaded = null,
        IEnumerable<KeyValuePair<string, string?>>? env = null, TelemetryWebPolicy? web = null,
        IReadOnlyList<string>? problems = null) =>
        new(markers ?? [new MarkerFact("GridSim.App.Desktop", ["true"]), new MarkerFact("GridSim.Core", ["true"])],
            loaded ?? ["System.Private.CoreLib", "GridSim.Core", "Avalonia.Base"],
            env ?? [new("PATH", "/usr/bin"), new("AVALONIA_TELEMETRY_OPTOUT", "1")],
            web,
            problems ?? []);

    private static IEnumerable<KeyValuePair<string, string?>> Env(string name, string? value) =>
        [new("PATH", "/usr/bin"), new(name, value)];

    // ---- Build switches ---------------------------------------------------------------------------------------------

    [Fact]
    public void Build_override_and_marker_are_stamped_by_directory_build_targets()
    {
        string targets = Read("Directory.Build.targets");
        Assert.Matches(new Regex(@"<Target\s+Name=""AvaloniaStats""\s*/>"), targets);
        Assert.DoesNotContain("AvaloniaStatsTask", targets, StringComparison.Ordinal);
        // The marker item, unconditional (not inside a conditioned ItemGroup) and with exactly the value the guard wants.
        Assert.Matches(new Regex(@"\n  <ItemGroup>\n    <AssemblyMetadata Include=""TelemetryOff"" Value=""true"" />\n  </ItemGroup>\n"), targets);
        Assert.Equal("TelemetryOff", TelemetryGuard.MarkerKey);
        Assert.Equal("true", TelemetryGuard.MarkerValue);
    }

    [Fact]
    public void Every_building_workflow_still_sets_the_avalonia_opt_out_at_workflow_level()
    {
        string dir = Path.Combine(Root(), ".github", "workflows");
        int building = 0;
        foreach (string path in Directory.GetFiles(dir, "*.yml"))
        {
            string yml = File.ReadAllText(path).Replace("\r\n", "\n");
            if (!Regex.IsMatch(yml, @"\bdotnet (build|test|publish|pack|restore)\b")) continue;
            building++;
            int jobs = yml.IndexOf("\njobs:", StringComparison.Ordinal);
            Assert.True(jobs > 0, $"{Path.GetFileName(path)} has no jobs: block");
            Assert.True(Regex.IsMatch(yml[..jobs], @"\nenv:\n(  [^\n]*\n)*  AVALONIA_TELEMETRY_OPTOUT: '1'\n"),
                $"{Path.GetFileName(path)} builds with dotnet but does not set AVALONIA_TELEMETRY_OPTOUT: '1' at workflow level");
            // And no workflow sets a variable the runtime guard refuses.
            foreach (string v in TelemetryGuard.DeniedVariables)
                Assert.DoesNotContain(v + ":", yml, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(building >= 5, $"only {building} building workflow(s) found");
    }

    // ---- Package graphs ---------------------------------------------------------------------------------------------

    private static bool Denied(string packageId) => TelemetryGuard.CheckLoadedAssembly(packageId) is not null;

    [Fact]
    public void No_project_file_references_a_telemetry_package()
    {
        foreach (string p in AppProjects)
        {
            string csproj = Read(Path.Combine("src", p, p + ".csproj"));
            foreach (Match m in Regex.Matches(csproj, @"<PackageReference\s+Include=""([^""]+)"""))
                Assert.False(Denied(m.Groups[1].Value), $"{p}.csproj references the telemetry package {m.Groups[1].Value}");
        }
        foreach (string shared in new[] { "Directory.Build.props", "Directory.Build.targets" })
            Assert.DoesNotMatch(new Regex(@"<PackageReference\b"), Read(shared));
    }

    [Fact]
    public void No_lock_file_holds_a_telemetry_package_except_the_build_only_avalonia_one()
    {
        foreach (string p in AppProjects)
        {
            using JsonDocument lockFile = JsonDocument.Parse(Read(Path.Combine("src", p, "packages.lock.json")));
            int packages = 0;
            foreach (JsonProperty target in lockFile.RootElement.GetProperty("dependencies").EnumerateObject())
                foreach (JsonProperty package in target.Value.EnumerateObject())
                {
                    packages++;
                    if (!Denied(package.Name)) continue;
                    Assert.True(package.Name == BuildOnlyPackage, $"{p}/packages.lock.json holds the telemetry package {package.Name}");
                    // Never asked for directly: it only arrives with Avalonia.
                    Assert.Equal("Transitive", package.Value.GetProperty("type").GetString());
                }
            if (p is "GridSim.App" or "GridSim.App.Desktop" or "GridSim.Web") Assert.True(packages > 0, $"{p}/packages.lock.json lists no packages");
        }
    }

    [Fact]
    public void The_restore_assets_give_the_avalonia_statistics_package_no_runtime_or_compile_assembly()
    {
        foreach (string p in AppProjects)
        {
            string path = Path.Combine(Root(), "src", p, "obj", "project.assets.json");
            Assert.True(File.Exists(path), $"{p} has not been restored ({path} is missing)");
            using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                string id = library.Name.Split('/')[0];
                if (!Denied(id)) continue;
                Assert.True(id == BuildOnlyPackage, $"{p} restores the telemetry package {library.Name}");
            }
            foreach (JsonProperty target in assets.RootElement.GetProperty("targets").EnumerateObject())
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    if (library.Name.Split('/')[0] != BuildOnlyPackage) continue;
                    foreach (string kind in new[] { "compile", "runtime", "native", "runtimeTargets", "resource", "frameworkReferences", "contentFiles" })
                        Assert.False(library.Value.TryGetProperty(kind, out _),
                            $"{p} ({target.Name}): {library.Name} now has '{kind}' assets; it must stay build-only");
                }
        }
    }

    [Fact]
    public void No_assembly_loaded_in_the_test_host_is_a_telemetry_assembly()
    {
        // The test host loads Core, Web (in process), Ui, Scripting and their dependencies.
        _ = typeof(GridSim.Web.Site.SecurityHeaders).Assembly;
        _ = typeof(GridSim.Ui.Settings.AppSettings).Assembly;
        foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            Assert.Null(TelemetryGuard.CheckLoadedAssembly(a.GetName().Name));
    }

    // ---- The build marker on every shipped app assembly -------------------------------------------------------------

    private static string Configuration() =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/bin/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

    // The TelemetryOff values on a compiled assembly, read from its metadata without loading it (the Avalonia projects
    // are not referenced by the tests).
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

    [Fact]
    public void Every_shipped_app_assembly_carries_the_telemetry_marker()
    {
        string config = Configuration();
        foreach (string p in AppProjects)
        {
            string dll = Path.Combine(Root(), "src", p, "bin", config, "net10.0", p + ".dll");
            Assert.True(File.Exists(dll), $"{dll} is missing; build the solution first");
            Assert.Null(TelemetryGuard.CheckMarker(p, MarkerValuesInFile(dll)));
        }
    }

    [Fact]
    public void The_loaded_app_assemblies_carry_the_marker_at_run_time()
    {
        foreach (System.Reflection.Assembly a in new[]
                 {
                     typeof(TelemetryGuard).Assembly, typeof(GridSim.Web.Site.SecurityHeaders).Assembly,
                     typeof(GridSim.Ui.Settings.AppSettings).Assembly, typeof(TelemetryGuardTests).Assembly,
                 })
        {
            var values = a.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .Where(m => m.Key == TelemetryGuard.MarkerKey).Select(m => m.Value).ToList();
            Assert.Null(TelemetryGuard.CheckMarker(a.GetName().Name!, values));
        }
    }

    // ---- Both entry points call the guard first ---------------------------------------------------------------------

    [Fact]
    public void The_desktop_entry_point_runs_the_guard_before_anything_else()
    {
        string src = Read("src/GridSim.App.Desktop/Program.cs");
        int main = src.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);
        Assert.True(main > 0);
        string body = src[(src.IndexOf('{', main) + 1)..];
        string firstStatement = string.Join('\n', body.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))).Split(';')[0];
        Assert.Equal("GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup(\"GridSim.App.Desktop\", AppAssemblies, foreignEntryAllowed: false)", firstStatement);
        Assert.Contains("[\"GridSim.App.Desktop\", \"GridSim.App\", \"GridSim.Ui\", \"GridSim.Core\", \"GridSim.Scripting\"]", src, StringComparison.Ordinal);
        // The rest of start-up is kept out of Main so that nothing it uses is loaded before the guard.
        Assert.Contains("MethodImplOptions.NoInlining)]\n    private static int Start(string[] args)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_entry_point_runs_the_guard_before_anything_else()
    {
        string src = Read("src/GridSim.Web/Program.cs");
        string code = string.Join('\n', src.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("using ", StringComparison.Ordinal)));
        string first = code.Split(';')[0].Replace("\n", " ", StringComparison.Ordinal);
        Assert.Equal("GridSim.Core.Diagnostics.TelemetryGuard.EnforceAtStartup( \"GridSim.Web\", [\"GridSim.Web\", \"GridSim.Core\", \"GridSim.Scripting\"], foreignEntryAllowed: true)", first);
    }

    [Fact]
    public void The_exit_wrapper_writes_one_line_to_stderr_and_the_trace_log_then_exits_with_the_dedicated_code()
    {
        string src = Read("src/GridSim.Core/Diagnostics/TelemetryGuard.cs");
        int exit = src.IndexOf("private static void Exit(string app, string reason)", StringComparison.Ordinal);
        Assert.True(exit > 0);
        string body = src[exit..src.IndexOf("private static string OneLine", exit, StringComparison.Ordinal)];
        Assert.Contains("Console.Error.WriteLine(line)", body, StringComparison.Ordinal);
        Assert.Contains("Trace.WriteLine(line)", body, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(ExitCode);", body, StringComparison.Ordinal);
        // No dialog, no other UI.
        Assert.DoesNotContain("MessageBox", src, StringComparison.Ordinal);
        Assert.DoesNotContain("using Avalonia", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Window", src, StringComparison.Ordinal);
        // The load handler is installed before the first scan.
        int handler = src.IndexOf("AppDomain.CurrentDomain.AssemblyLoad +=", StringComparison.Ordinal);
        int scan = src.IndexOf("Evaluate(() => Gather(", StringComparison.Ordinal);
        Assert.True(handler > 0 && scan > handler);
    }

    [Fact]
    public void The_exit_code_is_78_and_the_exit_line_is_one_plain_line_naming_the_reason()
    {
        Assert.Equal(78, TelemetryGuard.ExitCode);
        string line = TelemetryGuard.ExitLine("GridSim.Web", "environment variable SENTRY_DSN is set\nsecond line");
        Assert.Equal("GridSim.Web: telemetry guard: environment variable SENTRY_DSN is set second line. Exiting with code 78.", line);
        Assert.DoesNotContain('\n', line);
    }

    // ---- The decision: each trigger ---------------------------------------------------------------------------------

    [Fact]
    public void Clean_facts_carry_on()
    {
        Assert.Null(TelemetryGuard.Decide(Clean()));
        Assert.Null(TelemetryGuard.Evaluate(() => Clean()));
        Assert.Null(TelemetryGuard.Decide(Clean(web: new TelemetryWebPolicy("connect-src 'self'", []))));
    }

    [Theory]
    [InlineData(new string[0], "has no TelemetryOff build marker")]
    [InlineData(new[] { "false" }, "has TelemetryOff=false, not true")]
    [InlineData(new[] { "True" }, "has TelemetryOff=True, not true")]
    [InlineData(new[] { " true" }, "has TelemetryOff= true, not true")]
    [InlineData(new[] { "" }, "has TelemetryOff=, not true")]
    [InlineData(new[] { "true", "false" }, "has TelemetryOff=false, not true")]
    public void A_missing_or_wrong_marker_exits(string[] values, string expected)
    {
        string? reason = TelemetryGuard.Decide(Clean(markers:
            [new MarkerFact("GridSim.App.Desktop", ["true"]), new MarkerFact("GridSim.App", values)]));
        Assert.Equal("assembly GridSim.App " + expected, reason);
    }

    [Fact]
    public void A_null_marker_value_or_no_checked_assembly_at_all_exits()
    {
        Assert.Equal("assembly GridSim.Web has TelemetryOff=(null), not true",
            TelemetryGuard.Decide(Clean(markers: [new MarkerFact("GridSim.Web", [null])])));
        Assert.Equal("no app assembly was checked for the build marker", TelemetryGuard.Decide(Clean(markers: [])));
    }

    public static TheoryData<string> DeniedPrefixes()
    {
        var data = new TheoryData<string>();
        foreach (string p in TelemetryGuard.DeniedAssemblyPrefixes) data.Add(p);
        return data;
    }

    [Theory]
    [MemberData(nameof(DeniedPrefixes))]
    public void A_denylisted_assembly_exits_by_exact_name_and_by_dotted_prefix(string prefix)
    {
        foreach (string name in new[] { prefix, prefix + ".AspNetCore", prefix.ToUpperInvariant() + ".X" })
        {
            string? reason = TelemetryGuard.Decide(Clean(loaded: ["System.Private.CoreLib", name]));
            Assert.Equal($"telemetry assembly {name} is loaded (denied prefix {prefix})", reason);
        }
        // The same check is what the AssemblyLoad handler runs on a later load.
        Assert.NotNull(TelemetryGuard.CheckLoadedAssembly(prefix + ".Late"));
    }

    [Fact]
    public void The_denylist_is_the_documented_one_and_matches_on_whole_name_segments()
    {
        Assert.Equal(new[]
        {
            "Avalonia.BuildServices", "Microsoft.ApplicationInsights", "Microsoft.Extensions.Logging.ApplicationInsights",
            "OpenTelemetry.Exporter", "Azure.Monitor", "Sentry", "Microsoft.VisualStudio.Telemetry", "Segment", "Mixpanel",
            "Bugsnag", "Raygun", "Datadog", "Elastic.Apm", "NewRelic", "Honeycomb",
        }, TelemetryGuard.DeniedAssemblyPrefixes);
        foreach (string? allowed in new string?[] { "SentryLike", "Avalonia.Base", "Avalonia", "OpenTelemetry", "OpenTelemetry.Api",
                     "Microsoft.Extensions.Logging", "Azure.Core", "GridSim.Core", "Segmentation", "", null })
            Assert.Null(TelemetryGuard.CheckLoadedAssembly(allowed));
    }

    [Theory]
    // OpenTelemetry: any exporter setting, any exporter selector other than none, the automatic instrumentation.
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "")]
    [InlineData("otel_exporter_zipkin_endpoint", "http://z")]
    [InlineData("OTEL_TRACES_EXPORTER", "otlp")]
    [InlineData("OTEL_METRICS_EXPORTER", "console")]
    [InlineData("OTEL_LOGS_EXPORTER", "otlp,console")]
    [InlineData("OTEL_TRACES_EXPORTER", "")]
    [InlineData("OTEL_DOTNET_AUTO_HOME", "/otel")]
    // Application Insights / Azure Monitor.
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=x")]
    [InlineData("APPLICATIONINSIGHTS_AUTHENTICATION_STRING", "Authorization=AAD")]
    [InlineData("APPINSIGHTS_INSTRUMENTATIONKEY", "00000000-0000-0000-0000-000000000000")]
    [InlineData("ApplicationInsightsAgent_EXTENSION_VERSION", "~3")]
    [InlineData("XDT_MicrosoftApplicationInsights_Mode", "recommended")]
    [InlineData("APPINSIGHTS_PROFILERFEATURE_VERSION", "1.0.0")]
    [InlineData("APPINSIGHTS_SNAPSHOTFEATURE_VERSION", "1.0.0")]
    // Sentry, Datadog, New Relic, Elastic APM, Honeycomb.
    [InlineData("SENTRY_DSN", "https://k@o.ingest.sentry.io/1")]
    [InlineData("sentry_dsn", "x")]
    [InlineData("DD_API_KEY", "k")]
    [InlineData("DD_TRACE_ENABLED", "true")]
    [InlineData("DD_TRACE_ENABLED", "TRUE")]
    [InlineData("DD_TRACE_ENABLED", "1")]
    [InlineData("NEW_RELIC_LICENSE_KEY", "k")]
    [InlineData("ELASTIC_APM_SERVER_URL", "http://apm")]
    [InlineData("ELASTIC_APM_SERVER_URLS", "http://apm")]
    [InlineData("HONEYCOMB_API_KEY", "k")]
    // Avalonia's opt-out switched off.
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "0")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "false")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "yes")]
    public void Each_exporter_variable_family_exits(string name, string value)
    {
        string? reason = TelemetryGuard.Decide(Clean(env: Env(name, value)));
        Assert.NotNull(reason);
        Assert.StartsWith("environment variable " + name + " ", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_listed_variable_exits_whatever_its_value()
    {
        foreach (string v in TelemetryGuard.DeniedVariables)
            foreach (string? value in new[] { "x", "", "none", "0", null })
                Assert.Equal($"environment variable {v} is set (it switches on a telemetry exporter)",
                    TelemetryGuard.CheckEnvironment(Env(v, value)));
        foreach (string p in TelemetryGuard.DeniedVariablePrefixes)
            Assert.NotNull(TelemetryGuard.CheckEnvironment(Env(p + "ANYTHING", "none")));
    }

    [Theory]
    [InlineData("OTEL_TRACES_EXPORTER", "none")]
    [InlineData("OTEL_METRICS_EXPORTER", "NONE")]
    [InlineData("OTEL_LOGS_EXPORTER", " none ")]
    [InlineData("OTEL_SERVICE_NAME", "gridsim")]
    [InlineData("OTEL_SDK_DISABLED", "true")]
    [InlineData("DD_TRACE_ENABLED", "false")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "1")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "true")]
    [InlineData("AVALONIA_TELEMETRY_OPTOUT", "True")]
    [InlineData("GRIDSIM_CRASH_DIR", "/tmp")]
    public void Settings_that_keep_exporters_off_carry_on(string name, string value)
    {
        Assert.Null(TelemetryGuard.Decide(Clean(env: Env(name, value))));
    }

    [Theory]
    [InlineData("connect-src *")]
    [InlineData("connect-src 'self' https://www.google-analytics.com")]
    [InlineData("connect-src https:")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("connect-src 'none' 'self'")]
    public void A_web_policy_without_connect_src_self_exits(string? connectSrc)
    {
        string? reason = TelemetryGuard.Decide(Clean(web: new TelemetryWebPolicy(connectSrc, [])));
        Assert.Equal($"the Content-Security-Policy connect-src is '{connectSrc ?? ""}', not connect-src 'self'", reason);
    }

    [Fact]
    public void A_configured_third_party_script_origin_exits()
    {
        foreach (string origin in new[] { "https://www.googletagmanager.com", "https://stats.example.com/a.js", "http://x" })
            Assert.Equal($"a third-party script origin is configured ({origin})",
                TelemetryGuard.Decide(Clean(web: new TelemetryWebPolicy("connect-src 'self'", ["", origin]))));
        Assert.Null(TelemetryGuard.CheckWebPolicy("connect-src   'self'", ["", "  "]));
    }

    [Fact]
    public void An_exception_inside_the_guard_exits()
    {
        Assert.Equal("the telemetry guard itself failed (InvalidOperationException: boom)",
            TelemetryGuard.Evaluate(() => throw new InvalidOperationException("boom")));
        Assert.Equal("the telemetry guard itself failed (FileNotFoundException: no such assembly two)",
            TelemetryGuard.Evaluate(() => throw new FileNotFoundException("no such assembly\ntwo")));
        Assert.StartsWith("the telemetry guard itself failed (ArgumentNullException:", TelemetryGuard.Evaluate(null!) ?? "", StringComparison.Ordinal);
        // An exception during the decision itself (here, while reading the environment) fails closed too.
        Assert.Equal("the telemetry guard itself failed (IOException: environment unreadable)",
            TelemetryGuard.Evaluate(() => Clean(env: ThrowingEnvironment())));
        Assert.StartsWith("the telemetry guard itself failed (ArgumentNullException:",
            TelemetryGuard.Evaluate(() => null!) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_found_while_gathering_exits_first()
    {
        Assert.Equal("the entry assembly cannot be identified",
            TelemetryGuard.Decide(Clean(problems: ["the entry assembly cannot be identified"],
                env: Env("SENTRY_DSN", "x"))));
    }

    private static IEnumerable<KeyValuePair<string, string?>> ThrowingEnvironment()
    {
        yield return new("PATH", "/usr/bin");
        throw new IOException("environment unreadable");
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
