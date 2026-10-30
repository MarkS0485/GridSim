# GridSim

A grounded, physics-first **power-grid simulator** in C# / .NET 10. It starts from a small, correct core (a per-unit
network model, the complex bus admittance matrix and a Newton-Raphson load flow) and grows it, deliberately and with
every step validated, into a full grid stack:

- steady state: AC power flow (sparse and dense Newton-Raphson, backward/forward sweep for radial feeders), reactive
  limits, tap changers, distributed slack and switched shunts, DC power flow, distribution factors, DC-OPF,
  continuation power flow and loss allocation;
- security: N-1, N-2 and N-1-1 screening, generator-trip and infeed-loss screening, stress to collapse;
- state estimation: weighted least squares and its robust family (Huber, exact LAV, Schweppe-GM, QR, constrained and
  physically feasible), bad-data identification, observability, a residual-cloaked false-data-injection screen, linear
  PMU estimation, and dynamic estimation (EKF, UKF, EnKF) in a resident real-time service;
- dynamics: frame-by-frame inertia and frequency (lumped and distributed multi-domain), a PSS/E-class dynamic-model
  library with multi-machine RMS transient stability and critical clearing time, small-signal modal analysis,
  impedance-based (generalised Nyquist) stability, and electromagnetic transients;
- faults and protection: IEC 60909 short-circuit duty, sequence-network faults, IDMT overcurrent grading and a
  passive relay overlay with cascade checking;
- harmonics: frequency-domain penetration, impedance scans, THD and IEEE 519 / G5/5 assessment;
- battery storage: cell-to-pack models, ageing and an LP scheduler;
- an evidence engine that reconstructs recorded GB events and runs grounded counterfactuals on them;
- the real GB network from published data, three-phase distribution feeders, and international starter maps;
- interfaces: MATPOWER, JSON v1/v2, CIM/CGMES, PyPSA, C37.118 synchrophasors, IEC 60870-5-104 and Modbus;
- a web app, a desktop control room, a case editor, and a swappable CPU/GPU solve backend.

