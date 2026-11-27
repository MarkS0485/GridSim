# GridSim technical manual

The subsystem reference for maintaining, extending or auditing GridSim: the mathematics as implemented, the
parameters and defaults, the invariants and the tests that pin them, the wire formats and the architecture. How to
*use* GridSim is in the [user manual](<../manual/0000 Contents.md>).

Chapter files are named `NNNN Chapter title.md`, where NNNN is the chapter number. Chapters marked *in preparation*
are being written.

## Foundations

| Ch | Chapter | What it covers |
|---|---|---|
| 1 | [Architecture](<0001 Architecture.md>) | Projects, seams and data flows. |
| 2 | [Data model and per-unit](<0002 Data model and per-unit conventions.md>) | `GridModel` records, per-unit conventions, the Y-bus. |
| 4 | [Sparse and GPU numerics](<0004 Sparse and GPU numerics.md>) | CSR storage, sparse LU, ILU/BiCGSTAB, GPU backends, the tolerance table. |
| 8 | [Provenance and invariants](<0008 Provenance and invariants.md>) | Provenance tags, `[VERIFY]`, the 169-hour guard, determinism rules. |
| 10 | [File and wire formats](<0010 File and wire formats.md>) | Every schema and wire format. |
| 12 | [Glossary](<0012 Glossary.md>) | Terms, symbols and reference tables. |
| 28 | [The v2 JSON schema](<0028 The v2 JSON schema.md>) | The v2 case, network, library and kind schemas. |

## Steady state

| Ch | Chapter | What it covers |
|---|---|---|
| 3 | [Power flow](<0003 Power flow.md>) | Dense and sparse Newton-Raphson, Q-limits, the OLTC loop, ZIP loads, the sweep, bit-identity. |
| 23 | [Contingency and security analysis](<0023 Contingency and security analysis (N-k).md>) | N-k, N-2, N-1-1 and their cost. |
| 29 | [OPF, CPF, taps, distribution factors and loss allocation](<0029 OPF, CPF, taps, distribution factors and loss allocation.md>) | DC power flow, DC-OPF and the security simplex, the AC loss loop, the AC-OPF objectives and controls, AC-SCOPF, feeder reconfiguration, CPF, PTDF/LODF, loss allocation, the tap optimiser. |
| 40 | [Network equivalents](<0040 Network equivalents.md>) | Ward, extended Ward and REI equivalents: the Kron reduction, the transferred injections, the XW sources, the ZPB network, the base-case exactness and the perturbation error; the short-circuit equivalent (reduced sequence networks) and the coherency-based dynamic equivalent. |

## Dynamics

| Ch | Chapter | What it covers |
|---|---|---|
| 5 | [Frequency dynamics](<0005 Frequency dynamics.md>) | The centre-of-inertia swing model, `DynamicsConfig`, forming, distributed inertia. |
| 13 | [Asset-dynamics data grounding](<0013 Asset-dynamics data grounding.md>) | Which asset parameters are grounded and which are `[VERIFY]`. |
| 14 | [Cascade validation and blind prediction](<0014 Cascade validation and blind prediction.md>) | Blind cross-event validation of the asset cascade. |
| 24 | [Standard dynamic-model library](<0024 Standard dynamic-model library.md>) | Machines, exciters, governors, PSS, dynamic loads, SVC, STATCOM and HVDC, RMS transient stability with unbalanced faults, unit relays, user models and the plug-in contract. |
| 39 | [Grid-code compliance](<0039 Grid-code compliance.md>) | GB Grid Code ECC and RfG 2016/631: FRT lines, reactive capability, LFSM, FSM, RoCoF and frequency relay checks, the requirement files and the verdict. |
| 30 | [Small-signal and impedance stability](<0030 Small-signal and impedance stability.md>) | Classical swing and CCT search, modal analysis, converter impedance and Nyquist stability. |
| 21 | [Electromagnetic transient simulation](<0021 Electromagnetic transient (EMT) simulation.md>) | Companion models, the EMT engine, three-phase EMT. |
| 19 | [Harmonics](<0019 Harmonics.md>) | Y(h), frequency scan, harmonic power flow, harmonic state estimation. |
| 27 | [Battery energy storage](<0027 Battery energy storage.md>) | The chemistry-first cell, pack and ageing model. |

## Protection

| Ch | Chapter | What it covers |
|---|---|---|
| 15 | [Protections](<0015 Protections.md>) | The protection device layer on the asset seam. |
| 17 | [Protection assessment](<0017 Protection assessment (a passive relay overlay).md>) | The passive relay overlay, fault injection and cascade. |
| 25 | [IEC 60909 short-circuit duty study](<0025 IEC 60909 short-circuit duty study.md>) | c-factor, duty currents, correction factors. |
| 26 | [Overcurrent coordination](<0026 Overcurrent coordination (IDMT grading).md>) | IDMT curves and grading; placed distance, directional, earth-fault and differential elements and their coordination checks. |

## Estimation and evidence

| Ch | Chapter | What it covers |
|---|---|---|
| 6 | [State estimation](<0006 State estimation.md>) | WLS, robust estimators, chi-square, bad-data identification. |
| 16 | [Physics conformance and the constrained estimator](<0016 Physics conformance and the constrained estimator.md>) | The physical-law audit and the Feasible estimator. |
| 18 | [Real-time, distribution and dynamic estimation](<0018 Real-time, distribution and dynamic estimation.md>) | DSE, three-phase distribution, PMU, CIM and telemetry. |
| 7 | [The evidence engine](<0007 The evidence engine.md>) | Events, replay, scenarios and the dossier. |
| 9 | [GDA integration](<0009 GDA integration.md>) | The bridge protocol, LTDS assembly and lake products. |
| 11 | [Validation](<0011 Validation.md>) | Cross-checks against MATPOWER, pandapower and scipy, and the tolerance summary. |

