# 1. Getting started

This chapter takes you from a bare machine to a solved power flow. It covers the two ways to get the `gridsim`
command line tool (a release download, or a build from source), what the build must report, how to run the test
suite and read its result, your first solve with its real output, the `--help` and `--version` answers, and a first
look at the desktop and web apps. Read it once, then use the closing path to choose the next chapter.

## What `gridsim` means in this manual

Every command in this manual is written `gridsim <verb> ...`. That is the executable the build produces:
`src\GridSim.Cli\bin\Release\net10.0\gridsim.exe` on Windows, and `gridsim` in the same folder on Linux and macOS.
Which form you type depends on how you got it:

| You have | `gridsim` means |
|---|---|
| A release archive, unpacked | the `gridsim.exe` (or `gridsim`) in that folder, run from it or from a folder on your `PATH` |
| A source build, not installed | `dotnet run --project src/GridSim.Cli -c Release --` (the `--` ends the `dotnet` options; everything after it goes to GridSim) |
| A source build, using the built file | `src\GridSim.Cli\bin\Release\net10.0\gridsim.exe` (Windows) or `src/GridSim.Cli/bin/Release/net10.0/gridsim` (Linux, macOS) |

So `gridsim case9 --once` from source is `dotnet run --project src/GridSim.Cli -c Release -- case9 --once`. The
built file starts faster than `dotnet run`, which checks the build on every call, so use it for scripts and for
anything you time.

GridSim ships no `dotnet tool` package and no installer: the project chose self-contained release binaries over a
tool because a tool needs the SDK on the target machine and cannot carry the `data/` folder beside the executable.
The release archives below are the install route.

## Installing a release (no SDK needed)

To run GridSim without building it, download the archive for your machine from the repository's GitHub Releases
page. Each release is built by `.github/workflows/release.yml` from the tagged commit. Its first job, `verify`, runs on
`ubuntu-latest` and checks the tag against the version, builds, runs `dotnet test GridSim.slnx -c Release --no-build`
(normal CPU mode, no `continue-on-error`) and runs the vulnerable-package check; every later job needs `verify`. Each
command line archive is then smoke-tested and given the published-output tests on a runner of its own operating
system. The ship scope is the command line tool, the `GridSim.Core`
NuGet package and the replay-only web container image. The release page carries:

| Archive | Contents |
|---|---|
| `gridsim-<version>-win-x64.zip` | the `gridsim.exe` command line tool for Windows x64 |
| `gridsim-<version>-linux-x64.tar.gz` | the `gridsim` command line tool for Linux x64 |
| `gridsim-<version>-osx-arm64.tar.gz` | the `gridsim` command line tool for macOS on Apple silicon |
| `SHA256SUMS.txt` | the SHA-256 checksum of every other file on the page |
| `gridsim-<version>.sbom.json`, `gridsim-web-<version>.sbom.json` | CycloneDX software bills of materials for the command line tool and the web image |

The other two artefacts are not on the release page:

| Artefact | Where | Use |
|---|---|---|
| `GridSim.Core` NuGet package | the repository's GitHub Packages NuGet feed | `dotnet add package GridSim.Core --version <version>` with that feed configured as a package source |
| Web app container image | `ghcr.io/<owner>/gridsim-web:<version>` | `docker run --rm -p 127.0.0.1:5540:8080 ghcr.io/<owner>/gridsim-web:<version>`; replay-only (the solve API and the simulation socket are not served); deployment settings are in `docs/developer/deploy/README.md` |

The desktop app (`GridSim.Wpf`, Windows) and the Unity viewer are not in a release: build them from source (below).
`GridSim.Gda` and the GDA data are not release artefacts either: the `gda` verbs are in the command line tool, but
they need a GDA data lake (`GDA_DATA_ROOT`, see [ch. 3](<0003 Configuration.md>)), which no download includes.

