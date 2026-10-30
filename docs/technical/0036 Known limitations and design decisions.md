# 36. Known limitations and design decisions

This chapter lists behaviour that a full adversarial review of the code (October 2026) found questionable, and that is
kept as it is on purpose. For each entry it says what happens, when it shows, and why the behaviour stays. Two reasons
recur:

- **Validated numbers.** GridSim's results are pinned bit for bit across runs, engines and CPUs. A change that would
  move pinned numbers, verdicts or exit codes is made only together with a re-validation of those pins on the full
  test machine; until then the present behaviour is the validated one, and its limit is stated here.
- **Design.** Some entries are choices (an interface, a file format, a protocol contract) where the alternative is a
  different product decision, not a fix.

An entry here is not a hidden defect: it is the documented behaviour of this release. Each entry names where it
lives, so a later release can lift it deliberately.

## Numerics

- **Badly scaled matrices are refused as singular.** The sparse and dense LUs treat a pivot at or below
  `RelativePivotFloor` (1e-12) times the largest entry of the whole matrix as numerically singular. A matrix whose
  rows or columns differ in scale by more than about 1e12 is refused even when row or column scaling would make it
  well conditioned. Power-flow Jacobians and estimator gains in per-unit are far inside that range; a hand-built system
  with mixed units can meet it. (`SparseLuFactorization`, `DenseLuSolver`.)
- **The eigenvector check confirms consistency, not convergence.** `GeneralEigen` checks that the left vectors
  (the rows of `V^-1`) are biorthonormal to the right ones, which holds for any invertible `V`. Inverse iteration
  returns its last iterate after its sweep cap without a separate residual verdict, except for the deflated
  repeated-eigenvalue clusters, whose residuals are checked. Participation factors of two very close modes can
  therefore be less accurate than the eigenvalues themselves. (`GeneralEigen`.)
- **The bounded simplex uses absolute feasibility and pivot thresholds.** Phase I accepts a total artificial
  infeasibility of 1e-7, and the eta pivot floor (1e-12) and refactor clamp (1e-14) are absolute. On a very large
  case in MW the round-off can exceed 1e-7 and a feasible LP can be reported infeasible; the thresholds are pinned by
  the DC-OPF security tests. (`BoundedSimplex`.)
- **Non-finite input reaches some solvers.** The CPU sparse LU and QR refuse NaN and infinite matrix entries. The
  ILGPU dense LU eliminates through a NaN (as the CPU dense LU does) and returns a NaN solution; the ROCm dense LU
  refuses it; neither GPU solver checks the right-hand side, and the GPU solvers leave the input matrix untouched
  where the CPU dense LU overwrites it with its factors. (`GpuDenseLinearSolver`, `RocmDenseLinearSolver`.)
- **A ROCm or ILGPU device fault ends the run.** A device allocation or library failure inside a GPU sparse solve
  throws instead of returning null, so the Newton shell's CPU retry does not run and the power flow stops with the
  device error. A failure is loud; which device faults should fall back silently is a deployment decision.
  (`RocmSparseLinearSolver`, `GpuSparseLinearSolver`.)
- **GPU selection is simple.** OpenCL and CUDA use the first device of each platform; a later FP64-capable device
  on the same machine is not tried. (The ROCm availability probe loads the math libraries by the solvers' own route,
  the versioned-name resolver first, since GR-043.) The CUDA driver probe runs on first use of the GPU context, inside its lock and with no timeout, so a driver that
  hangs in `cuInit` hangs every later GPU query; the manual's GPU chapter describes the symptom. (`GpuContext`,
  `RocmContext`.)
- **GPU code paths are not exercised by the test suite on a machine without a GPU.** On such a machine the GPU seam
  tests run the CPU fallback, and the ROCm P/Invoke layer has never run on hardware in CI. A device run is the
  release check for those paths; on it the CUDA and OpenCL kernel tests run, and without the device they are reported
  skipped. (`GpuSolverSeamTests`, `GpuDeviceKernelTests`, `RocmSolverSeamTests`.)
- **The no-FMA rule is guarded by a source search.** The FMA arm of the SIMD kernels is kept out of numerical paths
  by a test that searches the sources for `fastFma: true`; a call that passes `true` positionally or through a
  variable would not be caught. Every current call site passes it by name. (`SimdKernels`, `DeterministicMathGuardTests`.)
- **The Nelder-Mead tolerances are absolute.** `FTolerance` (1e-12) and `XTolerance` (1e-8) do not scale with the
  objective or the coordinates, so a fit whose objective is of order 1e6 runs to its iteration cap and reports
  `Converged = false`, and a seed coordinate near 1e-11 gets a degenerate first simplex. The frequency fitter refuses a
  result outside its physical box, so a non-converged fit is never returned as a fitted value. (`NelderMead`.)

## Power flow

- **An elected island slack takes the island's whole imbalance.** When the island pre-pass gives a generating island
  without a slack a slack of its own (the bus with the largest generation), that bus absorbs the island's entire
  load-generation imbalance, and the Newton engines never limit a slack. The decision: the solve is not changed, but
  the island report says it (`IslandReport.ElectedIslands`: each elected island's load, schedule, imbalance and the
  elected units' `[Pmin, Pmax]`; `AnyElectedBeyondCapability` flags an island the units cannot balance, before
  losses). (`IslandPartition`.)
- **The sweep's convergence test is on the voltage update.** The backward/forward sweep stops when the largest voltage
  change of an iteration falls below the tolerance (with its PV and loop checks); the bus power mismatch it reports
  afterwards is not itself tested. On a heavily loaded feeder, where the sweep converges slowly, the mismatch can be
  well above the tolerance while the result reads converged. The reported mismatch is the figure to check.
  (`BackwardForwardSweep`.)
- **Distributed-slack bounds snap out-of-range schedules.** A unit scheduled outside its `[Pmin, Pmax]` jumps to the
  bound as soon as any share is placed, and a unit scheduled below zero with the default `Pmin = 0` (a battery
  charging) is moved to zero. (`DistributedSlackPowerFlow`.)
- **The sweep's Q-limit semantics are continuous.** The sweep clamps a PV unit at its reactive limit afresh every
  iteration (as Newton does with `RevertQLimits = true`) and ignores the option; Newton's default pins a bus once. The
  engine is chosen by topology, so a radial feeder with a saturating PV unit can solve differently from the same
  feeder solved by Newton. With the sweep the OLTC hold rule falls back to the model's bus types (the sweep reports no
  solved types), so a PV bus clamped at its limit still holds its tap. (`BackwardForwardSweep`, `SteadyStateSolver`.)
- **The sweep's weak-mesh loop compensation has no closed-loop test.** The breakpoint correction for up to six links
  is exercised only by radial cases in the suite; a forced sweep (`--bfs`) on a meshed feeder relies on code that
  no test closes a loop through. Cross-check such a run against Newton. (`BackwardForwardSweep`.)