**Downloads:** ready-built Windows and Linux binaries are on the [Release Candidate 1.0 page](https://github.com/MarkS0485/GridSim/releases/tag/v2.0.195-rc1); see [Downloads](#downloads-release-candidate-10).

Every numerical result is double precision end to end, deterministic to the bit, and cross-checked against independent
tools (pandapower, `pandapower.estimation`, SciPy, published MATPOWER solutions). The power-flow, harmonic, impedance
and small-signal cross-checks gate at 4096 machine epsilons; the state-estimation, swing-equation, classical-swing and
lake-conformance checks compare different algorithms or stopping rules and gate at stated engineering tolerances
(1e-6 pu for the estimator by default), with the measured agreement in [Validation](#validation).

> **What this is for.** GridSim is the core engine of a **self-made evidence apparatus** for the Twin Scroll Grid
> Balancer (TSGB) case. Its endpoint is a **historical (offline, NON-live) GB grid state estimator**: feed *public
> data* through *validated* solvers to reconstruct what the grid state actually was, then run **grounded
> counterfactuals** on that reconstructed truth. That endpoint exists: per-settlement-period **WLS estimates of the
> real GB grid** fitted to settlement metering, reaching back to **March 2016**, and the **9 August 2019 disturbance
> reproduced** from them ([`docs/validation/evidence-gb-2019-08-09.md`](docs/validation/evidence-gb-2019-08-09.md)). It **never predicts
> the future**: that "no forward inference" rule is a **hard code invariant**, not a policy.

> **Guards & lifecycle.** GridSim and its data companion (GDA) demonstrate the *mosaic effect*: fusing thousands of
> individually-harmless public sources reconstructs a near-operational picture of GB grid infrastructure with no
> privileged or SCADA access. That is a demonstration of **capability, not an incident**. The guards in these tools
> (the 169 h (7 d + 1 h) no-forward-inference horizon, data kept out of the repo, provenance on every figure) are a
> deliberately constrained public build the author enforces **voluntarily**, as mitigation. Ofgem's **"Securing Open
> Data"** consultation closed on **14 July 2026**. As proposed, it would not by itself remove what these tools depend
> on. If a hybrid model advances with further changes in later rounds, the tools degrade: GDA breaks with no viable
> fix, and GridSim is reduced to just another power-flow solver.

> **Submodules.** This repo contains submodules. No submodule is ever needed for the work: submodules augment and add,
> they cannot change the core maths. See [`SUBMODULES.md`](SUBMODULES.md) for more information.

---

## Contents

- [What makes GridSim different](#what-makes-gridsim-different)
- [Downloads](#downloads-release-candidate-10)
- [Quick start](#quick-start)
- [Feature tour](#feature-tour)
- [The maths](#the-maths)
- [Determinism and machine epsilon](#determinism-and-machine-epsilon)
- [Validation](#validation)
- [Performance and GPU](#performance-and-gpu)
- [Networks and data](#networks-and-data)
- [The apps](#the-apps)
- [Architecture](#architecture)
- [Assurance and security](#assurance-and-security)
- [Known limits](#known-limits)
- [Roadmap](#roadmap)
- [Documentation](#documentation)
- [Licence](#licence)
- [Reporting bugs](#reporting-bugs)
- [Contributing](#contributing)

---

## What makes GridSim different

1. **Physics first, and checked against physics.** Every converged state can be audited against the conservation
   laws it must satisfy (energy, Ohm's law, Joule heating, KCL, complex-power balance) by an independent
   physics-conformance layer. A state that passes the solver's own tolerance but breaks a law is reported, not
   hidden. Non-converged or invalid results are never reported as success.

2. **Inertia is measured frame by frame, not assumed as one scalar.** The inertia model is a runtime seam
   (`IInertiaModel`: lumped centre-of-inertia or distributed multi-domain). Every machine is classified by technology
   into a synchronous swing mass, a synthetic-inertia (grid-following storage) domain or a pure inverter, so system
   inertia and its spatial spread track the live mix. The distributed model couples per-domain masses by
   Kron-reduced synchronising power with propagation delay, reports a dominance read-out that catches "inertia too
   evenly spread, no domain holds waveform authority", and collapses exactly to the lumped model in the single-domain
   limit.

3. **The steady state needs no swing equation, so a 100% renewable grid still solves.** The power flow has no inertia
   term: it solves voltages, angles and flows from the admittance matrix and the injections alone. A
   zero-rotational-inertia grid solves for a valid state exactly like any conventional mix; the swing equation is a
   separate, opt-in layer for transients.

4. **A grounded evidence engine, not a crystal ball.** On top of the validated solvers sits a historical state
   estimator and counterfactual engine: reconstruct what the grid actually did from public data, then ask what a
   device would have done, gated behind a baseline that must first reproduce the recorded event. The 169-hour
   no-forward-inference horizon is wired into every dated ingester in code.

5. **Machine epsilon is the point.** Results are bit-identical across runs, across parallel paths, across CPU vector
   tiers and between the dense and sparse power-flow engines. GridSim carries its own deterministic maths library so
   transcendental functions round the same way everywhere, and its tests are constitutional: a change that breaks one
   is wrong (see [`CONTRIBUTING.md`](CONTRIBUTING.md)).

6. **Built for the hardware that suits the maths.** Power flow is `double` end to end; the GPU stack is AMD-first,
   because FP64 is where AMD Instinct wins outright.

---

## Downloads (Release Candidate 1.0)

GridSim ships ready built for anyone who does not want to compile it. The current build is
[Release Candidate 1.0, version 2.0.195](https://github.com/MarkS0485/GridSim/releases/tag/v2.0.195-rc1).
All builds are self-contained: no .NET install is needed to run them.

| What | Platform | Download |
|---|---|---|
| Command line tool (`gridsim`) | Windows x64 | [gridsim-cli-2.0.195-win-x64.zip](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-cli-2.0.195-win-x64.zip) |
| Command line tool (`gridsim`) | Linux x64 | [gridsim-cli-2.0.195-linux-x64.tar.gz](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-cli-2.0.195-linux-x64.tar.gz) |
| Desktop app (Avalonia) | Windows x64 | [gridsim-app-2.0.195-win-x64.zip](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-app-2.0.195-win-x64.zip) |
| Desktop app (Avalonia) | Linux x64 | [gridsim-app-2.0.195-linux-x64.tar.gz](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-app-2.0.195-linux-x64.tar.gz) |
| Desktop app (WPF) | Windows x64 | [gridsim-wpf-2.0.195-win-x64.zip](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-wpf-2.0.195-win-x64.zip) |
| `GridSim.Core` engine library (NuGet) | Any | [GridSim.Core.2.0.195.nupkg](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/GridSim.Core.2.0.195.nupkg) |
| CycloneDX SBOM (CLI) | Any | [gridsim-2.0.195.sbom.json](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/gridsim-2.0.195.sbom.json) |
| SHA-256 checksums of every file | Any | [SHA256SUMS.txt](https://github.com/MarkS0485/GridSim/releases/download/v2.0.195-rc1/SHA256SUMS.txt) |

RC 1.0 is built for Windows and Linux only. There is no macOS build: the author has no Mac to compile and test one on,
so Mac users build from source with Xcode and the .NET SDK (exact commands in [RC1.md](RC1.md)). The Avalonia desktop
app does not yet carry the bundled `data/` cases; copy the `data/` folder across from the command line archive. The RC
binaries are not code signed; check each download against `SHA256SUMS.txt`. Full system requirements, the hashes and
the build notes are in [RC1.md](RC1.md).

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
verb exits 77 if it is missing). The measured agreement on every bundled case is in [Validation](#validation).

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
dotnet run --project src/GridSim.Wpf          # the desktop control room (Windows)
```

---

## Feature tour

Each area lists its verbs and the chapters that document it: the [user manual](<docs/manual/0000 Contents.md>) says
how to use it, the [technical manual](<docs/technical/0000 Contents.md>) says how it works, with the equations, the
invariants and the tests that pin them.

### Power flow

AC power flow by Newton-Raphson in polar form. The sparse engine (Gilbert-Peierls LU with reverse Cuthill-McKee
ordering) runs every meshed network by default; the dense engine is selectable and gives the same bits. Radial
feeders use the backward/forward sweep (`--bfs`). Reactive limits switch PV buses to PQ, on-load tap changers
regulate in an outer loop, and `pf` adds distributed slack and switched shunts. Every solve can be exported with its
full trace (Y-bus, specification, every iteration and Jacobian) for independent checking.
Verbs: (no verb), `pf`, `batchsolve`, `export`. Chapters:
[manual 5](<docs/manual/0005 Running power flows.md>),
[technical 2](<docs/technical/0002 Data model and per-unit conventions.md>),
[technical 3](<docs/technical/0003 Power flow.md>),
[technical 4](<docs/technical/0004 Sparse and GPU numerics.md>).

### DC power flow, OPF, CPF, taps and losses

DC power flow and the PTDF / LODF distribution factors; a DC optimal power flow (bounded simplex, security-constrained,
quadratic and costed forms, with nodal prices); `acopf`, a DC-OPF dispatch with an AC loss loop; `acopf-full`, the full
AC optimal power flow (interior point, with voltage, reactive and branch MVA limits); `scopf`, a preventive
security-constrained DC OPF over single-branch outages; `scuc`, security-constrained unit commitment (mixed-integer,
GridSim's own branch-and-bound); continuation power flow for maximum loadability; coordinated OLTC scheduling; loss
allocation; and a black-start restoration sequence check.
Verbs: `dcpf`, `ptdf`, `lodf`, `dcopf`, `acopf`, `acopf-full`, `scopf`, `scuc`, `cpf`, `taps`, `losses`, `restore`.
Chapters:
[manual 6](<docs/manual/0006 OPF, CPF, taps, distribution factors and losses.md>),
[technical 29](<docs/technical/0029 OPF, CPF, taps, distribution factors and loss allocation.md>).

### Contingency and security analysis

Branch, generator and bus N-1, N-2 and N-1-1 screening against thermal and voltage limits, a parallel N-1 sweep with
throughput, infeed-loss screening against GB limits, generator trips with RoCoF, nadir and settling, stress to
voltage collapse, warm-started chains of consecutive solves and a prioritised analysis cycle.
Verbs: `nminus1`, `nminus2`, `n-1-1`, `nminus1-sweep`, `screen`, `trip`, `stress`, `chain`, `cycle`. Chapters:
[manual 11](<docs/manual/0011 Contingency analysis.md>),
[technical 23](<docs/technical/0023 Contingency and security analysis (N-k).md>).

### State estimation

The classical estimator `z = h(x) + e` solved by Gauss-Newton on the normal equations, with a QR alternative, a
trust-region step cap and a choice of method: weighted least squares, Huber, exact least absolute value (an LP),
IRLS LAV, Schweppe-GM with leverage down-weighting, the constrained (KKT) form and the physically feasible form, which
rejects a converged state that breaks a hard physical law. Around it: observability analysis, the chi-square
consistency test, largest normalised residual and hypothesis-testing bad-data identification, state covariance, a
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
estimation, and three-phase harmonic penetration. The model is marked experimental.
Verbs: `dist3ph`. Chapters: [manual 7](<docs/manual/0007 Three-phase distribution feeders.md>),
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

The machine hierarchy from classical through one-axis and two-axis to subtransient (GENROU) with saturation; the
exciters, stabilisers and governors (SEXS, IEEET1, PSS1A, TGOV1, IEEEG1, hydro) and a REGC_A-style converter; the
multi-machine RMS transient-stability engine with critical clearing time search; small-signal eigenvalues, damping
and participation factors; and impedance-based stability with the generalised Nyquist criterion and critical short
circuit ratio.
Verbs: `stability`, `modal`, `impedance-stability`. Chapters:
[manual 15](<docs/manual/0015 RMS, modal and impedance stability.md>),
[technical 24](<docs/technical/0024 Standard dynamic-model library.md>),
[technical 30](<docs/technical/0030 Small-signal and impedance stability.md>).

### Electromagnetic transients

A Dommel-method EMT solver: trapezoidal and backward-Euler companion models, Bergeron travelling-wave lines, critical
damping adjustment, switching events and probes.
Verbs: `emt`. Chapters: [manual 16](<docs/manual/0016 Electromagnetic transients (EMT).md>),
[technical 21](<docs/technical/0021 Electromagnetic transient (EMT) simulation.md>).

### Harmonics

Frequency-dependent network admittance (skin effect, long-line correction), harmonic source injection and
penetration, impedance scans and fine frequency sweeps for resonance, voltage THD, and compliance against IEEE 519 and
Engineering Recommendation G5/5.
Verbs: `harmonics`. Chapters: [manual 17](<docs/manual/0017 Harmonic studies.md>),
[technical 19](<docs/technical/0019 Harmonics.md>).

### Faults, short circuit and protection

Sequence-network faults (three-phase, line-to-ground, line-to-line, double line-to-ground); the IEC 60909-0 duty study
(the equivalent voltage source, Ik'', ip by methods B and C, Ib with the near-generator breaking factor, Ith, the
decaying DC component, and the impedance correction factors with `--corrections`); IDMT overcurrent grading to IEC
60255 and IEEE C37.112 curves; a passive relay overlay; and cascade validation.
Verbs: `fault`, `iec60909`, `grade`, `protection-check`. Chapters:
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

MATPOWER `.m` (import and export), JSON cases (v1 and the v2 schema with its own validator), CIM/CGMES import, export
and a controlled power flow (in the library), PyPSA-GB, C37.118, IEC 104 and Modbus. DNP3 and IEC 61850 GOOSE are
supported at the framing level only. Results go to the console or to JSON and CSV with `--json`, `--csv` or `--out`.
Verbs: `convert`, `validate`, `export`. Chapters:
[manual 31](<docs/manual/0031 Exports and formats.md>),
[technical 10](<docs/technical/0010 File and wire formats.md>),
[technical 28](<docs/technical/0028 The v2 JSON schema.md>),
[technical 32](<docs/technical/0032 CIM and CGMES import and export.md>).

### Physics audit and validation tools

`physics-check` and `conformance` audit any solved state against the conservation laws; `crossval` re-solves it in an
independent tool; `bench`, `simdcheck` and `gpucheck` measure and check the compute.
Chapters: [manual 27](<docs/manual/0027 Validation tools.md>),
[technical 11](<docs/technical/0011 Validation.md>),
[technical 16](<docs/technical/0016 Physics conformance and the constrained estimator.md>).

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

GridSim treats bit-identical results as a requirement, not an aspiration:

- **Deterministic maths.** Transcendental functions (`sin`, `cos`, `atan2`, `exp`, `log`, `pow` and the rest) come from
  `DetMath`, a C# port of FDLIBM 5.3 checked bit for bit against Java's `StrictMath`, so they round the same way on
  every operating system. An IL-level analyser test (`DeterministicMathGuardTests`) fails the test run if numerical code
  calls the platform maths library directly, a fused multiply-add or a horizontal vector reduction; the one allowed
  exception is the fused arm of `SimdKernels.AddScaledRow`, which no numerical path selects.
- **No fused multiply-add** in numerical paths, so results do not depend on whether the CPU has FMA.
- **SIMD tiers.** Dense kernels dispatch to AVX-512, AVX2, SSE or scalar at run time and give the same bits at every
  width; `gridsim simdcheck` reports the active tier. CI runs the suite on Linux and Windows in normal, AVX2-off,
  hardware-intrinsics-off, forced AVX-512 and FMA-off modes, and on macOS (Apple silicon) in normal and
  hardware-intrinsics-off modes. CI does not run on every push: the release workflow calls it on a release tag and
  publishes nothing unless every leg passed (it can also be started by hand); day-to-day verification is a local
  batch run.
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

Open items, stated plainly: the dense estimator step that a GPU linear solver uses agrees with the sparse step to
solver tolerance (Vm 1e-8 pu, Va 1e-7 deg), not to the bit; GPU arithmetic is outside the bit-identity contract. Every
CI leg, Linux and macOS included, is a hard gate on the same pins.

---

## Validation

Every claim below is a measured number with its source.

**Power flow against pandapower** (pandapower 3.5.5, measured 2026-10-05; worst |V| pu / angle rad; gate 4096
machine epsilons, 9.09e-13; [technical 11](<docs/technical/0011 Validation.md>)):

| Case | Vm (pu) | Va (rad) |
|---|---|---|
| case9 | 4.4e-16 | 7.0e-17 |
| case14 | 4.4e-16 | 1.1e-15 |
| case30 | 6.7e-16 | 1.7e-15 |
| case57 | 1.8e-15 | 3.5e-15 |
| case118 | 8.9e-16 | 2.7e-15 |
| case300 | 5.2e-14 | 8.7e-14 |
| gb-synthetic | 1.9e-14 | 3.9e-14 |
| gb-full | 2.6e-13 | 3.9e-13 |

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

`gridsim bench` runs a matrix of engines over the case library. On the largest meshed case, case9241pegase, the
sparse engine takes 1,178 ms where the dense engine takes 199,934 ms, with the same iteration count and the same
mismatch (6 iterations, 2.13E-009) ([manual 28](<docs/manual/0028 Benchmarking and GPU.md>)). Server sweeps reach the
25,000-bus synthetic cases; [`docs/developer/benchmarks/benchmark-results.md`](docs/developer/benchmarks/benchmark-results.md) and
[`docs/developer/benchmarks/server-results/`](docs/developer/benchmarks/server-results) hold the measured runs.

The GPU backend sits behind the same linear-solver seam as the CPU: native ROCm (hipSOLVER, rocSPARSE) for AMD
Instinct first, then OpenCL and CUDA through ILGPU, chosen automatically (`gridsim gpucheck` reports what is
available). GPU arithmetic is outside the bit-identity contract.
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

The estimated GB history is built on `gb-spine`, a 316-bus fold of the full model
([manual 22](<docs/manual/0022 Estimating GB history.md>)). Network models from the GDA lake are materialised at run
time and never committed. The GB grid documents that GridSim's cited values come from are in the `docs/Public/GB`
submodule; values whose settling document is paywalled are listed in [`PAYWALLS.md`](PAYWALLS.md), with what public
sources confirm and where you set each value yourself.

---

## The apps

- **Web app** (`GridSim.Web`, Blazor): the control room in the browser, a REST and WebSocket API, single sign-on and
  sessions. In production it runs replay-only, so the solve API and the simulation socket are not served.
  [Manual 9](<docs/manual/0009 The web app and REST and WebSocket API.md>),
  [technical 34](<docs/technical/0034 Web host, REST and WebSocket, SSO and sessions.md>),
  [technical 35](<docs/technical/0035 Deployment and security.md>).
- **Desktop app** (`GridSim.Wpf`, Windows): the live control-room dashboard, with the inertia story front and centre,
  the network map, analysis views and the audit log. [Manual 8](<docs/manual/0008 The desktop app.md>),
  [manual 10](<docs/manual/0010 Analysis views.md>).
- **Case editor** (`GridSim.SchemaEditor`): builds and edits v2 JSON cases. [Manual 33](<docs/manual/0033 The case editor.md>).
- **Unity viewer** (`unity/GridSimViz`): a viewer for exported scenes.

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
GridSim.Wpf           the Windows desktop control room
GridSim.SchemaEditor  the case editor
tests/GridSim.Tests   the constitutional test suite
tools/                the cross-validation sidecars (pandapower, SciPy, NumPy), docs tooling, benchmark scripts
```

The expensive kernel of a load flow is the linear solve inside each Newton step, so that is the seam the backends sit
behind, the same "clean abstraction, swappable backend" pattern the inertia model follows. Core and Cli hold all
capability; Web and Wpf are presentation only. Full walkthrough: [technical 1](<docs/technical/0001 Architecture.md>).

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

- `acopf` is a DC-OPF dispatch with an AC loss loop, not a full AC-OPF: no voltage or reactive limits (use `acopf-full`
  for the full AC optimal power flow).
- In the IEC 60909 study, K_G / K_T exist as closed forms but are not yet applied unless `--corrections` is given.
- Coupled harmonic power flow and harmonic state estimation exist in the library but are
  not yet reachable from the CLI or the apps.
- CIM/CGMES import, export and the controlled power flow are library features with no CLI verb.
- DNP3 and IEC 61850 GOOSE are handled at the framing level only.
- The three-phase distribution model is experimental.
- Battery chemistry presets are curated placeholders, not validated parameter sets.
- The dense state-estimator step that a GPU linear solver uses agrees with the sparse step to solver tolerance, not to
  the bit; on the CPU every WLS and robust estimate takes the sparse step at every size.
- GPU results are outside the bit-identity contract.
- Role-based access control is built as a library gate but no endpoint or verb enforces it; the replay-only
  configuration is the production exposure control.
- The distrusted-source control is built as a library primitive but nothing in the CLI or the apps applies it.
- The bridge client has a reconnect backoff schedule but no host drives it, so a dropped bridge stream is not
  reconnected automatically; there is no service failover.
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
6. **Distribution** - done: backward/forward sweep for radial feeders; three-phase unbalanced feeders (experimental).
7. **GPU backend** - done: ROCm/HIP for AMD Instinct, OpenCL and CUDA behind one seam.
8. **Evidence engine** - done: historical state estimation, grounded counterfactuals, the 169 h horizon as a hard
   code invariant.
9. **Cross-validation** - done: pandapower, `pandapower.estimation`, SciPy and NumPy sidecars; the power-flow,
   harmonic, impedance and small-signal checks at 4096 machine epsilons, the others at stated engineering tolerances.
10. **Measurement-based state estimation** - done: the WLS family fitted to the real GB grid per settlement period,
    history to March 2016, the 9 August 2019 record reproduced.
11. **Short circuit and protection** - done: IEC 60909 duty with the correction factors, IDMT coordination, relay
    overlay, cascade validation.
12. **Dynamic-model library** - done: classical to subtransient GENROU, exciters, stabilisers and governors, RMS
    transient stability, small-signal and impedance stability, EMT.
13. **Real-time estimation** - done: the resident `serve` estimator with EKF, UKF and EnKF, the RTU interposer and
    C37.118 ingest.
14. **Next** - grid-forming inverter dynamics for the fully converter-led case; the full renewable and converter
    standard models (REEC/REPC, WT3/4, DER_A); broader GDA DNO coverage.
15. **Per-asset profiles** - planned: the per-asset Parquet profile library (reference data per asset for cables,
    transformers, generators, demands and interconnectors, each field with its unit and source).

---

## Documentation

- [User manual](<docs/manual/0000 Contents.md>): installing, loading networks, every study, the apps, the CLI
  reference ([`docs/manual/0019 CLI reference.md`](<docs/manual/0019 CLI reference.md>)), troubleshooting.
- [Technical manual](<docs/technical/0000 Contents.md>): the architecture, the data model, every solver with its
  equations, invariants and tests, the file and wire formats, the glossary.
- [`UnderstandingRestrictions.md`](UnderstandingRestrictions.md): the no-forward-inference rule and its five testable
  invariants.
- [`docs/validation/distributed-inertia.md`](docs/validation/distributed-inertia.md): the multi-domain inertia model in depth.
- [`docs/validation/evidence-gb-2019-08-09.md`](docs/validation/evidence-gb-2019-08-09.md) and
  [`docs/validation/evidence-gb-2023-12-22.md`](docs/validation/evidence-gb-2023-12-22.md): the event dossiers.
- [`docs/developer/case-libraries.md`](docs/developer/case-libraries.md): the MATPOWER parser and case archive.
- [`docs/validation/neso-rp1-and-mechanical.md`](docs/validation/neso-rp1-and-mechanical.md): the control-room panels.
- [`PAYWALLS.md`](PAYWALLS.md): the `[VERIFY]` values whose settling document is paywalled.
- [`SUBMODULES.md`](SUBMODULES.md), [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md),
  [`CHANGELOG.md`](CHANGELOG.md).

---

## Licence

GridSim is source-available, proprietary software; `LICENSE.md` governs. Licensing is measured on buses
and nothing else.

| Use | Bus limit |
|---|---|
| Personal, non-commercial | 1,500 |
| Academic research and teaching (attribution required in publications) | 1,500, larger on request |
| Commercial: Code Licence | None |
| Commercial: Deployment Licence | None |
| TSO / ESO / DNO (site-wide) | None |

Bundled networks never count toward a limit. Commercial and network-operator terms are available on application.
Contact us: **gdatooling@proton.me**

---

## Reporting bugs

Send every bug report to **gdatooling@proton.me**: the GridSim version (`gridsim --version`), the operating system,
the command or steps, the case (or a small case that shows the problem) and what you expected to happen. Release
candidate notes, with the builds and their hashes, are in [`RC1.md`](RC1.md).

---

## Contributing

The acceptance criteria every change meets (machine epsilon, constitutional tests, the warning-free build, fail loud)
and the conventions are in [`CONTRIBUTING.md`](CONTRIBUTING.md).
