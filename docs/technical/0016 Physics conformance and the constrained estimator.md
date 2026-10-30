# 16. Physics conformance and the constrained estimator

This chapter specifies the physical-law audit: the function `PhysicsLaws.Audit` that scores a solved or estimated grid
state against ten named laws, the residual each law reports, the tolerance it is held to, the five laws that gate
acceptance, and the case-by-law conformance matrix that `gridsim conformance` writes. The audit
(`src/GridSim.Core/Physics/PhysicsLaws.cs`) is a positive-sequence phasor check. It recomputes every branch flow and
bus injection from the solved voltages and the branch data by its own per-branch path and never calls a power-flow
solver, so a pass is corroboration that the voltages satisfy the network equations.

The Feasible estimator (`EstimationMethod.Feasible`) uses the audit as its acceptance gate. This chapter specifies the
gate: which laws it scores, against which injections, at which tolerance, and when it demotes a state. The estimator's
solve (the interior guard, the active-set KKT, the observability and bad-data machinery) is specified in
[ch. 6, State estimation](<0006 State estimation.md>). It is not repeated here. The power flow whose states are
audited is [ch. 3, Power flow](<0003 Power flow.md>); the per-unit conventions and the branch stamp are in
[ch. 2, Data model and per-unit conventions](<0002 Data model and per-unit conventions.md>). Running the verbs is
[ch. 27, Validation tools](<../manual/0027 Validation tools.md>).

## Model

### The ten laws

GridSim is a positive-sequence phasor model with an electromechanical frequency layer. The circuit laws and AC
synchronism are checkable against it. The field laws are not, and the audit reports them as `Constitutive` or
`NotModeled` with no fabricated residual. `PhysicsCheckRegistry.Names` (`src/GridSim.Core/Physics/PhysicsCheckRegistry.cs`)
holds the ten labels; the audit reads them from there, so a label is written once.

| # | Law | Class | Residual | Unit | Status rule |
|---|---|---|---|---|---|
| 1 | Conservation of energy | hard | `abs(sum_i P_net_i - TotalLossMw)` | MW | `Holds` when the residual is at most `tol` |
| 2 | Ohm's law | hard | largest per-branch-end difference between the recomputed flow and the solver's `LineFlow` | MVA | `Holds` when at most `tol` |
| 3 | Joule heating | hard | `abs((sum_k L_k + shuntLoss) - TotalLossMw)` | MW | `Holds` when at most `tol` |
| 4 | Faraday induction | constitutive | none (`NaN`) | `-` | always `Constitutive` |
| 5 | Ampere's law (passivity) | reported | `max(0, -min_k L_k)` | MW | `Constitutive` when `min_k L_k >= -tol`, else `Violated` |
| 6 | Kirchhoff's current law | hard | largest real injection mismatch over the constrained buses | MW | `Holds` when at most `tol`; `NotApplicable` in an `AuditAgainst` with no real injection named |
| 7 | Kirchhoff's voltage law | structural | `0` | V | always `Holds` |
| 8 | Complex power S = V I* | hard | `max(r6, rq)`, the larger of the real and reactive injection mismatches | MVA | `Holds` when both are at most `tol`; `NotApplicable` as law 6, with no reactive injection named either |
| 9 | AC phase and frequency synchronism | dynamics | larger of the normalised angle and frequency margins | margin | `Holds` when at most 1.0; `NotApplicable` without a `SynchronismSample`, with a detail starting `NOT SCORED:` |
| 10 | Gauss's law / Coulomb | not modelled | none (`NaN`) | `-` | always `NotModeled` |

`tol` is `tolMva`, compared with the MW or MVA number directly, not in per-unit. When a caller passes none it is
`PhysicsLaws.DefaultTolMva(model) = BaseMva * 1e-8` (see "Where tolerances come from"). The unit of law 9 is `Hz` on a
steady-state snapshot (where it is `NotApplicable` and has no residual) and `margin` when a dynamics sample is scored.

### Independent recomputation

For every in-service branch whose two ends are in the solved result, the audit forms the branch end currents and
complex powers from the solved voltages with the branch pi-model stamp:

```
v_i              = Vm_i * (cos(Va_i) + j sin(Va_i))               Va in radians
(Yff,Yft,Ytf,Ytt)= AdmittanceStamp.Branch(R, X, B, TapRatio, PhaseShiftDeg)
I_from           = Yff*v_f + Yft*v_t           I_to = Ytf*v_f + Ytt*v_t
S_from           = v_f * conj(I_from)          S_to = v_t * conj(I_to)          (pu)
Sbus[f]         += S_from                      Sbus[t] += S_to
L_k              = Re(S_from + S_to) * BaseMva                                    (MW, branch k)
```

