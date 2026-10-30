# 11. Validation

This chapter specifies every cross-check that holds GridSim's numbers against something outside the solver that
produced them: what is compared, how agreement is measured, which tolerance gates the comparison, and what the
instrument reports on the reference cases. A tolerance is a maintainer expectation. A change that makes an observed
value worse is wrong until proven otherwise, and no test is loosened to make a change pass.

The stance has two layers. The primary instruments are independent re-derivations: NumPy code that rebuilds the
network equations from the recorded inputs and asks whether the answer satisfies them. That is a stronger question than
whether two iterative solvers agree, and it stays valid where a second solver cannot converge. The corroboration
instruments diff GridSim against named industry tools (MATPOWER's published solution, pandapower, SciPy) where those
tools converge. A third layer lives inside the test suite: alternative numerical paths (dense and sparse, method and
method) must produce the same physics, and in the power flow the same bits.

The instruments are run from the command line. [Ch. 27, Validation tools](<../manual/0027 Validation tools.md>)
shows how; the physics-law audit is specified in [ch. 16](<0016 Physics conformance and the constrained estimator.md>); the solver being checked
is [ch. 3, Power flow](<0003 Power flow.md>) and the estimator is [ch. 6](<0006 State estimation.md>). The
export documents the sidecars read are specified in [ch. 10, File and wire formats](<0010 File and wire formats.md>).

## Model

### How agreement is measured

Four measures are used, and each instrument below names the one it applies.

| Measure | Definition | Where |
|---|---|---|
| Residual | the independent recomputation of a balance, evaluated at GridSim's answer, minus the specified value | `residual_check.py`, `physics_audit.py`, `se_residual_check.py`, the physics audit |
| Cross-implementation difference | the largest absolute difference of the same quantity between GridSim and a named tool, after a shared reference is applied | `crossval.py`, `crossval_estimation.py`, `crossval_freq.py`, `crossval_classical.py` |
| Bit identity | every IEEE-754 bit pattern equal, no tolerance | dense against sparse Newton-Raphson, the injection-evaluation gate, export determinism |
| Conditioning-scaled difference | a relative difference gated at `rtol * cond(V)` | `crossval_smallsignal.py` |

Voltage angles are defined up to a global reference, so a cross-implementation angle comparison first aligns both sets
to the first slack bus. `crossval.py`:

```
offset  = va_pandapower(slack) - va_gridsim(slack)
dVm_i   = abs(vm_pandapower_i - vm_gridsim_i)
dVa_i   = abs((va_pandapower_i - offset) - va_gridsim_i)
agree   = max(dVm) <= tol  and  max(dVa in rad) <= tol       tol default 4096 eps = 2^-40 = 9.09e-13
```

A case in `crossval.py`'s `ALLOWANCES` is judged against its own measured bound instead, with the reason in the
report.

### case9 against the published MATPOWER solution

The WSCC 9-bus case (`IeeeCases.Case9`, `src/GridSim.Core/IO/IeeeCases.cs`) is the physics ground truth. The solve is
held to the published MATPOWER solution by `tests/GridSim.Tests/PowerFlowTests.cs`: every bus voltage magnitude equals
the published value after rounding to 3 decimals and every angle after rounding to 2 decimals, slack generation is 71.64
MW to 1 decimal, total real loss is 4.641 MW to 2 decimals, and the solve converges to a mismatch below `1e-8` pu in at
most 6 iterations. On the reference run (`gridsim gpucheck`, see ch. 27) the dense and the sparse engine both take 4
iterations to a mismatch of 1.6e-14 pu. The dynamic data is the published Anderson and Fouad WSCC set: H of 23.64, 6.40
and 3.01 s on machine bases of 247.5, 192 and 128 MVA, so the stored energies are 5850.9, 1228.8 and 385.28 MW s and the
system total is 7.465 GVA s (`Case9InertiaTests`, which also pins the MATPOWER branch `rateA` ratings). Inertia does not
enter the load flow; the pin protects the frequency work.

### The independent residual check (power flow)

`tools/crossval/residual_check.py` rebuilds the bus admittance matrix in NumPy from the exported branch and shunt data
with the standard MATPOWER pi-model, a different language and codebase from the solver, and evaluates the injected power
at GridSim's solved voltages:

```
Y[f,f] += (y + j*b/2) / abs(T)^2        Y[t,t] += y + j*b/2
Y[f,t] += -y / conj(T)                  Y[t,f] += -y / T
Y[i,i] += (Gs + j*Bs) / BaseMva
y = 1/(R + jX),   T = tap(0 means 1) * exp(j*shift)           out-of-service branches carry nothing
S   = V .* conj(Y V) * BaseMva
dP  = Re(S) - (Pg - Pd)                      over every non-slack bus
dQ  = Im(S) + Qd                             over every PQ bus
Qg  = Im(S) + Qd                             at a PV bus, against the summed Qmin..Qmax of its generators
pass = max|dP| <= tol  and  max|dQ| <= tol  and  no PV bus outside its Q range by more than max(1e-3, tol)
```

`tol` defaults to `1e-6` MVA, an absolute number on the MVA scale. Exit codes: 0 within tolerance, 1 mismatch, 3
export unreadable or unparseable. The script reads `Pd`, `Qd`, `Pg` and the limits as constants, so a bus with a voltage-dependent
load is not modelled (see Limits).

### pandapower: power flow

`tools/crossval/crossval.py`, driven by the `crossval` verb, rebuilds the exported network as a MATPOWER case
(`build_ppc`: 9999 stands in for a zero branch rating, an absent generator limit takes GridSim's own default of plus or
minus infinity, and `Vmax` 1.1, `Vmin` 0.9 are never read by a power flow), converts it with pandapower's `from_ppc`,
and solves. The script makes the two problems the same:

| What | GridSim | Old translation | Now |
|---|---|---|---|
| Several generators on a bus | one injection per bus: P, Q and Qmin / Qmax summed, the last unit's Vg (`NewtonShell.BuildBusSpec`, `VoltageSetpoint`) | from_ppc kept the first unit as a `gen` (its own Q range, its own Vg) and made every other one a static `sgen` with no reactive range | one ppc row per bus with those sums (`aggregate_generators`) |
| Transformer charging B | MATPOWER's stamp: `j b/2 / |tau|^2` at the from bus, `j b/2` at the to bus | from_ppc turned it into a magnetising current `i0_percent = |B|` (sign lost, moved across the tap) | the same admittances as bus shunts |
| Transformer written LV -> HV | ratio at the from bus | from_ppc put the ratio on the HV winding (the `tap_side` option does not change its matrix) | written HV -> LV: tap `1/t`, shift `-phi`, r and x times `t^2` (the same four admittances) |
| pandapower options | - | defaults (`trafo_model="t"`, tolerance 1e-8 pu) | `trafo_model="pi"`, `enforce_q_lims=True`, `distributed_slack=False`, `voltage_depend_loads=True` (only the export's ZIP loads), a fresh solve at each of 1e-8 ... 1e-15 pu keeping the tightest that converges |

A guard then compares the admittance matrix pandapower solved with MATPOWER's `makeYbus` on the UNREWRITTEN export
rows; above 4096 machine epsilons of the largest entry the sidecar exits 4 with no verdict. The report also lists both
tools' reactive-limit outcomes (`like_for_like`). The diff is the slack-aligned one above. Branch flows are not
compared. The verb solves the case with the selected engine, reactive limits on, up to 100 iterations, then solves it
again afresh at 1e-9 ... 1e-15 pu and exports the tightest converged state (Newton's state error is about J^-1 times
its final residual, so a 1e-8 pu stop leaves up to 1e-9 pu in |V|), and runs the sidecar.

Measured 2026-10-05 (pandapower 3.5.5, NumPy 2.4.6, SciPy 1.18.1), every case the verb names, worst |V| (pu) and angle
(rad): case9 4.4e-16 / 7.0e-17; case14 4.4e-16 / 1.1e-15; case30 6.7e-16 / 1.7e-15; case57 1.8e-15 / 3.5e-15; case118
8.9e-16 / 2.7e-15; case300 5.2e-14 / 8.7e-14; case145 1.9e-14 / 1.2e-14; case24 6.7e-16 / 5.0e-16; cigre 1.6e-15 /
2.0e-15; uk 4.4e-16 / 7.8e-17; gb-reduced-29 7.8e-16 / 1.4e-15; pypsa-gb-reduced 7.4e-15 / 3.5e-15; gb-synthetic 1.9e-14
/ 3.9e-14; gb-full 2.6e-13 / 3.9e-13 (the worst, 1774 eps, a 2.3x margin inside 4096 eps); gb-network-2224 3.9e-13 /
2.8e-12 under its allowance (2e-12 pu, 4e-12 rad: a floating-point floor; pandapower disagrees with itself by 3.6e-12
rad between a flat and a DC start on that case). Verb exit codes: 0 agrees, 3 mismatch or sidecar failure, 77 no interpreter
or no pandapower, 1 a case or script that cannot be found, 2 a missing argument. Sidecar exit codes: 0 within tolerance,
1 mismatch, 3 unreadable export, 4 pandapower solve crashed or the translation guard fired, 77 NumPy, SciPy or
pandapower absent. A skip is never a pass. `N243CrossvalMachineEpsilonTests` pins the default, the translation, the
guard and the verb's ladder by source-text assertions, and proves Newton reaches 1e-12 pu on eight bundled cases.
`CrossvalContractTests` locks the export fields the sidecar reads. The rule that exit 77 is the skip and a Python exit 2
is a failure is pinned by source-text assertions (`CliFailLoudFlagContractTests.Crossval_skip_is_exit_77_and_a_python_exit_2_is_a_failure`
reads `crossval.py` and `Program.cs`); no test runs the verb or a sidecar.

### pandapower: state estimation

`tools/crossval/crossval_estimation.py` rebuilds the network, feeds `pandapower.estimation` the identical measurement set
and diffs the estimated states, slack-aligned:

```
estimate(net, algorithm="wls", init="flat", tolerance=1e-8, maximum_iterations=50)
agree = max(dVm) <= tol_vm (1e-6 pu)  and  max(dVa) <= tol_va (1e-5 deg)
```

These two defaults are not machine-epsilon bounds, and the script's docstring says why: the two estimates are each the
stopping point of an iteration (pandapower stops at `tolerance=1e-8`, GridSim at its own convergence tolerance), so they
can differ by up to those stopping tolerances, not by rounding, and pandapower does not model GridSim's zero-injection
constraints. A tighter default needs both estimators driven to the same fixed point and the difference measured on
every covered case; until then an explicit `--tol-vm` / `--tol-va` overrides.

Pandapower's bus injection measurements are consumption-positive and GridSim's are net injection, so the sign flips in one
place (`INJECTION_SIGN`). Branch-flow measurements pass through unflipped. Every branch lands as a measurable line or
transformer because the script sets one base kV on every bus (per-unit results are unaffected). The measurement set is the
one the estimate was finally computed from, after GridSim's bad-data removals. The kinds `VmPu`, `VaDeg`, `PInjMw`,
`QInjMvar`, `PFlowMw` and `QFlowMvar` are fed; `IMagPu`, `IAngDeg` and any unknown kind exit 3 and name the kinds, since
pandapower's current measurements are in kA on its own base and that conversion is not validated.

With `--bad-data` the script feeds pandapower the original set with its gross errors, runs `chi2_analysis` and
`remove_bad_data(rn_max_threshold=3.0)`, and reports `counts_agree`: both tools removed something, or neither did. It
does not compare which measurement each removed, and reports GridSim's first removal for a human to compare. Exit codes: 0
within tolerance, 1 mismatch, 2 pandapower absent, 3 unreadable export or an unsupported kind, 4 pandapower estimation
crashed. `EstimateExportContractTests` locks `gridsim-estimate-export/1`.

### The estimation instrument: `se_residual_check.py`

`tools/crossval/se_residual_check.py` validates a `gridsim-estimate-export/1` document with no GridSim arithmetic and no
second estimator. It builds the admittance matrix with `residual_check.build_ybus` and its own measurement partials, over
the state `[Va at non-slack buses (rad); Vm at all buses (pu)]`:

```
scale   = 1 (VmPu, IMagPu) | 180/pi (VaDeg, IAngDeg) | BaseMva (power kinds)      h_pu = h_engineering / scale
honesty = max | (z - h(x_hat)) - reported_residual |                  fail above --tol (1e-6, engineering units)
J       = sum( ((z - h(x_hat)) / sigma)^2 )   |J - reported J| fail above max(1e-6, 1e-9 * max(J, 1))
W = 1/sigma_pu^2     G = H^T W H     g = H^T W r_pu     step = G^-1 g
optimality = max | step |                                             fail above --tol (1e-6, pu and rad)
K_i = W_i * h_i^T G^-1 h_i       Omega_i = sigma_pu_i^2 * (1 - K_i)          (--leverage)
flagged critical is CONFIRMED iff K_i > 1 - 1e-6; an unflagged row with K_i > 1 - 1e-6 also fails
```

`H` is built analytically from the script's own textbook partials over its own admittance matrix, with the current-kind
rows built by central differences, and held sparse so the check runs at the scale of the GB models. `--fd` cross-checks
the analytic rows against central differences and fails above `1e-6`, so the instrument validates itself. The check needs
SciPy for the sparse solve. Exit codes: 0 all checks pass; 1 any check fails, or an uncaught exception (a missing or
malformed file, a missing SciPy); 3 a wrong schema tag, a measurement branch that does not resolve to one branch, or an
unsupported measurement kind. `--leverage` recomputes `K_i` independently and arbitrates the export's critical flags in
both directions: a flagged row that is not confirmed fails, and an unflagged row with `K_i > 1 - 1e-6` fails.

### The Monte-Carlo sweep

`tools/crossval/mc_estimate_sweep.py` runs `gridsim estimate <case> --synth` through the Release build over seeds
(`--seeds`, default 10) and seven fixed configurations, scores each estimate against its own sampled truth, and
aggregates the mean and worst voltage error and the chi-square pass rate. The seven configurations, as
`(label, noise, coverage-vm, coverage-inj, coverage-flow)`, are `full/n0.5`, `full/n1`, `full/n2`, `half-flow`,
`no-flow`, `sparse` and `inj-only`. It writes `tools/crossval/mc_estimate_sweep_<case>.json`, exits 0 when it ran and 2
when the Release build is missing, and resolves the executable per operating system. It is an analysis, not a gate. Three
properties are the expectations (the `dof` column is the first seed's value, and for `full/n2` it is 843 because the
bad-data loop removed 20 of the 1,098 measurements); the committed `mc_estimate_sweep_case118.json` reproduces byte for byte on the reference
run:

| Property | Measured on case118, 10 seeds |
|---|---|
| Error scales with noise | mean `dVm` 2.684e-3 pu at noise 1 and 6.276e-3 pu at noise 2, a factor of 2.34 |
| Error rises as redundancy falls, not monotonically | `dof` 863 to 30 raises mean `dVm` from 2.684e-3 to 5.253e-3 pu, a factor of 1.96, yet `sparse` (`dof` 244) reads 3.79e-3 and `no-flow` (`dof` 119) reads 3.64e-3 |
| The chi-square gate is calibrated | noise 2 (data twice its declared sigma) fails 0 of 10; every correctly declared configuration passes 10 of 10 |

### The scipy swing and eigenvalue checks

`crossval_freq.py` re-integrates the swing physics of a `gridsim-frequency-export/1` document (governor droop, generic
fast frequency response, the optional grid-forming block, all starting at nominal frequency) with
`scipy.integrate.solve_ivp`, RK45, `rtol` and `atol` `1e-8`, `max_step` 0.02 s, and compares the initial rate of change of
frequency and the nadir:

```
d_rocof = abs( abs(peak df/dt scipy) - abs(initialRocofHzPerS) )        gate --tol-rocof 0.02 Hz/s
d_nadir = abs( min(f scipy) - nadirHz )                                  gate --tol-nadir 0.05 Hz
```

Exit 0 within tolerance, 1 outside, 2 unreadable export or no SciPy, 77 not checked: an export that carries recorded
response services or an offset pre-event start is physics the re-integration does not model, so the script reports `NOT
CHECKED` with the reasons and does not compare. `crossval_h0.py` skips such cells for the same reason.
`crossval_classical.py` re-integrates the classical multi-machine swing of a `gridsim-classical-export/1` document and
gates the largest rotor-angle difference at `--tol` `1e-3` rad; exit 0 within tolerance, 1 outside, 3 setup error
(unreadable export, wrong schema), 77 NumPy or SciPy missing. The default is not a machine-epsilon bound: GridSim
integrates with a fixed-step RK4 at the export's `stepSeconds` and SciPy with an adaptive RK45 at
`rtol = atol = 1e-9`, so the difference is GridSim's RK4 truncation error, not rounding. A tighter default needs a
like-for-like fixed-step mode with an allowance measured on every case; an explicit `--tol` overrides.
`crossval_smallsignal.py` recomputes the eigenvalues of the exported state matrix with `scipy.linalg.eig`; exit 0 within
tolerance, 1 outside, 3 setup error (unreadable export, wrong schema, no valid pairing), 77 NumPy or SciPy missing:

```
rel_i  = abs(lambda_gridsim_i - lambda_scipy_p(i)) / max(abs(lambda_scipy_p(i)), 1)   p = optimal assignment
guard  = rtol * max(1, cond(V))                                                      rtol default 4096 eps
pass   = max(rel_i) <= guard
```

A non-symmetric eigensolve agrees only to `cond(V)` times machine epsilon, so this is a conditioning-scaled
check and `cond(V)` is printed. The pairing `p` is the one-to-one assignment of SciPy's eigenvalues to GridSim's that
minimises the total `rel` (`scipy.optimize.linear_sum_assignment`); a sort on (Im, Re) would mis-pair real eigenvalues
whose imaginary parts are rounding noise. The report names the pairing method and the worst pair. Participation factors
are compared through the same pairing and reported, not gated, and are skipped when the export marks them unavailable (a
defective state matrix). The default `rtol` is 4096 machine epsilons, 9.09e-13 (`DEFAULT_RTOL_EPS` in
`crossval_smallsignal.py`). The module docstring of that script records the measured need over the bundled `modal` exports:
at most 0.2165 machine epsilons of `max_rel / max(1, cond(V))` (gb with `--damping 2`), and at most 0.0874 for the
undamped near-defective cases (case9: `max_rel` 3.121e-8, `cond(V)` 1.928e10), so the default carries a wide margin.

`crossval_h0.py` has two parts for a `gridsim-h0-study/1` export. The arithmetic part re-derives every cell: the stored
energy and the virtual-inertia separation to `1e-9 * max(1, e_real)` (a relative tolerance with an absolute floor of
`1e-9` MW s), the bare rate of change of frequency to an absolute `1e-9` Hz/s, and the forming-dependent sentinel
exactly. The second part is an RK45 re-integration of representative forming cells gated at 0.02 Hz/s and 0.05 Hz. Exit
0 when every runnable check passes (without SciPy the arithmetic runs and the re-integration is skipped, with a message
on the error stream), 1 on a mismatch, 2 on an unreadable export.

### The NumPy harmonic and impedance checks

`crossval_harmonics.py` rebuilds `Y(h)` from the case directory with the standard pi-model (series reactance scaling
with `h`, a capacitive shunt scaling with `h` and an inductive one with `1/h`, a resistive shunt unchanged), assembles
`I(h)` from a `gridsim-harmonics/1` source set, solves each order with `numpy.linalg.solve`, forms the voltage THD and
diffs both against a `gridsim-harmonic-export/1` study. A comparison passes when `|mine - GridSim| <= atol + rtol *
|GridSim|`, with `--tol` (`rtol`) and `--atol` each defaulting to 4096 machine epsilons, 9.09e-13 (the absolute one in pu).
Exit codes: 0 within tolerance, 1 mismatch, 3 unreadable input or a failed comparison, 77 NumPy missing (D-DOC-127).

```
ys   = 1 / (R + j*h*X)          THD_V = sqrt(sum_{h>1} |V_h|^2) / |V_1| * 100
```

`|V_1|` is the solved fundamental the export records in `indices.fundamentalVm` (GridSim forms THD over it). With a
nominal 1.0 pu in its place, on uk every harmonic voltage agrees to 9.0e-17 pu while 20 THD values fail (max |dTHD|
1.84e-1 %); with the recorded `|V_1|` the THD agrees to 5.3e-15 %. An older export without the list
falls back to 1.0 pu and the report says so.

The module docstring of `crossval_harmonics.py` records the observed agreement: max `dVm` 9.7e-17 pu on uk and 1.1e-16 pu
on gb-synthetic, one machine epsilon, against a default of 4096 epsilons that leaves room for the conditioning of `Y(h)`
near a resonance.

`crossval_impedance.py` rebuilds the converter admittance, the grid impedance in the converter's dq frame
(`Z(f + f0)` and `Z(f - f0)`), return ratio, encirclement count and critical short-circuit ratio of a `gridsim-impedance-stability/1` export with the same gates. The scan forms the return ratio on
the system base: the converter admittance, in per-unit on its rating, is multiplied by `PRatedPu`, so the canonical loop
gain is `Y_conv / SCR` and the verdict and critical SCR do not depend on the rating; the sidecar multiplies by `pRatedPu`
as well. Exit codes: 0 within tolerance, 1 mismatch, 2 NumPy missing, 3 unreadable input or a failed comparison, and 77 when the
export has no `converterParams` (a grid-forming or asset-class run), since no independent derivation ran. A real-network
export carries `converterParams`; the sidecar then checks `Y_conv` and `L` only and says so.
Tolerances are as for the harmonic check, 4096 machine epsilons relative and absolute. The module docstring of
`crossval_impedance.py` records the measured need over 25 grid-following scans (18,165 `Z`, `Y` and `L` comparisons): the
smallest `k` for which `rtol = atol = k eps` passes everything is 25.55 (gb-reduced-29, `Y` at 0.1096 Hz, where the PLL
and current-loop terms nearly cancel), and at most 3.24 on every other scan.

### Quasi-static time series against OpenDSS

`tools/crossval/crossval_qsts_opendss.py` runs a balanced feeder with a regulating transformer and a voltage-controlled
capacitor bank through OpenDSS (DSS C-API 0.14.5) in daily STATIC mode (96 x 15 min) and in TIME mode (1200 x 1 s), and
writes the reference voltages, taps and bank states to `data/qsts`. `QstsOpenDssCrossCheckTests` runs the same feeder in
GridSim and requires every tap and bank operation at the same step and every voltage within 1e-10 pu: the measured
largest difference is 1.8e-11 pu (STATIC) and 7.9e-12 pu (TIME). One deliberate difference in capacitor timer semantics
is pinned in its own test. The feeder, the settings and the traced residual sources are in
[technical ch. 37, section 37.6](<0037 Quasi-static time series and hosting capacity.md>).

### Agreement inside the test suite

- **Dense and sparse power flow: bit identical.** `tests/GridSim.Tests/DenseSparseParityTests.cs` solves each case with
  the dense and the sparse Newton engine and compares the raw bits of every voltage magnitude and angle, injection, line
  flow and loading, total loss, the maximum mismatch, the iteration count, the status and the slack-reactive note, or
  requires both engines to throw the same exception. The corpus is the eight built-in cases (each required to converge),
  24 bundled MATPOWER files, and the JSON cases `uk`, `gb-synthetic` and `gb-full`, each under four option sets (reactive
  limits on, off, flat start, and reactive-limit revert; `gb-full` under the first only), plus reused solver instances and
  a NaN load that must stop both engines at once with status `NonFinite`. The MATPOWER files include the unconverged PGLib
  operating points, so parity holds on a failing solve as well as a passing one. A missing data file fails the test.
  `case9241pegase` is excluded because the dense engine cannot hold it in memory. Parity holds because both engines take
  the same Newton step (`NewtonShell.SolveStep` and `ApplyStep`: one unknown numbering, the CPU direct sparse LU, one
  damped update) and assemble Y-bus, injection and Jacobian exactly ([ch. 3](<0003 Power flow.md>)).
- **Default routing.** `DefaultEngineRoutingTests` proves the routing rule (every CPU power flow routes sparse,
  `DefaultSparseThreshold` 0) number-neutral: `case300`, routed sparse, matches an explicit dense engine bit for bit
  through `SteadyStateSolver`, the contingency screens and the loss-contingency sweep. `SolverRoutingTests` holds the
  one rule.
- **GPU fallback.** GPU arithmetic is outside the bit-identity contract. `GpuFallbackTests` pins that a GPU sparse solve
  that gives up is retried on the exact CPU LU and then matches the CPU path to the bit.
- **Dense and sparse estimation.** `SparseEstimatorTests` forces the sparse backend (`SparseBusThreshold = 0`) and
  asserts exact-truth recovery to below `1e-9` pu and `1e-8` deg, dense-against-sparse agreement on a noisy set to below
  `1e-8` pu and `1e-7` deg, `J` to a relative `1e-6` and equal chi-square verdicts, the same observability rank, and identification of
  the same gross error. The backends are held to that tolerance, not to bit identity. Up to 2,500 state variables the
  sparse backend runs the same numerical rank proof as the dense one; above that it runs the topology sweep only and
  reports `rank = -1`.
- **Method against method.** Huber matches WLS on clean data and shrugs off several gross errors, LAV follows the
  trustworthy majority, and `constrained` honours zero-injection rows exactly while agreeing with WLS elsewhere
  (`SparseEstimatorTests`).
- **The bitwise injection gate.** `SparseEstimatorTests.Neighbour_injection_evaluation_is_bit_identical_to_the_solver_path`
  asserts with `Assert.Equal` on doubles, no tolerance, that the measurement model's neighbourhood-only injection
  evaluation equals the full-scan solver injections at an off-solution state. The skipped terms are exact structural
  zeros, so any drift between the estimator's `h(x)` and the solver's power equations is a copy-divergence defect and a
  hard failure. The equality stays exact.
- **Export determinism.** `SolveExportTests` assert that attaching a trace changes no voltage and that the export JSON is
  byte for byte reproducible for a deterministic solve and a fixed `meta` block (a CLI export's `meta` carries the run's
  UTC time, elapsed milliseconds and machine, so it differs between runs).
- **Solver properties.** `SolverPropertyTests` checks, on the eight built-in cases (case9, 14, 30, 57, 118, 300, 24 and
  145), that net injections sum to the total loss, generation equals load plus loss, line losses plus shunts reconcile
  with the total, exactly one slack closes the balance, dense and sparse agree, repeated solves are bit for bit
  identical, and flat and warm starts reach one solution. Per-branch real loss is checked non-negative on the six
  well-behaved cases only: case24 and case145 are excluded, case145 carries a negative-resistance branch. Five library
  files (case14, 30, 57, 118, 300 under `data/matpower/lib`) and three PEGASE files are parsed and checked for the
  conservation laws and non-negative total loss; those tests return without asserting when the file is absent from the
  checkout.
- **No unconverged state flows into a study.** `ConvergenceContractTests` pins the `RequireConverged` gate, the on-load
  tap-changer loop's settled flag, the reactive-limit pass cap, the DC-OPF outcome and contingency solver errors.

### The physics audit and the lake

The physics-law audit ([ch. 16](<0016 Physics conformance and the constrained estimator.md>)) is the same instrument as the NumPy audit in the
language of laws, and `physics_audit.py` is its cross-language backstop. `tools/crossval/lake_conformance.py` runs the
audit over the stored GDA lake, comparing `S = V conj(Y V)` with each period's stored injection (the states are
demand-scaled per period, so the static case's `Pg - Pd` is the wrong reference). The committed
`tools/crossval/lake-conformance.csv` records 98 sampled gb-spine periods of which 93 are physically admissible, with the
worst residual in the fully admissible rows 1.84e-9 MVA, and five anomalous presolved periods (one in 2019, four in 2026). The script
exits 0 once the sweep ran (including when no stored period matched, which it reports on the error stream) and 2 when no
lake is configured, the lake directory is missing, or NumPy or pyarrow is missing; its lake defaults to
`$GDA_DATA_ROOT/Derived`, else `$GDA_ROOT/Derived` (both variables name the DataSources folder itself, as the CLI reads them;
a set variable that is not a folder exits 2 naming the variable).

### Expected non-convergence

`gridsim bench` sweeps the corpus against every available engine. Two classes of non-convergence are properties of the input and not solver defects: PGLib-OPF operating points (optimisation solutions that a flat-start power
flow does not reach, while their non-OPF twins solve) and radial distribution feeders (backward and forward sweep
territory). `gridsim conformance` lists the three PGLib cases that do not converge and exits 3 for them
([ch. 16](<0016 Physics conformance and the constrained estimator.md>)). `OOM` is a dense-path memory ceiling recorded only when the allocation
throws. Benchmarking is covered in [user manual ch. 28, Benchmarking and GPU](<../manual/0028 Benchmarking and GPU.md>).

### The build

`dotnet build GridSim.slnx -c Release` produces 0 warnings and 0 errors: `Directory.Build.props` turns on
`TreatWarningsAsErrors` and the .NET analysers. `dotnet test GridSim.slnx -c Release --no-build` fails on any failed test.
A skipped test does not fail the run. The suite carries two kinds of skip:
`DcOpfSecuritySimplexParityTests` has one on-demand test (the gb-full security DC-OPF), and the
`PublishedOutputFact` tests skip unless `GRIDSIM_PUBLISHED_DIR` names a published release folder.

## Parameters and defaults

The gate of an instrument is the value that decides its exit code. The observed values are from this chapter's reference
run (pandapower 3.5.5, recorded in `docs/_captures/validation/pandapower_version.txt`; the Release build; the seeded
synthetic sets); the last digits depend on the pandapower version.

| Instrument | Gate | Default | Unit | Source symbol |
|---|---|---|---|---|
| `crossval.py` | `max dVm` | 4096 machine epsilons, 9.09e-13; a case in `ALLOWANCES` its own bound | pu | `--tol`, `DEFAULT_TOL` in `crossval.py` |
| `crossval.py` | `max dVa` | the same number in rad | rad | `diff` in `crossval.py` |
| `residual_check.py` | `max dP`, `max dQ` | `BaseMva * 1e-8` (1e-6 on 100 MVA; an absolute `1e-6` before D-DOC-125) | MVA | `--tol` in `residual_check.py`, `sidecar_common.default_tol_mva` |
| `residual_check.py` | PV reactive-limit slack | `max(1e-3, tol)` | MVAr | `tolq` in `residual_check.py` |
| `physics_audit.py` | energy, KCL, `S = VI` | `BaseMva * 1e-8` (an absolute `1e-4` before D-DOC-125; the Feasible gate's own `AuditTolMva` for a `zero-injection-constraints` export) | MVA | `--tol` in `physics_audit.py`, `sidecar_common.default_tol_mva` |
| `crossval_estimation.py` | `max dVm`, `max dVa` | `1e-6`, `1e-5` (two iterations with different stopping rules; see above) | pu, deg | `--tol-vm`, `--tol-va` |
| `se_residual_check.py` | honesty, optimality step | `1e-6` | eng. unit; pu, rad | `--tol` |
| `se_residual_check.py` | objective `abs(dJ)` | `max(1e-6, 1e-9 * max(J, 1))` | - | `main` in `se_residual_check.py` |
| `se_residual_check.py` | analytic Jacobian (`--fd`) | `1e-6` | - | `main` in `se_residual_check.py` |
| `se_residual_check.py` | critical-flag leverage | `K > 1 - 1e-6` | - | `main` in `se_residual_check.py` |
| `crossval_freq.py` | RoCoF, nadir | `0.02`, `0.05` | Hz/s, Hz | `--tol-rocof`, `--tol-nadir` |
| `crossval_classical.py` | rotor angle | `1e-3` (fixed-step RK4 against adaptive RK45: truncation, not rounding) | rad | `--tol` |
| `crossval_smallsignal.py` | eigenvalue, relative | `rtol * max(1, cond(V))`, `rtol` = 4096 machine epsilons (`9.09e-13`) | - | `--rtol` |
| `crossval_h0.py` | inertia arithmetic | `1e-9 * max(1, e_real)` | MW s | `TOL_E_REL` |
| `crossval_h0.py` | bare RoCoF; forming ODE | `1e-9` absolute; `0.02`, `0.05` | Hz/s; Hz/s, Hz | `TOL_ROCOF`, `TOL_NADIR` |
| `crossval_harmonics.py` | relative, absolute | 4096 machine epsilons each (`9.09e-13`) | pu | `--tol`, `--atol` |
| `crossval_impedance.py` | relative, absolute | 4096 machine epsilons each (`9.09e-13`) | pu | `--tol`, `--atol` |
| `lake_conformance.py` | KCL, `S = VI`, energy | `1e-3` | MVA | `--tol` |
| `batchsolve` | agreement with the CPU reference | `1e-6` | - | `CliDefaults.BatchedSolveAgreementTolerance` |
| `physics-check`, `conformance` | hard laws | `1e-4` | MVA | `--tol` |
| Dense and sparse power flow | every result bit | exact | - | `DenseSparseParityTests` |
| pandapower WLS reference | tolerance, iterations | `1e-8`, 50 | - | `run_estimation` |

## Invariants

| Invariant | Pinned by |
|---|---|
| case9 matches the published MATPOWER solution (Vm to 3 decimals, Va to 2, slack 71.64 MW to 1 decimal, loss 4.641 MW to 2 decimals), converging in at most 6 iterations below `1e-8` | `PowerFlowTests.Case9_converges`, `Case9_bus_voltages_match_published_solution`, `Case9_slack_and_losses_match_known_values` |
| case9 carries the published H, machine base, stored energy and system total, and the MATPOWER branch ratings | `Case9InertiaTests` |
| Dense and sparse Newton-Raphson are bit identical on every case the dense engine can hold, including failing solves | `DenseSparseParityTests` |
| The power-flow routing (always sparse on the CPU) is one rule and number-neutral against the dense engine | `SolverRoutingTests`, `DefaultEngineRoutingTests` |
| A GPU sparse solve that gives up falls back to the exact CPU LU, bit identical to the CPU path | `GpuFallbackTests` |
| Converged solutions on the built-in cases obey power balance, one closing slack and determinism, and the well-behaved cases carry non-negative branch loss | `SolverPropertyTests` |
| No unconverged state is reported as success | `ConvergenceContractTests` |
| Dense and sparse estimation agree to `1e-8` pu and `1e-7` deg with equal verdicts, rank and removals | `SparseEstimatorTests` |
| The measurement model's injection evaluation equals the solver's injections bit for bit | `SparseEstimatorTests.Neighbour_injection_evaluation_is_bit_identical_to_the_solver_path` |
| The solve and estimate export fields the sidecars read are present and unchanged | `CrossvalContractTests`, `EstimateExportContractTests`, `PhysicsExportContractTests` |
| `crossval` exit 77 is a skip and a sidecar exit 2 is a failure; the estimation sidecars check kinds, and refuse unknown ones, before the dependency check | `CliFailLoudFlagContractTests.Crossval_skip_is_exit_77_and_a_python_exit_2_is_a_failure`, `CrossvalSidecarExitContractTests` |
| The solve export is observational and byte for byte reproducible (apart from its `meta` block) | `SolveExportTests` |
| The Release build has 0 warnings and the suite has no failed test | `Directory.Build.props`, the test run |

## Worked example

`gridsim crossval case14` solves the IEEE 14-bus case in 2 Newton iterations, writes the export and runs the pandapower
sidecar. The diff reads (captured, ch. 27):

```
=== Cross-validation vs pandapower: case14 ===
  buses compared: 14
  max |delta Vm|: 5.480E-012 pu   max |delta Va|: 0.0000 deg
  tolerance: 1.0E-003   -> AGREES
```

`crossval-diff.json` holds the unrounded angle difference, 2.62e-10 deg. The independent residual check on the same
export reads `max_p_mismatch_mw` 2.01e-09 and `max_q_mismatch_pq_mvar` 1.32e-08 with no reactive-limit violation, and
`physics_audit.py` reads energy 4.01e-13 MW, KCL 2.01e-09 MW, `S = VI` 1.32e-08 MVAr. The residual check passes with a
factor of 76 to spare against its `1e-6` line, and the physics audit with a factor of 7,600 against its `1e-4` line.

The six classic cases, one pandapower run each:

| Case | Buses | max `dVm` (pu) | max `dVa` (deg) | `residual_check` max dP (MW) | max dQ (MVAr) |
|---|---|---|---|---|---|
| case9 | 9 | 4.44e-16 | 2.31e-14 | 1.66e-12 | 1.53e-12 |
| case14 | 14 | 5.48e-12 | 2.62e-10 | 2.01e-09 | 1.32e-08 |
| case30 | 30 | 1.89e-15 | 4.66e-14 | 9.57e-08 | 6.35e-08 |
| case57 | 57 | 2.04e-12 | 1.13e-10 | 3.45e-10 | 1.35e-10 |
| case118 | 118 | 6.66e-16 | 1.87e-13 | 4.51e-12 | 2.81e-12 |
| case145 | 145 | 3.69e-14 | 1.48e-12 | 1.35e-07 | 2.29e-08 |

The residual column is the machine-epsilon claim in its absolute form: balances close to 1.4e-7 MVA at worst on these six,
against a pass line of `1e-6`.

Where the second tool fails, the residual arbitrates. On case300 pandapower does not converge from a flat start or from a
DC start (sidecar exit 4, verb exit 3: `Power Flow nr did not converge after 100 iterations`), and the residual check on
GridSim's state reads 1.79e-11 MW and 9.03e-11 MVAr. On case24 pandapower converges to a different state (max `dVm`
8.02e-02 pu, max `dVa` 1.04 deg, verb exit 3, `MISMATCH`), and GridSim's state closes to 6.52e-10 MW and 3.01e-10 MVAr.
In both cases the equations are satisfied by GridSim's state; the disagreement is the second tool's, and the residual is
the evidence. The case24 cause is pandapower's reactive-limit switching on multi-generator buses.

The estimation chain, `gridsim estimate case14 --synth` (122 measurements, 27 states, 95 degrees of freedom, `J` =
80.01 against a chi-square threshold of 118.75):

| Check | Reading |
|---|---|
| `crossval_estimation.py` | `max dVm` 5.56e-12 pu, `max dVa` 7.74e-11 deg |
| `se_residual_check.py` honesty | 3.877e-13 |
| `se_residual_check.py` objective | recomputed `J` 80.010522, `abs(dJ)` 2.132e-13 |
| `se_residual_check.py` optimality | independent step 5.736e-12 |
| `se_residual_check.py --fd` | analytic Jacobian within 6.594e-08 of central differences |

The same checks on case30 and case118 (`gridsim estimate <case> --synth`, default seed; captures
`crossval_estimation_case30.txt`, `crossval_estimation_case118.txt`, `se_residual_check_case30.txt` and
`se_residual_check_case118.txt`) read `crossval_estimation.py` 4.44e-16 pu and 2.18e-14 deg (case30) and 2.36e-13 pu and
5.15e-11 deg (case118), honesty 1.399e-12 and 7.081e-12, optimality 3.120e-11 and 3.623e-14.

The swing check, `replay-event gb-largest-loss-1320` then `crossval_freq.py`: GridSim initial RoCoF -0.165 Hz/s against
SciPy -0.16484 (difference 1.59e-4), nadir 49.2420 Hz against 49.2418 (difference 2.16e-4), against gates of 0.02 Hz/s and
0.05 Hz.

## Tolerance summary

What a maintainer must preserve. The expectation is the observed worst value on the reference cases; a regression is a
value that moves toward the gate.

| Check | Instrument | Gate | Expectation |
|---|---|---|---|
| case9 solve | MATPOWER published solution (`PowerFlowTests`) | Vm 3 decimals, Va 2 decimals, loss 4.641 MW to 2 decimals | exact to those decimals; 4 iterations to 1.6e-14 pu |
| Any converged solve | `residual_check.py` | `1e-6` MVA, no reactive-limit violation | at most 1.4e-7 MVA on the six classic cases and 1.8e-11 MW on case300 |
| Dense and sparse power flow | `DenseSparseParityTests` | exact | every bit, iteration count and status equal |
| Default routing | `DefaultEngineRoutingTests`, `SolverRoutingTests` | exact | routed solves bit identical to dense |
| Power flow against pandapower | `crossval.py` | 4096 machine epsilons (9.09e-13) on `Vm` pu and `Va` rad; a listed case its own allowance | at most 1.9e-14 pu and 1.2e-14 rad on case9, 14, 30, 57, 118, 145; 5.2e-14 pu and 8.7e-14 rad on case300; 2.6e-13 pu and 3.9e-13 rad on gb-full |
| Estimation against pandapower | `crossval_estimation.py` | 1e-6 pu; 1e-5 deg | at most 5.6e-12 pu and 7.7e-11 deg on case14, 30, 118 |
| Estimate honesty and optimality | `se_residual_check.py` | 1e-6 | honesty at most 7.1e-12, optimality step at most 3.1e-11 on case14, 30, 118 |
| Swing against SciPy RK45 | `crossval_freq.py` | 0.02 Hz/s; 0.05 Hz | 1.6e-4 Hz/s and 2.2e-4 Hz on `gb-largest-loss-1320` |
| Classical swing against SciPy | `crossval_classical.py` | 1e-3 rad | 3.7e-8 rad on case9 |
| Small-signal eigenvalues | `crossval_smallsignal.py` | 4096 machine epsilons `* cond(V)` relative | 3.1e-8 relative on case9, `cond(V)` 1.9e10; worst need `max_rel / cond(V)` 0.2165 eps (gb `--damping 2`) over the bundled `modal` cases (script docstring) |
| Impedance-stability rebuild | `crossval_impedance.py` | 4096 machine epsilons relative and absolute | worst need 25.55 eps (gb-reduced-29, Y at 0.11 Hz), at most 3.24 eps elsewhere, over 25 grid-following scans (script docstring) |
| QSTS against OpenDSS | `crossval_qsts_opendss.py`, `QstsOpenDssCrossCheckTests` | 1e-10 pu; every tap and bank operation at the same step | 1.8e-11 pu daily STATIC, 7.9e-12 pu 1 s TIME (one pinned semantics difference, ch. 37) |
| Harmonic penetration | `crossval_harmonics.py` | 4096 machine epsilons relative and absolute (9.09e-13) | 9.7e-17 pu uk, 1.1e-16 pu gb-synthetic (script docstring) |
| Dense and sparse estimation | `SparseEstimatorTests` | 1e-8 pu, 1e-7 deg | same `J`, chi-square verdict, rank and removals |
| Injection evaluation | the bitwise gate | exact | every bit equal |
| Monte-Carlo sweep | `mc_estimate_sweep.py` | none (analysis) | error scales 2.34 with noise 2; chi-square 0 of 10 at noise 2 and 10 of 10 elsewhere |
| Physics-law audit | `physics-check`, `conformance` | 1e-4 MVA | 24 of 24 converged corpus cases admissible, worst 2.2e-7 MVA |
| Lake conformance | `lake_conformance.py` | 1e-3 MVA | 93 of 98 sampled periods admissible, worst residual in the fully admissible rows 1.84e-9 MVA |
| Build and suite | `dotnet build`, `dotnet test` | 0 warnings; 0 failed | green |

## Source map

| File | Type | Responsibility |
|---|---|---|
| `tools/crossval/crossval.py` | script | pandapower power-flow diff; `build_ppc` is shared by the estimation sidecar |
| `tools/crossval/residual_check.py` | script | independent admittance matrix and power-balance residual; `build_ybus` is shared |
| `tools/crossval/physics_audit.py` | script | independent recomputation of laws 1, 6 and 8 (energy, KCL, complex power) and the exported-gate check |
| `tools/crossval/crossval_estimation.py` | script | pandapower WLS diff and the bad-data count check |
| `tools/crossval/se_residual_check.py` | script | independent honesty, objective, optimality and leverage checks |
| `tools/crossval/mc_estimate_sweep.py` | script | Monte-Carlo accuracy and chi-square calibration sweep |
| `tools/crossval/crossval_freq.py` | script | SciPy RK45 swing re-integration |
| `tools/crossval/crossval_classical.py`, `crossval_smallsignal.py`, `crossval_h0.py` | scripts | classical swing, eigenvalue and H=0 study checks |
| `tools/crossval/crossval_harmonics.py`, `crossval_impedance.py` | scripts | NumPy harmonic and impedance-stability rebuilds |
| `tools/crossval/lake_conformance.py`, `lake-conformance.csv` | script, data | the audit over the stored GDA lake |
| `src/GridSim.Cli/Program.cs` | `RunCrossval`, `RunConformance`, `RunPhysicsCheck`, `RunBatchSolve`, `RunSimdCheck` | the verbs that drive the instruments |
| `src/GridSim.Core/IO/SolveExport.cs`, `EstimateExport.cs`, `FrequencyExport.cs` | writers | the export documents the sidecars read |
| `tests/GridSim.Tests/DenseSparseParityTests.cs` | tests | dense and sparse bit identity |
| `tests/GridSim.Tests/SparseEstimatorTests.cs` | tests | estimator backend and method agreement, the bitwise gate |
| `tests/GridSim.Tests/PowerFlowTests.cs`, `Case9InertiaTests.cs` | tests | the MATPOWER ground truth |

## Limits

- `crossval.py`, `residual_check.py` and `physics_audit.py` treat every load as constant power and ignore the export's
  `zip` block, so on a case with a voltage-dependent load they report a mismatch against a correct solve.
- The `residual_check.py` pass line is an absolute `1e-6` MVA, so the `uk` model, whose flows reach the thousands of MW,
  reads 1.06e-6 MW real and 2.48e-6 MVAr reactive mismatch and fails at the default.
- The solve-export sidecars read `meta.case` while the export writes `meta.Case`, so their reports print `"case": "?"`.
- `crossval_smallsignal.py` gates at `rtol * max(1, cond(V))`, so on a near-defective state matrix it is far looser than
  on a well-conditioned one: case9 (`cond(V)` 1.9e10) gets a threshold of 0.0175 against a measured 3.1e-8, and the
  verdict alone cannot tell an exact agreement from a loose one. Judge it by `max_relative_eig_diff` and `condV`.
- `crossval_estimation.py` exits 2 when pandapower is absent, the code Python uses for a usage error, so a skip cannot be
  told from a bad argument; `se_residual_check.py` and `crossval_harmonics.py` do not guard their imports and a missing
  package ends in a traceback and exit 1.
- The `--bad-data` comparison counts removals and does not compare which measurement each tool removed.
- `crossval_freq.py` does not check a frequency export that carries recorded response services or an offset start; it
  exits 77.
- Pandapower does not reproduce GridSim's state on case24 and does not converge on case300; the residual check is the
  arbiter in those cases.

## See also

- [ch. 16, Physics conformance and the constrained estimator](<0016 Physics conformance and the constrained estimator.md>) - the ten laws and their gate.
- [ch. 3, Power flow](<0003 Power flow.md>) - the solver whose solutions these instruments check.
- [ch. 6, State estimation](<0006 State estimation.md>) - the estimator, its methods and bad-data machinery.
- [ch. 9, GDA integration](<0009 GDA integration.md>) - the lake products, including the closure reports that audit the estimated history.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the export contracts the sidecars consume.
- [ch. 27, Validation tools](<../manual/0027 Validation tools.md>) - running these instruments.
- [ch. 30, Troubleshooting](<../manual/0030 Troubleshooting.md>) - the non-convergence list as symptoms and causes.