- **Physics feasibility does not require a converged result.** The check reads whatever state it is given; callers
  check convergence first (the estimator gates and the FDIA screen do). A corrupt reactive limit (NaN, `+Infinity`
  `Qmin`, `-Infinity` `Qmax`) is refused. (`PhysicsFeasibility`.)
- **Solver constants are engineering choices.** The Newton step damping (`MaxUndampedStep` 0.4), the voltage floor
  (`VmFloorPu` 0.2), the Q-limit and revert tolerances (1e-3 MVA, 1e-4 pu), the loop cap (6), the distributed-slack
  tolerance (1e-4 MW) and the experimental wave field's speed and spans are set from experience on the bundled cases,
  not taken from a cited source; each is a named constant in the solver tables.
- **Engine efficiency.** The dense Newton engine still builds its dense arrays on the CPU path although the CPU step
  is solved by the sparse LU, and the sweep's result assembly scans the generator list per bus. Both cost time and
  memory on large cases, not accuracy. (`NewtonRaphsonPowerFlow`, `BackwardForwardSweep`.)
- **`IslandReport.LostGenMw` is always zero.** An island is blacked out only when none of its generators produces
  power, so the lost generation it books is zero by construction; the field is kept for the report's shape.
  (`IslandPartition`.)

## State estimation

- **Constraint rows can mask weakly observed directions.** The constrained and feasible methods enter their equality
  rows in the observability and covariance gains at `1e4 x` the strongest free weight. With a very tight free row (a
  PMU angle at sigma 1e-4 rad) the relative rank floor rises enough that a direction observed only by a weak pseudo
  row can be reported unobservable, and covariance diagonals of such directions lose accuracy. (`WlsStep`.)
- **The EKF's first window is one linearised step.** The first update is a single Gauss-Newton step from a flat start
  with a diffuse prior, not an iterated filter, and `Converged` on a dynamic estimate means "finite". Start the filter
  from a converged static estimate when the first window matters. (`KalmanStateEstimator`.)
- **Distrusted sources keep their degree of freedom, and match by exact tag.** A distrusted row's weight is cut by
  `1e4` per sigma (1e8 in weight) but it still counts as one degree of freedom in the chi-square test, so its misfit
  is hidden in the headroom; and a source matches only by its exact (trimmed, case-insensitive) tag, so a compound tag
  such as `pseudo-demand+comp-range` is not matched by `pseudo-demand`. Name the exact tags to distrust.
  (`DistrustedSources`.)
- **FDIA screen bounds.** A bus quantity's uncertainty margin takes the smallest sigma of the rows measuring it, but
  branch rows sharing a `(From, To, end)` key take the largest, because parallel circuits share that key (a key by
  circuit would lift it); a branch with one unmetered end gets no rating margin; the protected-anchor test compares
  the anchor residual with `k x sigma_anchor` alone, without the estimate's own variance at the anchor, so a very
  tight anchor that is not in the fit can read inconsistent on clean data. The screen is therefore conservative toward `suspected`. Its tests use case-dispatch
  synthetic data; clean noisy case30 at the default seed reads `suspected`, as the estimation chapter says.
  (`FdiaScreen`.)
- **Every slack bus is an angle reference.** A bus of type Slack loses its angle state and is held at its case-file
  angle, so a second slack in one island is a hard angle constraint, not a measured quantity. Give each island one
  slack for estimation. (`MeasurementModel`.)
- **The feasible method's generator-Q constraint uses the case-file load.** `Qg = Qinj + Qd` takes `Qd` from the
  model, not the estimated or voltage-dependent load, so on telemetry whose load differs from the case the reactive
  bound is applied to a slightly wrong injection; the post-solve feasibility audit uses the load at the solved voltage.
  (`FeasibleWlsStep`.)
- **An estimate seen as a power flow carries estimator quantities.** `EstimationResult.ToPowerFlowResult` puts the
  last state-update norm in `MaxMismatch` (it is not a power mismatch), maps an unobservable estimate to `NonFinite`
  and an infeasible one to `MaxIterations`, and rebuilds the angles from degrees (one or two units in the last place
  away from the estimator's radians). Read the estimation result itself for the estimator's verdict.
  (`EstimationResult`.)
- **Kalman filter numerics.** The EKF and UKF store an exactly symmetrised covariance after each window, and the UKF
  refuses a posterior variance that is not positive (the filter keeps its previous state); the UKF's default
  `alpha = 1e-3` still gives central weights near -1e6, which costs about six significant digits in its sums. The process-noise and initial-variance defaults are engineering values, not cited data,
  and the frequency filter's process noise is not the discretised white-noise form. (`KalmanStateEstimator`,
  `UnscentedKalmanStateEstimator`, `RandomWalkProcessModel`.)
- **Robust methods' chi-square.** Huber and Schweppe-GM report their chi-square as a verdict although J at an
  M-estimate is not the WLS minimum, so a robust fit that down-weighted an outlier can read FAIL; only LAV marks its
  chi-square as no verdict. Read the robust weights with the verdict. (`WlsStateEstimator`.)
- **A degenerate equality block reads unobservable under the constrained method.** Two duplicate zero-injection rows
  at one bus make the KKT system singular; the feasible method re-gates and reports infeasible, the constrained
  method reports unobservable. Remove duplicate constraint rows. (`WlsStateEstimator`.)
- **Measurement feeds and resolvers.** Phase-angle rows carry a per-row source tag, so each forms its own noise-floor
  class and a distrust of `phaseangle` matches none of them; and the linear PMU estimator resolves `#k` circuits and
  out-of-service branches by its own rule, which can differ from the WLS measurement set's resolver.
  (`PhaseAngleFeed`, `LinearPmuStateEstimator`.)
- **The exact LAV retry loop can repeat a step.** When the plain step is rejected and a weighted step predicts no
  decrease, the loop returns to the plain step, so it can alternate until its 40 retries are spent and then report
  failure; each retry warm-starts from the previous LP basis, so the repeats are not provably identical and the loop
  is kept as validated. (`WlsStateEstimator`.)
- **Test coverage gaps.** There is no exact dense-versus-sparse test of the constrained or feasible gate with a free
  weight of 1e8 beside pseudo rows; the dense GPU step is checked against the sparse step to solver tolerance only.

## Dynamics

- **Distributed synchronising stiffness is the network-only reduction.** The coupling `K` between speed domains is
  the bus admittance reduced onto the generator terminal buses, without the machines' transient reactances or the
  loads in series. It overstates `K`, so the inter-domain mode is faster and the domains are tied more tightly than the
  internal-EMF reduction would give; read the distributed model's inter-area behaviour as an upper bound on coupling.
  A generator bus shared by machines of several domains is given to the domain of the single largest machine there,
  so a domain whose machines all share buses with a larger machine of another domain has no tie of its own.
  (`DomainCoupling`.)