A bus shunt `(Gs + jBs)/BaseMva` contributes `S = v_i * conj(ySh * v_i)` to `Sbus[i]`, and its real part, times
`BaseMva`, to `shuntLoss`. `Sbus[i]` is the complex power the network draws at bus i, so it equals the specified
injection (generation minus load) at a bus the solver balanced. Then:

```
P_net_i   = Re(Sbus[i]) * BaseMva              Q_net_i = Im(Sbus[i]) * BaseMva
r1        = abs(sum_i P_net_i - TotalLossMw)
r3        = abs((sum_k L_k + shuntLoss) - TotalLossMw)
r2        = max over aligned branches, over the four terms
            |Re(S_from)*B - PFromMw|, |Im(S_from)*B - QFromMvar|,
            |Re(S_to)*B   - PToMw|,   |Im(S_to)*B   - QToMvar|            (B = BaseMva)
r6        = max over i with constrainP[i]:  |P_net_i - specP_i|
rq        = max over i with constrainQ[i]:  |Q_net_i - specQ_i|
r8        = max(r6, rq)
r5        = max(0, -min_k L_k)                              minimum over audited branches
```

A branch is aligned when its ordinal is inside `LineFlows` and the flow's `From` and `To` equal the branch's. An
out-of-service branch is electrically absent: it is skipped, and so is a branch with an endpoint missing from the
result, but the ordinal advances for both so that every later branch is compared with its own flow. Laws 1 and 3 read
one quantity, the total dissipation, two ways (the sum of net injections and the sum of branch plus shunt losses), and
agree to rounding. Law 2 is the only law that compares against what the solver reported per branch.

The audit takes the branch stamp from `AdmittanceStamp.Branch`, the same function the Y-bus uses. It is independent of
the solver's nodal `Y V` product and of its iteration, and shares the stamp arithmetic, which
`AdmittanceStampPinTests` pins separately. `tools/crossval/physics_audit.py` rebuilds the admittance matrix in NumPy
and is the cross-language backstop.

### Specified injections for laws 6 and 8

```
specP_i = sum(Pg at bus i) - pLoad_i          specQ_i = sum(Qg at bus i) - qLoad_i
(pLoad_i, qLoad_i) = LoadAtVoltage.Mw(bus i, Vm_i)
```

`LoadAtVoltage` returns the nominal `Pd`, `Qd` untouched for a constant-power bus and `Pd * PMultiplier(Vm)`,
`Qd * QMultiplier(Vm)` for a voltage-dependent ZIP load, the rule the Newton mismatch uses. The constrained components
depend on which entry point is called.

| Entry point | `constrainP[i]` | `constrainQ[i]` | `specP`, `specQ` |
|---|---|---|---|
| `Audit` | bus is not `Slack` | the bus ENDED the solve as `Pq` (`PowerFlowResult.SolvedBusTypes`; the declared type when the solver does not report one) | the model's `Pg - load`, as above; at a declared-PV bus pinned at a reactive limit, the nearer of `sum(Qmax) - qLoad` and `sum(Qmin) - qLoad` |
| `AuditAgainst` | the bus appears in `SpecifiedInjections.PMw` | the bus appears in `SpecifiedInjections.QMvar` | the caller's value, replacing the model's, for exactly the named components |

`Audit` is the right test of a power-flow solution of the case-file dispatch. It is the wrong test of a state estimate,
because the real injections differ from the case file by megawatts. `AuditAgainst(model, result, specified, tolMva,
synchronism)` therefore scores laws 6 and 8 against the injections the state is bound to. Buses the specification does
not name are unconstrained, since the state alone is the authority on what flows there. A specification that names a
bus absent from the state, or a non-finite value, throws `ArgumentException`: silently skipping either would make the
verdict claim a check that never ran. When a specification names no real injection, law 6 is `NotApplicable` with a
`NaN` residual, and law 8 is `NotApplicable` when it names no real and no reactive injection; there is never a vacuous
`Holds`. `AuditAgainst` with the case-file injections reproduces `Audit` bit for bit
(`FeasibleKclGateTests.AuditAgainst_with_the_static_dispatch_reproduces_the_plain_audit_bit_for_bit`).

### Law 9: synchronism

A steady-state power flow is a snapshot at the model frequency, so law 9 is `NotApplicable`. A dynamics caller supplies
a `SynchronismSample` and the law is scored on a real residual:

```
angleMargin = MaxAngleSpreadDeg / PoleSlipLimitDeg        (infinite when the limit is not positive)
freqMargin  = MaxFreqExcursionHz / FreqBandHz             (infinite when the band is not positive)
residual    = max(angleMargin, freqMargin)
if FormingBoundHz > 0:  residual = max(residual, MaxFreqExcursionHz / FormingBoundHz)
status      = Holds when residual <= 1.0, else Violated
```

