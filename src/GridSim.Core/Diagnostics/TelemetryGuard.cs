using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// The telemetry guard: the first thing every GridSim app runs at start-up: the desktop app (GridSim.App.Desktop), the
/// WPF dashboard (GridSim.Wpf), the command line tool (gridsim, GridSim.Cli), the web app (GridSim.Web), the case editor
/// (GridSim.SchemaEditor) and the script console (gridsim-script, GridSim.Scripting.Console).
/// Telemetry is switched off where it could come from (the build; see Directory.Build.targets), and neither Avalonia
/// nor ASP.NET Core sends anything at run time by default. The guard enforces that this stays true: if
/// anything says telemetry could be on, the app exits before it does any other work.
///
/// <para>It checks four things, and any one of them failing ends the process with <see cref="ExitCode"/>:</para>
/// <list type="number">
/// <item>The build marker. Directory.Build.targets stamps <c>[assembly: AssemblyMetadata("TelemetryOff", "true")]</c>
/// on every project. The entry assembly and each of the app's own assemblies must carry it, with exactly the value
/// <c>true</c>. A missing marker means the binary was not built by this repository's build.</item>
/// <item>Telemetry assemblies. No loaded assembly may be on <see cref="DeniedAssemblyPrefixes"/> or
/// <see cref="DeniedAgentAssemblyPrefixes"/>. The check runs once at start-up and again on every later load (an
/// <see cref="AppDomain.AssemblyLoad"/> handler), so one loaded by a plugin, a startup hook or a late dependency also ends
/// the process.</item>
/// <item>Environment variables. None of the variables in <see cref="CheckEnvironment"/> that would switch on an
/// exporter, configure an APM agent, or load an agent into the process (a profiler, a startup hook, a hosting-startup
/// assembly), may be set.</item>
/// <item>Fail closed. Any exception inside the guard itself also ends the process.</item>
/// </list>
/// <para>The web app also runs <see cref="EnforceWebPolicy"/> as soon as its configuration is read: the
/// Content-Security-Policy its answers carry, composed from that configuration exactly as the host composes it, must keep
/// <c>connect-src 'self'</c>, and no third-party script origin may be configured.</para>
///
/// <para>On exit the guard writes one plain line to the error stream and to the trace log (the app log), naming the
/// reason, and ends with <see cref="ExitCode"/>. It shows no dialog. The guard does not watch network traffic: it
/// enforces that no telemetry code is present or switched on, not that the process never opens a connection.</para>
///
/// <para>The decision is a pure function (<see cref="Decide"/>, over <see cref="TelemetryGuardFacts"/>), so the tests
/// can drive every trigger without ending the test host; <see cref="EnforceAtStartup"/> gathers the facts from the
/// running process and exits. Only <see cref="EnforceAtStartup"/> reads the real environment. A test host run under a
/// profiler-based coverage tool or a startup hook (for example dotnet-coverage's dynamic mode, dotCover or Hot Reload)
/// carries variables refused here, so the web app hosted in that process and the CLI started as its child would exit
/// with <see cref="ExitCode"/>: run the suite without one (coverlet's static instrumentation sets none).</para>
/// <para>The guard is strict in every app, development included: <c>dotnet watch</c> and Hot Reload (a startup hook) and
/// profiler-based coverage tools make every GridSim app exit with <see cref="ExitCode"/>, by design. CONTRIBUTING says
/// how to develop without them.</para>
/// </summary>
public static class TelemetryGuard
{
    /// <summary>The exit code the guard ends the process with. Used for nothing else in GridSim.</summary>
    public const int ExitCode = 78;

    /// <summary>The assembly metadata key Directory.Build.targets stamps.</summary>
    public const string MarkerKey = "TelemetryOff";

    /// <summary>The only accepted marker value.</summary>
    public const string MarkerValue = "true";

    /// <summary>
    /// Assembly-name prefixes of telemetry SDKs. A loaded assembly whose simple name equals a prefix, or starts with the
    /// prefix followed by a dot, is refused (matched ignoring case). In order:
    /// Avalonia's build statistics, Application Insights and its logging provider, the OpenTelemetry exporters, Azure
    /// Monitor, Sentry, Visual Studio telemetry, Segment, Mixpanel, Bugsnag, Raygun, Datadog, Elastic APM, New Relic and
    /// Honeycomb.
    /// </summary>
    public static IReadOnlyList<string> DeniedAssemblyPrefixes { get; } =
    [
        "Avalonia.BuildServices",
        "Microsoft.ApplicationInsights",
        "Microsoft.Extensions.Logging.ApplicationInsights",
        "OpenTelemetry.Exporter",
        "Azure.Monitor",
        "Sentry",
        "Microsoft.VisualStudio.Telemetry",
        "Segment",
        "Mixpanel",
        "Bugsnag",
        "Raygun",
        "Datadog",
        "Elastic.Apm",
        "NewRelic",
        "Honeycomb",
    ];

