# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `391206901084891e4f2c3df1ed2053154195258f9adc2475679adefb4261e97a`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 149.596690 | 143.416300 | 158.289400 | 10/10 |
| gpu-sparse | 10/10 | 23831.754980 | 11295.701900 | 30524.666900 | 10/10 |
| matpower | 10/10 | 581.729388 | 543.059111 | 634.532928 | 10/10 |
| pandapower | 10/10 | 219.330567 | 211.142856 | 234.538281 | 0/10 |
| pypsa | 10/10 | 16274.566442 | 14789.759981 | 18694.531014 | 0/10 |
| veragrid | 10/10 | 585.934414 | 555.795646 | 653.861716 | 10/10 |
| powsybl | 10/10 | 592.760152 | 383.100543 | 713.540571 | 10/10 |
| andes | 10/10 | 152.824981 | 143.376839 | 161.642247 | 0/10 |
| powermodels | 10/10 | 455.535073 | 384.371539 | 669.537119 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 7.8205176e-12 | 0 |
| gpu-sparse | 7.8205176e-12 | 0 |
| matpower | 7.8330231e-12 | 1.110223e-16 |
| pandapower | 2.0670268 | 6.6613381e-16 |
| pypsa | 2.9810747 | 0 |
| veragrid | 7.8375706e-12 | 0 |
| powsybl | 7.8205176e-12 | 0 |
| andes | 0.001297098 | 2.9976022e-14 |
| powermodels | 1.5492549e-11 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 7.8182438e-12 pu.

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
| sparse | voltagePu | 8.3266727e-15 | 31594 |
| sparse | angleDeg | 1.4370727e-12 | 85616 |
| sparse | fromMw | 3.5730352e-10 | 364 |
| sparse | fromMvar | 7.2532202e-10 | 286 |
| sparse | toMw | 3.5731773e-10 | 364 |
| sparse | toMvar | 3.6573056e-10 | 286 |
| gpu-sparse | voltagePu | 8.3266727e-15 | 31594 |
| gpu-sparse | angleDeg | 1.4370727e-12 | 85616 |
| gpu-sparse | fromMw | 3.5730352e-10 | 364 |
| gpu-sparse | fromMvar | 7.2532202e-10 | 286 |
| gpu-sparse | toMw | 3.5731773e-10 | 364 |
| gpu-sparse | toMvar | 3.6573056e-10 | 286 |
| pandapower | voltagePu | 0.0036301978 | 84645 |
| pandapower | angleDeg | 0.81370839 | 84750 |
| pandapower | fromMw | 90.950503 | 8450 |
| pandapower | fromMvar | 33.162242 | 8450 |
| pandapower | toMw | 90.945344 | 8450 |
| pandapower | toMvar | 32.750351 | 8450 |
| pypsa | voltagePu | 0.0036850816 | 84645 |
| pypsa | angleDeg | 1.635763 | 84645 |
| pypsa | fromMw | 176.09776 | 1768 |
| pypsa | fromMvar | 32.768071 | 8450 |
| pypsa | toMw | 176.07192 | 1768 |
| pypsa | toMvar | 32.366152 | 8450 |
| veragrid | voltagePu | 6.7723605e-15 | 85267 |
| veragrid | angleDeg | 3.0109248e-12 | 84727 |
| veragrid | fromMw | 3.1738523e-10 | 364 |
| veragrid | fromMvar | 6.8329342e-10 | 364 |
| veragrid | toMw | 3.1739944e-10 | 364 |
| veragrid | toMvar | 6.8330053e-10 | 364 |
| powsybl | voltagePu | 9.3258734e-15 | 32085 |
| powsybl | angleDeg | 1.8403057e-12 | 28974 |
| powsybl | fromMw | 4.9168847e-10 | 364 |
| powsybl | fromMvar | 6.4566308e-10 | 364 |
| powsybl | toMw | 4.9171689e-10 | 364 |
| powsybl | toMvar | 7.2770945e-10 | 286 |
| andes | voltagePu | 2.6138933e-07 | 31898 |
| andes | angleDeg | 4.9456577e-05 | 28992 |
| andes | fromMw | 0.062422328 | 286 |
| andes | fromMvar | 0.12983985 | 286 |
| andes | toMw | 0.062408485 | 286 |
| andes | toMvar | 0.12991737 | 286 |
| powermodels | voltagePu | 1.1657342e-14 | 84658 |
| powermodels | angleDeg | 1.7834623e-12 | 28996 |
| powermodels | fromMw | 4.0832049e-10 | 364 |
| powermodels | fromMvar | 2.1451783e-09 | 364 |
| powermodels | toMw | 5.8702909e-10 | 364 |
| powermodels | toMvar | 2.1112783e-09 | 364 |
