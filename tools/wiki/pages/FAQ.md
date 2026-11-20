# FAQ

## Is GridSim open source?

No. It is source-available, not open source: you can read the code, but GridSim requires a licence and
[LICENSE.md](https://github.com/MarkS0485/GridSim/blob/main/LICENSE.md) governs its use. Releases published before version 1.3.0 were under the
GNU GPL v3.0-or-later and remain available under that licence. The licence terms set out tiers measured on buses and
nothing else; with no licence installed, GridSim runs at the free Personal tier (every bundled network, and user networks
of up to 1,500 buses). The manual explains how it works in practice
([Licensing and activation](https://marks0485.github.io/GridSim/manual/0004%20Licensing%20and%20activation/)), and it all works offline.

## Does a licence cost anything?

Not at present. At present licences are issued gratis: no payment is required for any tier, and there is no present
intention to charge. Anyone who emails mark@gridsimdigitaltwin.co.uk for a licence today receives a Network Operator (DNO)
licence, which has no restrictions. That is not a promise that licences will always be gratis; GridSim ships with a
licence mechanism so that the terms can be applied if that is ever needed, for example to support its development.
The licence covers the software, not Mark's time: support, custom work or other commitments on your terms need a
separate agreement and compensation.

## What data does it use?

The built-in cases are embedded in the engine, the MATPOWER case libraries and the GB and starter-map JSON cases
ship in the `data/` folder, and there is no database or service to set up. The real GB network is built from published
data. The GDA data lake that some verbs read is never in the repository or in any download. See
[Loading networks](https://marks0485.github.io/GridSim/manual/0002%20Loading%20networks/).

## Does it predict the future?

No. GridSim reconstructs what a grid did from recorded public data and runs counterfactuals on that reconstruction.
It never predicts the future: the "no forward inference" rule is a hard invariant in the code. Data newer than 169 hours
is rejected at every dated ingestion point. See
[Provenance and invariants](https://marks0485.github.io/GridSim/technical/0008%20Provenance%20and%20invariants/).

## Are the results reproducible?

Yes. Results are double precision and deterministic to the bit across runs, parallel paths, CPU vector tiers and the
dense and sparse power-flow engines. The test suite pins exact values and is treated as constitutional.

## Do I need a GPU?

No. Everything runs on the CPU. A GPU backend is optional: `--gpu` asks for a device and falls back to the CPU when
none answers (a line on standard error says so). It supports ROCm, OpenCL and CUDA, and `gridsim gpucheck` reports
what your machine offers. GPU arithmetic is outside the bit-identity contract. See
[Benchmarking and GPU](https://marks0485.github.io/GridSim/manual/0028%20Benchmarking%20and%20GPU/).

## Which platforms are supported?

The command line tool has release builds for Windows x64, Linux x64 and macOS arm64 (Apple silicon). The WPF desktop
app is Windows only; the Avalonia desktop app has Windows and Linux builds, and Mac users build it from source (see
[Install](Install)).

## Is GridSim validated?

No. Nothing in GridSim is validated, at any scale. Its author is an HPC engineer, not an electrical engineer, and his
role is the code. The core solvers are cross-checked against independent open-source tools and published solutions,
which shows the same equations were solved the same way; it does not show that a model matches a real power system.
GridSim claims no accuracy, no parity with any commercial tool and no fitness for production use. Results you obtain,
whether they agree or not, are welcome at mark@gridsimdigitaltwin.co.uk.

## Do I need to install .NET?

Not for the release binaries, which are self-contained. Building from source needs the .NET 10 SDK.

## How do I report a bug?

Email mark@gridsimdigitaltwin.co.uk with the output of `gridsim --version`, your operating system, the exact command, the case
(or a small case that shows the problem), and what you expected. The manual lists exactly what to send under
[How to report a problem](https://marks0485.github.io/GridSim/manual/0030%20Troubleshooting/#how-to-report-a-problem).

## How do I report a security issue?

Do not use a public issue or a discussion. Report it privately through the repository's Security tab
([Report a vulnerability](https://github.com/MarkS0485/GridSim/security/advisories/new)), or email the security contact of record,
`mark@gridsimdigitaltwin.co.uk`. The [security policy](https://github.com/MarkS0485/GridSim/blob/main/SECURITY.md) says how a report is assessed
(CVSS base scores, recorded), the 48-hour remediation target for a base score of 7.0 or above, how fixes ship, and how
to receive security notices (watch the repository for Releases and Discussions). The
[security governance document](https://github.com/MarkS0485/GridSim/blob/main/docs/assurance/security-governance.md) covers ownership and review.

## How do I cite GridSim?

The licence (section 8) asks that published research produced with the Software cite it by name ("GridSim"), with the
version and the project URL (https://github.com/MarkS0485/GridSim). Get the version from `gridsim --version`. See
[LICENSE.md](https://github.com/MarkS0485/GridSim/blob/main/LICENSE.md) for the exact wording.

## Where do I ask questions?

Ask in [GitHub Discussions](https://github.com/MarkS0485/GridSim/discussions) (the Q&A category), or email mark@gridsimdigitaltwin.co.uk. Start with the
[user manual](https://marks0485.github.io/GridSim/manual/0000%20Contents/) and [Troubleshooting](Troubleshooting).

## Can I use it on my own network model?

Yes. Any MATPOWER file runs directly, a JSON case folder loads with `--dir`, and CIM/CGMES, PyPSA and other formats are
supported through import and export. See
[Exports and formats](https://marks0485.github.io/GridSim/manual/0031%20Exports%20and%20formats/).