    /// <summary>
    /// Assembly-name prefixes of the agents an APM product injects into a process, matched as
    /// <see cref="DeniedAssemblyPrefixes"/> are. In order: the Application Insights hosting-startup assembly (how App
    /// Service attaches it), the OpenTelemetry .NET automatic instrumentation, Dynatrace and its OneAgent, AppDynamics,
    /// Instana, and the Serilog sinks that send logs to Application Insights or Datadog.
    /// </summary>
    public static IReadOnlyList<string> DeniedAgentAssemblyPrefixes { get; } =
    [
        "Microsoft.AspNetCore.ApplicationInsights.HostingStartup",
        "OpenTelemetry.AutoInstrumentation",
        "Dynatrace",
        "OneAgent",
        "AppDynamics",
        "Instana",
        "Serilog.Sinks.ApplicationInsights",
        "Serilog.Sinks.Datadog",
    ];

    /// <summary>Variables refused whenever they are set, whatever their value (names matched ignoring case).</summary>
    public static IReadOnlyList<string> DeniedVariables { get; } =
    [
        // Application Insights / Azure Monitor (the connection string, the older key, and App Service's agent switches).
        "APPLICATIONINSIGHTS_CONNECTION_STRING",
        "APPLICATIONINSIGHTS_AUTHENTICATION_STRING",
        "APPINSIGHTS_INSTRUMENTATIONKEY",
        "ApplicationInsightsAgent_EXTENSION_VERSION",
        "XDT_MicrosoftApplicationInsights_Mode",
        "APPINSIGHTS_PROFILERFEATURE_VERSION",
        "APPINSIGHTS_SNAPSHOTFEATURE_VERSION",
        // Sentry, Datadog, New Relic, Elastic APM and Honeycomb.
        "SENTRY_DSN",
        "DD_API_KEY",
        "NEW_RELIC_LICENSE_KEY",
        "ELASTIC_APM_SERVER_URL",
        "ELASTIC_APM_SERVER_URLS",
        "HONEYCOMB_API_KEY",
    ];

    /// <summary>Variable-name prefixes refused whenever such a variable is set (matched ignoring case).</summary>
    public static IReadOnlyList<string> DeniedVariablePrefixes { get; } =
    [
        // Any OpenTelemetry exporter setting (endpoint, headers, protocol and so on) and the OpenTelemetry .NET
        // automatic instrumentation.
        "OTEL_EXPORTER_",
        "OTEL_DOTNET_AUTO_",
    ];

    /// <summary>R3-09 (ruling 18): refused when set to a non-empty value.</summary>
    public const string DiagnosticPortsVariable = "DOTNET_DiagnosticPorts";

    /// <summary>R7-01: the same setting under the runtime's legacy prefix, refused the same way. The runtime reads
    /// <c>DiagnosticPorts</c> through CLRConfig, which takes <c>DOTNET_</c> first and then <c>COMPlus_</c>, so either name
    /// opens the port. (The profiler settings fall back to <c>CORECLR_</c>, not <c>COMPlus_</c>, and are covered by
    /// <see cref="ProfilerSwitches"/> and <see cref="DeniedProfilerVariablePrefixes"/>; the startup hook and the other
    /// <see cref="DeniedInjectionVariables"/> are read by the host or ASP.NET Core under the names listed, with no legacy
    /// form.)</summary>
    public const string LegacyDiagnosticPortsVariable = "COMPlus_DiagnosticPorts";

    /// <summary>
    /// Variables that load code into the process beside the app, refused whenever they are set, whatever their value
    /// (names matched ignoring case): a .NET startup hook, an ASP.NET Core hosting-startup assembly (under either prefix)
    /// and extra dependency files. These are how Application Insights, the OpenTelemetry automatic instrumentation and
    /// other agents attach to a process without a code change.
    /// </summary>
    public static IReadOnlyList<string> DeniedInjectionVariables { get; } =
    [
        "DOTNET_STARTUP_HOOKS",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
        "DOTNET_HOSTINGSTARTUPASSEMBLIES",
        "DOTNET_ADDITIONAL_DEPS",
    ];

