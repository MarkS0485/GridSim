# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `9da3bf1c58d4e5a72ea4d743c604355df9052e77fdb7f9ef21b86a2657466f16`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 133.690990 | 129.041300 | 141.107200 | 10/10 |
| gpu-sparse | 10/10 | 15235.130210 | 4565.579200 | 30254.025800 | 10/10 |
| matpower | 10/10 | 524.140191 | 501.048088 | 540.241003 | 10/10 |
| pandapower | 10/10 | 210.017568 | 205.603158 | 213.669226 | 0/10 |
| pypsa | 10/10 | 14018.594113 | 12670.273658 | 17137.150828 | 0/10 |
| veragrid | 10/10 | 560.804759 | 545.990842 | 583.743192 | 10/10 |
| powsybl | 10/10 | 626.112891 | 409.938421 | 771.795977 | 10/10 |
| andes | 10/10 | 143.287591 | 135.469602 | 149.595259 | 0/10 |
| powermodels | 10/10 | 437.296714 | 370.484166 | 602.391831 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 1.8875426e-11 | 0 |
| gpu-sparse | 1.8877984e-11 | 0 |
| matpower | 1.8875994e-11 | 0 |
| pandapower | 2.5609927 | 4.4408921e-16 |
| pypsa | 2.8990408 | 0 |
| veragrid | 1.8874005e-11 | 0 |
| powsybl | 1.8874005e-11 | 0 |
| andes | 0.0015295657 | 5.2402527e-14 |
| powermodels | 1.8875141e-11 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 1.8879973e-11 pu.

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
| sparse | voltagePu | 1.0991208e-14 | 29039 |
| sparse | angleDeg | 1.0338397e-12 | 27911 |
| sparse | fromMw | 1.7593038e-10 | 286 |
| sparse | fromMvar | 7.2781603e-10 | 364 |
| sparse | toMw | 2.2080826e-10 | 286 |
| sparse | toMvar | 3.6193626e-10 | 14 |
| gpu-sparse | voltagePu | 1.2101431e-14 | 84658 |
| gpu-sparse | angleDeg | 7.5495166e-13 | 27911 |
| gpu-sparse | fromMw | 1.1817747e-10 | 286 |
| gpu-sparse | fromMvar | 7.2688522e-10 | 364 |
| gpu-sparse | toMw | 1.7428192e-10 | 286 |
| gpu-sparse | toMvar | 3.5958436e-10 | 430 |
| pandapower | voltagePu | 0.0041851789 | 84645 |
| pandapower | angleDeg | 1.0482544 | 84750 |
| pandapower | fromMw | 115.68128 | 386 |
| pandapower | fromMvar | 38.915329 | 8450 |
| pandapower | toMw | 115.60509 | 386 |
| pandapower | toMvar | 38.47988 | 8450 |
| pypsa | voltagePu | 0.0042485159 | 84645 |
| pypsa | angleDeg | 1.5056573 | 84645 |
| pypsa | fromMw | 190.04074 | 1768 |
| pypsa | fromMvar | 38.463998 | 8450 |
| pypsa | toMw | 190.01031 | 1768 |
| pypsa | toMvar | 38.027313 | 8450 |
| veragrid | voltagePu | 8.9928065e-15 | 31898 |
| veragrid | angleDeg | 2.0001778e-12 | 30606 |
| veragrid | fromMw | 1.7089974e-10 | 286 |
| veragrid | fromMvar | 7.2324724e-10 | 364 |
| veragrid | toMw | 2.1580604e-10 | 286 |
| veragrid | toMvar | 3.6976644e-10 | 108 |
| powsybl | voltagePu | 8.2156504e-15 | 85524 |
| powsybl | angleDeg | 8.3932861e-13 | 27888 |
| powsybl | fromMw | 8.3097973e-11 | 430 |
| powsybl | fromMvar | 3.8016879e-10 | 286 |
| powsybl | toMw | 1.237197e-10 | 286 |
| powsybl | toMvar | 7.2045481e-10 | 364 |
| andes | voltagePu | 2.9188831e-07 | 31898 |
| andes | angleDeg | 5.5371978e-05 | 28992 |
| andes | fromMw | 0.050468444 | 364 |
| andes | fromMvar | 0.15336739 | 286 |
| andes | toMw | 0.050468972 | 364 |
| andes | toMvar | 0.15346172 | 286 |
| powermodels | voltagePu | 1.4543922e-14 | 30998 |
| powermodels | angleDeg | 1.1617374e-12 | 30550 |
| powermodels | fromMw | 2.4110136e-10 | 286 |
| powermodels | fromMvar | 7.2852657e-10 | 364 |
| powermodels | toMw | 2.8606451e-10 | 286 |
| powermodels | toMvar | 1.4535928e-09 | 364 |
