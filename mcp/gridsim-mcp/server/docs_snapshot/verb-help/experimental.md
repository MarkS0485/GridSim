### experimental

`gridsim experimental [<subcommand> <case> [flags]]`

Opt-in experimental models, tagged unvalidated-at-scale and kept off the verified solve and estimate paths. With no
subcommand it lists them. `<case>` must come straight after the subcommand and accepts `<case|case.m|case-dir>`.

| Subcommand | Does |
|---|---|
| `reciprocity <case>` | Physical-consistency law Y = Y^T (law 11). |
| `wave <case>` | Continuum electromechanical-wave model: inter-area normal modes, dominant mode and its shape. Needs per-machine inertia: a case with no generators or a generator with no inertia constant (H not above 0, as MATPOWER data loads) is refused with exit 1 right after the power flow and before the classical reduction builds its network matrices (about 1.46 GB on gb-full). The dominant mode is the first mode after the rigid-body (zero) modes, one per island of machines (N282, October 2026; it was the first mode above 1e-6 Hz, which could pick the centre-of-inertia mode or an island's); an islanded case prints a `rigid-body (island) modes` line and its inter-area list starts after them. The modes are those of the symmetric part of the synchronising coupling (R035, October 2026: the transfer conductances make the coupling asymmetric, and the symmetric eigen-solver used to be handed the asymmetric matrix, which lifted the centre-of-inertia mode off zero); a `coupling asymmetry set aside` line gives the largest \|k_ij - k_ji\| in MW/rad, and the mean stiffness is now in MW/rad (it was the per-unit figure). |
| `modal-consistency <case>` | Small-signal structural residuals (COI null mode, law 16, and the K_ext row sum). |
| `harmonic-kcl <case>` | Harmonic KCL, Y(h) V(h) = I(h), on the study-model penetration with a synthetic six-pulse source at the largest-demand bus (law 12): the G5 Issue 5 Table B.3 six-pulse profile `6-pulse-g5`, named in the header line. Exit 1 on a violation or a singular network. |
| `passivity <case> [--bus ID]` | The grid driving-point impedance is positive-real over 5-2500 Hz in 5 Hz steps (law 14). `--bus` defaults to the largest generator's bus; exit 2 if there is no generator and no `--bus`, or the bus is not in the model; exit 1 on a violation. |
| `trace <case> [--losses] [--top 5]` | Bialek proportional-sharing trace: where each generator's power went, or with `--losses` which generators cause the network losses. |
| `carbon <case> [--top 8]` | Carbon tracing: the gCO2/kWh each load consumes, from traced generation. |
| `boundaries <case> [--top 6]` | Which generators supply each ETYS boundary's transfer. |

Exit 2 on a missing case or an unknown subcommand; exit 1 on a load error or a flow graph with a loop (`trace`,
`carbon`); exit 3 on a non-converged solve.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)).
