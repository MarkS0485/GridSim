# 3. Configuration

This chapter lists every setting that changes GridSim's behaviour from outside the code: the environment variables the
code reads, the settings files of the desktop and web apps, the licence file, and the folders GridSim reads data from
and writes results to. Use it when a run reads or writes in the wrong place, when you deploy the web app, or when you
need to force a CPU mode. Command line flags are not repeated here; they are in the
[CLI reference](<0019 CLI reference.md>).

The command line tool and the web host pin `InvariantCulture` at process start, so number parsing and every CSV and
JSON output are locale-independent: a machine with a comma decimal separator reads `--dt-ms 0.5` as 0.5 and writes
`1.2340`, not `1,2340`. Neither needs `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`. The desktop app (`GridSim.Wpf`) does not
set the process culture and uses the machine's.

## Environment variables the code reads

These are all the variables the product source reads. The table covers the product (command line tool, desktop app, web
app, case editor) first; the test harness and the build tools follow. The container deployment reads further
`GRIDSIM_*` variables through `compose.yml`, which are listed in its header and in `docs/developer/deploy/README.md`.

| Variable | Default | Read by | Effect |
|---|---|---|---|
| `GDA_DATA_ROOT` | unset | the `gda` verb (`GridSim.Gda`), `tools/crossval/lake_conformance.py` | Path to the read-only GDA data lake, the fallback for `--root`. An explicit `--root` wins. Set but not an existing folder: `gda` exits 1 naming the variable (its value is never printed) and does not fall back to `GDA_ROOT`. |
| `GDA_ROOT` | unset | the same two | Second fallback for `--root`, read only when `GDA_DATA_ROOT` is unset or empty; the same missing-folder rule applies. With no `--root` and neither variable set, `gda` prints its usage and exits 2. |
| `GRIDSIM_LICENCE` | unset | licence resolution in the command line tool, the desktop app, the web app and the case editor | A `GSK1 ...` licence key for one run. Order: an explicit key (web `Licence:Key`), then this variable, then the installed licence file, then the free Personal tier. A key that is present but rejected leaves the run at the free tier; `gridsim licence status` shows why. |
| `GRIDSIM_CRASH_DIR` | unset | the command line tool and the desktop app (`CrashLog`) | The folder an unhandled-exception crash log is written to. Unset, the log goes to `GridSim/crash` under the per-user data folder (`%LOCALAPPDATA%\GridSim\crash` on Windows, `~/.local/share/GridSim/crash` on Linux, `~/Library/Application Support/GridSim/crash` on macOS), and to the system temporary folder if that cannot be created (on Linux and macOS the log is created owner-only there). |
| `ROCM_PATH` | `/opt/rocm` | the ROCm GPU interop (`GridSim.Gpu`) | The root of the ROCm install: the native libraries are also searched under `<ROCM_PATH>/lib`. |
| `GRIDSIM_PYTHON` | unset | `gridsim crossval` (`PythonLocator`) | The Python interpreter the sidecar runs with: a path, or a name looked up on PATH. When set, that one and no other; if it names nothing that exists, the run fails. |
| `VIRTUAL_ENV` | unset | `gridsim crossval` (`PythonLocator`) | Read when `GRIDSIM_PYTHON` is unset: the active virtual environment's own interpreter (`Scripts\python.exe` on Windows, `bin/python` elsewhere) is used when it exists; otherwise the PATH is searched. |

GPU selection has no variable of its own: it is the `--gpu[=rocm|opencl|cuda]` flag, resolved AMD-first (ROCm, then
OpenCL, then CUDA); with auto-detect a missing device warns and falls back to the CPU, and a named vendor that is
missing exits 2 ([ch. 28](<0028 Benchmarking and GPU.md>)).

The test project reads a few more, for tests and capture tooling only:

| Variable | Read by | Effect |
|---|---|---|
| `GRIDSIM_PUBLISHED_DIR` | `PublishedOutputTests` | Names a folder produced by `dotnet publish` with a release profile; the published-output tests run against it. Unset, they are reported as skipped. |
| `GRIDSIM_EDITOR_CAPTURES` | `EditorScreenCaptureTests` | A folder to write the case editor's screens to as text. Unset, nothing is written. |
| `EMT_REPORT_DIR`, `EMT_SUMMARY_DIR` | `EmtOutOfPhaseSyncTests` | Folders for the out-of-phase synchronisation CSV and summary. Unset, the summary goes to the system temporary folder. The CSV's default folder is one on the author's machine; where it cannot be created the CSV falls back to the system temporary folder, and where it can the test writes outside the repository, so set `EMT_REPORT_DIR` to a scratch folder. |
| `DOTNET_HOST_PATH` | the test process launcher | The `dotnet` host the tests start the command line tool with; the .NET SDK sets it. |

