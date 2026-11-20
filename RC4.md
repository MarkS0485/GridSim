# GridSim 2.1.073 RC-4

| | |
|---|---|
| Version | 2.1.073 (release candidate RC-4) |
| Source | the commit tagged `v2.1.073-RC-4`; every binary reports `2.1.073+<commit>` from `--version` |
| Platforms | Windows x64, Linux x64 and macOS arm64 (as RC-3), built by the release workflow |
| Previous | RC-3 (2.1.071), with the post-RC-3 fixes of 2.1.072 |

RC-4 adds study-case management, analysis verbs, probabilistic and reliability studies, short-circuit and protection
duty, declarative dynamic models, small-signal analysis, three-phase EMT, node-breaker topology and a workbench to
GridSim as **new, separate commands and library entry points**, together with a scripting language and console. Every
command, flag, default and exit code that RC-3 shipped behaves as before (see Verification); results move at rounding
level only, through the first BREAKING CHANGE below; the second changes only solves that did not converge. Every build is Release and self-contained (it carries the .NET 10
runtime).

## What is new in RC-4

| Job | Command or surface | Scope tested for this release candidate |
|---|---|---|
| Study cases | `study-case`, `case-diff`, `result-diff` | One immutable master with dated variations, named scenarios and a groups sidecar bound by SHA-256; comparison by stable identity |
| Analysis verbs | `sensitivity`, `qv-curve`, `pv-contingency`, `dc-contingency`, `fault-level-n1` | Analytical small cases; AC sensitivities refused as OUT OF SCOPE with tap changers, DC links, FACTS, SVC, induction machines or flow-controlling transformers |
| Seeded random foundation | `gridsim-rng/1` | Bit-repeatable from the seed, thread-count-invariant reduction, Sobol sequence to 64 dimensions |
| Probabilistic studies | `probabilistic` | Probabilistic load flow and static adequacy (LOLP, LOLE, EENS) by Monte Carlo, quasi-Monte Carlo or convolution, with sampling error |
| Network reliability | `reliability-network` | Load-point indices, SAIFI, SAIDI, CAIDI, ASAI, ENS, AENS with AC-verified restoration; hand-derived feeder cases |
| Line constants | `line-constants` | Overhead lines: perfect earth and exact earth-return integral, Kron reduction, transposition, sequence values |
| Resident endpoint | `rpc`, Python `Session` | `gridsim-rpc/1` on standard input and output or loopback TCP; no authentication (see Known limits) |
| Short-circuit duty | `g74`, `ansi-sc`, `sc-61363`, `sc-dc`, `fault-series`, `fault-simultaneous` | Equations on small cases; `iec60909` unchanged; open-conductor and simultaneous shunt faults on the operating point, a shunt and a series fault together not offered |
| Protection | `devices-check`, `fault-line`, `sc-trace`, `protection-audit` | Placed relays, fuses, reclosers and breakers with IEC 60255-151 and IEEE C37.112 curve families |
| Declarative models | `user-model` | `gridsim-model/1` block diagrams; the models the catalogue can express match the coded models bit for bit |
| Small-signal analysis | `modal-full` | Dense and Arnoldi eigen-solvers with residual-checked eigenpairs; unsupported controllers reported NOT LINEARISED |
| Adaptive dynamics | `dynamics-study` (`adaptive`) | Dormand-Prince kernel on the reduced swing model; fixed-step rows and `status` unchanged |
| Node-breaker topology and exchange | `substation-state`, `cgmes-export`, `cgmes-conform`, `cgmes-study --archive`, `dgs-import` | Constructed and synthetic bundles; `cgmes-conform` is a named subset of eleven checks, not a conformity certificate |
| Unbalanced distribution | `dist3ph-mesh` | IEEE 4-node cases and the IEEE 123-node feeder through the Core solver; regulators (fixed taps or a compensator tap search), transformer banks and switched capacitor banks in the case file |
| Three-phase EMT | `emt3` | Nodal solver with lines from line constants, faults, breakers, arresters and saturable elements; synchronous and induction machines, average and switched converters, VSC, MMC and LCC HVDC through the existing Core classes; partitioned runs and RMS-EMT co-simulation in Core only |
| Workbench | Avalonia (Ctrl+Shift+B, File > Workbench...), web `/workbench/{case}`, `report` | Model browser, plot kit, reports and an undoable single-line editor; opens v2, PSS/E RAW, UCTE and CGMES; typed Add generator, load and branch entries; no screenshots taken |
| Scripting | `gridsim-script`, `GridSim.Scripting`, `.gss` files | Grammar version 1, frozen for all of 2.x (manual chapter 35); engines LIVE, STATIC, FREQUENCY LUMPED, PROTECTION FREQUENCY, RMS CLASSICAL, SUBTRANSIENT and GOVERNED, EMT SINGLE_PHASE and THREE_PHASE; the same script, version and model give the same run record bytes |
| Script console | Avalonia File > Script console..., web `/console` | Line-by-line console with the session recorded; on the web, catalogue cases only, Analyst role for SOLVE, Operator role for changes |
| Study output formats | `--json`, `--csv` | Ten more study verbs write through the shared result writer |
| GPU solve report | `GPU:` line, `gpucheck`, `gpuSolves` metadata | Says how many Newton linear solves ran on the device and how many fell back to the CPU; counting only |
| UK power quality | `harmonic-assess`, `flicker`, `filter-design` | A missing background is reported INCOMPLETE, never a pass |
| Design and grid code | `cable-size`, `connection-assess` | Equations only; every table and limit AWAITING STANDARD TEXT |