- **Propagation delay makes the distributed ties non-conservative.** With propagation on, domain `i` sees
  `delta_j(t - tau)` and `j` sees `delta_i(t - tau)`, so the ties carry a net power while the angles move and the
  centre of inertia drifts from the lumped trajectory by an amount set by `tau`. Use propagation for arrival times,
  not for the COI nadir. The arrival time is stamped at the end of the step in which a domain crosses its threshold.
  (`MultiDomainSwing`.)
- **The Nyquist phase-step guard warns, it does not refuse.** The encirclement count sums the smallest-angle phase
  step between consecutive samples; a step above a quarter turn trips `PhaseStepGuardTripped` (GR-096) and the command
  line prints a WARNING beside the verdict, which is still the count's. Raise `--ppd` when it appears.
  (`MinorLoopNyquist`.)
- **The small-signal GFM RoCoF leg excludes the inverters' own injection.** The COI RoCoF that a grid-forming df/dt
  term reads is the machine-driven part only; including the inverter power state is a valid state-space term that
  would move the eigenvalues. (`SmallSignalAssembler`.)
- **Converter iteration failures do not decide stability.** In the RMS study the converter current fixed point (50
  passes) may stop unsettled; the step carries on with the last iterate and the failure is counted
  (`ConverterIterationFailures`, in the events export). The critical clearing time and the stability verdicts read
  synchronism only; the command line prints a WARNING beside a single-run RMS verdict whose count is not zero, and
  the verdict and exit code are unchanged (the CCT search does not report the count). The converter's initial state is not checked
  against its current limit, its low-voltage active-current management or its dip threshold, so a converter that
  starts beyond one of them is not stationary from the first step. (`RmsTransientStability`, `RmsConverter`.)
- **Machine initialisation conventions.** Both the classical and the subtransient preparation share a bus's solved
  reactive output between co-located machines by reactive capability (`Qmax - Qmin`, else `MBase`) and its active
  output by scheduled `Pg` (the classical one shared both by `Pg` before GR-083, so a 0 MW voltage-control machine
  carried no MVAr; a bus with one machine is unchanged). The subtransient AVR regulates the voltage
  behind the unit transformer (the reactances include it), not the machine terminal; the equilibrium is consistent, so
  undisturbed runs are stationary. The swing damping `D` is used as given while `H` and the reactances are moved to
  the system base, the convention of the dynamic-model chapter. The governed classical layer runs each TGOV1 on its
  machine's base when given the MBase / system-base ratios (`machineToSystemBase`); without them it feeds the governor
  system-base power, as before (no production caller uses it). (`ClassicalInit`, `MultiMachineSubtransient`,
  `SubtransientInit`, `GovernedMultiMachine`.)
- **Two routes to system inertia.** Some lumped engines receive `SystemInertiaGvaS * 1000`, others `SystemInertiaMwS`;
  the two can differ in the last bit, so the same model can give last-bit-different RoCoF and nadir through different
  entry points (each entry point is itself deterministic). (`LumpedScalarInertiaModel`, `AssetFrequencyDynamics`.)
- **Measured frequency traces are taken as given.** Trace RoCoF and upsampling refuse a backwards time stamp, skip a
  repeated one (no interval to take a rate over) and interpolate linearly across a gap, so a gap of minutes reads as a
  gentle slope. Check a trace's cadence before scoring it. (`RocofUpsampler`, `FrequencyLimits.AssessTrace`.)
- **Impedance and small-signal edges.** A SISO impedance scan still computes the dq impedance at every reporting point
  and refuses a reporting frequency within 1e-4 of the fundamental; a grid-following converter's small-signal droop
  is scaled by its `MBase`, so a record without a rating takes the default 100 MVA (a zero, negative or non-finite
  rating is refused). (`ImpedanceStabilityScan`, `SmallSignalAssembler`, `LinearInverterModel`.)

## Distribution feeders and harmonics

- **The coupled harmonic flow treats each bus's converters as one.** Converters at one bus add one Norton admittance
  and their voltage-dependent injection law is evaluated once on the bus's summed base injection; that is exact for a
  linear law and an aggregate for a nonlinear one. (`CoupledHarmonicPowerFlow`.)
- **The long-line correction's square root is the BCL's (decision).** It uses `System.Numerics.Complex.Sqrt`, which
  is managed .NET code built from IEEE 754 operations that are exact or correctly rounded (`Math.Sqrt`, `Math.Abs`,
  scaling), so it gives the same bits everywhere; `DetMath` lists it among the routines that stay on the BCL for that
  reason, and its transcendental neighbours (`Sinh`, `Tanh`) are GridSim's own. (`FrequencyDependentYBus`.)
- **Line codes are used as written.** The loader requires the shunt matrix to be exactly symmetric and the series `r`
  and `x` symmetric to a relative 1e-9 (a larger difference is refused as a transposed or mistyped entry); the bundled
  IEEE 4-node code is symmetric only to the last bit (about 2e-16, computed offline) and is used as written, not
  symmetrised. (`MultiPhaseJsonLoader`.)
- **The unbalance factor of a two-phase bus.** `VoltageUnbalancePercent` is computed from all three phase voltages,
  so on a single- or two-phase bus (an absent phase is exactly zero) the figure has no meaning; a dead bus returns
  NaN. (`ThreePhaseSweep`.)
- **A Core-built gy-gy unit on a three-wire system.** The loader refuses a grounded-wye transformer fed from a
  three-wire system; a model built in code bypasses that check: an off-nominal unit (an ideal ratio in Core) is refused
  by the sweep, whose message names both a regulator and a gy-gy ratio since it cannot tell them apart, and a rated one
  (a plain series impedance in Core) is solved with no neutral. Build feeders through the loader. (`ThreePhaseSweep`.)
- **Thresholds and references.** The unequal-winding transformer equivalents are self-consistent and pinned in their
  equal-winding limit, but not checked against a published unequal-winding case; the long-line threshold
  (`|gamma l| < 1e-9`), the Norton default conductance (0.05 pu) and the off-nominal band (0.8 to 1.2) are engineering
  values; the IEEE 519 limit table matches the 2014 edition's Table 1, and its tier boundaries are not pinned by a test.
  (`TransformerWindings`, `FrequencyDependentYBus`, `HarmonicCompliance`.)
- **Untested fail-loud paths.** No test covers a single-line-to-ground fault with a non-finite pre-fault EMF, the
  coupled flow with a NaN injection, a harmonic source on an unknown bus at Core level, or a build exception on the
  parallel penetration path; each of those refuses by code today.

## Short circuit and protection

- **IEC 60909 method and data edges.** Method C's DC-component table ends at `f t = 10` cycles and refuses longer
  breaking times the standard's table may still cover (the table values are marked for verification against the
  standard). The multiple-fed breaking current weights each machine by the voltage at its network bus, which with a
  unit transformer includes the transformer drop, so the deduction is too large and `Ib` reads low. A unit whose
  step-up transformer is an explicit branch gets the generator and network-transformer factors (`K_G`, `K_T`) rather
  than the power-station-unit factor `K_S`. (A machine with no rating, `MBase <= 0`, is refused by name: the
  near-generator test, mu and a converter's `k x I_rG` need its rated current. An element on a bus with no base voltage
  is not given an impedance correction and is listed as not corrected.) (`Iec60909`.)