`SynchronismSample` defaults are `PoleSlipLimitDeg` 180, `FreqBandHz` 0.8 and `FormingBoundHz` 0 (inactive). A residual
below 1 means synchronism held with headroom. Law 9 is never in the hard-gate set.

No verb or export supplies a sample (D-DOC-188, October 2026), so outside the tests law 9 always reads `NotApplicable`
and its detail starts `NOT SCORED:`. The one verb with machine trajectories, `stability --clear`, gives its own
synchronism verdict from the integrator's pole-slip test (`ClassicalMultiMachine.StayedInSynchronism`: any machine more
than 2 pi from the centre of inertia), on a different angle limit and with no frequency band, so a second verdict from
law 9 there could contradict it; it is not wired.

### The hard-gate set and the gate

```
PhysicsCheckRegistry.HardGateLaws = { 1, 2, 3, 6, 8 }
Gate(hardLaws) = every law whose number is in hardLaws has Status != Violated
```

The hard laws are those whose violation is physically impossible: a state that breaks one is not a network state. The
gate passes a hard law that is `NotApplicable`, so a gate verdict reads "no hard law is violated", not "every hard law
was checked". `PhysicsLawReport.AllHold` is the stricter test that no law at all is `Violated`. The other five laws are
reported and never gate: 4 and 5 are constitutive, 7 is structural, 9 is a dynamics property and 10 is outside the
model. Laws 6 and 8 share one residual (law 8 is the larger of the real and reactive parts); law 1 does not use a specified
injection and checks the total dissipation against the reported loss.

### Joule and Ampere: the passivity split

Joule heating gates the consistency of the reported total loss. Passivity, that every branch dissipates `I^2 R >= 0`,
is Ampere's law (law 5) and is reported when violated but never gated. Some case data carries a branch with negative
resistance, a network-reduction artefact. A negative `I^2 R` is a property of the model, not of the state, and neither
the solver nor the estimator can repair a model. `case145` is the bundled example. Its audit holds laws 1 to 3, 6 and 8,
reports law 5 `Violated` with the most negative branch loss, and the gate passes (see the worked example).

### Where tolerances come from

One documented rule (D-DOC-125, October 2026): the default tolerance is the power-flow convergence tolerance on the
case's own base, `DefaultTolMva(model) = BaseMva * DefaultTolerancePu` with `DefaultTolerancePu = 1e-8` (equal to the
`PowerFlowOptions.Tolerance` default; a test pins them together). Newton stops when the largest bus mismatch is below
1e-8 pu, so a converged solve promises exactly `BaseMva * 1e-8` MVA: laws 6 and 8 re-derive that mismatch by another
path, and laws 1, 2 and 3 are identities that hold to rounding. On a 100 MVA base the rule is exactly the double 1e-6,
the old absolute default, so every 100 MVA verdict is unchanged; on the 10,000 MVA `uk` base it is 1e-4, where the old
1e-6 failed a converged solve (law 6 1.06e-6 MW, law 8 2.48e-6 MVA); on the 1 and 10 MVA distribution bases it is
tighter than before. The rule scores the `physics` block of the solve export, the default of `physics-check` and
`conformance` (whose `--tol` sets one absolute line instead; the default was an absolute `1e-4`), the scheduler job
`physics` (also enabled by the job name `conformance`; the `gridsim conformance` verb is separate), the web conformance
panel (`FrameProjector.BuildConformance`), the WPF experimental view, and the sidecars `physics_audit.py` and
`residual_check.py` (whose defaults were an absolute 1e-4 and 1e-6). The Feasible estimator's gate passes
`FeasibilityConstraints.AuditTolMva`, default `1e-4`, its own documented envelope setting. The false-data-injection
screen uses its own `0.1 * BaseMva` (see ch. 6).

The estimate exports (`estimate-export.json` and the `--export-solve` `estimated-solve-export.json`) are scored against
the estimate's own state, not the case-file dispatch (D-DOC-040, `EstimateExport.PhysicsReport`): when the Feasible gate
ran, its own report is written (KCL against the hard zero-injection rows at `AuditTolMva`); otherwise `AuditAgainst` with
no specified injection at `DefaultTolMva`, so laws 1, 2 and 3 are scored and laws 6 and 8 read `NotApplicable`. The
`physics` block names which in an additive `basis` field: `case-dispatch`, `zero-injection-constraints` or
`estimate-state`.

### The conformance sweep