The command line archives are self-contained: one executable that carries the .NET runtime, with the bundled
`data/` cases, `schemas/`, the `crossval` sidecar (`tools/crossval/crossval.py` and `sidecar_common.py`), `LICENSE.md`
and `THIRD-PARTY-NOTICES.md` beside it. Unpack the archive anywhere and run
the executable from that folder; keep `data/` next to it, because the executable finds its cases there.

**Signatures.** When the release was built with the project's code-signing certificate, `gridsim.exe` carries an
Authenticode signature (check it in the file's Properties, Digital Signatures tab, or with
`Get-AuthenticodeSignature gridsim.exe` in PowerShell) and the NuGet package is signed
(`dotnet nuget verify GridSim.Core.<version>.nupkg`). Without the certificate the release is built unsigned and its
workflow log and run summary say so; a maintainer who sets the repository variable `GRIDSIM_ALLOW_UNSIGNED_RELEASE` to
`false` makes signing required, and the release then stops before it builds anything. For maintainers: the release workflow reads the certificate from the repository secrets
`SIGNING_CERT_PFX_BASE64` (the `.pfx` file, base64-encoded) and `SIGNING_CERT_PASSWORD`, plus the optional repository
variable `SIGNING_TIMESTAMP_URL`; the header of `.github/workflows/release.yml` gives the details. The Linux and macOS
binaries are not signed, so on an unsigned binary Windows SmartScreen and macOS Gatekeeper may ask you to confirm the
first run; on macOS, `xattr -d com.apple.quarantine gridsim` clears the download flag.

To build from source instead, read on.

## Prerequisites

The one hard requirement is the **.NET 10 SDK**: the SDK, not only the runtime, because you compile the source and
run the test host. Every project in `GridSim.slnx` targets `net10.0` (`GridSim.Wpf` targets `net10.0-windows`), and
the repository has no `global.json`, so any installed 10.0 SDK builds it. Confirm the SDK is present:

```bash
dotnet --list-sdks      # a 10.0.x line must be present
```

Everything else is optional and depends on the task:

| Task | Needs |
|---|---|
| Build, test, run the CLI | .NET 10 SDK only, any OS |
| Run the desktop app (`GridSim.Wpf`) | .NET 10 SDK on Windows (it builds on every OS, because the project sets `EnableWindowsTargeting`, and runs only on Windows) |
| `crossval` (diff against pandapower) | Python with `pandapower` (which brings `numpy` and `scipy`); without it the export is still written and the verb exits 77 ("skipped"), never 0 |
| GPU backends (`--gpu`) | a ROCm, OpenCL (FP64) or CUDA device; a missing device warns and falls back to CPU |
| GDA-materialised cases (`gb-spine`, `zone-*`) | the read-only GDA data lake on an authorised machine; it is never in the repository |

There is no database, no service to provision and no restricted data to fetch. The built-in cases are embedded in
`GridSim.Core`, and the JSON cases (the GB models `data/uk`, `data/gb-synthetic` and `data/gb-full`, plus the starter
maps under `data/intl`) ship in `data/`. Without a licence the tool runs at the free Personal tier, which is enough
for everything in this chapter ([ch. 4](<0004 Licensing and activation.md>)).

## Getting the code

```bash
git clone <your-remote>/GridSim.git
cd GridSim
```

The commands in the rest of this chapter run from that folder, the repository root.

## Building

```bash
dotnet build GridSim.slnx -c Release
```

The solution builds eight projects: the libraries `GridSim.Core`, `GridSim.Gda` and `GridSim.Gpu`, the command line
tool `GridSim.Cli`, the case editor `GridSim.SchemaEditor`, the web app `GridSim.Web`, the desktop app `GridSim.Wpf`
and the test project `tests/GridSim.Tests`.

`Directory.Build.props` turns on `TreatWarningsAsErrors` with the .NET code analysers in `Recommended` mode, so a healthy
build ends with **0 warnings and 0 errors**. A warning fails the build; fix it, never suppress it. The same rule
holds for any code you add.