    /// <summary>
    /// Profiler selection variables: any variable whose name starts with one of these is refused while a profiler switch
    /// (<see cref="ProfilerSwitches"/>) is on (SEC-08, Mark's ruling; matched ignoring case). They cover <c>CORECLR_PROFILER</c>, <c>CORECLR_PROFILER_PATH</c> and its
    /// <c>_32</c>/<c>_64</c>/<c>_ARM32</c>/<c>_ARM64</c> forms, and the notification-profiler list. A profiler is how the
    /// Datadog, New Relic, Dynatrace, AppDynamics, Elastic and Application Insights agents instrument a .NET process.
    /// </summary>
    public static IReadOnlyList<string> DeniedProfilerVariablePrefixes { get; } =
        ["CORECLR_NOTIFICATION_PROFILERS", "CORECLR_PROFILER", "DOTNET_NOTIFICATION_PROFILERS", "DOTNET_PROFILER"];

    /// <summary>
    /// APM agent settings, refused whenever they are set, whatever their value (names matched ignoring case). Each name is
    /// one vendor's own and configures that vendor's agent; none is a generic name an ordinary developer environment
    /// carries. In order: Datadog's agent host and trace agent URL (where the tracer sends) and its unified service tags
    /// <c>DD_ENV</c> and <c>DD_SERVICE</c> (each refused on its own, whatever <c>DD_TRACE_ENABLED</c> says: the guard is
    /// strict everywhere), New Relic's application name, and Dynatrace's tenant and connection point (where OneAgent
    /// connects). The other refused names of these families
    /// are in <see cref="DeniedVariables"/> (DD_API_KEY, NEW_RELIC_LICENSE_KEY, ELASTIC_APM_SERVER_URL and
    /// ELASTIC_APM_SERVER_URLS, HONEYCOMB_API_KEY).
    /// </summary>
    public static IReadOnlyList<string> DeniedApmAgentVariables { get; } =
        ["DD_AGENT_HOST", "DD_TRACE_AGENT_URL", "DD_ENV", "DD_SERVICE", "NEW_RELIC_APP_NAME", "DT_TENANT", "DT_CONNECTION_POINT"];

    /// <summary>
    /// APM agent setting prefixes: any variable whose name starts with one is refused whenever it is set (matched ignoring
    /// case). <c>APPDYNAMICS_</c>: every AppDynamics agent setting (controller host, account, application name and so on)
    /// starts with the vendor's name, so the prefix catches the family and nothing else.
    /// </summary>
    public static IReadOnlyList<string> DeniedApmAgentVariablePrefixes { get; } = ["APPDYNAMICS_"];

    /// <summary>APM agent switches: refused unless empty, <c>0</c> or <c>false</c> (matched ignoring case).
    /// <c>DD_PROFILING_ENABLED</c> switches on Datadog's continuous profiler.</summary>
    public static IReadOnlyList<string> ApmAgentSwitches { get; } = ["DD_PROFILING_ENABLED"];

    /// <summary>Profiler switches: refused unless empty or <c>0</c> (the runtime loads the named profiler when one is on).</summary>
    public static IReadOnlyList<string> ProfilerSwitches { get; } =
        ["CORECLR_ENABLE_NOTIFICATION_PROFILERS", "CORECLR_ENABLE_PROFILING", "DOTNET_ENABLE_NOTIFICATION_PROFILERS", "DOTNET_ENABLE_PROFILING"];

    /// <summary>SEC-08 (Mark's ruling) and R3-01: a profiler switch is OFF only when it is unset, empty, or reads as 0 the way
    /// the runtime reads it (a CLRConfig DWORD: leading blanks, an optional sign, an optional 0x, then the leading hex digits,
    /// strtoul-style). Any other value counts as ON, the safe direction whatever the runtime's exact parse: 1, 2, 10, ff,
    /// 0x2, -1 and 1x are on; true reads as 0 (t is not a hex digit) and is off.</summary>
    public static bool ProfilerSwitchOn(string? value)
    {
        string v = (value ?? "").TrimStart();
        if (v.Length == 0) return false;
        int i = 0;
        if (v[i] is '+' or '-') i++;
        if (i + 1 < v.Length && v[i] == '0' && v[i + 1] is 'x' or 'X') i += 2;
        for (; i < v.Length && Uri.IsHexDigit(v[i]); i++)
            if (v[i] != '0') return true;
        return false;
    }

