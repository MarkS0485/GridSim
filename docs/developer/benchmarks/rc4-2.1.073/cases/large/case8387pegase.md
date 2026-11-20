# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `d5427ae512961e5ec5e41224f0be48a71051d81aca21f16ba532ed5194bbb422`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 294.533800 | 273.002600 | 318.784700 | 10/10 |
| gpu-sparse | 10/10 | 288639.074510 | 129126.772800 | 668488.105200 | 10/10 |
| matpower | 10/10 | 883.640099 | 851.592064 | 913.455963 | 10/10 |
| pandapower | 10/10 | 355.091477 | 325.490172 | 504.455433 | 10/10 |
| pypsa | 10/10 | 10892.640550 | 10613.670624 | 11236.631305 | 10/10 |
| veragrid | 10/10 | 728.581573 | 710.768922 | 743.535068 | 10/10 |
| powsybl | 10/10 | 940.148289 | 774.745406 | 1590.655281 | 0/10 |
| andes | 10/10 | 299.568739 | 289.396558 | 309.219004 | 0/10 |
| powermodels | 0/10 | - | - | - | 0/0 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 1.4387282e-11 | 0 |
| gpu-sparse | 1.4387282e-11 | 0 |
| matpower | 4.3366699e-10 | 2.220446e-16 |
| pandapower | 4.3373527e-10 | 8.8817842e-16 |
| pypsa | 1.4845796e-11 | 0 |
| veragrid | 4.3368342e-10 | 2.220446e-16 |
| powsybl | 0.0085769898 | 0 |
| andes | 0.0033105692 | 8.9750429e-13 |
| powermodels | Unavailable | Unavailable |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 4.4657112e-10 pu.

## GridSim bitwise checks

| Check | Compared | Scalar differences |
| --- | --- | ---: |
| sparse repeats | 10/10 solves | 0 |
| gpu-sparse repeats | 10/10 solves | 0 |
| Dense vs Sparse | First converged solve on each path | Unavailable |

## GridSim physics audits

| Path | Attempt | Export | Audit | Exit code |
| --- | ---: | --- | --- | ---: |
| sparse | 1 | JSON (raw run output, not in the repository) | JSON (raw run output, not in the repository) | 0 |
| gpu-sparse | 1 | JSON (raw run output, not in the repository) | JSON (raw run output, not in the repository) | 0 |

## Differences from MATPOWER

Branch flows here are reconstructed on the common source case from each solver's voltages.
Each entry gives the largest absolute difference and its source bus or one-based branch row.

| Path | Quantity | Difference | Bus or branch |
| --- | --- | ---: | ---: |
| sparse | voltagePu | 9.6975761e-12 | 5853 |
| sparse | angleDeg | 2.2015456e-10 | 4620 |
| sparse | fromMw | 1.2371629e-08 | 8847 |
| sparse | fromMvar | 2.6779034e-08 | 8847 |
| sparse | toMw | 1.2178276e-08 | 8847 |
| sparse | toMvar | 2.5240979e-08 | 8847 |
| gpu-sparse | voltagePu | 9.6975761e-12 | 5853 |
| gpu-sparse | angleDeg | 2.2015456e-10 | 4620 |
| gpu-sparse | fromMw | 1.2371629e-08 | 8847 |
| gpu-sparse | fromMvar | 2.6779034e-08 | 8847 |
| gpu-sparse | toMw | 1.2178276e-08 | 8847 |
| gpu-sparse | toMvar | 2.5240979e-08 | 8847 |
| pandapower | voltagePu | 2.8954616e-13 | 7336 |
| pandapower | angleDeg | 3.5139891e-11 | 6247 |
| pandapower | fromMw | 1.3179005e-09 | 4087 |
| pandapower | fromMvar | 2.2783695e-09 | 4087 |
| pandapower | toMw | 1.317872e-09 | 4087 |
| pandapower | toMvar | 2.2781137e-09 | 4087 |
| pypsa | voltagePu | 9.7064579e-12 | 5853 |
| pypsa | angleDeg | 2.1887558e-10 | 4620 |
| pypsa | fromMw | 1.2407668e-08 | 8847 |
| pypsa | fromMvar | 2.680639e-08 | 8847 |
| pypsa | toMw | 1.2214343e-08 | 8847 |
| pypsa | toMvar | 2.5265365e-08 | 8847 |
| veragrid | voltagePu | 2.8432812e-13 | 7760 |
| veragrid | angleDeg | 1.8275159e-11 | 556 |
| veragrid | fromMw | 5.5154281e-10 | 3219 |
| veragrid | fromMvar | 1.4875923e-09 | 10385 |
| veragrid | toMw | 5.7900706e-10 | 2025 |
| veragrid | toMvar | 1.4881039e-09 | 10385 |
| powsybl | voltagePu | 0.00014222266 | 1510 |
| powsybl | angleDeg | 0.93385465 | 4528 |
| powsybl | fromMw | 15.944761 | 5854 |
| powsybl | fromMvar | 2.5469811 | 5854 |
| powsybl | toMw | 16.170754 | 5854 |
| powsybl | toMvar | 2.2135356 | 5373 |
| andes | voltagePu | 2.9211589e-07 | 6503 |
| andes | angleDeg | 0.00044588968 | 4398 |
| andes | fromMw | 0.30202789 | 2215 |
| andes | fromMvar | 0.32916838 | 4110 |
| andes | toMw | 0.30202789 | 2215 |
| andes | toMvar | 0.32896281 | 4110 |