`tools/docs/capture.ps1` expands an argument that is or starts with `$NAME` (`$GDA_ROOT`, or a path such as
`"$GDA_ROOT/some folder/file.csv"`, which is double-quoted because it holds spaces; `$env:NAME` is not recognised) to
the value of that environment variable for the run, and writes `$NAME` back into the capture in place of the value, so
capture scripts can name a variable without printing it. A variable that is not set stops the script. `-OutDir <folder>`
writes the captures under another folder than `docs/_captures`, so captures of private data can be checked before any
is copied into the docs.

## .NET runtime variables

The .NET runtime reads these before GridSim starts. GridSim does not read them itself, but its documentation relies on
them.

| Variable | Effect |
|---|---|
| `DOTNET_gcServer`, `DOTNET_gcConcurrent` | Override the command line tool's garbage-collector mode without a rebuild. The tool is built for Server GC with concurrent collection; every report header stamps the active mode as `GC=Server` or `GC=Workstation`. |
| `DOTNET_GCHeapHardLimit` and `DOTNET_GCHeapHardLimitPercent` | Cap the managed heap so a large `bench` sweep fails with a catchable out-of-memory error instead of exhausting the machine. `tools/run-regime.sh` and `tools/run-gpu.sh` set `DOTNET_GCHeapHardLimit=0xC80000000` (50 GiB). |
| `DOTNET_PreferredVectorBitWidth=512` | Enables the 8-wide AVX-512 kernels on CPUs where .NET caps the width at 256 bits (Skylake-SP and Cascade Lake Xeons). `gridsim simdcheck` prints this exact fix when the CPU supports AVX-512F but the active tier is narrower. |
| `DOTNET_EnableAVX2=0` | Drops the process to the 128-bit SSE kernels. The CI mode `avx2-off` uses it. |
| `DOTNET_EnableHWIntrinsic=0` | Drops the process to the scalar kernels. The CI mode `hwintrinsic-off` uses it. |
| `ASPNETCORE_URLS` | The web app's binding addresses and ports (or `--urls` on its command line). |
| `ASPNETCORE_ENVIRONMENT` | `Development` enables the developer exception page and would layer `appsettings.Development.json` (none ships); any other value uses the `/Error` page, and `Production` also layers `appsettings.Production.json` (replay-only, see below). |

### CPU vector tier