- **The zero-sequence network is two-winding only.** YNyn is a series path, YNd and dYN a shunt path, and every other
  pairing blocks; a YNyn unit with a delta tertiary (an autotransformer YNa0d), a zigzag or an earthing transformer is
  not represented, so earth-fault infeed through such a unit is understated and single- and double-line-to-ground
  duties are non-conservative there. (`Iec60909`.)
- **Breaker checks: design decisions.** The rated voltage is compared with the highest system voltage `Um` (the lowest
  IEC 60038 `Um` at or above the nominal voltage), not the nominal voltage. The DC (asymmetrical) breaking duty
  `sqrt(Ib^2 + idc^2)` is checked only when the rating gives an asymmetrical breaking current (the sixth `--breaker`
  value): a rating without one is not assumed to have any particular DC capability. A double-line-to-ground duty is
  checked at its largest line-conductor current. An ungrounded bus's double-line-to-ground duty is still the all-zero
  earth-current duty; check such a bus with the line-to-line duty. (`Iec60909`.)
- **Operating-point fault studies use the whole model.** The operating-point study builds the admittance matrix over
  every bus; an isolated bus (no branch, load, shunt or machine) is grounded through a unit admittance, decoupled from
  the rest, so a fault elsewhere is unaffected and a fault on it is refused by name (GR-135; it made every fault fail
  before); a bus missing from the pre-fault result takes its stored voltage; and the protection context
  derives zero current where both ends of a branch have collapsed, so an undercurrent element can operate on a branch
  carrying fault current. (A bus the protection context cannot find in the result is refused.) (`FaultAnalysis`,
  `ProtectionContext`.)
- **Relay models.** The thermal (49) replica starts cold for a steady-state overload (as the protection chapter
  states), the auto-recloser has no shot counter or lockout (it trips and recloses onto a persistent condition for as
  long as it lasts), and inverter low-voltage ride-through trips on a single sample below its threshold rather than on
  a voltage-time envelope. (`ProtectionRelays`, `ProtectionEvaluator`, `GridFollowingInverter`.)
- **Contingency netting of a worsened base-case violation: design decision.** Netting keeps the D03 verdicts: an
  outage whose only violations were already present in the base case reads secure ("base only"). A pre-existing
  violation the outage makes materially worse (loading up by more than 1 percentage point, or a bus 0.005 pu further
  outside the band) is always listed in `WorsenedPreExistingViolations` and named under the screen's table; a caller
  that wants it to count against the outage passes `countWorsened: true`. N-1-1 results screened with the default
  corrective action carry `CorrectiveOutcome` (applied, not needed, infeasible, no DC model). (`ContingencyAnalysis`.)
- **Asset model edges.** The transformer thermal replica is linear in loading where the line model uses
  loading squared, and its tap delay is a running sum. (The asset envelope's median of an even sample count is the mean
  of the two middle values; a swept parameter with an infinite end is held at its authored value while the others
  swing in the tornado.) (`GridFollowingInverter`, `TransformerDynamics`, `AssetEnvelope`.)
- **Storage model edges.** The storage LP has no complementarity between charge and discharge (with a negative price
  it may do both in one period), and the reported round-trip efficiency mixes AC energy with DC stored energy at
  nominal open-circuit voltage. (`PackModel.Sized` refuses a pack more than 50 % away from the requested energy; the
  state-of-charge bucket delivers only the energy left on the step that reaches its limit; an explicit LP end-energy
  request outside the energy window is refused.) (`StorageDispatch`, `StorageFleet`.)
- **Alarm-log matching.** A sequence-of-events alarm names a branch by its unordered bus pair, so the two circuits of a
  double circuit are one key, and bus id 0 is the "not a branch" marker. (`AlarmLogCsv`.)
- **External benchmarks.** The IEC 60909 suite is pinned to closed forms, hand networks and the WSCC 9-bus case, not to
  the IEC TR 60909-4 worked examples (not on disk); the IEEE relay curve constants are checked through a closed form
  that repeats them. (A breaker rating whose duration exceeds the fault duration is exercised by
  `Iec60909ShortFaultThermalTests`.)

## File formats, CIM and JSON

- **Built-in IEEE cases other than case9 load at 50 Hz.** The embedded IEEE 14, 30, 57, 118, 145, 300 and RTS-24 cases
  are 60 Hz systems; they load with the parser's 50 Hz assumption, and the assumption note is not carried with them.
  Dynamic studies of those built-ins therefore run at 50 Hz; load the `.m` file with `--frequency-hz 60` for a 60 Hz
  study. (`IeeeCases`.)
- **A bridge state with no convergence flag reads as converged.** A bridge `state` frame whose `result` block has no
  `converged` flag is taken as converged with zero iterations and mismatch, because the GDA bridge server's slim state
  frames do not carry the flag; reading its absence as "not converged" would mark every frame of today's server
  unconverged, so the change waits for a bridge protocol version whose server sends it. (A solve export with no `solve`
  block or no `solve.converged` is refused since GR-180.) (`BridgeProtocol`.)
- **The MATPOWER writer writes the operational band as per-bus limits.** A bus with no per-bus voltage limits is
  written with 0.94 / 1.06, so the re-parsed case has per-bus limits; it judges identically only while the reader's
  global band is the operational band. (`MatpowerWriter`.)
- **CIM export details.** A tap or phase shift within 1e-12 of nominal is not written, and the re-imported tap
  (`1 + step / 100`) is not bit-exact. (An exporter-generated identifier such as `bv_400`, `t_<mrid>_1` or `rc_<mrid>`
  that equals a source mRID, or two objects sharing an mRID, is refused at export, naming the identifier.)
  (`CimExporter`.)
- **CIM import details.** A load or machine with several terminals uses its first; a load or machine missing from the
  SSH profile draws or produces nothing even when other objects carry SSH values; the bridge produces a 50 Hz model
  whatever the source system. (A machine with no `ratedS` takes the system base; an SVC start moved inside its ratings
  is noted; a `urn:uuid` UUID is compared without regard to letter case.) (`CimImporter`, `CimGridModelBridge`.)
- **v2 library import order: design decision.** Two library files that both match an imported id prefix contribute
  their classes in the case's import order, then file order (no sort): the order is the one the case author wrote, so
  it is deterministic and visible in the case. A test of that order is not yet written. The v2 loader parses its schema
  set and kind registry for each validation stage of a case load (a cost of time only). (`V2CaseLoader`.)
- **The v2 loader does not resolve links.** The inside-the-case-folder test compares paths (by the operating system's
  rule) without resolving junctions or symbolic links, so a link inside the case folder can point outside it.
  (`V2CaseLoader`.)