    /// <summary>OpenTelemetry exporter selectors: refused unless set to <c>none</c>.</summary>
    public static IReadOnlyList<string> ExporterSelectors { get; } =
        ["OTEL_TRACES_EXPORTER", "OTEL_METRICS_EXPORTER", "OTEL_LOGS_EXPORTER"];

    private static int _installed;

    /// <summary>
    /// Run the guard for an app and exit if it refuses. Call it as the very first statement of the entry point.
    /// </summary>
    /// <param name="app">The app's name, used in the exit line (for example "GridSim.App.Desktop").</param>
    /// <param name="appAssemblyNames">The app's own assemblies, each of which must carry the marker. They are loaded by
    /// name; one that cannot be found ends the process.</param>
    /// <param name="foreignEntryAllowed">False (the desktop app): the entry assembly must carry the marker. True (the
    /// web app): an entry assembly that is not a GridSim assembly (a test runner hosting the app in process) is not
    /// checked; the app's own assemblies still are. A GridSim entry assembly is always checked.</param>
    public static void EnforceAtStartup(string app, IReadOnlyList<string> appAssemblyNames, bool foreignEntryAllowed)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(appAssemblyNames);
        // The load handler goes in first, so nothing can load unseen between the scan and the handler.
        if (Interlocked.Exchange(ref _installed, 1) == 0)
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => OnAssemblyLoad(app, e);
        string? reason = Evaluate(() => Gather(appAssemblyNames, foreignEntryAllowed));
        if (reason is not null) Exit(app, reason);
    }

    /// <summary>
    /// Gather the facts and decide, failing closed: an exception from <paramref name="gather"/> or from the decision is
    /// itself a refusal. Returns null to carry on, or the reason to exit.
    /// </summary>
    public static string? Evaluate(Func<TelemetryGuardFacts> gather)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(gather);
            return Decide(gather());
        }
