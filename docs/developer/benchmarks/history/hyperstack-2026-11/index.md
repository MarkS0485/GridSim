# AC power-flow suite on cloud servers, November 2026

*Status: historical. Superseded by the [RC4 (2.1.073) results](../../rc4-2.1.073/index.md). Back to the [benchmarks landing page](../../index.md).*

Pages in this run:

- Box 1: [VM note](box1-norway/VIRTUAL_MACHINE-NOTE.md)
- Box 2: [summary](box2-canada/SUMMARY.md), [table](box2-canada/table.md), [VM note](box2-canada/VIRTUAL_MACHINE.md), [operator log](box2-canada/OPERATOR-LOG.md)
- Box 3: [summary](box3-canada-fixes/SUMMARY.md), [scorecard](box3-canada-fixes/scorecard.md), [VM note](box3-canada-fixes/VIRTUAL_MACHINE.md), [operator log](box3-canada-fixes/OPERATOR-LOG.md); before and after: [focused](box3-canada-fixes/analysis-focused/before-after.md), [full](box3-canada-fixes/analysis-full/before-after.md), [against box 2](box3-canada-fixes/analysis-vs-box2/before-after.md)
- Chart packs: [journey](charts/journey/INDEX.md), [RC-3](charts/pack-2026-11-18-rc3/INDEX.md), [fixes](charts/pack-2026-11-18-fixes/INDEX.md)

The 67-case MATPOWER 8.1 suite was run with the public GridSim-Harness (commit ae2f53a) on three rented KVM servers.
Each solver path ran one at a time, pinned to one core. The driver was a Release build with tiered JIT off. Times are
solve time only, the mean of 10 timed solves from a flat start. A time counts only if all 10 solves converged and the
maximum residual is at or below 1e-8 pu. A result within 10% of the fastest other solver counts as a tie.

| Folder | Server | GridSim code | Result |
| --- | --- | --- | --- |
| `box1-norway` | AMD EPYC 7452 (Zen 2), 4 vCPU | 2.1.071 RC-3 | 54 fastest, 3 ties, 5 slower, 5 solved by no solver |
| `box2-canada` | AMD EPYC 7763 (Zen 3), 28 vCPU | 2.1.071 RC-3 | 54 fastest, 3 ties, 5 slower, 5 solved by no solver |
| `box3-canada-fixes` | AMD EPYC 7763 (Zen 3), 28 vCPU | RC-3 plus the minimum-degree ordering and Vm floor change | 62 fastest, 0 ties, 0 slower, 5 solved by no solver |

Box 3 compares the old and new GridSim code on the same server, alternating runs. For the 16 focused cases it also
timed the other solvers on that server. For the other 51 cases, the other solvers' times come from box 2, which uses
the same CPU class. `box3-canada-fixes/SUMMARY.md` has the details, including the Linux determinism test results.

The ordering and Vm floor change is a BREAKING change to the numerical contract. Results differ from 2.1.071 at
rounding level: at most 9.7e-13 pu in voltage magnitude and 1.9e-11 degrees in angle across 66 cases, with the same
convergence and iteration counts. See CHANGELOG.md.

Charts:

- `charts/pack-2026-11-18-rc3`: RC-3 results (box 2)
- `charts/pack-2026-11-18-fixes`: results with the fixes (box 3)
- `charts/journey`: laptop to box 1, box 1 to box 2, and box 2 to the fixes

Each folder has an `INDEX.md` that gives the numbers behind every chart. The raw per-case logs, about 0.8 to 2 GB per
server, are kept offline.
