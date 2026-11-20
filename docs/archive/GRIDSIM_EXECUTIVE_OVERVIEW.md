# GridSim: Executive and Product Overview

> **Archived assessment, not maintained.** This report describes GridSim 2.1.030 as inspected on 7 November 2026 and is
> kept as a record. Where it differs from the [project README](../../README.md), the README is current. Nothing in GridSim
> is validated, at any scale, and no buyer-facing framing here is current. For licensing, [LICENSE.md](../../LICENSE.md)
> governs, not this report.

**A programmable power-system study platform for engineers, researchers and software integrators**

**Assessment date:** 7 November 2026  
**Source baseline:** `main`, commit `b0ff2fc47f46df9cc5724f5b336c858651fa9527`, source version `2.1.030`.

## Contents

1. [What GridSim delivers](#1-what-gridsim-delivers)
2. [A library and a working study environment](#2-a-library-and-a-working-study-environment)
3. [Capabilities and benefits](#3-capabilities-and-benefits)
4. [Practical applications](#4-practical-applications)
5. [Engineering differentiation](#5-engineering-differentiation)
6. [Evidence and product maturity](#6-evidence-and-product-maturity)
7. [Adoption and commercial model](#7-adoption-and-commercial-model)
8. [Product summary](#8-product-summary)

## 1. What GridSim delivers

GridSim combines an electrical-network model, a substantial numerical library and a command-line study environment. It helps users move from a supported network case to calculated voltages, flows, losses and engineering assessments, then retain results in forms suitable for review and automation.

The platform extends beyond a single power-flow calculation. Its implementation includes contingency analysis, state estimation, frequency response, machine dynamics, harmonics, fault duties, economic dispatch, time-series studies and battery modelling. A common model and shared numerical infrastructure connect these capabilities, allowing developers and analysts to build repeatable workflows around the same software.

The product's value lies in making electrical-system experiments programmable and inspectable. Users can see which assumptions produced a result, distinguish convergence from failure and export detailed results without manually transcribing terminal tables. These are implemented mechanisms; the investigation does not claim measured productivity gains, market uniqueness or independently certified operational readiness. [Domain model](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Model/Model.cs), [power flow](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/PowerFlow/SparseNewtonRaphsonPowerFlow.cs), [CLI result writer](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Cli/CliResultWriter.cs).

## 2. A library and a working study environment

**GridSim.Core** is the reusable .NET 10 engine. It contains network records, numerical solvers, study services, file formats, diagnostics and evidence handling. Its solver interfaces allow alternative numerical implementations to be composed with the study logic. Core has no explicit third-party runtime package references, although it uses the .NET runtime and SDK build tooling.

**GridSim.Cli**, distributed as `gridsim`, makes those capabilities accessible through commands. It manages arguments, model loading, backend selection, output and process outcomes. Users can work with built-in reference cases or supported external cases, and automation can consume JSON or CSV from the commands that implement the shared result contract.

This combination serves two adoption paths: embed selected Core functionality in an application, or use the executable as a study tool and integration boundary. Supporting desktop, web and editor projects demonstrate additional consumers of the platform. [Core project](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/GridSim.Core.csproj), [CLI project](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Cli/GridSim.Cli.csproj), [solution](../../GridSim.slnx).

## 3. Capabilities and benefits

The capabilities below have concrete source implementations. They were not all executed or independently validated during this assessment; the detailed report records that distinction.

| Capability | What users gain | Important boundary |
|---|---|---|
| AC and radial power flow | Calculated bus voltages, phase angles, branch flows and losses | Convergence and control settlement must be checked separately. |
| Contingency studies | A repeatable way to examine selected outages and identify limit violations | Candidate screens are not automatically exhaustive security assessments. |
| State estimation | Methods for reconstructing network state from redundant observations, with observability and residual diagnostics | Data quality, uncertainty and method-specific size limits matter. |
| Frequency and dynamic studies | Conditional RoCoF, nadir, machine and stability experiments | Default parameters and reduced models are not universal field calibrations. |
| Harmonics and fault studies | Power-quality propagation and supported short-circuit/equipment-duty calculations | Credibility depends on spectra, grounding, sequence and equipment data. |
| Optimisation | Dispatch, commitment and security-constrained study functions | DC, loss-loop and full AC optimisation solve different mathematical problems. |
| QSTS and hosting capacity | Profile-driven assessment of voltage/loading exposure, controls and additional resource capacity | A capacity ceiling is limited to the selected profiles, criteria and search range. |
| Battery and asset models | Stateful experiments with power delivery, energy, controls and thermal/ageing assumptions | Requested output can differ from feasible delivered output. |
| Recorded-event evidence | Baseline reproduction checks before conditional counterfactual studies | Failed or marginal baselines can block a sweep. |
| Structured results and audit | Repeatable files, explicit exits, provenance and optional chained/signed records | Audit controls do not themselves establish external certification. |

Implementation: [contingencies](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Analysis/ContingencyAnalysis.cs), [estimation](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Estimation/WlsStateEstimator.cs), [frequency](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Dynamics/FrequencyDynamics.cs), [harmonics](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Harmonics/HarmonicPenetration.cs), [fault duty](../../src/GridSim.Core/Protection/ShortCircuit/Iec60909.cs), [full AC OPF](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Analysis/AcOpfInteriorPoint.cs), [QSTS](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Qsts/QstsEngine.cs), [hosting](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Qsts/HostingCapacity.cs), [storage](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Storage/PackModel.cs), [replay](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Replay/ReplayValidator.cs), [audit](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Diagnostics/AuditRunRecorder.cs).

## 4. Practical applications

### 4.1 Engineering analysis and repeatable studies

A team can load a supported case, solve its operating point, inspect voltage and loading, run selected outages and export the full results. Command lines and versioned inputs provide a repeatable record of the experiment. Structured files reduce transcription and support further analysis in external tools. The result remains conditional on the supplied network and its accepted modelling assumptions. [CLI reference](<../manual/0019 CLI reference.md>).

### 4.2 Research and teaching

Built-in IEEE/CIGRE reference cases, synthetic measurements and replaceable solver interfaces provide useful material for teaching and controlled numerical experiments. Researchers can compare estimator methods, change measurement coverage or study dynamic assumptions while retaining clear separation between synthetic truth and independent evidence. This is a plausible use of implemented facilities, not a claim of academic adoption already demonstrated. [Reference cases](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/IO/IeeeCases.cs), [synthetic measurements](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Estimation/SyntheticMeasurements.cs).

### 4.3 Distribution and storage experiments

Time-series and hosting-capacity studies can investigate additional PV or load against chosen voltage, thermal and reverse-flow criteria. Storage policies and models allow conditional comparisons of charging, discharging and response behaviour. These tools can support research and internal assessments, with validation appropriate to any consequential application. [Hosting](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Qsts/HostingCapacity.cs), [storage dispatch](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Storage/StorageDispatch.cs).

### 4.4 Historical-event assessment

GridSim can replay recorded disturbances and assess whether a baseline model reproduces selected observed metrics. Only a reproduced baseline unlocks the principal scenario sweep. That mechanism makes it possible to present alternative assumptions without disguising them as observed outcomes. Dated external inputs are subject to the implemented historical-data boundary, generally requiring a lag of at least 169 hours. GridSim should therefore be positioned around historical reconstruction and hypothetical studies, not unrestricted current-grid prediction. [Replay validation](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Replay/ReplayValidator.cs), [scenario gate](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Scenarios/ScenarioEngine.cs), [historical guard](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Diagnostics/ForwardInferenceGuard.cs).

### 4.5 Software integration

A licensed developer can embed the Core interfaces in an application or invoke the CLI from a surrounding workflow. This offers reuse of the same numerical implementation across a custom interface, a batch job and a test harness. Integration must preserve units, convergence checks, instance ownership, licence rules and provenance. [Solver interface](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Abstractions/IPowerFlowSolver.cs), [licence](../../LICENSE.md).

## 5. Engineering differentiation

GridSim's strongest differentiators are visible in implementation:

- **Shared numerical paths.** Dense and sparse CPU Newton engines share canonical solution/update routines to support bit-identical results. GPU paths have a separately qualified floating-point contract.
- **Explicit outcomes.** Numerical status, control settlement, observability and study verdicts are represented rather than hidden behind a successful-looking table.
- **Inspectable evidence.** Source, versioned files, provenance tags and replay gates support review of how conclusions were obtained.
- **A reusable .NET foundation.** Study capability is available through library interfaces as well as the executable.
- **Automation with detail.** Supported result-writing commands retain complete tables and numeric precision even when terminal views show only leading rows.
- **Breadth around a common model.** Related studies can use the same network representation while retaining their own mathematical assumptions.

These are architectural strengths, not claims that competing software lacks similar features. [Shared Newton kernel](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/PowerFlow/NewtonShell.cs), [results](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/PowerFlow/PowerFlowResult.cs), [provenance](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Diagnostics/ProvenanceTrust.cs), [result files](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Cli/CliResultWriter.cs).

## 6. Evidence and product maturity

The repository contains extensive numerical and integration tests, source-contract checks, reference cases, detailed manuals and release workflows. The code also identifies unsupported modes and experimental models. That combination demonstrates substantial engineering investment and supports a capability-specific evaluation.

For this assessment, the CLI Release build completed with zero warnings and errors, and the rebuilt executable reported source version `2.1.030` and the inspected commit. A case14 power flow, its physical-law audit and a case9 frequency trip ran successfully. The first test attempt aborted on test-host connection timeout, and a subsequent test retry was stopped following the laptop working restrictions. These smoke checks do not establish a complete test-suite pass or validate every advertised study family. The checkout also contained pre-existing fault-analysis changes, which were preserved. The [technical verification ledger](GRIDSIM_TECHNICAL_REFERENCE.md#2-verification-ledger) gives the exact scope.

A buyer should ask which study families are supported for its chosen network, which parameters are independently calibrated, which reference comparisons pass, and what support/deployment commitments accompany the licence. Experimental or unvalidated-at-scale functions should remain labelled accordingly. No independent certification, customer uptake, service-level commitment or performance benchmark was established by this investigation.

## 7. Adoption and commercial model

The inspected repository licence describes GridSim as source-available proprietary software. Personal non-commercial and academic tiers have stated limits for non-bundled networks; commercial code, deployment and network-operator arrangements are available by application. The implementation includes offline signed entitlements, bundled-network classification and major-version matching. Public source access must not be confused with unrestricted redistribution or commercial hosting rights. [Licence terms](../../LICENSE.md), [licence enforcement](https://github.com/MarkS0485/GridSim/blob/b0ff2fc47f46df9cc5724f5b336c858651fa9527/src/GridSim.Core/Licensing/LicenceGate.cs).

A proportionate adoption route is to select one study family, verify a known reference case, establish independent agreement where possible, then assess a validated internal case and retain its inputs and outcomes. Organisations can expand scope as evidence accumulates. This creates a concrete basis for technical and commercial discussions without requiring blanket acceptance of every subsystem.

## 8. Product summary

> GridSim brings programmable power-system analysis into a reusable .NET engine and comprehensive command-line environment. It combines network calculations, study automation and structured evidence so engineers can calculate, compare and explain system behaviour with transparent assumptions and explicit outcomes.

For the full architecture, algorithms, limitations and engineering assessment, read the [comprehensive report](GRIDSIM_COMPREHENSIVE_REPORT.md). For class/API inventories and all command options, read the [technical reference](GRIDSIM_TECHNICAL_REFERENCE.md).