A plain `dotnet build GridSim.slnx` (Debug) is fine for development. Use Release for anything you time (`bench`,
`nminus1-sweep`, `stress`, `chain`): a Debug timing is not a comparable number
([ch. 28](<0028 Benchmarking and GPU.md>)).

The Release build puts the command line tool, its `data/` folder and its `schemas/` folder in
`src\GridSim.Cli\bin\Release\net10.0\`. To make the self-contained single-file build that a release ships, publish
with the profile for your platform:

```bash
dotnet publish src/GridSim.Cli -c Release -p:PublishProfile=win-x64     # or linux-x64, osx-arm64
```

The result lands in `artifacts/publish/gridsim-<rid>/`.

## Checking the build: `--version` and `--help`

```bash
gridsim --version
```

```
$ gridsim --version
gridsim 1.95.467+203c6b9cc0edff55f2bd73642c94dc0963a82b92
[exit 0]
```

The line is `gridsim <release number>+<git commit>`: the one release number from `Directory.Build.props` and the commit
the binary was built from. A build from a source tarball without `.git` has no `+commit` part. The answer needs no
licence and never fails, so a script can ask which build it has. The number and the commit differ by build.

```bash
gridsim --help
```

```
$ gridsim --help
gridsim <verb> [args] - GridSim CLI. See docs/manual/0019 CLI reference.md (or the header of Program.cs) for the full flag list.
run:        (none)=live UK model, <case|file.m>, --dir <path>, --mpc <file.m>, --once
contingency: trip, screen, nminus1 (n-1), nminus2 (n-2), n-1-1 (nminus1minus1), nminus1-sweep, stress,
...
tools:      bench, simdcheck, gpucheck, batchsolve, licence, audit, compliance, help
verb help:  gridsim <verb> --help (or gridsim help <verb>) prints the verb's usage, flags, defaults and exit codes.
...
[exit 0]
```

The first line is the usage line, the next lines name the verb groups and the last lines show the global switches.
Each group lists every verb in it.
`gridsim <verb> --help` prints that verb's section of the [CLI reference](<0019 CLI reference.md#help>), which is compiled
into the binary, so the help and the manual never disagree. Help never needs a licence.

## Running the tests

```bash
dotnet test GridSim.slnx -c Release --no-build      # after the Release build above
```

Green means the final summary line starts with `Passed!` and reports `Failed: 0`. Any failure, on any platform, is a
regression. The suite needs no GDA data lake: the tests build their
own fixtures. The tests pin exact values, including bit-for-bit parity between the dense and sparse Newton engines
(`DenseSparseParityTests`).

A few tests report as skipped, never as passed, because they need something your machine does not have:

- the published-output tests (`PublishedOutputTests`) run only when `GRIDSIM_PUBLISHED_DIR` names a folder produced by
  `dotnet publish` with a release profile; the release workflow sets it;
- one long-running DC-OPF security test on the full GB case carries an explicit skip with its reason in the source.

To run a subset while iterating, filter by name:

```bash
dotnet test tests/GridSim.Tests --filter "FullyQualifiedName~Case9"
```

### The three modes

GridSim's rule is bit-identical results across CPUs, so the full suite is run in three CPU modes, which differ only
in which vector instructions the .NET runtime may use:

| Mode | Environment | Vector path exercised |
|---|---|---|
| normal | none | whatever the CPU offers (AVX-512, AVX2 or the 128-bit path) |
| `avx2-off` | `DOTNET_EnableAVX2=0` | the SSE (128-bit) kernels |
| `hwintrinsic-off` | `DOTNET_EnableHWIntrinsic=0` | the scalar fallback |

`.github/workflows/ci.yml` runs all three on Linux and on Windows; it starts on a release tag or by hand. A failing
test fails the workflow. To run a
mode yourself, set the variable for the test run only (the build is always done in normal mode):

```bash
DOTNET_EnableAVX2=0 dotnet test GridSim.slnx -c Release --no-build
```

In PowerShell: `$env:DOTNET_EnableAVX2 = "0"; dotnet test GridSim.slnx -c Release --no-build`, then remove the
variable. `gridsim simdcheck` reports which tier the process is on (see [ch. 3](<0003 Configuration.md>)).
The tests are a constitution: on Windows the same exact expected values are pinned in every mode.

## First solve

Solve the WSCC 9-bus benchmark, the project's physics ground truth:

```bash
gridsim case9 --once
```

```
$ gridsim case9 --once
GridSim  -  IEEE 9-bus (WSCC)
9 buses  9 branches  3 generators  base 100 MVA  60 Hz
========================================================================
Converged (4 NR iters, 0 OLTC passes, max mismatch 1.65E-014 p.u.)
Generator output 0.32 GW  Demand 0.32 GW  Losses 4.6 MW  Inertia 7.5 GVAs

