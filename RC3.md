# GridSim 2.1.071 RC-3

| | |
|---|---|
| Version | 2.1.071 (release candidate RC-3) |
| Source | the commit tagged `v2.1.071-RC-3`; every binary reports `2.1.071+<commit>` from `--version` |
| Platforms | Windows x64, Linux x64 and macOS arm64 (as RC-2), built by the release workflow |
| Previous | RC-2 (2.1.031) |

RC-3 adds transmission study jobs and specialist study packages to GridSim as **new, separate commands**. Every
command, flag, default, exit code and output that RC-2 shipped behaves as before (see Verification). Every build is
Release and self-contained (it carries the .NET 10 runtime).

## What is new in RC-3

| Job | Command or surface | Scope tested for this release candidate |
|---|---|---|
| Frequency Security panel | WPF, Avalonia and web tab (was "NESO RP1") | Every client, GB 50 Hz and non-50 Hz cases; a desktop `--tab rp1` or `--tab neso` still opens it |
| Preventive branch N-1 with AC verification | `transmission-security` | DC candidate dispatch, every listed outage solved with full AC and checked against absolute voltage, terminal MVA and unit limits; analytical two-bus and three-bus cases |
| PSS/E v35 node-breaker topology | Core API | Substation switching, terminal identity, bus coupler open/close, a model bit-identical to the existing RAW loader with every switch closed, topology-aware RAW write and read-back |
| CGMES as a study job | `cgmes-study` | One profile bundle through import, topology, controls, AC solve, optional N-1, export and read-back (constructed bundle) |
| AC-verified transfer capability | `transfer-capability` | Bounded search with re-solved bracket and the first limiting element and outage (two- and three-bus cases) |
| Ordered corrective N-1-1 | `corrective-sequence` | Nine recorded steps per sequence with an explicit permitted-action policy, ramp and time budgets (three-bus case) |
| Native RMS dynamics study | `dynamics-study` | DYR model coverage report before any verdict, identity-resolved events, CCT brackets, deterministic batch (IEEE 14-bus) |
| Planning snapshots | `planning-study` | Dated, ordered operations on an immutable master, UTC effective intervals, conflict refusal, reproducible retry (synthetic four-bus master) |
| Restoration and reliability | `restoration-study`, `reliability-study` | Enumerated switching order with AC-verified steps; analytical radial-feeder indices (SAIFI, SAIDI, CAIDI, ENS) |
| EMT lossy line | `emt-line` | Constant-parameter lossy line against analytic travel time, reflection and attenuation |
| Industrial arc flash | `arc-flash` | IEEE 1584-2018 model inside its stated voltage, current, gap and enclosure envelope |
| Cable rating | `cable-rating` | IEC 60287 steady-state rating of one buried single-core cable |
| Motor start | `motor-start` | Direct-on-line acceleration from a native v2 operating point; placed motor rows in the v2 network schema |

Each command reads a strict, versioned JSON study file bound to its case by SHA-256, writes its result only to a new
file, and reports what it could not establish as incomplete rather than as a pass. Full details are in the CLI
reference (`docs/manual/0019 CLI reference.md`) and in CHANGELOG.md, versions 2.1.032 to 2.1.071.

## Artefacts

The release workflow builds the archives, SBOMs and `SHA256SUMS.txt` from the tagged commit and attaches them to the
release, as for RC-2: the command line tool for Windows x64, Linux x64 and macOS arm64, the WPF app for Windows x64,
the Avalonia app for Windows x64 and Linux x64, and CycloneDX SBOMs for the tool, the apps, the core library and the
web image. Check every download against `SHA256SUMS.txt` with `sha256sum <file>` (Linux, macOS) or
`Get-FileHash <file> -Algorithm SHA256` (Windows PowerShell).

## System requirements

Unchanged from RC-1 (see RC1.md): Windows 10 or 11 x64, x64 Linux with glibc 2.27 or later, 8 GB RAM minimum and
16 GB recommended, no .NET install needed. A v2 case that uses the new placed-motor rows needs GridSim 2.1.070 or
later to load; older builds refuse the unknown member.

## Verification of this release candidate

All on one Windows 11 x64 machine (8 cores, 8 GB RAM), .NET SDK 10.0.401.

- Release build of the whole solution with 0 warnings.
- Full test suite in Release, in all three CPU modes of `docs/manual/0001 Getting started.md` (normal,
  `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`): see the results recorded at release time below.
- Full test suite in Debug before the specialist packages were added: GridSim.Tests 11,456 passed, 35 skipped;
  GridSim.App.Tests 314 passed.
- Legacy command compatibility: 34 existing command invocations (power flow, DC and AC OPF, SCOPF, N-1, N-1-1, N-2,
  screen, PSS/E v30/v33/v35 RAW loads, help and usage errors) give byte-identical exit codes, standard output and
  standard error to the RC-2 development base, apart from run timestamps, the build stamp and a results path.
- The Frequency Security panel inspected in the running WPF, Avalonia and web clients, including the web raw-replay
  layout and the non-50 Hz state.

**Not done for RC-3, and why.**

- No comparison with a commercial tool (PSS/E, PowerWorld, PowerFactory, PSCAD/EMTP): no licence is available. No
  parity with any of them is claimed; results carry an `UNVERIFIED` comparator record where the study defines one.
- The new jobs are tested on small analytical and constructed cases (2 to 14 buses, one constructed CGMES bundle); no
  large or published utility case has been run through them yet.
- Arc flash: the IEEE 1584-2018 coefficients were taken from an open-source implementation and checked against the
  standard's Annex D worked examples it reproduces, not against a copy of the standard obtained under licence.
- Cable rating: the IEC 60287 arithmetic is independently re-derived in the tests; no published ampacity example has
  been compared, and the clause references are to be checked against the standard.
- The large cases skipped by the RAM gate on an 8 GB machine.

## Reporting bugs

Send every bug report to **mark@gridsimdigitaltwin.co.uk**. Please give the version (`gridsim --version`), the operating system,
the command or the steps, the case (or a small case that shows the problem) and what you expected.