Each command reads a strict, versioned JSON study file bound to its case by SHA-256 where it takes a case, writes its
result only to a new file, and reports what it could not establish as incomplete rather than as a pass. Full details are
in the CLI reference (`docs/manual/0019 CLI reference.md`), the scripting chapter (`docs/manual/0035`) and in
CHANGELOG.md, versions 2.1.072 to 2.1.073.

### BREAKING CHANGE: solver ordering and voltage floor

**BREAKING CHANGE:** results and determinism digests are NOT bit-identical to 2.1.071 or 2.1.072. Stored digests and
bit-exact comparisons against those releases will fail and must be regenerated. Sparse LU now uses a minimum-degree
ordering (was reverse Cuthill-McKee) and the Newton power flow voltage floor is 0.01 pu (was 0.2 pu).

The difference is at rounding level: on 66 MATPOWER cases the largest differences are 9.7e-13 pu in voltage magnitude and
1.9e-11 deg in angle, dynamic and EMT trajectories agree within about 2e-10 normwise, and convergence and iteration counts
are identical. This breaches the strict bit-for-bit contract but will not materially change results; they stay at the
rounding floor. `case2848rte` now converges, and large cases solve 4 to 7 times faster on an EPYC 7763 server
(case_ACTIVSg2000 306 -> 46 ms, case3375wp 560 -> 83 ms).

### BREAKING CHANGE: Newton retry after a failed solve

**BREAKING CHANGE:** a Newton power flow (dense or sparse) that does not converge is now retried automatically, so the
outcome of a solve that used to fail can change. Every solve that converged before gives bit-identical results: the
retry is never reached (checked on 15 MATPOWER and PGLib cases, dense and sparse, up to case9241pegase).

The retry repeats the same Newton iteration with the same options and voltage magnitudes, starting the bus angles from
the DC power flow. It needs one slack bus and a DC model that forms a single island. A converged retry is reported
unless a branch carries more than 90 deg net of any phase shift (a root past the branch's maximum-transfer point, not a
usable operating point); otherwise the original non-converged result is reported unchanged, with a note.
`PowerFlowResult.NewtonRetry` and `NewtonRetryNote` record what happened, and `pf` and `export` print the note.
`case13659pegase` now converges from flat start in 10 iterations (it used to stop at a mismatch of 63.157 pu).

The retry runs only when the failed solve stalled or diverged (its final mismatch is above 1e-3 of its starting
mismatch). A solve stopped by the iteration cap while still converging is not retried and is reported `MaxIterations`
as before, so `MaxIterations` is never exceeded by a second attempt. `PowerFlowOptions.RetryFromDcStart = false` turns
the retry off.

## Artefacts

The release workflow builds the archives, SBOMs and `SHA256SUMS.txt` from the tagged commit and attaches them to the
release, as for RC-3: the command line tool for Windows x64, Linux x64 and macOS arm64, the WPF app for Windows x64,
the Avalonia app for Windows x64 and Linux x64, and CycloneDX SBOMs for the tool, the apps, the core library and the
web image. Check every download against `SHA256SUMS.txt` with `sha256sum <file>` (Linux, macOS) or
`Get-FileHash <file> -Algorithm SHA256` (Windows PowerShell).

## System requirements

Unchanged from RC-1 (see RC1.md): Windows 10 or 11 x64, x64 Linux with glibc 2.27 or later, 8 GB RAM minimum and
16 GB recommended, no .NET install needed.