- **The case editor reads some loader exceptions as case problems.** `InvalidOperationException`,
  `KeyNotFoundException` and `FormatException` from the loader are shown as a refusal of the case, so a loader defect of
  those types would appear as a case problem. (`CaseSession`.)
- **Generator cost files by index.** A cost file that addresses units by index with an out-of-service unit in the middle
  of `generation.json` is refused when the loader dropped rows, and is otherwise matched in file order; there is no test
  of that combination with dispatch on. (`GeneratorCostFile`.)

## Command line

- **Verbs use their own power-flow options.** Several verbs solve with the library defaults (30 iterations, no OLTC
  loop) and others with the contingency study options (100 iterations, OLTC loop), and the classical stability screen
  and single-bus study of one verb differ the same way; so two verbs can disagree about whether a hard case converges,
  or report slightly different operating points. Each verb's options are fixed and documented with the verb.
  (`Program.cs`.)
- **`convert`'s proof solve is on the CPU.** `convert` proves a written case by solving input and output and comparing
  the results bit for bit, which only the CPU engines promise, so it ignores `--gpu` by decision. (`nminus1`, `nminus2`
  and `n-1-1` use the selected backend since GR-213, as `nminus1-sweep`, `stress` and `chain` do; `nminus1
  --batched-lanes` runs its branch outages on the CPU batched solver and says so.) (`Program.cs`, `SolverBackend`.)
- **Some refusals end the process from inside a loader.** A licence refusal or an ambiguous MATPOWER name exits 2 from
  inside the loader, so a batch verb stops mid-report instead of recording a `LOAD FAIL` row, and some verbs catch every
  exception and report it as a usage or input error (exit 2 or 1) with no crash log. (`Gated`, `Program.cs`.)
- **The per-verb flag gate is lenient in two ways.** A verb accepts every `--flag` named anywhere in its section of the
  CLI reference (including flags of other verbs mentioned in passing) and the global switches, and a verb that does not
  read one ignores it; and `gda` ignores flags that do not apply to the mode it picks. (The bare run, repeated value
  flags, single-dash words, a second case on the analysis verbs and the spaced `--enableExport` path are refused since
  October 2026: GR-202, GR-670, GR-683, GR-676, GR-688; manual ch. 19, "Parsing rules".) (`VerbFlags`, `Program.cs`.)
- **"No inertia data" has two exit codes.** A case whose machines carry no inertia exits 1 for `screen` and
  `experimental wave` and 2 for `stability`, `modal` and `trip`. (`Program.cs`.)

## Web host, sessions and sign-on

- **The estate key ring is shared symmetric key material.** To recognise estate users the web host mounts the
  estate's data-protection key ring under the estate's application name and cookie scheme. Data protection is
  symmetric, so the host could also mint an estate sign-in cookie or ticket; treat a GridSim host with the ring mounted
  as part of the estate's trust boundary, not as a "decrypt-only" consumer. With the ring writable, GridSim can create a
  key in it (on first run or at the 90-day rollover) protected by its own account's DPAPI on Windows or in plain XML on
  Linux, where the estate may protect its keys with a certificate; mount the ring read-only or give GridSim the
  estate's protection settings. (`KeyRingProtection`, `Program.cs`.)
- **Sign-on flow.** `/sso/land` signs the browser in from a ticket in the query string with no state bound to the
  browser that started the flow (a login CSRF is possible between estate users); `/sso/logout` is a GET (a page
  elsewhere can sign a visitor out of GridSim when sign-on is on); the sign-in cookie is persistent for 14 days with
  sliding expiry and its roles are not re-checked against the authority. (Its `Secure` flag no longer follows the request
  scheme: with sign-on on it is always set, GR-733.) The ticket and logout endpoints are the estate's shared sign-on
  protocol, so binding the flow to the browser waits for a protocol version the authority also speaks. Run sign-on
  only over HTTPS. (`EstateSso`, `Program.cs`.)
- **Resource limits are per instance, not per client.** `/api/solve` and `/api/topology` load and solve on the request
  thread with no concurrency limit or timeout (`/api/topology` re-lays a non-replay model on every call); `/ws/sim`
  runs a full solve per `step` or `start` frame with no rate limit; `GridSim:MaxSessions` is one global counter, so one
  visitor can take every slot, and a disconnected Blazor circuit holds its slot for the retention period; prerendering
  a dashboard page builds and disposes a whole session before the interactive circuit builds its own; and bridge frames
  run every projection on the bridge receive thread. Put a rate-limiting proxy in front of a public instance.
  (`RestApi`, `SimWebSocket`, `SessionManager`, `Dashboard.razor`, `SimSession`.)
- **The bridge client has no keep-alive timeout or reconnect.** A silently dropped TCP path leaves a session "Playing"
  with no data; the session does not detect the stall or reconnect. (`GridBridgeClient`.)