#pragma warning disable CA1031 // Fail closed: whatever went wrong inside the guard, the answer is "exit".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return InternalFailure(ex);
        }
    }

    /// <summary>The decision over a set of facts: null to carry on, or the first reason to exit.</summary>
    public static string? Decide(TelemetryGuardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Problems.Count > 0) return facts.Problems[0];
        if (facts.Markers.Count == 0) return "no app assembly was checked for the build marker";
        foreach (MarkerFact m in facts.Markers)
            if (CheckMarker(m.Assembly, m.Values) is { } bad) return bad;
        foreach (string name in facts.LoadedAssemblies)
            if (CheckLoadedAssembly(name) is { } bad) return bad;
        if (CheckEnvironment(facts.Environment) is { } env) return env;
        if (facts.Web is { } web && CheckWebPolicy(web.ConnectSrc, web.ScriptOrigins) is { } policy) return policy;
        return null;
    }

    /// <summary>The marker check for one assembly: exactly one or more <c>TelemetryOff</c> values, all exactly "true".</summary>
    public static string? CheckMarker(string assembly, IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return $"assembly {assembly} has no {MarkerKey} build marker";
        foreach (string? v in values)
            if (!string.Equals(v, MarkerValue, StringComparison.Ordinal))
                return $"assembly {assembly} has {MarkerKey}={OneLine(v ?? "(null)")}, not {MarkerValue}";
        return null;
    }

    /// <summary>The denylist check for one assembly name: the reason when it is a telemetry assembly, else null.</summary>
    public static string? CheckLoadedAssembly(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (string prefix in DeniedAssemblyPrefixes.Concat(DeniedAgentAssemblyPrefixes))
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || (name.Length > prefix.Length && name[prefix.Length] == '.' && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return $"telemetry assembly {name} is loaded (denied prefix {prefix})";
        return null;
    }

    /// <summary>
    /// The environment check. Refused: any variable in <see cref="DeniedVariables"/>; any variable starting with one of
    /// <see cref="DeniedVariablePrefixes"/>; an <see cref="ExporterSelectors"/> variable set to anything but <c>none</c>;
    /// <c>DD_TRACE_ENABLED</c> set to <c>true</c> or <c>1</c>; <c>AVALONIA_TELEMETRY_OPTOUT</c> set to anything but
    /// <c>1</c> or <c>true</c>; any variable in <see cref="DeniedInjectionVariables"/> or starting with one of
    /// <see cref="DeniedProfilerVariablePrefixes"/>; a <see cref="ProfilerSwitches"/> variable set to anything but empty or
    /// <c>0</c>; any variable in <see cref="DeniedApmAgentVariables"/> or starting with one of
    /// <see cref="DeniedApmAgentVariablePrefixes"/>; an <see cref="ApmAgentSwitches"/> variable set to anything but empty,
    /// <c>0</c> or <c>false</c>. Names are matched ignoring case; the first refused name in ordinal order is reported.
    /// </summary>
    public static string? CheckEnvironment(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        List<KeyValuePair<string, string?>> ordered = variables.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        // SEC-08 (Mark's ruling): the variables that name a profiler are refused only while a profiler switch is on. A
        // machine that has CORECLR_PROFILER / _PATH_64 set system-wide with profiling off (left by security agents, APM
        // installers, IDE tooling) loads no profiler, and used to be unable to start any GridSim command.
        bool profilingOn = ordered.Any(v => ProfilerSwitches.Any(s => v.Key.Equals(s, StringComparison.OrdinalIgnoreCase)) && ProfilerSwitchOn(v.Value));
        foreach (KeyValuePair<string, string?> kv in ordered)
        {
            string name = kv.Key;
            string value = (kv.Value ?? "").Trim();
            if (DeniedVariables.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is set (it switches on a telemetry exporter)";
            if (DeniedVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is set (it configures a telemetry exporter)";
            if (ExporterSelectors.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))
                && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return $"environment variable {name} is '{OneLine(value)}', not none";
            if (name.Equals("DD_TRACE_ENABLED", StringComparison.OrdinalIgnoreCase)
                && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"))
                return $"environment variable {name} is '{OneLine(value)}' (it switches on Datadog tracing)";
            if (name.Equals("AVALONIA_TELEMETRY_OPTOUT", StringComparison.OrdinalIgnoreCase)
                && !(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is '{OneLine(value)}', not 1 or true";
            if (DeniedInjectionVariables.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is set (it loads code into the process)";
            // R3-09 (ruling 18): a diagnostic port named in DOTNET_DiagnosticPorts lets a profiler or tracer attach at
            // start-up; a non-empty value is refused. Runtime attach through the default port is a local, user-initiated
            // action, not telemetry, so System.Runtime.EnableDiagnostics is left as it is. R7-01: the runtime reads the
            // legacy COMPlus_DiagnosticPorts too, so it is refused the same way.
            if ((name.Equals(DiagnosticPortsVariable, StringComparison.OrdinalIgnoreCase)
                 || name.Equals(LegacyDiagnosticPortsVariable, StringComparison.OrdinalIgnoreCase)) && value.Length > 0)
                return $"environment variable {name} is '{OneLine(value)}' (it opens a diagnostic port a profiler or tracer can attach through)";
            if (ProfilerSwitches.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)) && ProfilerSwitchOn(value))
                return $"environment variable {name} is '{OneLine(value)}' (it switches on a profiler)";
            if (profilingOn && DeniedProfilerVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is set and a profiler switch is on (it names a profiler to load into the process)";
            if (DeniedApmAgentVariables.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase))
                || DeniedApmAgentVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is set (it configures an APM agent)";
            if (ApmAgentSwitches.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))
                && !(value.Length == 0 || value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)))
                return $"environment variable {name} is '{OneLine(value)}', not empty, 0 or false (it switches on an APM agent)";
        }
        return null;
    }


    /// <summary>
    /// The web response policy: the Content-Security-Policy connect-src directive must be exactly
    /// <c>connect-src 'self'</c> (spacing aside), and no third-party script origin may be configured.
    /// </summary>
    public static string? CheckWebPolicy(string? connectSrc, IReadOnlyList<string> scriptOrigins)
    {
        ArgumentNullException.ThrowIfNull(scriptOrigins);
        string[] tokens = (connectSrc ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2 || tokens[0] != "connect-src" || tokens[1] != "'self'")
            return $"the Content-Security-Policy connect-src is '{OneLine(connectSrc ?? "")}', not connect-src 'self'";
        foreach (string origin in scriptOrigins)
            if (!string.IsNullOrWhiteSpace(origin))
                return $"a third-party script origin is configured ({OneLine(origin.Trim())})";
        return null;
    }

    /// <summary>
    /// The web policy check, failing closed: null to carry on, or the reason to exit (an exception from
    /// <paramref name="policy"/> is itself a refusal).
    /// </summary>
    public static string? EvaluateWebPolicy(Func<TelemetryWebPolicy> policy)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(policy);
            TelemetryWebPolicy p = policy();
            ArgumentNullException.ThrowIfNull(p);
            return CheckWebPolicy(p.ConnectSrc, p.ScriptOrigins);
        }