`gridsim conformance` is the audit over a fixed corpus. The corpus is nine embedded cases (case9, case14, case30,
case57, case118, case145, case300, case24_ieee_rts and cigre_mv) plus every `.m` file under `data/matpower/` whose stem
is not one of those nine (those nine stems are the embedded copies), 27 cases in all. `BuildCases` assembles the list and
`RunConformance` sorts it by a size key and then by label: the key is the bus count for an embedded case and, for a file,
the first six digits of all the digits in the stem joined together (0 when there are none). For each case, with `PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true }` and the selected engine:

```
load fails or the solve throws        -> skipped    (message on the error stream)
not Converged, on the allowance list
  with the status the list expects    -> allowed (row printed with "allowed", reason listed, no audit, no CSV rows)
not Converged, otherwise              -> unconverged (row printed, no audit, no CSV rows)
otherwise  report = Audit(model, result, tol or DefaultTolMva);  gate = report.Gate(HardGateLaws)
           worst  = the hard law with the largest residual (NaN counts as 0; a tie keeps the lower number)
```

The matrix has one row per case. `conformance.md` (the table printed to the console) has the columns `case`, `buses`,
`conv`, `gate`, `worst hard law` and `residual`. `conformance.csv` has one row per converged case per law, after
`# `-prefixed run-stamp lines, with the columns `case,buses,converged,gate_pass,law_number,law_name,status,residual,
unit,tolerance,commit`; `residual` is written with six decimals of mantissa (`E6`) and is empty for a `NaN`.
`run-metadata.json` is the run stamp. The exit code is:

```
exit 0   every converged case passed, none was skipped, and every unconverged case is allowed
exit 1   a converged case failed the gate
exit 3   any case skipped, or unconverged without an allowance   (overrides 1)
```

Exit 3 exists so a case that stops converging cannot disappear from the gate. On the shipped corpus three cases are
unconverged on every run: `pglib_opf_case3_lmbd`, `pglib_opf_case162_ieee_dtc` and `pglib_opf_case240_pserc`. Each is a
PGLib optimal-power-flow operating point, an optimisation solution that plain Newton-Raphson from its stored voltages
does not converge on within 100 iterations (status `MaxIterations`). They are on the explicit allowance list
(`ConformanceAllowances.KnownUnconverged`, D-DOC-039), each with its reason, matched by file stem and by the expected
status, so the shipped corpus exits 0 and any new failure still fails. An allowed case that converges is audited like any
other (a stderr note says the allowance was not needed); `--no-allowances` switches the list off.

A separate lake-scale sweep audits stored, demand-scaled states: `tools/crossval/lake_conformance.py` compares
`S = V conj(Y V)` with the stored injection of each period rather than the case file's `Pg - Pd` (see ch. 11).

### How the Feasible estimator uses the audit

After the Feasible loop converges, the estimator evaluates the state as a power-flow result
(`EstimationResult.ToPowerFlowResult`, pure evaluation, no solver call) and runs two checks
(`src/GridSim.Core/Estimation/WlsStateEstimator.cs`):

```
report = AuditAgainst(model, projected, ZeroInjectionSpecification(mm), AuditTolMva)
limits = PhysicsFeasibility.Check(model, projected, Feasibility)

lawBreach   = not report.Gate(HardGateLaws)
limitBreach = RejectOnLimitViolation and limits.Count > 0
if lawBreach or limitBreach:  Status = Infeasible,  Converged = false
```

`ZeroInjectionSpecification` names exactly the buses whose real or reactive injection a hard zero-injection constraint
row binds, at the constraint's value. The converged state must meet those to `AuditTolMva`. A telemetered injection is a
noisy observation of a device, so the estimate's departure from it is a statistical residual, judged by the chi-square
test and the bad-data loop, not a breach of Kirchhoff's law. Two constraint rows for one bus and component that disagree
throw `ArgumentException`. Laws 1, 2 and 3 are identities of the projected state and gate as before. With no
zero-injection rows, laws 6 and 8 read `NotApplicable`, the gate passes them, and the estimate is bound to no nodal
injection. `AuditGate` (default true) switches the gate off; `Feasibility.Validate()` rejects an empty or inverted
envelope before the solve starts.

The solve's convergence test accounts for step scaling: the loop stops when the largest component of the applied step is
below `Tolerance` (default `1e-8`) and either the step was left unscaled (neither a trust cap nor, for the Feasible method,
the interior guard reduced it) or the largest component of the unscaled Gauss-Newton correction is also below `Tolerance`,
so a step that a cap or the guard shrank, even to zero, cannot end the solve as converged. The rule is the one in ch. 6
(`WlsStateEstimator`, `stepScale >= 1.0 || rawUpdate < Tolerance`). The envelope fields and the rings are in ch. 6.

### The operational envelope