- **Web output conventions.** A NaN or infinite double in any JSON response or `/ws/sim` frame is written as `null`,
  with no flag; the converter does not read numbers written as strings (the ASP.NET "Web" default); a raw replay frame
  carries `converged: true` and 1.0 pu placeholder voltages although no solve ran, so the status lamp can read normal on
  it; the tabs of a non-converged frame still show values computed from the unconverged iterate; raw replay seeks are
  clamped to the replay's range but not to the forward-inference horizon; and `/health` does not reflect bridge
  sessions. (A `/ws/sim` `speed` or `fps` out of range is refused since GR-734; a bridge seek, pause or speed change
  that fails is shown as the session's warning.) (`NonFiniteDoubleConverter`, `FrameProjector`, `SimSession`, `TransportBar.razor`, `Program.cs`.)
- **Development profile.** The default `dotnet run` profile is Development, which shows the developer exception page.
  Run production under the Production environment. (A `/ws/sim` error frame no longer carries server paths: GR-721,
  `ClientErrorText`.) (`Program.cs`.)
- **Discovery and static files.** Case discovery checks up to eight ancestors of the install for `results/` and
  `gda-out/` and lists what it finds as public cases. Deploy with only the folders you mean to publish. (`/geo` serves
  only the `.geojson` and `.json` files of its folder since GR-708; two `gda-*` folders that slug alike get distinct
  ids, the later one numbered, as `mp-*` and `run-*` ids do.)
  (`CaseCatalog`, `Program.cs`.)
- **Licence checks trust the local machine.** Expiry and not-yet-valid checks read the local clock, so setting the
  clock back extends a key (as the licensing chapter states); a key has two valid signatures (both ECDSA signature
  forms, low-S and high-S), harmless while there is no revocation list; the bundled-network hash cache
  is keyed by path, length and timestamp, and the hash is checked after the model was parsed; and a model is parsed in
  full before the licence refuses it. (`Entitlement`, `Base64Url`, `BundledNetworks`, `CaseCatalog`.)
- **The usage log.** The presence usage log is appended for ever (no rotation) and read whole at start-up. (Where it
  cannot be written it is off with a warning on stderr naming the path and the error.) (`PresenceService`.)
- **security.txt is built at start-up.** Its `Expires` is checked once, when the host starts; a host that runs past the
  date keeps serving the file until it is restarted (the deployment guide says so). (`SiteSettings`.)

## Desktop app

- **Some presentation physics lives in the desktop and web projects.** The desktop MACHINE view integrates its own
  narrative quantities (a droop-implied surplus, commanded and delivered torque, a mains-clock accumulator, a narrative
  battery state of charge) and the dashboard runs its own swing studies on a trip; the web has twins of some of them.
  These are presentation, labelled as such, and not part of the validated Core results; moving them into Core
  presenters is a refactor for a later release. (`MachineVm`, `DashboardViewModel`.)
- **The GENERATION tile shows net positive injection.** It sums the positive net bus injections, so generation at a bus
  with local load is reduced by that load, and the derived percentages divide by it. (`DashboardViewModel`.)
- **The GB map is drawn without latitude scaling.** Longitude and latitude are each scaled to the bounding box, which
  stretches Great Britain east-west by about 1.6 times; positions are consistent with each other, distances are not.
  (`CoastlineGb`.)
- **Model switching runs on the UI thread.** Loading a case, laying it out, the licence hash and building the live
  simulation run on the dispatcher thread, and a solve started on the old model is not cancelled by a switch.
  (`MainWindow`.)
- **Unbound view-model values.** The OPERATOR and MARKET view-models compute a few values every tick that no view shows
  (response MW, net interconnector flow, voltage band, warnings; system sell price, day-ahead, NIV). (`OperatorVm`,
  `MarketVm`.)

## GDA data builds

- **Missing or ambiguous source columns and files.** A CSV column that does not exist reads as blank or 0 on every
  row (the LTDS readers do not check their columns exist; UKPN and NPg demand do); a directory of Parquet parts is read
  in full, every vintage included; NPg falls back from Parquet to CSV on any Parquet error (the build notes name the
  dataset and the error). Keep one vintage per dataset folder. (`Csv`, `Tabular`, `LtdsReader`, `NpgSource`.)
- **Data conventions the builder applies.** A blank power factor reads as 1.0 (no reactive demand), as the GDA chapter
  documents; UKPN and NPg installed capacity in MVA is used as export capacity in MW; a generator whose own GSP group
  does not list its primary is placed by name in the one other group that does (counted in a note); a three-winding
  transformer carries no OLTC and its resistance goes wholly on the high-voltage leg; generator names are not made
  unique; NPg node keys round kV to one decimal place, so 0.4 and 0.415 kV busbars of one name merge (kept: whether the
  tables spell one LV board both ways needs the real NPg data to decide); the UKPN demand split can differ from the
  published figure in the last bits. Decided (GR-333): the note on UKPN aggregate (grid board) rows now states, in MW,
  how much more they publish than the rows they feed add up to; that excess is not in the replica.
  (`GdaModelBuilder`, `UkpnSource`, `NpgSource`.)
- **Validation findings do not block a build.** Every `GridModel.Validate` finding of a GDA build becomes a note; a
  build with invalid data is returned as a result (the JSON loader that reads the written case refuses it). (Since
  GR-332 the case writer writes every bus and branch field a JSON v1 case carries, when set: ZIP, per-bus voltage
  limits, ratings B and C, a line's tap and phase shift and zero-sequence impedance; what v1 cannot hold, a
  transformer's shunt susceptance and transformer zero-sequence data, is counted in a note.) (`GdaModelBuilder`,
  `GdaModelWriter`.)
- **Register choice by date.** Decided (GR-821, GR-827): a register dated D in its name is available from 00:00 on D (the
  name gives a day, not a time, and the as-of rule works in days); only full and short English month names (and `sept`)
  parse, because a numeric or ISO date in a GDA name is as often a data period as a publication date. The UKPN
  shortest-suffix match ignores vintages. Name registers with a full or short month and keep one vintage. (`GdaPaths`,
  `UkpnSource`.)
- **NESO and phase-angle readers.** Any extra numeric column in the boundary table is read as a boundary, 0 MW weeks are
  dropped (so `MinMw` can be overstated) and the median of an even count is the upper middle (kept: what a 0 MW week and
  the extra columns mean needs the real NESO table to decide). The phase-angle reader labels a non-UTC typed stamp as
  UTC without converting it and truncates stamps to the whole second, so a sample can answer an instant just before it:
  both come from the shared text conversion of a Parquet time cell that the reference reader goes through, and the typed
  reader must match it bit for bit, so they stay until that conversion is changed for every GDA reader. Decided
  (GR-318, GR-794): the ISO week is looked for under the ISO-year folder and then the calendar-year folder, and a week
  held in both is refused; two rows with one time stamp (after the whole-second reading) are refused, naming both rows.
  (`NesoReader`, `PhaseAngleInferedReader`.)
- **Geolocation keys by name.** Two substations whose names share their first word take the first one's position, and a
  malformed substations file becomes a note. (`GeoLocator`.)
- **Test coverage.** No test feeds two UKPN demand rows with one substation name in two licence areas.

## Interposer, telemetry and synchrophasors

- **Window time checks.** The forward-inference horizon is checked on a window's instant (its newest stamp) only; a
  point stamped long before the others is merged into the snapshot, and a wildly future stamp moves the whole window
  past the horizon. (`InterposePipeline`.)
- **Protocol state and addressing.** The 32-bit Modbus companion register's quality is not folded in; IEC 104 points are
  addressed by IOA alone, without the common address, so two stations or sectors on one link with the same IOA collide
  (as the interposer chapter states; adding the common address changes every crosswalk's point names, a file-format
  change). Run one interposer per station. Decided (GR-329, GR-801, GR-803, GR-823): a stateful decoder is reset when its
  client goes away; a Modbus response to an id that had two different requests outstanding is refused, and an exception
  response ends its transaction; the IEC 104 controlling station closes the connection when STARTDT act or TESTFR act is
  not confirmed within t1, or an I-frame arrives out of sequence. (`ModbusProtocol`, `RtuMeasurementSetMapper`,
  `Iec104Protocol`, `Iec104ControllingStation`.)
- **C37.118 decoding.** STAT bit 10 (configuration change) does not decide usability (a test pins it as usable); PDC
  alignment is exact to the tick, so PMUs whose TIME_BASE rounds the same instant to adjacent ticks form two superframes;
  a PMU current channel has no metered end (every current is taken at the From end; the channel map has no field for
  it, a file-format change); the single-PMU decode overloads do not compare IDCODEs. Decided (GR-304, GR-818, GR-826): the
  stream decoder refuses a DATA frame whose IDCODE is not its parsed CONFIG frame's (`WrongStream`, counted as malformed
  and named on stderr); a CONFIG frame of protocol version 0 is refused, and pmu-ingest never lets a CONFIG-1 replace a
  CONFIG-2 in force; `--live-udp-source` (opt-in) reads only one sender's datagrams. (`C37118Decoder`,
  `C37118SampleQuality`, `C37118ConfigFrame`, `PhasorDataConcentrator`, `PmuMeasurementSetMapper`,
  `C37118LiveTransport`.)