## Integration and operations

| Ch | Chapter | What it covers |
|---|---|---|
| 20 | [The RTU/SCADA interposer](<0020 The RTU and SCADA interposer.md>) | The interposer pipeline and protocols. |
| 22 | [Licensing: key format and trust model](<0022 Licensing - key format and trust model.md>) | Key layout, signature, trust model. |
| 31 | [Audit log and diagnostics](<0031 Audit log and diagnostics.md>) | `gridsim-audit/1` and its hash chain, SolveTrace, the forward-inference guard and its seams, provenance, restriction review. |
| 32 | [CIM/CGMES import and export](<0032 CIM and CGMES import and export.md>) | RDF/XML import, merge by mRID, the bridge to `GridModel` with three-winding star points, the exporter, the DL, GL, SC and DY profiles and the SHACL checker. |
| 33 | [C37.118 synchrophasor ingest](<0033 C37.118 synchrophasor ingest.md>) | Frame, CRC, CONFIG and DATA decoding, the phasor data concentrator, the measurement mapper, the linear PMU estimator and the `pmu-ingest` verb. |
| 34 | [Web host, REST/WebSocket, SSO and sessions](<0034 Web host, REST and WebSocket, SSO and sessions.md>) | `GridSim.Web`: start-up, routes, the REST status contract, the `/ws/sim` protocol, sessions and the replay-only gate. |
| 35 | [Deployment and security](<0035 Deployment and security.md>) | Docker, compose, nginx, the web host, SSO, sessions, CI and release workflows, assurance claims against the code. |
| 36 | [Known limitations and design decisions](<0036 Known limitations and design decisions.md>) | Behaviour kept on purpose after the October 2026 review, why it stays, and where it lives. |
| 37 | [Quasi-static time series and hosting capacity](<0037 Quasi-static time series and hosting capacity.md>) | The QSTS engine, its time model and controllers (OLTC, banks, IEEE 1547 inverters, storage), the OpenDSS cross-check, and the hosting-capacity bisection. |
| 38 | [Deterministic mathematics](<0038 Deterministic mathematics.md>) | Why platform maths is not bit-reproducible, the FDLIBM port (`DetMath`), the canonical NaN, the fixed-order SIMD kernels, the FMA policy, parallel loops, the frozen sort, the tolerances register, and how the IL analyser and the CI tier matrix enforce bit-identity across CPUs and operating systems. |
| 41 | [Scripting object model and API](<0041 Scripting object model and API.md>) | The Core object model (keys, attributes, immutable edits), `gridsim rpc --protocol 2`, the Python ModelSession and psspy layer, and the STUDIES extension of grammar 1. |
| 42 | [Run journal and breaking-change validation](<0042 Run journal and breaking-change validation.md>) | The run journal (entry schema, locking, rotation, snapshot store), `breaking-change validate` (child-process re-runs, the proof that the report does not depend on the parallelism, the volatile-field list and its census, bits and ulps), the breaking-change map and its explanation rule, and the security model. |
| 43 | [Long-term dynamics](<0043 Long-term dynamics.md>) | Quasi-steady-state simulation (one system frequency, AGC, tap changer timers, recovering loads), and the step schedule and error-controlled step of the full RMS model. |
| 44 | [Geomagnetically induced currents](<0044 Geomagnetically induced currents.md>) | GIC for a uniform field: the DC network, distances, effective GIC, reactive losses, and the check against the NERC guide's example. |
| 45 | [IEC 61850 SCL substation import](<0045 IEC 61850 SCL substation import.md>) | The substation section of an SCL file into the node-breaker model: what is mapped and refused, the params file, switch states, and safe XML reading. |
| 47 | [MCP server design](<0047 MCP server design.md>) | The MCP server over `gridsim rpc`: the tool contract (annotations, structured content, the evidence block), the verb allowlist and the no-case refusal, the `gridsim-mcp-record/1` hash chain and replay, build status, environment cleaning, telemetry and the network guard, determinism and its limits, tests. |
| 46 | [Determinism: specification and evidence](<0046 Determinism - specification and evidence.md>) | The determinism contract by axis (runs, threads, CPU tiers, FMA, operating system, culture, toolchain, versions), the mechanism and the test that pins each, the stated exceptions, and the author's platform observations. |

## Design records

These are the designs written before implementation. Each opens with a status block naming what was built and where;
the chapters above document current behaviour.

| Design | Current chapter |
|---|---|
| [C37.118 synchrophasor](design/c37118-synchrophasor.md) | 33 |
| [CIM/CGMES mapping](design/cim-cgmes-mapping.md) | 32 |
| [Robust GM estimation and leverage](design/robust-gm-leverage.md) | 6, 18 |
| [Three-phase distribution state estimation](design/three-phase-dsse.md) | 18 |
| [EMT time domain](design/emt-time-domain.md) | 21 |

The per-asset dynamics framework is documented in ch. 5 (the `pAssetFleet` term) and authored through
[user manual ch. 29](<../manual/0029 Extending GridSim.md>); ch. 13-15 cover its data grounding, validation and the
protections layer built on the same seam.