Bus  Name                            kV    Vm(pu)  Va(deg)    P(MW)   Q(MVAr)
------------------------------------------------------------------------------
  1                                 345   1.040     0.00      71.6      27.0
...
Voltage range: 0.996 - 1.040 p.u. (in band)
Total losses: 4.64 MW, -92.16 MVAr
Protection simulation: no relay would operate on this state (generic settings).
[exit 0]
```

`case9` loads at its WSCC nominal frequency, so the second line ends `60 Hz`; the power flow does not use the
frequency.

The signature of this run is 4 Newton iterations, a final mismatch of 1.65E-014 p.u. and 4.64 MW of total losses.
`PowerFlowTests` pins convergence within 6 iterations, a final mismatch below 1e-8 p.u., the published voltages (to
1e-3 p.u.) and angles (to 1e-2 degrees) and the 4.64 MW loss (to two decimal places); it does not pin the iteration
count or the 1e-14 level. A case9 solve that does not converge, or whose voltages or losses differ from these, means
something is wrong with the build, not the case. The exit code is
0 because the power flow converged; a solve that does not converge exits 3. The same case is cross-checked against
pandapower ([ch. 27](<0027 Validation tools.md>)). The case ships the published WSCC machine data, so the report
shows the inertia (7.5 GVAs).

Now a case with the report in full:

```bash
gridsim case14 --once
```

```
$ gridsim case14 --once
GridSim  -  IEEE 14-bus
14 buses  20 branches  5 generators  base 100 MVA  50 Hz
========================================================================
Converged (2 NR iters, 0 OLTC passes, max mismatch 1.32E-010 p.u.)
Generator output 0.27 GW  Demand 0.26 GW  Losses 13.4 MW  Inertia not in the data (no generator H)
...
```

The report says what the data does not give instead of printing a zero: a MATPOWER case has no generator inertia, so
the totals line says so. [Chapter 5](<0005 Running power flows.md>) reads every part of the report.

Last, solve the reduced GB model once:

```bash
gridsim uk --once
```

```
$ gridsim uk --once
GridSim  -  GB reduced (10-zone)
20 buses  24 branches  29 generators  base 10000 MVA  50 Hz
========================================================================
Converged (3 NR iters, 3 OLTC passes, max mismatch 1.29E-011 p.u.)
Generator output 39.56 GW  Demand 39.50 GW  Losses 55.6 MW  Inertia 171.7 GVAs
...
```

The extra figure is `3 OLTC passes`: the on-load tap-changer loop stepping the ten GSP transformers until their
controlled buses sit inside their voltage deadbands.

These captures were taken on one Windows machine. The suite pins exact values in all three CPU modes, on Windows and on
Linux. Stamps and timings in other reports differ by
machine and run.

With no arguments at all, `gridsim` runs the same GB model live, re-solving a day and night demand curve every tick
until you press a key. Give `--once` (solve once and exit) or `--ticks N` (N headless ticks, then exit) whenever a
script runs it, so it terminates.

## The apps at a glance

**Desktop app (Windows).**

```bash
dotnet run --project src/GridSim.Wpf
```

The control-room dashboard opens on the last model you used, or on the reduced GB model on first launch, and animates
the day and night curve on a network map. The NETWORK picker switches case; the SOLVER picker forces an engine (Auto
picks per network). To boot onto a case, pass its label or any part of it as the first argument that does not start
with `-`: `dotnet run --project src/GridSim.Wpf -- case118`. Text that matches no label boots the first model in the
picker, not the last-used one. The launch arguments are in [ch. 3](<0003 Configuration.md>)
and the tour is [ch. 8, The desktop app](<0008 The desktop app.md>).

**Web app.** `dotnet run --project src/GridSim.Web` serves the same control room in a browser on any OS, in the
`Development` environment on `http://localhost:5180` with the REST and WebSocket API mapped. The pages and the API are
in [ch. 9](<0009 The web app and REST and WebSocket API.md>), the configuration keys in
[ch. 3](<0003 Configuration.md>), and the Docker deployment, which starts replay-only, in
[ch. 26](<0026 Audit log, deployment and assurance.md>).

