# GridSim

GridSim is a physics-first power-grid simulator written in C# on .NET 10. It starts from a small core (a per-unit
network model, the complex bus admittance matrix and a Newton-Raphson load flow) and grows it, with every step
tested, into a broad grid stack: power flow and optimal power flow, contingency and security screening, state
estimation, frequency and transient dynamics, faults and protection, harmonics, battery storage, and interfaces to
the common grid file formats.

**Nothing in GridSim is validated, at any scale.** It is written by Mark, an HPC engineer by trade with no
electrical-engineering training; his role is the code. GridSim claims no accuracy, no parity with any commercial tool
and no fitness for production use. Use it, compare it with what you trust, and send results and bug reports to
mark@gridsimdigitaltwin.co.uk.

Every numerical result is double precision end to end and deterministic to the bit, and the core solvers are
cross-checked against independent open-source tools.

The latest release is [2.1.073 RC-4](https://github.com/MarkS0485/GridSim/releases/tag/v2.1.073-RC-4), with ready-built Windows, Linux and
macOS (Apple silicon) binaries. The full documentation lives on the project site; this wiki is only a short front
door.

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
- [Cross-checks](https://marks0485.github.io/GridSim/technical/0011%20Validation/): agreement with independent tools; the event dossiers are
  under Validation on the site
- [Developer](https://marks0485.github.io/GridSim/developer/case-libraries/): case libraries and developer notes

## Elsewhere

- [Source repository](https://github.com/MarkS0485/GridSim)
- [Releases](https://github.com/MarkS0485/GridSim/releases)
- [Changelog](https://github.com/MarkS0485/GridSim/blob/main/CHANGELOG.md)