- **The quarantine store.** Decided (GR-825, GR-828): the evidence-lake firewall resolves every symbolic link or junction
  on the way up and checks the target's chain too (an unresolvable link is refused); the `_QUARANTINE.json` marker is
  written when the store opens, before any partition; a window with no taken time is refused, never stamped with the
  write time. (`QuarantineLiveStore`.)
- **Replay validation edges.** Decided (GR-800, GR-802): `MaxWindowedRocof` refuses a trajectory that is not on a uniform
  grid; a replay of an event whose recorded RoCoF is zero (below 1e-9 Hz/s) is refused, since the RoCoF test is relative
  to it. (`ReplayValidator`.)

## Scheduling and live simulation

- **The cycle scheduler admits by measured cost.** Which Low-priority analysis jobs run in a cycle is decided by a
  moving average of their measured run times against the core-millisecond budget, and the deadline is a wall-clock
  cancellation, so the set of jobs admitted, cancelled and rotated (and so which N-1 results exist in a given cycle)
  varies with machine load. Each result that is produced is deterministic. A cancelled N-1 shard keeps its partial
  results and resumes on the next frame, so a completed shard can hold outages screened against different frames
  under one cycle stamp. (`CycleScheduler`, `AnalysisJobLibrary`.)
- **Live clock.** The day clock adds the step in minutes each tick (rounding builds up over a long session) and does
  not check the tick number, so a repeated or skipped tick is not detected. (`LiveSim`.)
- **Real-time estimator windows.** Any `InvalidOperationException` from a filter, a programming fault included, counts
  as a failed window when the engine continues on error. (`RealtimeDseEngine`.)
- **Passive-plant replays** keep the `DerivedFromRecorded` tag although modelled tap and thermal changes alter the
  reconstruction; only storage downgrades it. (`GdaReplayLoop`.)
- **`HybridMultiRateScheduler`** has no production caller; it is kept for its tests.

## Audit trail, incidents and crash logs

- **The writer lock.** The single-writer lock is advisory on Linux and macOS (`flock`): a process that does not take
  it can append, and unlinking a held `.lock` file lets a second run lock a new file and write with the same sequence
  numbers. Give the log one writer and a directory only it can write. (`AuditLogWriter`.)
- **Anchors.** A missing anchor is accepted with a warning and an `audit-anchor=missing` marker; deleting both the log
  and the anchor starts a fresh log with no marker. The signed anchor binds the entry count and head hash only (no log
  identity or time), so restoring an older log prefix with its older signed anchor verifies. The anchor is fsynced but
  its directory is not after the rename, and the temporary name is fixed. Forward the log to write-once storage where
  truncation matters. (`AuditRunRecorder`, `SignedAuditAnchor`, `AuditAnchorFile`.)
- **Recorded inputs.** `AuditInput.OfText` records an unsalted SHA-256, which is guessable for a short secret, so do
  not pass secrets to it; an input file is hashed on one read and loaded on
  another, so a file replaced in between is recorded with the old hash. (`AuditEvent`, `AuditRunRecorder`.)
- **Log size.** Opening a writer reads and checks the whole log, and the log is not rotated, so start-up time and
  memory grow with it. Rotate by starting a new log path. (`AuditLogFile`.)
- **The incident monitor counts for the life of the process.** Counts do not decay and each kind latches after its
  first burst, so bursts spread over a long run raise one incident and later bursts of that kind raise none; kinds with
  no burst threshold (RestrictionRefused, DistrustedSource, DependencyAlert, AvailabilityDegraded) never escalate
  through the monitor; they are still written to the audit log as security entries. (`SecurityEventMonitor`.)
- **Horizon seams take the caller's clock.** The library seams that take a `utcNow` accept a time later than the real
  clock; the real-time engines and the presolved replay clamp it to the real clock, and the other library callers
  (event catalogue, replay loop, incident report, restriction review) do not. (`ForwardInferenceGuard`.)
- **Crash logs.** The arguments are written in full, by decision: no GridSim flag carries a secret (licences are
  imported from a file, and `--licence` selects a distribution area), and the exact command line is what a bug report
  needs. When the per-user folder cannot be created the log goes to the temporary folder; on Linux and macOS it is
  created there new and owner-only (0600) in an owner-only folder (GR-839). (`CrashLog`.)
- **Release workflows.** Third-party actions and the SBOM tool are referenced by version tag, not by commit hash.
  (`.github/workflows`.)

## Determinism checks and the web session

- **Seeded random numbers come from .NET.** EnKF ensembles, synthetic measurements, the synthetic GB case, LiveSim's
  ambient noise and graph layout use `new Random(seed)`. Today a seeded `Random` uses .NET's long-standing compatible
  algorithm, but .NET does not promise to keep it, so a future runtime could change these sequences; the pinned tests
  would show it. (`EnsembleKalmanStateEstimator`, `LiveSim`.)
- **What the determinism guard scans.** The IL guard scans Core, Gda, Gpu and the command line, not the web and
  desktop hosts, by decision: those compute display values only (a phasor angle by `Math.Atan2`, for example), which
  are not bit-identity checked. Its vector list names the reciprocal-estimate intrinsics and `Lerp` (GR-861). Since
  1.95.436 a second test requires every `AddScaledRow` call in `src` to name `fastFma: false` literally.
  (`DeterministicMathGuardTests`.)
- **Cross-OS checks in CI.** Every leg of the CI matrix (Linux, Windows and macOS; normal, AVX2 off, hardware
  intrinsics off, AVX-512 and FMA off) is a hard gate, but the workflow runs when the release calls it or by hand, not
  on every push; day-to-day verification is the local batch run. (`.github/workflows/ci.yml`.)
- **Web session state.** Since 1.95.436 the bridge callbacks write the per-tick fields under the session lock; the
  topology and error text are single reference writes, read without it. The replay topology cache waits on its lock
  synchronously and reads the file details of every export on each call, so a cold build of a large replay blocks the
  requests for it. (`SimSession`, `ReplayTopologyCache`.)

## The test suite

Tests are never loosened, re-baselined or deleted to make a change pass, and an assertion changes only with written
justification. The points below are where a test is weaker than its name or comment, or covers less than it could.
Each is kept as it is (tightening an assertion moves a pinned value or verdict and waits for a batch run); new tests
that close a gap are added beside the old ones.

- **References that are GridSim's own output.** The IEC 60909 network duties on WSCC-9 (Ik'', Ib) are pinned to values
  GridSim produced; no test uses the standard's published worked examples (IEC TR 60909-4). The 2019 event check feeds
  the inertia derived from the recorded RoCoF and asserts the formula returns that RoCoF, with a 1,618 MW loss where the
  event catalogue holds 1,521 MW. The chi-square threshold is the Wilson-Hilferty approximation, tested at 10, 50 and
  100 degrees of freedom to 1 % (about 2 % low at one degree of freedom), and one range was moved to bracket the
  approximation's output. Some tests pin a known model error as a band (2023 nadir 0.15 to 0.45 Hz too deep; replay
  RoCoF relative error 0.15 to 0.30). (`Iec60909Tests`, `AssetValidationTests`, `BadDataTests`,
  `Cf5ProjectionStatisticsTests`, `BlindPredictionTests`.)
