# Contributing to GridSim

Solution `GridSim.slnx`.

- `GridSim.Core` (the engine) and `GridSim.Cli` (the `gridsim` command line tool) hold all capability.
- `GridSim.Gda` (the GDA data-lake bridge) and `GridSim.Gpu` (the GPU backends) plug into the engine.
- `GridSim.Web`, `GridSim.Wpf`, `GridSim.App`, `GridSim.App.Desktop`, `GridSim.Ui` and `GridSim.SchemaEditor` are
  presentation only.
- Tests live in `tests/GridSim.Tests` and `tests/GridSim.App.Tests`.

## Acceptance criteria for every change

**Machine epsilon is the point of GridSim.** Bit-identical results are mandatory: across runs, across
parallel paths, across CPUs (AVX-512 / AVX2 / non-FMA) and between the dense and sparse solvers. The only
exception is a mathematical proof that a reference implementation elsewhere is wrong.

**The tests are constitutional.** Never edit, loosen, re-baseline or delete a test to make a change pass.
A change that breaks a test is wrong. A test may change only with written mathematical justification,
agreed with the maintainer first and recorded in the commit and CHANGELOG. Adding new tests is always fine.
The RAM gate (`tests/CONDITIONAL-TESTING.md`) is not a loosening: a large-case test the machine
cannot hold is reported Skipped by installed RAM, with its assertions unchanged; releases run it (`GRIDSIM_TEST_MAX_BUSES=all`).

- Deterministic: identical results across runs, including parallel paths.
- Machine-epsilon pinned: orchestration-only refactors leave every number bit-for-bit unchanged
  (prove with exact `Assert.Equal`); new numerics on a cross-checked path need the cross-checks re-run.
- A deliberate change to pinned numbers is recorded in CHANGELOG.md and the commit as **BREAKING CHANGE**, followed by
  the size of the difference (2.1.073 is the example).
- `dotnet build GridSim.slnx -c Release` gives 0 warnings (TreatWarningsAsErrors), and
  `dotnet test GridSim.slnx -c Release --no-build` stays green.
- Fail loud: non-converged or invalid results must never be reported as success.

## Build telemetry

Build telemetry is off. The Avalonia packages bring in Avalonia.BuildServices, whose `AvaloniaStats` build target sends
anonymous build statistics; `Directory.Build.targets` replaces that target with an empty one, so no build of this
repository runs it. The CI workflows also set `AVALONIA_TELEMETRY_OPTOUT=1`, Avalonia's own switch; set it in your
shell as well if you build Avalonia projects outside this repository. Do not remove either.

The same file stamps every assembly with the marker `TelemetryOff=true`. At start-up every app (the Avalonia desktop
app, the WPF dashboard, the `gridsim` CLI, the web app, the case editor and `gridsim-script`) runs
`GridSim.Core.Diagnostics.TelemetryGuard` before
anything else; it exits with code 78, writing one line that names the reason to the error stream and the trace log,
when an app assembly lacks the marker, a telemetry library or injected agent (Application Insights, an OpenTelemetry
exporter or its automatic instrumentation, Azure Monitor, Sentry, Dynatrace, AppDynamics and the others on its
denylists) is loaded now or later, an environment variable would switch on an exporter (`OTEL_EXPORTER_*`,
`APPLICATIONINSIGHTS_CONNECTION_STRING`, `SENTRY_DSN`, `DD_API_KEY` and the rest), configure an APM agent
(`DD_AGENT_HOST`, `DD_ENV`, `DD_SERVICE`, `NEW_RELIC_APP_NAME`, `DT_TENANT`, `APPDYNAMICS_*` and the rest) or load an agent
(`DOTNET_STARTUP_HOOKS`, `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`, `CORECLR_ENABLE_PROFILING=1`, `CORECLR_PROFILER*` and
the rest), `AVALONIA_TELEMETRY_OPTOUT` is set to anything but 1 or true, or the guard itself fails. So `dotnet watch`,
Hot Reload and a profiler-based coverage tool make the apps, and the tests that host the web app or run the CLI, exit
with code 78; use `dotnet run`, and coverlet if you want coverage. Neither Avalonia nor ASP.NET Core sends telemetry at run time by default; the guard keeps it that way. It
checks what is loaded and configured, not network traffic. Never add a telemetry package to any project:
`TelemetryGuardTests` reads every app and web package graph and fails if one appears. The full lists are in technical
ch. 35, section Telemetry.

### Developing with the telemetry guard

The guard is strict everywhere, by Mark's ruling: it has no developer mode and no switch that loosens it, in a Debug
build as in a release. So these make every GridSim app exit with code 78, by design:

- `dotnet watch` and Hot Reload, from the command line or an IDE: they load their change applier through
  `DOTNET_STARTUP_HOOKS`;
- profiler-based coverage and performance tools (dotnet-coverage in its dynamic mode, dotCover, dotTrace and the
  like): they set `CORECLR_ENABLE_PROFILING` and `CORECLR_PROFILER*`;
- an IDE's browser refresh or Browser Link for the web app: it loads a hosting-startup assembly through
  `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`;
- an APM agent on your machine whose variables are in your shell (Datadog, New Relic, Dynatrace, AppDynamics and the
  others above).

Work without them:

- Edit, then `dotnet build GridSim.slnx` and start the app with `dotnet run --project <project>` (or the built
  executable). Stop it and run it again to see a change; there is no live reload.
- In an IDE, start or debug the app with Hot Reload (and, for the web app, browser refresh) switched off in the IDE's
  settings. A plain debug session (breakpoints, stepping, watches) needs none of the refused variables; if the app
  still exits, the line names the variable the IDE set.
- Run the tests with `dotnet test`, not `dotnet watch test`; the tests that host the web app or start the CLI run the
  real guard, and `TelemetryGuardAllAppsTests.The_test_host_itself_passes_the_environment_check` names the variable at
  fault when the shell carries one.
- For coverage, use a tool that instruments the assemblies at build time instead of loading a profiler (coverlet does);
  none is part of this repository.
- If the exit line names a variable your shell sets for other work (an APM agent's, say), unset it for the GridSim
  shell; the line says which.

## Conventions

- Per change: code + tests + docs + CHANGELOG entry (ascending, newest at bottom) + commit.
- CHANGELOG entries are a user-facing changelog (Keep a Changelog: Added / Changed / Deprecated / Removed / Fixed /
  Security). Short, impersonal bullets that say what changed for users. A test change is recorded as a brief
  "Tests: ..." bullet.
- Version: 2.1.N. 2.1.000 was RC-1 (2026-10-31), 2.1.031 RC-2 and 2.1.071 RC-3; the RC label appears only in the git
  tag and the release title. Each commit bumps N with a new CHANGELOG heading `## [2.1.N]`. The single `<Version>` lives in
  `Directory.Build.props` (no csproj sets its own; `VersionContractTests` checks it and the CHANGELOG heading).
- Commit subjects in plain English.
- Only `main` exists. Any other branch is merged or discarded and then deleted.
