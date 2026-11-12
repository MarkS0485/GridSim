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
- Machine-epsilon validated: orchestration-only refactors leave every number bit-for-bit unchanged
  (prove with exact `Assert.Equal`); new numerics on a validated path need re-validation.
- `dotnet build GridSim.slnx -c Release` gives 0 warnings (TreatWarningsAsErrors), and
  `dotnet test GridSim.slnx -c Release --no-build` stays green.
- Fail loud: non-converged or invalid results must never be reported as success.

## Build telemetry

Build telemetry is off. The Avalonia packages bring in Avalonia.BuildServices, whose `AvaloniaStats` build target sends
anonymous build statistics; `Directory.Build.targets` replaces that target with an empty one, so no build of this
repository runs it. The CI workflows also set `AVALONIA_TELEMETRY_OPTOUT=1`, Avalonia's own switch; set it in your
shell as well if you build Avalonia projects outside this repository. Do not remove either.

## Conventions

- Per change: code + tests + docs + CHANGELOG entry (ascending, newest at bottom) + commit.
- CHANGELOG entries are a user-facing changelog (Keep a Changelog: Added / Changed / Deprecated / Removed / Fixed /
  Security). Short, impersonal bullets that say what changed for users. A test change is recorded as a brief
  "Tests: ..." bullet.
- Version: 2.1.N. 2.1.000 is the RC-1 release (2026-10-31); the RC label appears only in the git tag and the release
  title. Each commit bumps N with a new CHANGELOG heading `## [2.1.N]`. The single `<Version>` lives in
  `Directory.Build.props` (no csproj sets its own; `VersionContractTests` checks it and the CHANGELOG heading).
- Commit subjects in plain English.
- Only `main` exists. Any other branch is merged or discarded and then deleted.