## Verification of this release candidate

All on one Windows 11 x64 machine (8 cores, 8 GB RAM), .NET SDK 10.0.401.

- Release build of the whole solution with 0 warnings.
- Full test suite in Release at commit e2b1b9f1: 12,911 passed, 4 failed, 35 skipped (plus 4 long rows, all passed).
  The 4 failures (a stale bundled-data manifest and two direct solver choices in the CLI) were fixed in abc0d87a, and
  those tests and their neighbours were re-run green (312 passed). The full suite was not re-run after that fix.
- Legacy command compatibility: 34 existing command invocations give byte-identical exit codes, standard output and
  standard error to the RC-3 base in 33 cases; the 34th differs only in volatile fields (run timestamps, the build
  stamp and a results path).
- Documentation checks clean: `tools/docs/lint.py` (0 failing), `tools/wiki/gen_cli_reference.py --check` and
  `mkdocs build --strict`.

On Linux x64 (Ubuntu 24.04, 4 cores, 20 GB RAM, NVIDIA RTX A4000), .NET SDK 10.0.401, at commit 7b648759 (this
candidate apart from the contact address in LICENSE.md):

- Release build of the whole solution with 0 warnings and 0 errors.
- Full test suite: GridSim.Tests 12,736 passed, 0 failed, 22 skipped (OpenCL, ROCm, Windows-only and absent external
  data folders); GridSim.App.Tests 376 passed, 0 failed; GridSim.Scripting.Tests 192 passed, 0 failed.
- The determinism slice passed in all three CPU modes: normal (AVX2 with FMA), `DOTNET_EnableAVX2=0` and
  `DOTNET_EnableHWIntrinsic=0`.

**Not done for RC-4, and why.**

- No comparison with a commercial tool (PSS/E, PowerWorld, PowerFactory, PSCAD/EMTP): no licence is available. No
  parity with any of them is claimed; every such statement is COMPARATOR-UNVERIFIED.
- The new work is tested on small analytical, constructed and synthetic cases; no large or published utility case has
  been run through it.
- No digest-by-digest comparison of Windows and Linux results has been made; the Linux determinism evidence is the
  determinism test slice in three CPU modes.
- The large cases skipped by the RAM gate on an 8 GB machine.

## Known limits

- AWAITING STANDARD TEXT (paid standard not read): ENA ER G74 Issue 2 examples and converter rule; IEEE C37.010 and
  C37.013 multipliers; IEC 61000-4-15 flickermeter coefficients; IEC 61000-3-12; IEC 61363, 61660 and 60364-5-52 with
  BS 7671; VDE-AR-N 4105, 4110, 4120, 4130 and BDEW limits; the IEC 60255-151 and IEEE C37.112 constants (taken from
  secondary sources); vendor fuse and recloser tables. Affected outputs carry the label.
- AWAITING FILES: PowerFactory DGS exports (`dgs-import` is tested on synthetic files only) and the ENTSO-E CGMES
  conformity bundle.
- UNCONFIRMED: RBTS and RTS reliability data and the published IEEE 4-node and 123-node values.
- Core only, no command line surface yet: partitioned EMT runs and RMS-EMT co-simulation.
- GPU paths are deterministic run to run but not bit-identical to the CPU path; they are held to solver tolerance.
  A `--gpu` run may solve some or all Newton steps on the CPU, and the `GPU:` line says how many.
- Not in RC-4, by design: PSS/E v35 node-breaker records (incomplete); coupled harmonic power flow, harmonic state
  estimation and the RoCoF filter (experimental); DNP3 and GOOSE; partitioned `emt3` runs and grid-forming and
  grid-following inverters (they need new numerics); `PROTECTION FULL` in scripting; web studies beyond `POST /solve`;
  any LLM integration.
- Adaptive dynamics covers the reduced swing model only. `modal-full` reports AcExciter, Esst4b, rate-limited IEEEG1 and
  fixed delays as NOT LINEARISED.
- `gridsim rpc` has no authentication. It is a local tool and refuses a non-loopback listener unless
  `--allow-non-loopback` is given.

The items needed to close the first three groups are listed in the request for information
(`RFI-STANDARDS.md`, kept with the release evidence).

## Reporting bugs

Send every bug report to **mark@gridsimdigitaltwin.co.uk**. Please give the version (`gridsim --version`), the operating system,
the command or the steps, the case (or a small case that shows the problem) and what you expected.
