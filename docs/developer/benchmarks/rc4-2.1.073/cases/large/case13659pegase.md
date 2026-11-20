# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `6b4f7fec7a509db8291b0e3b2acefa0b164fdfc595085af9eda9634be65271dd`.
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
| veragrid | 10/10 | 1105.715944 | 1088.968278 | 1123.953726 | 10/10 |
| powsybl | 10/10 | 1670.566944 | 1390.494225 | 2159.636316 | 10/10 |
| andes | 10/10 | 505.178796 | 488.144790 | 513.506998 | 0/10 |
| powermodels | 0/10 | - | - | - | 0/0 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | Unavailable | Unavailable |
| gpu-sparse | Unavailable | Unavailable |
| matpower | Unavailable | Unavailable |
| pandapower | Unavailable | Unavailable |
| pypsa | Unavailable | Unavailable |
| veragrid | 3.3069227e-11 | 2.220446e-16 |
| powsybl | 1.7486173e-11 | 0 |
| andes | 0.0013289053 | 0 |
| powermodels | Unavailable | Unavailable |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 4.7664344e-11 pu.

## GridSim bitwise checks

| Check | Compared | Scalar differences |
| --- | --- | ---: |
| sparse repeats | 0/10 solves | Unavailable |
| gpu-sparse repeats | 0/10 solves | Unavailable |
| Dense vs Sparse | First converged solve on each path | Unavailable |

## GridSim physics audits

| Path | Attempt | Export | Audit | Exit code |
| --- | ---: | --- | --- | ---: |
