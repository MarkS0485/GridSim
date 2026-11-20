# Free AC power-flow comparison, 1,354-bus PEGASE

*Status: historical. Measured on a Debug build of the GridSim Core source of the 2.1.031 RC-2 snapshot, 15 November 2026. Superseded by the [RC4 (2.1.073) results](../../rc4-2.1.073/index.md). Back to the [benchmarks landing page](../../index.md).*

The [expanded suite rehearsal](../suite-rehearsal/index.md) covers the additional MATPOWER cases run on the busy laptop.

These results record an unattended run of the listed builds and settings.
Each path made ten fresh attempts after one warm-up. The timed interval
covers the solve API. Input loading, exports and audits are outside that
interval. MATPOWER is the numerical comparison point.

The input is [MATPOWER's 1,354-bus PEGASE case](https://github.com/MATPOWER/matpower/blob/8.1/data/case1354pegase.m).
Data citation: C. Josz, S. Fliscounakis, J. Maeght and P. Panciatici,
*AC Power Flow Data in MATPOWER and QCQP Format: iTesla, RTE Snapshots,
and PEGASE*.

Run type: **measurement**. The host was an Intel Core i3-N305 with eight
logical CPUs, Windows 11 and the Balanced power plan. The GridSim Core source
tree matches [the public 2.1.031 RC-2 snapshot](https://github.com/MarkS0485/GridSim/tree/8301e8c514c33a141fc5024c8d61c201dcfc6659/src/GridSim.Core).
The measured binary was a Debug build.

Both GridSim paths passed the [pre-run correctness and bitwise criteria](../../criteria.md).
Sparse took 0.233 times the MATPOWER mean and 0.959 times the pandapower mean.
The Sparse and pandapower timing ranges overlap, so their small gap is
inconclusive from this run.

The evidence archive normalises user-profile paths in logs and the manifest. The published run manifest writes the
local folders as `<repo>` (the GridSim checkout) and `<benchmarks-dir>` (the benchmark working folder).
The numerical records and audit exports retain their original bytes.

Case SHA-256: `1b08b25a2f6c1d540d090009dfaff41ff2b05784a2d8d302a7ad695821557b89`.
GridSim commit: `240edcc92410faef602d27cae51b7d5fb9fec68d`.
GridSim build configuration: Debug.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| dense | 10/10 | 3302.634850 | 3142.675600 | 3605.294300 | 10/10 |
| sparse | 10/10 | 52.763550 | 50.195500 | 56.282900 | 10/10 |
| matpower | 10/10 | 226.806951 | 219.192028 | 238.404036 | 10/10 |
| pandapower | 10/10 | 55.026380 | 53.058700 | 57.851800 | 10/10 |
| pypsa | 10/10 | 1276.602800 | 1247.565100 | 1356.039700 | 10/10 |
| veragrid | 10/10 | 96.143060 | 92.854400 | 100.369300 | 10/10 |
| powsybl | 10/10 | 143.460420 | 70.497800 | 216.861700 | 10/10 |
| andes | 10/10 | 31.594880 | 30.766800 | 33.080100 | 0/10 |
| powermodels | 10/10 | 82.178510 | 69.588100 | 127.908700 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| dense | 5.7072569e-12 | 0 |
| sparse | 5.7072569e-12 | 0 |
| matpower | 2.830518e-12 | 2.220446e-16 |
| pandapower | 6.2870242e-12 | 6.6613381e-16 |
| pypsa | 1.0863346e-11 | 0 |
| veragrid | 5.1194604e-12 | 2.220446e-16 |
| powsybl | 2.1670843e-12 | 0 |
| andes | 0.0012603177 | 8.0602192e-14 |
| powermodels | 4.825722e-12 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 4.8378013e-12 pu.

## GridSim bitwise checks

| Check | Compared | Scalar differences |
| --- | --- | ---: |
| dense repeats | 10/10 solves | 0 |
| sparse repeats | 10/10 solves | 0 |
| Dense vs Sparse | First converged solve on each path | 0 |

## GridSim physics audits

| Path | Attempt | Export | Audit | Exit code |
| --- | ---: | --- | --- | ---: |
| dense | 1 | [JSON](evidence/dense/export-01.json) | [JSON](evidence/dense/audit-01.json) | 0 |
| sparse | 1 | [JSON](evidence/sparse/export-01.json) | [JSON](evidence/sparse/audit-01.json) | 0 |

## Differences from MATPOWER

Branch flows here are reconstructed on the common source case from each solver's voltages.
Each entry gives the largest absolute difference and its source bus or one-based branch row.

| Path | Quantity | Difference | Bus or branch |
| --- | --- | ---: | ---: |
| dense | voltagePu | 4.0856207e-14 | 5131 |
| dense | angleDeg | 1.1812773e-12 | 4402 |
| dense | fromMw | 1.5310353e-10 | 1206 |
| dense | fromMvar | 2.6690267e-10 | 634 |
| dense | toMw | 1.5310353e-10 | 1206 |
| dense | toMvar | 2.66903e-10 | 634 |
| sparse | voltagePu | 4.0856207e-14 | 5131 |
| sparse | angleDeg | 1.1812773e-12 | 4402 |
| sparse | fromMw | 1.5310353e-10 | 1206 |
| sparse | fromMvar | 2.6690267e-10 | 634 |
| sparse | toMw | 1.5310353e-10 | 1206 |
| sparse | toMvar | 2.66903e-10 | 634 |
| pandapower | voltagePu | 2.3092639e-14 | 4402 |
| pandapower | angleDeg | 2.5970337e-12 | 7885 |
| pandapower | fromMw | 2.3912028e-10 | 1206 |
| pandapower | fromMvar | 5.6147087e-10 | 353 |
| pandapower | toMw | 2.3911961e-10 | 1206 |
| pandapower | toMvar | 5.61446e-10 | 353 |
| pypsa | voltagePu | 3.7747583e-14 | 903 |
| pypsa | angleDeg | 2.1493918e-12 | 1584 |
| pypsa | fromMw | 1.9628821e-10 | 1206 |
| pypsa | fromMvar | 3.9148063e-10 | 600 |
| pypsa | toMw | 1.9628799e-10 | 1206 |
| pypsa | toMvar | 3.9102588e-10 | 600 |
| veragrid | voltagePu | 8.7485574e-14 | 4885 |
| veragrid | angleDeg | 3.6664005e-12 | 4355 |
| veragrid | fromMw | 1.8442847e-10 | 1205 |
| veragrid | fromMvar | 4.1262638e-10 | 601 |
| veragrid | toMw | 1.8454216e-10 | 1205 |
| veragrid | toMvar | 4.1214321e-10 | 601 |
| powsybl | voltagePu | 2.0428104e-14 | 3817 |
| powsybl | angleDeg | 4.6718185e-12 | 903 |
| powsybl | fromMw | 1.1846979e-10 | 295 |
| powsybl | fromMvar | 2.2149038e-10 | 600 |
| powsybl | toMw | 1.184699e-10 | 295 |
| powsybl | toMvar | 2.2137669e-10 | 600 |
| andes | voltagePu | 4.2594803e-07 | 747 |
| andes | angleDeg | 0.00013275033 | 8748 |
| andes | fromMw | 0.061457706 | 600 |
| andes | fromMvar | 0.067625924 | 600 |
| andes | toMw | 0.061441078 | 600 |
| andes | toMvar | 0.067276731 | 600 |
| powermodels | voltagePu | 5.2624571e-14 | 4402 |
| powermodels | angleDeg | 2.5828228e-12 | 401 |
| powermodels | fromMw | 1.9623403e-10 | 1206 |
| powermodels | fromMvar | 4.2132342e-10 | 601 |
| powermodels | toMw | 1.962337e-10 | 1206 |
| powermodels | toMvar | 4.2109605e-10 | 601 |

## Reproduce this run

See the [benchmark harness and runtime instructions](https://github.com/MarkS0485/GridSim/tree/main/benchmarks/free-ac-power-flow). The run manifest records the dependency fingerprints and exact command arguments.

Download the [run manifest](evidence/manifest.json) or download the
[full evidence archive](evidence/measurement.zip).

## Report a discrepancy

I ran this benchmark to give people something concrete to check. Please
share reproduced timings, convergence failures, numerical differences,
bugs or concerns about the method. Include the case hash, command, tool
build, hardware and relevant output.

[Report a discrepancy](https://github.com/MarkS0485/GridSim/issues/new)
