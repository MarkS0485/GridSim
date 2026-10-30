# 21. State estimation

A power flow needs a complete, exactly determined specification: real and reactive power at every bus, and a voltage
at every generator. Real telemetry never looks like that. A control room holds some voltage magnitudes, some bus
injections, some branch flows and sometimes phase-angle meters, each carrying noise, and some of them wrong.
State estimation fits one network state (a voltage magnitude and angle at every bus) to whatever measurements exist,
weighting each by its declared standard deviation, and uses the surplus of measurements over unknowns to average the
noise down. It reports how well the fit agrees with the noise the meters promised (chi-square), finds and removes
gross errors, and says so plainly when the measurements cannot determine the state at all. `gridsim estimate` is the
verb. Use it when you hold redundant telemetry for a network and want a corroborated state, when you want to test how
a meter layout, a noise level or a bad reading changes the answer, or when you want estimated states to feed replay
([ch. 23](<0023 Replaying estimated states.md>)). It reconstructs the past only: a measurement set dated within
the last 169 hours is refused (see [Pitfalls](#pitfalls)).

## Before you start

- A case: a built-in name (`case9`, `case14`, `case30`, `case57`, `case118`, `case300`), a MATPOWER `.m` file (an absolute
  path, or a path under `data/matpower/` such as `pegase/case1354pegase.m`), or a
  JSON case folder holding `system.json` such as `data/gb-full` (see
  [ch. 2, Loading networks](<0002 Loading networks.md>)).
- Measurements, supplied in one of two ways: `--measurements <file>`, a `gridsim-measurements/1` JSON file (see
  [The measurement file](#the-measurement-file)), or `--synth`, which solves the case, samples a noisy measurement set
  from the solution and estimates from that. `--synth` is the self-test and the demonstration: it needs no data and
  it scores the estimate against the truth it came from.
- No Python is needed. Python and pandapower are needed only by the cross-check scripts in
  [ch. 27](<0027 Validation tools.md>).

Every flag of `gridsim estimate` is in [ch. 19, CLI reference](<0019 CLI reference.md#estimate>). This chapter shows the
flags in use and explains what each one changes in the result.

## Worked example

Estimate the IEEE 14-bus case from a synthetic measurement set:

```bash
gridsim estimate case14 --synth
```

```
$ gridsim estimate case14 --synth

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\estimate-case14
[exit 0]
```

The estimator converged in 5 iterations, the 122 measurements (14 voltage magnitudes, 28 bus injections and 80 branch
flows) are consistent with their declared sigmas, and the estimate lies within 1.33E-003 pu and 0.225 degrees of the
power-flow solution the measurements were sampled from. The exit code is 0 because the estimate converged and the
network is observable.

## Reading the output

The summary has a fixed layout. Lines that do not apply are left out.

**Header.** `=== WLS state estimate: case14 ===` carries the case argument exactly as typed (`data/gb-full` prints as
`data/gb-full`) and names the method that ran: `WLS`, `Huber M-estimator`, `LAV (linear programming)`, `LAV by IRLS
(approximate)`, `Schweppe GM`, `WLS (QR)`, `Constrained WLS` or `Feasible (physics-constrained) WLS`.

**Status line.** `status: Converged   iterations: 5   max |dx|: 2.72E-009`.

- `status` is one of `Converged`, `MaxIterations` (the iteration cap was reached), `NonFinite` (a NaN or infinity
  appeared in the state), `Unobservable` (the measurements cannot determine the state) or `Infeasible` (only
  `--method feasible`: the converged state breaks a physical law, or the constrained solve fails on a set that is
  observable). Only `Converged` is a result. Every other status
  exits 3, and the state it leaves behind must not be used.
- `iterations` counts Gauss-Newton iterations over the whole run: every bad-data re-estimation pass and, for the robust
  methods, every reweighting pass are added in. The cap, `--max-iter`, default 30, applies to each Gauss-Newton solve.
- `max |dx|` is the largest component of the last step applied to the state, angles in radians and magnitudes in
  per-unit. The run converges when it falls below `--tol`, default `1e-8`. A step is capped at 0.2 rad per angle and
  0.1 pu per magnitude, with the whole step scaled and its direction kept, so a poor start cannot throw a voltage
  through zero. The caps do not bind near the optimum. With `--max-iter 2` the run stops early and reports it:

```
$ gridsim estimate case14 --synth --max-iter 2 --out results/err

=== WLS state estimate: case14 ===
  status: MaxIterations   iterations: 2   max |dx|: 8.42E-002
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)

  -> results\err
[exit 3]
```

**Size line.** `measurements: 122   states: 27   redundancy (dof): 95`.

- `measurements` is the number of rows in the set as it was supplied, after any `--phaseangle` rows were added and
  before any bad data was removed.
- `states` is the number of unknowns: an angle at every bus except the slack, and a magnitude at every bus. case14
  has 13 angles and 14 magnitudes.
- `redundancy (dof)` is the measurements left after removal minus the states. It is the degrees of freedom of the
  chi-square test. When bad data was removed it is smaller than `measurements` minus `states`: in the planted-error
  run under [Bad data](#bad-data) the line reads `measurements: 122`, `redundancy (dof): 92` because three rows were
  removed. Zero or negative redundancy means the fit is an interpolation, not a corroborated estimate.

**Observability line.** `observability: observable (rank 27/27)`. The estimator proves the measurements determine
every state by factoring the gain matrix `H^T W H` and counting its independent directions; `27/27` is the rank over
the number of states. The proof runs on the dense backend (`wls`, `huber`, `lav` and `schweppe-gm` up to 200 buses), for `qr`, `constrained`
and `feasible` on every case they accept (up to 3,000 buses), and on the sparse backend up to 2,500 states.
Above 2,500 states on the sparse backend only a topology check for unmeasured islands runs, and the rank prints as `-1` (see
[Larger cases](#a-json-case-folder-and-larger-cases)). When the set does not determine the state the line reads
`UNOBSERVABLE` and names what is free:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-thin.json --out results/case9-thin

=== WLS state estimate: case9 ===
  status: Unobservable   iterations: 0   max |dx|: Infinity
  measurements: 13   states: 17   redundancy (dof): -4
  observability: UNOBSERVABLE
    free state directions: Vm@bus 5, Vm@bus 7, Vm@bus 8, Vm@bus 9

  -> results\case9-thin
[exit 3]
```

This set holds one voltage meter and the injections at six buses of case9, and nothing else. The four voltage
magnitudes the estimator names (`Vm@bus 5`, `7`, `8`, `9`) are the directions the data cannot pin down: add voltage or
flow meters near those buses and run again. An unobservable run stops before it iterates (`iterations: 0`, `max |dx|`
at its starting value of infinity), prints no consistency line, writes the output folder anyway, and exits 3. A line
`unmeasured island buses: ...` appears when whole electrical islands carry no measurement.

**Consistency line.** `consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS`.

- `J` is the weighted sum of squared residuals, the sum over measurements of `((z - h(x)) / sigma)^2`. If the
  measurements carry exactly the noise their sigmas declare, `J` lands near the degrees of freedom (95 here).
- `chi2(95%)` is the 95 % point of the chi-square distribution for those degrees of freedom. The confidence level is
  fixed at 95 %.
- `PASS` means `J` is at or below the threshold: the residuals look like the noise that was promised and the estimate
  is statistically credible. `FAIL` means at least one reading is wrong, at least one sigma is too optimistic, or the
  network model does not match the network that was metered. A `FAIL` does not change the exit code (see
  [Exit codes](#exit-codes)); read the line.
- With no redundancy the line reads `not run (no redundancy: dof <dof>; J = <J>)`: the test did not run, so there is
  no verdict (and the export's `chiSquarePassed` is `null`, never `true`). With `--no-bad-data` the test is switched
  off and the line reads `not tested (chi-square not run: --no-bad-data; J = <J>)`, giving `J` without a verdict. The
  audit log records either as `chi2=not-run`.
- A robust method (`huber`, `lav`, `schweppe-gm`) whose reweighting loop stopped at its cap before the weights settled
  adds the line `WARNING: the robust re-weighting (IRLS) reached its cap before the weights settled; ...`. The status
  stays `Converged`; the state is the solve under the last weights, not a settled robust estimate.

**Bad-data lines.** One line per removal, for example `removed bad data (pass 0): VmPu @ bus 9  rN = 18.3`: the
measurement kind, its location, and the normalised residual that condemned it. The pass number counts from 0. The
`--hti` form and the closing `bad-data method:` line are described under [Bad data](#bad-data). A line
`critical measurements (sole observers, unremovable, errors undetectable): N` appears when `N` rows are the only
observer of some state direction; see [Critical measurements](#critical-measurements).

**Truth line.** `vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg`, printed only for
`--synth`. It is the largest difference over all buses between the estimated state and the solved power-flow state the
measurements were sampled from, magnitudes in per-unit and angles in degrees. It is the one place the output states
how wrong the estimate is. A real measurement set has no truth line. With `--noise 0` the sampled values are exact
and the estimate recovers the truth to machine precision:

```
$ gridsim estimate case9 --synth --noise 0 --out results/case9-exact

=== WLS state estimate: case9 ===
  status: Converged   iterations: 4   max |dx|: 6.07E-010
  measurements: 63   states: 17   redundancy (dof): 46
  observability: observable (rank 17/17)
  consistency: J = 0.00 vs chi2(95%) = 62.83 -> PASS
  vs synthetic truth: max |dVm| = 2.220E-016 pu   max |dVa| = 3.553E-015 deg

  -> results\case9-exact
[exit 0]
```

`J` reads `0.00` because every residual is zero, and the state differs from the truth by 2.2E-016 pu and
4.4E-015 degrees.

**Output path.** The last line names the output folder. The default is `results/estimate-<case>` under the folder that
holds the GridSim executable, which is why the worked example above prints a path through `src\GridSim.Cli\bin\...`.
`--out <dir>` puts the folder anywhere, and the other captures in this chapter use it. The timings, hashes and paths in the captured output differ by machine and
by run.

### The output folder

```
results/estimate-case14/
|-- estimate-export.json         the whole run: network, measurements, state, residuals, observability, removals
|-- residuals.csv                one row per measurement that took part in the final fit
|-- run-metadata.json            when and on what the run executed, and what ran
|-- measurements.json            --synth only: the sampled set, ready to feed back with --measurements
|-- estimated-solve-export.json  --export-solve only, converged runs only: the estimate as a solve export
|-- hti-removals.csv             --hti only: what hypothesis-testing identification removed
`-- fdia-screen.json             --fdia-screen only: the screen's verdict
```

`estimate-export.json` has the schema `gridsim-estimate-export/1`. Its top-level sections are `meta`, `model` (buses,
generators and branches as estimated), `measurements`, `estimate` (status, `iterations`, `maxStateUpdate`,
`objectiveJ`, `degreesOfFreedom`, `chiSquareThreshold`, `chiSquarePassed` (`true` / `false` when the test ran, `null`
when it did not), `chiSquareTest`, `irlsCapReached` and the state per bus),
`residuals`, `observability`, `removed`, `trace` (only with `--trace`: per iteration the pass, the largest update and
the step scale) and `physics` (the physical-law audit of the estimated state). Chapter 31 gives the field guide
([ch. 31](<0031 Exports and formats.md>)); the independent checkers in [ch. 27](<0027 Validation tools.md>)
read this file.

`residuals.csv` begins with a block of `#` comment lines describing the run, then the header

```
kind,location,value,sigma,predicted,residual,normalized_residual,critical
```

and one row per measurement in the final fit. `value`, `predicted` and `residual` are in the measurement's own unit
(pu, MW, MVAr or degrees); `residual` is value minus predicted. `normalized_residual` is `|r| / sqrt(Omega)`, with
`Omega` the residual variance after the fit (see [Bad data](#bad-data)), to three decimals; it is `NaN` on a critical
row, on every row of a `--no-bad-data` run, on every row of a `--lean` run whose chi-square test passed, on every row of
a `huber`, `lav` or `schweppe-gm` run, and on every row of a set with no redundancy (`dof <= 0`).
`critical` is `True` for a sole-observer row. A row that bad-data removal deleted is not in this file; it is in the
`removed` section of the export.

`estimated-solve-export.json` projects the converged estimate into the `gridsim-solve-export/1` shape (injections,
losses and flows evaluated at the estimated state), so replay and the dashboards read an estimated state exactly as
they read a solved one. `run-metadata.json` records the timestamp, the commit and build, the backend, the SIMD tier,
the host, and a one-line description of the run (`estimate  case=case14  measurements=122  source=synthetic`).

## The measurement file

A measurement file is JSON, camelCase, with the schema `gridsim-measurements/1`. Here is a small one, written by hand
for case9: 24 readings from a control room that meters four voltage magnitudes, six real and six reactive injections, three real
and two reactive flows, one current and two PMU angles.

```json
{
  "schema": "gridsim-measurements/1",
  "case": "case9",
  "baseMva": 100.0,
  "takenUtc": "2024-03-01T12:00:00Z",
  "note": "hand-authored demonstration telemetry for case9",
  "measurements": [
    { "kind": "VmPu",     "value": 1.040,  "sigma": 0.004, "bus": 1, "source": "scada" },
    { "kind": "VmPu",     "value": 1.026,  "sigma": 0.004, "bus": 2, "source": "scada" },
    { "kind": "VmPu",     "value": 1.012,  "sigma": 0.004, "bus": 5, "source": "scada" },
    { "kind": "VmPu",     "value": 0.996,  "sigma": 0.004, "bus": 9, "source": "scada" },

    { "kind": "PInjMw",   "value": 71.9,   "sigma": 1.0, "bus": 1, "source": "scada" },
    { "kind": "PInjMw",   "value": 162.2,  "sigma": 1.0, "bus": 2, "source": "scada" },
    { "kind": "PInjMw",   "value": 85.6,   "sigma": 1.0, "bus": 3, "source": "scada" },
    { "kind": "PInjMw",   "value": -89.4,  "sigma": 1.0, "bus": 5, "source": "scada" },
    { "kind": "PInjMw",   "value": -100.7, "sigma": 1.0, "bus": 7, "source": "scada" },
    { "kind": "PInjMw",   "value": -124.1, "sigma": 1.0, "bus": 9, "source": "scada" },

    { "kind": "QInjMvar", "value": 26.5,   "sigma": 1.0, "bus": 1, "source": "scada" },
    { "kind": "QInjMvar", "value": 7.4,    "sigma": 1.0, "bus": 2, "source": "scada" },
    { "kind": "QInjMvar", "value": -10.2,  "sigma": 1.0, "bus": 3, "source": "scada" },
    { "kind": "QInjMvar", "value": -30.6,  "sigma": 1.0, "bus": 5, "source": "scada" },
    { "kind": "QInjMvar", "value": -34.2,  "sigma": 1.0, "bus": 7, "source": "scada" },
    { "kind": "QInjMvar", "value": -50.8,  "sigma": 1.0, "bus": 9, "source": "scada" },

    { "kind": "PFlowMw",   "value": 31.2,  "sigma": 1.6, "from": 4, "to": 5, "end": "From", "source": "scada" },
    { "kind": "QFlowMvar", "value": 1.5,   "sigma": 1.6, "from": 4, "to": 5, "end": "From", "source": "scada" },
    { "kind": "PFlowMw",   "value": 87.3,  "sigma": 1.6, "from": 8, "to": 9, "end": "From", "source": "scada" },
    { "kind": "QFlowMvar", "value": -8.9,  "sigma": 1.6, "from": 8, "to": 9, "end": "From", "source": "scada" },
    { "kind": "PFlowMw",   "value": -23.5, "sigma": 1.6, "from": 6, "to": 7, "end": "To",   "source": "scada" },

    { "kind": "IMagPu",    "value": 0.7549, "sigma": 0.005, "from": 7, "to": 8, "end": "From", "source": "scada" },

    { "kind": "VaDeg",     "value": 4.7,   "sigma": 0.5, "bus": 3, "source": "pmu" },
    { "kind": "VaDeg",     "value": -3.9,  "sigma": 0.5, "bus": 9, "source": "pmu" }
  ]
}
```

It runs like any other set:

```bash
gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry.json
```

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry.json --out results/case9-telemetry

=== WLS state estimate: case9 ===
  status: Converged   iterations: 8   max |dx|: 1.47E-009
  measurements: 24   states: 17   redundancy (dof): 7
  observability: observable (rank 17/17)
  consistency: J = 1.11 vs chi2(95%) = 14.05 -> PASS

  -> results\case9-telemetry
[exit 0]
```

24 readings, 17 states, 7 degrees of freedom. The set is observable, `J = 1.11` is well inside the 14.05 threshold,
and the estimate took 8 iterations from the flat start. The sampled set that `--synth` writes to `measurements.json`
has the same shape and can be edited, kept, and fed back with `--measurements`.

### Top-level fields

| Field | Meaning |
|---|---|
| `schema` | Must be the string `gridsim-measurements/1`. Any other value is refused and the run exits 1. |
| `case` | The case name the set describes. Informational: it is not checked against the case on the command line. |
| `baseMva` | The system base the authoring side used, in MVA. Defaults to 100 when omitted. It must equal the model's base to within 1e-9 MVA, or the set is refused. |
| `takenUtc` | When the telemetry was taken, ISO-8601 UTC. Required: an undated set is refused with `REJECTED (forward inference): ...` and exit 2, and so is a set less than 169 hours (7 days and 1 hour) old. It is also the instant `--phaseangle` rows are projected to. |
| `note` | Free text provenance. Carried into the export. |
| `measurements` | The array of readings. |

### Fields of a reading

| Field | Meaning |
|---|---|
| `kind` | One of the eight kinds below. Required: a row without it is refused (exit 1, the message names the row). |
| `value` | The reading, in the unit of its kind. Required: a row without it is refused (exit 1, the message names the row). |
| `sigma` | The 1-sigma standard deviation of the reading, in the same unit as `value`. Required. It must be finite and above 0: the weight of the row is `1/sigma^2`. |
| `bus` | The bus id, for the bus kinds. Never a position in the case file. |
| `from`, `to` | The branch endpoints as bus ids, for the branch kinds. Matched in the orientation the branch is stored; a branch stored `4 -> 1` cannot be addressed as `1 -> 4`. |
| `end` | Which terminal of the branch is metered: `"From"` (default) or `"To"`. |
| `circuit` | Picks one of several parallel branches between the same two buses: the branch name, or the ordinal form `"#k"`, where `k` is the branch's position in the case. |
| `source` | A provenance tag, default `"scada"`. It is carried to the export and groups rows in [estimate-noise-floor](#calibrating-sigmas-with-estimate-noise-floor). Three tags carry meaning, listed below. |

A file written by `--synth` also holds `bus`, `from`, `to`, `end` and `isBranch` on every row; the reader ignores the
ones a kind does not use.

### The eight kinds

| Kind | Measures | Unit of `value` and `sigma` | Addressed by |
|---|---|---|---|
| `VmPu` | Bus voltage magnitude | pu | `bus` |
| `VaDeg` | Bus voltage angle, relative to the slack | degrees | `bus` |
| `PInjMw` | Net real-power injection at the bus (generation minus demand) | MW | `bus` |
| `QInjMvar` | Net reactive-power injection at the bus | MVAr | `bus` |
| `PFlowMw` | Real power entering the branch at the metered end | MW | `from`, `to`, `end`, `circuit` |
| `QFlowMvar` | Reactive power entering the branch at the metered end | MVAr | `from`, `to`, `end`, `circuit` |
| `IMagPu` | Current magnitude at the metered end, `sqrt(P^2 + Q^2) / Vm` | pu | `from`, `to`, `end`, `circuit` |
| `IAngDeg` | Current phasor angle at the metered end, `Va - atan2(Q, P)` | degrees | `from`, `to`, `end`, `circuit` |

Power values are written in MW and MVAr and divided by `baseMva` on the way in; voltage and current magnitudes are
already per-unit; angles are written in degrees and converted to radians, `sigma` included. A positive flow enters the
branch at the metered terminal. In the case9 file above, `{"kind": "PFlowMw", "from": 4, "to": 5, "end": "From"}` is
the power leaving bus 4 into branch 4-5, and `{"from": 6, "to": 7, "end": "To"}` is the power entering branch 6-7 from
the bus 7 side, which is negative here because power flows from 6 to 7.

`VaDeg` and `IAngDeg` are PMU kinds, and their values must be referenced to the case's slack bus. A real PMU feed
carries angles against its own arbitrary reference, and the estimator does not solve for a per-feed offset. Either
subtract the feed's angle at a datum bus before writing the rows, or let `--phaseangle` do the re-referencing (see
[Phase-angle feeds](#phase-angle-feeds)). A `VaDeg` row on the slack bus is legal and carries no information, because
the slack angle is fixed. Angle residuals are wrapped to the interval from -180 to 180 degrees, so a reading either
side of the cut still compares correctly.

Parallel circuits need `circuit`. The synthetic sampler writes the ordinal form for every parallel pair, because real
cases carry duplicate names on parallel branches. case57 has two branches between buses 4 and 18, and the sampled rows
for them read `"from": 4, "to": 18, "circuit": "#18"` and `"circuit": "#19"`. A reading that names the endpoints
without `circuit` when two branches match is refused with the count of matches.

Three `source` tags are acted on:

- `virtual-zero-injection` makes an injection row a hard equality under `--method constrained` and `--method feasible`
  (see [Choosing a method](#choosing-a-method)).
- `pseudo-voltage-schedule` marks a flat voltage guess that `--prior solve` replaces with the solved profile (see
  [Priors and warm starts](#priors-and-warm-starts)).
- `removal-backfill-prior` is written by `--backfill-removals` onto the replacement rows it creates; such a row is
  never removed.

### When the file is wrong

The whole set is validated against the case before any iteration, and every offender is named in one message:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-invalid.json --out results/err
invalid measurement set: Measurement set 'case9' failed validation:
  measurement #0 (VmPu @ bus 1): sigma 0 must be finite and > 0
  measurement #1 (VmPu @ bus 99): bus 99 does not exist in the model
  measurement #2 (PFlowMw @ branch 4->1 (to)): no branch 4->1 in the model
[exit 1]
```

Here a zero sigma, a bus that is not in the case and a branch addressed against its stored orientation are all
reported at once. The exit code is 1.

## Choosing a method

Every method shares one loop: start from a flat profile (or a warm start), take trust-capped Gauss-Newton steps until
the update falls below `--tol`, then judge the result. `--method` swaps what happens inside the step and in the
judgement.

| Method | What it does | Bad-data handling | Limit |
|---|---|---|---|
| `wls` (default) | Weighted least squares: Gauss-Newton on the normal equations. | Chi-square test, then largest-normalised-residual removal. | None. Above 200 buses it uses the sparse backend. |
| `qr` | The same least-squares solution by Householder QR on the weighted Jacobian, without forming `H^T W H`. | As `wls`. | Dense. Refused above 3,000 buses. |
| `constrained` | Rows tagged `virtual-zero-injection` become exact equalities (a KKT augmented system). | As `wls`. | Dense. Refused above 3,000 buses. |
| `feasible` (alias `constrained-physics`) | `constrained`, plus a floor of 1e-6 pu on every voltage, generator reactive output held in its limits, branch angle differences held under 90 degrees, and an audit of the converged state against the hard physical laws. | As `wls`. | Dense. Refused above 3,000 buses. |
| `huber` | Iteratively reweighted least squares. Residuals within 1.345 sigma count quadratically, beyond it linearly. | Down-weighting, no removal. | At most 50 reweighting passes. |
| `lav` (alias `lav-lp`) | Least absolute values, solved exactly as a sequence of linear programmes: minimises the sum of absolute standardised residuals. | Fits through outliers, no removal. Prints its objective and no chi-square verdict. | Dense. Refused above 2,000 states. |
| `lav-irls` | Least absolute values by reweighting (weight `1/max(|r|, 1e-3)` on the standardised residual): an approximation of `lav` whose weights do not settle. | As `lav`. A `NOTE` line says it is approximate. | At most 10 reweighting passes. |
| `schweppe-gm` (alias `gm`) | Huber on the leverage-adjusted residual, so an error on a high-leverage row is not masked. | Down-weighting, no removal. | At most 50 reweighting passes. |

An unknown method name is refused before any work, with the accepted names:

```
$ gridsim estimate case14 --synth --method newton --out results/err
error: --method expected 'wls', 'huber', 'lav', 'lav-lp', 'lav-irls', 'schweppe-gm', 'qr', 'constrained' or 'feasible' but got 'newton'
[exit 2]
```

### What the methods give on the same data

On clean data, from the case14 captures (`gridsim estimate case14 --synth --method <m>`):

| Method | Iterations | max abs dx | J | max abs dVm (pu) | max abs dVa (deg) |
|---|---|---|---|---|---|
| `wls` | 5 | 2.72E-009 | 80.01 | 1.330E-003 | 2.248E-001 |
| `qr` | 5 | 2.72E-009 | 80.01 | 1.330E-003 | 2.248E-001 |
| `constrained` | 5 | 2.72E-009 | 80.01 | 1.330E-003 | 2.248E-001 |
| `feasible` | 6 | 5.46E-011 | 80.01 | 1.330E-003 | 2.248E-001 |
| `huber` | 13 | 8.11E-009 | 80.18 | 1.218E-003 | 2.220E-001 |
| `lav` | 32 | 7.90E-012 | 86.75 | 1.114E-003 | 2.064E-001 |
| `schweppe-gm` | 29 | 2.93E-009 | 80.88 | 1.601E-003 | 2.148E-001 |
| `wls --no-bad-data` | 5 | 2.72E-009 | n/a | 1.330E-003 | 2.248E-001 |

`qr` and `constrained` print the same figures as `wls` here: `qr` solves the same least-squares problem, and this set
has no `virtual-zero-injection` rows for `constrained` to promote. `feasible` takes one more iteration here. The three
robust methods take more iterations because each reweighting pass is a full Gauss-Newton solve, and they land between
1.1E-003 and 1.6E-003 pu from the truth, not on the `wls` figures.

With three gross errors planted in the case14 set (`case14-planted.json`: bus 4 injection +30 MW, branch 2-3 flow
+25 MW, bus 9 voltage -0.08 pu, on the same noise draw as the clean run), the methods separate. The last two columns
are measured against the exact case14 solution, read from each run's `estimate-export.json`; the clean reference
for them is 1.330E-003 pu and 0.2248 degrees.

| Method | Iterations | Chi-square | Rows removed | max abs dVm (pu) | max abs dVa (deg) |
|---|---|---|---|---|---|
| `wls` | 16 | J = 78.65, PASS | 3 | 1.568E-003 | 0.2046 |
| `wls --hti` | 10 | J = 78.65, PASS | 3 | 1.568E-003 | 0.2046 |
| `huber` | 31 | J = 1271.34, FAIL | 0 | 2.055E-003 | 0.2527 |
| `lav` | 34 | J = 1204.82, FAIL | 0 | 1.648E-003 | 0.3267 |
| `schweppe-gm` | 33 | J = 1390.91, FAIL | 0 | 1.991E-003 | 0.2091 |
| `wls --no-bad-data` | 5 | not run | 0 | 8.594E-003 | 0.7199 |

The removal loop of `wls` finds all three planted rows and returns an estimate as good as the clean one; ignoring
bad data costs a factor of 5.5 in voltage and 3.5 in angle. The robust methods keep every row in the fit with a reduced
weight, so their chi-square test, computed against the original sigmas, reads `FAIL` by construction while the state is
usable. Their iteration counts are summed over all their reweighting passes.

Redundancy and leverage decide which method wins. On a small set with little redundancy, one bad row can sit where
the data has no other view of its bus. Plant a 40 MW error on the bus 7 injection of the case9 file
(`case9-telemetry-bad.json`, 24 rows, 7 degrees of freedom; every file named in this chapter is in
`docs/_captures/estimation/`) and the same four methods give:

| Method | Iterations | Chi-square | Rows removed | max abs dVm (pu) | max abs dVa (deg) |
|---|---|---|---|---|---|
| `wls` | 22 | J = 0.59, PASS | 1 (`PInjMw` bus 7, rN 16.5) | 7.182E-004 | 0.0491 |
| `huber` | 99 | J = 466.22, FAIL | 0 | 7.652E-002 | 1.726 |
| `lav` | 95 | J = 522.72, FAIL | 0 | 8.049E-002 | 1.888 |
| `schweppe-gm` | 153 | J = 672.02, FAIL | 0 | 6.027E-003 | 0.4834 |
| `wls --no-bad-data` | 17 | not run | 0 | 4.916E-002 | 0.7012 |

Here `huber` and `lav` end up farther from the truth than an estimate that ignores the error. The bad row is an
injection with high leverage: its raw residual stays small, which is the masking that `schweppe-gm` is built to defeat.
`schweppe-gm` recovers most of the loss and the removal loop recovers all of it.

### When each method fits

- **`wls`** is the default and the method the others are checked against. Use it first, on every set. Its removal loop
  deletes up to 20 rows, one per pass.
- **`qr`** gives the `wls` answer without squaring the condition number of the gain matrix. Use it when the gain is
  ill conditioned: very tight sigmas (a PMU angle at 1e-4 rad) beside very loose pseudo-measurements. It stores a dense
  matrix of measurements by states.
- **`constrained`** is for sets that carry zero-injection facts. A switching node has no generation or load, so its net
  injection is exactly zero, a fact about the network and not a reading. Tag such rows `virtual-zero-injection` and
  this method enforces them exactly instead of through a small sigma (worked example below).
- **`feasible`** is for estimates that feed something downstream and must not be physically impossible. On top of
  `constrained` it keeps every magnitude above 1e-6 pu, holds each generator's reactive output inside its limits and
  every angle difference across a branch under 90 degrees, and audits the converged state against the hard physical
  laws; a breach ends the run as `Infeasible` with exit 3. The 0.9 to 1.1 pu voltage band and the thermal ratings are
  audited but are not enforced during iteration, and they do not change the status.
- **`huber` and `lav`** suit large, highly redundant sets where you do not want rows deleted: they have no removal cap
  and no removal passes. `huber`'s chi-square line reports the misfit of the down-weighted rows and reads `FAIL`
  whenever outliers are present. `lav` minimises the sum of absolute standardised residuals exactly, by linear
  programming, so its optimum fits some rows exactly and leaves outliers with their whole residual. It prints
  `objective: sum |r| / sigma = ...` and `consistency: no chi-square verdict (J = ..., information only)`, with a `NOTE`
  that the LAV objective is not chi-square distributed, so no PASS or FAIL applies. `lav-irls` is the reweighting
  approximation of the same optimum and says so on every run. Prefer `wls` on a small set.
- **`schweppe-gm`** is the robust choice when the suspect row is a high-leverage one, such as the sole injection meter
  of a radial bus.

The dense methods (`qr`, `constrained`, `feasible`) are accepted up to 3,000 buses, and the cost of each iteration
grows with the cube of the state count: on case300 with `--synth` the wall-clock run (process start included, 8-thread
laptop) takes 2.1 s with `constrained` and 0.8 s with `wls`. A larger case with one of them stops with an
refusal and exit 2 (see [Limits](#limits)).

### Zero-injection rows and constrained

`case9-telemetry-zi.json` is the case9 telemetry file with a P and a Q row of value 0 added at each of the switching
buses 4, 6 and 8, tagged `virtual-zero-injection`. Under `wls` those rows are ordinary readings with a sigma of 1:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-zi.json --out results/case9-zi-wls

=== WLS state estimate: case9 ===
  status: Converged   iterations: 6   max |dx|: 5.99E-012
  measurements: 30   states: 17   redundancy (dof): 13
  observability: observable (rank 17/17)
  consistency: J = 2.60 vs chi2(95%) = 22.35 -> PASS

  -> results\case9-zi-wls
[exit 0]
```

Under `constrained` they are equalities:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-zi.json --method constrained --out results/case9-zi-constrained

=== Constrained WLS state estimate: case9 ===
  status: Converged   iterations: 6   max |dx|: 1.72E-011
  measurements: 30   states: 17   redundancy (dof): 13
  observability: observable (rank 17/17)
  consistency: J = 2.92 vs chi2(95%) = 22.35 -> PASS
  critical measurements (sole observers, unremovable, errors undetectable): 6

  -> results\case9-zi-constrained
[exit 0]
```

The residual tables (`residuals.csv`) show the difference. Under `wls` the six rows are fitted to between 0.16 and 0.25
in magnitude (MW and MVAr), with normalised residuals from 0.3 to 0.6. Under `constrained` no row is off by more than
1.3E-12. The summary then reports six critical measurements: a hard constraint has no residual variance, so it is a sole
observer by construction. `J` rises from 2.60 to 2.92 because the estimate now honours the constraints exactly.

## Bad data

### The chi-square test

After a converged solve with redundancy, `J` is compared with the 95 % chi-square point for the degrees of freedom.
The quantile comes from the Wilson-Hilferty approximation with a rational approximation of the normal quantile, within
1 % of table values for 3 or more degrees of freedom; at 1 and 2 degrees of freedom the exact closed forms are used. The test runs when bad-data detection is on and the degrees of
freedom are positive. Robust methods run it against the original sigmas and then stop; they remove nothing.

### Largest normalised residual

When the test fails, the estimator removes the single worst row and re-estimates from the current state. The worst row
is the one with the largest normalised residual above 3.0:

```
Omega_ii = sigma_i^2 - h_i^T (H^T W H)^-1 h_i
rN_i     = |r_i| / sqrt(Omega_ii)
```

`Omega_ii` is the variance the residual has once the fit has used the row to estimate itself, so `rN` is a fair
comparison across rows with different leverage. It is also why a 30 MW error on an injection does not read as `rN = 30`:
the fit bends toward the bad row. The loop repeats until the test passes, no removable row exceeds 3.0, or 20 rows have
been removed. The 95 % level, the 3.0 threshold and the cap of 20 are fixed in the CLI.

The case14 set with the three planted errors shows the loop, one removal per pass:

```
$ gridsim estimate case14 --measurements docs/_captures/estimation/case14-planted.json --out results/case14-planted

=== WLS state estimate: case14 ===
  status: Converged   iterations: 16   max |dx|: 8.00E-009
  measurements: 122   states: 27   redundancy (dof): 92
  observability: observable (rank 27/27)
  consistency: J = 78.65 vs chi2(95%) = 115.39 -> PASS
  removed bad data (pass 0): VmPu @ bus 9  rN = 18.3
  removed bad data (pass 1): PInjMw @ bus 4  rN = 14.2
  removed bad data (pass 2): PFlowMw @ branch 2->3 (from)  rN = 14.3

  -> results\case14-planted
[exit 0]
```

The three removals are the three planted rows, the worst remaining row each pass. After the third removal
`J = 78.65` against a threshold of 115.39, and `redundancy (dof)` has fallen from 95 to 92.

### Critical measurements

A row whose residual variance `Omega_ii` is at or below `1e-8 * sigma_i^2` is critical: it is the only observer of some
direction of the state, the fit passes through it exactly, and its residual is structurally zero. A gross error on it
cannot show up in any residual test. A critical row is never removed and is never counted in the search for the worst
row. The summary reports how many there are, `residuals.csv` marks them `critical = True` with a `NaN` normalised
residual, and the right reading is that their values are unchecked telemetry, not corroborated estimates.

### Skipping and trimming the test

`--no-bad-data` skips the chi-square test and the removal loop. Use it for a set you already trust, or to see the raw
fit to a set you are diagnosing. The planted-error file under it:

```
$ gridsim estimate case14 --measurements docs/_captures/estimation/case14-planted.json --no-bad-data --out results/case14-planted-nbd

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 8.81E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: not tested (chi-square not run: --no-bad-data; J = 822.02)

  -> results\case14-planted-nbd
[exit 0]
```

The state is the raw fit to all 122 rows and sits 8.6E-003 pu from the truth, against 1.6E-003 pu with removal; no
consistency verdict is given, although `J` (822.02) is far above the 95 % threshold the full run prints.

`--lean` keeps the test and the loop but skips the residual-covariance solves when the test passes. It is a throughput
switch for large sets. Normalised residuals become `NaN` in that case, so a lean run cannot feed
[estimate-noise-floor](#calibrating-sigmas-with-estimate-noise-floor).

### Backfilling removed rows

`--backfill-removals` changes what removal means for real-power rows. When the loop removes a `PInjMw` or `PFlowMw`
row it is replaced by a pseudo-measurement evaluated at the start state (the prior), with a loose sigma of 3 times the
removed one or 5 % of the value, whichever is larger, tagged `source = "removal-backfill-prior"` and itself never
removable. It keeps the answer anchored to the prior where a row was the only informative constraint on a weakly
metered region. Reactive rows are never backfilled. It is not available with `--hti`. The replacement is only as good
as the prior: with `--prior solve` the prior is built from the set's own injection rows, bad ones included, so on the
planted file the backfilled run ends with `J = 149.88` against 117.63, a chi-square `FAIL`. The default start is the flat start, which is no prior at all, so without `--prior solve` or `--warm-start` the flag is
refused (exit 2; it used to run and, on this file, remove 20 rows and end far from the truth). With `--warm-start` it
removes 3 rows and ends `J = 78.71` against 117.63, a `PASS`:

```
$ gridsim estimate case14 --measurements docs/_captures/estimation/case14-planted.json --prior solve --backfill-removals --out results/case14-planted-bf
  prior: measured-injection solve converged (3 iters); the set has no voltage-schedule rows, so nothing was replaced; used as the warm start

=== WLS state estimate: case14 ===
  status: Converged   iterations: 15   max |dx|: 2.89E-009
  measurements: 122   states: 27   redundancy (dof): 94
  observability: observable (rank 27/27)
  consistency: J = 149.88 vs chi2(95%) = 117.63 -> FAIL
  removed bad data (pass 0): VmPu @ bus 9  rN = 18.3
  removed bad data (pass 1): PInjMw @ bus 4  rN = 14.2
  removed bad data (pass 2): PFlowMw @ branch 2->3 (from)  rN = 14.2

  -> results\case14-planted-bf
warning: the measured-injection prior solve puts Vm at 1.0190 to 1.1022 pu, outside the 0.90 to 1.10 pu band. Its voltages enter as prior-solved rows, so a chi-square PASS cannot vouch for the voltage level: check the injection rows (missing interconnector or unallocated generation), or run with --prior flat.
[exit 0]
```

### Hypothesis-testing identification

`--hti` replaces the one-row-at-a-time loop with Hypothesis-Testing Identification. When the test fails, every row
with a normalised residual above 3.0 becomes a suspect, the gross errors of all suspects are estimated jointly, and a
suspect is condemned only if its test statistic `t` (the estimated error over its standard deviation) exceeds 3.0.
Interacting errors are separated in one step, where the sequential loop can blame a good row whose residual was
inflated by a bad neighbour. Under `--method constrained` and `--method feasible` a `virtual-zero-injection` row is a
hard equality, so HTI never makes it a suspect and never removes it, exactly as the sequential loop treats it (until
October 2026 HTI weighed such rows at their declared sigma and could remove one).

```
$ gridsim estimate case14 --measurements docs/_captures/estimation/case14-planted.json --hti --out results/case14-planted-hti

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.88E-009
  measurements: 122   states: 27   redundancy (dof): 92
  observability: observable (rank 27/27)
  consistency: J = 78.65 vs chi2(95%) = 115.39 -> PASS
  removed bad data (HTI pass 0): VmPu @ bus 9  t = 17.9  estimated error = -0.0764948 pu
  removed bad data (HTI pass 0): PFlowMw @ branch 2->3 (from)  t = 13.6  estimated error = 0.23808 pu_base_mva (23.808 MW)
  removed bad data (HTI pass 0): PInjMw @ bus 4  t = 7.2  estimated error = 0.272927 pu_base_mva (27.2927 MW)
  bad-data method: HTI, 2 pass(es), 10 iteration(s) in all, stopped: ChiSquarePassed

  -> results\case14-planted-hti
[exit 0]
```

Each removal line gives `t` and the estimated error in per-unit: bus 9 voltage -0.0765 pu (planted -0.08), the 2-3
flow 0.238 pu (23.8 MW, planted 25) and the bus 4 injection 0.273 pu (27.3 MW, planted 30). The closing line reports
2 passes and 10 iterations in all and why it stopped: `ChiSquarePassed`, `ChiSquareNotRun`, `NothingIdentified` (the
test failed but no row was condemned) or `RemovalCapReached`. The removals are also written to `hti-removals.csv`
with the columns `pass,kind,location,value,sigma,estimated_error,estimated_error_unit,estimated_error_measured,measured_unit,test_statistic`:
`estimated_error` is the estimator's number in its
internal unit, which `estimated_error_unit` names per row (`pu` for voltage and current magnitudes, `rad` for angles,
`pu_base_mva` for powers and flows), and `estimated_error_measured` is the same error in the measurement's own unit
(`measured_unit`: `pu`, `deg`, `MW` or `Mvar`). The console line names the unit the same way, for example
`estimated error = 0.23808 pu_base_mva (23.808 MW)` for a flow and `... rad (... deg)` for an angle. Under `--hti` the export's `removed` section is empty, because that section lists the
sequential loop's removals.

HTI is dense and works up to 2,000 states, which is 1,000 buses with one slack bus. It is refused, with exit 2, with
the robust methods, with `--backfill-removals`, with `--no-bad-data` and above that size. `--hti` on a set with little
redundancy can end `NothingIdentified` with chi-square still `FAIL` and exit 0. With a 20 MW error planted on the bus 5
injection of the case9 file (`case9-telemetry-bus5.json`):

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-bus5.json --hti --out results/case9-bus5-hti

=== WLS state estimate: case9 ===
  status: Converged   iterations: 12   max |dx|: 5.13E-009
  measurements: 24   states: 17   redundancy (dof): 7
  observability: observable (rank 17/17)
  consistency: J = 45.45 vs chi2(95%) = 14.05 -> FAIL
  bad-data method: HTI, 1 pass(es), 12 iteration(s) in all, stopped: NothingIdentified

  -> results\case9-bus5-hti
[exit 0]
```

HTI condemns nothing here and the test fails, while the sequential loop on the same file removes `PInjMw @ bus 5`
(`rN = 6.7`) and passes with `J = 0.45` (capture `case9-bus5-wls`). Use the sequential loop on a set this small.

### False-data-injection screen

Chi-square and normalised-residual tests share a blind spot. An attacker, or a systematic error, that adds a vector in
the range of the measurement Jacobian leaves every residual unchanged and shifts the estimated state undetected.
`--fdia-screen` adds a second layer for the part of that class that matters: it audits the final estimate against the
hard physical laws (with a power-balance tolerance of 0.1 times the system base in MVA, 10 MVA on a 100 MVA base) and against the operating envelope (the
0.9 to 1.1 pu voltage band, branch ratings and generator reactive limits), and reports a state that passed chi-square
yet breaks one of them. A limit breach counts only when it is larger than three times the estimate's own uncertainty
for that quantity, taken from the sigma of the telemetry that measures it (the bus voltage meter, the bus reactive
injection meter, or the P and Q flow meters at both ends of the branch); a breach with no such meter has no margin.
Smaller breaches are still listed, marked `[within estimate uncertainty; not counted]`.
The verdict is `clear`, `suspected`, `chi-square-failed` (conventional bad data, not a cloaked
attack) or `no-verdict` (the estimate did not converge, or the test never ran). The verdict is written to
`fdia-screen.json`. A `suspected` or `no-verdict` result exits 3. A clean estimate:

```
$ gridsim estimate case14 --synth --fdia-screen --out results/case14-fdia

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg
  FDIA screen: clear - chi-square PASSED and the state is physically plausible and anchor-consistent - no FDIA indicator.

  -> results\case14-fdia
[exit 0]
```

The screen reports; it never rejects. A perfectly crafted injection that lands on another feasible, in-limit operating
point is not caught. It is refused with `--no-bad-data`, because it needs the chi-square result. A breach smaller than
the estimate can resolve is listed but does not make the verdict `suspected`:

```
$ gridsim estimate case30 --synth --fdia-screen --out results/case30-fdia

=== WLS state estimate: case30 ===
  status: Converged   iterations: 4   max |dx|: 7.63E-009
  measurements: 254   states: 59   redundancy (dof): 195
  observability: observable (rank 59/59)
  consistency: J = 203.68 vs chi2(95%) = 228.58 -> PASS
  vs synthetic truth: max |dVm| = 7.267E-003 pu   max |dVa| = 3.474E-001 deg
    fdia limit violation: Rating (6-8) value 34.6965 vs limit 32: branch 6->8 at 108.4% of 32 MVA [within estimate uncertainty; not counted]
  FDIA screen: clear - chi-square PASSED and the state is physically plausible and anchor-consistent - no FDIA indicator. 1 limit breach(es) lie within 3 sigma of the estimate's uncertainty and are not counted.

  -> results\case30-fdia
[exit 0]
```

Branch 6-8 of case30 is rated 32 MVA, and the solved case itself loads it to 34.8 MVA (108.8 %), so the breach is
real; the noisy estimate puts it at 34.7 MVA. The breach is 2.7 MVA, and with flow meters of sigma 1.6 MW/MVAr the
estimate cannot tell it from noise at three sigma (4.8 MVA), so it is not evidence of an attack. Until October 2026 the
screen compared with no margin and this run read `suspected` with exit 3; case118 did the same for two generators that
sit exactly on their reactive minimum (Qg -14.13 against Qmin -14.00 MVAr with 1 MVAr meters).

### What to do when chi-square fails

1. Read the removed lines first. The loop stops on a `FAIL` when it hits the cap of 20, or when no removable row has a
   normalised residual above 3.0 (the worst sits on a critical row or a backfilled row).
2. Sort `residuals.csv` by `normalized_residual`. One value far above 3 is a bad reading. A broad shelf a little above
   1 across one `source` tag is a miscalibrated sigma class (see
   [estimate-noise-floor](#calibrating-sigmas-with-estimate-noise-floor)). Residuals clustered in one region point at a
   model error there: a missing shunt, a wrong impedance, a unit at the wrong bus.
3. If several simultaneous gross errors are plausible, run again with `--hti`, and with `--method schweppe-gm` if the
   suspect rows are high-leverage injections, and compare which rows each flags.
4. Check that `baseMva` and the case match the side that wrote the measurements.

A converged estimate with a failed test is still a state, and the GB estimation pipeline records the verdict per
timestamp in `chi_passed` (see [ch. 22](<0022 Estimating GB history.md>)); its numbers are uncorroborated until
you know why the test failed.

## Variations

### Priors and warm starts

A flat start (1 pu, slack angle everywhere) is the default. On stressed real telemetry an undamped flat start is the
classic way to walk a voltage through zero, so real data wants a better starting point. There are two.

`--prior solve` builds a power flow from the set's own injection rows and solves it. A bus injecting 50 MW or more
becomes a voltage-controlled bus at 1.01 pu with a reactive range of plus or minus 0.6 times the larger of its power and 100 MVA;
every other bus is a load bus carrying the measured injection, unmeasured buses inject nothing, and the slack absorbs
the imbalance. The solved state is the warm start, and the values of rows tagged `pseudo-voltage-schedule` are
replaced with the solved voltage at sigma 0.02 pu and the tag `prior-solved`. The run prints a line when it does so.
If the prior solve does not converge the run falls back to the flat schedule with a warning.

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry.json --prior solve --out results/case9-prior
  prior: measured-injection solve converged (4 iters); the set has no voltage-schedule rows, so nothing was replaced; used as the warm start

=== WLS state estimate: case9 ===
  status: Converged   iterations: 5   max |dx|: 4.43E-009
  measurements: 24   states: 17   redundancy (dof): 7
  observability: observable (rank 17/17)
  consistency: J = 1.11 vs chi2(95%) = 14.05 -> PASS

  -> results\case9-prior
[exit 0]
```

The same file converges in 5 iterations here against 8 from a flat start. The file has no `pseudo-voltage-schedule`
row, so the prior supplied only the warm start, and the line says so. A set with such rows prints
`<n> voltage-schedule row(s) replaced with the solved profile; used as the warm start`.

The `prior-solved` rows are not independent evidence: they are a power flow of the same injection rows. If those
injections are wrong (an interconnector feed missing, generation left unallocated), the prior can settle at a wrong
voltage level and hold the estimate there, and the chi-square test still passes because the voltage rows agree with
the injections by construction. When the prior's voltages leave the 0.9 to 1.1 pu band the run prints
`warning: the measured-injection prior solve puts Vm at <min> to <max> pu, outside the 0.90 to 1.10 pu band. ...` on
stderr; the estimate is unchanged. On the GDA re-run of 1 September 2026, period 2, the prior reached 1.2639 pu, the
estimate passed at `J = 301.59` with `vm_max` 1.267891 pu, and the same set with `--prior flat` failed the test
(`J = 557.40`, 20 removals at the very buses that sat at 1.27 pu); see
[ch. 22](<0022 Estimating GB history.md#pitfalls>). `--fdia-screen` on that run read `suspected` when it was
captured, at a time when the screen still scored Kirchhoff's current law against the case file's dispatch rather than
against the estimate's own zero-injection rows; its voltages at 1.27 pu are limit breaches in their own right.

The chi-square test does not count the `prior-solved` rows: they stay in the fit
(their weight still shapes the estimate) and in the residual list, but their residuals are left out of the reported
`J` and the rows themselves out of the degrees of freedom (`dof`), since they add no evidence the injection rows did not
already carry. A run with no `prior-solved` rows (`--prior flat`, `--prior schedule`, any synthetic set) is unchanged
to the last bit. On a `--prior solve` run both `J` and `dof` fall; with few independent rows left, the test is much
stricter, as it should be. On the SP02 set above, the recorded J breakdown (315 `prior-solved` rows contributing
32.21 of the 301.59) means the counted J is about 269.38 and the degrees of freedom fall by 315, which leaves almost
no redundancy for that set: the old PASS cannot recur (a re-run on the lake data was not made with this change).

`--warm-start` seeds the iteration from the case's own solved operating point instead, which is standard practice when
the case file already describes the network's usual state. It falls back to a flat start with a warning when the base
case will not solve. When both flags are given and the prior solve converges, the prior's state is the start.

### Choosing the coverage of a synthetic set

`--coverage-vm`, `--coverage-inj` and `--coverage-flow` give the fraction of buses with a voltage meter, buses with a P
and Q injection meter, and branches with flow meters (both ends, P and Q). Meters are placed on a deterministic stride
over the case's own order, so the same fractions always give the same layout. `--noise S` scales the injected noise
while the declared sigmas stay unchanged (the sampler uses 0.004 pu on voltage, 1.0 MW and MVAr on injections and
1.6 MW and MVAr on flows), and `--seed N` changes the draw (default 20260713). Halving the injection meters and
keeping 30 % of the flows leaves case14 observable but much less accurate:

```
$ gridsim estimate case14 --synth --coverage-inj 0.5 --coverage-flow 0.3 --out results/case14-coverage

=== WLS state estimate: case14 ===
  status: Converged   iterations: 8   max |dx|: 1.26E-009
  measurements: 52   states: 27   redundancy (dof): 25
  observability: observable (rank 27/27)
  consistency: J = 17.17 vs chi2(95%) = 37.65 -> PASS
  vs synthetic truth: max |dVm| = 5.231E-003 pu   max |dVa| = 5.287E+000 deg

  -> results\case14-coverage
[exit 0]
```

52 readings instead of 122 push the angle error from 0.22 to 5.3 degrees.

### Phase-angle feeds

`--phaseangle` turns per-bus voltage angles from a PMU feed into slack-referenced `VaDeg` rows and merges them into
the set before the prior. Each observation is `BUS:ANGLE_DEG:UTC:FREQ_HZ`, with the UTC in ISO-8601 `Z` form, and any
number of them follow the flag. A real feed measures against its own reference, so by default (`--phaseangle-ref auto`)
the angles are projected to a common instant using the frequency (`angle + 360 * (f - 50) * dt`) and re-referenced to
the slack through a datum bus. Here a feed whose reference sits 10 degrees off the slack's reads buses 1, 7 and 9;
the slack observation anchors the datum, its own row is dropped, and the other two rows land on the estimator's frame:

```
$ gridsim estimate case9 --synth --phaseangle 1:10.0:2024-03-01T12:00:00Z:50.0 7:10.7275:2024-03-01T12:00:00Z:50.0 9:6.0112:2024-03-01T12:00:00Z:50.0 --phaseangle-sigma 0.5 --out results/case9-phaseangle
  phaseangle: injected 2 VaDeg row(s) at 2024-03-01 12:00:00Z (mode AutoReference, sigma 0.5 deg)

=== WLS state estimate: case9 ===
  status: Converged   iterations: 5   max |dx|: 1.84E-011
  measurements: 65   states: 17   redundancy (dof): 48
  observability: observable (rank 17/17)
  consistency: J = 43.07 vs chi2(95%) = 65.17 -> PASS
  vs synthetic truth: max |dVm| = 5.918E-004 pu   max |dVa| = 6.065E-002 deg

  -> results\case9-phaseangle
[exit 0]
```

If the feed has no observation at the slack bus, the datum is the first bus listed and needs the present-state angle
as an anchor, which `--prior solve` or `--warm-start` supplies; without either the run refuses:

```
$ gridsim estimate case9 --synth --phaseangle 7:10.7275:2024-03-01T12:00:00Z:50.0 --out results/err
error: --phaseangle-ref auto: no observation at the slack bus 1, so the datum bus 7 needs its present-state angle as an anchor. Add --prior solve (or --warm-start), or include the slack bus among the observations.
[exit 2]
```

`--phaseangle-ref referenced` projects the angles in time only, for a feed already in the slack frame, and `raw`
injects them exactly as given. `--phaseangle-sigma` sets the 1-sigma of each row in degrees, default 2.0.
`--phaseangle-at` sets the instant to project to, default the set's `takenUtc`. `--phaseangle-from-gda <root>` reads
the GDA `PHASEANGLE_INFERED` system angle and frequency at that instant and prints them on a `gda reference:` line;
the system angle is used as the common-mode frame for an absolute feed in `referenced` mode, and cancels under `auto`.
Genuine differences between buses are what the rows add; a common offset on every bus cancels against the slack and
adds nothing.

### A JSON case folder and larger cases

A folder holding `system.json` estimates directly, so the GB materialisations work without conversion:

```bash
gridsim estimate data/gb-full --synth
```

```
$ gridsim estimate data/gb-full --synth --out results/gb-full-estimate

=== WLS state estimate: data/gb-full ===
  status: Converged   iterations: 7   max |dx|: 5.79E-010
  measurements: 34289   states: 7076   redundancy (dof): 27213
  observability: observable (rank -1/7076)
  consistency: J = 26996.85 vs chi2(95%) = 27597.87 -> PASS
  vs synthetic truth: max |dVm| = 7.892E-003 pu   max |dVa| = 1.172E+000 deg

  -> results\gb-full-estimate
[exit 0]
```

The case has 3,539 buses and 7,076 states. Above 200 buses the `wls`, `huber`, `lav` and `schweppe-gm` methods switch
to a sparse backend (a CSR gain matrix with a sparse LU factorisation); the estimate agrees with the dense backend to
1e-8 pu in voltage magnitude and 1e-7 degrees in angle, the tolerances `SparseEstimatorTests` asserts on case118, and is
not bit-identical to it, because the dense and sparse gain fills and factorisations sum in a different order (see
[ch. 6](<../technical/0006 State estimation.md>)). Nothing in the output names the backend. The observability proof, which needs a dense matrix, runs on the
sparse backend only up to 2,500 states, so here the rank prints as `-1`: it was not computed. The unmeasured-island
check still ran and a singular gain would still have been caught as `Unobservable`. The run is an `estimate` of a
sampled set, not of GB telemetry; [ch. 22](<0022 Estimating GB history.md>) covers the real GB pipeline.

A mid-sized case such as `case118` runs on the dense backend and keeps the full proof:

```
$ gridsim estimate case118 --synth

=== WLS state estimate: case118 ===
  status: Converged   iterations: 6   max |dx|: 2.40E-011
  measurements: 1098   states: 235   redundancy (dof): 863
  observability: observable (rank 235/235)
  consistency: J = 877.80 vs chi2(95%) = 932.45 -> PASS
  vs synthetic truth: max |dVm| = 1.619E-003 pu   max |dVa| = 1.134E-001 deg

  -> results\estimate-case118
[exit 0]
```

### A linear PMU estimate: `pmu-estimate`

When a set holds synchrophasors, `gridsim pmu-estimate <case> --measurements <set.json>` gives the linear state
estimate. It pairs a bus's `VmPu` and `VaDeg` rows into its voltage phasor, and a branch's `IMagPu` and `IAngDeg` rows
metered at the From end into its current phasor, each pair weighted by its exact first-order error covariance. Both
phasor kinds are linear in the rectangular bus voltages, so the estimate is one weighted least-squares solve: no flat
start, no iterations and no convergence test. The output lists the phasors used, every row not used with its reason
(power and flow rows, or a magnitude without its angle), the objective `J` with its degrees of freedom, and each bus's
`|V|` and angle. There is no bad-data test; use `estimate` for chi-square and bad-data handling. A set `pmu-ingest
--channels ... --measurements-dir` writes is the usual input ([ch. 25](<0025 Interposing live telemetry.md>)). It
exits 3 when the phasors do not determine every bus voltage, and 1 for a set with no phasor, two rows of one kind at one
place, a current metered at the To end or a phasor of magnitude 0. Every flag is in
[ch. 19, pmu-estimate](<0019 CLI reference.md#pmu-estimate>).

### Calibrating sigmas with estimate-noise-floor

A chi-square `FAIL` across a whole class of meters points to a wrong declared sigma. `estimate-noise-floor`
tests that over a corpus. Give it estimate folders (or `estimate-export.json` files) and it pools the normalised
residuals by `source` tag and measurement kind. A correctly declared class has normalised residuals shaped like the
absolute value of a standard normal, so each class gets a scale factor `k`, the true noise over the declared sigma:
`k_robust` from the median and `k_std` from the spread, with `bias` the difference between the mean-based and
median-based factors as a shape check. A class is flagged `MISCALIBRATED` when `k_robust` leaves the band 1 +/- 0.25
or `|bias|` exceeds 0.25, and only when it has at least 8 samples. The suggested fix is to multiply the sigma by
`k_robust`. A corpus of five clean case14 runs (seeds 1 to 5) reads:

```bash
for s in 1 2 3 4 5; do gridsim estimate case14 --synth --seed $s --out results/nf/seed$s; done
gridsim estimate-noise-floor results/nf --out results/nf-report
```

```
$ gridsim estimate-noise-floor results/nf --out results/nf-report

=== Measurement noise floor: 5 estimate(s), 610 residual(s) (0 NaN skipped) ===
  source                   kind         count  k_robust    k_std     bias  verdict
  synthetic                QInjMvar        70     0.814    0.917    0.091  ok
  synthetic                PInjMw          70     0.848    1.006    0.139  ok
  synthetic                VmPu            70     1.143    1.207    0.004  ok
  synthetic                QFlowMvar      200     0.899    0.983    0.066  ok
  synthetic                PFlowMw        200     1.079    0.988   -0.067  ok
  0 of 5 class(es) miscalibrated (tolerance 0.25, k = true noise / declared sigma)

  -> results\nf-report
[exit 0]
```

Five of five classes are inside the band. Five runs with the injected noise doubled (`--noise 2`, declared sigmas
unchanged) are not:

```bash
for s in 1 2 3 4 5; do gridsim estimate case14 --synth --seed $s --noise 2 --out results/nf2/seed$s; done
gridsim estimate-noise-floor results/nf2 --out results/nf2-report
```

```
$ gridsim estimate-noise-floor results/nf2 --out results/nf2-report

=== Measurement noise floor: 5 estimate(s), 547 residual(s) (0 NaN skipped) ===
  source                   kind         count  k_robust    k_std     bias  verdict
  synthetic                PFlowMw        175     1.796    1.350   -0.202  MISCALIBRATED: scale sigma by 1.796
  synthetic                QFlowMvar      185     1.666    1.357   -0.119  MISCALIBRATED: scale sigma by 1.666
  synthetic                VmPu            56     1.572    1.179   -0.125  MISCALIBRATED: scale sigma by 1.572
  synthetic                QInjMvar        65     1.370    1.135   -0.121  MISCALIBRATED: scale sigma by 1.370
  synthetic                PInjMw          66     1.137    1.193    0.080  ok
  4 of 5 class(es) miscalibrated (tolerance 0.25, k = true noise / declared sigma)

  -> results\nf2-report
[exit 0]
```

Four classes are flagged, with suggested scalings of 1.370 to 1.796, below the true factor of 2. The removal loop
deleted 63 rows from the corpus (the residual count falls from 610 to 547). A class needs 8 residuals by default
(`--min-samples`), so run the study over many timestamps. Exports of
non-converged runs are skipped with a warning, and residuals that are `NaN` (critical rows, `--lean` and
`--no-bad-data` runs) are counted and skipped. The study is not a gate and exits 0 whenever it produces a report,
miscalibration included. `--miscal-tol` and `--min-samples` change the two limits, and the output goes to `noise-floor.csv`
(`source,kind,count,spread_robust,spread_std,bias,suggested_sigma_scale,miscalibrated`) beside `run-metadata.json`. List
the paths before any flags. The verb is in
[ch. 19, CLI reference](<0019 CLI reference.md#estimate-noise-floor>).

### Recording a run in the audit log

`--audit-log <file>` appends a tamper-evident entry to a hash-chained log. It is accepted with `estimate`, `crossval`
and `interpose` only; with any other verb it exits 2. The log is opened and verified before the verb works, so a
tampered or locked log stops the run with exit 1. One entry is written when the run ends, however it ends. It holds who
ran it, the verb, a SHA-256 of every input (a built-in case is recorded as the hash of its name), the provenance
(`Synthetic` for `--synth`, `DerivedFromRecorded` for a measurement file), the instant the state describes, and a
result line of the form `method=Wls status=Converged observable=True J=80.01052231478454 chi2=PASS dof=95 removed=0
measurements=122`, with the `--hti` and `--fdia-screen` outcomes appended when those flags ran. The run prints the
anchor, the entry count and head hash, on stderr:

```
$ gridsim estimate case14 --synth --audit-log results/audit.log --out results/case14-audit

=== WLS state estimate: case14 ===
  status: Converged   iterations: 5   max |dx|: 2.72E-009
  measurements: 122   states: 27   redundancy (dof): 95
  observability: observable (rank 27/27)
  consistency: J = 80.01 vs chi2(95%) = 118.75 -> PASS
  vs synthetic truth: max |dVm| = 1.330E-003 pu   max |dVa| = 2.248E-001 deg

  -> results\case14-audit
audit: results\audit.log entries=1 head=98469C9B6B9AFCD06AFDFB758531F87AC7518814E8039FE0404293514DE20DAA
[exit 0]
```

Keep that line outside the log; `gridsim audit verify` recomputes the chain and, given the anchor, detects truncation:

```
$ gridsim audit verify results/audit.log
OK results/audit.log: 1 entries, head 7FA1A23508D9103CA95FE3E23DD4BF3E662A08EED5A12F694AB75A06A2497C02
[exit 0]
```

The head hash differs on every run because each entry carries its time and user. The `audit` verb is in
[ch. 19, CLI reference](<0019 CLI reference.md#audit>), and the log format in
[technical ch. 10](<../technical/0010 File and wire formats.md>).

### Cross-checking against pandapower

Two Python scripts check estimates independently of GridSim: `tools/crossval/crossval_estimation.py` feeds pandapower
the same measurement set from an `estimate-export.json` and diffs the states, and `tools/crossval/se_residual_check.py`
verifies the residuals and optimality in NumPy with no GridSim arithmetic. Both are covered in
[ch. 27](<0027 Validation tools.md>), with the agreement they reach on the seeded synthetic sets. The
`gridsim crossval` verb is the power-flow check, not the estimator check, and it needs pandapower:

```
$ gridsim crossval case9
[crossval] solved 'case9' (converged, 4 iters) -> src\GridSim.Cli\bin\Release\net10.0\results\crossval-case9\solve-export.json
[crossval] sidecar unavailable (python): crossval: pandapower not installed (pip install pandapower)
           install pandapower to run the diff (pip install pandapower). The export is at src\GridSim.Cli\bin\Release\net10.0\results\crossval-case9\solve-export.json.
[exit 77]
```

Exit 77 is the conventional "skipped" code. The solve of case9 converged (the first line); the second step, the
comparison with pandapower, never ran because Python's pandapower package is not installed on the machine that made
this capture. Exit 77 says "the check did not run", which is different from exit 0 ("the check ran and agrees") and from
exit 3 ("the check ran and failed"). A CI gate must treat 77 as not validated. Install pandapower and run it again to
get the comparison.

## Exit codes

| Code | Meaning for `estimate` |
|---|---|
| 0 | The estimate converged and the network is observable. A chi-square `FAIL`, an HTI stop of `NothingIdentified` and an FDIA verdict of `clear` or `chi-square-failed` all still exit 0. |
| 1 | An input could not be used: the case or the measurement file did not load, the set failed validation, the ground-truth solve for `--synth` did not converge, `--phaseangle-from-gda` could not read its lake, or the `--audit-log` file could not be opened or verified. |
| 2 | A usage error: no case, neither `--measurements` nor `--synth`, an unknown flag, a bad value for `--method`, `--prior` or `--phaseangle-ref`, a malformed `--phaseangle`, a `--phaseangle-from-gda` with no target instant, a forward-inference rejection of a `takenUtc` inside the 169-hour horizon, `--hti` with `--no-bad-data`, a robust method, `--backfill-removals` or too large a case, `--fdia-screen` with `--no-bad-data`, or `--audit-log` on another verb. |
| 3 | The estimate is not a result: `MaxIterations`, `NonFinite`, `Unobservable` or `Infeasible`; or `--fdia-screen` returned `suspected` or `no-verdict`. |

`estimate-noise-floor` exits 0 whenever a report is produced, 1 for an unreadable input, a path that does not exist, no `estimate-export.json`, or no
finite residual in the whole corpus, and 2 for no path given or a bad flag. `crossval` exits 0 when the diff agrees, 3
on a mismatch or a failed sidecar, 1 when the case does not load, and 77 when Python or pandapower is absent.

## Pitfalls

- **`consistency: ... FAIL` still exits 0.** A script that tests only the exit code cannot see a failed chi-square.
  Read the consistency line, or read `chiSquareTest` in `estimate-export.json` (`Passed`, `Failed` or `NotRun`).
- **`not tested (chi-square not run: --no-bad-data ...)` is not a pass.** The `J` it prints has no verdict; compare it
  with a run without `--no-bad-data` before trusting the state.
- **A robust method reads `FAIL` whenever the set has outliers.** `huber` and `schweppe-gm` keep the down-weighted rows
  in `J`, so the test fails by construction. Judge the state, not the verdict. `lav` and `lav-irls` give no verdict at all.
- **An `Unobservable` run still writes files.** The state in `estimate-export.json` is the start point, not an
  estimate. Check `status` before reading any number.
- **A set dated in the last 169 hours is refused.** The run prints `REJECTED (forward inference)` and exits 2 (the
  block after this list). The estimator reconstructs history and does not nowcast.
- **The default output folder is under `results/` in the current directory.** Pass `--out` to keep results with the work.
- **A `VaDeg` row on an unreferenced feed conflicts with every other row.** The estimator does not solve for a feed
  offset. Use `--phaseangle` or subtract the datum angle yourself.
- **Parallel circuits need `circuit`.** Two branches between the same buses with no `circuit` is refused with the count
  of matches. Use the ordinal form `"#k"` when names are missing or duplicated.
- **`--fdia-screen` lists breaches it does not count.** A breach within three sigma of the estimate's uncertainty is
  listed and marked `not counted`; a breach of a quantity no meter measures has no margin and always counts. Read the
  `fdia limit violation` lines before treating a `suspected` as an incident.
- **Sigmas decide everything.** The weight of a row is `1/sigma^2`, so a sigma that is too small makes a row dominate
  and fail the test, and one that is too large makes it irrelevant. Use `estimate-noise-floor` to check them.
- **A critical row is never checked.** Its value passes into the estimate unchallenged. Add a second observer.
- **Flat starts on stressed data.** Use `--prior solve` or `--warm-start` for real telemetry.
- **`--lean` (when the chi-square test passed), `--no-bad-data` and the robust methods blank the normalised residuals.** `estimate-noise-floor` then refuses the corpus with
  exit 1.

A set stamped in the future (here `2099-01-01`) is refused like this. The horizon in the message is the real time minus
169 hours, so it differs on every run:

```
$ gridsim estimate case9 --measurements docs/_captures/estimation/case9-telemetry-future.json --out results/err
REJECTED (forward inference): measurement set timestamp 2099-01-01T12:00:00.0000000Z is newer than the historical horizon 2026-10-12T16:39:58.8865562Z (data must lag real time by >=169 h). GridSim replays history only - it never processes recent or future data, so it cannot forward-infer.
[exit 2]
```

## Limits

- `--method constrained`, `--method feasible` and `--method qr` are dense only and are refused above 3,000 buses with
  `error: estimate: --method qr is dense only (fails loud above 3000 buses; this case has 3539). ...` and exit 2.
- `--hti` on a set where the suspect measurements are not jointly identifiable, such as `case9-telemetry-bad.json`,
  stops with `error: --hti could not identify the bad data on this set: singular matrix in DSE covariance inverse ...`
  and exit 3; run without `--hti` for largest-normalised-residual removal.
- `--synth` with `--measurements` is refused with exit 2.
- `lav` is dense and refused above 2,000 states.
- The chi-square confidence (95 %), the removal threshold (3.0) and the removal cap (20) are fixed in the CLI.
- The summary does not print the limit violations the `feasible` audit records.
- `estimate` does not compute the observability rank above 2,500 states on the sparse backend; the rank prints as `-1`
  and only the island check and a singular factorisation can report `Unobservable`.


## See also

- [../technical/0006 State estimation.md](<../technical/0006 State estimation.md>) - the equations, the
  gain matrix, the step backends, the invariants and the tests that pin them
- [ch. 19, CLI reference](<0019 CLI reference.md#estimate>) - every `estimate` flag; also
  [estimate-noise-floor](<0019 CLI reference.md#estimate-noise-floor>), [crossval](<0019 CLI reference.md#crossval>) and
  [audit](<0019 CLI reference.md#audit>)
- [ch. 22, Estimating GB history](<0022 Estimating GB history.md>) - the pipeline that runs this verb over real GB
  telemetry
- [ch. 23, Replaying estimated states](<0023 Replaying estimated states.md>) - viewing estimated history in the
  apps
- [ch. 27, Validation tools](<0027 Validation tools.md>) - `crossval`, `crossval_estimation.py` and
  `se_residual_check.py`
- [ch. 31, Exports and formats - a field guide](<0031 Exports and formats.md>) - the export schemas field by field
- [ch. 2, Loading networks](<0002 Loading networks.md>) - case selection, MATPOWER files and JSON folders