`PhysicsFeasibility.Check` (`src/GridSim.Core/Physics/PhysicsFeasibility.cs`) is separate from the laws. The laws ask
whether the state is a network state at all; the envelope asks whether the equipment can hold it. It reports a
`LimitViolation(Kind, A, B, Value, Limit, Detail)` for each breach and never gates by itself.

| Kind | Breach | Slack |
|---|---|---|
| `VmBand` | `Vm < lo` or `Vm > hi`, per-bus band else `VmMin`..`VmMax` | `VmBandTolPu` = `1e-9` pu |
| `GenQ` | `Qg` outside the summed `[Qmin, Qmax]` of the generators on the bus; the slack bus is exempt | `GenQTolMvar` = `1e-3` MVAr |
| `Rating` | `LoadingPercent > 100` on a branch with a positive rating | `RatingTolPercent` = `1e-6` percentage points |

`Qg` is `QInjMvar + qLoad` with the load taken at the solved voltage. `GenQTolMvar` is `NewtonShell.QLimitTolMva`
itself, so the audit and the solver cannot disagree on whether a bus is at its limit. An infinite limit is not
checked. A breach demotes a state only under `RejectOnLimitViolation`; otherwise it is reported on
`EstimationResult.LimitViolations`.

### Experimental laws 11 to 16

Law numbers above 10 are allocated in one place, `PhysicsCheckRegistry`, so two audits cannot claim one number. None is
in the hard-gate set, none is part of `Audit`, and each is reached through `gridsim experimental` and
`src/GridSim.Core/Physics/Experimental/`. Each is off by default and collapses to a trivial pass in its degenerate limit.

| # | Law | Residual | Unit | Default `tol` | Verb |
|---|---|---|---|---|---|
| 11 | Reciprocity | `max abs(Y_ij - Y_ji)` over branch pairs with no phase shift; phase shifters are excluded as non-reciprocal by design | pu | `1e-9` | `experimental reciprocity <case>` |
| 12 | Harmonic KCL | `max abs(Y(h) V(h) - I(h))` over analysed orders (h = 1 and orders with no injection skipped) and buses | pu | `1e-8` | `experimental harmonic-kcl <case>` |
| 13 | Synchronising-Laplacian row sum | `max abs(row sum of K_ext)` | MW/rad | `1e-6 * max(1, max abs(K_ext))` | `experimental modal-consistency <case>` |
| 14 | Grid passivity | `max(0, -min Re(Z_grid(jw)))` over 5 to 2500 Hz in 5 Hz steps | pu | `1e-9` | `experimental passivity <case> [--bus ID]` |
| 15 | Converter non-passive band | `max(0, -min Re(Y_conv(jw)))` | pu | `0` | none |
| 16 | COI null mode | smallest `abs(eigenvalue)` of the swing-only A matrix | rad/s | `1e-6` | `experimental modal-consistency <case>` |

A residual in laws 11, 12, 13 or 16 is a model or solve defect, not an impossible state. Law 14 reports `Constitutive`
when the driving-point impedance is positive-real and `Violated` with the worst frequency otherwise. It skips
frequencies where the network is singular and counts them in the detail line; when every frequency is singular it throws
rather than report "passive". Law 15 is always `Constitutive`: a grid-following converter is expected to show a
negative-real band near its PLL bandwidth, which the Nyquist test then adjudicates. The default bus for law 14 is the bus
of the generator with the largest `Pg`.

`experimental wave <case>` (`ContinuumWaveModel`) is not a law. It treats the linearised swing as a damped wave
equation `M d2(delta)/dt2 + D d(delta)/dt + K_L delta = P` (inertia `M = 2H/ws`, synchronising graph-Laplacian `K_L`),
whose modes `K_L phi = w^2 M phi` are the inter-area electromechanical modes, solved by the Jacobi eigensolver
`SymmetricEigen`. It is a linearisation of `ClassicalMultiMachine` and reduces to the single-machine analytic frequency in
its degenerate limit. The other experimental verbs (`trace`, `carbon`, `boundaries`) are listed in the
[CLI reference](<../manual/0019 CLI reference.md>).

## Parameters and defaults

