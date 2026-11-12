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

- **Badly scaled columns are refused as singular.** Since GR-044 the real LUs (dense, batched, sparse and its
  replays, Kron reduction, ILGPU and ROCm) treat a pivot at or below `RelativePivotFloor` (1e-12) times the largest
  entry of its own ORIGINAL row as numerically singular, so rows in different units no longer matter (diag(1e6, 1e-9)
  solves; it used to be refused against the whole matrix's scale). Every system the old whole-matrix floor accepted
  gives the same bits. A matrix whose COLUMNS differ in scale by more than about 1e12 can still be refused when column
  scaling would make it well conditioned, as there is no column equilibration. The opt-in diagonal preference of the
  sparse LU now holds the diagonal to its own row's floor, so it can take a diagonal it passed over before. The complex
  LUs, QR, eigen-solvers and estimator inverses keep the whole-matrix floor. (`NumericTolerances.ToRowPivotFloors`,
  `SparseLuFactorization`, `DenseLuSolver`, `KronReduction`.)
- **The bounded simplex uses absolute feasibility and pivot thresholds.** Phase I accepts a total artificial
  infeasibility of 1e-7, and the eta pivot floor (1e-12) and refactor clamp (1e-14) are absolute. On a very large
  case in MW the round-off can exceed 1e-7 and a feasible LP can be reported infeasible; the thresholds are pinned by
  the DC-OPF security tests. (`BoundedSimplex`.)
- **Non-finite input to the GPU dense solvers.** The CPU sparse LU and QR refuse NaN and infinite matrix entries. The
  ILGPU and ROCm dense LUs refuse a NaN matrix entry and a NaN or infinite right-hand side (GR-457 added the
  right-hand-side check to ROCm), but an infinite matrix entry is not refused by either and is eliminated through.
  The GPU solvers leave the input matrix untouched where the CPU dense LU overwrites it with its factors (a design
  decision: the device works on a copy). (`GpuDenseLinearSolver`, `RocmDenseLinearSolver`.)
- **A ROCm or ILGPU device fault ends the run.** A device or library failure inside a GPU sparse solve throws
  instead of returning null, so the Newton shell's CPU retry does not run and the power flow stops with the device
  error. The one exception (GR-475) is the ROCm sparse solve running out of device memory (hipErrorOutOfMemory, or
  the rocSPARSE or rocBLAS memory-error status): it is solved on the CPU sparse LU instead, counted
  (`RocmSparseLinearSolver.OutOfMemoryFallbacks`, with the fault text kept) and written to stderr. Every other
  ROCm fault, and every ILGPU fault, stays loud. (`RocmSparseLinearSolver`, `GpuSparseLinearSolver`.)
- **GPU selection is simple.** OpenCL and CUDA use the first device of each platform; a later FP64-capable device
  on the same machine is not tried. (The ROCm availability probe loads the math libraries by the solvers' own route,
  the versioned-name resolver first, since GR-043.) The CUDA driver probe runs on first use of the GPU context, on a
  background worker outside the context lock, bounded at 10 s (GR-471); a driver that hangs in `cuInit` is recorded
  in the CUDA description as a timed-out probe and leaves that worker blocked, but no longer hangs other GPU queries
  at the probe. ILGPU's own context creation, which follows, has no timeout. (`GpuContext`, `RocmContext`.)
- **GPU code paths are not exercised by the test suite on a machine without a GPU.** On such a machine the GPU seam
  tests run the CPU fallback, and the ROCm P/Invoke layer has never run on hardware in CI. A device run is the
  release check for those paths; on it the CUDA, OpenCL and (since GR-885) ROCm device tests run, and without the
  device they are reported skipped with the reason, never passed. The hardware-free seam tests are kept as the
  fallback contract. (`GpuSolverSeamTests`, `GpuDeviceKernelTests`, `RocmSolverSeamTests`, `RocmDeviceKernelTests`.)
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
- **Solver constants are engineering choices.** The Newton step damping (`MaxUndampedStep` 0.4), the voltage floor
  (`VmFloorPu` 0.2), the Q-limit and revert tolerances (1e-3 MVA, 1e-4 pu), the loop cap (6), the distributed-slack
  tolerance (1e-4 MW) and the experimental wave field's speed and spans are set from experience on the bundled cases,
  not taken from a cited source; each is a named constant in the solver tables.
- **Engine efficiency.** The dense Newton engine still builds its dense arrays on the CPU path although the CPU step
  is solved by the sparse LU. This costs time and memory on large cases, not accuracy. (The sweep's result assembly
  indexes the generators by bus once since GR-501.) (`NewtonRaphsonPowerFlow`.)
- **`IslandReport.LostGenMw` is always zero.** An island is blacked out only when none of its generators produces
  power, so the lost generation it books is zero by construction; the field is kept for the report's shape as a
  documented zero (design decision, GR-491), pinned by `Gr491IslandLostGenTests`. (`IslandPartition`.)

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
- **FDIA screen bounds.** A bus quantity's uncertainty margin takes the smallest sigma of the rows measuring it, and
  so does a branch with one line between its buses (rows are grouped by circuit label); between parallel circuits the
  widest circuit group's bound is kept, because a rating violation names its buses, not its circuit. A branch with one
  metered end takes that end's bound as its rating margin (GR-532; it used to get none), and a branch with neither end
  metered gets no margin; the protected-anchor test compares
  the anchor residual with `k x sigma_anchor` alone, without the estimate's own variance at the anchor, so a very
  tight anchor that is not in the fit can read inconsistent on clean data. The screen is therefore conservative toward
  `suspected`. Since GR-535 a test screens exact telemetry from a dispatch the case file does not hold, with co-located
  meters of different sigma (`Gr535FdiaNonCaseDispatchTests`); clean noisy case30 at the default seed still reads
  `suspected`, as the estimation chapter says.
  (`FdiaScreen`.)
- **Every slack bus is an angle reference.** A bus of type Slack loses its angle state and is held at its case-file
  angle. Since GR-076 WLS state estimation refuses a model with two slacks in one island, naming both (a second slack
  would be a hard angle constraint the measurements cannot test); give each island one slack. The Kalman filters still
  hold each slack at its own angle. (`MeasurementModel`, `WlsStateEstimator`.)
- **The feasible method's generator-Q constraint uses the case-file load.** `Qg = Qinj + Qd` takes `Qd` from the
  model, not the estimated or voltage-dependent load, so on telemetry whose load differs from the case the reactive
  bound is applied to a slightly wrong injection; the post-solve feasibility audit uses the load at the solved voltage.
  (`FeasibleWlsStep`.)
- **An estimate seen as a power flow carries estimator quantities.** `EstimationResult.ToPowerFlowResult` keeps the
  last state-update norm in `MaxMismatch` (it is not a power mismatch) and its power-flow `Status` can only say
  `NonFinite` or `MaxIterations` for an unobservable or infeasible estimate; since GR-081 the projection also carries
  `EstimatorStatus` (the estimator's own status, including `Unobservable` and `Infeasible`) and `StateUpdateNorm`
  under their own names, both null for a power-flow solve. It rebuilds the angles from degrees (one or two units in
  the last place away from the estimator's radians). (`EstimationResult`, `PowerFlowResult`.)
- **Kalman filter numerics.** The EKF and UKF store an exactly symmetrised covariance after each window, and the UKF
  refuses a posterior variance that is not positive (the filter keeps its previous state); the UKF's default
  `alpha = 1e-3` still gives central weights near -1e6, which costs about six significant digits in its sums. The process-noise and initial-variance defaults are engineering values, not cited data,
  and the frequency filter's process noise is not the discretised white-noise form. (`KalmanStateEstimator`,
  `UnscentedKalmanStateEstimator`, `RandomWalkProcessModel`.)
- **A degenerate equality block.** Two duplicate zero-injection rows on one quantity make the KKT system singular.
  Since GR-507 the constrained method refuses such a set by name before solving (it used to report it
  unobservable); the feasible method still re-gates and reports infeasible with an observable report. Remove
  duplicate constraint rows. (`ConstrainedDenseWlsStep`, `WlsStateEstimator`.)
- **Measurement feeds and resolvers.** Phase-angle rows share one source class (`phaseangle` by default) and carry
  each row's frequency and instant in the optional `provenance` field (GR-529; they used to be appended to the source,
  which made every row a class of its own). The WLS family and the linear PMU estimator resolve a branch by one strict
  rule (GR-536): exact name first, then a plain `#k` ordinal, refusing a shared name or a `#k` that does not fit. They
  differ in one point by design: the PMU estimator never resolves an out-of-service branch (an open branch carries no
  current), while the WLS family does (its measurement model gives it zero admittance, and the synthetic sampler
  meters it). (`PhaseAngleFeed`, `MeasurementSet`, `LinearPmuStateEstimator`.)
- **The exact LAV retry loop can repeat a step.** When the plain step is rejected and a weighted step predicts no
  decrease, the loop returns to the plain step, so it can alternate until its 40 retries are spent and then report
  failure; each retry warm-starts from the previous LP basis, so the repeats are not provably identical and the loop
  is kept as validated. (`WlsStateEstimator`.)
- **Test coverage gaps.** The dense GPU step is checked against the sparse step to solver tolerance only. (The exact
  dense-versus-sparse test of the constrained and feasible gates with a free weight of 1e8 beside pseudo rows is
  `Gr534ConstrainedDenseSparseParityTests`, GR-534.)

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
  than the power-station-unit factor `K_S`. (A machine with no rating, `MBase <= 0` or not finite, is refused by name:
  the near-generator test, mu, a converter's `k x I_rG` and, since GR-603, a grounded machine's own zero-sequence
  impedance all need it; the zero-sequence network no longer falls back to the system base. An element on a bus with no base voltage
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
  before). A bus missing from the pre-fault result is de-energised: 0 V, no load and no machine contribution, and a
  fault on it is refused by name (GR-622; it used to take its stored case-file voltage, so a dead bus became a source).
  Where both ends of a branch have collapsed the protection context cannot derive its current, so it flags the branch
  `CurrentUnknown` and the undercurrent (37), thermal (49), instantaneous overcurrent (50) and time overcurrent (51)
  elements give it no verdict (GR-623; it used to read 0 pu, so 37 operated and 50 / 51 read a confident "not
  operating", and 49 graded an MVA loading that says nothing of the current there). (A bus the protection context
  cannot find in the result is refused.)
  (`FaultAnalysis`, `ProtectionContext`, `ProtectionRelays`.)
- **Relay models.** The thermal (49) replica starts cold for a steady-state overload (as the protection chapter
  states), and inverter low-voltage ride-through trips on a single sample below its threshold rather than on a
  voltage-time envelope. The auto-recloser's shot limit (`ProtectionStage.MaxRecloseShots`, GR-123) defaults to
  unlimited and is set only by code that builds stages directly; no asset-class key populates it yet, so an authored
  recloser still recloses onto a persistent condition for as long as it lasts. (`ProtectionRelays`, `ProtectionEvaluator`,
  `GridFollowingInverter`.)
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
  it may do both in one period). Decided (GR-626), kept: forbidding both needs a binary variable per period (a
  mixed-integer model), which the LP solver does not have; at a non-negative price, charging and discharging together
  only loses energy, so it can arise only at a zero or negative price; read such a period as its net flow. The reported
  round-trip efficiency mixes AC energy with DC stored energy at
  nominal open-circuit voltage. (`PackModel.Sized` refuses a pack more than 50 % away from the requested energy; the
  state-of-charge bucket delivers only the energy left on the step that reaches its limit; an explicit LP end-energy
  request outside the energy window is refused.) (`StorageDispatch`, `StorageFleet`.)