- **Tolerances looser than the comments.** The tests that were named "machine epsilon", "machine
  precision" or "exactly" while checking 1e-6 Hz, 1e-6, 1e-5, nine or two decimal places are named for the bound they
  check (the bounds themselves wait for a batch run to be tightened); the EKF static-truth test at 1e-3 pu and 1e-2 degrees on exact data; full
  redundancy "well inside a meter's sigma" allowing 2.5 sigma; Baran and Wu case33bw at about 10 % on losses and BFS
  against Newton at 1e-3 pu; MATPOWER published-solution checks at about twice the observed error, solved with Q limits
  on; frequency-integrator sub-stepping at 0.05 Hz with a known off-by-one in the batch loop. The legacy `Dense_and_sparse_solvers_agree` (1e-5 pu) is a smoke test;
  dense/sparse bit identity is held by `DenseSparseParityTests`, which compares the bus results, every branch
  flow field (losses, rating and name included) and the solved bus types.
- **Identities beside the physics.** `TotalLossMw` is the sum of the injections, so the balance assertions in
  `SolverPropertyTests` (and `PowerFlowTests.Generation_balances_load_plus_losses`, `IeeeCasesTests.Converges_and_balances`)
  check that definition; the two `SolverPropertyTests` theories are named for it, and the independent
  check is `Solved_injections_recomputed_from_the_ybus_meet_the_specified_injections`, which recomputes every bus
  injection from the solved voltages and a Y-bus built in the test and holds it to the case's specified P and Q at
  1e-4 MW. The branch-plus-shunt reconciliation is still at 1e-3 of the loss in MW.
- **Outcomes the suite accepts either way.** Several tests on fixed inputs accept two opposite outcomes (a case9 bus-5
  fault cleared or not cleared by a branch element; `modal case9` exiting 0 or 1; about ten CLI tests accepting exit 0
  or 3); the dense/sparse theories over MATPOWER files and JSON cases do not require convergence, so for the rows that
  do not solve as shipped they compare two failures; the one gb-full security DC-OPF test is skipped unconditionally and
  accepts Optimal or not.
- **Passes that are not runs.** The bundled-data guards are assertions, and the CGMES conformity
  benchmarks (no workflow sets `GRIDSIM_CGMES_CONFORMITY_DIR`) and the Windows-only header check are reported as
  skipped through the `EnvironmentTheory` and `WindowsFact` attributes; `BundledDataPresenceTests` also finds the
  braced, `OperatingSystem` and `string.IsNullOrWhiteSpace` forms of an early return. Left: the optional template
  library and the crash-log default-folder check still return early, a guard written in another form (`x is null`)
  is not found, and on a machine with no GPU the GPU seam tests compare the CPU fallback with the CPU.
- **Source-text contract tests.** About 133 test files assert that strings are present in source files (for example
  the exit-code convention in `Program.cs`), which a comment or dead code satisfies and a correct rename breaks.
- **CI coverage.** Every reduced leg checks that its switch took effect (`simdcheck` must report
  `Avx2=False`, the `Scalar` tier, `Fma=False` or, on the `avx256` leg added for GR-399, the `Avx256` tier, so the
  256-bit production tier runs whatever the runner CPU). Every leg and the release's own test run
  set `GRIDSIM_TEST_MAX_BUSES=all`, so a runner with less than the 16 GB the whole suite needs can run out of memory on
  the largest cases rather than skip them.
- **Environment and clock.** CLI child processes inherit `GRIDSIM_LICENCE` and the per-user licence file; licence
  expiry reads the system clock, so a run crossing UTC midnight can flip an expiry-day check. The graph-layout
  wall-clock test runs alone in a non-parallel collection. (`CliProcess`, `Entitlement`.)
- **Added in 1.95.437.** Tests of the audit sink forwarding path (`AuditSinkTests`), a timed test that `GeneralEigen`
  refuses an infinite entry by name, the phase-angle reader tests in a non-parallel collection, and exclusive callers
  of the large dense solve gate queued on their own lock.

## Build, release and deployment

- **Pinning.** GitHub Actions are referenced by version tag, not commit hash, in every workflow, including the jobs
  that hold the signing secrets; the release installs the CycloneDX SBOM tool without a version; the Docker base images
  (`sdk:10.0`, `aspnet:10.0`) and the runtime `curl` package float; there is no `global.json`, so the SDK (and with
  `AnalysisLevel=latest` its analyser set) is whatever 10.0 patch the runner has; the `packages.lock.json` files are
  committed but no restore uses locked mode. Dependabot version updates are paused. (`.github/workflows`, `Dockerfile`,
  `Directory.Build.props`.)
- **Release gates.** The release runs the full CI matrix and its own Linux test run before anything ships; the package
  workflow, started by hand or by a published release, runs the whole CI workflow first, though not
  the release's own Linux test run. The release passes it the two signing secrets by name (GR-909). Without the
  certificate a release is unsigned and says so; setting the repository variable `GRIDSIM_ALLOW_UNSIGNED_RELEASE` to
  `false` makes signing required, checked in the first job before anything is built (GR-359; unsigned stays the default
  while the certificate is self-signed). `signtool` signs from the runner's certificate store (GR-907);
  the signing password is still passed to `dotnet nuget sign` on its command line, on a single-use runner.
- **What the SBOMs describe.** Each SBOM lists the NuGet packages of its project; the web SBOM does not describe the
  container's Debian base, the ASP.NET runtime or `curl`, and the self-contained CLI SBOM does not list the .NET runtime
  it carries. The .NET runtime's own third-party notices are referenced by URL, not shipped beside the binaries.
- **Line endings.** `.gitattributes` pins LF for the Dockerfile and `.dockerignore` only, so on a Windows checkout with
  `core.autocrlf=true` scripts, compose files and data are written with CR LF. The bundled-data manifest hashes text
  with CR LF read as LF, so it verifies on either checkout.
- **Local publishes.** The CLI and test projects copy `data/**/*.json` and `data/**/*.csv`, so a local publish also
  carries anything left in git-ignored data folders. Publish from a clean checkout.
- **Internals.** GridSim.Core grants `InternalsVisibleTo` to `GridSim.Tests` and `GridSim.SchemaEditor` by simple name
  and is not strong-named, so an assembly of either name can call Core's internals, licensing included.
- **Forwarded headers.** `compose.yml` defaults `GRIDSIM_TRUSTED_PROXIES` to `loopback,private`, which trusts every
  RFC 1918 address, other containers on a shared network included. Set it to the reverse proxy's address.