The dense-LU, normal-equations and covariance kernels pick a vector width at run time: `Avx512` (8 doubles per step),
`Avx256` (4), `Sse128` (2) or `Scalar`. `gridsim simdcheck` reports the active tier ([CLI
reference](<0019 CLI reference.md#simdcheck>)). The tiers are meant to give the same answer: the full test suite runs in all three CPU modes and pins exact
values, and the bit-exact kernels do not use FMA because it changes the last bit
(`src/GridSim.Core/Numerics/SimdCapabilities.cs`). The pins hold in every mode, on Windows and on Linux. The variables above exist to prove that and to choose the fastest tier on a given
machine, not to change an answer.

## Worked example: overriding a setting

The vector tier shows the pattern: the variable applies to one process, and the tool reports what it did.

```bash
gridsim simdcheck
```

```
$ gridsim simdcheck
GridSim SIMD preflight
======================
CPU cores: 8   OS: Microsoft Windows 10.0.26300
process arch: X64
commit: v1.95.294-173-g203c6b9c

SIMD: Avx256 (4 doubles/step)  FMA=yes  [Avx512F=False Avx2=True Fma=True Sse2=True AdvSimd=False]

Active vector tier: Avx256 (4 doubles per vector step)
...
[exit 0]
```

Set `DOTNET_EnableAVX2=0` for one run (bash form; in PowerShell set `$env:DOTNET_EnableAVX2 = "0"`, run, then
`Remove-Item Env:DOTNET_EnableAVX2`):

```bash
DOTNET_EnableAVX2=0 gridsim simdcheck
```

```
$ DOTNET_EnableAVX2=0 gridsim simdcheck
GridSim SIMD preflight
======================
CPU cores: 8   OS: Microsoft Windows 10.0.26200
process arch: X64
commit: 11b298f1

SIMD: Sse128 (2 doubles/step)  FMA=no  [Avx512F=False Avx2=False Fma=False Sse2=True AdvSimd=False]

Active vector tier: Sse128 (2 doubles per vector step)
...
[exit 0]
```

The tier fell from `Avx256` to `Sse128` and FMA went with AVX2. The machine, core count and commit lines differ on
your machine; the override changes which kernels compute a solve, and on Windows the suite's exact-value pins hold in each mode.

A path setting is checked the same way. `GDA_DATA_ROOT` is validated before `gda` does any work, and a wrong value
stops the run with an error that names the variable but never prints what it held:

```bash
GDA_DATA_ROOT=missing-lake gridsim gda
```

```
$ GDA_DATA_ROOT=missing-lake gridsim gda
GDA root error: the environment variable GDA_DATA_ROOT is set but does not name an existing folder (its value is not printed). Point GDA_DATA_ROOT at the GDA lake folder (the one holding NationalGrid/ and NESO/), unset it, or pass --root <path>. GridSim does not fall back to GDA_ROOT while GDA_DATA_ROOT is set.
[exit 1]
```

To set a variable for a whole session on Windows, use the Environment Variables dialog or `setx NAME value` (it takes
effect in new shells), and keep keys and credentials there, never on a command line or in a file under the repository.

## Data and results paths

GridSim reads cases from a `data/` folder beside the executable and writes results to a `results/` folder in the
current directory. The two differ on purpose: `data/` travels with the build, `results/` belongs to where you run.

**Results go under `results/` in the current directory.** Every verb that writes a default output writes it there as
a relative path, so it lands wherever you ran the command: run directories (`transient-*`, `replay-<id>`,
`scenario-<id>`, `h0-study-<id>`, `fleet-<id>`, `evidence-<id>`, `gda-replay-<case>`, `estimate-<case>`, `synth/gb-synthetic`,
`estimate-noise-floor`, `crossval-<case>`, `emt-out`, `conformance`, `bench-estimate`), single files (`gridviz.json`,
`harmonic-export.json`, `impedance-stability-export.json`, `nminus1-sweep.md`, `stress.md`, `chain-<case>.md`,
`chain-storage-<case>.md`, `benchmark-results.md`, `perf-baseline.md`), and solve exports (`results/exports/<case>-solve.json` for `--enableExport`). It does not matter
whether you started the tool with `dotnet run` or from a release folder: the path is relative to the current directory,
never to the executable. Each of the six `.md` reports above (`nminus1-sweep`, `stress`, `chain`, `chain-storage`,
`benchmark-results`, `perf-baseline`) writes a `.csv` of the same name beside it. An explicit `--out` (or `--enableExport=<path>`) is used exactly as given. Each run
directory holds its own artefacts, and `run-metadata.json` for the verbs listed in
[ch. 31](<0031 Exports and formats.md>).

**Data is read beside the executable.** Case data, schemas and sample inputs resolve against the folder holding the
executable (`AppContext.BaseDirectory`), so keep `data/` and `schemas/` next to it:

| Path | Use |
|---|---|
| `data/uk` | The reduced GB 10-zone JSON case, the no-argument default model. |
| `data/gb-full`, `data/intl` | The full GB model and the starter international maps. |
| `data/matpower/` | MATPOWER `.m` files. A MATPOWER argument is resolved in this order: the path as given, if that file exists; then the path relative to this folder (for example `pegase/case1354pegase.m`); then, for a bare file name, a search through every subfolder, sorted by relative path. A bare name that matches more than one file is refused with exit 2 and a message listing every match; give the relative path (for example `dist/case33bw.m`) instead. `bench`, `conformance`, `nminus1-sweep --all` and `stress --all` scan it recursively. Add files with `tools/fetch-cases.sh`. |
| `data/gb-synthetic/` | The synthetic GB model with its asset and protection bundle, loaded by the name `gb-synthetic`. `synth` writes to `results/synth/gb-synthetic` unless `--out` names this folder. |
| `data/neso/frequency-sample.csv` | The default input of `freq` and `replay`. |
| `data/events/` | Event-catalogue JSON files (`gridsim-event-catalog/1`). |
| `schemas/` | The JSON schemas this build understands, for validating your own case files. |
| `tools/crossval/` | The pandapower sidecar of `crossval` (`crossval.py`, `sidecar_common.py`); the verb runs it from here. |

A bare name is enough for a shipped case. `case18.m` exists only in `data/matpower/dist`, and the run finds it there:

```bash
gridsim case18.m --once
```

```
$ gridsim case18.m --once
GridSim  -  case18
18 buses  17 branches  1 generators  base 10 MVA  50 Hz
========================================================================
Converged on the voltage step (11 sweep iters, 0 OLTC passes, last |dV| 3.41E-009 p.u.; max mismatch 1.37E-009 p.u., which the sweep does not test)
Generator output 0.01 GW  Demand 0.01 GW  Losses 0.3 MW  Inertia not in the data (no generator H)
...
note: case18.m: nominal frequency not given (MATPOWER files carry none); assumed 50 Hz - pass the real value if this is, for example, a 60 Hz system (gridsim: --frequency-hz 60).
note: case18.m: read mpc.gencost (1 row(s)); the costs are carried on the generators and used by dcopf / acopf when asked (--costs case); without it the OPF uses the fuel-type merit order.
[exit 0]
```

No two shipped files share a name, so no shipped name is ambiguous today. If one did, the run would print
`error: MATPOWER case name '<name>' is ambiguous: it matches N files under data/matpower: ...` and exit 2.

**The apps find run directories in a `results/` folder up to seven levels above themselves.** The desktop app's LOAD
RUN picker and the web case catalogue scan the folder holding the executable and its seven parent folders for
`results/` (and for a `gda-out/` folder of GDA replicas), so runs started from the repository root are
discovered when the apps run from `bin\Release\...`. The web catalogue scans once per process: restart the host to
pick up a new run directory.

| Location | Contents |
|---|---|
| GDA lake (`GDA_DATA_ROOT`) | Read-only. Replicas go wherever `gda --out` points; keep that folder out of the repository. |
| `%LOCALAPPDATA%\GridSim\licence.gridsim` | The installed licence ([ch. 4](<0004 Licensing and activation.md>)). |
| `%LOCALAPPDATA%\GridSim\settings.json` | The desktop app's persisted settings. |
| `%LOCALAPPDATA%\GridSim\crash\` | Crash logs. |

On Linux and macOS `%LOCALAPPDATA%` is the per-user data folder: `~/.local/share` (or `$XDG_DATA_HOME`) and
`~/Library/Application Support`.

## Desktop app (`GridSim.Wpf`)

Launch arguments, all optional:

| Argument | Example | Effect |
|---|---|---|
| `<model>` | `case9` | Boot onto the first model source whose picker label contains the text (case-insensitive). The text is the first argument that does not start with `-`; see Limits. Text that matches no label boots the first model in the picker. |
| `--zone <case>` | `--zone zone-ukpn` | Pre-select the bridge case before connecting: the first bridge case whose name contains the value (case-insensitive, so `ukpn` works). An unmatched value is ignored. |
| `--back <days>` | `--back 30` | Pre-select the bridge start offset: the value must be one of 9, 12, 14, 21, 30 or 60 (days behind now); any other value is ignored. |
| `--bridge-source <s>` | `--bridge-source estimated` | Pre-select the reconstruction source: `presolved`, `estimated` or `live` (case-insensitive). An unmatched value is ignored. |
| `--tab <name>` | `--tab OPERATOR` | Open the first tab whose header contains the text (case-insensitive). An unmatched value leaves the default tab. |

A flag takes the argument straight after it; the flag names are matched exactly as written.

Settings persist in `%LOCALAPPDATA%\GridSim\settings.json`. The load and the save are best effort: a missing or corrupt
file never stops the app, and a failed save is ignored. The one key you set by hand:

| Key | Default | Meaning |
|---|---|---|
| `BridgeEndpoint` | `""` (empty) | The GDA-to-GridSim bridge WebSocket. Non-empty adds a "Live bridge" source to the model picker; empty hides it. Example: `"BridgeEndpoint": "ws://127.0.0.1:8770/ws/bridge"` for a bridge on your own machine, with `BridgeAllowUnencrypted` set (below). |
| `BridgeAllowUnencrypted` | `false` | Allow an unencrypted `ws://` `BridgeEndpoint`. The bridge client refuses `ws://` unless this is `true`; set it only for a bridge on your own machine or a private network, and use `wss://` otherwise. |

The remaining keys (`LastSource`, `ColorVoltage`, `Fleet`, `Minimap`, `Coast`, `Boundaries`, `GspAreas`, `DnoAreas`,
`GspRegions`, `Assets`) are user-interface state the app rewrites as you use it. The app reads its licence from
`GRIDSIM_LICENCE` or the installed licence file. Tour: [ch. 8](<0008 The desktop app.md>).

## Web app (`GridSim.Web`)

`GridSim.Web` is a standard ASP.NET Core host, so the whole configuration stack applies: `appsettings.json`, then
`appsettings.<Environment>.json`, then environment variables, then the command line, later sources winning. In an
environment variable the section separator `:` is written `__` (`GridSim__ReplayOnly`). The shipped
`appsettings.Production.json` sets `GridSim:ReplayOnly` to `true`, so a Production host (the container image, the
systemd unit) is replay-only unless you set `GridSim__ReplayOnly=false`. `dotnet run --project src/GridSim.Web` uses the
`http` launch profile: the `Development` environment on `http://localhost:5180`, with the API mapped. The pages and the
API are described in [ch. 9](<0009 The web app and REST and WebSocket API.md>).

| Key | Default | Effect |
|---|---|---|
| `GridSim:BridgeEndpoint` | unset | GDA bridge WebSocket URI for `bridge:<case>[@<source>]` sessions. Unset or empty, any bridge request fails with `No GDA bridge endpoint is configured (GridSim:BridgeEndpoint).` |
| `GridSim:ReplayOnly` | `false` (`true` in Production) | `true` serves only the raw public-data replay: the solve REST API and the simulation WebSocket are not mapped, the real-time estimator stays off and the UI hides every solver surface. |
| `GridSim:ReplayCases` | `gb-full` | The remote cases whose raw replay (`bridge:<case>@raw`) a replay-only instance serves, comma-separated; the home page opens the first. A malformed or repeated name stops start-up. |
| `GridSim:MaxSessions` | `64` | The most sessions (dashboards plus `/ws/sim` connections) held at once. A value that is not a whole number of 1 or more stops start-up. |
| `GridSim:TrustedProxies` | `loopback` (`loopback,private` in `compose.yml`) | The proxies whose `X-Forwarded-For` and `X-Forwarded-Proto` headers are believed: `loopback`, `private`, addresses and CIDR networks, comma-separated, or `any` alone. A malformed value stops start-up. |
| `Licence:Key` | unset | A licence key; it takes precedence over `GRIDSIM_LICENCE` and the installed file. |
| `Realtime:Enabled`, `Realtime:Case`, `Realtime:WindowsDir` | `false`, `case9`, empty | `Realtime:Enabled=true` runs the resident real-time estimator once at start-up on `Realtime:Case`, over the measurement-window folder `Realtime:WindowsDir`. Ignored when `ReplayOnly` is set. |
| `Audit:LogPath`, `Audit:AnchorPath` | unset, `<log>.anchor` | Record the resident estimator's run in a tamper-evident audit log and keep its anchor ([ch. 26](<0026 Audit log, deployment and assurance.md>)). The log is opened and verified against its anchor at start-up. `Audit:LogPath` on a host with no resident estimator, or `Audit:AnchorPath` without `Audit:LogPath`, stops start-up. |
| `Auth:KeyProtection`, `Auth:KeyCertificatePath`, `Auth:KeyCertificatePassword`, `Auth:KeyPreviousCertificatePath`, `Auth:KeyPreviousCertificatePassword` | `auto`, unset | How new data-protection keys are encrypted at rest: `auto` (Windows DPAPI for the current account; on other systems the certificate when one is set, else plain keys with a start-up warning), `dpapi`, `dpapi-machine`, `certificate` or `none`. The previous certificate is read-only, for a rotation ([ch. 26](<0026 Audit log, deployment and assurance.md>)). |
| `Auth:ApplicationName`, `Auth:KeysDirectory`, `Auth:CookieName`, `Auth:CookieDomain` | `GridSim`, `App_Data/keys` under the content root, `.AspNetCore.Identity.Application`, no domain | Recognise an estate single-sign-on cookie (decrypt only; no page requires sign-in). |
| `Sso:Enabled`, `Sso:Authority`, `Sso:IsAuthority`, `Sso:AllowedClients`, `Sso:TicketSeconds` | `false`, empty, `false`, empty list, `120` | Cross-domain single sign-on settings. With the shipped settings every visitor is anonymous. |
| `Site:GoogleTagId`, `Site:AnalyticsScriptUrl`, `Site:AnalyticsCollectorUrl`, `Site:ShareImageUrl`, `Site:Organisation*`, `Site:FooterLinks:N:*`, `Site:SecurityTxt:*` | all empty; `Site:AnalyticsSiteName` is `GridSim` | The deployment's own analytics, share card, organisation block, footer links and `/.well-known/security.txt`. With none set the page loads no third-party script and security.txt answers 404; a malformed value stops start-up naming the setting. The full list is in `docs/developer/deploy/README.md`. |
| `Analytics:LogPath` | `gridsim-usage/usage.jsonl` in the folder above the app's own folder (`AppContext.BaseDirectory`) | The visit log file. Best effort: the file is opened for append at start-up (created empty when missing), and a path whose folder cannot be created or whose file cannot be written disables the log without stopping the app, with a `warning: the usage log is off` line on stderr naming the path and the error. A later append that fails is reported on stderr once. |

```bash
GridSim__BridgeEndpoint=ws://127.0.0.1:8770/ws/bridge GridSim__BridgeAllowUnencrypted=true ASPNETCORE_URLS=http://127.0.0.1:5000 dotnet run --project src/GridSim.Web
```

### The live cycle engine

The `Cycle:` section configures the prioritised analysis cycle (the CYCLE tab, the same engine as `gridsim cycle`) that
runs the post-solve fan-out behind a live session.

| Key | Default | Effect |
|---|---|---|
| `Cycle:Enabled` | `true` | Run the cycle engine on each solved frame of a live session. `false` turns it off. |
| `Cycle:WindowMs` | `1000` | Wall-clock deadline per cycle in milliseconds; the compute budget is `Workers` x `WindowMs` core-milliseconds. |
| `Cycle:Workers` | `min(4, max(1, cores - 1))` | Fan-out worker count, kept modest so the background work does not starve the web server. Values below 1 become 1. |
| `Cycle:Shards` | `8` | Rotating low-priority N-1 shard count; full branch coverage takes this many cycles. Values below 1 become 1. |

## Variables of the separate GDA repository

These belong to the GDA pipeline scripts, which live in the GDA repository, not in this one; nothing in the GridSim
source reads them. You set them when you run the estimated-state workflow
([ch. 22](<0022 Estimating GB history.md>)).

| Variable | Read by | Effect |
|---|---|---|
| `GRIDSIM_CLI` | `NetworkModel/build_estimated_state.py` and `calibrate_sigmas.py` | Path to the command line tool (`gridsim.exe`). Default: the repository's Release build if present, else Debug. Build Release first: backfill days are CLI-bound and Release is 2 to 3 times faster per period. A GDA checkout that predates the rename of the executable to `gridsim` needs this set to the new name. |
| `SPINE_ETYS` | `NetworkModel/spine/build_spine.py` | `1` opts in to the ETYS 2025 circuit overlay. The overlay describes today's network, and under it the 2019 event day regressed from 46/46 chi-square clean to 32/46, so leave it off for backfill. |
| `GRIDRADAR_API_TOKEN` | `Ingest/gridradar/gridradar_acquire.py` | The PMU-feed access token (a gitignored `config.json` beside the script works too). Without it the Gridradar products are not built. |

## Build settings that affect configuration

`Directory.Build.props` turns on `TreatWarningsAsErrors` with the .NET code analysers in `Recommended` mode: a Release build
is 0 warnings and 0 errors, and any code you add must clear the same bar. The one release number lives in the single
`<Version>` property there; `gridsim --version` prints it ([ch. 1](<0001 Getting started.md>)).

## Limits

The desktop app does not set the process culture, so its number formatting follows the machine's regional settings.

The desktop app takes the first argument that does not start with `-` as the model text, and that includes the value
of a flag. `--back 30` is read as model text `30` and boots onto the first picker label containing `30` (for example
`MATPOWER  case30`); `--zone zone-ukpn` and `--tab OPERATOR` are read the same way, and text that matches no label boots
the first model in the picker. Put the model argument first, or leave it out.

## See also

- [ch. 1, Getting started](<0001 Getting started.md>) - building, the three test modes and the first solve.
- [ch. 19, CLI reference](<0019 CLI reference.md>) - the flags these variables interact with.
- [ch. 24, The GDA bridge](<0024 The GDA bridge.md>) - starting the bridge the endpoints point at.
- [ch. 22, Estimating GB history](<0022 Estimating GB history.md>) - the pipeline that reads `GRIDSIM_CLI` and `SPINE_ETYS`.
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - symptoms when a setting is wrong.
