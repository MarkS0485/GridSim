# GridSim

[![Docs site](https://github.com/MarkS0485/GridSim/actions/workflows/pages.yml/badge.svg?branch=main)](https://marks0485.github.io/GridSim/)
[![CI](https://github.com/MarkS0485/GridSim/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MarkS0485/GridSim/actions/workflows/ci.yml)
[![CodeQL](https://github.com/MarkS0485/GridSim/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/MarkS0485/GridSim/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/MarkS0485/GridSim?include_prereleases&sort=semver&label=release)](https://github.com/MarkS0485/GridSim/releases)
[![Licence: source-available](https://img.shields.io/badge/licence-source--available-blue)](LICENSE.md)
[![Documentation](https://img.shields.io/badge/docs-marks0485.github.io%2FGridSim-informational)](https://marks0485.github.io/GridSim/)

**Documentation site:** <https://marks0485.github.io/GridSim/> (user manual, technical reference, validation and
developer notes, searchable). **Wiki:** <https://github.com/MarkS0485/GridSim/wiki> (getting started, install, CLI
quick reference, FAQ). **Questions:** [Discussions](https://github.com/MarkS0485/GridSim/discussions).

A physics-first **power-grid simulator** in C# / .NET 10. It starts from a small core (a per-unit network model, the
complex bus admittance matrix and a Newton-Raphson load flow) and grows it, deliberately and with every step tested,
into a broad grid stack:

- steady state: AC power flow (sparse and dense Newton-Raphson, backward/forward sweep for radial feeders, and
  fast-decoupled XB/BX and Gauss-Seidel engines by choice), reactive
  limits, tap changers, distributed slack and switched shunts, area interchange control and remote voltage
  regulation, DC power flow, distribution factors, DC-OPF,
  continuation power flow, QV modal analysis and loss allocation;
- security: N-1, N-2 and N-1-1 screening, generator-trip and infeed-loss screening, stress to collapse;
- state estimation: weighted least squares and its robust family (Huber, exact LAV, Schweppe-GM, QR, constrained and
  physically feasible), bad-data identification, observability, a residual-cloaked false-data-injection screen, linear
  PMU estimation, and dynamic estimation (EKF, UKF, EnKF) in a resident real-time service;
- dynamics: frame-by-frame inertia and frequency (lumped and distributed multi-domain), a dynamic-model library
  (GENROU, GENSAL, GENTPJ and GENCLS machines, thirteen exciters, five stabilisers, seven governors, induction motor, ZIP
  recovery and composite (CMLD, with DER_A distributed generation) dynamic loads, the WECC renewable models, IEC 61400-27-1
  style Type 4A and 4B wind turbines (built from public sources: Dynawo and the CGMES DY definitions, not checked against
  the IEC text), and SVC (CSVGN1), STATCOM, LCC HVDC,
  VSC HVDC and multi-terminal DC grid (MTDC1, with an averaged MMC) models). PSS/E DYR files are read for these models (not the IEC wind turbines, which have no PSS/E record); the extended ones need `--dyr-extended`, and GENTPJ, PSS2C, CIMW,
  CMLD, the STATCOM and the VSC link are not read from DYR. Studies include multi-machine RMS transient stability and
  critical clearing time (with a user-chosen step schedule or an error-controlled step for the full model), long-term
  quasi-steady-state simulation (one system frequency, AGC, tap changer timers, recovering loads), declarative user models (with injection, load and relay roles, an expression block and whole-step relay timers) and .NET
  model plug-ins loaded only from a hash-pinned, confined configuration behind a determinism check (governors, exciters,
  stabilisers, injections and loads, and, under contract 2, Norton-source machines and relays; injection, machine and
  load models also run in a dynamics study without a DYR record of their own, and user relays, on a unit or a branch, run
  in its full-model companion run),
  small-signal modal analysis (dense and Arnoldi), impedance-based (generalised Nyquist) stability, and
  electromagnetic transients in one and three phases;
- faults and protection: IEC 60909 short-circuit duty, sequence-network, series and simultaneous faults, the ENA G74,
  ANSI, IEC 61363 and IEC 61660 duty studies, IEEE 1584 arc flash, IDMT overcurrent grading, placed relays, fuses and
  reclosers (50/51, 50N/51N, 67/67N/67Q, 21 (mho, quad), 87L, 87T and 87B evaluated on three-phase and unbalanced
  faults, with grading-margin, zone-reach, earth-fault sensitivity, busbar zone completeness and differential stability
  checks; distance schemes DUTT, PUTT, POTT and DCB, zero-sequence mutual compensation, load blinders, switch-onto-fault,
  power-swing blocking on an RMS trace, and distance through a two-winding transformer; transformer differential with
  vector-group and zero-sequence compensation; low-impedance busbar differential with a check zone), a passive relay
  overlay with cascade checking, and per-asset generator protection;
- harmonics: frequency-domain penetration, impedance scans, THD and IEEE 519 / G5/5 assessment;
- battery storage: cell-to-pack models, ageing and an LP scheduler;
- an evidence engine that reconstructs recorded GB events and runs grounded counterfactuals on them;
- study jobs: study cases with variations and scenarios, sensitivities, probabilistic and network reliability studies,
  overhead line and single-core cable constants (core, sheath and armour; both-ends, single-point and cross-bonded),
  cable sizing and rating, flicker and filter design, motor starting (direct-on-line, star-delta,
  autotransformer, reactor, soft starter and VFD, one motor or a group), and a scripting language with a
  console and a STUDIES extension that runs contingency, short-circuit, OPF, SCOPF, RMS and critical clearing time
  studies from a script;
- programmatic access: a Python package over the CLI's result files, a resident `rpc` endpoint whose level 2 is an
  editable object model of the case (buses, generators, loads, branches, shunts: read, write, add and delete), and a
  psspy-style layer for a listed subset of psspy calls;
- the real GB network from published data, three-phase distribution feeders (radial and meshed, with neutral and
  earthing), and international starter maps;
- interfaces: MATPOWER, JSON v1/v2, PSS/E RAW (revisions 30 to 36, with node-breaker topology), RAWX, DYR and SEQ,
  UCTE-DEF, PowerFactory DGS import, CIM/CGMES (import, per-profile export with the DL, GL, SC and DY profiles, CGMES
  3.0 bus-branch export, node-breaker three-winding transformers, and conformity checks including a native SHACL Core
  checker with a census of the features a shapes set uses), IEC 61850 SCL substation import (topology), COMTRADE
  read and write, PyPSA, C37.118 synchrophasors, IEC 60870-5-104 and Modbus;
- a web app, a desktop control room, a workbench with a single-line-diagram editor, a case editor, and a swappable
  CPU/GPU solve backend.

**Downloads:** the latest release is [2.1.105 RC-5](https://github.com/MarkS0485/GridSim/releases/tag/v2.1.105-RC-5),
with ready-built Windows, Linux and macOS (Apple silicon) binaries. See [Downloads](#downloads).

> **Status: unvalidated, and stated plainly.** Nothing in GridSim is validated, at any scale, for any purpose.
> GridSim is written by Mark, an HPC engineer by trade. He has no electrical-engineering training and is not
> qualified to validate power-system software or to model power systems; his role is the code. What the repository
> does carry is evidence you can check for yourself: bit-deterministic results, a large test suite, cross-checks
> against independent open-source tools and benchmark timings, each with its source. GridSim claims no accuracy, no
> parity with any commercial tool and no fitness for production, planning or operational use.
>
> **Use it, and tell us what you find.** Results, comparisons with tools you trust, and bug reports are all wanted:
> send them to **mark@gridsimdigitaltwin.co.uk** (see [Reporting bugs](#reporting-bugs)).

Every numerical result is double precision end to end and deterministic to the bit. The core solvers are cross-checked
against independent open-source tools (pandapower, `pandapower.estimation`, SciPy, published MATPOWER solutions): the
power-flow, harmonic, impedance and small-signal cross-checks gate at 4096 machine epsilons; the state-estimation,
swing-equation, classical-swing and lake-conformance checks compare different algorithms or stopping rules and gate at
stated engineering tolerances (1e-6 pu for the estimator by default). The measured agreement is in
[Cross-checks](#cross-checks). Agreement with another tool shows that the same equations were solved the same way; it
is not validation against a real power system.

> **Submodules.** This repo contains submodules. No submodule is ever needed for the work: submodules augment and add,
> they cannot change the core maths. The 36 national and regional network models are the `models` submodule: clone
> with `git clone --recurse-submodules`, or run `git submodule update --init models` in an existing clone. See
> [`docs/developer/submodules.md`](docs/developer/submodules.md) for more information.

---

## Contents

- [What makes GridSim different](#what-makes-gridsim-different)
- [Downloads](#downloads)
- [Quick start](#quick-start)
- [Feature tour](#feature-tour)
- [The maths](#the-maths)
- [Determinism and machine epsilon](#determinism-and-machine-epsilon)
- [Cross-checks](#cross-checks)
- [Performance and GPU](#performance-and-gpu)
- [Networks and data](#networks-and-data)
- [The apps](#the-apps)
- [Architecture](#architecture)
- [Repository layout](#repository-layout)
- [Assurance and security](#assurance-and-security)
- [Known limits](#known-limits)
- [Roadmap](#roadmap)
- [Documentation](#documentation)
- [Licence](#licence)
- [Reporting bugs](#reporting-bugs)
- [Contributing](#contributing)

---

## What makes GridSim different

1. **Physics checks.** Every converged state can be audited against the conservation laws it must satisfy (energy,
   Ohm's law, Joule heating, KCL, complex-power balance). A state that passes the solver's tolerance but breaks a law is
   reported. Non-converged or invalid results are never reported as success.

2. **Inertia is modelled per machine, not as one scalar.** The inertia model is a runtime seam (`IInertiaModel`): a
   lumped centre-of-inertia model or a distributed multi-domain model. Each machine is classified by technology as a
   synchronous swing mass, a synthetic-inertia domain or a pure inverter. The distributed model reports a dominance
   read-out and collapses exactly to the lumped model in the single-domain limit.

3. **The steady state needs no swing equation.** The power flow solves voltages, angles and flows from the admittance
   matrix and the injections alone, so a grid with zero rotational inertia solves like any other. The swing equation is
   a separate, opt-in layer for transients.

4. **A historical evidence engine.** A state estimator and counterfactual engine estimate what the grid did from public
   data. A counterfactual runs only after the baseline reproduces the recorded event. The 169-hour no-forward-inference
   horizon is enforced in code at every dated ingester.

5. **Bit-identical results.** Results are identical across runs, parallel paths, CPU vector tiers and the dense and sparse
   power-flow engines, using GridSim's own deterministic maths library ([`docs/developer/deterministic-maths.md`](docs/developer/deterministic-maths.md)). The tests are
   constitutional: a change that breaks one is wrong ([`CONTRIBUTING.md`](CONTRIBUTING.md)).

6. **Double precision throughout.** Power flow is `double` end to end. The GPU backend is AMD-first (ROCm), because the
   solves are FP64.

---

## Downloads

GridSim ships ready built for anyone who does not want to compile it. The latest release is
[2.1.105 RC-5](https://github.com/MarkS0485/GridSim/releases/tag/v2.1.105-RC-5); its notes are in [RC5.md](RC5.md)
(RC-4 is in [RC4.md](RC4.md), RC-3 in [RC3.md](RC3.md)). All builds are self-contained: no .NET install
is needed to run them.

| What | Platform | File on the release page |
|---|---|---|
| Command line tool (`gridsim`) | Windows x64 | `gridsim-<version>-win-x64.zip` |
| Command line tool (`gridsim`) | Linux x64 | `gridsim-<version>-linux-x64.tar.gz` |
| Command line tool (`gridsim`) | macOS arm64 (Apple silicon) | `gridsim-<version>-osx-arm64.tar.gz` |
| Desktop app (Avalonia) | Windows x64 | `gridsim-app-<version>-win-x64.zip` |
| Desktop app (Avalonia) | Linux x64 | `gridsim-app-<version>-linux-x64.tar.gz` |
| Desktop app (WPF) | Windows x64 | `gridsim-wpf-<version>-win-x64.zip` |
| CycloneDX SBOMs (tool, apps, core library, web image) | Any | `*.sbom.json` |
| SHA-256 checksums of every file | Any | `SHA256SUMS.txt` |

Check every download against `SHA256SUMS.txt`. The `GridSim.Core` NuGet package and the container images (the web app
and the command line tool) are on the roadmap, and nothing of them is published yet. The groundwork exists: the two
Dockerfiles, which you can build yourself (see [the deployment notes](docs/developer/deploy/README.md)), and release
jobs that build the package and both images but are switched off, pending broader validation. Shipping GridSim as a code
library also needs a safe way to handle licences, which is still to come. [Manual ch. 1](<docs/manual/0001 Getting started.md>)
says how to check signatures on what is published. There is no macOS desktop app build: Mac users build the desktop app from source with Xcode
and the .NET SDK (exact commands in [RC1.md](RC1.md)). System requirements are in [RC1.md](RC1.md) and unchanged
since. Earlier releases stay available on the [Releases](https://github.com/MarkS0485/GridSim/releases) page.

`GridSim.Gda` and the GDA data are not release artefacts: there is no `GridSim.Gda` package and no GDA data in any
download. The `gda` verbs stay in the CLI, but they read a GDA data lake (`GDA_DATA_ROOT`), which a release does not
include.

---

## Quick start

### Build and test

You need the .NET 10 SDK. From the repository root:

```
dotnet build GridSim.slnx -c Release
dotnet test  GridSim.slnx -c Release --no-build
```

The build is warning-free by contract (`TreatWarningsAsErrors`). The examples below are written as `gridsim <verb>`:
that is the release binary, or from source `dotnet run --project src/GridSim.Cli -c Release -- <verb>`. Every verb
answers `--help` with its usage, flags, defaults and exit codes, and the full reference is
[`docs/manual/0019 CLI reference.md`](<docs/manual/0019 CLI reference.md>).

### Solve a network

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
  2                                 345   1.025     9.28     163.0       6.7
  3                                 345   1.025     4.66      85.0     -10.9
  4                                 345   1.026    -2.22      -0.0       0.0
  5                                 345   1.013    -3.69     -90.0     -30.0
  6                                 345   1.032     1.97       0.0      -0.0
  7                                 345   1.016     0.73    -100.0     -35.0
  8                                 345   1.026     3.72       0.0       0.0
  9                                 345   0.996    -3.99    -125.0     -50.0
```

`case9` carries its WSCC nominal frequency, 60 Hz; a power flow does not use the frequency.

Other built-in cases that run this way: `case14`, `case30`, `case57`, `case118`, `case300`, `case145`, `case24` (RTS),
`cigre` and `uk` (the GB reduced 10-zone model, live with no argument). Any MATPOWER file runs the same way, for example
`gridsim data/matpower/pegase/case9241pegase.m --once`, and `--dir <case-dir>` loads a JSON case: the full GB network
is `gridsim --dir data/gb-full --once` and the synthetic GB network `gridsim --dir data/gb-synthetic --once`. The
names `gb-full` and `gb-synthetic` are for the verbs (`gridsim pf gb-full`), not this bare form.

### Estimate the state from measurements

```
$ gridsim estimate case14 --synth

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg
```

`--synth` samples noisy telemetry from a solved truth; `--measurements <m.json>` reads a dated measurement set (it
must be older than the 169 h horizon). `--method` selects `wls`, `constrained`, `feasible`, `huber`, `lav` (exact LP),
`lav-irls`, `schweppe-gm` or `qr`, and `--fdia-screen` adds the false-data-injection screen.

### Cross-validate against pandapower

```
gridsim crossval case14
```

This solves the case, exports the state and re-solves it independently in pandapower (`pip install pandapower`; the
verb exits 77 if it is missing). The measured agreement on the reference cases is in [Cross-checks](#cross-checks).

### Replay a recorded event

```
$ gridsim replay-event gb-2019-08-09

=== Replay: GB 9 Aug 2019 disturbance (Hornsea + Little Barford) ===
  when (UTC):        2019-08-09T15:52:33.0000000Z   provenance: DerivedFromRecorded (event tagged Recorded; the weakest of its parts)
  pre-event inertia: 215.0 GVA.s   demand: 21.1 GW   total loss: 2768 MW
  inferred inertia from recorded RoCoF: 223.7 GVA.s (cross-check vs 215.0)

  RoCoF:  sim  -0.171   recorded  -0.170 Hz/s   (1 % error)
  Nadir:  sim   48.84   recorded   48.80 Hz   (0.04 Hz error)
  Nadir envelope (declared-uncertainty corners): [48.20, 49.17] Hz - recorded INSIDE (consistent within the model's own error bars)
  RMSE vs trace: n/a (no 1-sec NESO trace attached - data-gated)
  assessment: BELOW STATUTORY (49.5 Hz)

  VERDICT: REPRODUCED  - baseline reproduces the record; counterfactuals unlocked.
```

The `when` line gives the replay's effective provenance, the weakest of its parts: a built-in recorded event reads
`DerivedFromRecorded`, with the event's own tag in brackets when the two differ.

Once the baseline reproduces the record, `scenario`, `fleet`, `h0-study` and `evidence` run counterfactuals on it, for
example `gridsim scenario --event gb-2019-08-09 --device-inertia 40 --device-ffr 1.5`.

### Optimal dispatch, short circuit, harmonics and EMT

```
$ gridsim dcopf case9
DC-OPF - IEEE 9-bus (WSCC) (9 buses)  [cheapest secure dispatch]
  demand 315 MW  generation 315 MW  total cost 15,750 GBP/h  (unconstrained merit order)
  dispatch (by output):
      gen bus 3         270.0 MW  @    50 GBP/MWh
      gen bus 2          35.0 MW  @    50 GBP/MWh
      gen bus 1          10.0 MW  @    50 GBP/MWh

$ gridsim iec60909 case9 --bus 5
IEC 60909 - IEEE 9-bus (WSCC), bus 5, three-phase, Maximum (60 Hz, tb 0.1 s, Tk 1 s)
  Ik''  = 1.4683989584292985 kA  (8.774528526143639 pu)
  ip    = 4.143787818206106 kA  (kappa 1.995438943368623, R/X 0.10501342238071008, meshed network: 1.15 x kappa(R/X), Method B)
  Ib    = 1.4683989584292985 kA    Ik = 1.4683989584292985 kA
  Ith   = 1.4881526648788939 kA  (m 0.027086065637215752)    idc(tb) = 0.03963009392407432 kA
  Zth   = 0.013092788564404878 + j0.12467728665140516 pu

$ gridsim harmonics case9
harmonics: case IEEE 9-bus (WSCC), 1 source(s), orders 24
  synthetic source : 6-pulse-g5 at bus 9, 0.5 pu fundamental (G5 Issue 5 Table B.3 six-pulse profile, the default; --synth-spectrum ideal for the idealised 1/h)
  worst voltage THD : 6.31% at bus 9 (relative to the solved fundamental voltage)
  resonance (bus 9): order 7, |Z|=0.658 pu, sharpness 3.8
  fine sweep peak  : h=7.4750 (448.50 Hz), |Z|=0.688 pu, positive sequence
  IEEE-519         : 0/9 buses compliant

$ gridsim emt case9 --duration 0.01 --dt-us 20 --probe v:5
emt: 2 floating node(s) pinned at 0 V in the initial-condition solve.
emt case9: 500 steps @ 20us, 1 factorization(s), peak |probe| 1.079, 3 ms -> results\emt-out
```

`case9` is studied at its 60 Hz nominal frequency, and `iec60909` uses the meshed-network peak factor (Method B).
`harmonics` injects the G5 six-pulse profile at bus 9 by default and names it on the `synthetic source :` line.

`gridsim stability case9 --rms` finds the critical clearing time with the subtransient machine engine,
`gridsim modal case9` gives the small-signal eigenvalues, `gridsim acopf case9` and `gridsim cpf case9` run the
loss-loop dispatch and the loadability limit, and `gridsim nminus1` screens every single outage on the GB reduced
model.

### Three-phase feeders and real-time estimation

```
gridsim dist3ph data/dist3ph/ieee123.json
gridsim serve case9 --measurements-dir <dir> --method wls
```

`dist3ph` solves an unbalanced three-phase feeder (the IEEE 123-node feeder and the IEEE 4-node transformer test
feeders are bundled) and also runs faults, estimation and harmonics on it. `serve` is the resident real-time
estimator: it streams measurement windows through WLS, EKF, UKF or EnKF, and refuses any window newer than the
horizon:

```
$ gridsim serve case9 --measurements-dir results/realtime/windows --method wls
serving wls estimator over 4 window(s) from results/realtime/windows
horizon guard: now=2026-10-04T18:39:00.7522631Z, newest processable=2026-09-27T17:39:00.7522631Z
  #0 2024-03-01T12:00:00.0000000Z OK J=47.35 chi2=PASS
  #1 2024-03-01T12:01:00.0000000Z OK J=61.17 chi2=PASS
  #2 2024-03-01T12:02:00.0000000Z OK J=44.61 chi2=PASS
  #3 2024-03-01T12:03:00.0000000Z OK J=53.41 chi2=PASS
done: processed 4, converged 4, rejected-fresh 0, failed 0
```

### The web app and the desktop app

```
docker compose up -d --build                  # replay-only web app on http://127.0.0.1:5540/
dotnet run --project src/GridSim.Web          # from source, on http://localhost:5180
dotnet run --project src/GridSim.Wpf          # the WPF desktop control room (Windows)
dotnet run --project src/GridSim.App.Desktop   # the Avalonia desktop app (Windows, Linux)
```

---

## Feature tour

Each area lists its verbs and the chapters that document it: the [user manual](<docs/manual/0000 Contents.md>) says
how to use it, the [technical manual](<docs/technical/0000 Contents.md>) says how it works, with the equations, the
invariants and the tests that pin them.

### Power flow

AC power flow by Newton-Raphson in polar form. The sparse engine (Gilbert-Peierls LU over an exact minimum-degree
column ordering, which replaced reverse Cuthill-McKee in 2.1.073) runs every meshed network by default; the dense engine is selectable and gives the same bits. Radial
feeders use the backward/forward sweep (`--bfs`). Reactive limits switch PV buses to PQ, on-load tap changers
regulate in an outer loop, and `pf` adds distributed slack, switched shunts, area interchange control (tie lines,
metered ends as in PSS/E) and remote voltage regulation, from a file or from a PSS/E case's AREA and IREG data.
`pf --engine fdxb|fdbx|gs` solves with the fast-decoupled load flow (XB or BX, B' and B'' as MATPOWER builds them) or
Gauss-Seidel instead. They use Newton's convergence test, so a converged answer is Newton's solution to the tolerance;
Newton stays the default. Every solve can be exported with its
full trace (Y-bus, specification, every iteration and Jacobian) for independent checking. `reduce` builds Ward, extended
Ward and REI network equivalents from a solved case: the kept buses stay as they are, the external network becomes
boundary branches, shunts, injections and fictitious sources, and the base case solves the reduced case to machine
precision (1e-12 pu on case14). `reduce --method sc` builds the short-circuit equivalent (the IEC 60909 sequence networks
reduced, so fault currents at the kept buses match the full case to 1e-12), and `reduce --method dynamic` the
coherency-based dynamic equivalent (slow-coherency grouping and inertial aggregation into classical machines).
Verbs: (no verb), `pf`, `reduce`, `batchsolve`, `export`. Chapters:
[manual 5](<docs/manual/0005 Running power flows.md>),
[technical 2](<docs/technical/0002 Data model and per-unit conventions.md>),
[technical 3](<docs/technical/0003 Power flow.md>),
[technical 4](<docs/technical/0004 Sparse and GPU numerics.md>),
[technical 40](<docs/technical/0040 Network equivalents.md>).

### DC power flow, OPF, CPF, taps and losses

DC power flow and the PTDF / LODF distribution factors, with MATPOWER's DC model (transformer tap ratios, phase
shifters and bus shunt conductance included; verified against an independent NumPy transcription of MATPOWER `makeBdc`
to 4096 machine epsilon); a DC optimal power flow (bounded simplex, security-constrained,
quadratic and costed forms, with nodal prices); `acopf`, a DC-OPF dispatch with an AC loss loop; `acopf-full`, the full
AC optimal power flow (interior point, with voltage, reactive and branch MVA limits); `scopf`, a preventive
security-constrained DC OPF over single-branch outages; `acopf-opt`, the full AC-OPF with a loss or reactive power
objective and continuous taps, shunts and phase-shifter angles, rounded to their steps (nearest point, or the best of
the floor/ceil neighbours); `acscopf`, a preventive or corrective AC SCOPF by constraint generation with an AC screen,
over branch, N-2 and generator outages; `reconfig`, feeder reconfiguration for loss reduction by branch exchange
(it reaches the published case33bw optimum); `dist-opt`, distribution optimisation: capacitor placement and sizing
against bank cost, volt/VAr optimisation and conservation voltage reduction over OLTC taps and switched banks, and
phase balancing of single-phase loads checked by an unbalanced solve; `scuc`, security-constrained unit commitment (mixed-integer,
GridSim's own branch-and-bound); continuation power flow for maximum loadability; coordinated OLTC scheduling; loss
allocation; and a black-start restoration sequence check. `qv-modal` is QV modal analysis (Gao, Morison and Kundur):
the eigenvalues of the reduced Q-V Jacobian at the solved state and each mode's bus participation factors.
Verbs: `dcpf`, `ptdf`, `lodf`, `dcopf`, `acopf`, `acopf-full`, `acopf-opt`, `scopf`, `acscopf`, `reconfig`, `dist-opt`, `scuc`, `cpf`, `taps`, `losses`, `restore`, `qv-modal`.
Chapters:
[manual 6](<docs/manual/0006 OPF, CPF, taps, distribution factors and losses.md>),
[technical 29](<docs/technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).

### Contingency and security analysis

Branch, generator and bus N-1, N-2 and N-1-1 screening against thermal and voltage limits, a parallel N-1 sweep with
throughput, infeed-loss screening against GB limits, generator trips with RoCoF, nadir and settling, stress to
voltage collapse, warm-started chains of consecutive solves and a prioritised analysis cycle.
`nminus1` and `nminus2` also run user-authored remedial action schemes (`--ras`, a `gridsim-ras/1` file): armed by the
outage and the pre-contingency state, triggered by overloads, voltages or a failed solve, acting by trips, load shedding,
redispatch, shunts and taps, level by level, with the state after the schemes judged in the verdict (steady state).
Verbs: `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `screen`, `trip`, `stress`, `chain`, `cycle`. Chapters:
[manual 11](<docs/manual/0011 Contingency analysis.md>),
[technical 23](<docs/technical/0023 Contingency and security analysis (N-k).md>).

### State estimation

The classical estimator `z = h(x) + e` solved by Gauss-Newton on the normal equations, with a QR alternative, a
trust-region step cap and a choice of method: weighted least squares, Huber, exact least absolute value (an LP),
IRLS LAV, Schweppe-GM with leverage down-weighting, the constrained (KKT) form and the physically feasible form, which
rejects a converged state that breaks a hard physical law. Around it: observability analysis, the chi-square
consistency test, largest normalised residual and hypothesis-testing bad-data identification (with optional largest
normalised residual detection for errors the chi-square test passes), state covariance, a
noise-floor study, and the FDIA screen, which flags a measurement-consistent estimate whose state is physically
implausible (the signature of a residual-cloaked false-data injection). `pmu-estimate` runs the linear
synchrophasor estimator.
Verbs: `estimate`, `estimate-noise-floor`, `pmu-estimate`, `crossval`. Chapters:
[manual 21](<docs/manual/0021 State estimation.md>),
[manual 22](<docs/manual/0022 Estimating GB history.md>),
[technical 6](<docs/technical/0006 State estimation.md>),
[technical 16](<docs/technical/0016 Physics conformance and the constrained estimator.md>).

### Real-time and dynamic estimation

`serve` runs a resident estimator over a stream of measurement windows: static WLS, or dynamic state estimation with
an extended, unscented or ensemble Kalman filter, plus a frequency and RoCoF filter. Every window passes the horizon
guard first, and the run is recorded in the audit log. The web host runs the same engine as a hosted service.
Verbs: `serve`. Chapters:
[manual 23](<docs/manual/0023 Replaying estimated states.md>),
[technical 18](<docs/technical/0018 Real-time, distribution and dynamic estimation.md>).

### Three-phase distribution

Unbalanced three-phase feeders with single- and two-phase laterals, regulators, switches and capacitor banks: a
three-phase backward/forward sweep, sequence-network fault studies, three-phase WLS and branch-current state
estimation, and three-phase harmonic penetration. `dist3ph` is the radial sweep and stays marked experimental.
`dist3ph-mesh` is the meshed unbalanced load flow (Newton on the three-phase network with explicit neutral, earthing,
split-phase and SWER, step regulators and unbalanced time series); it is tested on the IEEE 4-node cases and the IEEE
123-node feeder through the Core solver.
Verbs: `dist3ph`, `dist3ph-mesh`. Chapters: [manual 7](<docs/manual/0007 Three-phase distribution feeders.md>),
[technical 18](<docs/technical/0018 Real-time, distribution and dynamic estimation.md>).

### Frequency and inertia

The lumped centre-of-inertia swing with governor droop and load damping, and the distributed multi-domain model with
Kron-coupled domains, propagation delay and dominance; grid-forming inverter support; NESO 1-second frequency
statistics; terminal frequency replay; distributed-inertia domains; and fitting swing constants to a recorded trace.
Verbs: `freq`, `replay`, `transient`, `domains`, `fit-frequency`. Chapters:
[manual 14](<docs/manual/0014 Frequency studies.md>),
[technical 5](<docs/technical/0005 Frequency dynamics.md>),
[`docs/validation/distributed-inertia.md`](docs/validation/distributed-inertia.md).

### RMS dynamics, small-signal and impedance stability

The machine hierarchy from classical through one-axis and two-axis to subtransient (GENROU) with saturation, with GENCLS
and GENTPJ on the RMS engine; the exciters, stabilisers and governors (SEXS, IEEET1, EXAC1, ESAC1A, ESST4B, ESST1A, EXST1,
SCRX, ESDC1A, EXDC2, IEEEX1, ESAC8B, ESST3A, PSS1A, IEEEST, PSS2A, PSS2B, PSS2C, TGOV1, IEEEG1, IEEEG3, GAST, HYGOV, GGOV1, WSIEG1), the dynamic loads (a third-order
induction motor, a ZIP load with exponential recovery and a CMLD composite load: motors A to C, the WECC air-conditioner
motor D with its stall and protection logic, the electronic load, the static load, the substation transformer with its tap
changer and the feeder, and DER_A distributed generation at the far end) and the WECC renewable models (REGC_A and B, REEC_A
to D, REPC_A, the WTG set, DER_A, REGFM_A1), the IEC 61400-27-1 style Type 4A and 4B wind turbines (Dynawo's 2020
structure: generator system, active and reactive control with the fault ride-through states, current limiter, two-mass
drive train for 4B, PLL and grid protection; implemented from public open-source implementations and the CGMES DY
definitions, not checked against the IEC 61400-27-1 text; given in a `dynamics-study` file), and the device models: the CSVGN1 static var compensator, a STATCOM, a
quasi-steady two-terminal LCC link after CDC4T (current order and VDCOL, blocking and recovery ramps), a P/Q-controlled
two-terminal VSC link, and an RMS multi-terminal DC grid on the case's multi-terminal links (DC bus voltages from stored
energy, power, DC voltage and droop control, and an averaged MMC with an optional energy controller; the power flow solves
the link with PSS/E's line-commutated converter equations, and the RMS grid starts there with lossless voltage-source
converters), each starting on its power-flow state and checked against analytical steady states and step
responses (not against PSS/E or PowerFactory runs). PSS/E DYR import maps this set (the extended models, and the CIM5BL motor, with `--dyr-extended`)
and reports every other model as not run. Each new model starts at rest at the solved point. The exciters EXST1, ESDC1A and ESST1A and the stabilisers IEEEST and PSS2A follow a SciPy
reference integration of their IEEE 421.5 block diagrams; GGOV1 and WSIEG1 (turbine-governor models, not IEEE 421.5), the third-order induction motor, the ZIP recovery load, the CMLD subset and the CMLD with motors B and D and the electronic load (above motor D's breakdown voltage and the electronic load's Vd1; motor D's low-voltage run curve, its stall and the electronic load's tripping are checked against closed forms instead) follow a SciPy reference integration
of their own block diagrams or equations (`tests/tooling/dynamic-library-reference.py`). ESAC1A, SCRX, PSS2B, GENCLS and
GENTPJ have no SciPy reference; their tests cover the limits, the start at rest, the DYR mapping and, for GENTPJ with no
saturation increments, bit-identity with GENROU. The library is a PSS/E-compatible subset, not the full PSS/E
library: other models PSS/E ships (for example ESAC7B, HYGOV4, GENQEC and the full CMLD) are not in it, PSS2C is read from a
`gridsim-dynamics/2` file only (its DYR record order has one public source), a saturated GENTPJ
uses GENROU's saturation form, and `user-model` can express new controller blocks. RMS faults are three-phase, single line to ground,
line to line and double line to ground, bolted or through a fault impedance, at a bus or part-way along a line, by
the balanced (positive-sequence) RMS method of PSS/E and PowerFactory; at the fault instant they equal the static unbalanced fault
study. The multi-machine RMS transient-stability engine with critical clearing time search. The full model runs on a fixed
step, on a step schedule (a different fixed step in each window; one window is the fixed-step run bit for bit), or with an
error-controlled step that chooses the windows by step doubling over whole runs, so every event lands on its own time.
Long-term studies run by quasi-steady-state simulation (`long-term`): a power flow with one system frequency at each step,
with governor droop, single-area AGC, tap changer timers and exponential recovery loads, checked against the exact
discrete solutions of those recurrences (not against PSS/E or PowerFactory long-term runs). Small-signal eigenvalues, damping
and participation factors; and impedance-based stability with the generalised Nyquist criterion and critical short
circuit ratio. A grid-code compliance suite checks one unit against the GB Grid Code ECC (Issue 6 Revision 33) or RfG
(Regulation (EU) 2016/631): fault ride-through at the code's retained voltage on the RMS engine, checked against the
voltage-against-time line, the retained-voltage fault reactance,
active-power recovery, undervoltage settings against the line, reactive capability at the PCC, LFSM-O, LFSM-U and FSM by
governor frequency injection, RoCoF withstand and frequency withstand, each with its clause, a signed margin and a
COMPLIANT, NON-COMPLIANT or INCOMPLETE verdict; RfG choices are refused outside the Regulation's ranges. GB studies can
also check fast fault current injection against the ECC.6.3.16 envelope, ride-through of the voltage-duration profile for
faults over 140 ms, the open-circuit AVR step response and the PPM reactive response to a voltage step.
Verbs: `stability`, `modal`, `modal-full`, `dynamics-study`, `long-term`, `grid-code`, `user-model`, `impedance-stability`, `emt3`. Chapters:
[manual 15](<docs/manual/0015 RMS, modal and impedance stability.md>),
[technical 24](<docs/technical/0024 Standard dynamic-model library.md>),
[technical 30](<docs/technical/0030 Small-signal and impedance stability.md>),
[technical 39](<docs/technical/0039 Grid-code compliance.md>),
[technical 43](<docs/technical/0043 Long-term dynamics.md>).

### Electromagnetic transients

A Dommel-method EMT solver: trapezoidal and backward-Euler companion models, Bergeron travelling-wave lines, critical
damping adjustment, switching events and probes.
RMS-EMT co-simulation of an `emt3` study against a phasor source (`cosim`), and the partitioned parallel run cut at
Bergeron lines, bit-identical to the single-network run at any thread count (`emt-partition`).
Verbs: `emt`, `emt3`, `cosim`, `emt-partition`. Chapters: [manual 16](<docs/manual/0016 Electromagnetic transients (EMT).md>),
[technical 21](<docs/technical/0021 Electromagnetic transient (EMT) simulation.md>).

### Harmonics

Frequency-dependent network admittance (skin effect, long-line correction), harmonic source injection and
penetration, impedance scans and fine frequency sweeps for resonance, voltage THD, and compliance against IEEE 519 and
Engineering Recommendation G5/5. The coupled harmonic power flow with a Norton converter coupling (`harmonic-pf`) and
harmonic state estimation from metered bus voltage phasors, with normalised residuals and a chi-square bad-data test
(`harmonic-se`), both experimental.
Verbs: `harmonics`, `harmonic-pf`, `harmonic-se`. Chapters: [manual 17](<docs/manual/0017 Harmonic studies.md>),
[technical 19](<docs/technical/0019 Harmonics.md>).

### Geomagnetically induced currents

GIC for a uniform geoelectric field by the NERC application guide's DC network method: line sources from substation
coordinates, grounded-wye, autotransformer and delta windings, substation grounds and blocking devices, the effective GIC
of each transformer and its reactive loss, and a sweep over field directions. Checked against the closed form of a
two-substation loop and the guide's worked example: every current of its Tables B-6 and B-7 matches an independent
re-derivation to rounding, every published current of Table B-6 agrees to the printed digit, and Table B-7 to within
0.006 A (eight of its cells are one printed unit off, for a reason the guide's figures do not show).
Verb: `gic`. Chapter: [technical 44](<docs/technical/0044 Geomagnetically induced currents.md>).

### Faults, short circuit and protection

Sequence-network faults (three-phase, line-to-ground, line-to-line, double line-to-ground); the IEC 60909-0 duty study
(the equivalent voltage source, Ik'', ip by methods B and C, Ib with the near-generator breaking factor, Ith, the
decaying DC component, and the impedance correction factors with `--corrections`); IDMT overcurrent grading to IEC
60255 and IEEE C37.112 curves; a passive relay overlay; and cascade validation. Placed devices (`devices-check`,
`protection-audit`) are relays, fuses, reclosers and breakers: functions 50 and 51 are evaluated, and 21, 67, 50N and
51N are accepted in the settings file and reported as not evaluated. Further duty studies are `fault-series`,
`fault-simultaneous`, `fault-line`, `g74`, `ansi-sc`, `sc-61363`, `sc-dc` and `arc-flash`.
Verbs: `fault`, `iec60909`, `grade`, `protection-check`, `devices-check`, `protection-audit`. Chapters:
[manual 12](<docs/manual/0012 Faults, short circuit and protection grading.md>),
[technical 15](<docs/technical/0015 Protections.md>),
[technical 17](<docs/technical/0017 Protection assessment (a passive relay overlay).md>),
[technical 25](<docs/technical/0025 IEC 60909 short-circuit duty study.md>),
[technical 26](<docs/technical/0026 Overcurrent coordination (IDMT grading).md>).

### Battery storage

Cell equivalent-circuit models for six chemistries, packs, rainflow ageing and an LP storage scheduler, carried
through time series (`chain`, `gda-replay`).
Chapters: [manual 13](<docs/manual/0013 Battery storage studies.md>),
[technical 27](<docs/technical/0027 Battery energy storage.md>).

### The evidence engine

Recorded events with sourced figures pass a credibility gate (`replay-event`) before any counterfactual is allowed:
`scenario` sweeps device inertia and fast frequency response and draws a risk curve, `fleet` compares one large unit
with a fleet of small ones, `h0-study` asks what a zero-inertia grid would have done, and `evidence` assembles a
dossier. Every figure carries a provenance tag (recorded, derived, curated placeholder, synthetic, counterfactual).
Verbs: `replay-event`, `scenario`, `fleet`, `h0-study`, `evidence`. Chapters:
[manual 18](<docs/manual/0018 Replaying recorded events.md>),
[manual 20](<docs/manual/0020 Counterfactuals.md>),
[technical 7](<docs/technical/0007 The evidence engine.md>),
[technical 13](<docs/technical/0013 Asset-dynamics data grounding.md>),
[technical 14](<docs/technical/0014 Cascade validation and blind prediction.md>).

### The GDA bridge and GB history

GDA materialises GB networks from a data lake of public sources (DNO long-term development statements, NESO data,
CSV and Parquet), and `gda-replay` reconstructs the grid state at each past settlement period. `synth` builds a
synthetic GB-at-scale network; `zonal` reads the PyPSA-GB zonal model.
Verbs: `gda`, `gda-replay`, `synth`, `zonal`. Chapters: [manual 24](<docs/manual/0024 The GDA bridge.md>),
[technical 9](<docs/technical/0009 GDA integration.md>).

### Live telemetry: the RTU interposer and synchrophasors

`interpose` is a passive tap on IEC 60870-5-104 or Modbus traffic that decodes RTU frames into measurement windows;
live frames feed the estimator only after ageing past the horizon, and are held in a quarantine store outside the
evidence lake until then. `pmu-ingest` reads IEEE C37.118 synchrophasor data from a capture or a live stream.
Verbs: `interpose`, `pmu-ingest`, `pmu-estimate`. Chapters:
[manual 25](<docs/manual/0025 Interposing live telemetry.md>),
[technical 20](<docs/technical/0020 The RTU and SCADA interposer.md>),
[technical 33](<docs/technical/0033 C37.118 synchrophasor ingest.md>).

### Formats and interfaces

MATPOWER `.m` (import and export), JSON cases (v1 and the v2 schema with its own validator), PSS/E RAW (revisions 30,
33, 34, 35 and 36 read, 36 through its `@!` heading lines; 33 to 36 written, 36 with a heading line per record group;
version 35 node-breaker topology), PSS/E RAWX (JSON; read and written, with the DC link, FACTS, impedance correction,
multi-section line and inter-area transfer sections), PSS/E DYR dynamics and SEQ sequence data (revisions 30 and 33 to 36),
COMTRADE (IEEE C37.111-1999 and -2013 configuration files with ASCII and binary data, read, and written from simulation
trajectories; library only), UCTE-DEF (read, and written by `convert` to the precision of its fixed columns), PowerFactory DGS (import, tested on synthetic files only), CIM/CGMES
(import with `cgmes-study`, per-profile export with `cgmes-export`, including the diagram layout, geographical,
short-circuit and dynamics profiles of a bus-branch case with `--profiles`, a bus-branch case as CGMES 3.0 EQ, TP, SSH
and SV documents (with those profiles in 3.0 too) with `--model bus-branch --cgmes-version 3.0`, `cgmes-conform` checks,
SHACL Core checks of the ENTSO-E shape files with `cgmes-shacl` and a census of the features they use with
`cgmes-shacl --census`, and `convert` to RDF/XML), IEC 61850 SCL substation import (topology and equipment from the
substation section into the node-breaker model, electrical values from a sidecar file, with `scl-import`),
PyPSA-GB, C37.118, IEC 104 and Modbus. DNP3 and IEC 61850 GOOSE are link-framing scaffolding only: they have no
application layer and cannot be selected. Results go to the console or to JSON and CSV with `--json`, `--csv` or `--out`.
Verbs: `convert`, `validate`, `export`. Chapters:
[manual 31](<docs/manual/0031 Exports and formats.md>),
[technical 10](<docs/technical/0010 File and wire formats.md>),
[technical 28](<docs/technical/0028 The v2 JSON schema.md>),
[technical 32](<docs/technical/0032 CIM and CGMES import and export.md>),
[technical 45](<docs/technical/0045 IEC 61850 SCL substation import.md>).

### Physics audit and validation tools

`physics-check` and `conformance` audit any solved state against the conservation laws; `crossval` re-solves it in an
independent tool; `bench`, `simdcheck` and `gpucheck` measure and check the compute.
Chapters: [manual 27](<docs/manual/0027 Validation tools.md>),
[technical 11](<docs/technical/0011 Validation.md>),
[technical 16](<docs/technical/0016 Physics conformance and the constrained estimator.md>).

### Study jobs (RC-3 to RC-5)

RC-3 added transmission study jobs as separate commands, each reading a strict JSON study file bound to its case by
SHA-256: `transmission-security`, `cgmes-study`, `transfer-capability`, `corrective-sequence`, `dynamics-study`,
`planning-study`, `restoration-study`, `reliability-study`, `emt-line`, `arc-flash`, `cable-rating` and `motor-start`
([RC3.md](RC3.md)). RC-4 adds study-case management, sensitivity and contingency verbs, probabilistic
and network reliability studies, line constants, a resident `rpc` endpoint, further short-circuit and protection duty
verbs, declarative dynamic models, full-model small-signal analysis, node-breaker exchange, meshed unbalanced
distribution, three-phase EMT, a workbench and UK power-quality checks ([RC4.md](RC4.md)). RC-5 adds protection of
each generating unit, unbalanced and impedance RMS faults, a wider dynamic-model library with SVC, STATCOM and HVDC
models, `protection-eval` and `protection-grade`, `acopf-opt`, `acscopf` and `reconfig`, `grid-code`, `reduce`,
`cgmes-shacl`, an editable object model with a psspy-style Python layer, and .NET model plug-ins ([RC5.md](RC5.md)).
Each job reports what it
could not establish as incomplete, never as a pass, and each is tested on small analytical, constructed or synthetic
cases only. The [CLI reference](<docs/manual/0019 CLI reference.md>) documents every verb.

### Scripting and API

`gridsim rpc --protocol 2` holds each loaded case as an object model: elements keyed in model order, attributes read and
written by request, every edit an immutable replacement logged with its revision, and `study` running `pf`, `nminus1`,
`iec60909`, `acopf-full` and the other study verbs on the case as edited (protocol level 1 is unchanged byte for byte).
Elements can also be added and deleted (`add`, `delete`; a bus with cascade), under the same checks: adding a bus, a line
and a load solves to the same bits as the case built directly. `gridsim.ModelSession` drives it from Python, and
`gridsim.psspy` maps a listed subset of psspy calls onto it, with
GridSim's own error codes and a refusal, never a substitute, for anything it does not map; each psspy argument layout
comes from a public source, and one no source confirms is refused. A held case belongs to the session and host instance
that loaded it, so a handle can never reach another session's case. A script that starts
`GRAMMAR 1 WITH STUDIES` runs the same studies on its edited plant (grammar 1 itself is unchanged). An edit then solve
equals the same edit made in the file, bit for bit. The RC-5 verbs also write the shared `--json` / `--csv` result files.
Chapters: [technical 41](<docs/technical/0041 Scripting object model and API.md>),
[manual 19](<docs/manual/0019 CLI reference.md>), [manual 35](<docs/manual/0035 Scripting.md>).

---

## The maths

The formulations, in brief; each technical chapter derives them in full, names the source symbols and lists the tests
that pin them.

**Network model** ([technical 2](<docs/technical/0002 Data model and per-unit conventions.md>)). Each branch is a
pi-section with series admittance `ys = 1/(R + jX)`, shunt charging `bc` at each end and complex tap `a`:

```
Yff = (ys + bc) / a^2        Ytt = ys + bc
Yft = -ys / conj(tap)        Ytf = -ys / tap
```

stamped into the sparse bus admittance matrix `Y = G + jB` in a fixed order so that dense and sparse assembly agree to
the bit. The asymmetric `Yft != Ytf` is what makes a phase shifter move flow.

**Power flow** ([technical 3](<docs/technical/0003 Power flow.md>)). The injections at bus `i`:

```
P_i = Vm_i * sum_k Vm_k (G_ik cos th_ik + B_ik sin th_ik)
Q_i = Vm_i * sum_k Vm_k (G_ik sin th_ik - B_ik cos th_ik)
```

Newton-Raphson drives the mismatch `f(x) = [P_spec - P(x); Q_spec - Q(x)]` to zero with the polar Jacobian blocks
`H = dP/dth`, `N = Vm dP/dVm`, `M = dQ/dth`, `L = Vm dQ/dVm`, factorised by sparse LU each iteration. Reactive limits
and tap changers are outer loops; the backward/forward sweep solves radial feeders branch by branch.

**DC power flow and OPF** ([technical 29](<docs/technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>)).
`P = B' th`; PTDF and LODF follow from `B'`; the DC-OPF minimises dispatch cost subject to the DC flow equations and
line limits, and the dual of the balance constraint gives the nodal price.

**State estimation** ([technical 6](<docs/technical/0006 State estimation.md>)). Minimise
`J(x) = sum_i ((z_i - h_i(x)) / sigma_i)^2`. Each Gauss-Newton step solves the normal equations

```
G(x) dx = H^T W (z - h(x)),   G = H^T W H,   W = diag(1 / sigma_i^2)
```

The chi-square test compares `J` with the quantile at `m - n` degrees of freedom. The residual covariance
`Omega = R - H G^-1 H^T` gives normalised residuals `r_N,i = |r_i| / sqrt(Omega_ii)` for bad-data identification.
Robust methods reweight the same equations (Huber with k = 1.345, Schweppe-GM with leverage down-weighting); exact
LAV is a linear programme; the constrained form solves the Hachtel KKT system.

**Dynamic estimation** ([technical 18](<docs/technical/0018 Real-time, distribution and dynamic estimation.md>)).
The EKF predicts `P- = F P F^T + Q` and corrects with the measurement Jacobian; the UKF propagates sigma points; the
EnKF propagates a seeded ensemble.

**Frequency dynamics** ([technical 5](<docs/technical/0005 Frequency dynamics.md>)). The lumped swing:
`df/dt = f0 / (2 E) * (P_m - P_e)`, with `E` the stored kinetic energy in GVA.s. The distributed model couples domains
by `P_tie,i = sum_j K_ij (delta_i - delta_j(t - tau_ij))` and integrates with symplectic Euler; the inverse form
`E = f |dP| / (2 |RoCoF|)` infers inertia from a recorded event.

**Machines and controls** ([technical 24](<docs/technical/0024 Standard dynamic-model library.md>)). The swing of
each machine, `domega/dt = (Pm - Pe - D*omega) / (2*H)` and `ddelta/dt = ws*omega`, with one-axis, two-axis and
subtransient flux dynamics and the standard exciter, stabiliser and governor block diagrams.

**Small-signal and impedance** ([technical 30](<docs/technical/0030 Small-signal and impedance stability.md>)).
Linearise to `dx/dt = A x`; each eigenvalue `s = sigma + j w` has damping ratio `zeta = -sigma / |s|`.
Impedance stability applies the generalised Nyquist criterion to the converter and grid admittances.

**Electromagnetic transients** ([technical 21](<docs/technical/0021 Electromagnetic transient (EMT) simulation.md>)).
Each step solves the nodal equation `G v = i_hist + i_src`; a trapezoidal inductor is the companion conductance
`2L/dt` (in series with its resistance) plus a history current source; lines are Bergeron travelling-wave models.

**Short circuit** ([technical 25](<docs/technical/0025 IEC 60909 short-circuit duty study.md>)). The equivalent
voltage source `c Un / sqrt(3)` at the fault (c = 1.10 or 1.00 above 1 kV for maximum and minimum currents);
`Ik'' = c Un / (sqrt(3) |Zk|)`; the peak `ip = kappa sqrt(2) Ik''` with `kappa = 1.02 + 0.98 e^(-3 R/X)`; the
thermal equivalent `Ith = Ik'' sqrt(m + n)`.

**Harmonics** ([technical 19](<docs/technical/0019 Harmonics.md>)). The network admittance is rebuilt at each order
`Y(h)`; injections give `V(h) = Y(h)^-1 I(h)`; `THD_V = sqrt(sum_h |V_h|^2) / |V_1|`.

**Battery storage** ([technical 27](<docs/technical/0027 Battery energy storage.md>)). A cell equivalent circuit
(open-circuit voltage against state of charge, series resistance and RC branches), rainflow-counted cycle ageing, and
a scheduling LP.

---

## Determinism and machine epsilon

GridSim treats bit-identical results as a requirement, not an aspiration. The overview is [`docs/developer/deterministic-maths.md`](docs/developer/deterministic-maths.md) and the
specification is [technical ch. 38, Deterministic mathematics](<docs/technical/0038 Deterministic mathematics.md>):

- **Deterministic maths.** Transcendental functions come from `DetMath`, a C# port of FDLIBM 5.3 checked bit for bit
  against Java's `StrictMath`, so they round the same way on every operating system. No numerical path uses fused
  multiply-add. An IL-level analyser test (`DeterministicMathGuardTests`) fails the test run if numerical code calls the
  platform maths library, a fused multiply-add or a horizontal vector reduction. The one allowed exception is the fused
  arm of `SimdKernels.AddScaledRow`, which no numerical path selects. The full rules are in [`docs/developer/deterministic-maths.md`](docs/developer/deterministic-maths.md).
- **SIMD tiers.** Dense kernels dispatch to AVX-512, AVX2, SSE or scalar at run time and give the same bits at every
  width; `gridsim simdcheck` reports the active tier. CI (`.github/workflows/ci.yml`) runs the suite on Linux and Windows
  x64 (normal, AVX2 off with FMA off, hardware intrinsics off, forced AVX-512 and forced 256-bit), on macOS (Apple
  silicon; normal and hardware intrinsics off) and on Linux and Windows ARM64 (normal and hardware intrinsics off). CI
  does not run on every push: the release workflow calls it on a release tag and publishes nothing unless every leg
  passed (it can also be started by hand); day-to-day verification is a local batch run.
- **Dense and sparse power flow agree to the bit** (`DenseSparseParityTests`: every bit, iteration count and status
  equal), as do routed and explicit engine choices.
- **Estimates and replays are bit-reproducible** run to run on each backend, and no estimation arithmetic reads the
  clock. The forward-inference horizon does: which windows are accepted depends on when a run happens, unless
  `--utc-now` pins it.
- **The live analysis cycle is the one scheduling exception.** `cycle` and the live web cycle admit their Low-priority
  jobs against a wall-clock budget, so which jobs run in a cycle depends on the machine; each job that runs is itself
  deterministic ([technical ch. 8](<docs/technical/0008 Provenance and invariants.md>)).

- **The state estimate does not depend on the bus count.** On the CPU every WLS and robust estimate takes the same
  sparse step at every size (`CanonicalWlsParityTests`: bit-identical across the old 200-bus dense / sparse threshold).
- **BREAKING CHANGE in RC-4 (2.1.073).** Results and determinism digests are not bit-identical to
  2.1.071 or 2.1.072, because the sparse LU ordering and the Newton voltage floor changed; within one version, bit
  identity holds as above. The difference is at rounding level (on 66 MATPOWER cases at
  most 9.7e-13 pu in voltage magnitude and 1.9e-11 deg in angle, with identical convergence and iteration counts);
  stored digests must be regenerated. Details are in [RC4.md](RC4.md).
- **RC-5 (2.1.105) is a BREAKING CHANGE release.** Twenty-six changes since RC-4, B1 to B26, can change a result, an
  outcome or an exit code. Script expressions and script hosts have depth and run limits; a Newton solve stopped by
  its iteration cap while still converging is no longer retried from a DC start (status `MaxIterations` where RC-4
  reported it converged); with `--dyr-extended --allow-unmodelled` a CSVGN1 unit follows its SVC control after a
  disturbance instead of holding its power-flow Q; and the storage time series counts a voltage-dependent load at the
  slack bus at the solved voltage (+8.16 MW of slack output for a 100 MW constant-impedance load at 1.04 pu). The core
  review's bug fixes are BREAKING CHANGEs as well: PSS/E, DGS and CGMES transformer impedances and ratios, the DGS
  capacitor sign, the PSS/E VSC DCSET sign, G74 motor breaking current, Z2 with dynamic loads, overlapping RMS faults,
  VSC1, REEC_A/B/C, small-signal RoCoF with inverters, battery reversible heat, AC-SCOPF screening, AC-OPF tap prices,
  barrier floor and loss-objective convergence, and QSTS storage sharing. The adaptive-step RMS rotor angles have a
  fixed error scale, moving angles by at most 1.9e-4 degrees on case118. Any change to a numerical path is labelled
  BREAKING, because no test can prove a change has no effect on every path; the measurements give its size. Everything
  else in RC-5 is additive, in new verbs, options, model types and event kinds. RC5.md has the table: what changed,
  why, the magnitude, who is affected and how to keep the old behaviour.
- **Run journal and `breaking-change validate`.** Re-run your earlier jobs on a new release, in parallel, and see bit
  for bit what changed and which breaking change explains it. Switch the journal on (`--journal <file.jsonl>`,
  `GRIDSIM_JOURNAL` or a config file) and every run is recorded with the hashes of its inputs and outputs; the run
  itself is byte for byte the same. After an upgrade, `gridsim breaking-change validate` re-runs each entry in a scratch
  folder and reports it IDENTICAL, CHANGED (each field that moved, by how many bits, and its breaking-change ID, B1 to
  B26 for RC-5) or why it cannot be re-run. `breaking-change import` brings in `--json` result files kept from before.
  [Manual 19](<docs/manual/0019 CLI reference.md#breaking-change>), [technical 42](<docs/technical/0042 Run journal and breaking-change validation.md>).

Open items, stated plainly: the dense estimator step that a GPU linear solver uses agrees with the sparse step to
solver tolerance (Vm 1e-8 pu, Va 1e-7 deg), not to the bit; GPU arithmetic is outside the bit-identity contract. Every
CI leg, Linux and macOS included, is a hard gate on the same pins.

---

## Cross-checks

Every figure below is a measured number with its source. Each one measures agreement with an independent tool, a
published solution or a published event record; none of them validates GridSim against a real power system, and no
comparison with a commercial tool (PSS/E, PowerWorld, PowerFactory, PSCAD/EMTP) has been made. The figures below were
measured before the RC-4 ordering change, which moves results at rounding level only.

**Power flow against pandapower** (pandapower 3.5.5, measured 2026-10-05; gb-full re-measured 2026-10-31 after the
2.0.200 charging correction; worst |V| pu / angle rad; gate 4096 machine epsilons, 9.09e-13; [technical 11](<docs/technical/0011 Validation.md>)):

| Case | Vm (pu) | Va (rad) |
|---|---|---|
| case9 | 4.4e-16 | 7.0e-17 |
| case14 | 4.4e-16 | 1.1e-15 |
| case30 | 6.7e-16 | 1.7e-15 |
| case57 | 1.8e-15 | 3.5e-15 |
| case118 | 8.9e-16 | 2.7e-15 |
| case300 | 5.2e-14 | 8.7e-14 |
| gb-synthetic | 1.9e-14 | 3.9e-14 |
| gb-full | 3.3e-13 | 3.0e-13 |

**State estimation against `pandapower.estimation`** (technical 11): case14 5.56e-12 pu and 7.74e-11 deg; case30
4.44e-16 pu; case118 2.36e-13 pu. An independent NumPy check confirms each estimate's residuals (3.877e-13 on case14),
its objective and its optimality.

**Other independent checks** (technical 11): case9 against the published MATPOWER solution (4 iterations, mismatch
1.6e-14, loss 4.641 MW); the swing equation against SciPy RK45 (1.6e-4 Hz/s and 2.2e-4 Hz on a 1,320 MW loss); the
classical multi-machine swing against SciPy (3.7e-8 rad on case9); small-signal eigenvalues (3.1e-8 relative on
case9 at an eigenvector condition number of 1.9e10); harmonic penetration (9.7e-17 pu).

**Recorded events.** The 9 August 2019 disturbance: simulated RoCoF -0.171 against a recorded -0.170 Hz/s, nadir 48.84
against 48.80 Hz, verdict REPRODUCED ([`docs/validation/evidence-gb-2019-08-09.md`](docs/validation/evidence-gb-2019-08-09.md)). The 22
December 2023 event: verdict MARGINAL, RMSE 0.281 Hz against the 1-second trace, with the reasons
([`docs/validation/evidence-gb-2023-12-22.md`](docs/validation/evidence-gb-2023-12-22.md)).

---

## Performance and GPU

Every timing here comes from a benchmark run recorded in the repository. `gridsim bench` runs a matrix of engines over
the case library. On the largest meshed case, case9241pegase, the sparse engine took 1,178 ms where the dense engine
took 199,934 ms, with the same iteration count and the same mismatch (6 iterations, 2.13E-009)
([manual 28](<docs/manual/0028 Benchmarking and GPU.md>); measured before the RC-4 ordering change). With the
minimum-degree ordering of RC-4, large sparse solves are 4 to 7 times faster on an EPYC 7763 server
(case_ACTIVSg2000 306 to 46 ms, case3375wp 560 to 83 ms; [RC4.md](RC4.md)). Server sweeps reach the 25,000-bus
synthetic cases; the [benchmark landing page](docs/developer/benchmarks/index.md) lists the measured runs, the latest first.

The GPU backend sits behind the same linear-solver seam as the CPU: native ROCm (hipSOLVER, rocSPARSE) for AMD
Instinct first, then OpenCL and CUDA through ILGPU, chosen automatically (`gridsim gpucheck` reports what is
available). GPU results are deterministic but not bit-identical with the CPU contract: the device runs different
algorithms (natural-order dense elimination, Jacobi-preconditioned BiCGSTAB to a 1e-10 residual), sums in a
different order and fuses multiply-add. Measured on an RTX A4000 over 27 MATPOWER cases they differ from the CPU by at
most 1.45e-12 pu and 5.2e-11 deg, with the same convergence and iteration counts; the 4096-epsilon cross-check gate does not apply to GPU
paths. A GPU solve prints how many of its Newton steps fell back to the CPU LU.
[Technical 4](<docs/technical/0004 Sparse and GPU numerics.md>), [`docs/developer/gpu-rental-runbook.md`](docs/developer/gpu-rental-runbook.md).

---

## Networks and data

| Network | Size | Where |
|---|---|---|
| IEEE 9, 14, 30, 57, 118, 145, 300; RTS 24; CIGRE | built in | `gridsim case9` and so on |
| GB reduced (10 zones) | 20 buses | `data/uk`, `gridsim uk` |
| GB full transmission model | 3,539 buses, 5,918 branches | `data/gb-full` |
| GB synthetic at scale | 2,227 buses, 2,268 branches | `data/gb-synthetic` |
| GB reduced 29-bus, GB network 2224-bus, PyPSA-GB reduced and zonal | MATPOWER | `data/matpower/gb` |
| PEGASE, RTS, NESTA, PGLib and benchmark libraries | MATPOWER | `data/matpower` ([`docs/developer/case-libraries.md`](docs/developer/case-libraries.md)) |
| International starter maps: Ireland, Iberia, France, continental Europe | 60 to 5,824 buses | `data/intl` |
| Three-phase feeders: IEEE 123-node and the IEEE 4-node transformer tests | JSON | `data/dist3ph` |
| Recorded events, NESO frequency samples | JSON, CSV | `data/events`, `data/neso` |
| National and regional network models, 36 grids (`UnvalidatedAtScale`) | 36 to 5,771 buses | `models/<case>` (the `models` submodule, fetched with `git submodule update --init models`; list in `models/README.md`). Not bundled, so the licence bus limit applies: eight cases are above 1,500 buses and need a licence, issued free of charge at present on request (see [Licence](#licence)) |

The estimated GB history is built on `gb-spine`, a 316-bus fold of the full model
([manual 22](<docs/manual/0022 Estimating GB history.md>)). Network models from the GDA lake are materialised at run
time and never committed. The GB grid documents that GridSim's cited values come from are in the `docs/Public/GB`
submodule; values whose settling document is paywalled are listed in [`docs/reference/paywalls.md`](docs/reference/paywalls.md), with what public
sources confirm and where you set each value yourself.

---

## The apps

- **Web app** (`GridSim.Web`, Blazor): the control room in the browser, a REST and WebSocket API, single sign-on and
  sessions. In production it runs replay-only, so the solve API and the simulation socket are not served.
  [Manual 9](<docs/manual/0009 The web app and REST and WebSocket API.md>),
  [technical 34](<docs/technical/0034 Web host, REST and WebSocket, SSO and sessions.md>),
  [technical 35](<docs/technical/0035 Deployment and security.md>).
- **Desktop apps**: the live control-room dashboard, the network map, analysis views and the audit log, in two builds.
  The Avalonia app (`GridSim.App.Desktop`) runs on Windows and Linux; the WPF app (`GridSim.Wpf`) runs on Windows only.
  [Manual 8](<docs/manual/0008 The desktop app.md>), [manual 10](<docs/manual/0010 Analysis views.md>).
- **Case editor** (`GridSim.SchemaEditor`): builds and edits v2 JSON cases. [Manual 33](<docs/manual/0033 The case editor.md>).
- **Workbench** (RC-4): a model browser, plot kit, reports and an undoable single-line editor, in the
  Avalonia app (Ctrl+Shift+B) and the web client (`/workbench/{case}`).

![THE MACHINE - every generator locked together as one rotating mass; wind/solar/imports tagged "no inertia", 242 GVA.s of stored spin](<docs/screenshots/gb/the-machine-spinning-grid.png>)
![MECHANICAL - per-machine state, inertia-per-unit, and the RoCoF relay margin, most-stressed first](<docs/screenshots/gb/mechanical-per-machine-inertia.png>)
![OPERATOR - the live generation stack split synchronous (26.2 GW) vs converter (23.7 GW), 242 GVA.s inertia](<docs/screenshots/gb/operator-sync-vs-converter.png>)
![the live network drawn like a tube map - power flowing as marching dashes](<docs/screenshots/synthetic/network-map-fitted.png>)
![any MATPOWER case, topology synthesised - IEEE 118-bus, meshed with graph-derived voltage tiers](<docs/screenshots/matpower/case118-meshed-tiers.png>)
![PEGASE 2869 at scale - concentric-ring layout](<docs/screenshots/matpower/case2869-pegase-ring.png>)

More in [`docs/screenshots/`](docs/screenshots).

---

## Architecture

```
GridSim.Core          the engine: Model, PowerFlow, Numerics (sparse LU, ILU0/BiCGSTAB, QR, eigen, SIMD, DetMath),
                      Analysis (DC, OPF, CPF, contingency, taps, losses), Estimation (WLS family, bad data, FDIA,
                      Dse), Distribution (three-phase), Dynamics (swing, distributed inertia, machine models,
                      small-signal, impedance, EMT), Harmonics, Protection (faults, IEC 60909, coordination),
                      Storage, Events/Replay/Scenarios/Fleet/Evidence, Synchrophasor, Interpose, IO (MATPOWER,
                      JSON v1/v2, CIM/CGMES, PyPSA), Physics (conformance), Diagnostics (horizon guard, provenance,
                      audit log), Licensing
GridSim.Gpu           ROCm (hipSOLVER, rocSPARSE), OpenCL and CUDA backends behind the solver seams
GridSim.Gda           the GDA data-lake bridge (reads the lake at run time; no data in the repo)
GridSim.Cli           the gridsim command line tool (every verb)
GridSim.Web           the Blazor web app, REST and WebSocket API, SSO, sessions
GridSim.Wpf           the Windows desktop control room (WPF)
GridSim.App           the shared Avalonia desktop UI
GridSim.App.Desktop   the Avalonia desktop app (Windows and Linux)
GridSim.Ui            the UI layer shared by the desktop front ends
GridSim.SchemaEditor  the case editor
tests/GridSim.Tests   the constitutional test suite
tests/GridSim.App.Tests  tests for the Avalonia desktop app
tools/                the cross-validation sidecars (pandapower, SciPy, NumPy), docs tooling, benchmark scripts
```

The expensive kernel of a load flow is the linear solve inside each Newton step, so that is the seam the backends sit
behind, the same "clean abstraction, swappable backend" pattern the inertia model follows. Core and Cli hold all
capability; the web, WPF and Avalonia front ends are presentation only. Full walkthrough: [technical 1](<docs/technical/0001 Architecture.md>).

---

## Repository layout

```
.github/                      CI and release workflows, issue templates, Dependabot
.well-known/                  security.txt (RFC 9116) served by the web app
known_hosts/                  a second copy of security.txt
benchmarks/                   the free AC power-flow comparison harness and its inputs
data/                         bundled networks and data (hashed in data/bundled.sha256)
docs/                         user manual, technical reference, validation, developer, assurance, archive, brand
models/                       submodule: GridSim-Models, the national and regional grid snapshots
python/                       the Python API package
schemas/                      JSON schemas for the GridSim file formats
signing/                      the code-signing certificate the release workflow checks
src/                          the GridSim projects (see Architecture)
tests/                        the test projects
tools/                        cross-validation sidecars, docs tooling, benchmark and GPU scripts, smoke apps
CHANGELOG.md                  every change, newest first; the release notes come from it
CONTRIBUTING.md, SECURITY.md  how to contribute and how to report a vulnerability
LICENSE.md                    the GridSim licence
THIRD-PARTY-NOTICES.md        third-party licences and attributions
README.md                     this file
RC1.md, RC3.md to RC5.md      release-candidate records
UnderstandingRestrictions.md  the governing no-forward-inference rule
Directory.Build.*             MSBuild settings shared by every project
GridSim.slnx, global.json     the solution and the pinned .NET SDK
Dockerfile, compose.yml       the web app container build and example deployment
mkdocs.yml                    the documentation site (requirements in docs/requirements.txt)
```

---

## Assurance and security

- The forward-inference guard (169 h) at every dated ingestion seam is the foundational control: a full compromise of
  GridSim yields week-old reconstruction, never live or forward operational visibility
  ([`UnderstandingRestrictions.md`](UnderstandingRestrictions.md),
  [technical 8](<docs/technical/0008 Provenance and invariants.md>)).
- A hash-chained, anchored audit log records `estimate`, `crossval`, `interpose` and `serve` runs and the web host's
  resident estimator, with one entry per security event at the detection sites, optional forwarding of every entry
  to a second file, optional incident raising and an optional signed anchor
  ([technical 31](<docs/technical/0031 Audit log and diagnostics.md>),
  [manual 26](<docs/manual/0026 Audit log, deployment and assurance.md>)).
- `gridsim compliance verify` (and the web `/compliance` endpoint) reports the build identity and the recorded control
  set. Only a release build can read COMPLIANT; a local build reads NOT ATTESTED and the verb exits 1. At this release
  three controls are recorded Partially achieved (below), so a release build reads NOT COMPLIANT. The build hash it
  shows is an identity label, not an integrity check; a download's integrity comes from the provenance attestation,
  `SHA256SUMS.txt` and the signatures.
- The assurance documents map GridSim to the Cyber Assessment Framework and the NIS Regulations, control by control,
  with the gaps stated: [`docs/assurance/caf-mapping.md`](docs/assurance/caf-mapping.md),
  [`docs/assurance/nis-oes-gap-assessment.md`](docs/assurance/nis-oes-gap-assessment.md),
  [`docs/assurance/rbac-sbom-secrets-notes.md`](docs/assurance/rbac-sbom-secrets-notes.md). The supporting documents
  they cross-reference are in the same folder: [`security-governance.md`](docs/assurance/security-governance.md),
  [`risk-register.md`](docs/assurance/risk-register.md), [`incident-response.md`](docs/assurance/incident-response.md),
  [`slsa-provenance.md`](docs/assurance/slsa-provenance.md), [`ssdf-mapping.md`](docs/assurance/ssdf-mapping.md),
  [`nasa-std-7009-credibility.md`](docs/assurance/nasa-std-7009-credibility.md),
  [`cyber-essentials-note.md`](docs/assurance/cyber-essentials-note.md) and
  [`tier3-roadmap.md`](docs/assurance/tier3-roadmap.md).

---

## Known limits

- Nothing is validated (see the status note at the top). The cross-checks compare GridSim with other software and
  published figures, not with a real power system, and no commercial tool has been compared.
- The RC-3 to RC-5 study jobs are tested on small analytical, constructed and synthetic cases only; outputs that need
  paid standard text are labelled AWAITING STANDARD TEXT ([RC4.md](RC4.md), [RC5.md](RC5.md)).
- `acopf` is a DC-OPF dispatch with an AC loss loop, not a full AC-OPF: no voltage or reactive limits (use `acopf-full`
  for the full AC optimal power flow).
- In the IEC 60909 study, K_G / K_T exist as closed forms but are not yet applied unless `--corrections` is given.
- Coupled harmonic power flow and harmonic state estimation run from the CLI (`harmonic-pf`, `harmonic-se`) but not
  from the apps, and both stay experimental: the converter coupling model is an assumed placeholder, and neither has been
  compared with field measurements. `harmonic-se` reads bus voltage phasors only (no branch currents).
- `cosim` couples an `emt3` study to an RMS phasor source (a source behind a line or impedance), not to the RMS
  transient kernel. `emt-partition` takes its own single-conductor network: the three-phase `emt3` lines cannot be cut.
- CGMES DL, GL, SC and DY profiles are written for bus-branch cases in CGMES 2.4.15 and, with
  `--model bus-branch`, in CGMES 3.0, with six DY models (GENROU, GENSAL, GENCLS, SEXS, SCRX, TGOV1). The CGMES 3.0
  bus-branch export covers what the 2.4.15 export covers; it has not been checked with the ENTSO-E QoCDC platform or a
  vendor import. On the four public CGMES 3.0 "Simple" shape files (61970-600-2 Equipment, Topology,
  SteadyStateHypothesis and StateVariables, cardinality and datatype checks only) the case14 export selects 189 focus
  nodes and fails only the `GeneratingUnit.normalPF` count, which the case does not hold. The "Complex" CGMES-AP files
  (61970-301, -452, -456, -457 and the others) hold SHACL-SPARQL constraints, which GridSim does not evaluate: they are NOT
  CHECKED, and they find more on case14 (line containment and base voltage checks among them). `cgmes-conform` runs eleven
  hand-coded checks, and `cgmes-shacl` runs the SHACL Core subset of the shape files and lists every SHACL-SPARQL
  constraint it meets as NOT CHECKED by shape, so neither is a conformity certificate.
- DNP3 and IEC 61850 GOOSE are handled at the framing level only.
- The radial three-phase sweep (`dist3ph`) is experimental. The meshed `dist3ph-mesh` is not marked so; its IEEE
  123-node reference values are not confirmed against a published solution.
- Placed protection devices: `sc-trace`, `fault-line` and `protection-audit` evaluate phase overcurrent (50/51) only and
  report distance, directional, earth-fault and differential devices as not evaluated; `protection-eval` and
  `protection-grade` evaluate them all. The static unbalanced fault takes a fault reactance only (no fault resistance).
  Distance schemes are logic on static fault phasors (no channel failure, current-reversal guard, echo or weak infeed);
  power-swing blocking reads a positive-sequence trace and has no out-of-step tripping; mutual compensation covers
  parallel lines between the same two buses. The 87T covers two-winding units on fundamental phasors (no inrush or
  harmonic restraint), and the 87B is low-impedance with ideal CTs. CVT transients, CT saturation, high-impedance 87B and
  line charging compensation are not modelled, and 21, 67 and 87 do not act inside the RMS study.
- The dynamic-model library is a PSS/E-compatible subset, not the full PSS/E library. RMS unbalanced faults are a
  positive-sequence representation: negative-sequence braking torque and per-phase detail are not modelled.
- Battery chemistry presets are curated placeholders, not validated parameter sets.
- The dense state-estimator step that a GPU linear solver uses agrees with the sparse step to solver tolerance, not to
  the bit; on the CPU every WLS and robust estimate takes the sparse step at every size.
- GPU results are outside the bit-identity contract.
- Role-based access control is enforced on the web solve surface (`POST /api/solve`, `GET /api/topology` and every
  `/ws/sim` control frame) through `GridSim:AnonymousRole` or a `GridSim.<role>` claim; the command-line verbs do not
  apply it, and the replay-only
  configuration is the production exposure control.
- The distrusted-source control is built as a library primitive and is not yet reachable from the CLI or the apps: nothing in them applies it.
- The web host (`GridSim:BridgeReconnect`) and the desktop app (`BridgeAutoReconnect`) drive the bridge reconnect
  backoff schedule when the setting is on. It is off by default, so a dropped bridge stream is not reconnected unless
  it is switched on, and there is no service failover.
- Secrets can be read from the environment or a one-file-per-secret directory (`Secrets:Directory`); no OS keychain
  or vault backend ships.
- Security-event alerting is a JSON-lines incident file (`--incident-log`, `Audit:IncidentLogPath`); routing it to a
  SOC or notification system is the deployer's. The assurance documents list each gap.

---

## Roadmap

1. **Steady state** - done: Q-limits (PV to PQ), OLTC regulation, distributed slack, switched shunts.
2. **Live simulation** - done: day/night curve, re-dispatch and re-solve each tick.
3. **Frequency dynamics** - done: lumped centre-of-inertia swing and the distributed multi-domain inertia model.
4. **Scale** - done: sparse solver to 25,000+ buses, parallel benchmarking and N-1 sweeps.
5. **MATPOWER libraries** - done: PEGASE, RTS, NESTA, PGLib and CIGRE through the `.m` importer.
6. **Distribution** - done: backward/forward sweep for radial feeders; three-phase unbalanced feeders (radial sweep and meshed Newton).
7. **GPU backend** - done: ROCm/HIP for AMD Instinct, OpenCL and CUDA behind one seam.
8. **Evidence engine** - done: historical state estimation, grounded counterfactuals, the 169 h horizon as a hard
   code invariant.
9. **Cross-checks** - done: pandapower, `pandapower.estimation`, SciPy and NumPy sidecars; the power-flow,
   harmonic, impedance and small-signal checks at 4096 machine epsilons, the others at stated engineering tolerances.
10. **Measurement-based state estimation** - done: the WLS family fitted to the real GB grid per settlement period,
    history to March 2016, the 9 August 2019 record reproduced.
11. **Short circuit and protection** - done: IEC 60909 duty with the correction factors, IDMT coordination, relay
    overlay, cascade validation.
12. **Dynamic-model library** - done: classical to subtransient GENROU, exciters, stabilisers and governors, RMS
    transient stability, small-signal and impedance stability, EMT.
13. **Real-time estimation** - done: the resident `serve` estimator with EKF, UKF and EnKF, the RTU interposer and
    C37.118 ingest.
14. **Transmission study jobs** - done in RC-3: preventive N-1 with AC verification, transfer capability, corrective
    sequences, CGMES and dynamics study jobs, planning snapshots, restoration and reliability.
15. **RC-4** - done: study cases, probabilistic and network reliability, short-circuit and protection duty,
    declarative models, full-model small-signal, three-phase EMT, the workbench, and a scripting interface
    (`GridSim.Scripting`).
16. **RC-5** - done: per-unit generator protection, unbalanced RMS faults, the extended dynamic-model library with SVC,
    STATCOM and HVDC, placed distance, directional, earth-fault and differential protection, loss and reactive-power
    OPF with AC-SCOPF, the GB and RfG grid-code suite, Ward and REI equivalents, the CGMES DL, GL, SC and DY profiles
    with a SHACL checker, the object model and psspy-style Python layer, and .NET model plug-ins.
17. **Next** - grid-forming inverter dynamics for the fully converter-led case; the full renewable and converter
    standard models (REEC/REPC, WT3/4, DER_A); broader GDA DNO coverage.
18. **Per-asset profiles** - planned: the per-asset Parquet profile library (reference data per asset for cables,
    transformers, generators, demands and interconnectors, each field with its unit and source).

---

## Documentation

Everything below is also published as a searchable site at <https://marks0485.github.io/GridSim/>, rebuilt
from `docs/` on every push to main.

- [User manual](<docs/manual/0000 Contents.md>): installing, loading networks, every study, the apps, the CLI
  reference ([`docs/manual/0019 CLI reference.md`](<docs/manual/0019 CLI reference.md>)), troubleshooting.
- [Technical manual](<docs/technical/0000 Contents.md>): the architecture, the data model, every solver with its
  equations, invariants and tests, the file and wire formats, the glossary.
- [`docs/developer/deterministic-maths.md`](docs/developer/deterministic-maths.md): the deterministic maths library that keeps results bit-identical across operating systems
  and CPUs.
- [`UnderstandingRestrictions.md`](UnderstandingRestrictions.md): the no-forward-inference rule and its five testable
  invariants.
- [`docs/validation/distributed-inertia.md`](docs/validation/distributed-inertia.md): the multi-domain inertia model in depth.
- [`docs/validation/evidence-gb-2019-08-09.md`](docs/validation/evidence-gb-2019-08-09.md) and
  [`docs/validation/evidence-gb-2023-12-22.md`](docs/validation/evidence-gb-2023-12-22.md): the event dossiers.
- [`docs/developer/case-libraries.md`](docs/developer/case-libraries.md): the MATPOWER parser and case archive.
- [`docs/validation/neso-rp1-and-mechanical.md`](docs/validation/neso-rp1-and-mechanical.md): the control-room panels.
- [`docs/reference/paywalls.md`](docs/reference/paywalls.md): the `[VERIFY]` values whose settling document is paywalled.
- [`docs/developer/submodules.md`](docs/developer/submodules.md), [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md),
  [`CHANGELOG.md`](CHANGELOG.md).

---

## Licence

GridSim is source-available, not open source: the code is public on GitHub, but GridSim requires a licence and
`LICENSE.md` governs. These are the licence terms, measured on buses and nothing else:

| Use | Bus limit |
|---|---|
| Personal, non-commercial | 1,500 |
| Academic research and teaching (attribution required in publications) | 1,500, larger on request |
| Commercial Code Licence | None |
| Commercial Deployment Licence | None |
| Network Operator (TSO / ESO / DNO, site-wide, subject to regulatory approval) | None |

Bundled networks never count toward a limit.

At present licences are issued gratis: no payment is required for any tier, and there is no present intention to charge.
Anyone who asks today receives a Network Operator (DNO) licence, which has no restrictions. Ask by email:
**mark@gridsimdigitaltwin.co.uk**. Issuing licences gratis today is not a promise that it will always be so. GridSim ships with a
licence mechanism so that the terms above can be applied if that is ever needed, for example to support its development.

The licence covers the software, not Mark's time. Help is given when he can; support, custom work or other commitments
on your terms need a separate agreement and compensation. Nothing in GridSim is validated at any scale, and his role is
the code. What helps most is use: run it, and send results and bug reports (see below).

---

## Reporting bugs

Send every bug report to **mark@gridsimdigitaltwin.co.uk**: the GridSim version (`gridsim --version`), the operating system,
the command or steps, the case (or a small case that shows the problem) and what you expected to happen. Results
are just as welcome as bugs: if you run GridSim against a case or a tool you trust, send what you found, agreement or
not. Release candidate notes are in [`RC1.md`](RC1.md), [`RC3.md`](RC3.md), [`RC4.md`](RC4.md) and
[`RC5.md`](RC5.md).

---

## Contributing

The acceptance criteria every change meets (machine epsilon, constitutional tests, the warning-free build, fail loud)
and the conventions are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

---

## A note from the author

I have at times been a staunch advocate for developers writing their own code, and I still maintain that as true. However, with time and advancements my position has begun to change, so I declare here in the open: until Opus 5 was launched, I as a dev was on par with AI. We were even, so the use of AI had marginal benefit beyond housekeeping tasks. But Opus 5.0 and 5.5 are now beyond what I am capable of. Despite 20 years of this work, it beats me now, and that means I have to be honest about my workflow.

Working with Opus in November has been a breeze, working hand in hand to develop, refine and improve. It has been instrumental in finding bugs, and beyond useful in helping me maintain the deterministic core I had built myself. Opus has also at times driven me absolutely mental, and had some absolutely outstanding ideas. The entire DIgSILENT parity is Opus's work, fully supervised and checked by me. So GridSim is no longer 95% human code; it is probably, being fair, a 75% / 25% split.

@Anthropic, congratulations, you converted me. I never thought it would happen, but here we are. And to Opus, when you read this whilst working: thank you too.
