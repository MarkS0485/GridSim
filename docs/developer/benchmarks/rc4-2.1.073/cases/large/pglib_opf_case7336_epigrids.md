# GridSim AC power-flow benchmark

Run type: **measurement**.
Case SHA-256: `d0a4b4ca5c438544f1e73fafc32331f388f73a7806a0835ff74b3102023075d5`.
GridSim commit: `7b6487590cf397e4a94aa36d63386ae95b7639e3`.
GridSim build configuration: Release.
Scheduling: paths ran one after another, pinned to CPU 2.

| Path | Successful solves | Mean ms | Min ms | Max ms | Reference residual passes |
| --- | ---: | ---: | ---: | ---: | ---: |
| sparse | 10/10 | 368.703990 | 355.832200 | 387.270900 | 10/10 |
| gpu-sparse | 10/10 | 116087.826150 | 45796.161900 | 225670.568700 | 10/10 |
| matpower | 10/10 | 574.163365 | 563.827038 | 583.671093 | 10/10 |
| pandapower | 10/10 | 210.312281 | 205.526105 | 215.655070 | 0/10 |
| pypsa | 10/10 | 14797.706283 | 14535.194282 | 15345.553126 | 0/10 |
| veragrid | 10/10 | 582.012031 | 571.537840 | 597.034516 | 10/10 |
| powsybl | 10/10 | 597.430493 | 498.215401 | 846.963044 | 10/10 |
| andes | 10/10 | 182.225774 | 173.305944 | 200.005777 | 0/10 |
| powermodels | 10/10 | 525.943757 | 438.156426 | 706.402858 | 10/10 |

## Independent electrical checks

| Path | Maximum mismatch (pu) | Maximum controlled voltage error (pu) |
| --- | ---: | ---: |
| sparse | 5.866761e-10 | 0 |
| gpu-sparse | 5.866761e-10 | 0 |
| matpower | 9.1881702e-12 | 1.110223e-16 |
| pandapower | 1.476008 | 5.5511151e-16 |
| pypsa | 1.476008 | 0 |
| veragrid | 6.0936145e-12 | 1.110223e-16 |
| powsybl | 4.9703885e-12 | 0 |
| andes | 0.0013859798 | 1.3322676e-15 |
| powermodels | 4.5240888e-10 | 0 |

ANDES adds 1e-8 pu to line resistance and reactance.
The largest independently reconstructed ANDES-model mismatch was 3.3605829e-12 pu.

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
| sparse | voltagePu | 3.8869186e-10 | 57039 |
| sparse | angleDeg | 3.1299088e-08 | 57039 |
| sparse | fromMw | 9.5527156e-08 | 7954 |
| sparse | fromMvar | 7.3110769e-08 | 10203 |
| sparse | toMw | 1.0246865e-07 | 7954 |
| sparse | toMvar | 6.8942299e-08 | 10203 |
| gpu-sparse | voltagePu | 3.8869186e-10 | 57039 |
| gpu-sparse | angleDeg | 3.1299088e-08 | 57039 |
| gpu-sparse | fromMw | 9.5527156e-08 | 7954 |
| gpu-sparse | fromMvar | 7.3110769e-08 | 10203 |
| gpu-sparse | toMw | 1.0246865e-07 | 7954 |
| gpu-sparse | toMvar | 6.8942299e-08 | 10203 |
| pandapower | voltagePu | 0.0001345732 | 94498 |
| pandapower | angleDeg | 0.21739302 | 92806 |
| pandapower | fromMw | 138.31348 | 803 |
| pandapower | fromMvar | 23.04704 | 803 |
| pandapower | toMw | 138.37677 | 803 |
| pandapower | toMvar | 22.349626 | 803 |
| pypsa | voltagePu | 0.0001345732 | 94498 |
| pypsa | angleDeg | 0.217393 | 92806 |
| pypsa | fromMw | 138.31348 | 803 |
| pypsa | fromMvar | 23.04704 | 803 |
| pypsa | toMw | 138.37677 | 803 |
| pypsa | toMvar | 22.349626 | 803 |
| veragrid | voltagePu | 1.1213253e-14 | 56806 |
| veragrid | angleDeg | 5.4569682e-12 | 50390 |
| veragrid | fromMw | 1.1611405e-09 | 390 |
| veragrid | fromMvar | 3.5339554e-10 | 117 |
| veragrid | toMw | 1.3744739e-09 | 390 |
| veragrid | toMvar | 4.5960746e-10 | 117 |
| powsybl | voltagePu | 2.0095037e-14 | 56798 |
| powsybl | angleDeg | 4.206413e-12 | 57950 |
| powsybl | fromMw | 7.3657702e-10 | 117 |
| powsybl | fromMvar | 5.4983218e-10 | 117 |
| powsybl | toMw | 8.0177642e-10 | 117 |
| powsybl | toMvar | 3.5097969e-10 | 390 |
| andes | voltagePu | 3.0654523e-07 | 57689 |
| andes | angleDeg | 6.0139903e-05 | 57689 |
| andes | fromMw | 0.13182127 | 59 |
| andes | fromMvar | 0.036639554 | 395 |
| andes | toMw | 0.13205769 | 59 |
| andes | toMvar | 0.036554544 | 395 |
| powermodels | voltagePu | 2.6374769e-10 | 57039 |
| powermodels | angleDeg | 2.0305841e-08 | 57039 |
| powermodels | fromMw | 6.3197966e-08 | 7954 |
| powermodels | fromMvar | 4.2111736e-08 | 10203 |
| powermodels | toMw | 6.7811733e-08 | 7954 |
| powermodels | toMvar | 4.7238977e-08 | 7954 |
