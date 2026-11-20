# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `d55833986cc4e3e417cad93458f39810e1ed6a54aaa2a979200b0232f271474d`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 469.452240 | 453.483100 | 488.246800 | 10/10 |
| gpu-sparse | 10/10 | 2335.269500 | 1812.706300 | 2958.459200 | 10/10 |
| matpower | 10/10 | 893.949795 | 882.565022 | 904.792070 | 10/10 |
| pandapower | 10/10 | 300.548982 | 290.137818 | 310.310224 | 0/10 |
| pypsa | 0/10 | - | - | - | 0/0 |
| veragrid | 10/10 | 781.769018 | 758.280944 | 830.909392 | 10/10 |
| powsybl | 10/10 | 1078.780121 | 919.431535 | 1473.702544 | 10/10 |
| andes | 10/10 | 310.926215 | 300.584791 | 318.989595 | 0/10 |
| powermodels | 10/10 | 841.618968 | 739.008551 | 1040.634981 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 2.0381208e-11 | 0 |
| gpu-sparse | 2.0381208e-11 | 0 |
| matpower | 1.8385151e-11 | 1.110223e-16 |
| pandapower | 9.2551428 | 8.8817842e-16 |
| pypsa | Unavailable | Unavailable |
| veragrid | 9.2415886e-12 | 1.110223e-16 |
| powsybl | 1.0132908e-11 | 0 |
| andes | 0.0011915379 | 4.9737992e-14 |
| powermodels | 2.6465138e-11 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 1.1110615e-11 pu.

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
| sparse | voltagePu | 6.0507155e-13 | 7822 |
| sparse | angleDeg | 6.1561423e-11 | 7822 |
| sparse | fromMw | 9.44409e-10 | 8138 |
| sparse | fromMvar | 7.8742346e-10 | 9170 |
| sparse | toMw | 9.4441077e-10 | 8138 |
| sparse | toMvar | 7.8733819e-10 | 9170 |
| gpu-sparse | voltagePu | 6.0507155e-13 | 7822 |
| gpu-sparse | angleDeg | 6.1561423e-11 | 7822 |
| gpu-sparse | fromMw | 9.44409e-10 | 8138 |
| gpu-sparse | fromMvar | 7.8742346e-10 | 9170 |
| gpu-sparse | toMw | 9.4441077e-10 | 8138 |
| gpu-sparse | toMvar | 7.8733819e-10 | 9170 |
| pandapower | voltagePu | 0.050938877 | 2935 |
| pandapower | angleDeg | 1.2979057 | 8558 |
| pandapower | fromMw | 86.56091 | 13757 |
| pandapower | fromMvar | 465.74915 | 13758 |
| pandapower | toMw | 91.468514 | 13757 |
| pandapower | toMvar | 532.97465 | 13758 |
| veragrid | voltagePu | 1.0460521e-12 | 7822 |
| veragrid | angleDeg | 8.4430241e-11 | 7822 |
| veragrid | fromMw | 6.892833e-10 | 10076 |
| veragrid | fromMvar | 1.0396661e-09 | 7598 |
| veragrid | toMw | 6.8939698e-10 | 10076 |
| veragrid | toMvar | 1.0522854e-09 | 7598 |
| powsybl | voltagePu | 2.4202862e-13 | 8137 |
| powsybl | angleDeg | 2.0315838e-10 | 8254 |
| powsybl | fromMw | 1.6389095e-09 | 6211 |
| powsybl | fromMvar | 1.7607817e-09 | 5373 |
| powsybl | toMw | 1.9472282e-09 | 5854 |
| powsybl | toMvar | 1.8717401e-09 | 5373 |
| andes | voltagePu | 7.8894688e-06 | 1656 |
| andes | angleDeg | 0.0040623666 | 9031 |
| andes | fromMw | 0.085668879 | 6211 |
| andes | fromMvar | 0.11159526 | 5373 |
| andes | toMw | 0.094697565 | 5854 |
| andes | toMvar | 0.11862509 | 5373 |
| powermodels | voltagePu | 1.1127765e-12 | 7822 |
| powermodels | angleDeg | 9.6576969e-11 | 7822 |
| powermodels | fromMw | 9.5009156e-10 | 8323 |
| powermodels | fromMvar | 8.4321528e-10 | 7598 |
| powermodels | toMw | 9.500738e-10 | 8323 |
| powermodels | toMvar | 8.5367446e-10 | 7598 |