- **Alarm-log matching.** A sequence-of-events alarm names a branch by its unordered bus pair, so the two circuits of a
  double circuit are one key, and bus id 0 is the "not a branch" marker. (`AlarmLogCsv`.)
- **External benchmarks.** The IEC 60909 suite is pinned to closed forms, hand networks and the WSCC 9-bus case, not to
  the IEC TR 60909-4 worked examples (not on disk); the IEEE relay curve constants are checked through a closed form
  that repeats them. (A breaker rating whose duration exceeds the fault duration is exercised by
  `Iec60909ShortFaultThermalTests`, and an operating-point fault whose converter current fixed point stops unsettled,
  reported not converged and refused by the protection assessment, by `Gr629StalledConverterFixedPointTests`.)

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
- **CIM export details.** The tap and the phase shift are written whenever they differ from nominal at all (exact compare since GR-151), but the re-imported tap
  (`1 + step / 100`) is not bit-exact. (An exporter-generated identifier such as `bv_400`, `t_<mrid>_1` or `rc_<mrid>`
  that equals a source mRID, or two objects sharing an mRID, is refused at export, naming the identifier.)
  (`CimExporter`.)
- **CIM import details.** A load or machine missing from the SSH profile draws or produces nothing even when other
  objects carry SSH values, unless the opt-in `CimImportOptions.RequireSshSetPoints` is set, which refuses it by name
  (GR-641; off by default, by decision); the bridge produces a 50 Hz model unless the caller passes `frequencyHz`, because
  the CGMES profiles it reads carry no system frequency (GR-635). (A load, machine or injection with more than one
  connected terminal is refused since GR-640. A machine with no `ratedS` takes the system base; an SVC start moved inside its ratings
  is noted; a `urn:uuid` UUID is compared without regard to letter case.) (`CimImporter`, `CimGridModelBridge`.)
