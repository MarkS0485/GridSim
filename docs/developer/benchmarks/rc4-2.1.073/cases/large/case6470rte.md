# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `bf41270b1064fd72b3b059c12d66f82fc766e6960c9c70781b09be50d2216bec`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 0/10 | - | - | - | 0/0 |
| gpu-sparse | 0/10 | - | - | - | 0/0 |
| matpower | 0/10 | - | - | - | 0/0 |
| pandapower | 0/10 | - | - | - | 0/0 |
| pypsa | 0/10 | - | - | - | 0/0 |
| veragrid | 0/10 | - | - | - | 0/0 |
| powsybl | 0/10 | - | - | - | 0/0 |
| andes | 0/10 | - | - | - | 0/0 |
| powermodels | 0/10 | - | - | - | 0/0 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | Unavailable | Unavailable |
| gpu-sparse | Unavailable | Unavailable |
| matpower | Unavailable | Unavailable |
| pandapower | Unavailable | Unavailable |
| pypsa | Unavailable | Unavailable |
| veragrid | Unavailable | Unavailable |
| powsybl | Unavailable | Unavailable |
| andes | Unavailable | Unavailable |
| powermodels | Unavailable | Unavailable |

ANDES adds 1e-8 pu to line resistance and reactance.

## GridSim bitwise checks

| Check | Compared | Scalar differences |
| --- | --- | ---: |
| sparse repeats | 0/10 solves | Unavailable |
| gpu-sparse repeats | 0/10 solves | Unavailable |
| Dense vs Sparse | First converged solve on each path | Unavailable |

## GridSim physics audits

| Path | Attempt | Export | Audit | Exit code |
| --- | ---: | --- | --- | ---: |