| Name | Value | Unit | Meaning | Source symbol |
|---|---|---|---|---|
| Audit tolerance | `BaseMva * 1e-8` | MVA | default pass line of `Audit` (and the exports, verbs and sidecars); 1e-6 on a 100 MVA case | `PhysicsLaws.DefaultTolMva`, `DefaultTolerancePu` |
| `physics-check` tolerance | `BaseMva * 1e-8` | MVA | `--tol` default (an explicit value is one absolute line) | `RunPhysicsCheck` in `src/GridSim.Cli/Program.cs` |
| `conformance` tolerance | `BaseMva * 1e-8` per case | MVA | `--tol` default (an explicit value is one absolute line) | `RunConformance` in `src/GridSim.Cli/Program.cs` |
| Conformance allowances | 3 PGLib cases, status `MaxIterations` | - | unconverged cases that do not fail the run | `ConformanceAllowances.KnownUnconverged` in `src/GridSim.Cli/ConformanceAllowances.cs` |
| Gate audit tolerance | `1e-4` | MVA | the Feasible gate's pass line | `FeasibilityConstraints.AuditTolMva` |
| Hard-gate laws | `{1, 2, 3, 6, 8}` | - | laws whose violation fails the gate | `PhysicsCheckRegistry.HardGateLaws` |
| Verb power-flow options | 100 iterations, Q limits on, case-file start | - | solve used by `physics-check` and `conformance` | `PowerFlowOptions` as built in `Program.cs` |
| Law 9 pass line | `1.0` | margin | synchronism holds at or below | `PhysicsLaws.SynchronismLaw` |
| Pole-slip limit | `180` | deg | angle denominator of law 9 | `SynchronismSample.PoleSlipLimitDeg` |
| Frequency band | `0.8` | Hz | frequency denominator of law 9 | `SynchronismSample.FreqBandHz` |
| Forming bound | `0` (inactive) | Hz | extra grid-forming margin when positive | `SynchronismSample.FormingBoundHz` |
| Voltage floor | `1e-6` | pu | positivity floor of the Feasible interior guard | `FeasibilityConstraints.VmFloor` |
| Voltage band | `0.9` to `1.1` | pu | audited after convergence, per-bus override allowed | `FeasibilityConstraints.VmMin`, `VmMax`, `PerBusVm` |
| Angle-difference limit | `pi/2` | rad | across-branch ceiling the active set holds | `FeasibilityConstraints.AngleDiffMaxRad` |
| Enforce generator Q | `true` | - | hold each generator inside `[Qmin, Qmax]` at solve time | `FeasibilityConstraints.EnforceGeneratorQ` |
| Enforce branch ratings | `false` | - | hold branch flow at its rating at solve time | `FeasibilityConstraints.EnforceBranchRatings` |
| Fraction to boundary | `0.995` | - | interior-guard step fraction | `FeasibilityConstraints.FractionToBoundary` |
| Audit gate | `true` | - | run the acceptance gate | `FeasibilityConstraints.AuditGate` |
| Reject on limit violation | `false` | - | a limit breach also demotes | `FeasibilityConstraints.RejectOnLimitViolation` |
| Band slack | `1e-9` | pu | voltage-band edge tolerance | `PhysicsFeasibility.VmBandTolPu` |
| Generator Q slack | `1e-3` | MVAr | reactive-capability tolerance | `PhysicsFeasibility.GenQTolMvar` |
| Rating slack | `1e-6` | percentage points | thermal-loading tolerance | `PhysicsFeasibility.RatingTolPercent` |

## Invariants

| Invariant | Pinned by |
|---|---|
| A true power-flow solution holds every hard law at `1e-4` MVA on case9, 14, 30, 57, 118 and 300, and no law is `Violated` | `PhysicsAuditTests.A_true_solution_holds_every_hard_law` |
| The audit reports ten laws in order, with Faraday and Ampere `Constitutive`, Gauss `NotModeled` and synchronism `NotApplicable` without a sample | `PhysicsAuditTests.All_ten_laws_are_reported_in_order` |
| A physically impossible state (a negative voltage magnitude) fails the gate | `PhysicsAuditTests.A_negative_bus_voltage_is_caught_by_the_gate` |
| Law 9 scores a real margin: held at 40 deg spread, violated at 200 deg | `PhysicsAuditTests.Synchronism_scores_a_real_residual_from_a_sample` |
| Law labels come from the registry, with the audit's wording, and the audit source carries no hand-written headline label | `PhysicsRegistryNamesTests` |
| An exactly solved ZIP case holds laws 6 and 8 | `PowerFlowPhysicsConsistencyTests.Physics_audit_of_an_exactly_solved_zip_case_holds` |
| A branch the audit cannot check does not misalign later branches with their line flows | `PowerFlowPhysicsConsistencyTests.Physics_audit_keeps_line_flow_alignment_after_a_branch_it_cannot_audit` |
| `AuditAgainst` with the case-file injections equals `Audit` bit for bit; bad specifications and contradictory constraints fail loud | `FeasibleKclGateTests` |
| A noisy Feasible estimate is not demoted by the case-file dispatch, KCL is checked at the zero-injection bus, and the gate still demotes a state that breaks a constraint | `FeasibleKclGateTests.Noisy_feasible_estimate_is_not_demoted_by_the_static_dispatch`, `Gate_still_demotes_a_state_that_breaks_a_zero_injection_constraint` |
| The acceptance gate demotes a state that fails a hard law; limit breaches are reported and do not demote by default | `FeasibleEstimationTests.The_acceptance_gate_demotes_to_Infeasible_when_a_hard_law_fails`, `Operational_limits_are_reported_but_do_not_demote_by_default` |
| Feasible keeps every voltage positive and equals Constrained on clean data | `FeasibleEstimationTests.Feasible_keeps_every_voltage_strictly_positive`, `Feasible_matches_Constrained_on_clean_data` |
| An empty or inverted envelope fails loud before the solve | `FeasibilityEnvelopeValidationTests` |
| The export `physics` block carries the gate verdict, the hard-law list and all ten laws with the full field set | `PhysicsExportContractTests` |
| Experimental law numbers are unique, named, above 10 and never hard-gate laws | `ExperimentalLawNumberTests.Experimental_law_numbers_are_unique_named_and_never_hard_gates` |
| The hard-gate set is the definition of "physically possible": no operational preference (band, rating) joins it | `PhysicsCheckRegistry.HardGateLaws`, read by every gate caller |