### The rebuild file-lock gotcha

While the desktop app runs, especially under the Visual Studio debugger, its output DLLs are locked. Because
`GridSim.Core` is the root dependency, any rebuild that re-links a downstream assembly fails with MSB3027 or MSB3021
copy errors. Two fixes:

1. Stop the app first: stop the debugger, or from a shell run `taskkill /F /IM GridSim.Wpf.exe` (in Git Bash write
   the flags with doubled slashes, `taskkill //F //IM GridSim.Wpf.exe`).
2. Compile-check without touching the running app by redirecting the build output:

   ```bash
   dotnet build src/GridSim.Wpf/GridSim.Wpf.csproj -p:BaseOutputPath=obj/verifybin/
   rm -rf src/GridSim.Wpf/obj/verifybin      # discard the scratch output afterwards
   ```

   Treat that as a compile gate only: do not launch the binaries it produces.

When you batch several UI edits, keep the app closed and build once at the end.

## Where to go next

A short path through the manual:

1. [Ch. 2, Loading networks](<0002 Loading networks.md>): the built-in cases, MATPOWER files, JSON case folders
   and the GB models.
2. [Ch. 3, Configuration](<0003 Configuration.md>): environment variables, settings files and where results go.
3. [Ch. 5, Running power flows](<0005 Running power flows.md>): the report, engine choice and recorded runs.
4. [Ch. 11, Contingency analysis](<0011 Contingency analysis.md>): what happens when a branch trips.
5. [Ch. 8, The desktop app](<0008 The desktop app.md>): the live control room.
6. [Ch. 19, CLI reference](<0019 CLI reference.md>): every verb, flag and exit code, when you need the exact form.

From there the table of contents ([0000 Contents.md](<0000 Contents.md>)) groups the rest by task: dynamics, events,
estimation and operations.

## Limits

The desktop app takes the first argument that does not start with `-` as the model text, including the value that
follows a flag: `--back 30` is read as model text `30` and boots onto the first label containing it. Put the model
argument first, or expect an unintended start.

## See also

- [ch. 2, Loading networks](<0002 Loading networks.md>) - every way to get a network into GridSim
- [ch. 3, Configuration](<0003 Configuration.md>) - environment variables, settings files and paths
- [ch. 5, Running power flows](<0005 Running power flows.md>) - the solve verbs and their outputs
- [ch. 8, The desktop app](<0008 The desktop app.md>) - the dashboard tour
- [ch. 19, CLI reference](<0019 CLI reference.md#running-a-case-no-verb>) - the bare run and every flag
- [ch. 30, Troubleshooting](<0030 Troubleshooting.md>) - build failures, non-convergence and other faults
