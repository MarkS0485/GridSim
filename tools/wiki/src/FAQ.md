# FAQ

## Is GridSim open source?

No. It is source-available, proprietary software: you can read the code, but [LICENSE.md]({{REPO}}/blob/main/LICENSE.md)
governs its use. Releases published before version 1.3.0 were under the GNU GPL v3.0-or-later and remain available
under that licence. For what you may do with the current version, read the licence itself. Licensing is measured
on buses and nothing else; the manual explains how it works in practice
([Licensing and activation]({{SITE}}manual/0004%20Licensing%20and%20activation/)). It works offline and, with no licence
installed, GridSim runs at the free Personal tier.

## What data does it use?

The built-in cases are embedded in the engine, the MATPOWER case libraries and the GB and starter-map JSON cases
ship in the `data/` folder, and there is no database or service to set up. The real GB network is built from published
data. The GDA data lake that some verbs read is never in the repository or in any download. See
[Loading networks]({{SITE}}manual/0002%20Loading%20networks/).

## Does it predict the future?

No. GridSim reconstructs what a grid did from recorded public data and runs counterfactuals on that reconstruction.
It never predicts the future: the "no forward inference" rule is a hard invariant in the code. Data newer than 169 hours
is rejected at every dated ingestion point. See
[Provenance and invariants]({{SITE}}technical/0008%20Provenance%20and%20invariants/).

## Are the results reproducible?

Yes. Results are double precision and deterministic to the bit across runs, parallel paths, CPU vector tiers and the
dense and sparse power-flow engines. The test suite pins exact values and is treated as constitutional.

## Do I need a GPU?

No. Everything runs on the CPU. A GPU backend is optional: `--gpu` asks for a device and falls back to the CPU when
none answers (a line on standard error says so). It supports ROCm, OpenCL and CUDA, and `gridsim gpucheck` reports
what your machine offers. GPU arithmetic is outside the bit-identity contract. See
[Benchmarking and GPU]({{SITE}}manual/0028%20Benchmarking%20and%20GPU/).

## Which platforms are supported?

Release builds exist for Windows x64 and Linux x64. There is no macOS build in RC-1; Mac users build from source (see
[Install](Install)). The WPF desktop app is Windows only; the Avalonia desktop app has Windows and Linux builds.

## Do I need to install .NET?

Not for the release binaries, which are self-contained. Building from source needs the .NET 10 SDK.

## How do I report a bug?

Email gdatooling@proton.me with the output of `gridsim --version`, your operating system, the exact command, the case
(or a small case that shows the problem), and what you expected. The manual lists exactly what to send under
[How to report a problem]({{SITE}}manual/0030%20Troubleshooting/#how-to-report-a-problem).

## How do I report a security issue?

Do not use a public issue or a discussion. Report it privately through the repository's Security tab
([Report a vulnerability]({{REPO}}/security/advisories/new)), or email the security contact of record,
`gdatooling@proton.me`. The [security policy]({{REPO}}/blob/main/SECURITY.md) says how a report is assessed
(CVSS base scores, recorded), the 48-hour remediation target for a base score of 7.0 or above, how fixes ship, and how
to receive security notices (watch the repository for Releases and Discussions). The
[security governance document]({{REPO}}/blob/main/docs/assurance/security-governance.md) covers ownership and review.

## How do I cite GridSim?

The licence (section 8) asks that published research produced with the Software cite it by name ("GridSim"), with the
version and the project URL ({{REPO}}). Get the version from `gridsim --version`. See
[LICENSE.md]({{REPO}}/blob/main/LICENSE.md) for the exact wording.

## Where do I ask questions?

Ask in [GitHub Discussions]({{REPO}}/discussions) (the Q&A category), or email gdatooling@proton.me. Start with the
[user manual]({{SITE}}manual/0000%20Contents/) and [Troubleshooting](Troubleshooting).

## Can I use it on my own network model?

Yes. Any MATPOWER file runs directly, a JSON case folder loads with `--dir`, and CIM/CGMES, PyPSA and other formats are
supported through import and export. See
[Exports and formats]({{SITE}}manual/0031%20Exports%20and%20formats/).