## Worked example

`gridsim physics-check case14` solves the IEEE 14-bus case in 2 Newton iterations and audits it:

```
$ gridsim physics-check case14
...
| 1 | Conservation of energy | Holds | 2.38E-013 | MW | independent dissipation 13.393272 MW (branch 13.3933 + shunt 0.0000) vs reported loss 13.393272 MW |
...
| 6 | Kirchhoff's current law | Holds | 2.01E-009 | MW | network real injection vs specified, at constrained buses |
...
| 8 | Complex power S = V I* | Holds | 1.32E-008 | MVA | same residual as law 6, reactive component included at 9 PQ bus(es), by the bus types the solve ended with |
...
GATE: PASS  -  state is physically admissible (all hard laws hold).
[exit 0]
```

Law 1 compares the sum of the per-branch dissipation (13.3933 MW, shunt 0.0000 MW) with the reported total loss
13.393272 MW and finds 2.38e-13 MW. Law 6 is the real-power mismatch at the 13 non-slack buses, 2.01e-9 MW, and law 8
is the larger of that and the reactive mismatch at the PQ buses, so the reactive mismatch is 1.32e-8 MVAr. The independent
NumPy audit of the same export agrees on the two balance residuals to the digits printed:

```
$ python tools/crossval/physics_audit.py src/GridSim.Cli/bin/Release/net10.0/results/crossval-case14/solve-export.json
physics audit: ? (14 buses), tol 0.0001 MVA
  energy 4.01e-13 MW  KCL 2.01e-09 MW  S=VI 1.32e-08 MVAr  min branch loss -2.84e-14 MW
  independent gate: PASS   exported gate: PASS   agree: True
VERDICT: PASS
[exit 0]
```

The model-passivity case. `gridsim physics-check case145` carries a negative-resistance branch, and the audit holds the
hard laws while reporting law 5 (case145 total loss 74802.589870 MW: branch -1829.8883 MW plus shunt 76632.4782 MW):

```
$ gridsim physics-check case145
...
| 5 | Ampere's law (passivity) | Violated | 2.80E+002 | MW | NON-PASSIVE branch in the model: most-negative branch loss -2.80E+002 MW (R < 0 in the data) |
| 6 | Kirchhoff's current law | Holds | 1.35E-007 | MW | network real injection vs specified, at constrained buses |
...
GATE: PASS  -  state is physically admissible (all hard laws hold).
[exit 0]
```

The same holds at scale. `gridsim physics-check data/gb-full` (3,539 buses) reports law 1 at 3.39e-09 MW on a loss of
523.821145 MW, law 6 at 1.81e-09 MW and law 8 at 5.00e-09 MVA, and passes the gate.

A noisy estimate is not a dispatch. `gridsim estimate case14 --synth --export-solve --out results/est-case14s` writes
`estimated-solve-export.json`. Its `physics` block is scored by `Audit` against the case-file dispatch, and so is the
NumPy audit; both fail on the same 1.90 MW real mismatch, and agree that they fail:

```
$ python tools/crossval/physics_audit.py results/est-case14s/estimated-solve-export.json
physics audit: ? (14 buses), tol 0.0001 MVA
  energy 2.49e-14 MW  KCL 1.90e+00 MW  S=VI 1.35e+00 MVAr  min branch loss -3.55e-15 MW
  independent gate: FAIL   exported gate: FAIL   agree: True
VERDICT: FAIL
[exit 1]
```

