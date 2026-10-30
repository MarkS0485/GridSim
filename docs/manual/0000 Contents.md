# GridSim user manual

This manual covers using GridSim: building it, loading networks, running solves and studies, driving the desktop and
web apps, estimating historical GB grid states, and replaying the results. Each chapter stands alone, so start at the
task you need. The internals, equations and invariants are in the [technical manual](<../technical/0000 Contents.md>),
and the writing rules in [STYLE.md](../developer/STYLE.md).

Chapter files are named `NNNN Chapter title.md`, where NNNN is the chapter number. Chapters marked *in preparation*
are being written.

## I. Getting going

| Ch | Chapter | What it covers |
|---|---|---|
| 1 | [Getting started](<0001 Getting started.md>) | Install the .NET SDK, build, run the tests, first solve. |
| 2 | [Loading networks](<0002 Loading networks.md>) | Built-in cases, MATPOWER files, JSON case folders, the GB models and the case library. |
| 3 | [Configuration](<0003 Configuration.md>) | Environment variables, settings files and paths. |
| 4 | [Licensing and activation](<0004 Licensing and activation.md>) | Tiers, bus limits, installing and checking a licence. |

## II. Steady state

| Ch | Chapter | What it covers |
|---|---|---|
| 5 | [Running power flows](<0005 Running power flows.md>) | The bare run, the report, engine choice, Q-limits, OLTC, solve exports, recorded runs. |
| 6 | [OPF, CPF, taps, distribution factors and losses](<0006 OPF, CPF, taps, distribution factors and losses.md>) | `dcpf`, `dcopf`, `acopf`, `cpf`, `taps`, `ptdf`, `lodf`, `losses`. |
| 7 | [Three-phase distribution feeders](<0007 Three-phase distribution feeders.md>) | `dist3ph`: unbalanced sweep, faults and estimation on feeders. |
| 34 | [Time series and hosting capacity](<0034 Time series and hosting capacity.md>) | `qsts`: quasi-static time series with timed tap changers, capacitor banks, inverters and storage; `hosting`: PV and load hosting capacity. |

## III. The apps

| Ch | Chapter | What it covers |
|---|---|---|
| 8 | [The desktop app](<0008 The desktop app.md>) | The WPF app (and how the web app differs): map, live view, pickers, replay. |
| 9 | [The web app and REST/WebSocket API](<0009 The web app and REST and WebSocket API.md>) | `GridSim.Web`: the browser dashboard, `/api/cases`, `/api/topology`, `/api/solve`, `/api/realtime`, the `/ws/sim` stream, sessions and the replay-only mode. |
| 10 | [Analysis views](<0010 Analysis views.md>) | SCADA, OPERATOR, MARKET, RP1, MECHANICAL, CYCLE and ASSETS tabs. |
| 33 | [The case editor](<0033 The case editor.md>) | `GridSim.SchemaEditor`: browse, edit, add, delete and rename case records, checked and saved byte for byte; `--check`. |

## IV. Security and protection

| Ch | Chapter | What it covers |
|---|---|---|
| 11 | [Contingency analysis](<0011 Contingency analysis.md>) | `trip`, `screen`, N-1, N-2, N-1-1, sweeps, `stress`, `chain`, `cycle`. |
| 12 | [Faults, short circuit and protection grading](<0012 Faults, short circuit and protection grading.md>) | `fault`, `iec60909`, `protection-check`, `grade`. |
| 13 | [Battery storage studies](<0013 Battery storage studies.md>) | `--bess` sites on `chain` and `gda-replay`: chemistries, dispatch policies, state of charge, losses and ageing. |

## V. Dynamics

| Ch | Chapter | What it covers |
|---|---|---|
| 14 | [Frequency studies](<0014 Frequency studies.md>) | `freq`, `transient`, `replay`, `domains`, `h0-study`, `fit-frequency`, the live frequency view. |
| 15 | [RMS, modal and impedance stability](<0015 RMS, modal and impedance stability.md>) | `stability` (classical and RMS, CCT, RMS event studies), `modal`, `impedance-stability`, machine data files. |
| 16 | [Electromagnetic transients](<0016 Electromagnetic transients (EMT).md>) | The `emt` verb: switching, faults and waveforms. |
| 17 | [Harmonic studies](<0017 Harmonic studies.md>) | The `harmonics` verb: penetration, impedance scans and resonance, IEEE 519 compliance. |

## VI. Events

| Ch | Chapter | What it covers |
|---|---|---|
| 18 | [Replaying recorded events](<0018 Replaying recorded events.md>) | `replay-event`, verdicts, authoring an event. |
| 20 | [Counterfactuals](<0020 Counterfactuals.md>) | `scenario`, `fleet`, `evidence`. |

## VII. Estimation

| Ch | Chapter | What it covers |
|---|---|---|
| 21 | [State estimation](<0021 State estimation.md>) | The `estimate` verb: methods (`lav` by linear programming), measurements, bad data, chi-square; the linear PMU estimate `pmu-estimate`. |
| 22 | [Estimating GB history](<0022 Estimating GB history.md>) | The GDA estimation pipeline. |
| 23 | [Replaying estimated states](<0023 Replaying estimated states.md>) | Viewing estimated states in the apps. |
| 24 | [The GDA bridge](<0024 The GDA bridge.md>) | Connecting GridSim to the GDA data lake. |

## VIII. Operations

| Ch | Chapter | What it covers |
|---|---|---|
| 25 | [Interposing live telemetry](<0025 Interposing live telemetry.md>) | The RTU/SCADA interposer (IEC 104 and Modbus) and the IEEE C37.118 synchrophasor ingest `pmu-ingest`, live or recorded. |
| 26 | [Audit log, deployment and assurance](<0026 Audit log, deployment and assurance.md>) | `--audit-log` and its anchor, `audit verify` and `audit show`, the web host in Docker behind nginx, SSO, CI, and the assurance documents. |
| 27 | [Validation tools](<0027 Validation tools.md>) | `crossval`, SE sidecars, `physics-check`, `conformance`, `simdcheck`. |
| 28 | [Benchmarking and GPU](<0028 Benchmarking and GPU.md>) | `bench`, `batchsolve`, `gpucheck`, `simdcheck`, the GPU backends, published results, renting a GPU server. |

## IX. Reference

| Ch | Chapter | What it covers |
|---|---|---|
| 19 | [CLI reference](<0019 CLI reference.md>) | Every verb, flag, default and exit code. (Its file keeps the number 19 because a test reads it.) |
| 29 | [Extending GridSim](<0029 Extending GridSim.md>) | The extension seams: solvers, estimators, assets, formats. |
| 30 | [Troubleshooting](<0030 Troubleshooting.md>) | Messages, exit codes and what to do about them. |
| 31 | [Exports and formats](<0031 Exports and formats.md>) | Every export and run-directory file, field by field. |
| 32 | Glossary (*in preparation*) | User-level terms; the full glossary is technical ch. 12. |