#pragma warning disable CA1031 // Fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return InternalFailure(ex);
        }
    }

    /// <summary>
    /// The web host's second check, run as soon as its configuration is read: exit with <see cref="ExitCode"/> when the
    /// policy refuses (<see cref="EvaluateWebPolicy"/>).
    /// </summary>
    public static void EnforceWebPolicy(string app, Func<TelemetryWebPolicy> policy)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (EvaluateWebPolicy(policy) is { } reason) Exit(app, reason);
    }

    /// <summary>The reason given when the guard itself fails.</summary>
    public static string InternalFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return $"the telemetry guard itself failed ({ex.GetType().Name}: {OneLine(ex.Message)})";
    }

    /// <summary>The one line written before exiting.</summary>
    public static string ExitLine(string app, string reason) =>
        $"{app}: telemetry guard: {OneLine(reason)}. Exiting with code {ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";

    // Facts from the running process. Any failure here (an assembly that will not load, an unreadable environment)
    // throws, and Evaluate turns that into a refusal.
    private static TelemetryGuardFacts Gather(IReadOnlyList<string> appAssemblyNames, bool foreignEntryAllowed)
    {
        var problems = new List<string>();
        var markers = new List<MarkerFact>();
        Assembly? entry = Assembly.GetEntryAssembly();
        if (entry is null)
            problems.Add("the entry assembly cannot be identified");
        else
        {
            string entryName = entry.GetName().Name ?? "";
            bool gridSim = entryName.StartsWith("GridSim.", StringComparison.Ordinal);
            if (gridSim || !foreignEntryAllowed) markers.Add(MarkerOf(entry));
        }
        foreach (string name in appAssemblyNames)
            markers.Add(MarkerOf(Assembly.Load(new AssemblyName(name))));

        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToList();

        var env = new List<KeyValuePair<string, string?>>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env.Add(new KeyValuePair<string, string?>((string)e.Key, e.Value as string));

        return new TelemetryGuardFacts(markers, loaded, env, null, problems);
    }

    private static MarkerFact MarkerOf(Assembly a) =>
        new(a.GetName().Name ?? "(unnamed)",
            a.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(m => string.Equals(m.Key, MarkerKey, StringComparison.Ordinal))
                .Select(m => m.Value).ToList());

    private static void OnAssemblyLoad(string app, AssemblyLoadEventArgs e)
    {
        string? reason;
        try { reason = CheckLoadedAssembly(e.LoadedAssembly.GetName().Name); }
#pragma warning disable CA1031 // Fail closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            reason = InternalFailure(ex);
        }
        if (reason is not null) Exit(app, reason);
    }

    // The thin exit wrapper: one line to stderr and the trace log, then the dedicated exit code. No dialog.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Exit(string app, string reason)
    {
        string line = ExitLine(app, reason);
        try { Console.Error.WriteLine(line); }
#pragma warning disable CA1031 // The exit must happen even if the error stream is gone.
        catch (Exception) { }
#pragma warning restore CA1031
        try { Trace.WriteLine(line); Trace.Flush(); }
#pragma warning disable CA1031
        catch (Exception) { }
#pragma warning restore CA1031
        Environment.Exit(ExitCode);
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>The marker values found on one assembly.</summary>
public sealed record MarkerFact(string Assembly, IReadOnlyList<string?> Values);

/// <summary>The web response policy the guard checks: the CSP connect-src directive and the configured script origins.</summary>
public sealed record TelemetryWebPolicy(string? ConnectSrc, IReadOnlyList<string> ScriptOrigins);

/// <summary>Everything <see cref="TelemetryGuard.Decide"/> looks at.</summary>
/// <param name="Markers">The marker values of the entry and app assemblies.</param>
/// <param name="LoadedAssemblies">The simple names of the loaded assemblies.</param>
/// <param name="Environment">The process environment.</param>
/// <param name="Web">The web policy, or null when not checked.</param>
/// <param name="Problems">Anything that went wrong while gathering (the first is the reason to exit).</param>
public sealed record TelemetryGuardFacts(
    IReadOnlyList<MarkerFact> Markers,
    IReadOnlyList<string> LoadedAssemblies,
    IEnumerable<KeyValuePair<string, string?>> Environment,
    TelemetryWebPolicy? Web,
    IReadOnlyList<string> Problems);