- **v2 library import order: design decision.** Two library files that both match an imported id prefix contribute
  their classes in the case's import order, then file order (no sort): the order is the one the case author wrote, so
  it is deterministic and visible in the case. (Pinned by a test since GR-665.) (`V2CaseLoader`.)
- **The v2 loader does not resolve links.** The inside-the-case-folder test compares paths (by the operating system's
  rule) without resolving junctions or symbolic links, so a link inside the case folder can point outside it.
  (`V2CaseLoader`.)
- **The case editor and loader exceptions.** Since GR-642 the editor shows only the loader's refusal types as a
  problem of the case (`InvalidDataException`, `JsonException`, `IOException`, `ArgumentException`, the last because the
  model's constructors refuse a bad value with it); the loaders turn System.Text.Json's other refusals of the input into
  `InvalidDataException`, and any other exception surfaces as the defect it is. (`CaseSession`, `JsonGridLoader`,
  `V2CaseLoader`.)
- **Generator cost files by index.** A cost file that addresses units by index with an out-of-service unit in the middle
  of `generation.json` is refused when the loader dropped rows, and is otherwise matched in file order (pinned by a test since GR-661). (`GeneratorCostFile`.)

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
- **Catch-all handlers.** Since GR-190 a licence refusal or an ambiguous MATPOWER name is a `GateRefusedException`, not
  an exit from inside the loader: a batch verb running several cases (transient, bench, bench --estimate, nminus1-sweep,
  stress, conformance) records the refused case as its `LOAD FAIL` (or skip) row and runs the rest, and a single-case
  run ends with `error: <message>` and exit 2 as before. Some verbs still catch every other exception and report it as a
  usage or input error (exit 2 or 1) with no crash log. (`Gated`, `Program.cs`.)
- **The per-verb flag gate and the global switches.** A verb accepts the flags its section of the CLI reference declares
  (its usage line or the first column of its flag table, since GR-194; a flag named only in prose is refused) and every
  global switch. A global switch the verb does not read is warned about on stderr and the run goes on, by decision
  (GR-195: `--inertia-model`, `--asset-dynamics`, the forming switches, `--once`, `--ticks`, `--bfs`, `--enableExport`,
  `--export-jacobian`; `--cpu` and `--gpu` are not checked, the backend being shared by every solver route). (A flag
  that the chosen `gda` mode does not use is refused since GR-696. The bare run, repeated value
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
  elsewhere can sign a visitor out of GridSim when sign-on is on); the sign-in cookie slides over 8 hours
  (`Auth:SlidingHours`) and is capped at 14 days from sign-in (`Auth:AbsoluteDays`) since GR-732, but its roles are not
  re-checked against the authority: they are the ones in the ticket minted at sign-in, so a role removed at the authority
  takes effect at the next sign-in, which is at most 8 hours idle or 14 days in all. Re-validating against the
  authority on each request is deferred, because it needs a per-provider refresh. (Its `Secure` flag no longer follows the request
  scheme: with sign-on on it is always set, GR-733.) The ticket and logout endpoints are the estate's shared sign-on
  protocol, so binding the flow to the browser waits for a protocol version the authority also speaks. Run sign-on
  only over HTTPS. (`EstateSso`, `Program.cs`.)
- **Resource limits are per instance, except the session count.** `/api/solve` and `/api/topology` are capped per instance
  (`GridSim:MaxConcurrentSolves`, 503 when full; `GridSim:SolveTimeoutSeconds`, 504), but a solve cannot be interrupted,
  so a timed-out one runs on and keeps its slot until it ends, and `/api/topology` still re-lays a non-replay model on
  every call; `/ws/sim` limits `step` and `start` frames with a token bucket per socket (`GridSim:WsControlBurst`,
  `GridSim:WsControlRatePerSecond`), not per client; since GR-235 one client (a signed-in subject, or an anonymous
  caller's remote address) holds at most `GridSim:MaxSessionsPerClient` sessions (default 16) inside the global
  `GridSim:MaxSessions`, but an anonymous Blazor circuit whose remote address the host cannot see is counted against the
  global cap only, and clients behind one shared address (a NAT, or a proxy not listed in `GridSim:TrustedProxies`) share
  one quota; a disconnected Blazor circuit holds its slot for the retention period; prerendering
  a dashboard page builds and disposes a whole session before the interactive circuit builds its own, so each plain GET
  of `/dashboard/<bundled case>` builds a session, bounded only by the per-client quota and the session cap (`GridSim:MaxSessionsPerClient`,
  `GridSim:MaxSessions`) with no rate guard on the page itself; and, since GR-224, bridge frames are projected on a worker of the
  session's own (a first-in first-out queue of 64 frames, never dropping or reordering one, because projecting a frame also
  advances the histories, the SCADA alarm log, the mechanical ramp and the frequency-risk trace), so a slow projection no
  longer holds up the bridge receive loop; a session whose projection falls 64 frames behind is stopped with an error naming
  that, rather than shown out of step. Put a rate-limiting proxy in front of a public instance.
  (`RestApi`, `SimWebSocket`, `SessionManager`, `Dashboard.razor`, `SimSession`.)
- **The bridge client reconnects only when configured.** Since GR-233 the client pings an idle connection every 30 s and
  treats a ping unanswered for 60 s as a dead connection (`KeepAliveInterval`, `KeepAliveTimeout`), so a silently
  dropped TCP path is detected within about 90 s: pending queries fail and Faulted is raised. A detected loss is
  reconnected only when supervised reconnect is configured (`GridSim:BridgeReconnect` on the web host,
  `BridgeAutoReconnect` in the desktop app; both off by default, and the legacy WPF window is not wired).
  (`GridBridgeClient`, `BridgeReconnectSupervisor`.)
- **Web output conventions.** A NaN or infinite double in any JSON response or `/ws/sim` frame is written as `null`,
  with no flag (the converter reads a number written as a string under the Web defaults, since GR-717). Decided (GR-234):
  a per-response non-finite marker or a refusal would change the JSON contract of every endpoint, so it waits for API
  versioning after GA; until then a `null` where a number is expected means the value was not finite. A raw replay frame
  carries 1.0 pu placeholder voltages, but it is marked `converged: false` and `notSolved: true` and the status lamp reads
  NO SOLVE (after FREQUENCY EVENT, because the raw frequency is measured); the CONFORMANCE, HUMAN SCALE and HARMONICS
  tabs and the EXPERIMENTAL wave field are empty for a non-converged frame, while EXPERIMENTAL keeps its model-only
  reciprocity check and marks its trace and carbon cards not converged; a raw replay seek is clamped to the replay's
  range and to the forward-inference horizon; and `/health` reports the bridge sessions alive and faulted as information
  only, never in its status code (GR-709), so one broken bridge session does not make an orchestrator restart the host. (A `/ws/sim` `speed` or `fps` out of range is refused since GR-734; a bridge seek, pause or speed change
  that fails is shown as the session's warning.) (`NonFiniteDoubleConverter`, `FrameProjector`, `SimSession`, `TransportBar.razor`, `Program.cs`.)
- **Development profile.** The default `dotnet run` profile is Development, which shows the developer exception page.
  Run production under the Production environment. (A `/ws/sim` error frame no longer carries server paths: GR-721,
  `ClientErrorText`.) (`Program.cs`.)
- **Discovery and static files.** Unless `GridSim:CaseRoots` is set, case discovery checks up to eight ancestors of the
  install for `results/` and `gda-out/`; set it to name the only folders searched (GR-715). Whatever is found there is
  listed, and its topology read, only for callers in the `Operator` band or above; below it only the bundled, built-in
  and MATPOWER cases are listed. (The bundled and MATPOWER cases stay open to every caller. Opening a `results/` or
  `gda-out/` model by id follows the same rule: a `/ws/sim` `start` already needed the `Operator` band for every
  case, so a lower band never reaches it, and the dashboard (`/dashboard/<id>`) now refuses such an id below `Operator`
  with the same denial text and one audit entry, building no session. On the dashboard the decision is made on the
  interactive pass only, where the signed-in user is known: the prerender of a private id builds no session, writes no
  entry and shows the loading state, so a plain GET of one reveals nothing and an open that is refused writes exactly one
  entry.) Deploy with only the folders you mean to publish. (`/geo` serves
  only the `.geojson` and `.json` files of its folder since GR-708; two `gda-*` folders that slug alike get distinct
  ids, the later one numbered, as `mp-*` and `run-*` ids do.)
  (`CaseCatalog`, `Program.cs`.)
- **Licence checks trust the local machine.** Expiry and not-yet-valid checks read the local clock, so setting the
  clock back extends a key (as the licensing chapter states); keys and signed anchors are written in the low-S
  ECDSA form since GR-726, but verification still accepts the high-S twin (keys issued before were in either form),
  harmless while there is no revocation list; and a JSON case directory is parsed in full before the
  licence refuses it (a MATPOWER file is refused before the parse, from the bus rows its text declares, GR-712). (The bundled-network hashes are taken afresh on every check, before and after the model is
  loaded, since bug #178; there is no path-keyed cache, GR-716.) (`Entitlement`, `Base64Url`, `BundledNetworks`, `CaseCatalog`.)
- **The usage log.** The presence usage log is rotated once the live file passes `Analytics:MaxLogBytes` (default 16 MiB):
  the file is renamed beside itself, a fresh one starts, and the visits in the rotated files are kept as one number in a
  sidecar. Start-up reads that number and then counts the visits in the live file, so the read is bounded by the rotation
  size (a log from before rotation existed is read in full once, then rotated). The live file is counted, not tailed,
  because the visit total needs every line of it. Rotated files are never deleted. (Where the log cannot be written it is
  off with a warning on stderr naming the path and the error.) (`PresenceService`.)
- **security.txt is built at start-up.** The text is built once, when the host starts, and a configured `Expires` already
  in the past stops start-up. The `Expires` moment is then re-checked on every request: a host that runs past the date
  answers 404 for `/.well-known/security.txt` (as for an unconfigured file) instead of serving a stale contact, until a
  renewed date is configured and the host restarted. (`SiteSettings`, `Program.cs`.)

## Desktop app

- **Some presentation physics lives in the desktop and web projects.** The desktop MACHINE view integrates its own
  narrative quantities (a droop-implied surplus, commanded and delivered torque, a mains-clock accumulator, a narrative
  battery state of charge) and the dashboard runs its own swing studies on a trip; the web has twins of some of them.
  These are presentation, labelled as such, and not part of the validated Core results; moving them into Core
  presenters is a refactor for a later release. (`MachineVm`, `DashboardViewModel`.)
- **The GENERATION tile shows net positive injection, by definition.** GENERATION is defined as the sum of the positive
  net bus injections, so generation at a bus with local load is reduced by that load, and the derived percentages
  divide by it. It is kept as defined (owner decision on GR-753): the shared dashboard model reproduces the WPF tile
  exactly and the parity tests freeze it; summing generator output instead would need a generator total carried in the
  frame and the parity oracle re-baselined. (`DashboardModel`, `DashboardViewModel`.)
- **The GB map is drawn without latitude scaling.** Longitude and latitude are each scaled to the bounding box, which
  stretches Great Britain east-west by about 1.6 times; positions are consistent with each other, distances are not.
  The fix, scaling longitude by the cosine of the box centre's latitude, has to be made in the coastline, the bus
  locator and the case geography together so buses stay on the coastline, and the app's coastline is pinned point for
  point to the WPF copy; it is scheduled after RC2 (owner decision on GR-761). (`CoastlineGb`, `GeoLocator`,
  `GeographyData`.)
- **Model switching runs on the UI thread.** Loading a case, laying it out, the licence hash and building the live
  simulation run on the dispatcher thread, and a solve started on the old model is not cancelled by a switch.
  (`MainWindow`.)
- **Unbound view-model values (WPF only).** The Avalonia OPERATOR tab shows response, net interconnector flow and
  voltage band in a System strip, and the MARKET tab shows system sell price, day-ahead and NIV in a Prices row. The
  WPF OPERATOR and MARKET views still show none of these. (`OperatorVm`, `MarketVm`.)

## GDA data builds

- **Missing or ambiguous source columns and files.** A CSV column that does not exist reads as blank or 0 on every
  row except where a reader checks its columns first (the LTDS readers, UKPN and NPg demand do, since GR-289); a
  directory of Parquet parts is read in full, but parts whose file names carry different publication dates are refused
  (GR-287; undated parts are not checked, so keep one vintage per dataset folder); NPg falls back from Parquet to CSV
  on a Parquet read error other than that refusal (GR-313: the build notes name the dataset and the error).
  (`Csv`, `Tabular`, `LtdsReader`, `NpgSource`.)
- **Data conventions the builder applies.** A blank power factor reads as 1.0 (no reactive demand), as the GDA chapter
  documents; UKPN and NPg installed capacity in MVA is used as export capacity in MW; a generator whose own GSP group
  does not list its primary is placed by name in the one other group that does (counted in a note); a three-winding
  transformer carries no OLTC and its resistance goes wholly on the high-voltage leg; a second and later embedded
  generator of one fuel at one primary is named with a " #2", " #3" suffix so that no two names match (GR-796; the first
  keeps its plain name; a name is a label, no solver reads it); NPg node keys round kV to one decimal place, so 0.4 and 0.415 kV busbars of one name merge (kept: whether the
  tables spell one LV board both ways needs the real NPg data to decide); the UKPN demand split can differ from the
  published figure in the last bits. Decided (GR-333): the note on UKPN aggregate (grid board) rows now states, in MW,
  how much more they publish than the rows they feed add up to; that excess is not in the replica.
  (`GdaModelBuilder`, `UkpnSource`, `NpgSource`.)
- **Validation findings do not block a build by default.** Every `GridModel.Validate` finding of a GDA build becomes a
  note (set `GdaBuildOptions.StrictValidation` to make any Error finding throw, GR-311); a
  build with invalid data is returned as a result (the JSON loader that reads the written case refuses it). (Since
  GR-332 the case writer writes every bus and branch field a JSON v1 case carries, when set: ZIP, per-bus voltage
  limits, ratings B and C, a line's tap and phase shift and zero-sequence impedance; what v1 cannot hold, a
  transformer's shunt susceptance and transformer zero-sequence data, makes the write throw before any file is
  written.) (`GdaModelBuilder`,
  `GdaModelWriter`.)
- **Register choice by date.** Decided (GR-821, GR-827): a register dated D in its name is available from 00:00 on D (the
  name gives a day, not a time, and the as-of rule works in days); only full and short English month names (and `sept`)
  parse, because a numeric or ISO date in a GDA name is as often a data period as a publication date. Decided (GR-836):
  UKPN files are matched by the shortest suffix, and two equally good matches (for example two vintages of one register)
  are refused rather than one picked by date (a longer name loses to a shorter one without a date check): equal matches
  are refused; name one vintage. Name registers with a full or short month and keep one vintage. (`GdaPaths`,
  `UkpnSource`.)
- **NESO and phase-angle readers.** Any extra numeric column in the boundary table is read as a boundary, 0 MW weeks are
  dropped (so `MinMw` can be overstated) and the median of an even count is the upper middle (kept: what a 0 MW week and
  the extra columns mean needs the real NESO table to decide). The phase-angle reader labels a non-UTC typed stamp as
  UTC without converting it and truncates stamps to the whole second, so a sample can answer an instant just before it:
  both come from the shared text conversion of a Parquet time cell that the reference reader goes through. Decided
  (GR-809, GR-810), kept: the typed reader exists to match the reference reader bit for bit, so neither is changed
  unless that conversion changes for every GDA reader at once. Decided
  (GR-318, GR-794): the ISO week is looked for under the ISO-year folder and then the calendar-year folder, and a week
  held in both is refused; two rows with one time stamp (after the whole-second reading) are refused, naming both rows.
  (`NesoReader`, `PhaseAngleInferedReader`.)
- **Geolocation keys by name.** Two substations whose names share their first word take the first one's position, and a
  malformed substations file becomes a note. (`GeoLocator`.)
- **One substation name in two licence areas.** Since GR-316 the UKPN demand table is keyed by licence area and
  substation name: a name published in two licence areas keeps both rows' load (keyed `<name>@<area>`, each on its own
  area's node, with a note), and a demand row with a substation name but no licence area is refused, naming it. A demand
  table with no `LicenceArea` column at all is still read as one area. (`UkpnSource`.)

## Interposer, telemetry and synchrophasors

- **Window time checks.** The forward-inference horizon is checked on a window's instant (its newest stamp) only; a
  point stamped long before the others is merged into the snapshot unless the opt-in limit on the spread of time tags
  is set (GR-305: `InterposePipeline.MaxStampSpread`, `interpose --max-stamp-spread-ms`; off by default, by decision, so
  no recorded window moves), and a wildly future stamp moves the whole window past the horizon. (`InterposePipeline`.)
- **Protocol state and addressing.** (The 32-bit companion register's quality is folded in and a companion of another
  point type is refused since GR-815.) Decided (GR-306): IEC
  104 points are addressed by IOA alone by default, so every crosswalk keeps its point names; an IOA that arrives with a
  second common address on one link is a collision, and its points are marked invalid from then on, counted and the
  first one named on stderr. The opt-in crosswalk member `"iec104Key": "casdu-ioa"` names points
  `CA:<casdu>/IOA:<n>`, so stations sharing a link stay apart. Decided (GR-329, GR-801, GR-803, GR-823): a stateful decoder is reset when its
  client goes away; a Modbus response to an id that had two different requests outstanding is refused, and an exception
  response ends its transaction. Inherent to Modbus TCP (GR-801): a response is paired with its request by the MBAP
  transaction id alone, the protocol carrying no sequence number or request echo, so a master that reuses an id after its
  earlier request was answered or abandoned (no overlap) cannot be told apart from a late answer to the earlier one; the IEC 104 controlling station closes the connection when STARTDT act or TESTFR act is
  not confirmed within t1, or an I-frame arrives out of sequence. (`ModbusProtocol`, `RtuMeasurementSetMapper`,
  `Iec104Protocol`, `Iec104ControllingStation`.)
- **C37.118 decoding.** Decided (GR-325): STAT bit 10 (configuration change) does not decide usability (a test pins it
  as usable): by IEEE C37.118.2 it warns of a coming change and the data is valid under the configuration in force;
  pmu-ingest counts the frames that carry it and prints one stderr line asking for the CONFIG frame to be re-sent; (PDC
  alignment tolerates one tick since GR-806, so PMUs whose TIME_BASE rounds the same instant to adjacent ticks form one
  superframe;) a PMU current channel is metered at the branch's From end unless the channel map's optional `end` field
  says "to" (GR-808); the single-PMU decode overloads do not compare IDCODEs. Decided (GR-304, GR-818, GR-826): the
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
  results and resumes on the next frame, so a completed shard can hold outages screened against different frames.
  Decided (GR-376), kept for now: the design that would make the admitted set depend on the frame alone is a
  deterministic work budget (each job's cost counted in iterations or factorisations, not measured in milliseconds,
  with no wall-clock cancellation), at the price of a cycle that can overrun its time slot on a slow machine.
  This is kept on purpose (discarding them would leave a large shard never finishing under live load); since GR-339
  each N-1 result carries the tick of the frame it was screened on (`ContingencyResult.FrameTick`) and the web CYCLE
  tab shows each N-1 job's frame range. The desktop CYCLE tab does not show the range yet.
  (`CycleScheduler`, `AnalysisJobLibrary`.)
- **Live clock.** The day clock adds the step in minutes each tick (rounding builds up over a long session) and does
  not check the tick number, so a repeated or skipped tick is not detected. (`LiveSim`.)
- **Real-time estimator windows.** When the DSE engine continues on error, only domain refusals count as a failed
  window (GR-855): bad input data and the filters' numerical refusals, which carry the `DomainRefusal` mark. An unmarked
  `InvalidOperationException` (a programming fault) fails the run. The snapshot (WLS) real-time engine still counts any
  `InvalidOperationException` from the estimator as a failed window. (`RealtimeDseEngine`, `RealtimeEstimatorEngine`.)
- **Passive-plant replays** whose modelled plant moved a tap or derated a rating are tagged `Interpolated` (recorded
  inputs augmented by modelled behaviour) since GR-854; one whose plant changed nothing stays `DerivedFromRecorded`, and
  storage keeps `Counterfactual`. The tag records only that the plant changed the model, not by how much.
  (`GdaReplayLoop`.)
- **`HybridMultiRateScheduler`** has no production caller; it is kept for its tests and marked test-only in its doc
  comment (design decision, GR-848). It refuses an end before its start and mixed `DateTimeKind` values.

## Audit trail, incidents and crash logs

- **The writer lock.** The single-writer lock is advisory on Linux and macOS (`flock`): a process that does not take
  it can append, and unlinking a held `.lock` file lets a second run lock a new file and write with the same sequence
  numbers. Give the log one writer and a directory only it can write. (`AuditLogWriter`.)
- **Anchors.** A missing anchor is accepted with a warning and an `audit-anchor=missing` marker; deleting both the log
  and the anchor starts a fresh log with no marker. Since GR-374 the signed anchor (v2) also binds the log's first entry
  and the signing time, and `audit verify --pubkey` prints that time; restoring an older log prefix with its older
  signed anchor still verifies (the signature is genuine), so it is detected only by comparing the signing time with
  the operator's own record of the last signing. A v1 signature still verifies, with a warning, and is replaced by v2 at
  the next signing. The anchor and its signature
  are fsynced and, since GR-837, so is their folder after the rename on Linux and macOS (Windows has no directory flush;
  NTFS journals the rename); the temporary name is fixed. Forward the log to write-once storage where
  truncation matters. (`AuditRunRecorder`, `SignedAuditAnchor`, `AuditAnchorFile`.)
- **Recorded inputs.** `AuditInput.OfText` records an unsalted SHA-256 so a reviewer can recompute it; it is for
  non-secret identifiers only, by decision (GR-357), since a short secret would be guessable. An input file is hashed on
  one read and loaded on another; since GR-851 the audited verbs hash it again once it is loaded (the interpose
  crosswalk, the estimate case and measurement set, the crossval case) and refuse the run (exit 1) if it changed, so a
  replacement between the two reads is caught, unless the original bytes are put back before the second hash. `serve`
  (whose window folder is read throughout the run) and a solve export given to `crossval` (read by the sidecar) are not
  re-checked. (`AuditEvent`, `AuditRunRecorder`.)
- **Log size.** Opening a writer checks the whole chain, so start-up time grows with the log; since GR-859 the check is
  one streaming pass that holds a line at a time and keeps only the count and head hash, so memory no longer grows
  with the log and a log over 2 GiB opens (`audit verify` and `audit show` still hold the verified entries they list).
  The log is not rotated: rotate by starting a new log path, keeping the old log and its anchor. (`AuditLogFile`.)
- **Incident monitor bursts.** The CLI and web host wire the burst monitor with a one-hour sliding window on the
  entries' own timestamps (GR-363): a burst is the kind's threshold of events within the hour, and the latch re-arms
  once the events within the hour fall below it, so events of one kind spaced more widely than the threshold per hour
  never escalate; a monitor built directly without a window keeps cumulative counts and latches once. Kinds with
  no burst threshold (RestrictionRefused, DistrustedSource, DependencyAlert, AvailabilityDegraded) never escalate
  through the monitor; they are still written to the audit log as security entries. (`SecurityEventMonitor`.)
- **Horizon seams and the caller's clock.** Since GR-365 every horizon check clamps a supplied `utcNow` later than the
  real clock to the real clock (`ForwardInferenceGuard.EffectiveNow`), so a later "now" can never admit recent data; the
  command-line and live entry points still refuse such a "now" by name first. The one deliberate exception is the
  explicit `DateTime.MaxValue` deferral of the live RTU mapper, whose consuming engine re-checks against the real clock.
  (`ForwardInferenceGuard`.)
- **Crash logs.** The arguments are written in full, by decision: no GridSim flag carries a secret (licences are
  imported from a file, and `--licence` selects a distribution area), and the exact command line is what a bug report
  needs. When the per-user folder cannot be created the log goes to the temporary folder; on Linux and macOS it is
  created there new and owner-only (0600) in an owner-only folder (GR-839). (`CrashLog`.)

## Determinism checks and the web session

- **Seeded random numbers come from .NET.** EnKF ensembles, synthetic measurements, the synthetic GB case, LiveSim's
  ambient noise and graph layout use `new Random(seed)`. Today a seeded `Random` uses .NET's long-standing compatible
  algorithm, but .NET does not promise to keep it, so a future runtime could change these sequences; the pinned tests
  would show it. (`EnsembleKalmanStateEstimator`, `LiveSim`.)
- **What the determinism guard scans.** The IL guard scans Core, Gda, Gpu and the command line. Since GR-861 a second
  guard (`PresentationMathGuardTests`) scans the presentation assemblies GridSim.Ui and GridSim.Web with the same
  forbidden list, allowing the host's trigonometry only in five display-geometry types that draw and compute nothing
  people read (`PhasorGeometry`, `GaugeGeometry`, `SchematicView`, and the web's `Gauge` and `Phasor` components); a
  stale allowlist entry fails too. The Avalonia and WPF app assemblies themselves are not scanned (the test project does
  not reference them; their view models live in GridSim.Ui). Its vector list names the reciprocal-estimate intrinsics
  and `Lerp` (GR-861). Since
  1.95.436 a second test requires every `AddScaledRow` call in `src` to name `fastFma: false` literally.
  (`DeterministicMathGuardTests`.)
- **Cross-OS checks in CI.** Every leg of the CI matrix (Linux, Windows and macOS; normal, AVX2 off, hardware
  intrinsics off, AVX-512 and FMA off) is a hard gate. The workflow runs when the release calls it, by hand, and on
  every push to main and weekly on the public repository only (GR-382; its jobs are guarded to that repository), so
  commits on the private development repository are not checked by CI until they reach the public one; day-to-day
  verification is the local batch run. (`.github/workflows/ci.yml`.)
- **Web session state.** Since 1.95.436 the bridge callbacks write the per-tick fields under the session lock; the
  topology and error text are single reference writes, read without it. A request for a replay's topology waits for
  another caller's cold build of the same run on an asynchronous lock (no thread is held), and the cache keeps a run's
  file fingerprint for 5 seconds, so a warm call does not list and stat the exports again; a run re-exported in place
  is therefore seen within 5 seconds, not at once. A cold build of a large replay still takes as long as it takes and
  holds a solve slot. (`SimSession`, `ReplayTopologyCache`.)

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
  flow field (losses, rating and name included) and the solved bus types, and by `Gr889DeviceFieldParityTests`
  (GR-889), which compares the coupled-device results (LCC, VSC and MTDC links, FACTS, SVCs, induction machines), their
  notes, the sweep step and the slack reactive note on the same cases.
- **Identities beside the physics.** `TotalLossMw` is the sum of the injections, so the balance assertions in
  `SolverPropertyTests` (and `PowerFlowTests.Generation_balances_load_plus_losses`, `IeeeCasesTests.Converges_and_balances`)
  check that definition; the two `SolverPropertyTests` theories are named for it, and the independent
  check is `Solved_injections_recomputed_from_the_ybus_meet_the_specified_injections`, which recomputes every bus
  injection from the solved voltages and a Y-bus built in the test and holds it to the case's specified P and Q at
  1e-4 MW. The branch-plus-shunt reconciliation in `SolverPropertyTests` is at 1e-3 of the loss in MW; since GR-407
  `Gr407BranchLossReconciliationTests` holds the same reconciliation at 1e-9 of the gross branch throughput on every
  built-in case, stress cases included.
- **Outcomes the suite accepts either way.** Several tests on fixed inputs accept two opposite outcomes (a case9 bus-5
  fault cleared or not cleared by a branch element; `modal case9` exiting 0 or 1; about ten CLI tests accepting exit 0
  or 3); the dense/sparse theories over MATPOWER files and JSON cases do not require convergence, so for the rows that
  do not solve as shipped they compare two failures; the one gb-full security DC-OPF test is skipped unconditionally and
  accepts Optimal or not.
- **Passes that are not runs.** The bundled-data guards are assertions, and the CGMES conformity
  benchmarks (no workflow sets `GRIDSIM_CGMES_CONFORMITY_DIR`) and the Windows-only header check are reported as
  skipped through the `EnvironmentTheory` and `WindowsFact` attributes; `BundledDataPresenceTests` also finds the
  braced, `OperatingSystem` and `string.IsNullOrWhiteSpace` forms of an early return. Since GR-404 was completed the
  optional template library check and the crash-log default-folder check are skipped by attribute too
  (`RepositoryDataFact`, `EnvironmentUnsetFact`), so no test returns early on missing data. The scan also finds an
  early return on `x is null` or `x == null` when `x` was assigned from an environment variable in the same method
  (a general `is null` pattern would flag the many ordinary null checks in test helpers). Left: a guard written in a
  form the scan does not recognise is not found, and on a machine with no GPU the GPU seam tests compare the CPU fallback with the CPU.
- **Source-text contract tests.** About 133 test files assert that strings are present in source files (for example
  the exit-code convention in `Program.cs`), which a comment or dead code satisfies and a correct rename breaks. GR-403:
  the exit-code convention tests now have behavioural twins that run the built CLI on a case that cannot converge
  (`Gr403CliExitCodeBehaviourTests`); the source-text tests are kept beside them, and the other files are not yet
  paired.
- **CI coverage.** Every reduced leg checks that its switch took effect (`simdcheck` must report
  `Avx2=False`, the `Scalar` tier, `Fma=False` or, on the `avx256` leg added for GR-399, the `Avx256` tier, so the
  256-bit production tier runs whatever the runner CPU). Every leg and the release's own test run
  set `GRIDSIM_TEST_MAX_BUSES=all`, so a runner with less than the 16 GB the whole suite needs can run out of memory on
  the largest cases rather than skip them.
- **Environment and clock.** Since GR-883 CLI child processes do not see the machine's licence: `CliProcess` removes
  `GRIDSIM_LICENCE` and points `GRIDSIM_LICENCE_FILE` at a file in the run's temporary folder, unless a test opts in
  (`RunBuiltWithMachineLicence`); in-process tests that resolve a licence (the web host) still read the machine's. Licence
  expiry reads the system clock, so a run crossing UTC midnight can flip an expiry-day check. The graph-layout
  wall-clock test runs alone in a non-parallel collection and is kept as a deliberate performance smoke test (owner
  decision on GR-881): `GraphLayout` takes no clock by design, so a fake clock cannot time it, and a slow or heavily
  loaded runner can still fail its 3 s bound. (`CliProcess`, `Entitlement`, `GraphLayoutTests`.)
- **Added in 1.95.437.** Tests of the audit sink forwarding path (`AuditSinkTests`), a timed test that `GeneralEigen`
  refuses an infinite entry by name, the phase-angle reader tests in a non-parallel collection, and exclusive callers
  of the large dense solve gate queued on their own lock.

## Build, release and deployment

- **Pinning.** Every GitHub Action in every workflow is pinned to a full commit SHA with its release tag in a
  comment, the release installs the CycloneDX SBOM tool at a named version, `global.json` pins the SDK (10.0.401, no
  roll-forward), CI and the image build restore in locked mode, and the Docker base images are pinned by tag and
  digest (`sdk:10.0.401@sha256:...`, `aspnet:10.0.12@sha256:...`) with no package installed in the runtime stage (the
  health check uses bash's `/dev/tcp`, GR-423). Dependabot version updates are paused. (`.github/workflows`, `Dockerfile`,
  `Directory.Build.props`.)
- **Release gates.** The release runs the full CI matrix and its own Linux test run before anything ships; the package
  workflow, started by hand or by a published release, runs the whole CI workflow first, though not
  the release's own Linux test run. The release passes it the two signing secrets by name (GR-909). Without the
  certificate a release is unsigned and says so; setting the repository variable `GRIDSIM_ALLOW_UNSIGNED_RELEASE` to
  `false` makes signing required, checked in the first job before anything is built (GR-359; unsigned stays the default
  while the certificate is self-signed). `signtool` signs from the runner's certificate store (GR-907);
  the package workflow, on a Linux runner with no such store, decrypts the PFX with the password read from the environment
  into a private temporary folder, re-exports it without a password and signs with that file, so no signing password is
  on any command line; the folder is removed when the step ends, and a test pins that no workflow passes
  `--certificate-password`.
- **What the SBOMs describe.** Each project SBOM lists the NuGet packages of its project. Since GR-439 the release
  also makes an SBOM of the built web image with Syft (the Ubuntu base's packages and the ASP.NET runtime, checked for
  both), and adds the bundled .NET runtime (and, for the WPF app, the Windows Desktop runtime) to the self-contained
  CLI and desktop SBOMs as framework components, by name and version, before they are hashed; the runtime's own
  packages are not itemised. Every self-contained publish of the CLI and the two desktop apps copies the .NET runtime's own
  `THIRD-PARTY-NOTICES.TXT` beside the binary as `dotnet-THIRD-PARTY-NOTICES.txt` and fails if it cannot find it
  (GR-892); the container image's runtime notices are still referenced by URL.
- **Line endings.** `.gitattributes` pins LF for the Dockerfile and `.dockerignore` only, so on a Windows checkout with
  `core.autocrlf=true` scripts, compose files and data are written with CR LF. The bundled-data manifest hashes text
  with CR LF read as LF, so it verifies on either checkout.
- **Local publishes.** The CLI and test projects copy `data/**/*.json` and `data/**/*.csv`. A CLI publish now stops with
  an error naming any untracked or git-ignored data file (`-p:GridSimAllowStrayData=true` overrides, and it is skipped
  when git or a repository is absent); a build, and the other projects, are not guarded. Publish from a clean checkout.
- **Internals.** GridSim.Core grants `InternalsVisibleTo` to `GridSim.Tests`, `GridSim.SchemaEditor` and `GridSim.Ui` by
  simple name and is not strong-named, so an assembly of any of those names can call Core's internals, licensing
  included. All three friends use Core internals (the case-editing engine, and the tests), so none can be dropped
  without strong-naming or widening the public API; Gpu, Web, SchemaEditor and Ui each grant `GridSim.Tests` only, and a
  test pins the exact list of grants.
- **Shipped data files.** `data/bundled.sha256` covers the bundled networks only (the licence exemption). A test
  (`Every_tracked_shipped_data_file_is_accounted_for`) requires every git-tracked data file of a shipped type to be in
  that manifest or under a reviewed list of non-network folders (events, neso, qsts, devices, library), so a new shipped
  file cannot appear unnoticed.
- **Forwarded headers.** `compose.yml` defaults `GRIDSIM_TRUSTED_PROXIES` to `loopback,private`, which trusts every
  RFC 1918 address, other containers on a shared network included. Set it to the reverse proxy's address. The default
  is kept (owner decision on GR-897): the published host port is bound to 127.0.0.1, so only the host itself and its
  private network reach the container, and an empty default would make every caller behind a reverse proxy appear as
  the proxy's address, so the per-client session quota (`GridSim:MaxSessionsPerClient`, GR-235) would put them all in
  one quota.