Energy conservation holds to 2.5e-14 MW, since the projected state is exact network physics, and the injections differ
from the case file by the telemetry noise. This is why the Feasible gate uses `AuditAgainst` against the zero-injection
rows and not `Audit` (`FeasibleKclGateTests.Noisy_feasible_estimate_is_not_demoted_by_the_static_dispatch` proves the
case-file audit would reject that state).

The sweep, `gridsim conformance`, over the shipped corpus gives 24 audited cases, all `PASS`, with worst hard-law
residuals from 1.69e-12 (case9) to 2.20e-07 (case1354pegase), and 3 unconverged PGLib operating points (exit 3). The
complete matrix is in [ch. 27](<../manual/0027 Validation tools.md>).

## Source map

| File | Type | Responsibility |
|---|---|---|
| `src/GridSim.Core/Physics/PhysicsLaws.cs` | `PhysicsLaws`, `SpecifiedInjections` | `Audit`, `AuditAgainst`, the independent recomputation, laws 1 to 10 |
| `src/GridSim.Core/Physics/PhysicsLawReport.cs` | `LawStatus`, `LawResult`, `PhysicsLawReport`, `SynchronismSample` | status, per-law result, `Gate`, `AllHold`, the dynamics input |
| `src/GridSim.Core/Physics/PhysicsCheckRegistry.cs` | `PhysicsCheckRegistry` | law names, `HardGateLaws`, experimental law numbers |
| `src/GridSim.Core/Physics/PhysicsFeasibility.cs` | `PhysicsFeasibility` | the operational envelope check |
| `src/GridSim.Core/Estimation/FeasibilityConstraints.cs` | `FeasibilityConstraints` | the Feasible envelope, `Validate`, `AuditTolMva` |
| `src/GridSim.Core/Estimation/WlsStateEstimator.cs` | `WlsStateEstimator` | the acceptance gate, `ZeroInjectionSpecification`, the convergence test |
| `src/GridSim.Core/Estimation/FeasibleWlsStep.cs` | `FeasibleWlsStep` | the active-set KKT (ch. 6) |
| `src/GridSim.Core/IO/SolveExport.cs`, `EstimateExport.cs` | `SolveExport.PhysicsBlock` | the additive `physics` block |
| `src/GridSim.Cli/Program.cs` | `RunPhysicsCheck`, `RunConformance`, `BuildCases` | the two verbs and the corpus |
| `src/GridSim.Core/Physics/Experimental/` | `ReciprocityCheck`, `HarmonicKclCheck`, `SmallSignalConsistency`, `ImpedancePassivityCheck`, `ContinuumWaveModel` | laws 11 to 16 and the wave model |
| `src/GridSim.Core/Scheduling/AnalysisJobLibrary.cs` | `PhysicsAudit` | the scheduler job |
| `src/GridSim.Web/Projection/FrameProjector.cs` | `BuildConformance` | the web conformance panel |
| `tools/crossval/physics_audit.py` | script | independent NumPy recomputation of laws 1, 6 and 8 and the exported-gate cross-check |
| `tools/crossval/residual_check.py` | script | independent admittance matrix and power-balance residual |
| `tools/crossval/lake_conformance.py` | script | the audit over the stored GDA lake |

## Limits

- No verb or export supplies a `SynchronismSample`, so law 9 reads `NotApplicable` (`NOT SCORED`) outside the tests.
- A solver that does not report the bus types it ended with (the backward/forward sweep, a presolved state read from
  disk) leaves `SolvedBusTypes` null, and laws 6 and 8 then use the declared types, as before October 2026.
- The `physics` block's `gatePass` is the hard-law gate only; whether the solve converged is the separate `converged`
  field of the export (`physics-check` itself fails a non-converged solve, exit 3).
- `experimental wave` builds a non-symmetric synchronising Laplacian on a solved case and feeds it to a solver that reads
  one triangle, so the centre-of-inertia eigenvalue lifts off zero and the dominant inter-area mode it reports can be that
  lifted mode (R035).

## See also

- [ch. 6, State estimation](<0006 State estimation.md>) - the Feasible method's solve, rings and envelope fields.
- [ch. 3, Power flow](<0003 Power flow.md>) - the solves the audit scores.
- [ch. 11, Validation](<0011 Validation.md>) - the cross-checks, including `residual_check.py` and `lake_conformance.py`.
- [ch. 10, File and wire formats](<0010 File and wire formats.md>) - the export contracts that carry the `physics` block.
- [ch. 27, Validation tools](<../manual/0027 Validation tools.md>) - running `physics-check`, `conformance` and the sidecars.
- [CLI reference](<../manual/0019 CLI reference.md#physics-check>) - the verb flags.
