using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The APM agent families the telemetry guard refuses through the environment: Datadog, New Relic, Dynatrace,
/// AppDynamics, Elastic APM and Honeycomb. Each refused name is one vendor's own, so an ordinary developer environment
/// does not trip the guard; the "carries on" cases pin that, including names that only resemble a refused one. The
/// guard is strict everywhere (Mark's ruling): Datadog's DD_ENV and DD_SERVICE are each refused on their own, whatever
/// DD_TRACE_ENABLED says.
/// <para>Every case drives the pure decision (<see cref="TelemetryGuard.Decide"/> or
/// <see cref="TelemetryGuard.CheckEnvironment"/>) over made-up facts; the test host's environment is never changed. The
/// expected reasons are the guard's documented texts (technical ch. 35, section Telemetry), written out in full.</para>
/// </summary>
public sealed class TelemetryGuardApmAgentTests
{
    private static TelemetryGuardFacts Clean(IEnumerable<KeyValuePair<string, string?>> env) =>
        new([new MarkerFact("gridsim", ["true"]), new MarkerFact("GridSim.Core", ["true"])],
            ["System.Private.CoreLib", "GridSim.Core"],
            env,
            null,
            []);

    private static IEnumerable<KeyValuePair<string, string?>> Env(params (string Name, string? Value)[] vars) =>
        new KeyValuePair<string, string?>[] { new("PATH", "/usr/bin") }.Concat(vars.Select(v => new KeyValuePair<string, string?>(v.Name, v.Value)));

    private static string Configures(string name) => $"environment variable {name} is set (it configures an APM agent)";

    // ---- The documented lists -----------------------------------------------------------------------------------------

    [Fact]
    public void The_apm_agent_lists_are_the_documented_ones()
    {
        Assert.Equal(new[] { "DD_AGENT_HOST", "DD_TRACE_AGENT_URL", "DD_ENV", "DD_SERVICE", "NEW_RELIC_APP_NAME", "DT_TENANT", "DT_CONNECTION_POINT" },
            TelemetryGuard.DeniedApmAgentVariables);
        Assert.Equal(new[] { "APPDYNAMICS_" }, TelemetryGuard.DeniedApmAgentVariablePrefixes);
        Assert.Equal(new[] { "DD_PROFILING_ENABLED" }, TelemetryGuard.ApmAgentSwitches);
    }

    [Fact]
    public void The_families_already_on_the_exporter_list_stay_there()
    {
        // Elastic APM and Honeycomb are covered by their server URL and API key, and New Relic and Datadog by their keys,
        // on the exporter list (TelemetryGuardTests); the APM families here add to that list, they do not replace it.
        foreach (string v in new[] { "DD_API_KEY", "NEW_RELIC_LICENSE_KEY", "ELASTIC_APM_SERVER_URL", "ELASTIC_APM_SERVER_URLS", "HONEYCOMB_API_KEY" })
            Assert.Contains(v, TelemetryGuard.DeniedVariables);
    }

    [Fact]
    public void Chapter_35_names_every_refused_apm_variable()
    {
        string ch35 = File.ReadAllText(Path.Combine(CliProcess.Root(), "docs", "technical", "0035 Deployment and security.md"));
        foreach (string v in TelemetryGuard.DeniedApmAgentVariables.Concat(TelemetryGuard.ApmAgentSwitches))
            Assert.Contains("`" + v + "`", ch35, StringComparison.Ordinal);
        foreach (string p in TelemetryGuard.DeniedApmAgentVariablePrefixes)
            Assert.Contains("`" + p + "`", ch35, StringComparison.Ordinal);
    }

    // ---- Refused --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_listed_apm_variable_exits_whatever_its_value()
    {
        foreach (string v in TelemetryGuard.DeniedApmAgentVariables)
            foreach (string? value in new[] { "x", "", "0", "false", "none", null })
            {
                Assert.Equal(Configures(v), TelemetryGuard.CheckEnvironment(Env((v, value))));
                Assert.Equal(Configures(v.ToLowerInvariant()), TelemetryGuard.CheckEnvironment(Env((v.ToLowerInvariant(), value))));
            }
    }

