# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `5558db635caab385d89c3d10afa10b94483f7e908f11cb73da53aa74420e6359`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 0/10 | - | - | - | 0/0 |
| gpu-sparse | 0/10 | - | - | - | 0/0 |
| matpower | 10/10 | 569.985819 | 562.780142 | 582.129955 | 10/10 |
| pandapower | 10/10 | 213.751503 | 208.684826 | 219.201076 | 0/10 |
| pypsa | 10/10 | 14240.092820 | 13951.970702 | 14565.007893 | 0/10 |
| veragrid | 10/10 | 593.485711 | 575.202805 | 639.873541 | 10/10 |
| powsybl | 0/10 | - | - | - | 0/0 |
| andes | 10/10 | 182.935631 | 172.783986 | 190.287056 | 0/10 |
| powermodels | 10/10 | 541.640841 | 460.656226 | 673.928271 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | Unavailable | Unavailable |
| gpu-sparse | Unavailable | Unavailable |
| matpower | 1.2971668e-12 | 1.110223e-16 |
| pandapower | 0.62383033 | 4.4408921e-16 |
| pypsa | 0.62383033 | 0 |
| veragrid | 2.3371172e-12 | 0 |
| powsybl | Unavailable | Unavailable |
| andes | 0.001241446 | 6.6613381e-16 |
| powermodels | 1.5127455e-12 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 1.6169954e-12 pu.

## GridSim bitwise checks

| Check | Compared | Scalar differences |
| --- | --- | ---: |
| sparse repeats | 0/10 solves | Unavailable |
| gpu-sparse repeats | 0/10 solves | Unavailable |
| Dense vs Sparse | First converged solve on each path | Unavailable |

## GridSim physics audits

| Path | Attempt | Export | Audit | Exit code |
| --- | ---: | --- | --- | ---: |

## Differences from MATPOWER

Branch flows here are reconstructed on the common source case from each solver's voltages.
Each entry gives the largest absolute difference and its source bus or one-based branch row.

| Path | Quantity | Difference | Bus or branch |
| --- | --- | ---: | ---: |
| pandapower | voltagePu | 0.00016868056 | 50491 |
| pandapower | angleDeg | 0.091838302 | 92806 |
| pandapower | fromMw | 58.514791 | 803 |
| pandapower | fromMvar | 23.390689 | 803 |
| pandapower | toMw | 58.537537 | 803 |
| pandapower | toMvar | 23.140133 | 803 |
| pypsa | voltagePu | 0.00016868056 | 50491 |
| pypsa | angleDeg | 0.091838298 | 92806 |
| pypsa | fromMw | 58.514791 | 803 |
| pypsa | fromMvar | 23.390689 | 803 |
| pypsa | toMw | 58.537537 | 803 |
| pypsa | toMvar | 23.140133 | 803 |
| veragrid | voltagePu | 1.1213253e-14 | 54928 |
| veragrid | angleDeg | 3.1405989e-12 | 94236 |
| veragrid | fromMw | 1.1414159e-10 | 8968 |
| veragrid | fromMvar | 3.6270098e-10 | 390 |
| veragrid | toMw | 1.140279e-10 | 8968 |
| veragrid | toMvar | 3.6408121e-10 | 390 |
| andes | voltagePu | 3.0362841e-07 | 54980 |
| andes | angleDeg | 0.0001236335 | 54871 |
| andes | fromMw | 0.12385828 | 117 |
| andes | fromMvar | 0.028923891 | 395 |
| andes | toMw | 0.12385472 | 117 |
| andes | toMvar | 0.028868638 | 395 |
| powermodels | voltagePu | 1.0769163e-14 | 56056 |
| powermodels | angleDeg | 3.0127012e-12 | 55110 |
| powermodels | fromMw | 6.6620487e-11 | 908 |
| powermodels | fromMvar | 3.6521897e-10 | 310 |
| powermodels | toMw | 7.8216544e-11 | 32 |
| powermodels | toMvar | 3.6521541e-10 | 310 |
