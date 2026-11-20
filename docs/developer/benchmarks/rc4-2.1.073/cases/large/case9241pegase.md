# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `593a58ecddb5af509ff94410a6630f81021b48fa31da0694ff516acfa9ea5f3b`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 270.477660 | 259.646000 | 283.661700 | 10/10 |
| gpu-sparse | 10/10 | 3381.822250 | 3080.229200 | 3739.948800 | 10/10 |
| matpower | 10/10 | 806.247759 | 787.449121 | 839.719772 | 10/10 |
| pandapower | 10/10 | 296.036930 | 282.363103 | 308.167555 | 0/10 |
| pypsa | 10/10 | 12064.035946 | 11936.177102 | 12201.197859 | 10/10 |
| veragrid | 10/10 | 736.995755 | 722.523373 | 758.746612 | 10/10 |
| powsybl | 10/10 | 1044.995733 | 938.790244 | 1241.194223 | 10/10 |
| andes | 10/10 | 265.741902 | 258.110703 | 277.532523 | 0/10 |
| powermodels | 10/10 | 1161.726455 | 963.711083 | 1755.141630 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 6.8709483e-11 | 0 |
| gpu-sparse | 6.8709483e-11 | 0 |
| matpower | 8.4759222e-12 | 2.220446e-16 |
| pandapower | 10.515515 | 8.8817842e-16 |
| pypsa | 9.4446762e-12 | 0 |
| veragrid | 4.7401727e-12 | 2.220446e-16 |
| powsybl | 4.0362181e-12 | 0 |
| andes | 0.0012489679 | 2.9531932e-14 |
| powermodels | 4.4788995e-09 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 6.9786665e-12 pu.

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
| sparse | voltagePu | 6.2194694e-13 | 5007 |
| sparse | angleDeg | 1.0005863e-10 | 7822 |
| sparse | fromMw | 2.9658622e-09 | 5854 |
| sparse | fromMvar | 4.6262567e-09 | 5373 |
| sparse | toMw | 3.011678e-09 | 5854 |
| sparse | toMvar | 4.6258624e-09 | 5373 |
| gpu-sparse | voltagePu | 6.2194694e-13 | 5007 |
| gpu-sparse | angleDeg | 1.0005863e-10 | 7822 |
| gpu-sparse | fromMw | 2.9658622e-09 | 5854 |
| gpu-sparse | fromMvar | 4.6262567e-09 | 5373 |
| gpu-sparse | toMw | 3.011678e-09 | 5854 |
| gpu-sparse | toMvar | 4.6258624e-09 | 5373 |
| pandapower | voltagePu | 0.05420904 | 2935 |
| pandapower | angleDeg | 0.50011985 | 8558 |
| pandapower | fromMw | 78.455814 | 13758 |
| pandapower | fromMvar | 580.69397 | 13758 |
| pandapower | toMw | 81.034681 | 13758 |
| pandapower | toMvar | 613.35961 | 13758 |
| pypsa | voltagePu | 1.911804e-13 | 7319 |
| pypsa | angleDeg | 4.0593306e-11 | 4018 |
| pypsa | fromMw | 4.2825832e-10 | 5854 |
| pypsa | fromMvar | 5.3887561e-10 | 3827 |
| pypsa | toMw | 4.3485215e-10 | 5854 |
| pypsa | toMvar | 5.393872e-10 | 3827 |
| veragrid | voltagePu | 2.7200464e-13 | 7822 |
| veragrid | angleDeg | 3.9278802e-11 | 363 |
| veragrid | fromMw | 4.8316906e-10 | 5854 |
| veragrid | fromMvar | 4.9641358e-10 | 9088 |
| veragrid | toMw | 4.905587e-10 | 5854 |
| veragrid | toMvar | 4.9627147e-10 | 9088 |
| powsybl | voltagePu | 8.149037e-14 | 4835 |
| powsybl | angleDeg | 1.3880452e-11 | 5245 |
| powsybl | fromMw | 3.0149749e-10 | 4110 |
| powsybl | fromMvar | 3.5294434e-10 | 2587 |
| powsybl | toMw | 3.0149749e-10 | 4110 |
| powsybl | toMvar | 3.5290704e-10 | 2587 |
| andes | voltagePu | 4.7315185e-07 | 2891 |
| andes | angleDeg | 0.00061216328 | 5122 |
| andes | fromMw | 0.062041269 | 5957 |
| andes | fromMvar | 0.067098107 | 5957 |
| andes | toMw | 0.062024495 | 5957 |
| andes | toMvar | 0.066745841 | 5957 |
| powermodels | voltagePu | 1.0803071 | 8662 |
| powermodels | angleDeg | 92.843319 | 8170 |
| powermodels | fromMw | 1244.9492 | 5714 |
| powermodels | fromMvar | 6079.8489 | 5714 |
| powermodels | toMw | 373.9289 | 14963 |
| powermodels | toMvar | 5497.4265 | 10076 |