    [Theory]
    [InlineData("DD_AGENT_HOST", "localhost")]
    [InlineData("DD_TRACE_AGENT_URL", "http://localhost:8126")]
    [InlineData("DD_ENV", "prod")]
    [InlineData("DD_SERVICE", "gridsim")]
    [InlineData("dd_env", "dev")]
    [InlineData("NEW_RELIC_APP_NAME", "GridSim")]
    [InlineData("DT_TENANT", "abc12345")]
    [InlineData("DT_CONNECTION_POINT", "https://abc12345.live.dynatrace.example/communication")]
    [InlineData("APPDYNAMICS_CONTROLLER_HOST_NAME", "controller.example")]
    [InlineData("APPDYNAMICS_AGENT_ACCOUNT_NAME", "customer1")]
    [InlineData("APPDYNAMICS_AGENT_APPLICATION_NAME", "GridSim")]
    [InlineData("appdynamics_agent_tier_name", "web")]
    [InlineData("APPDYNAMICS_", "")]
    public void An_apm_agent_setting_exits(string name, string value)
    {
        Assert.Equal(Configures(name), TelemetryGuard.Decide(Clean(Env((name, value)))));
    }

    [Theory]
    [InlineData("DD_PROFILING_ENABLED", "true")]
    [InlineData("DD_PROFILING_ENABLED", "1")]
    [InlineData("DD_PROFILING_ENABLED", "auto")]
    [InlineData("DD_PROFILING_ENABLED", " TRUE ")]
    [InlineData("dd_profiling_enabled", "yes")]
    public void The_datadog_profiler_switched_on_exits(string name, string value)
    {
        Assert.Equal($"environment variable {name} is '{value.Trim()}', not empty, 0 or false (it switches on an APM agent)",
            TelemetryGuard.Decide(Clean(Env((name, value)))));
    }

    [Theory]
    [InlineData("DD_ENV", null)]        // tracing at its default
    [InlineData("DD_ENV", "false")]     // tracing switched off: still refused, the guard is strict everywhere
    [InlineData("DD_ENV", "0")]
    [InlineData("DD_SERVICE", null)]
    [InlineData("DD_SERVICE", "false")]
    [InlineData("DD_SERVICE", "0")]
    public void Each_datadog_service_tag_alone_exits_whatever_the_tracing_switch_says(string name, string? traceEnabled)
    {
        // Ordinal order: DD_ENV and DD_SERVICE sort before DD_TRACE_ENABLED, so the tag is the name reported.
        var vars = new List<(string, string?)> { (name, "x") };
        if (traceEnabled is not null) vars.Add(("DD_TRACE_ENABLED", traceEnabled));
        Assert.Equal(Configures(name), TelemetryGuard.Decide(Clean(Env(vars.ToArray()))));
    }

    // ---- Carries on: an ordinary developer environment --------------------------------------------------------------------

    [Theory]
    [InlineData("DD_PROFILING_ENABLED", "false")]
    [InlineData("DD_PROFILING_ENABLED", "FALSE")]
    [InlineData("DD_PROFILING_ENABLED", "0")]
    [InlineData("DD_PROFILING_ENABLED", "")]
    [InlineData("DD_PROFILING_ENABLED", null)]
    [InlineData("DDENV", "prod")]
    [InlineData("MY_DD_ENV", "prod")]
    [InlineData("DD_ENVIRONMENT", "prod")]
    [InlineData("DDSERVICE", "x")]
    [InlineData("MY_DD_SERVICE", "x")]
    [InlineData("DD_SERVICE_NAME", "x")]
    [InlineData("DD_AGENT_HOSTNAME", "x")]
    [InlineData("DT", "x")]
    [InlineData("DT_TENANTS", "x")]
    [InlineData("DT_HOME", "/opt/dt")]
    [InlineData("NEW_RELIC", "x")]
    [InlineData("NEW_RELIC_APP_NAMES", "x")]
    [InlineData("APPDYNAMICS", "x")]
    [InlineData("MY_APPDYNAMICS_HOST", "x")]
    [InlineData("ELASTIC_PASSWORD", "x")]
    [InlineData("HONEYCOMB_DATASET", "x")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT", "1")]
    [InlineData("TERM", "xterm-256color")]
    [InlineData("HOME", "/home/dev")]
    public void An_ordinary_or_merely_similar_setting_carries_on(string name, string? value)
    {
        Assert.Null(TelemetryGuard.Decide(Clean(Env((name, value)))));
    }

    [Fact]
    public void No_workflow_sets_an_apm_agent_variable()
    {
        // As TelemetryGuardTests checks for the exporter variables: CI would otherwise start every app into exit 78.
        string dir = Path.Combine(CliProcess.Root(), ".github", "workflows");
        foreach (string path in Directory.GetFiles(dir, "*.yml"))
        {
            string yml = File.ReadAllText(path);
            foreach (string v in TelemetryGuard.DeniedApmAgentVariables.Concat(TelemetryGuard.ApmAgentSwitches))
                Assert.DoesNotContain(v + ":", yml, StringComparison.OrdinalIgnoreCase);
            foreach (string p in TelemetryGuard.DeniedApmAgentVariablePrefixes)
                Assert.DoesNotContain(p, yml, StringComparison.OrdinalIgnoreCase);
        }
    }
}
