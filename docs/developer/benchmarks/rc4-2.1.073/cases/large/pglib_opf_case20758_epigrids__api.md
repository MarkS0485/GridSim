# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `cdbab649c28941a5a6d5255579a3b417c5dcf2e65423c1ddbca7d3d77c4b2e3a`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 763.964880 | 727.934400 | 790.772700 | 10/10 |
| gpu-sparse | 10/10 | 271984.645280 | 156217.195600 | 504994.531700 | 10/10 |
| matpower | 10/10 | 1493.934965 | 1466.657877 | 1531.426907 | 10/10 |
| pandapower | 10/10 | 711.044977 | 697.283503 | 746.056576 | 0/10 |
| pypsa | 10/10 | 62706.726815 | 61680.478703 | 63832.423512 | 0/10 |
| veragrid | 10/10 | 1783.182154 | 1746.657427 | 1813.586787 | 10/10 |
| powsybl | 10/10 | 2302.797109 | 1878.387735 | 2904.025685 | 10/10 |
| andes | 10/10 | 567.724436 | 549.326549 | 581.013846 | 0/10 |
| powermodels | 10/10 | 1826.829833 | 1557.072714 | 2137.368087 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 8.7394796e-09 | 0 |
| gpu-sparse | 8.7394796e-09 | 0 |
| matpower | 6.9244626e-09 | 1.110223e-16 |
| pandapower | 2.2815972 | 0.0084154558 |
| pypsa | 4.7701452 | 0 |
| veragrid | 6.9244853e-09 | 1.110223e-16 |
| powsybl | 6.9244717e-09 | 0 |
| andes | 0.0048664008 | 5.206946e-13 |
| powermodels | 5.0925202e-11 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 6.924603e-09 pu.

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
| sparse | voltagePu | 1.0291781e-09 | 63432 |
| sparse | angleDeg | 6.7213803e-08 | 63392 |
| sparse | fromMw | 4.5010904e-07 | 14268 |
| sparse | fromMvar | 8.251908e-07 | 7300 |
| sparse | toMw | 4.4886542e-07 | 14268 |
| sparse | toMvar | 7.4671578e-07 | 7300 |
| gpu-sparse | voltagePu | 1.0291781e-09 | 63432 |
| gpu-sparse | angleDeg | 6.7213803e-08 | 63392 |
| gpu-sparse | fromMw | 4.5010904e-07 | 14268 |
| gpu-sparse | fromMvar | 8.251908e-07 | 7300 |
| gpu-sparse | toMw | 4.4886542e-07 | 14268 |
| gpu-sparse | toMvar | 7.4671578e-07 | 7300 |
| pandapower | voltagePu | 0.013661098 | 78276 |
| pandapower | angleDeg | 1.2792587 | 76819 |
| pandapower | fromMw | 107.87 | 7815 |
| pandapower | fromMvar | 127.78856 | 7803 |
| pandapower | toMw | 107.71575 | 7815 |
| pandapower | toMvar | 126.69629 | 7803 |
| pypsa | voltagePu | 0.025009222 | 58771 |
| pypsa | angleDeg | 19.913148 | 66504 |
| pypsa | fromMw | 305.68003 | 16273 |
| pypsa | fromMvar | 168.08154 | 7803 |
| pypsa | toMw | 307.35835 | 16273 |
| pypsa | toMvar | 167.04583 | 7803 |
| veragrid | voltagePu | 2.0894397e-13 | 75985 |
| veragrid | angleDeg | 5.5592864e-11 | 58670 |
| veragrid | fromMw | 6.8644113e-10 | 6757 |
| veragrid | fromMvar | 1.5750174e-09 | 31741 |
| veragrid | toMw | 6.8803274e-10 | 6757 |
| veragrid | toMvar | 1.5750459e-09 | 31741 |
| powsybl | voltagePu | 1.0635937e-13 | 75908 |
| powsybl | angleDeg | 1.1650059e-10 | 75527 |
| powsybl | fromMw | 6.3170802e-10 | 16790 |
| powsybl | fromMvar | 4.5631907e-09 | 31741 |
| powsybl | toMw | 5.9492322e-10 | 6757 |
| powsybl | toMvar | 4.5632476e-09 | 31741 |
| andes | voltagePu | 9.100645e-07 | 63414 |
| andes | angleDeg | 0.00055335837 | 61291 |
| andes | fromMw | 0.21579358 | 6822 |
| andes | fromMvar | 0.26023362 | 6822 |
| andes | toMw | 0.21579358 | 6822 |
| andes | toMvar | 0.26024512 | 6822 |
| powermodels | voltagePu | 1.0294414e-09 | 63432 |
| powermodels | angleDeg | 7.112131e-08 | 63392 |
| powermodels | fromMw | 4.1685557e-07 | 10333 |
| powermodels | fromMvar | 5.4197969e-07 | 9653 |
| powermodels | toMw | 4.1571824e-07 | 10333 |
| powermodels | toMvar | 5.787648e-07 | 9653 |
