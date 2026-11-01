# GridSim

GridSim is a grounded, physics-first power-grid simulator written in C# on .NET 10. It starts from a small, correct
core (a per-unit network model, the complex bus admittance matrix and a Newton-Raphson load flow) and grows it, with
every step validated, into a full grid stack: power flow and optimal power flow, contingency and security screening,
state estimation, frequency and transient dynamics, faults and protection, harmonics, battery storage, and interfaces
to the common grid file formats.

Every numerical result is double precision end to end and deterministic to the bit, and is cross-checked against
independent tools. GridSim is also the core engine of an evidence apparatus: it reconstructs what the real GB grid
did from public data, offline, and runs grounded counterfactuals on that reconstruction. It never predicts the
future; that "no forward inference" rule is a hard invariant in the code, not a policy.

The current release is [2.1.000 RC-1](https://github.com/MarkS0485/GridSim/releases/tag/v2.1.000-RC-1), with ready-built Windows and Linux binaries. The full
documentation lives on the project site; this wiki is only a short front door.

**Licence:** source-available, proprietary software; see [LICENSE.md](https://github.com/MarkS0485/GridSim/blob/main/LICENSE.md).

## In this wiki

- [Install](Install): release binaries for Windows and Linux, or build from source
- [Getting started](Getting-started): your first solve
- [CLI quick reference](CLI-quick-reference): every `gridsim` verb on one page
- [FAQ](FAQ)
- [Troubleshooting](Troubleshooting)

## On the project site

- [Site home](https://marks0485.github.io/GridSim/)
- [User manual](https://marks0485.github.io/GridSim/manual/0000%20Contents/): installing, loading networks, every study, the apps, the CLI
- [Technical reference](https://marks0485.github.io/GridSim/technical/0000%20Contents/): architecture, data model, solvers and their equations
- [Validation](https://marks0485.github.io/GridSim/validation/crossval-results/): cross-checks against independent tools and the event dossiers
- [Developer](https://marks0485.github.io/GridSim/developer/case-libraries/): case libraries and developer notes

## Elsewhere

- [Source repository](https://github.com/MarkS0485/GridSim)
- [Releases](https://github.com/MarkS0485/GridSim/releases)
- [Changelog](https://github.com/MarkS0485/GridSim/blob/main/CHANGELOG.md)
